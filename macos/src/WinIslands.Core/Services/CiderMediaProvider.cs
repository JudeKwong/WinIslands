namespace WinIslands.Services;

/// <summary>
/// Session-level wrapper around <see cref="CiderClient"/>: owns connection lifecycle
/// (reconnect, backoff) and surfaces snapshots. When Cider is not reachable it simply
/// returns null so the coordinator falls back to SMTC / window-title.
/// </summary>
public sealed class CiderMediaProvider : IDisposable
{
    private readonly SettingsService _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private DateTime _lastConnectAttempt;
    private DateTime _nextProbeUtc;
    private DateTime _processCheckUtc;
    private bool _ciderProcessRunning;
    private int _consecutiveFailures;
    private bool _reconnecting;

    public CiderClient Client { get; } = new();

    public CiderMediaProvider(SettingsService settings) => _settings = settings;

    public bool IsEnabled => _settings.Current.CiderEnabled;

    /// <summary>媒体程序列表里若禁用 Cider（Cider.exe），则不检测。</summary>
    private bool IsEnabledByMediaApps()
    {
        var list = _settings.Current.MediaApps;
        if (list is null || list.Count == 0) return true;
        foreach (var e in list)
            if (string.Equals(e.Key, "Cider.exe", StringComparison.OrdinalIgnoreCase)) return e.Enabled;
        return true;
    }

    /// <summary>Try to connect (once per few seconds at most).</summary>
    public async Task EnsureConnectedAsync()
    {
        if (!IsEnabled || Client.IsConnected) return;
        if (_reconnecting) return;
        var now = DateTime.UtcNow;
        if (now < _nextProbeUtc) return;
        if (!IsCiderProcessRunningCached(now) && _settings.Current.CiderPort <= 0)
        {
            ScheduleNextProbe(now, failed: true, noProcess: true);
            return;
        }

        await _connectLock.WaitAsync();
        try
        {
            if (Client.IsConnected) return;
            _reconnecting = true;
            _lastConnectAttempt = DateTime.UtcNow;
            var s = _settings.Current;
            // 用户未手动填写 Token 时，自动从 Cider 配置读取（零配置）
            var token = !string.IsNullOrWhiteSpace(s.CiderToken)
                ? s.CiderToken
                : CiderTokenAutoDetect.TryGetToken() ?? string.Empty;
            Client.SetToken(token);
            var connected = await Client.ConnectAsync(s.CiderPort, _cts.Token);
            ScheduleNextProbe(DateTime.UtcNow, failed: !connected, noProcess: false);
        }
        finally
        {
            _reconnecting = false;
            _connectLock.Release();
        }
    }


    /// <summary>缓存 Cider 进程检查，避免后台每秒枚举进程。</summary>
    private bool IsCiderProcessRunningCached(DateTime now)
    {
        if (now - _processCheckUtc < TimeSpan.FromSeconds(30)) return _ciderProcessRunning;
        _processCheckUtc = now;
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Cider");
            _ciderProcessRunning = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
        }
        catch
        {
            _ciderProcessRunning = true; // 无法判断时保持兼容探测
        }
        return _ciderProcessRunning;
    }

    /// <summary>探测失败后指数退避，减少 Cider 不存在时的端口扫描和异常。</summary>
    private void ScheduleNextProbe(DateTime now, bool failed, bool noProcess)
    {
        if (!failed)
        {
            _consecutiveFailures = 0;
            _nextProbeUtc = now.AddSeconds(5);
            return;
        }

        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 6);
        var seconds = noProcess
            ? 30
            : Math.Min(60, 5 * (1 << Math.Min(_consecutiveFailures, 4)));
        _nextProbeUtc = now.AddSeconds(seconds);
    }
    public async Task<MediaSnapshot?> GetSnapshotAsync()
    {
        if (!IsEnabled || !IsEnabledByMediaApps() || !Client.IsConnected) return null;

        var snap = await Client.GetSnapshotAsync(_cts.Token);
        if (snap is null)
        {
            // Connection may have dropped; force a re-probe on next tick.
            Client.MarkDisconnected();
            await EnsureConnectedAsync();
            return null;
        }

        return snap;
    }

    public Task<bool> TogglePlayPauseAsync() => Client.TogglePlayPauseAsync(_cts.Token);
    public Task<bool> PlayAsync() => Client.PlayAsync(_cts.Token);
    public Task<bool> PauseAsync() => Client.PauseAsync(_cts.Token);
    public Task<bool> NextAsync() => Client.NextAsync(_cts.Token);
    public Task<bool> PreviousAsync() => Client.PreviousAsync(_cts.Token);
    public Task<bool> SeekAsync(double seconds) => Client.SeekAsync(seconds, _cts.Token);
    public Task<double?> GetVolumeAsync() => Client.GetVolumeAsync(_cts.Token);
    public Task<bool> SetVolumeAsync(double v) => Client.SetVolumeAsync(v, _cts.Token);
    public Task<string?> GetLyricsAsync() => Client.GetLyricsAsync(null, _cts.Token);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
