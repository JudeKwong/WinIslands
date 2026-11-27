using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WinIslands.Mac.Services;
using WinIslands.Services;

namespace WinIslands.Mac.UI;

/// <summary>灵动岛显示模式：推送卡片 > 媒体 > 空闲时钟。</summary>
public enum IslandMode { Idle, Media, Push }

/// <summary>
/// 灵动岛 ViewModel（macOS 版）：把媒体快照/歌词/上岛推送映射为可绑定状态，
/// 以 5Hz 插值播放位置（进度条与卡拉OK平滑推进），管理展开/收起与空闲时钟。
/// </summary>
public sealed class IslandViewModel : ObservableObject, IDisposable
{
    private readonly MacMediaCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly LyricsService _lyricsService;
    private readonly IslandApiServer? _api;
    private readonly SynchronizationContext? _sync;
    private readonly System.Timers.Timer _progressTimer; // 200ms：进度/歌词推进
    private readonly System.Timers.Timer _widgetTimer;   // 1s：时钟、推送过期、状态落盘
    private readonly System.Timers.Timer _seekDebounce;  // 220ms 单次：进度条拖动落点 seek
    private double _pendingSeekSeconds = -1;
    private readonly Stopwatch _interpClock = Stopwatch.StartNew();
    private int _tick;
    private int _disposed;

    // ── 媒体状态 ─────────────────────────────────────────────
    private MediaSnapshot? _snapshot;
    private double _interpolatedPosition;
    private Bitmap? _artwork;
    private string _artworkPath = "";
    private string _trackTitle = "";
    private string _trackArtist = "";
    private bool _isPlaying;
    private bool _hasMedia;
    private string _positionText = "0:00";
    private string _durationText = "0:00";
    private double _progressValue;
    private bool _suppressSeek;

    // ── 歌词状态 ─────────────────────────────────────────────
    private LyricsResult _lyrics = LyricsResult.Empty;
    private string _lyricsKey = "";
    private int _lyricIndex = -1;
    private string _lyricText = "";
    private string _nextLyricText = "";
    private IReadOnlyList<TtmlWord>? _lyricWords;
    private double _karaokePosition;
    private readonly Dictionary<double, TtmlLine> _ttmlLineIndex = new();
    private double _lyricOffset;

    // ── 推送状态 ─────────────────────────────────────────────
    private readonly List<IslandPush> _pushes = new();
    private IslandPush? _activePush;
    private string _pushInputValue = "";

    // ── 空闲/时钟 ────────────────────────────────────────────
    private string _clockText = DateTime.Now.ToString("HH:mm");
    private string _dateText = "";

    // ── 播放状态恢复 ─────────────────────────────────────────
    private string? _restoredTrackKey;
    private double _restoredPosition;
    private bool _restoreApplied;
    private string _currentTrackKey = "";

    private bool _isExpanded;
    private bool _islandVisible = true;
    private string _lastPreferredSource = "Auto";
    private bool _lastAmll = true;
    private bool _lastOnline = true;

    public IslandViewModel(MacMediaCoordinator coordinator, SettingsService settings,
        LyricsService lyricsService, IslandApiServer? api = null, SynchronizationContext? syncContext = null)
    {
        _coordinator = coordinator;
        _settings = settings;
        _lyricsService = lyricsService;
        _api = api;
        _sync = syncContext;

        PlayPauseCommand = new AsyncRelayCommand(_ => _coordinator.TogglePlayPauseAsync());
        NextCommand = new AsyncRelayCommand(_ => _coordinator.NextAsync());
        PreviousCommand = new AsyncRelayCommand(_ => _coordinator.PreviousAsync());
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(this, EventArgs.Empty));
        DismissPushCommand = new RelayCommand(() =>
        {
            if (_activePush is not null) DismissPush(_activePush.Id);
        });
        PushButtonCommand = new RelayCommand(p =>
        {
            if (p is IslandPushButtonView v && v.Source is { } src && _activePush is not null)
                HandlePushAction(_activePush, src.Action, src.Value, src.Label);
        });
        PushClickCommand = new RelayCommand(() =>
        {
            if (_activePush?.Click is { } click) HandlePushAction(_activePush, click.Action, click.Value, "click");
        });
        SubmitPushInputCommand = new RelayCommand(() =>
        {
            if (_activePush is null) return;
            var input = _activePush.Input;
            var value = PushInputValue ?? "";
            PushInputValue = "";
            HandlePushAction(_activePush, input?.Action ?? "notify", value, "input");
        });

