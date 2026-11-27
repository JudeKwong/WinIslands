using System.IO;
using WinIslands.Services;

namespace WinIslands.Mac.Services;

/// <summary>
/// macOS 媒体状态机：轮询 Cider（HTTP API）→ AppleScript（Music.app / Spotify），
/// 解析封面到本地缓存，并通过 <see cref="SynchronizationContext"/> 把统一快照发布到 UI 线程。
/// （Windows 版的 SMTC / 窗口标题回退在 macOS 不存在：SMTC 无对应 API，窗口标题启发式对 Mac 无意义。）
/// </summary>
public sealed class MacMediaCoordinator : IDisposable
{
    private readonly SettingsService _settings;
    private readonly CiderMediaProvider _cider;
    private readonly AppleScriptMediaProvider _apple = new();
    private readonly SynchronizationContext? _sync;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private System.Timers.Timer? _timer;
    private int _tick;
    private int _started;
    private int _disposed;
    private MediaSnapshot? _current;

    public MacMediaCoordinator(SettingsService settings, CiderMediaProvider cider, SynchronizationContext? syncContext = null)
    {
        _settings = settings;
        _cider = cider;
        _sync = syncContext;
    }

    public MediaSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>快照变化（已切回 UI 线程）。</summary>
    public event EventHandler<MediaSnapshot>? SnapshotChanged;
    /// <summary>媒体结束/退出（已切回 UI 线程）。</summary>
    public event EventHandler? MediaEnded;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _timer = new System.Timers.Timer(500) { AutoReset = false };
        _timer.Elapsed += (_, _) => _ = TickAsync();
        _timer.Start();
    }

    private async Task TickAsync()
    {
        if (!await _tickLock.WaitAsync(0).ConfigureAwait(false))
        {
            ScheduleNextTick();
            return;
        }
        try
        {
            _tick++;

            // 1) Cider API（可关闭；未连接时很快返回 null）
            await _cider.EnsureConnectedAsync().ConfigureAwait(false);
            var snapshot = await _cider.GetSnapshotAsync().ConfigureAwait(false);

            // 2) AppleScript：Music.app / Spotify
            if (snapshot is null)
            {
                snapshot = await Task.Run(() => _apple.GetSnapshot(AppPaths.ThumbCacheDir)).ConfigureAwait(false);
            }

            if (snapshot is null)
            {
                if (Current is not null)
                {
                    _current = null;
                    Raise(() => MediaEnded?.Invoke(this, EventArgs.Empty));
                }
                return;
            }

            if (snapshot.Status is PlaybackStatus.Closed or PlaybackStatus.Stopped)
            {
                if (Current is not null && Current.Source == snapshot.Source)
                {
                    _current = null;
                    Raise(() => MediaEnded?.Invoke(this, EventArgs.Empty));
                }
                return;
            }

            snapshot = await ResolveArtworkAsync(snapshot).ConfigureAwait(false);
            snapshot = await AttachVolumeAsync(snapshot).ConfigureAwait(false);

            var previous = Current;
            if (previous is not null && IsRedundant(previous, snapshot)) return;
            _current = snapshot;
            Raise(() => SnapshotChanged?.Invoke(this, snapshot));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Mac media tick failed", ex);
        }
        finally
        {
            _tickLock.Release();
            ScheduleNextTick();
        }
    }

    private void ScheduleNextTick()
    {
        if (_cts.IsCancellationRequested) return;
        // AutoReset=false：每次手动重启；Interval 1s（有媒体）/ 2.5s（空闲），空闲降频省电
        try
        {
            if (_timer is null) return;
            _timer.Interval = Current is not null ? 1000 : 2500;
            _timer.Start();
        }
        catch (ObjectDisposedException) { }
    }

    internal static bool IsRedundant(MediaSnapshot previous, MediaSnapshot next)
    {
        const double positionTolerance = 0.25;
        const double durationTolerance = 0.5;
        return previous.Source == next.Source
            && previous.Status == next.Status
            && previous.Track == next.Track
            && Math.Abs(previous.DurationSeconds - next.DurationSeconds) < durationTolerance
            && Math.Abs(previous.PositionSeconds - next.PositionSeconds) < positionTolerance;
    }

    private async Task<MediaSnapshot> ResolveArtworkAsync(MediaSnapshot snapshot)
    {
        if (string.IsNullOrEmpty(snapshot.Track.ArtworkUrl) || !string.IsNullOrEmpty(snapshot.Track.ArtworkPath))
            return snapshot;

        var key = ArtworkCache.CacheKey("art", snapshot.Track.SourceAppId, snapshot.Track.Title, snapshot.Track.Artist, snapshot.Track.Album);
        var existing = Directory.EnumerateFiles(AppPaths.ThumbCacheDir, $"{key}.*").FirstOrDefault();
        var path = existing ?? await ArtworkCache.DownloadAsync(snapshot.Track.ArtworkUrl, key, _cts.Token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path)) return snapshot;

        return snapshot with
        {
            Track = snapshot.Track with { ArtworkPath = path, ArtworkUrl = string.Empty },
        };
    }

    private async Task<MediaSnapshot> AttachVolumeAsync(MediaSnapshot snapshot)
    {
        if (snapshot.Source == MediaSourceKind.Cider)
        {
            var v = await _cider.GetVolumeAsync().ConfigureAwait(false);
            return snapshot with { Volume = v, HasVolumeControl = v is not null };
        }
        // AppleScript 来源已在查询脚本里带上音量
        return snapshot;
    }

    private void Raise(Action action)
    {
        if (_sync is null || ReferenceEquals(_sync, SynchronizationContext.Current)) action();
        else _sync.Post(_ => action(), null);
    }

    // ── 控制（按当前来源路由；AppleScript 路径返回同步结果包装为 Task）──────

    public async Task<bool> TogglePlayPauseAsync()
    {
        if (Current?.Source == MediaSourceKind.Cider) return await _cider.TogglePlayPauseAsync();
        var app = AppleAppName();
        return app is not null && await _apple.TogglePlayPauseAsync(app);
    }

    public async Task<bool> PlayAsync()
    {
        if (Current?.Source == MediaSourceKind.Cider) return await _cider.PlayAsync();
        var app = AppleAppName();
        return app is not null && await _apple.PlayAsync(app);
    }

    public async Task<bool> PauseAsync()
    {
        if (Current?.Source == MediaSourceKind.Cider) return await _cider.PauseAsync();
        var app = AppleAppName();
        return app is not null && await _apple.PauseAsync(app);
    }

    public async Task<bool> NextAsync()
    {
        if (Current?.Source == MediaSourceKind.Cider) return await _cider.NextAsync();
        var app = AppleAppName();
        return app is not null && await _apple.NextAsync(app);
    }

    public async Task<bool> PreviousAsync()
    {
        if (Current?.Source == MediaSourceKind.Cider) return await _cider.PreviousAsync();
        var app = AppleAppName();
        return app is not null && await _apple.PreviousAsync(app);
    }

    public async Task<bool> SeekAsync(double seconds)
    {
        if (Current?.Source == MediaSourceKind.Cider) return await _cider.SeekAsync(seconds);
        var app = AppleAppName();
        return app is not null && await _apple.SeekAsync(app, seconds);
    }

    public async Task<bool> SetVolumeAsync(double volume01)
    {
        if (Current?.Source == MediaSourceKind.Cider) return await _cider.SetVolumeAsync(volume01);
        var app = AppleAppName();
        return app is not null && await _apple.SetVolumeAsync(app, volume01);
    }

    /// <summary>当前快照来自 Music.app / Spotify 时返回其应用名，否则 null。</summary>
    private string? AppleAppName()
    {
        var id = Current?.Track.SourceAppId;
        if (string.Equals(id, "com.apple.Music", StringComparison.OrdinalIgnoreCase)) return "Music";
        if (string.Equals(id, "com.spotify.client", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        return null;
    }

    public Task RefreshNowAsync() => TickAsync();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _timer?.Stop();
        _timer?.Dispose();
        _cider.Dispose();
    }
}