        _coordinator.SnapshotChanged += OnSnapshotChanged;
        _coordinator.MediaEnded += OnMediaEnded;
        if (_api is not null)
        {
            _api.PushReceived += OnPushReceived;
            _api.PushRemoved += OnPushRemoved;
        }
        _settings.Changed += OnSettingsChanged;
        _lastPreferredSource = settings.Current.LyricsPreferredSource;
        _lastAmll = settings.Current.AmllTtmlEnabled;
        _lastOnline = settings.Current.OnlineLyricsEnabled;

        // 恢复上次退出时的播放位置（同一曲目再次播放时跳回）
        var restored = PlaybackStateStore.Load();
        if (restored is not null)
        {
            _restoredTrackKey = restored.TrackKey;
            _restoredPosition = restored.PositionSeconds;
        }

        UpdateDateText();
        RefreshIdleWidgets();

        _progressTimer = new System.Timers.Timer(200) { AutoReset = true };
        _progressTimer.Elapsed += (_, _) => Raise(ProgressTick);
        _progressTimer.Start();

        _widgetTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _widgetTimer.Elapsed += (_, _) => Raise(WidgetTick);
        _widgetTimer.Start();

        // 拖动进度条防抖：Mac 每次 seek 都要新起 osascript 进程，按最后落点单次执行
        _seekDebounce = new System.Timers.Timer(220) { AutoReset = false };
        _seekDebounce.Elapsed += (_, _) =>
        {
            var t = _pendingSeekSeconds;
            _pendingSeekSeconds = -1;
            if (t >= 0) _ = _coordinator.SeekAsync(t);
        };

        if (_coordinator.Current is { } cur) ApplySnapshot(cur, force: true);
    }

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? QuitRequested;

    // ── 命令 ─────────────────────────────────────────────────

    public AsyncRelayCommand PlayPauseCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public RelayCommand ToggleExpandCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand QuitCommand { get; }
    public RelayCommand DismissPushCommand { get; }
    public RelayCommand PushButtonCommand { get; }
    public RelayCommand PushClickCommand { get; }
    public RelayCommand SubmitPushInputCommand { get; }

    // ── 绑定属性 ─────────────────────────────────────────────

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) OnPropertyChanged(nameof(ExpandToggleGlyph)); }
    }

    public string ExpandToggleGlyph => IsExpanded ? "\u2303" : "\u2304";

    /// <summary>岛是否应显示（无媒体且无推送时按设置隐藏）。</summary>
    public bool IslandVisible
    {
        get => _islandVisible;
        private set => Set(ref _islandVisible, value);
    }

    public IslandMode Mode =>
        _activePush is not null ? IslandMode.Push : (_hasMedia ? IslandMode.Media : IslandMode.Idle);

    public bool HasMedia => _hasMedia;
    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _hasMedia && !_isPlaying;
    public string TrackTitle => _trackTitle;
    public string TrackArtist => _trackArtist;
    public Bitmap? Artwork => _artwork;
    public string ClockText => _clockText;
    public string DateText => _dateText;

    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }
    public string DurationText { get => _durationText; private set => Set(ref _durationText, value); }

    /// <summary>进度 0..1（进度条绑定；用户拖动后立即 seek）。</summary>
    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (!Set(ref _progressValue, value)) return;
            if (_suppressSeek) return;
            var dur = _snapshot?.DurationSeconds ?? 0;
            if (dur <= 0) return;
            _pendingSeekSeconds = Math.Clamp(value, 0, 1) * dur;
            _seekDebounce.Stop();
            _seekDebounce.Start();
        }
    }

    public string LyricText { get => _lyricText; private set => Set(ref _lyricText, value); }
    public string NextLyricText { get => _nextLyricText; private set => Set(ref _nextLyricText, value); }
    public bool HasLyrics => _lyricText.Length > 0;
    public IReadOnlyList<TtmlWord>? LyricWords { get => _lyricWords; private set => Set(ref _lyricWords, value); }
    public double KaraokePosition { get => _karaokePosition; private set => Set(ref _karaokePosition, value); }
    /// <summary>无逐字时间轴时的整行均分高亮比例 0..1。</summary>
    public double LineHighlightFraction { get => _lineHighlightFraction; private set => Set(ref _lineHighlightFraction, value); }
    private double _lineHighlightFraction;
    private double _lineStartSec;
    private double _lineEndSec;
    /// <summary>歌词来源说明（调试/设置页显示）。</summary>
    public string LyricsSourceLabel => _lyrics.Source switch
    {
        LyricsSourceKind.LocalFile => "本地歌词",
        LyricsSourceKind.AmllTtml => "AMLL TTML",
        LyricsSourceKind.Cider => "Cider",
        LyricsSourceKind.Online => "在线歌词",
        _ => "",
    };

    // 推送卡片
    public bool HasPush => _activePush is not null;
    public string PushTitle => _activePush?.Title ?? "";
    public string PushSubtitle => _activePush?.Subtitle ?? "";
    public bool PushSubtitleVisible => PushSubtitle.Length > 0;
    public string PushBody => _activePush?.Body ?? "";
    public bool PushBodyVisible => PushBody.Length > 0;
    public string PushIcon => NormalizeIcon(_activePush?.Icon ?? "");
    public bool PushProgressVisible => _activePush?.EffectiveProgress is not null;
    public double PushProgress => _activePush?.EffectiveProgress ?? 0;
    public string PushAccent => _activePush?.Accent ?? "";
    public IBrush PushAccentBrush => ResolveAccent(_activePush);
    public bool PushImageVisible => !string.IsNullOrEmpty(_activePush?.Image);
    public Bitmap? PushImage => _pushImage;
    private Bitmap? _pushImage;
    public ObservableCollection<IslandPushButtonView> PushButtons { get; } = new();
    public bool PushButtonsVisible => PushButtons.Count > 0;
    public bool PushInputVisible => _activePush?.Input is not null;
    public string PushInputPlaceholder => _activePush?.Input?.Placeholder ?? "输入…";
    public string PushSubmitLabel
    {
        get
        {
            var label = _activePush?.Input?.SubmitLabel ?? "";
            return string.IsNullOrWhiteSpace(label) ? "提交" : label;
        }
    }
    public string PushInputValue
    {
        get => _pushInputValue;
        set => Set(ref _pushInputValue, value);
    }

    /// <summary>徽标色（类型默认色，可被 accent 覆盖）。</summary>
    public IBrush TypedAccentBrush => ResolveAccent(_activePush, forText: true);

    // ── 媒体流 ───────────────────────────────────────────────

    /// <summary>滚轮音量微调（仅当前来源支持音量时生效）。</summary>
    public async Task AdjustVolumeAsync(double delta)
    {
        if (_snapshot is null || !_snapshot.HasVolumeControl) return;
        var target = Math.Clamp((_snapshot.Volume ?? 0) + delta, 0, 1);
        await _coordinator.SetVolumeAsync(target);
    }

    private void OnSnapshotChanged(object? sender, MediaSnapshot snapshot) => Raise(() => ApplySnapshot(snapshot, force: false));

    private void OnMediaEnded(object? sender, EventArgs e) => Raise(() =>
    {
        _snapshot = null;
        Set(ref _hasMedia, false, nameof(HasMedia));
        Set(ref _isPlaying, false, nameof(IsPlaying));
        _interpolatedPosition = 0;
        SetArtwork("");
        Set(ref _trackTitle, "", nameof(TrackTitle));
        Set(ref _trackArtist, "", nameof(TrackArtist));
        ClearLyrics();
        RaiseMode();
        RefreshIdleWidgets();
    });

    private void ApplySnapshot(MediaSnapshot snapshot, bool force)
    {
        var trackChanged = _currentTrackKey != TrackKeyOf(snapshot.Track) || _snapshot?.Track != snapshot.Track;
        _snapshot = snapshot;
        Set(ref _hasMedia, true, nameof(HasMedia));
        Set(ref _isPlaying, snapshot.Status == PlaybackStatus.Playing, nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));

        // 每次快照都重置插值基准：两次快照之间由 200ms 定时器按墙钟连续推进
        _interpolatedPosition = snapshot.PositionSeconds;
        _interpClock.Restart();

        Set(ref _trackTitle, snapshot.Track.Title, nameof(TrackTitle));
        Set(ref _trackArtist, snapshot.Track.Artist, nameof(TrackArtist));

        // 封面（同路径不重复解码）
        SetArtwork(snapshot.Track.HasArtwork ? snapshot.Track.ArtworkPath : "");

        if (trackChanged || force)
        {
            _currentTrackKey = TrackKeyOf(snapshot.Track);
            _restoreApplied = false;
            _lyricIndex = -1;
            _ = LoadLyricsAsync(snapshot);
            TryRestorePlayback(snapshot);
        }

        UpdateProgressText();
        UpdateLyricLine(_interpolatedPosition);
        RaiseMode();
        RefreshIdleWidgets();
    }

    private void SetArtwork(string path)
    {
        if (path == _artworkPath) return;
        Bitmap? bmp = null;
        if (path.Length > 0)
        {
            try { bmp = new Bitmap(path); }
            catch { bmp = null; }
        }
        _artworkPath = path;
        Set(ref _artwork, bmp, nameof(Artwork));
    }

    private void TryRestorePlayback(MediaSnapshot snapshot)
    {
        if (_restoreApplied || _restoredTrackKey is null) return;
        _restoreApplied = true;
        if (!string.Equals(_restoredTrackKey, TrackKeyOf(snapshot.Track), StringComparison.OrdinalIgnoreCase)) return;
        if (_restoredPosition < 5) return;
        if (snapshot.DurationSeconds > 0 && _restoredPosition > snapshot.DurationSeconds - 5) return;
        // 仅当确实从曲目开头开始（<10s）才跳回上次位置，避免用户在时间轴中间手动重启时被打断
        if (snapshot.PositionSeconds > 10) return;
        _ = _coordinator.SeekAsync(_restoredPosition);
    }

    // ── 歌词 ─────────────────────────────────────────────────

    private async Task LoadLyricsAsync(MediaSnapshot snapshot)
    {
        try
        {
            var key = LyricsKeyOf(snapshot.Track);
            if (key == _lyricsKey) return;
            var result = await _lyricsService.GetLyricsAsync(snapshot);
            // 期间可能又切歌：只在仍为同一曲目时应用
            if (_currentTrackKey != TrackKeyOf(snapshot.Track)) return;
            _lyricsKey = key;
            _lyrics = result;
            _ttmlLineIndex.Clear();
            if (result.Ttml is not null)
            {
                foreach (var line in result.Ttml.Lines)
                {
                    if (!_ttmlLineIndex.ContainsKey(line.BeginSec)) _ttmlLineIndex[line.BeginSec] = line;
                }
            }
            _lyricIndex = -1;
            _lyricOffset = _settings.Current.LyricTimeOffsets.TryGetValue(key, out var off) ? Math.Clamp(off, -30, 30) : 0;
            UpdateLyricLine(_interpolatedPosition);
            OnPropertyChanged(nameof(LyricsSourceLabel));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Mac lyrics load failed: {ex.Message}");
        }
    }

    private void ClearLyrics()
    {
        _lyrics = LyricsResult.Empty;
        _lyricsKey = "";
        _ttmlLineIndex.Clear();
        _lyricIndex = -1;
        Set(ref _lyricText, "", nameof(LyricText));
        Set(ref _nextLyricText, "", nameof(NextLyricText));
        Set(ref _lyricWords, null, nameof(LyricWords));
        OnPropertyChanged(nameof(HasLyrics));
    }

    private void UpdateLyricLine(double position)
    {
        if (_lyrics.IsEmpty)
        {
            if (_lyricText.Length > 0) ClearLyrics();
            return;
        }
        var effPos = position - _lyricOffset;
        var idx = _lyrics.Document.IndexAt(TimeSpan.FromSeconds(Math.Max(0, effPos)));
        if (idx == _lyricIndex)
        {
            KaraokePosition = effPos;
            UpdateLineHighlight(effPos);
            return;
        }
        _lyricIndex = idx;
        var lines = _lyrics.Document.Lines;
        var lineText = idx >= 0 && idx < lines.Count ? lines[idx].Text : "";
        Set(ref _lyricText, lineText, nameof(LyricText));
        OnPropertyChanged(nameof(HasLyrics));

        // 本句时间窗（整行均分模式用；最后一行按 +6s 兜底）
        if (idx >= 0 && idx < lines.Count)
        {
            _lineStartSec = lines[idx].Time.TotalSeconds;
            _lineEndSec = idx + 1 < lines.Count ? lines[idx + 1].Time.TotalSeconds : _lineStartSec + 6;
        }
        else
        {
            _lineStartSec = _lineEndSec = 0;
        }

        // 该句若有 TTML 逐字时间轴 → 逐字卡拉OK；否则整行比例高亮
        IReadOnlyList<TtmlWord>? words = null;
        if (idx >= 0 && idx < lines.Count && _ttmlLineIndex.Count > 0)
        {
            words = FindTtmlWords(lines[idx].Time.TotalSeconds);
        }
        Set(ref _lyricWords, words, nameof(LyricWords));
        KaraokePosition = effPos;
        UpdateLineHighlight(effPos);
        UpdateNextLyric(idx);
    }

    private void UpdateLineHighlight(double effPos)
    {
        if (_lyricWords is not null || _lineEndSec <= _lineStartSec)
        {
            if (_lineHighlightFraction != 0) LineHighlightFraction = 0;
            return;
        }
        LineHighlightFraction = Math.Clamp((effPos - _lineStartSec) / (_lineEndSec - _lineStartSec), 0, 1);
    }

    private IReadOnlyList<TtmlWord>? FindTtmlWords(double lineStart)
    {
        // 容差 0.35s 匹配 TTML 行与 LRC 行（两个来源的时间轴可能有轻微偏差）
        foreach (var kv in _ttmlLineIndex)
        {
            if (Math.Abs(kv.Key - lineStart) < 0.35 && kv.Value.Words.Count > 0) return kv.Value.Words;
        }
        return null;
    }

    private void UpdateNextLyric(int idx)
    {
        var next = idx + 1 < _lyrics.Document.Lines.Count ? _lyrics.Document.Lines[idx + 1].Text : "";
        Set(ref _nextLyricText, next, nameof(NextLyricText));
    }

    // ── 推送 ─────────────────────────────────────────────────

    private void OnPushReceived(IslandPush push) => Raise(() =>
    {
        _pushes.RemoveAll(p => string.Equals(p.Id, push.Id, StringComparison.Ordinal));
        _pushes.Add(push);
        RefreshActivePush();
    });

    private void OnPushRemoved(string id) => Raise(() =>
    {
        _pushes.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        RefreshActivePush();
    });

    private void DismissPush(string id)
    {
        _api?.Remove(id);
        _pushes.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        RefreshActivePush();
    }

    private void RefreshActivePush()
    {
        var previous = _activePush;
        // 最高优先级 + 最近加入者优先
        IslandPush? best = null;
        for (var i = _pushes.Count - 1; i >= 0; i--)
        {
            var p = _pushes[i];
            if (best is null || p.PriorityRank > best.PriorityRank) best = p;
            if (p.PriorityRank == 2) break; // high 已是最优
        }
        _activePush = best;
        if (!ReferenceEquals(previous, best)) OnPropertyChanged(nameof(PushAccentBrush));

        PushButtons.Clear();
        if (_activePush?.Buttons is { Count: > 0 } buttons)
        {
            foreach (var b in buttons)
            {
                if (string.IsNullOrWhiteSpace(b.Label)) continue;
                PushButtons.Add(new IslandPushButtonView(b.Label, PushButtonCommand) { Source = b });
            }
        }

        if (_activePush is not null && !string.IsNullOrEmpty(_activePush.Image))
        {
            _ = LoadPushImageAsync(_activePush.Image);
        }
        else if (_pushImage is not null)
        {
            _pushImage = null;
        }

        PushInputValue = _activePush?.Input?.Value ?? "";
        OnPropertyChanged(nameof(HasPush));
        OnPropertyChanged(nameof(PushTitle));
        OnPropertyChanged(nameof(PushSubtitle));
        OnPropertyChanged(nameof(PushSubtitleVisible));
        OnPropertyChanged(nameof(PushBody));
        OnPropertyChanged(nameof(PushBodyVisible));
        OnPropertyChanged(nameof(PushIcon));
        OnPropertyChanged(nameof(PushProgressVisible));
        OnPropertyChanged(nameof(PushProgress));
        OnPropertyChanged(nameof(PushAccent));
        OnPropertyChanged(nameof(PushImageVisible));
        OnPropertyChanged(nameof(PushImage));
        OnPropertyChanged(nameof(PushButtonsVisible));
        OnPropertyChanged(nameof(PushInputVisible));
        OnPropertyChanged(nameof(PushInputPlaceholder));
        OnPropertyChanged(nameof(PushSubmitLabel));
        OnPropertyChanged(nameof(TypedAccentBrush));
        RaiseMode();
    }

    private async Task LoadPushImageAsync(string image)
    {
        try
        {
            Bitmap? bmp = null;
            if (image.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var comma = image.IndexOf(',');
                if (comma > 0)
                {
                    var bytes = Convert.FromBase64String(image[(comma + 1)..]);
                    using var ms = new MemoryStream(bytes);
                    bmp = new Bitmap(ms);
                }
            }
            else if (image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var key = "push-" + ArtworkCache.CacheKey("push", "", image, "", "");
                var path = Path.Combine(AppPaths.ThumbCacheDir, key + ".img");
                if (!File.Exists(path))
                {
                    await ArtworkCache.DownloadAsync(image, key, CancellationToken.None);
                }
                if (File.Exists(path)) bmp = new Bitmap(path);
            }
            else if (File.Exists(image))
            {
                bmp = new Bitmap(image);
            }

            Raise(() =>
            {
                if (_activePush?.Image != image) return;
                _pushImage = bmp;
                OnPropertyChanged(nameof(PushImage));
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Push image load failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 执行推送动作：
    ///   url     → open（默认浏览器/系统打开）
    ///   launch  → open（应用包/文件路径）
    ///   command → /bin/sh -c（仅本机回环 API；请只信任可信推送方）
    ///   notify  → 通过 IslandApiServer 广播 push_button 事件回传推送方
    /// </summary>
    private void HandlePushAction(IslandPush push, string action, string value, string label)
    {
        try
        {
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "url":
                case "launch":
                    if (!string.IsNullOrWhiteSpace(value)) OpenWithSystem(value);
                    break;
                case "command":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _ = Task.Run(() => RunShell(value));
                    }
                    break;
                default: // notify（含 input 提交）
                    _api?.BroadcastPushButton(push.Id, label, value);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Push action failed: {ex.Message}");
        }
    }

    private static void OpenWithSystem(string value)
    {
        var psi = new ProcessStartInfo { FileName = "/usr/bin/open", UseShellExecute = false };
        psi.ArgumentList.Add(value);
        Process.Start(psi);
    }

    private static void RunShell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "/bin/sh", UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Push command failed: {ex.Message}");
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings s) => Raise(() =>
    {
        OnPropertyChanged(nameof(PushAccentBrush));
        OnPropertyChanged(nameof(TypedAccentBrush));
        UpdateDateText();

        // 歌词来源相关设置变化 → 清缓存并重新加载当前曲目歌词
        if (_lastPreferredSource != s.LyricsPreferredSource || _lastAmll != s.AmllTtmlEnabled || _lastOnline != s.OnlineLyricsEnabled)
        {
            _lastPreferredSource = s.LyricsPreferredSource;
            _lastAmll = s.AmllTtmlEnabled;
            _lastOnline = s.OnlineLyricsEnabled;
            _lyricsService.ClearCache();
            _lyricsKey = "";
            if (_snapshot is not null) _ = LoadLyricsAsync(_snapshot);
        }
    });

    // ── 定时推进 ─────────────────────────────────────────────

    private void ProgressTick()
    {
        if (_snapshot is null) return;
        if (_isPlaying)
        {
            // 两次快照之间按墙钟连续推进（位置每 ~1s 由快照校正一次，帧率无关）
            var pos = _snapshot.PositionSeconds + _interpClock.Elapsed.TotalSeconds;
            if (_snapshot.DurationSeconds > 0) pos = Math.Min(pos, _snapshot.DurationSeconds);
            _interpolatedPosition = pos;
        }
        else
        {
            // 暂停：位置保持不动
            _interpolatedPosition = _snapshot.PositionSeconds;
        }
        UpdateProgressText();
        UpdateLyricLine(_interpolatedPosition);
    }

    private void UpdateProgressText()
    {
        var dur = _snapshot?.DurationSeconds ?? 0;
        var pos = Math.Max(0, _interpolatedPosition);
        _suppressSeek = true;
        ProgressValue = dur > 1 ? Math.Clamp(pos / dur, 0, 1) : 0;
        _suppressSeek = false;
        PositionText = FormatTime(pos);
        DurationText = FormatTime(Math.Max(0, dur));
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void WidgetTick()
    {
        _tick++;
        // 推送过期（服务端也会推 PushRemoved，这里兜底）与动态进度
        if (_pushes.Count > 0)
        {
            var now = DateTime.UtcNow;
            var removed = _pushes.RemoveAll(p => p.ExpiresAt is DateTime dt && dt <= now);
            if (removed > 0) RefreshActivePush();
            else if (_activePush?.ProgressDurationSeconds is int d && d > 0)
                OnPropertyChanged(nameof(PushProgress));
        }

        // 时钟（仅分钟变化时刷新绑定）
        var hhmm = DateTime.Now.ToString("HH:mm");
        if (hhmm != _clockText) Set(ref _clockText, hhmm, nameof(ClockText));
        if (_tick % 30 == 0) UpdateDateText();

        // 播放状态每 5 秒落盘（崩溃后尽量恢复）
        if (_tick % 5 == 0 && _snapshot is not null && _isPlaying) SavePlaybackState();

        RefreshIdleWidgets();
    }

    private void RefreshIdleWidgets()
    {
        var shouldShow = _hasMedia || HasPush || _settings.Current.IslandAlwaysVisible || !_settings.Current.HideWhenNoMedia;
        if (IslandVisible != shouldShow) IslandVisible = shouldShow;
    }

    private void SavePlaybackState()
    {
        try
        {
            if (_snapshot is null) return;
            new PlaybackStateStore
            {
                TrackKey = TrackKeyOf(_snapshot.Track),
                PositionSeconds = _interpolatedPosition,
                Status = _isPlaying ? "Playing" : "Paused",
                SavedAtUtc = DateTime.UtcNow,
            }.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Save playback state failed: {ex.Message}");
        }
    }

    private void RaiseMode() => OnPropertyChanged(nameof(Mode));

    private void UpdateDateText()
    {
        var now = DateTime.Now;
        var isZh = !(_settings.Current.Language ?? "zh-CN").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var text = isZh
            ? $"{now:M月d日} {ZhWeekday(now.DayOfWeek)}"
            : now.ToString("ddd, MMM d", CultureInfo.GetCultureInfo("en-US"));
        Set(ref _dateText, text, nameof(DateText));
    }

    private static string ZhWeekday(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    internal static string TrackKeyOf(TrackInfo t) => $"{t.Artist}\u0001{t.Title}\u0001{t.Album}";

    /// <summary>与 Core LyricsService.TrackKey 相同的清洗规则（小写字母数字），保证歌词偏移等键跨平台一致。</summary>
    internal static string LyricsKeyOf(TrackInfo t) =>
        $"{SanitizeKey(t.Artist)}\u0001{SanitizeKey(t.Title)}\u0001{SanitizeKey(t.Album)}";

    private static string SanitizeKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Segoe MDL2 私有区字形在 macOS 无字体，忽略；保留 emoji/普通字符。</summary>
    private static string NormalizeIcon(string icon)
    {
        if (string.IsNullOrEmpty(icon)) return "";
        var ch = icon[0];
        if (ch is >= '\uE000' and <= '\uF8FF') return "";
        return icon;
    }

    private static IBrush ResolveAccent(IslandPush? push, bool forText = false)
    {
        var fallback = push?.Type?.Trim().ToLowerInvariant() switch
        {
            "success" => Color.FromRgb(0x2E, 0xCC, 0x71),
            "warning" => Color.FromRgb(0xF1, 0xC4, 0x0F),
            "error" => Color.FromRgb(0xE7, 0x4C, 0x3C),
            _ => Color.FromRgb(0x6C, 0x5C, 0xE7),
        };
        var accent = (push?.Accent ?? "").Trim();
        if (accent.StartsWith("#") && TryParseColor(accent, out var c)) return new SolidColorBrush(c);
        return new SolidColorBrush(fallback);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.White;
        try
        {
            var s = hex.TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            if (s.Length != 8) return false;
            color = Color.FromArgb(
                Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16), Convert.ToByte(s.Substring(6, 2), 16));
            return true;
        }
        catch { return false; }
    }

    private void Raise(Action action)
    {
        if (_sync is null || ReferenceEquals(_sync, SynchronizationContext.Current)) action();
        else _sync.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _progressTimer.Stop();
        _progressTimer.Dispose();
        _widgetTimer.Stop();
        _widgetTimer.Dispose();
        _seekDebounce.Stop();
        _seekDebounce.Dispose();
        _coordinator.SnapshotChanged -= OnSnapshotChanged;
        _coordinator.MediaEnded -= OnMediaEnded;
        if (_api is not null)
        {
            _api.PushReceived -= OnPushReceived;
            _api.PushRemoved -= OnPushRemoved;
        }
        _settings.Changed -= OnSettingsChanged;
        SavePlaybackState();
    }
}

/// <summary>推送按钮的绑定包装（命令 + 原按钮）。</summary>
public sealed class IslandPushButtonView
{
    public IslandPushButtonView(string label, RelayCommand command)
    {
        Label = label;
        Command = command;
    }

    public string Label { get; }
    public RelayCommand Command { get; }
    public IslandPushButton? Source { get; set; }
}
