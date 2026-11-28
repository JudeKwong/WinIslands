using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using WinIslands.Services;

namespace WinIslands.UI;

/// <summary>
/// View model for the island window: maps coordinator snapshots to bindable state,
/// interpolates playback position for a smooth progress bar, and manages lyrics.
/// </summary>
public sealed partial class IslandViewModel : ObservableObject, IDisposable
{
    private int _disposed;
    private readonly MediaCoordinator _coordinator;
    private bool _demoInjected;
    private readonly SettingsService _settings;
    private readonly LyricsService _lyricsService;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _widgetTimer;
    private readonly DispatcherTimer _copyToastTimer;
    private readonly WeatherService _weather = new();
    private int _weatherTick;
    private DateTime _trackStartTime = DateTime.UtcNow;
    private bool _useFreeClock; // 播放器不报 SMTC 进度（如 Cider）时用本地时钟推进卡拉OK

    private MediaSnapshot? _snapshot;
    private LyricsResult _lyrics = LyricsResult.Empty;
    private DateTime _lastPositionTime = DateTime.UtcNow;
    private double _interpolatedPosition;
    private int _lyricIndex = -1;
    private DateTime? _positionStaleSinceUtc; // 上报位置明显回退的起始时刻（防瞬间 0/过期位置打回开头）
    private bool _expanded;
    private bool _visible;
    private bool _userHidden;
    private bool _suppressVolume;
    private int _suppressSeek;
    private string _lyricsKey = string.Empty;
    private readonly Dictionary<string, double> _lyricTimeOffsets = new(); // #4 歌词时间微调：曲目 key -> 偏移秒
    private string? _restoredTrackKey; // 上次退出时保存的曲目（用于启动恢复位置）
    private double _restoredPosition; // 上次退出时保存的位置（秒）
    private bool _karaokeFrozen; // 暂停时高亮是否已冻结（避免位置校正导致跳动）
    private bool _restoredMode; // 启动恢复后信任恢复位置：暂不采纳回退/过期位置
    private bool _toggleInFlight; // 播放/暂停命令在途（防连点重复触发）
    private bool _statusOverrideActive; // 乐观状态保护期：期间不被快照打回
    private PlaybackStatus _optimisticStatus; // 乐观目标状态（等待快照确认）
    private DateTime _statusOverrideUntilUtc; // 保护期截止时间
    private bool _pauseLock; // 暂停锁定：期间不随快照恢复播放、不推进歌词
    private PlaybackStatus _restoredStatus = PlaybackStatus.Closed; // 上次退出的状态（用于启动恢复）

    // ── 上岛推送（第三方软件推送到灵动岛）──
    private readonly List<IslandPush> _pushes = new();
    private IslandPush? _activePush;
    private string _pushInputValue = ""; // 上岛输入框当前文字
    private string _copyToastText = string.Empty;
    private bool _hasCopyToast;

    /// <summary>页面过渡开始：旧页、新页、方向（正数表示新页从右侧进入）。</summary>
    public event Action<IslandPage, IslandPage, int>? PageTransitionStarted;

    // ── 效率工具 / 波纹 / 键盘指示灯 ──
    private readonly AudioWaveService _wave;
    private readonly KeyboardIndicatorMonitor _keyboard;
    private readonly ClipboardCopyMonitor _clipboard;
    private int _capsLockSecondsLeft; // 键盘指示灯剩余显示秒数（由 _widgetTimer 每秒递减）
    private int _volumeTempSecondsLeft; // 音量指示剩余显示秒数
    private double _lastVolumeTempValue = -1; // 上次轮询到的系统音量（变化时触发上岛）
    private bool _lastVolumeTempMuted; // 上次轮询到的静音状态
    // ── 多播放器选择器（迷你播放器 / 设置中切换媒体来源）──
    private readonly ObservableCollection<MediaSessionItem> _mediaSessions = new();
    private MediaSessionItem? _selectedMediaSession;
    private bool _suppressSessionSwitch;

    /// <summary>复制成功轻量浮层文本；不进入通知队列、不写入通知历史。</summary>
    public string CopyToastText
    {
        get => _copyToastText;
        private set => Set(ref _copyToastText, value);
    }

    public bool HasCopyToast
    {
        get => _hasCopyToast;
        private set => Set(ref _hasCopyToast, value);
    }

    // ── 展开态通知历史已在 3.1.2 beta12 移除；通知仍按队列即时展示。 ──
    private readonly ObservableCollection<MessengerUnread> _messengerUnreads = new();
    private readonly ObservableCollection<IslandPage> _pages = new();
    private string _pageSignature = string.Empty;
    private static readonly (string Key, string Title, string Description, string Accent)[] PageDefs =
    {
        ("Music", "音乐", "封面、歌词与播放控制", "#FD685C"),
    };
    private static readonly Brush WeChatBadgeBrush = MakeFrozenBrush(0x46, 0xCB, 0x7B);
    private static readonly Brush QqBadgeBrush = MakeFrozenBrush(0x12, 0xB7, 0xF5);
    private static readonly Brush BluetoothBadgeBrush = MakeFrozenBrush(0x0A, 0x84, 0xFF);
    private static readonly Brush DefaultBadgeBrush = MakeFrozenBrush(0x6C, 0x5C, 0xE7);

    private static Brush MakeFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
    private readonly MessengerUnreadMonitor? _messengerMonitor;
    private readonly Dictionary<string, DateTime> _lastEventTimes = new(); // 通知去抖：记录每种类型最后触发时间
    public ObservableCollection<MessengerUnread> MessengerUnreads => _messengerUnreads;
    public bool HasMessengerUnreads => _messengerUnreads.Count > 0;
    public int MessengerUnreadCount => _messengerUnreads.Count;

    public ObservableCollection<IslandPage> Pages => _pages;
    public IslandPage? ActivePage => _pages.FirstOrDefault(p => p.IsActive);
    public string ActivePageTitle => ActivePage?.Title ?? "音乐";
    public Brush ActivePageAccentBrush => ActivePage?.AccentBrush ?? new SolidColorBrush(Color.FromRgb(0x79, 0x55, 0xB8));
    public bool HasMultiplePages => _pages.Count > 1;
    public bool IsMusicPage => ActivePage?.Key == "Music";
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand NextPageCommand { get; }

    public IslandViewModel(MediaCoordinator coordinator, SettingsService settings, LyricsService lyricsService,
        AudioWaveService? wave = null, KeyboardIndicatorMonitor? keyboard = null,
        ClipboardCopyMonitor? clipboard = null,
        MessengerUnreadMonitor? messenger = null)
    {
        _coordinator = coordinator;
        _settings = settings;
        _lyricsService = lyricsService;

        // 效率工具 / 波纹 / 键盘指示灯：默认自建实例（App 可注入共享实例）
        _wave = wave ?? new AudioWaveService();
        _keyboard = keyboard ?? new KeyboardIndicatorMonitor();
        _clipboard = clipboard ?? new ClipboardCopyMonitor();
        _messengerMonitor = messenger;
        if (_messengerMonitor is not null) _messengerMonitor.Changed += OnMessengerUnreadsChanged;
        PrevPageCommand = new RelayCommand(_ => NavigatePage(-1));
        NextPageCommand = new RelayCommand(_ => NavigatePage(1));
        _settings.Changed += OnPageSettingsChanged;
        RebuildPages();
        // 轻量化：波纹可视化关闭时不启动音频线程（旧代码无条件启动，导致即使关掉波纹仍常驻采集/唤醒）
        if (_settings.Current.WaveVisualizerEnabled)
        {
            _wave.Start();
            _wave.SetSyncEnabled(_settings.Current.WaveSyncEnabled);
        }
        _wave.SetPlaying(false);
        _keyboard.StateChanged += OnKeyboardStateChanged;

        // #4 歌词时间微调：加载为每首歌保存的时间偏移（用户手动校准的歌词对齐）
        try
        {
            foreach (var kv in _settings.Current.LyricTimeOffsets)
                _lyricTimeOffsets[kv.Key] = Math.Clamp(kv.Value, -30, 30);
        }
        catch (Exception ex) { AppLogger.Warn($"Load lyric offsets failed: {ex.Message}"); }

        // 启动时恢复上次退出的播放位置（暂停后重启不跳回开头）
        var restored = PlaybackStateStore.Load();
        if (restored is not null)
        {
            _restoredTrackKey = restored.TrackKey;
            _restoredPosition = restored.PositionSeconds;
            _restoredStatus = string.Equals(restored.Status, "Paused", StringComparison.OrdinalIgnoreCase)
                ? PlaybackStatus.Paused : PlaybackStatus.Playing;
        }

        // 系统状态计数器（CPU/内存）—— 复用实例，避免每次采样都新建
        _cpuCounter = CreateCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = CreateCounter("Memory", "% Committed Bytes In Use", null);

        PlayPauseCommand = new AsyncRelayCommand(_ => TogglePlayPauseLocalAsync());
        NextCommand = new AsyncRelayCommand(_ => _coordinator.NextAsync());
        PreviousCommand = new AsyncRelayCommand(_ => _coordinator.PreviousAsync());
        SeekBackwardCommand = new AsyncRelayCommand(_ => SeekRelativeAsync(-MediaControlMath.SeekStepSeconds));
        SeekForwardCommand = new AsyncRelayCommand(_ => SeekRelativeAsync(MediaControlMath.SeekStepSeconds));
        ToggleShuffleCommand = new AsyncRelayCommand(_ => _coordinator.ToggleShuffleAsync());
        CycleRepeatCommand = new AsyncRelayCommand(_ => _coordinator.CycleRepeatAsync());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        ToggleLyricsWindowCommand = new RelayCommand(_ => ToggleLyricsWindowRequested?.Invoke(this, EventArgs.Empty));

        _coordinator.SnapshotChanged += OnSnapshotChanged;
        _coordinator.MediaEnded += OnMediaEnded;
        _coordinator.SessionsChanged += OnSessionsChanged;
        RefreshMediaSessions();
        Localization.LanguageChanged += OnLanguageChanged;
        _copyToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
        _copyToastTimer.Tick += (_, _) =>
        {
            _copyToastTimer.Stop();
            HasCopyToast = false;
            CopyToastText = string.Empty;
        };

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) }; // 进度插值 5Hz：进度条按秒显示足够，逐字卡拉OK由控件内部按 CompositionTarget.Rendering 连续推进，降低播放时 CPU 占用
        _progressTimer.Tick += (_, _) => AdvanceProgress();
        // 不立即启动：有媒体快照时（OnSnapshotChanged）才启动，空闲/无媒体时停用，降低后台占用

        _widgetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _widgetTimer.Tick += async (_, _) =>
        {
            // 始终轮询的轻量检测：即使灵动岛隐藏，音量/复制/下载/截图等事件也要能触发上岛
            PollVolumeTemp();
            UpdateVolumeTempCountdown();
            PollFileCopy();
            PollDownloadProgress();
            _widgetTimer.Interval = TimeSpan.FromSeconds(1); // 岛可见时恢复正常 1 秒轮询
            if (!IsVisible) { _widgetTimer.Interval = TimeSpan.FromSeconds(5); return; } // 岛隐藏时降频到 5 秒，减少空闲 CPU 占用
            var nowClock = DateTime.Now.ToString("HH:mm");
            if (ClockText != nowClock) ClockText = nowClock;
            var nowDate = FormatDateText(DateTime.Now);
            if (DateText != nowDate) DateText = nowDate;
            CheckPushExpiry();
            if (_activePush is not null) OnPropertyChanged(nameof(ActivePushProgress)); // v3 动态进度按秒推进
            UpdateSystemStats();
            UpdateCapsLockCountdown();
            if (ShowIdleInputMethod) InputMethodText = InputMethodMonitor.GetStatusText();
            if (ShowIdleQuickToggles) RefreshQuickToggles();
            if (ShowIdleWeather && ++_weatherTick % 60 == 1)
            {
                var w = await _weather.GetWeatherAsync(_settings.Current.WeatherCity);
                if (w is not null) // 失败保留旧值，避免天气忽有忽无
                {
                    _weatherInfo = w;
                    WeatherText = FormatWeatherCompact(w);
                    WeatherDetailText = FormatWeatherDetail(w);
                }
            }
        };
        _widgetTimer.Start();
        RebuildQuickActions();
    }

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ToggleLyricsWindowRequested;
    /// <summary>#10 上岛按钮回调：推送方配置了 notify 动作的按钮被点击时触发（参数：按钮 + 推送 ID）。</summary>
    public event Action<IslandPushButton, string>? PushActionRequested;

    // ── 快捷操作按钮（展开卡片底部一排）──────────────────────────
    private IReadOnlyList<QuickActionItem> _quickActions = Array.Empty<QuickActionItem>();
    public IReadOnlyList<QuickActionItem> QuickActions { get => _quickActions; private set => Set(ref _quickActions, value); }

    /// <summary>是否有快捷操作可显示（总开关开启且有勾选的操作）。</summary>
    public bool HasQuickActions => QuickActions.Count > 0;

    /// <summary>按设置重建快捷操作列表（顺序即设置中的顺序）。</summary>
    public void RebuildQuickActions()
    {
        var master = _settings.Current.QuickActionsEnabled;
        var shown = _settings.Current.QuickActionsShown ?? new List<string>();
        var items = new List<QuickActionItem>();
        if (master)
        {
            foreach (var key in _settings.Current.QuickActions ?? new List<string>())
            {
                if (!shown.Contains(key, StringComparer.Ordinal)) continue; // 未勾选不显示
                var (glyph, tipKey) = QuickActionMeta(key);
                if (glyph is null) continue;
                items.Add(new QuickActionItem(key, glyph, Localization.Get(tipKey)));
            }
        }
        QuickActions = items;
        OnPropertyChanged(nameof(HasQuickActions));
    }

    /// <summary>快捷操作键 → (图标字形, 提示本地化键)；未知键返回 null。</summary>
    private static (string? Glyph, string TipKey) QuickActionMeta(string key) => key switch
    {
        "Lock" => ("\uE72E", "QA_Lock"),
        "Mute" => ("\uE74F", "QA_Mute"),
        "PlayPause" => ("\uE768", "QA_PlayPause"),
        "Screenshot" => ("\uE916", "QA_Screenshot"),
        "Settings" => ("\uE713", "QA_Settings"),
        "Desktop" => ("\uE8B9", "QA_Desktop"),
        "TaskManager" => ("\uE7C3", "QA_TaskManager"),
        "Calculator" => ("\uE8EF", "QA_Calculator"),
        "Sleep" => ("\uE7E0", "QA_Sleep"),
        "VolumeUp" => ("\uE995", "QA_VolumeUp"),
        "VolumeDown" => ("\uE994", "QA_VolumeDown"),
        _ => (null, string.Empty),
    };

    /// <summary>执行快捷操作（按钮点击）。全部为系统能力，纯本机，不联网。</summary>
    public void ExecuteQuickAction(string key)
    {
        try
        {
            switch (key)
            {
                case "Lock":
                    NativeMethods.LockWorkStation();
                    break;
                case "Mute":
                    SystemVolume.SetMute(!SystemVolume.IsMuted());
                    break;
                case "PlayPause":
                    PlayPauseCommand.Execute(null);
                    break;
                case "Screenshot":
                    // 发送 PrintScreen 键（触发系统截图 / 粘贴）
                    NativeMethods.keybd_event(0x2C, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(0x2C, 0, 2, UIntPtr.Zero);
                    break;
                case "Settings":
                    OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case "Desktop":
                    NativeMethods.keybd_event(0x5B, 0, 0, UIntPtr.Zero); // Win
                    NativeMethods.keybd_event(0x44, 0, 0, UIntPtr.Zero); // D
                    NativeMethods.keybd_event(0x44, 0, 2, UIntPtr.Zero);
                    NativeMethods.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
                    break;
                case "TaskManager":
                    Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                    break;
                case "Calculator":
                    Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
                    break;
                case "Sleep":
                    NativeMethods.SetSuspendState(false, true, false);
                    break;
                case "VolumeUp":
                    SystemVolume.SetVolume(Math.Clamp((SystemVolume.GetVolume() ?? 0.5) + 0.1, 0, 1));
                    break;
                case "VolumeDown":
                    SystemVolume.SetVolume(Math.Clamp((SystemVolume.GetVolume() ?? 0.5) - 0.1, 0, 1));
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Quick action '{key}' failed: {ex.Message}");
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool LockWorkStation();
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("powrprof.dll")]
        public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    }

    // ── 多播放器选择器 ────────────────────────────────────────
    /// <summary>当前可用媒体会话（SMTC 全部 + Cider 伪会话），供迷你播放器/设置切换来源。</summary>
    public ObservableCollection<MediaSessionItem> MediaSessions => _mediaSessions;

    /// <summary>是否同时存在多个可用媒体来源（#3 多播放器切换：展开卡中显示选择器）。</summary>
    public bool HasMultipleSessions => _mediaSessions.Count > 1;

    public MediaSessionItem? SelectedMediaSession
    {
        get => _selectedMediaSession;
        set
        {
            if (!Set(ref _selectedMediaSession, value)) return;
            OnPropertyChanged(nameof(SelectedMediaSessionName));
            if (_suppressSessionSwitch || value is null) return;
            _ = SwitchSelectedSessionAsync(value.AppId);
        }
    }

    /// <summary>当前所选媒体来源名称（#3 选择器按钮显示）。</summary>
    public string SelectedMediaSessionName => _selectedMediaSession?.AppName ?? "";

    /// <summary>循环切换到下一个可用媒体来源（#3 多播放器切换）。</summary>
    public void CycleMediaSession()
    {
        if (_mediaSessions.Count < 2) return;
        var idx = _mediaSessions.IndexOf(_selectedMediaSession!);
        var next = _mediaSessions[(idx + 1) % _mediaSessions.Count];
        SelectedMediaSession = next;
    }

    /// <summary>刷新媒体会话列表并保持当前选中（不会触发切换）。</summary>
    private void OnSessionsChanged(object? s, EventArgs e) => RefreshMediaSessions();
    private void OnLanguageChanged(object? s, EventArgs e) => RaiseAllText();

    public void RefreshMediaSessions()
    {
        try
        {
            var sessions = _coordinator.GetAvailableSessions();
            var selectedId = _selectedMediaSession?.AppId;
            _suppressSessionSwitch = true;
            try
            {
                _mediaSessions.Clear();
                foreach (var s in sessions)
                    _mediaSessions.Add(new MediaSessionItem(s.AppId, s.AppName, s.IsCurrent));

                _selectedMediaSession = _mediaSessions.FirstOrDefault(x => x.AppId == selectedId)
                    ?? _mediaSessions.FirstOrDefault(x => x.IsCurrent)
                    ?? _mediaSessions.FirstOrDefault();
            }
            finally
            {
                _suppressSessionSwitch = false;
            }
            OnPropertyChanged(nameof(SelectedMediaSession));
            OnPropertyChanged(nameof(SelectedMediaSessionName));
            OnPropertyChanged(nameof(HasMultipleSessions));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"刷新媒体会话列表失败: {ex.Message}");
        }
    }

    private async Task SwitchSelectedSessionAsync(string appId)
    {
        try
        {
            await _coordinator.SwitchSessionAsync(appId);
            RefreshMediaSessions();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"切换媒体来源失败: {ex.Message}");
        }
    }

    // ── Commands ───────────────────────────────────────────────
    public AsyncRelayCommand PlayPauseCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand SeekBackwardCommand { get; }
    public AsyncRelayCommand SeekForwardCommand { get; }
    public AsyncRelayCommand ToggleShuffleCommand { get; }
    public AsyncRelayCommand CycleRepeatCommand { get; }
    public string SeekBackwardHint => Localization.Get("Media_SeekBackHint");
    public string SeekForwardHint => Localization.Get("Media_SeekForwardHint");
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ToggleLyricsWindowCommand { get; }

    // ── Track ──────────────────────────────────────────────────
    private string _title = string.Empty;
    public string Title { get => _title; private set => Set(ref _title, value); }

    private string _artist = string.Empty;
    public string Artist { get => _artist; private set => Set(ref _artist, value); }

    private string _album = string.Empty;
    public string Album { get => _album; private set => Set(ref _album, value); }

    private string _sourceLabel = string.Empty;
    public string SourceLabel { get => _sourceLabel; private set => Set(ref _sourceLabel, value); }

    private string _sourceDetail = string.Empty;
    public string SourceDetail
    {
        get => _sourceDetail;
        private set
        {
            if (Set(ref _sourceDetail, value))
                OnPropertyChanged(nameof(HasSourceDetail));
        }
    }
    /// <summary>是否有播放来源名（歌名旁的小徽标是否显示）。</summary>
    public bool HasSourceDetail => !string.IsNullOrEmpty(_sourceDetail);

    private ImageSource? _artwork;
    // 封面缓存：同一路径只解码一次并复用（SMTC/Cider 每秒上报同一封面，避免反复 IO + 内存抖动）
    private readonly Dictionary<string, ImageSource> _artworkCache = new(StringComparer.OrdinalIgnoreCase);
    private const int ArtworkCacheMax = 12; // 近期封面上限，超出淘汰最旧，防止无限增长
    private string _pushImageCacheKey = string.Empty;
    private ImageSource? _pushImageCache;
    public ImageSource? Artwork { get => _artwork; private set => Set(ref _artwork, value); }

    // ── Playback ───────────────────────────────────────────────
    private PlaybackStatus _status;
    public PlaybackStatus Status { get => _status; private set => Set(ref _status, value); }

    public bool IsPlaying => Status == PlaybackStatus.Playing;
    public bool IsPaused => Status == PlaybackStatus.Paused;

    private double _durationSeconds;
    public double DurationSeconds { get => _durationSeconds; private set => Set(ref _durationSeconds, value); }

    private TimeSpan _position;
    private int _positionTextSecond = -1; // 已通知过的整秒；只在整秒变化时才刷新秒文本，避免每 200ms 重复格式化
    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (!Set(ref _position, value)) return;
            var sec = (int)Math.Max(0, value.TotalSeconds);
            if (sec != _positionTextSecond)
            {
                _positionTextSecond = sec;
                OnPropertyChanged(nameof(PositionText));
            }
        }
    }

    /// <summary>当前位置文本（精确到秒，如 1:23 / 1:02:03），与 DurationText 同格式。</summary>
    public string PositionText
    {
        get
        {
            var total = (int)Math.Max(0, _position.TotalSeconds);
            return total >= 3600
                ? TimeSpan.FromSeconds(total).ToString(@"h\:mm\:ss")
                : TimeSpan.FromSeconds(total).ToString(@"m\:ss");
        }
    }

    public string DurationText =>
        DurationSeconds >= 3600
            ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"h\:mm\:ss")
            : TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private bool _canPlayPause = true;
    public bool CanPlayPause { get => _canPlayPause; private set => Set(ref _canPlayPause, value); }

    private bool _canNext = true;
    public bool CanNext { get => _canNext; private set => Set(ref _canNext, value); }

    private bool _canPrevious = true;
    public bool CanPrevious { get => _canPrevious; private set => Set(ref _canPrevious, value); }

    private bool _canSeek;
    public bool CanSeek { get => _canSeek; private set => Set(ref _canSeek, value); }

    private bool _canShuffle;
    public bool CanShuffle { get => _canShuffle; private set => Set(ref _canShuffle, value); }

    private bool _canRepeat;
    public bool CanRepeat { get => _canRepeat; private set => Set(ref _canRepeat, value); }

    private bool _shuffleActive;
    public bool ShuffleActive
    {
        get => _shuffleActive;
        private set
        {
            if (!Set(ref _shuffleActive, value)) return;
            OnPropertyChanged(nameof(ShuffleGlyph));
        }
    }

    private MediaRepeatMode _repeatMode;
    public MediaRepeatMode RepeatMode
    {
        get => _repeatMode;
        private set
        {
            if (!Set(ref _repeatMode, value)) return;
            OnPropertyChanged(nameof(RepeatGlyph));
        }
    }

    public string ShuffleGlyph => "\uE8B1";
    public string RepeatGlyph => RepeatMode == MediaRepeatMode.Track ? "\uE8ED" : "\uE8EE";

    private bool _hasVolumeControl;
    public bool HasVolumeControl { get => _hasVolumeControl; private set => Set(ref _hasVolumeControl, value); }

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 1))) return;
            if (_suppressVolume) return;
            _ = _coordinator.SetVolumeAsync(_volume);
            OnPropertyChanged(nameof(VolumeText));
            ShowVolumeTemp((int)Math.Round(_volume * 100), _volume < 0.001); // 拖动音量滑杆时临时上岛
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768"; // Pause / Play (Segoe MDL2)

    // ── Lyrics ─────────────────────────────────────────────────
    private IReadOnlyList<LyricLineViewModel> _lyricLines = Array.Empty<LyricLineViewModel>();
    public IReadOnlyList<LyricLineViewModel> LyricLines { get => _lyricLines; private set => Set(ref _lyricLines, value); }

    public bool HasLyrics => LyricLines.Count > 0;

    public int LyricIndex
    {
        get => _lyricIndex;
        private set
        {
            if (_lyricIndex == value) return;
            var old = _lyricIndex;
            _lyricIndex = value;
            if (old >= 0 && old < LyricLines.Count) LyricLines[old].IsCurrent = false;
            if (value >= 0 && value < LyricLines.Count) LyricLines[value].IsCurrent = true;
            RefreshLyricTranslationVisibility();
            // 换句时解除暂停冻结，让 UpdateKaraokeHighlight 按当前（正确）位置重算一次
            _karaokeFrozen = false;
            // 每行 ViewModel 自带逐词时间轴（TTML 直接采用，LRC 自动补齐），无需再按时间反查
            CurrentLyricWords = value >= 0 && value < LyricLines.Count
                ? LyricLines[value].Words
                : Array.Empty<TtmlWord>();
            CurrentLyricText = value >= 0 && value < LyricLines.Count
                ? LyricLines[value].Text
                : string.Empty;
            OnPropertyChanged();
        }
    }

    private string _lyricsStatus = string.Empty;
    public string LyricsStatus { get => _lyricsStatus; private set => Set(ref _lyricsStatus, value); }

    /// <summary>当前歌词行文本（紧凑胶囊内未悬停时也显示）。</summary>
    private string _currentLyricText = string.Empty;
    public string CurrentLyricText { get => _currentLyricText; private set => Set(ref _currentLyricText, value); }

    /// <summary>是否显示歌词翻译行（展开歌词快捷操作：翻译开关）。</summary>
    private bool _showLyricTranslation = true;
    public bool ShowLyricTranslation
    {
        get => _showLyricTranslation;
        private set
        {
            if (Set(ref _showLyricTranslation, value))
            {
                // 展开歌词只给当前句显示翻译，保证上一句 / 当前句+翻译 / 下一句同时可见
                RefreshLyricTranslationVisibility();
                OnPropertyChanged(nameof(LyricTranslateText));
            }
        }
    }
    /// <summary>
    /// 展开歌词只给当前句显示翻译行：74px 视口内可同时呈现上一句、当前句+翻译、下一句。
    /// </summary>
    private void RefreshLyricTranslationVisibility()
    {
        for (var i = 0; i < LyricLines.Count; i++)
            LyricLines[i].ShowTranslation = _showLyricTranslation && i == _lyricIndex;
    }

    /// <summary>翻译开关按钮文本（如「翻译：开」）。</summary>
    public string LyricTranslateText => Localization.Get("Lyric_TranslateShort");
    /// <summary>「复制当前句」按钮文本。</summary>
    public string LyricCopyText => Localization.Get("Lyric_CopyShort");
    /// <summary>翻译开关提示。</summary>
    public string LyricTranslateHint => Localization.Get("Lyric_TranslateHint");
    /// <summary>复制当前句提示。</summary>
    public string LyricCopyHint => Localization.Get("Lyric_CopyHint");
    /// <summary>#4 歌词提前 0.5s 按钮提示。</summary>
    public string LyricOffsetDownHint => Localization.Get("Lyric_OffsetDownHint");
    /// <summary>#4 歌词延后 0.5s 按钮提示。</summary>
    public string LyricOffsetUpHint => Localization.Get("Lyric_OffsetUpHint");
    // ── 多歌词源一键切换（1.2.0）──
    /// <summary>歌词来源切换按钮文本（如「歌词：自动」）。</summary>
    public string LyricsSourceButtonText => Localization.Get("Lyrics_SourceButton") + "：" + CurrentLyricsSourceDisplay;
    /// <summary>当前首选歌词来源（设置里的值）。</summary>
    public string CurrentLyricsSource => _settings.Current.LyricsPreferredSource ?? "Auto";
    /// <summary>当前首选歌词来源的显示名。</summary>
    public string CurrentLyricsSourceDisplay => CurrentLyricsSource.ToUpperInvariant() switch
    {
        "LOCAL" => Localization.Get("Lyrics_SrcLocal"),
        "AMLL" => Localization.Get("Lyrics_SrcAmll"),
        "CIDER" => Localization.Get("Lyrics_SrcCider"),
        "ONLINE" => Localization.Get("Lyrics_SrcOnline"),
        _ => Localization.Get("Lyrics_SrcAuto"),
    };
    /// <summary>来源切换按钮提示。</summary>
    public string LyricsSourceButtonHint => Localization.Get("Lyrics_SourceHint");

    /// <summary>循环切换歌词来源：自动 → 本地 → AMLL → Cider → 在线 → 自动；立即重新加载当前歌曲歌词。</summary>
    public async void CycleLyricsSource()
    {
        try
        {
            var order = new[] { "Auto", "Local", "Amll", "Cider", "Online" };
            var cur = CurrentLyricsSource;
            var next = order[0];
            for (var i = 0; i < order.Length; i++)
            {
                if (string.Equals(order[i], cur, StringComparison.OrdinalIgnoreCase))
                {
                    next = order[(i + 1) % order.Length];
                    break;
                }
            }

            var snapshot = _snapshot;
            await Task.Run(() => _lyricsService.SetPreferredSource(next));
            OnPropertyChanged(nameof(LyricsSourceButtonText));
            OnPropertyChanged(nameof(CurrentLyricsSourceDisplay));
            AppLogger.Info($"Lyrics source switched to {next}");
            if (snapshot is not null)
            {
                _lyricsKey = string.Empty; // 强制重新加载歌词
                await LoadLyricsAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CycleLyricsSource failed: {ex.Message}");
        }
    }

    /// <summary>紧凑态逐字卡拉OK已点亮字符数。</summary>
    private double _compactHighlightFraction;
    public double CompactHighlightFraction { get => _compactHighlightFraction; private set => Set(ref _compactHighlightFraction, value); }

    /// <summary>当前卡拉OK绝对秒（含歌词偏移），驱动逐字高亮（控件按墙钟 60fps 连续推进）。</summary>
    private double _karaokePositionSeconds;
    public double KaraokePositionSeconds { get => _karaokePositionSeconds; private set => Set(ref _karaokePositionSeconds, value); }

    /// <summary>当前句逐字时间轴（AMLL TTML），供逐字卡拉OK控件使用；无则空集合。</summary>
    private IReadOnlyList<TtmlWord> _currentLyricWords = Array.Empty<TtmlWord>();
    public IReadOnlyList<TtmlWord> CurrentLyricWords { get => _currentLyricWords; private set => Set(ref _currentLyricWords, value); }

    // ── Idle widgets（无媒体时组件）───────────────────────────
    private bool _hasMedia;
    public bool HasMedia { get => _hasMedia; private set => Set(ref _hasMedia, value); }

    private bool _showIdleWidgets;
    public bool ShowIdleWidgets { get => _showIdleWidgets; private set => Set(ref _showIdleWidgets, value); }

    private string _clockText = string.Empty;
    public string ClockText { get => _clockText; private set => Set(ref _clockText, value); }

    private WeatherInfo? _weatherInfo;
    private string _weatherText = string.Empty;
    public string WeatherText { get => _weatherText; private set => Set(ref _weatherText, value); }
    private string _weatherDetailText = string.Empty;
    public string WeatherDetailText { get => _weatherDetailText; private set => Set(ref _weatherDetailText, value); }

    /// <summary>紧凑胶囊里的一个顺序组件。</summary>
    /// <summary>紧凑胶囊里的一个顺序组件（Kind=组件标识，Icon=显示图标字符，支持用户定制）。</summary>
    public sealed record IslandComponent(string Kind, string Icon, string? Text = null, string? ToolTip = null, bool IsPlugin = false, ImageSource? Image = null, double? Progress = null, System.Windows.Media.Brush? AccentBrush = null, IslandPushButton? Click = null); // "Time" | "Weather" | "Song" | Plugin:*
    // 歌曲相关组件（封面/歌名/歌手/歌词/进度条）：只在播放时显示，固定开启
    public bool ShowCover => HasMedia;
    public bool ShowTitle => HasMedia;
    public bool ShowArtist => HasMedia;
    public bool ShowLyrics => HasMedia;
    public bool ShowCompactProgress => HasMedia;

    // 时间/天气：空闲与播放分别按勾选显示
    public bool ShowIdleTime => HasMedia ? _settings.Current.Components.TimeWhenPlaying : _settings.Current.Components.TimeWhenIdle;
    public bool ShowIdleWeather => HasMedia ? _settings.Current.Components.WeatherWhenPlaying : _settings.Current.Components.WeatherWhenIdle;
    public bool ShowIdleDate => HasMedia ? _settings.Current.Components.DateWhenPlaying : _settings.Current.Components.DateWhenIdle;
    public bool ShowIdleCpu => HasMedia ? _settings.Current.Components.CpuWhenPlaying : _settings.Current.Components.CpuWhenIdle;
    public bool ShowIdleRam => HasMedia ? _settings.Current.Components.RamWhenPlaying : _settings.Current.Components.RamWhenIdle;
    public bool ShowIdleNet => HasMedia ? _settings.Current.Components.NetWhenPlaying : _settings.Current.Components.NetWhenIdle;
    public bool ShowIdleGpu => HasMedia ? _settings.Current.Components.GpuWhenPlaying : _settings.Current.Components.GpuWhenIdle;
    public bool ShowIdleMic => HasMedia ? _settings.Current.Components.MicWhenPlaying : _settings.Current.Components.MicWhenIdle;
    public bool ShowIdleCam => HasMedia ? _settings.Current.Components.CamWhenPlaying : _settings.Current.Components.CamWhenIdle;
    public bool ShowIdleBattery => HasMedia ? _settings.Current.Components.BatteryWhenPlaying : _settings.Current.Components.BatteryWhenIdle;
    public bool ShowIdleVolume => HasMedia ? _settings.Current.Components.VolumeWhenPlaying : _settings.Current.Components.VolumeWhenIdle;
    public bool ShowIdleCapsLock => HasMedia ? _settings.Current.Components.CapsLockWhenPlaying : _settings.Current.Components.CapsLockWhenIdle;
    public bool ShowIdleDisk => HasMedia ? _settings.Current.Components.DiskWhenPlaying : _settings.Current.Components.DiskWhenIdle;
    public bool ShowIdleInputMethod => HasMedia ? _settings.Current.Components.InputMethodWhenPlaying : _settings.Current.Components.InputMethodWhenIdle;
    public bool ShowIdleQuickToggles => HasMedia ? _settings.Current.Components.QuickTogglesWhenPlaying : _settings.Current.Components.QuickTogglesWhenIdle;
    public bool ShowAnyWidget => ShowIdleTime || ShowIdleWeather || ShowIdleDate
        || ShowIdleCpu || ShowIdleRam || ShowIdleGpu || ShowIdleNet || ShowIdleBattery || ShowIdleMic || ShowIdleCam
        || ShowIdleVolume || ShowIdleCapsLock || ShowIdleDisk || ShowIdleInputMethod || ShowIdleQuickToggles;

    // ── 效率工具 / 波纹 文本 ──
    /// <summary>波纹强度（0..1），由 AudioWaveService 实时采集/模拟，UI 轮询。</summary>
    public double WaveLevel => _wave.Level;
    public string VolumeText => HasVolumeControl ? $"{(_volume * 100):0}%" : string.Empty;

    private string _capsLockText = string.Empty;
    /// <summary>按键指示灯文本（如「Caps 开」），出现 N 秒后自动清空。</summary>
    public string CapsLockText { get => _capsLockText; private set => Set(ref _capsLockText, value); }
    private int _volumeTempGen; // 音量指示代际计数：用于取消过期的淡出清理
    private string _volumeTempText = string.Empty;
    /// <summary>音量/静音临时上岛文本（调节音量后出现，几秒后自动消失）。</summary>
    public string VolumeTempText { get => _volumeTempText; private set => Set(ref _volumeTempText, value); }

    private double _volumeTempPercent;
    /// <summary>音量临时指示的进度条值（0..1，与 VolumeTempText 同步刷新）。</summary>
    public double VolumeTempPercent { get => _volumeTempPercent; private set => Set(ref _volumeTempPercent, value); }

    private bool _volumeTempFading;
    /// <summary>音量指示消失前的淡出标志（True 时播放退场动画，随后从组件列表移除）。</summary>
    public bool VolumeTempFading
    {
        get => _volumeTempFading;
        private set { if (_volumeTempFading == value) return; _volumeTempFading = value; OnPropertyChanged(nameof(VolumeTempFading)); }
    }
    private string _usageMergeText = string.Empty;
    /// <summary>「使用中」合并胶囊文本（麦克风/摄像头/会议/录屏合并为一个状态胶囊）。</summary>
    public string UsageMergeText { get => _usageMergeText; private set => Set(ref _usageMergeText, value); }
    private string _fileCopyText = string.Empty;
    /// <summary>文件复制/移动进行中文本。</summary>
    public string FileCopyText { get => _fileCopyText; private set => Set(ref _fileCopyText, value); }
    private string _downloadText = string.Empty;
    /// <summary>下载进行中文本。</summary>
    public string DownloadText { get => _downloadText; private set => Set(ref _downloadText, value); }

    private sealed record PluginComponentEntry(string PluginId, PluginComponentSpec Component);
    private readonly List<PluginComponentEntry> _pluginComponents = new();

    private IReadOnlyList<IslandComponent> _compactItems = Array.Empty<IslandComponent>();
    public IReadOnlyList<IslandComponent> CompactItems { get => _compactItems; private set => Set(ref _compactItems, value); }

    // ── 文件中转站（拖文件上岛 → 组件显示 → 拖出到其他应用 / 资源管理器）────
    /// <summary>中转站中的文件项：Name=显示的文件名，Path=原始完整路径（拖出时还原）。</summary>
    public sealed record FileTransferItem(string Name, string Path);

    private readonly List<FileTransferItem> _fileTransferItems = new();
    /// <summary>当前中转文件列表（只保留路径引用，不复制文件）。</summary>
    public IReadOnlyList<FileTransferItem> FileTransferItems => _fileTransferItems;

    /// <summary>是否已有中转文件（驱动 FileTransfer 组件出现/消失）。</summary>
    public bool HasFileTransfer => _fileTransferItems.Count > 0;

    private string _fileTransferSummary = string.Empty;
    /// <summary>组件显示文字：单文件为文件名，多文件为「首个文件名 +N」。</summary>
    public string FileTransferSummary { get => _fileTransferSummary; private set => Set(ref _fileTransferSummary, value); }

    private string _fileTransferToolTip = string.Empty;
    /// <summary>悬停提示：列出全部中转文件完整路径。</summary>
    public string FileTransferToolTip { get => _fileTransferToolTip; private set => Set(ref _fileTransferToolTip, value); }

    /// <summary>「×」按钮提示。</summary>
    public string FileTransferRemoveTip => Localization.Get("FileTransfer_Remove");

    /// <summary>把拖入的文件加入中转站（去重、上限 20 个），并刷新组件顺序。</summary>
    public void AddFilesToTransfer(IEnumerable<string> paths)
    {
        var added = false;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var full = System.IO.Path.GetFullPath(p);
            if (_fileTransferItems.Any(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
            if (_fileTransferItems.Count >= 20) break;
            _fileTransferItems.Add(new FileTransferItem(System.IO.Path.GetFileName(full), full));
            added = true;
        }
        if (!added) return;
        RefreshFileTransfer();
        RebuildCompactItems();
    }

    /// <summary>清空中转站（组件随之消失）。</summary>
    public void ClearFileTransfer()
    {
        if (_fileTransferItems.Count == 0) return;
        _fileTransferItems.Clear();
        RefreshFileTransfer();
        RebuildCompactItems();
    }

    private void RefreshFileTransfer()
    {
        if (_fileTransferItems.Count == 0)
        {
            FileTransferSummary = string.Empty;
            FileTransferToolTip = string.Empty;
            return;
        }
        var first = _fileTransferItems[0].Name;
        FileTransferSummary = _fileTransferItems.Count == 1
            ? first
            : $"{first} +{_fileTransferItems.Count - 1}";
        FileTransferToolTip = string.Join("\n", _fileTransferItems.Select(f => f.Path));
    }

    /// <summary>替换某个插件贡献的组件；空列表会清空该插件的组件。</summary>
    public void SetPluginComponents(string pluginId, IReadOnlyList<PluginComponentSpec> components)
    {
        if (components is null) return;
        _pluginComponents.RemoveAll(x => string.Equals(x.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        foreach (var component in components)
            _pluginComponents.Add(new PluginComponentEntry(pluginId, component));
        RebuildCompactItems();
        try { UpdateVisibility(); }
        catch (Exception ex) { AppLogger.Warn($"Plugin visibility refresh failed: {ex.Message}"); }
    }

    /// <summary>移除某个插件的全部组件。</summary>
    public void RemovePluginComponents(string pluginId)
    {
        _pluginComponents.RemoveAll(x => string.Equals(x.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        RebuildCompactItems();
        UpdateVisibility();
    }

    /// <summary>按 WidgetOrder 重建灵动岛组件顺序；没有播放信息时自动去掉歌曲组件。</summary>
    private static System.Windows.Media.Brush? ParsePluginBrush(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch { return null; }
    }

    private static ImageSource? ParsePluginImage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var idx = value.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var bytes = Convert.FromBase64String(value[(idx + 7)..].Trim());
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 96;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static IslandPushButton? ParsePluginClick(PluginComponentSpec c)
        => string.IsNullOrWhiteSpace(c.ClickAction) ? null : new IslandPushButton { Action = c.ClickAction.Trim(), Value = c.ClickValue ?? string.Empty };

    /// <summary>插件组件是否在组件设置页被勾选显示（Idle/Playing 分别判断；缺省视为显示）。</summary>
    private bool PluginVisible(string pluginId)
    {
        try
        {
            var flags = _settings.Current.PluginComponents;
            if (flags is not null)
            {
                if (flags.TryGetValue(pluginId, out var f))
                    return HasMedia ? f.Playing : f.Idle;
                foreach (var kv in flags)
                    if (string.Equals(kv.Key, pluginId, StringComparison.OrdinalIgnoreCase))
                        return HasMedia ? kv.Value.Playing : kv.Value.Idle;
            }
            return true;
        }
        catch { return true; }
    }

    /// <summary>把某个插件的全部组件（按 order 排序）追加到紧凑区列表。</summary>
    private void AddPluginComponents(List<IslandComponent> items, string pluginId)
    {
        foreach (var entry in _pluginComponents
            .Where(x => string.Equals(x.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Component.Order)
            .ThenBy(x => x.Component.Id, StringComparer.OrdinalIgnoreCase))
        {
            var c = entry.Component;
            var visible = HasMedia ? c.ShowWhenPlaying : c.ShowWhenIdle;
            if (!visible) continue;
            items.Add(new IslandComponent($"Plugin:{entry.PluginId}:{c.Id}", c.Icon, c.Text, c.ToolTip, true,
                ParsePluginImage(c.Image), c.Progress, ParsePluginBrush(c.Color), ParsePluginClick(c)));
        }
    }

    private void RebuildCompactItems()
    {
        // 局部工厂：按组件 Kind 解析显示图标（用户自定义优先，否则默认字形）
        IslandComponent I(string kind) => new(kind, ComponentIcons.Resolve(kind, _settings.Current.ComponentIcons));

        // 读取顺序，并补齐缺失的已知组件（兼容旧配置里只有 Time,Weather 的情况）
        // 注意：已知组件列表必须覆盖下面所有分支用到的 key，否则该组件即使勾选也永远不会显示。
        var keys = (_settings.Current.WidgetOrder ?? "Time,Weather,Song")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var known in new[] { "Time", "Weather", "Date", "Cpu", "Ram", "Gpu", "Mic", "Cam", "Net", "Battery", "Song", "Volume", "CapsLock", "VolumeTemp", "Usage", "FileCopy", "Download", "Disk", "InputMethod", "QuickToggles", "FileTransfer" })
            if (!keys.Contains(known)) keys.Add(known);

        // 插件组件以 Plugin:{插件id} 为键参与排序；未写入顺序配置的插件追加到末尾（向后兼容）
        foreach (var pid in _pluginComponents.Select(x => x.PluginId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pkey = "Plugin:" + pid;
            if (!keys.Contains(pkey, StringComparer.OrdinalIgnoreCase)) keys.Add(pkey);
        }

        // 「使用中」合并胶囊：把勾选的 Mic/Cam/Meeting/Recording 合并为单个状态胶囊（默认关闭）
        var mergeItems = _settings.Current.UsageMergeItems ?? new List<string>();
        var mergeEnabled = _settings.Current.UsageMergeEnabled && mergeItems.Count > 0;
        var activeMerge = new List<string>();
        if (mergeEnabled)
        {
            if (mergeItems.Contains("Mic") && ShowIdleMic && !string.IsNullOrEmpty(MicText)) activeMerge.Add(Localization.Get("Comp_Mic"));
            if (mergeItems.Contains("Cam") && ShowIdleCam && !string.IsNullOrEmpty(CamText)) activeMerge.Add(Localization.Get("Comp_Cam"));

        }
        var newUsageText = mergeEnabled && activeMerge.Count > 0
            ? Localization.Get("Comp_Usage") + " · " + string.Join(" · ", activeMerge)
            : string.Empty;
        if (UsageMergeText != newUsageText) UsageMergeText = newUsageText;

        var items = new List<IslandComponent>();
        var usageInserted = false;
        var handledPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            // 合并模式：参与合并的项不再单独显示；「使用中」胶囊放在第一个激活合并项的位置
            var isMergedKey = mergeEnabled && mergeItems.Contains(key);
            if (isMergedKey)
            {
                if (!usageInserted && UsageMergeText.Length > 0)
                {
                    items.Add(I("Usage"));
                    usageInserted = true;
                }
                continue;
            }
            if (key == "Time" && ShowIdleTime) items.Add(I("Time"));
            else if (key == "Weather" && ShowIdleWeather) items.Add(I("Weather"));
            else if (key == "Date" && ShowIdleDate) items.Add(I("Date"));
            else if (key == "Cpu" && ShowIdleCpu) items.Add(I("Cpu"));
            else if (key == "Ram" && ShowIdleRam) items.Add(I("Ram"));
            else if (key == "Gpu" && ShowIdleGpu) items.Add(I("Gpu"));
            else if (key == "Mic" && ShowIdleMic && !string.IsNullOrEmpty(MicText)) items.Add(I("Mic"));
            else if (key == "Cam" && ShowIdleCam && !string.IsNullOrEmpty(CamText)) items.Add(I("Cam"));
            else if (key == "Net" && ShowIdleNet) items.Add(I("Net"));
            else if (key == "Battery" && ShowIdleBattery) items.Add(I("Battery"));
            else if (key == "Song" && HasMedia && _settings.Current.ShowMediaInfo) items.Add(I("Song"));
            else if (key == "CapsLock" && ShowIdleCapsLock && !string.IsNullOrEmpty(CapsLockText)) items.Add(I("CapsLock"));
            else if (key == "VolumeTemp" && _settings.Current.VolumeTempIndicatorEnabled && !string.IsNullOrEmpty(VolumeTempText)) items.Add(I("VolumeTemp"));
            else if (key == "FileCopy" && _settings.Current.FileCopyNotifyEnabled && !string.IsNullOrEmpty(FileCopyText)) items.Add(I("FileCopy"));
            else if (key == "Download" && _settings.Current.DownloadProgressEnabled && !string.IsNullOrEmpty(DownloadText)) items.Add(I("Download"));
            else if (key == "Disk" && ShowIdleDisk) items.Add(I("Disk"));
            else if (key == "InputMethod" && ShowIdleInputMethod) items.Add(I("InputMethod"));
            else if (key == "QuickToggles" && ShowIdleQuickToggles) items.Add(I("QuickToggles"));
            else if (key == "FileTransfer" && HasFileTransfer) items.Add(I("FileTransfer"));
            else if (key.StartsWith("Plugin:", StringComparison.OrdinalIgnoreCase))
            {
                // 插件组件：按组件设置页勾选（Idle/Playing）+ 插件输出的 show_when_* 双重控制
                var pluginId = key["Plugin:".Length..];
                handledPluginIds.Add(pluginId);
                if (!PluginVisible(pluginId)) continue;
                AddPluginComponents(items, pluginId);
            }
        }
        // 兜底：万一插件 id 未出现在顺序键中（配置异常/旧配置），仍追加显示，保证插件不丢
        foreach (var pid in _pluginComponents.Select(x => x.PluginId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (handledPluginIds.Contains(pid)) continue;
            AddPluginComponents(items, pid);
        }

        if (_compactItems.SequenceEqual(items))
            return;
        // 重建后发布紧凑区组件列表。
        CompactItems = items;
    }


    private int _statsTick;
    private int _diskTick; // 磁盘查询节流：每 5 秒查一次，减少空闲 IO 唤醒
    private float? _cpuValue;
    private long _lastNetDownBytes;
    private long _lastNetUpBytes;
    private DateTime _lastNetTime = DateTime.UtcNow;
    // 网速迷你曲线：环形缓冲最近 32 秒下行速率（KB/s）
    private const int NetCurveSamples = 32;
    private readonly double[] _netCurveSamples = new double[NetCurveSamples];
    private int _netCurvePos;
    // 性能计数器复用实例：新建实例的第一次 NextValue() 会返回 0，导致 CPU 显示错乱
    private readonly System.Diagnostics.PerformanceCounter? _cpuCounter;
    private readonly System.Diagnostics.PerformanceCounter? _ramCounter;
    // GPU 占用：读取「GPU Engine」分类下所有 3D 引擎实例的最大利用率（实例随进程启停变化，需定期刷新）
    private readonly Dictionary<string, System.Diagnostics.PerformanceCounter> _gpuCounters = new();
    private bool _gpuProbed;
    private bool _gpuAvailable;
    private System.Diagnostics.PerformanceCounterCategory? _gpuCategory;

    private System.Diagnostics.PerformanceCounter? CreateCounter(string cat, string name, string? inst)
    {
        try { return inst is null ? new System.Diagnostics.PerformanceCounter(cat, name) : new System.Diagnostics.PerformanceCounter(cat, name, inst); }
        catch { return null; }
    }

    /// <summary>刷新 GPU 计数器实例并返回当前最大 3D 引擎利用率（%）；本机无 GPU Engine 计数器或新实例预热时返回 null。</summary>
    private double? SampleGpuUsage()
    {
        try
        {
            if (!_gpuProbed)
            {
                _gpuProbed = true;
                _gpuAvailable = System.Diagnostics.PerformanceCounterCategory.Exists("GPU Engine");
                if (_gpuAvailable) _gpuCategory = new System.Diagnostics.PerformanceCounterCategory("GPU Engine");
                if (!_gpuAvailable) return null;
            }
            if (!_gpuAvailable) return null;

            var names = _gpuCategory!.GetInstanceNames()
                .Where(n => n.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

            // 清理已退出进程的引擎实例
            foreach (var k in _gpuCounters.Keys.ToList())
                if (!names.Contains(k, StringComparer.OrdinalIgnoreCase))
                {
                    try { _gpuCounters[k].Dispose(); } catch { }
                    _gpuCounters.Remove(k);
                }

            // 新建实例：首次 NextValue() 返回 0，先预热不显示
            bool created = false;
            foreach (var n in names)
            {
                if (_gpuCounters.ContainsKey(n)) continue;
                try
                {
                    var c = new System.Diagnostics.PerformanceCounter("GPU Engine", "Utilization Percentage", n);
                    _gpuCounters[n] = c;
                    created = true;
                }
                catch { }
            }
            if (created) return null;

            double max = 0;
            foreach (var c in _gpuCounters.Values)
            {
                try { max = Math.Max(max, c.NextValue()); } catch { }
            }
            return max;
        }
        catch { return null; }
    }

    /// <summary>更新 CPU/内存/网络/电池/日期 等系统状态文本（每秒一次，UI 线程）。</summary>
    private void UpdateSystemStats()
    {
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            var hasBattery = ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
            var battery = ps.BatteryLifePercent * 100f;
            if (hasBattery)
            {
                // 电池预估剩余时间（秒 → 时:分；充电中/未知时仅显示百分比）
                var text = $"{battery:0}%";
                var remainSecs = ps.BatteryLifeRemaining;
                if (remainSecs > 0 && battery > 0)
                {
                    var ts = TimeSpan.FromSeconds(remainSecs);
                    var t = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}" : $"{Math.Max(1, ts.Minutes)}:{ts.Seconds:00}";
                    text += $" \u00b7 {t}";
                }
                BatteryText = text;
            }
            else BatteryText = string.Empty;

            // 低电量提醒（每个充电周期提醒一次）
            if (hasBattery && _settings.Current.LowBatteryThreshold > 0)
            {
                if (!_lowBatteryNotified && battery <= _settings.Current.LowBatteryThreshold)
                {
                    _lowBatteryNotified = true;
                    LowBatteryRequested?.Invoke((int)battery);
                }
                if (battery > _settings.Current.LowBatteryThreshold + 5) _lowBatteryNotified = false;
            }

            var acOnline = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

            // 低电量常驻指示（1.2.4）：电量 ≤ 阈值+2 且未接电源时，右上角胶囊常驻显示电量百分比，
            // 随电量实时刷新；接上电源或电量回升到阈值+5 以上才消失。与一次性弹出提醒解耦，不重复弹通知。
            if (_settings.Current.LowBatteryPersistentEnabled && hasBattery && _settings.Current.LowBatteryThreshold > 0)
            {
                var low = !acOnline && battery <= _settings.Current.LowBatteryThreshold + 2;
                var recovered = acOnline || battery > _settings.Current.LowBatteryThreshold + 5;
                if (low)
                {
                    ShowLowBatteryBadge = true;
                    LowBatteryBadgeText = $"{(int)Math.Round(battery)}%";
                    var red = battery <= 10;
                    LowBatteryBadgeBrush = red ? _lowBatteryRedBrush : _lowBatteryOrangeBrush;
                    LowBatteryBadgeBackground = red ? _lowBatteryRedBgBrush : _lowBatteryOrangeBgBrush;
                }
                else if (recovered) ShowLowBatteryBadge = false;
            }
            else ShowLowBatteryBadge = false;

            // 开始充电提醒（电源接入瞬间触发一次，iOS 风格上岛卡片；拔出电源后复位可再次触发）
            if (hasBattery && _settings.Current.ChargedNotifyEnabled)
            {
                if (!_chargingNotified && acOnline)
                {
                    _chargingNotified = true;
                    ChargingStartedRequested?.Invoke((int)Math.Round(battery));
                }
                if (!acOnline) _chargingNotified = false;
            }

            // 充电完成提醒（每个充电周期提醒一次；连接电源且电量达到阈值时）
            if (hasBattery && _settings.Current.ChargedNotifyEnabled && _settings.Current.ChargedThreshold > 0)
            {
                if (!_chargedNotified && acOnline && battery >= _settings.Current.ChargedThreshold)
                {
                    _chargedNotified = true;
                    ChargedRequested?.Invoke((int)Math.Round(battery));
                }
                if (!acOnline || battery < _settings.Current.ChargedThreshold - 5) _chargedNotified = false;
            }

            // 磁盘剩余空间（系统盘；仅组件开启或提醒开启时查询，查询开销小）
            if ((ShowIdleDisk || _settings.Current.DiskAlertEnabled) && ++_diskTick % 5 == 1)
            {
                try
                {
                    var root = Path.GetPathRoot(Environment.SystemDirectory);
                    if (root is not null)
                    {
                        var di = new DriveInfo(root);
                        if (di.IsReady)
                        {
                            var freeGb = di.AvailableFreeSpace / (1024d * 1024d * 1024d);
                            var totalGb = di.TotalSize / (1024d * 1024d * 1024d);
                            DiskText = $"{root.TrimEnd('\\')} {freeGb:0}GB / {totalGb:0}GB";
                            // 剩余空间低于阈值提醒（恢复后复位，每个周期只提醒一次）
                            if (_settings.Current.DiskAlertEnabled && _settings.Current.DiskAlertThresholdGB > 0)
                            {
                                if (!_diskAlertNotified && freeGb < _settings.Current.DiskAlertThresholdGB)
                                {
                                    _diskAlertNotified = true;
                                    DiskLowRequested?.Invoke((int)Math.Floor(freeGb));
                                }
                                if (freeGb > _settings.Current.DiskAlertThresholdGB + 5) _diskAlertNotified = false;
                            }
                        }
                        else DiskText = string.Empty;
                    }
                    else DiskText = string.Empty;
                }
                catch { DiskText = string.Empty; }
            }

            // CPU / 内存（每 2 秒；计数器实例复用，避免首次采样为 0）
            if (++_statsTick % 2 == 0)
            {
                if (ShowIdleCpu) // 后台减负：CPU 组件未勾选时不采样
                {
                    try
                    {
                        if (_cpuCounter is not null)
                        {
                            _cpuValue = (float?)_cpuCounter.NextValue();
                            CpuText = _cpuValue.HasValue ? $"{_cpuValue.Value:0}%" : "--";
                        }
                    }
                    catch { CpuText = "--"; }
                }
                // GPU：与 CPU 同节奏采样（新实例先预热，避免首采显示 0%）
                if (ShowIdleGpu)
                {
                    var gpu = SampleGpuUsage();
                    GpuText = gpu.HasValue ? $"{gpu.Value:0}%" : "--";
                }
                // 麦克风/摄像头占用（隐私注册表，Start > Stop 表示占用中；仅勾选时轮询）
                if (ShowIdleMic || ShowIdleCam)
                {
                    var (mic, cam) = PrivacyDeviceMonitor.GetUsage();
                    if (ShowIdleMic) MicText = mic ? Localization.Get("Comp_Mic") : string.Empty;
                    if (ShowIdleCam) CamText = cam ? Localization.Get("Comp_Cam") : string.Empty;
                    RebuildCompactItems();
                }
                if (ShowIdleRam) // 后台减负：内存组件未勾选时不采样
                {
                    try
                    {
                        if (_ramCounter is not null)
                        {
                            var ram = _ramCounter.NextValue();
                            RamText = $"{ram:0}%";
                        }
                    }
                    catch { RamText = "--"; }
                }
            }

            // 网络速度（每秒）：下行文字 + 上行文字 + 迷你曲线（仅网络组件勾选时采样）
            if (ShowIdleNet)
            {
                try
                {
                    var iface = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && i.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback && i.Speed > 0);
                if (iface is not null)
                {
                    var stats = iface.GetIPv4Statistics();
                    var now = DateTime.UtcNow;
                    var secs = Math.Max(0.1, (now - _lastNetTime).TotalSeconds);
                    var downKbs = (stats.BytesReceived - _lastNetDownBytes) / 1024.0 / secs;
                    var upKbs = (stats.BytesSent - _lastNetUpBytes) / 1024.0 / secs;
                    _lastNetDownBytes = stats.BytesReceived;
                    _lastNetUpBytes = stats.BytesSent;
                    _lastNetTime = now;
                    NetText = FormatKbs(downKbs);
                    NetTextUp = FormatKbs(upKbs);
                    PushNetSample(downKbs);
                }
                    else { NetText = string.Empty; NetTextUp = string.Empty; }
                }
                catch { NetText = string.Empty; NetTextUp = string.Empty; }
            }

        }
        catch { /* 忽略性能计数器/电量异常 */ }
    }

    private static string FormatKbs(double kbs) =>
        kbs >= 1024 ? $"{kbs / 1024:0.0} MB/s" : $"{kbs:0} KB/s";

    /// <summary>把一次下行采样推入环形缓冲，并按需重建曲线点串（仅网络组件勾选时才有意义）。</summary>
    private void PushNetSample(double downKbs)
    {
        _netCurveSamples[_netCurvePos] = downKbs;
        _netCurvePos = (_netCurvePos + 1) % NetCurveSamples;
        if (_settings.Current.NetCurveEnabled && ShowIdleNet)
            NetCurvePoints = BuildNetCurvePoints();
    }

    /// <summary>生成 32 秒下行速率曲线点串（自动按峰值缩放）。</summary>
    private string BuildNetCurvePoints()
    {
        const double w = 64, h = 14;
        double max = 1.0;
        foreach (var v in _netCurveSamples) if (v > max) max = v;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < NetCurveSamples; i++)
        {
            var idx = (_netCurvePos + i) % NetCurveSamples;
            var v = _netCurveSamples[idx];
            var x = i * (w / (NetCurveSamples - 1));
            var y = h - (Math.Min(v, max) / max) * (h - 1);
            sb.Append(x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            if (i < NetCurveSamples - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    // 组件摆放顺序（WidgetOrder 中的下标决定左右列）
    public double WidgetTimeFontSize => HasMedia ? 14 : 16; // 空闲时钟字号适中，启动不突兀

    // 系统状态/日期组件文本
    private string _dateText = string.Empty;
    public string DateText { get => _dateText; private set => Set(ref _dateText, value); }
    private string _cpuText = string.Empty;
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    private string _ramText = string.Empty;
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    private string _gpuText = string.Empty;
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    private string _micText = string.Empty;
    /// <summary>麦克风占用指示文本（占用中显示「麦克风」，否则为空，组件随之出现/消失）。</summary>
    public string MicText { get => _micText; private set => Set(ref _micText, value); }
    private string _camText = string.Empty;
    /// <summary>摄像头占用指示文本（占用中显示「摄像头」，否则为空）。</summary>
    public string CamText { get => _camText; private set => Set(ref _camText, value); }
    private string _netText = string.Empty;
    public string NetText { get => _netText; private set => Set(ref _netText, value); }
    private string _netTextUp = string.Empty;
    public string NetTextUp { get => _netTextUp; private set => Set(ref _netTextUp, value); }
    private string _netCurvePoints = string.Empty;
    public string NetCurvePoints { get => _netCurvePoints; private set => Set(ref _netCurvePoints, value); }
    private string _batteryText = string.Empty;
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }

    // 低电量常驻胶囊配色（1.2.4，iOS 风格：≤10% 红 / 其余橙；背景为同色低透明度）
    private static readonly SolidColorBrush _lowBatteryRedBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush _lowBatteryOrangeBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A));
    private static readonly SolidColorBrush _lowBatteryRedBgBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush _lowBatteryOrangeBgBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0x9F, 0x0A));
    private static SolidColorBrush FrozenBrush(System.Windows.Media.Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private bool _showLowBatteryBadge;
    /// <summary>低电量常驻指示是否显示（电量低于阈值且未接电源时为 true）。</summary>
    public bool ShowLowBatteryBadge { get => _showLowBatteryBadge; private set => Set(ref _showLowBatteryBadge, value); }
    private string _lowBatteryBadgeText = string.Empty;
    /// <summary>低电量胶囊电量文本（如「23%」）。</summary>
    public string LowBatteryBadgeText { get => _lowBatteryBadgeText; private set => Set(ref _lowBatteryBadgeText, value); }
    private SolidColorBrush _lowBatteryBadgeBrush = _lowBatteryRedBrush;
    /// <summary>低电量胶囊前景/图标色（≤10% 红，其余橙，随电量实时变化）。</summary>
    public SolidColorBrush LowBatteryBadgeBrush { get => _lowBatteryBadgeBrush; private set => Set(ref _lowBatteryBadgeBrush, value); }
    private SolidColorBrush _lowBatteryBadgeBackground = _lowBatteryRedBgBrush;
    /// <summary>低电量胶囊背景（同色低透明度，随电量实时变化）。</summary>
    public SolidColorBrush LowBatteryBadgeBackground { get => _lowBatteryBadgeBackground; private set => Set(ref _lowBatteryBadgeBackground, value); }
    private string _inputMethodText = string.Empty;
    /// <summary>输入法状态文本（如「中 · 微软拼音」）。</summary>
    public string InputMethodText { get => _inputMethodText; private set => Set(ref _inputMethodText, value); }
    /// <summary>输入法组件提示（点击切换中/英）。</summary>
    public string InputMethodHint => Localization.Get("Comp_InputMethodHint");
    private string _quickWifiText = string.Empty;
    /// <summary>快捷开关：WiFi 状态文本（如「WiFi 开」）。</summary>
    public string QuickWifiText { get => _quickWifiText; private set => Set(ref _quickWifiText, value); }
    private string _quickBtText = string.Empty;
    /// <summary>快捷开关：蓝牙状态文本。</summary>
    public string QuickBtText { get => _quickBtText; private set => Set(ref _quickBtText, value); }
    private string _quickNightText = string.Empty;
    /// <summary>快捷开关：夜间模式状态文本。</summary>
    public string QuickNightText { get => _quickNightText; private set => Set(ref _quickNightText, value); }
    private string _quickMuteText = string.Empty;
    /// <summary>快捷开关：静音状态文本。</summary>
    public string QuickMuteText { get => _quickMuteText; private set => Set(ref _quickMuteText, value); }
    /// <summary>快捷开关组件提示（点击各开关即时切换）。</summary>
    public string QuickTogglesHint => Localization.Get("Comp_QuickTogglesHint");
    private string _diskText = string.Empty;
    /// <summary>系统盘剩余空间文本（如「C: 385GB / 510GB」；无可用盘符时为空）。</summary>
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }

    /// <summary>切歌时触发（参数：歌名、歌手），用于 Now Playing 横幅。</summary>
    public event Action<string, string>? NowPlayingRequested;
    /// <summary>低电量触发（参数：电量百分比）。</summary>
    public event Action<int>? LowBatteryRequested;
    /// <summary>充电完成触发（参数：电量百分比）。</summary>
    public event Action<int>? ChargedRequested;
    /// <summary>开始充电触发（参数：电量百分比）。</summary>
    public event Action<int>? ChargingStartedRequested;
    private bool _lowBatteryNotified;
    private bool _chargedNotified;
    private bool _chargingNotified;
    /// <summary>磁盘剩余不足触发（参数：剩余 GB）。</summary>
    public event Action<int>? DiskLowRequested;
    private bool _diskAlertNotified;
    private string _holidayText = string.Empty;
    public string HolidayText { get => _holidayText; private set => Set(ref _holidayText, value); }
    private string _meetingText = string.Empty;
    /// <summary>会议中状态文本（如「会议中 · Microsoft Teams」；非会议时为空，组件随之消失）。</summary>
    public string MeetingText { get => _meetingText; private set => Set(ref _meetingText, value); }

    // ── 农历 / 节气（纯本地计算，ChineseLunisolarCalendar + 节气近似公式，不联网）──
    private static readonly string[] LunarMonths =
        { "正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月" };
    private static readonly string[] LunarDays =
        { "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
          "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
          "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十" };
    private static readonly string[] SolarTermNames =
        { "小寒", "大寒", "立春", "雨水", "惊蛰", "春分", "清明", "谷雨", "立夏", "小满", "芒种", "夏至",
          "小暑", "大暑", "立秋", "处暑", "白露", "秋分", "寒露", "霜降", "立冬", "小雪", "大雪", "冬至" };
    // 24 节气日期近似公式常数（平气法，1900-2100 误差 ≤1 天）
    private static readonly double[] SolarTermBase =
        { 0, 21208, 42467, 63836, 85337, 107014, 128867, 150921, 173149, 195551, 218072, 240693,
          263343, 285989, 308563, 331033, 353350, 375494, 397447, 419210, 440795, 462224, 483532, 504758 };

    /// <summary>日期组件文本：公历 + 农历月日（+ 当日节气）。</summary>
    private string FormatDateText(DateTime now)
    {
        var baseText = now.ToString("M月d日 ddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        if (!_settings.Current.ShowLunarOnDate) return baseText;
        try
        {
            var cal = new System.Globalization.ChineseLunisolarCalendar();
            var year = cal.GetYear(now);
            var monthVal = cal.GetMonth(now);
            var leap = cal.IsLeapMonth(year, monthVal);
            var monthNum = leap ? monthVal - 1 : monthVal;
            var lunar = (leap ? "闰" : "") + LunarMonths[Math.Clamp(monthNum, 1, 12) - 1]
                + LunarDays[Math.Clamp(cal.GetDayOfMonth(now), 1, 30) - 1];
            var suffix = " 农历" + lunar;
            var term = SolarTermOf(now);
            if (term.Length > 0) suffix += " · " + term;
            return baseText + suffix;
        }
        catch { return baseText; } // 计算失败优雅降级为纯公历
    }

    /// <summary>当日节气名（不是节气返回空字符串）。</summary>
    private static string SolarTermOf(DateTime now)
    {
        try
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var baseMs = new DateTime(1900, 1, 6, 2, 5, 0, DateTimeKind.Utc).Subtract(epoch).TotalMilliseconds;
            for (int i = 0; i < SolarTermBase.Length; i++)
            {
                var ms = 31556925974.7 * (now.Year - 1900) + SolarTermBase[i] * 60000 + baseMs;
                var d = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime;
                if (d.Month == now.Month && d.Day == now.Day) return SolarTermNames[i];
            }
            return string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>内置节假日表（年份+公历/农历日期整理，纯本地不联网；如需可自行补充条目）。</summary>
    private static readonly (string Name, int Year, int Month, int Day)[] HolidayTable =
    {
        ("元旦", 2026, 1, 1), ("春节", 2026, 2, 17), ("清明", 2026, 4, 5), ("劳动节", 2026, 5, 1),
        ("端午", 2026, 6, 19), ("中秋", 2026, 9, 25), ("国庆", 2026, 10, 1),
        ("元旦", 2027, 1, 1), ("春节", 2027, 2, 6), ("清明", 2027, 4, 5), ("劳动节", 2027, 5, 1),
        ("端午", 2027, 6, 9), ("中秋", 2027, 9, 15), ("国庆", 2027, 10, 1),
    };

    /// <summary>下一个节假日名称与剩余天数（今天为 0；找不到返回空）。</summary>
    private static (string Name, int Days) NextHolidayInfo()
    {
        var today = DateTime.Today;
        var best = ("", -1);
        foreach (var h in HolidayTable)
        {
            DateTime d;
            try { d = new DateTime(h.Year, h.Month, h.Day); } catch { continue; }
            if (d < today) continue;
            var days = (int)(d - today).TotalDays;
            if (best.Item2 < 0 || days < best.Item2) best = (h.Name, days);
        }
        return best;
    }

    // ── 上岛推送（第三方软件推送到灵动岛）──────────────────
    public IReadOnlyList<IslandPush> ActivePushes => _pushes;

    public IslandPush? ActivePush
    {
        get => _activePush;
        private set
        {
            if (!Set(ref _activePush, value)) return;
            OnPropertyChanged(nameof(HasActivePush));
            OnPropertyChanged(nameof(ActivePushIcon));
        OnPropertyChanged(nameof(ActivePushBadgeGlyph));
        OnPropertyChanged(nameof(ActivePushBadgeFont));
        OnPropertyChanged(nameof(ActivePushBadgeBrush));
        OnPropertyChanged(nameof(ActivePushHasRedDot));
            OnPropertyChanged(nameof(ActivePushTitle));
            OnPropertyChanged(nameof(ActivePushSubtitle));
            OnPropertyChanged(nameof(ActivePushHasSubtitle));
            OnPropertyChanged(nameof(ActivePushBody));
            OnPropertyChanged(nameof(ActivePushHasBody));
            OnPropertyChanged(nameof(ActivePushSummary));
            OnPropertyChanged(nameof(ActivePushHasSummary));
            OnPropertyChanged(nameof(ActivePushIsMessenger));
            OnPropertyChanged(nameof(ActivePushMessengerSource));
            OnPropertyChanged(nameof(ActivePushIsWeChat));
            OnPropertyChanged(nameof(ActivePushIsQq));
            OnPropertyChanged(nameof(ActivePushNeedsDetails));
            OnPropertyChanged(nameof(ActivePushHasProgress));
            OnPropertyChanged(nameof(ActivePushProgress));
            OnPropertyChanged(nameof(ActivePushHasImage));
            OnPropertyChanged(nameof(ActivePushImageSource));
            OnPropertyChanged(nameof(ActivePushHasButtons));
            OnPropertyChanged(nameof(ActivePushButtons));
            OnPropertyChanged(nameof(ActivePushHasClick));
            OnPropertyChanged(nameof(ActivePushAccent));
            OnPropertyChanged(nameof(ActivePushTheme));
            OnPropertyChanged(nameof(ActivePushRepeatCount));
            OnPropertyChanged(nameof(ActivePushHasGroupCount));
            OnPropertyChanged(nameof(ActivePushGroupText));
            OnPropertyChanged(nameof(HasPushInput));
            OnPropertyChanged(nameof(PushInputPlaceholder));
            OnPropertyChanged(nameof(PushInputSubmitLabel));
            OnPropertyChanged(nameof(PushInputValue));
            OnPropertyChanged(nameof(PushInputHasText));
            UpdateVisibility(); // 上岛卡片显示/消失影响灵动岛可见性
        }
    }

    public bool HasActivePush => ActivePush is not null;
    public string ActivePushIcon => string.IsNullOrEmpty(ActivePush?.Icon) ? "\uE7F4" : ActivePush!.Icon;
    public string ActivePushBadgeGlyph => ActivePush?.Type switch
    {
        "wechat" => "\uE8BD",
        "qq" => "\U0001F427",
        "bluetooth" => "\uE702",
        _ => ActivePushIcon,
    };
    public string ActivePushBadgeFont => ActivePush?.Type switch
    {
        "qq" => "Segoe UI Emoji",
        _ => "Segoe MDL2 Assets",
    };
    public Brush ActivePushBadgeBrush => ActivePush?.Type switch
    {
        "wechat" => WeChatBadgeBrush,
        "qq" => QqBadgeBrush,
        "bluetooth" => BluetoothBadgeBrush,
        _ => DefaultBadgeBrush,
    };
    public bool ActivePushHasRedDot => ActivePushIsMessenger;
    public string ActivePushTitle => ActivePush?.Title ?? string.Empty;
    public string ActivePushSubtitle => ActivePush?.Subtitle ?? string.Empty;
    public bool ActivePushHasSubtitle => !string.IsNullOrEmpty(ActivePush?.Subtitle);
    public string ActivePushBody => ActivePush?.Body ?? string.Empty;
    public bool ActivePushHasBody => !string.IsNullOrEmpty(ActivePush?.Body);
    public string ActivePushSummary => !string.IsNullOrEmpty(ActivePush?.Subtitle) ? ActivePush!.Subtitle! : (ActivePush?.Body ?? string.Empty);
    public bool ActivePushHasSummary => !string.IsNullOrEmpty(ActivePushSummary);

    /// <summary>同应用通知在短时间内合并后的重复次数（至少 1）。</summary>
    public int ActivePushRepeatCount => Math.Max(1, ActivePush?.RepeatCount ?? 1);
    public bool ActivePushHasGroupCount => ActivePushRepeatCount > 1;
    public string ActivePushGroupText => ActivePushHasGroupCount ? "×" + ActivePushRepeatCount : string.Empty;

    /// <summary>
    /// 当前通知是否采用微信/QQ 样式（应用图标 + 查看/关闭）。
    /// 未读监控产生的通知带 MessengerUnread；第三方 API 直接以 type=wechat/qq 推送时也走同一套样式。
    /// </summary>
    public bool ActivePushIsMessenger => ActivePushMessengerSource is not null;

    /// <summary>当前通知对应的具体客户端；未读监控对象优先，其次看推送 type。</summary>
    public MessengerSource? ActivePushMessengerSource =>
        ActivePush?.MessengerUnread?.Source ??
        (ActivePush?.Type ?? "").Trim().ToLowerInvariant() switch
        {
            "wechat" or "weixin" => MessengerSource.WeChat,
            "qq" => MessengerSource.QQ,
            _ => (MessengerSource?)null,
        };

    public bool ActivePushIsWeChat => ActivePushMessengerSource == MessengerSource.WeChat;
    public bool ActivePushIsQq => ActivePushMessengerSource == MessengerSource.QQ;

    /// <summary>
    /// 当前通知是否需要额外详情区：只有图片/进度/按钮/输入框这类富内容才向下展开。
    /// 纯文字通知本身就是 334×44 的双行通知条（标题 + 正文），不再额外挂一张详情卡。
    /// </summary>
    public bool ActivePushNeedsDetails => ActivePush is not null &&
        (ActivePushHasImage || ActivePushHasProgress || ActivePushHasButtons || HasPushInput);
    public bool ActivePushHasProgress => ActivePush?.EffectiveProgress is not null;
    public double ActivePushProgress => Math.Clamp(ActivePush?.EffectiveProgress ?? 0, 0, 1);

    // ── 上岛输入框（v4）：第三方推送配置 input 时，卡片内显示输入框 + 提交按钮 ──
    public bool HasPushInput => ActivePush?.Input is not null;
    public string PushInputPlaceholder => string.IsNullOrEmpty(ActivePush?.Input?.Placeholder)
        ? Localization.Get("Push_InputPlaceholder") : ActivePush!.Input!.Placeholder;
    public string PushInputSubmitLabel => string.IsNullOrEmpty(ActivePush?.Input?.SubmitLabel)
        ? Localization.Get("Push_InputSubmit") : ActivePush!.Input!.SubmitLabel;
    public string PushInputValue
    {
        get => _pushInputValue;
        set { if (Set(ref _pushInputValue, value)) OnPropertyChanged(nameof(PushInputHasText)); }
    }
    public bool PushInputHasText => !string.IsNullOrEmpty(PushInputValue);

    /// <summary>提交上岛输入框内容：按推送方配置的 action 执行（默认 notify 回传），提交后清空输入框。</summary>
    public void SubmitPushInput()
    {
        if (ActivePush?.Input is not IslandPushInput input) return;
        var value = PushInputValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(input.Value)) return; // 空输入且无默认值时忽略
        var b = new IslandPushButton
        {
            Label = string.IsNullOrEmpty(input.SubmitLabel) ? Localization.Get("Push_InputSubmit") : input.SubmitLabel,
            Action = string.IsNullOrEmpty(input.Action) ? "notify" : input.Action,
            Value = string.IsNullOrWhiteSpace(value) ? input.Value : value,
        };
        ExecutePushAction(b);
        PushInputValue = string.Empty;
    }

    /// <summary>上岛推送图片（v3）：data URI 或 http(s) 链接。</summary>
    public bool ActivePushHasImage => !string.IsNullOrEmpty(ActivePush?.Image);

    /// <summary>上岛推送图片源：data URI 解码为本地位图；http(s) 直接加载。</summary>
    public ImageSource? ActivePushImageSource
    {
        get
        {
        var img = ActivePush?.Image;
        if (string.IsNullOrWhiteSpace(img))
        {
            _pushImageCacheKey = string.Empty;
            _pushImageCache = null;
            return null;
        }
        if (string.Equals(img, _pushImageCacheKey, StringComparison.Ordinal)) return _pushImageCache;

        ImageSource? result = null;
        if (img.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            var idx = img.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                try
                {
                    var b64 = img.Substring(idx + 7).Trim();
                    var bytes = Convert.FromBase64String(b64);
                    using var ms = new MemoryStream(bytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 512;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    result = bmp;
                }
                catch { result = null; }
            }
        }
        else if (img.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 img.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnDemand;
                bmp.DecodePixelWidth = 512;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(img, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                result = bmp;
            }
            catch { result = null; }
        }

        _pushImageCacheKey = img;
        _pushImageCache = result;
        return result;
    }
    }
    public bool ActivePushHasButtons => ActivePush?.Buttons is { Count: > 0 };
    public IReadOnlyList<IslandPushButton> ActivePushButtons
        => (IReadOnlyList<IslandPushButton>)(ActivePush?.Buttons ?? new List<IslandPushButton>());
    /// <summary>整卡点击回跳（click）是否已配置。</summary>
    public bool ActivePushHasClick => ActivePush?.Click is not null;
    /// <summary>强调色（#RRGGBB / #AARRGGBB），未配置时为空字符串，由 UI 用类型默认色。</summary>
    public string ActivePushAccent => ActivePush?.Accent ?? string.Empty;

    /// <summary>推送卡片主题：dark / light / auto（auto 跟随应用明暗主题）。</summary>
    public string ActivePushTheme => ActivePush?.Theme ?? string.Empty;

    /// <summary>估算文本宽度：中文/全角按 cjkPx，ASCII 按 asciiPx。</summary>
    private static double MeasureText(string s, double cjkPx, double asciiPx)
    {
        double w = 0;
        foreach (var ch in s) w += ch > 0x2E7F ? cjkPx : asciiPx;
        return w;
    }

    /// <summary>估算多行文本的最宽单行宽度（换行符按行分离，取最大值），用于推送卡片宽度自适应。</summary>
    private static double MeasureTextML(string s, double cjkPx, double asciiPx)
    {
        double max = 0;
        foreach (var line in s.Split('\n'))
        {
            double w = 0;
            foreach (var ch in line) w += ch > 0x2E7F ? cjkPx : asciiPx;
            if (w > max) max = w;
        }
        return max;
    }

    /// <summary>天气图标（按 WMO weather_code 选 emoji）。</summary>
    private static string WeatherIcon(int code) => code switch
    {
        0 => "\u2600\uFE0F",          // ??
        1 => "\uD83C\uDF24\uFE0F",   // ???
        2 => "\u26C5",                  // ?
        3 => "\u2601\uFE0F",          // ??
        45 or 48 => "\uD83C\uDF2B\uFE0F", // ???
        >= 51 and <= 57 => "\uD83C\uDF26\uFE0F", // ???
        >= 61 and <= 67 => "\uD83C\uDF27\uFE0F", // ???
        >= 71 and <= 77 => "\uD83C\uDF28\uFE0F", // ???
        >= 80 and <= 82 => "\uD83C\uDF27\uFE0F", // ???
        85 or 86 => "\uD83C\uDF28\uFE0F",        // ???
        >= 95 => "\u26C8\uFE0F",      // ??
        _ => "\uD83C\uDF21\uFE0F",   // ???
    };

    /// <summary>天气描述（本地化）。</summary>
    private static string WeatherDesc(int code) => code switch
    {
        0 => Localization.Get("Weather_Desc0"),
        1 or 2 => Localization.Get("Weather_Desc12"),
        3 => Localization.Get("Weather_Desc3"),
        45 or 48 => Localization.Get("Weather_Desc45"),
        >= 51 and <= 57 => Localization.Get("Weather_Desc51"),
        >= 61 and <= 67 => Localization.Get("Weather_Desc61"),
        >= 71 and <= 77 => Localization.Get("Weather_Desc71"),
        >= 80 and <= 82 => Localization.Get("Weather_Desc80"),
        85 or 86 => Localization.Get("Weather_Desc85"),
        >= 95 => Localization.Get("Weather_Desc95"),
        _ => Localization.Get("Weather_Desc12"),
    };

    /// <summary>紧凑模式天气文案：图标 + 温度 + 描述 + 今日高/低温。</summary>
    private static string FormatWeatherCompact(WeatherInfo w)
    {
        var range = w.Low < w.High ? $" {w.Low:0}/{w.High:0}°" : string.Empty;
        return $"{WeatherIcon(w.Code)} {w.Temperature:0}° {WeatherDesc(w.Code)}{range}";
    }

    /// <summary>悬浮提示的完整天气信息（体感 / 湿度 / 风速 / 降水 / 更新时间）。</summary>
    private string FormatWeatherDetail(WeatherInfo w)
    {
        var feels = $"{Localization.Get("Weather_Feels")} {w.FeelsLike:0}°";
        var hum = $"{Localization.Get("Weather_Humidity")} {w.Humidity:0}%";
        var wind = $"{Localization.Get("Weather_Wind")} {w.WindSpeed:0}km/h";
        var precip = $"{Localization.Get("Weather_Precip")} {w.Precipitation:0}mm";
        var updated = $"{Localization.Get("Weather_Updated")} {w.Updated}";
        return $"{WeatherDesc(w.Code)} · {string.Join(" · ", feels, hum, wind, precip, updated)}";
    }

    /// <summary>估算紧凑宽度：按激活组件内容（时间/日期/天气/系统状态/歌曲）逐项累加，稳定可靠（不依赖 UI 布局时机）。</summary>
    public double EstimatedCompactWidth
    {
        get
        {
            double w = 4; // 左内边距
            foreach (var item in CompactItems)
            {
                switch (item.Kind)
                {
                    case "Time": w += 48; break;
                    case "Weather": w += Math.Min(MeasureText(WeatherText, 11.5, 6.5) + 8, 140); break;
                    case "Date": w += Math.Min(MeasureText(DateText, 12.5, 7) + 8, 100); break;
                    case "Cpu": w += MeasureText(CpuText, 10.5, 6) + 16; break;
                    case "Ram": w += MeasureText(RamText, 10.5, 6) + 16; break;
                    case "Gpu": w += MeasureText(GpuText, 10.5, 6) + 16; break;
                    case "Mic": w += MeasureText(MicText, 10.5, 6) + 16; break;
                    case "Cam": w += MeasureText(CamText, 10.5, 6) + 16; break;
                    case "Net": w += MeasureText(NetText, 10.5, 6) + 16; break;
                    case "Battery": w += MeasureText(BatteryText, 10.5, 6) + 16; break;
                    case "VolumeTemp": w += 10 + 6 + 58 + 6 + MeasureText(VolumeTempText, 10.5, 6) + 24; break;
                    case "Usage": w += Math.Min(MeasureText(UsageMergeText, 10.5, 6) + 16, 200); break;
                    case "FileCopy": w += Math.Min(MeasureText(FileCopyText, 10.5, 6) + 16, 220); break;
                    case "Download": w += Math.Min(MeasureText(DownloadText, 10.5, 6) + 16, 220); break;
                    case "Song":
                        w += 40 + 6
                            + Math.Min(MeasureText(Title, 12.5, 7), 140)
                            + 6 + Math.Min(MeasureText(Artist, 10.5, 6), 100);
                        if (HasLyrics) w += 8 + Math.Min(MeasureText(CurrentLyricText, 11.5, 6.5), 300);
                        break;
                }
                w += 8; // 组件间右边距（模板 Margin 0,0,8,0 左右），这里按单边即可
            }
            if (HasMedia) w += 120; // 播放/暂停 + 下一首 按钮
            if (HasActivePush) w += PushCompactWidth; // 有上岛推送时叠加推送卡宽度，保证整体估宽足够
            var maxW = HasActivePush ? Math.Max(800, System.Windows.SystemParameters.WorkArea.Width - 48) : 800;
            return Math.Clamp(w + 4, 260, maxW);
        }
    }

    /// <summary>估算紧凑高度：按内容实际高度计算（不再取手动设置值，避免上下留白过大）。</summary>
    public double EstimatedCompactHeight
    {
        get
        {
            double contentH = 40; // 单行内容高（时间/日期/上岛单行/歌曲封面）
            if (HasActivePush) contentH = Math.Max(contentH, _settings.Current.SingleLineMode ? 40 : PushCompactHeight);
            if (HasMedia && _settings.Current.ShowMediaInfo && !_settings.Current.SingleLineMode) contentH = Math.Max(contentH, 68);
            return Math.Clamp(contentH + 12, 48, 224); // 内容高 + 上下内边距(6+6)；推送正文可多行，上限放宽到 224
        }
    }

    /// <summary>估算展开宽度：至少 420，有上岛推送时按展开卡完整内容宽度（标题/副标题/正文/按钮）自适应。</summary>
    public double EstimatedExpandedWidth
    {
        get
        {
            var w = Math.Max(600, _settings.Current.ExpandedWidth);
            if (HasActivePush) w = Math.Max(w, PushExpandedCardWidth);
            return Math.Clamp(w, 600, 820);
        }
    }

    /// <summary>估算展开高度：按上岛卡片 + 歌曲各区块累加。</summary>
    public double EstimatedExpandedHeight
    {
        get
        {
            double h = 24;
            if (HasActivePush)
            {
                h += 52; // 334×44 通知条 + 间距
                if (ActivePushNeedsDetails) h += 34;
                if (ActivePushHasButtons) h += 40;
                if (HasPushInput) h += 42; // 展开状输入框行
                if (ActivePushHasProgress) h += 12;
            }
            if (HasMedia)
            {
                if (_settings.Current.ExpandedShowArtTitle) h += 90;
                if (_settings.Current.ExpandedShowProgress) h += 40;
                if (_settings.Current.ExpandedShowControls) h += 42;
                if (_settings.Current.ExpandedShowLyrics) h += 190;
            }
            return Math.Clamp(h, 600, 760);
        }
    }

    /// <summary>上岛推送时的紧凑宽度：单行显示（图标 + 标题 + 单行摘要），摘要过长省略，宽度紧凑不大幅撑宽灵动岛。</summary>
    public double PushCompactWidth
    {
        get
        {
            var baseW = Math.Max(_settings.Current.CompactWidth, 240);
            if (ActivePush is null) return baseW;
            double need = 38; // 图标 30 + 间距 8
            need += Math.Min(MeasureText(ActivePush.Title ?? string.Empty, 13, 7), 240); // 标题上限 240，与 XAML MaxWidth 一致
            if (ActivePushHasSummary)
                need += 8 + Math.Min(MeasureText(ActivePushSummary, 11.5, 6.2), 200); // 摘要单行上限 200，超出省略（与 XAML MaxWidth 一致）
            return Math.Clamp(need + 48, 240, 520); // +48：左右内边距(12+12) + 余量
        }
    }

    /// <summary>上岛推送时的紧凑高度：固定单行高（图标 30 + 卡片上下内边距 7+7 ≈ 44，留 2px 余量），自动模式下显示紧凑。</summary>
    public double PushCompactHeight
    {
        get
        {
            return 46;
        }
    }

    /// <summary>
    /// <summary>
    /// 上岛推送展开卡片的完整内容宽度：图标+标题 / 副标题 / 正文 / 按钮 / 输入框 中最宽者（上限 560），
    /// 供展开态宽度自适应使用；紧凑态请用 PushCompactWidth（单行）。
    /// </summary>
    private double PushExpandedCardWidth
    {
        get
        {
            var p = ActivePush;
            if (p is null) return 420;
            double need = 0;
            need = Math.Max(need, 50 + MeasureText(p.Title ?? string.Empty, 15, 8)); // 图标40+间距10+标题
            if (!string.IsNullOrEmpty(p.Subtitle))
                need = Math.Max(need, MeasureTextML(p.Subtitle, 11.5, 6.2)); // 副标题
            if (!string.IsNullOrEmpty(p.Body))
                need = Math.Max(need, Math.Min(MeasureTextML(p.Body, 12, 6.5), 560)); // 正文
            if (p.Buttons is { Count: > 0 })
            {
                double btnW = 0;
                foreach (var b in p.Buttons) btnW += MeasureText(b.Label ?? string.Empty, 12, 6.5) + 26;
                btnW += (p.Buttons.Count - 1) * 8;
                need = Math.Max(need, btnW);
            }
            if (p.Input is not null) need = Math.Max(need, 300 + 8 + 72); // 输入框 + 提交按钮
            var scale = Math.Clamp(_settings.Current.FontScale, 0.8, 1.4);
            return Math.Clamp((Math.Min(need, 560) + 56) / scale, 420, 640);
        }
    }


    // ── Visibility / expansion ─────────────────────────────────
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value)) return;
            OnPropertyChanged(nameof(ExpandedContentVisibility));
        }
    }

    /// <summary>全屏（视频/游戏/演示）时由 FullScreenMonitor 置位，灵动岛自动隐藏，退出全屏恢复。</summary>
    public bool FullScreenHidden { get; set; }

    /// <summary>Windows 锁屏时由 SessionSwitch 置位，灵动岛自动隐藏，解锁后恢复。</summary>
    public bool LockScreenHidden { get; set; }

    public bool IsVisible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    // Used by XAML to collapse expanded-only sections without a converter.
    public System.Windows.Visibility ExpandedContentVisibility
        => IsExpanded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // ── Visibility ─────────────────────────────────────────────
    private readonly Dictionary<string, bool> _visCache = new();
    private bool _visFirst = true;
    private void RaiseVisIfChanged(string name, bool current)
    {
        if (_visFirst || !_visCache.TryGetValue(name, out var prev) || prev != current)
        {
            _visCache[name] = current;
            OnPropertyChanged(name);
        }
    }

    public void UpdateVisibility()
    {
        var hasMedia = _snapshot is not null && Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
        HasMedia = hasMedia;
        var alwaysVisible = _settings.Current.IslandAlwaysVisible;
        var comp = _settings.Current.Components;
        // 空闲时是否有任意组件（或常驻/旧开关）需要显示
        var anyIdleComp = _pluginComponents.Any(x => x.Component.ShowWhenIdle && PluginVisible(x.PluginId)) || comp.TimeWhenIdle || comp.WeatherWhenIdle || comp.CoverWhenIdle
            || comp.TitleWhenIdle || comp.ArtistWhenIdle || comp.LyricsWhenIdle || comp.ProgressWhenIdle
            || comp.DiskWhenIdle;
        var showWidgets = !hasMedia && (_settings.Current.ShowWidgetsWhenNoMedia || alwaysVisible || anyIdleComp);
        ShowIdleWidgets = !hasMedia; // 空闲面板可见性（内部按组件勾选）

        // 有上岛推送时也要显示灵动岛（否则第三方推送看不到）
        // 音量/复制/下载等临时指示激活时也强制显示（到期自动消失后恢复隐藏）
        var anyTempStatus = (VolumeTempText.Length > 0 || FileCopyText.Length > 0 || DownloadText.Length > 0);
        var show = !_userHidden && !FullScreenHidden && !LockScreenHidden && (hasMedia || showWidgets || HasActivePush || !_settings.Current.HideWhenNoMedia || anyTempStatus);
        // 常驻时不因暂停而隐藏
        if (!alwaysVisible && hasMedia && Status == PlaybackStatus.Paused && !_settings.Current.ShowWhenPaused)
            show = false;

        if (!ShowIdleWeather) { WeatherText = string.Empty; WeatherDetailText = string.Empty; } // 仅天气组件不显示时才清空
        if (!ShowIdleMic) MicText = string.Empty; // 麦克风/摄像头组件不勾选时清空
        if (!ShowIdleCam) CamText = string.Empty;

        // 通知界面组件可见性变化（仅在值实际改变时触发，减少 GC 抖动）
        RaiseVisIfChanged(nameof(ShowCover), ShowCover);
        RaiseVisIfChanged(nameof(ShowTitle), ShowTitle);
        RaiseVisIfChanged(nameof(ShowArtist), ShowArtist);
        RaiseVisIfChanged(nameof(ShowLyrics), ShowLyrics);
        RaiseVisIfChanged(nameof(ShowCompactProgress), ShowCompactProgress);
        RaiseVisIfChanged(nameof(ShowIdleTime), ShowIdleTime);
        RaiseVisIfChanged(nameof(ShowIdleWeather), ShowIdleWeather);
        RaiseVisIfChanged(nameof(ShowAnyWidget), ShowAnyWidget);
        RebuildCompactItems();
        RaiseVisIfChanged(nameof(WidgetTimeFontSize), WidgetTimeFontSize > 0);
        RaiseVisIfChanged(nameof(ShowIdleDate), ShowIdleDate);
        RaiseVisIfChanged(nameof(ShowIdleCpu), ShowIdleCpu);
        RaiseVisIfChanged(nameof(ShowIdleMic), ShowIdleMic);
        RaiseVisIfChanged(nameof(ShowIdleCam), ShowIdleCam);
        RaiseVisIfChanged(nameof(ShowIdleRam), ShowIdleRam);
        RaiseVisIfChanged(nameof(ShowIdleNet), ShowIdleNet);
        RaiseVisIfChanged(nameof(ShowIdleBattery), ShowIdleBattery);
        RaiseVisIfChanged(nameof(ShowIdleVolume), ShowIdleVolume);
        RaiseVisIfChanged(nameof(ShowIdleCapsLock), ShowIdleCapsLock);
        RaiseVisIfChanged(nameof(ShowIdleDisk), ShowIdleDisk);
        RaiseVisIfChanged(nameof(ShowIdleInputMethod), ShowIdleInputMethod);
        RaiseVisIfChanged(nameof(ShowIdleQuickToggles), ShowIdleQuickToggles);
        RaiseVisIfChanged(nameof(VolumeText), VolumeText.Length > 0);
        _visFirst = false;

        IsVisible = show;
    }

    /// <summary>点击输入法组件：切换中/英输入法后立即刷新状态文本。</summary>
    public void ToggleInputMethod()
    {
        InputMethodMonitor.ToggleChineseEnglish();
        InputMethodText = InputMethodMonitor.GetStatusText();
    }

    /// <summary>刷新快捷开关状态文本（Radio 2 秒缓存，其余本地即时读取；值不变不触发通知）。</summary>
    public async void RefreshQuickToggles()
    {
        try
        {
            await QuickSwitchService.RefreshRadiosAsync();
            QuickWifiText = FormatQuickSwitch("Quick_Wifi", QuickSwitchService.HasWifi, QuickSwitchService.IsWifiOn);
            QuickBtText = FormatQuickSwitch("Quick_Bluetooth", QuickSwitchService.HasBluetooth, QuickSwitchService.IsBluetoothOn);
            QuickNightText = Localization.Get("Quick_Night") + " " + (QuickSwitchService.IsNightMode ? Localization.Get("Quick_On") : Localization.Get("Quick_Off"));
            QuickMuteText = Localization.Get("Quick_Mute") + " " + (QuickSwitchService.IsMuted ? Localization.Get("Quick_On") : Localization.Get("Quick_Off"));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"RefreshQuickToggles failed: {ex.Message}");
        }
    }

    private static string FormatQuickSwitch(string nameKey, bool available, bool on)
    {
        if (!available) return Localization.Get(nameKey) + " " + Localization.Get("Quick_NA");
        return Localization.Get(nameKey) + " " + (on ? Localization.Get("Quick_On") : Localization.Get("Quick_Off"));
    }

    /// <summary>切换歌词翻译显示开关。</summary>
    public void ToggleLyricTranslation()
    {
        ShowLyricTranslation = !ShowLyricTranslation;
    }

    /// <summary>复制当前歌词句到剪贴板（无歌词时无操作）。</summary>
    public void CopyCurrentLyric()
    {
        try
        {
            var text = CurrentLyricText;
            if (string.IsNullOrEmpty(text)) return;
            _clipboard.CopyToClipboard(text);
            AppLogger.Info("Current lyric copied to clipboard.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CopyCurrentLyric failed: {ex.Message}");
        }
    }

    /// <summary>点击快捷开关（which: wifi / bluetooth / night / mute）。</summary>
    public async void ToggleQuickSwitch(string which)
    {
        try
        {
            switch (which)
            {
                case "wifi":
                    var ok = await QuickSwitchService.SetRadioAsync(false, !QuickSwitchService.IsWifiOn);
                    if (!ok) TryOpenNetworkSettings(); // Radio 不可控（硬件/驱动限制）时兜底：打开系统网络设置
                    break;
                case "bluetooth":
                    await QuickSwitchService.SetRadioAsync(true, !QuickSwitchService.IsBluetoothOn);
                    break;
                case "night":
                    QuickSwitchService.ToggleNightMode();
                    break;
                case "mute":
                    QuickSwitchService.ToggleMute();
                    break;
            }
            RefreshQuickToggles();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ToggleQuickSwitch failed: {ex.Message}");
        }
    }

    private static void TryOpenNetworkSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Open network settings failed: {ex.Message}");
        }
    }

    public void ToggleUserVisible()
    {
        _userHidden = !_userHidden;
        UpdateVisibility();
    }

    /// <summary>
    /// 用户显式要求显示（再次启动 exe / 托盘「显示」）。同时清掉自动隐藏门控，
    /// 否则前台存在全屏窗口时 UpdateVisibility 仍会算出 show=false，表现为「点了没反应」。
    /// </summary>
    public void ForceShow()
    {
        _userHidden = false;
        FullScreenHidden = false;
        LockScreenHidden = false;
        UpdateVisibility();
    }

    public void ForceHide() { _userHidden = true; UpdateVisibility(); }

    /// <summary>强制重新获取当前曲目的歌词（在线歌词开关变化后调用）。</summary>
    public async Task RefreshLyricsAsync()
    {
        _lyricsKey = string.Empty;
        _lyricsService.ClearCache();
        if (_snapshot is not null) await LoadLyricsAsync(_snapshot);
    }

    /// <summary>Clear the persisted remote-lyrics cache and reload the current track.</summary>
    public async Task ClearLyricsDiskCacheAsync()
    {
        _lyricsService.ClearDiskCache();
        await RefreshLyricsAsync();
    }
    /// <summary>
    /// Demo mode (--demo): injects a fake track so the island can be previewed
    /// without any media playing. Also writes a sample .lrc for lyrics.
    /// </summary>
    public void InjectDemoMedia(double startSeconds = 12.4)
    {
        _demoInjected = true;
        var artPath = CreateDemoArtwork();

        var track = new TrackInfo("Shall We Talk", "陈奕迅 · Eason Chan", "Shall We Dance? Shall We Talk!", "陈奕迅",
            "粤语流行", "demo-source", artPath, string.Empty, TimeSpan.FromSeconds(210));
        var snap = new MediaSnapshot
        {
            Track = track,
            Source = MediaSourceKind.Smtc,
            Status = PlaybackStatus.Playing,
            PositionSeconds = double.IsFinite(startSeconds) ? Math.Max(0, startSeconds) : 12.4,
            DurationSeconds = 210,
            CanPlayPause = true,
            CanNext = true,
            CanPrevious = true,
            CanSeek = true,
            HasVolumeControl = true,
            Volume = 0.6,
            HasLyrics = true,
        };

        // Demo previews must be deterministic: drop any playback position restored from a
        // previous run, otherwise --demo-at is silently overridden by stale state.
        _restoredTrackKey = null;
        _restoredPosition = 0;
        _restoredMode = false;
        _restoredStatus = PlaybackStatus.Closed;

        OnSnapshotChanged(this, snap);
        ApplyDemoLyrics(snap.PositionSeconds);
    }

    /// <summary>
    /// Demo-only word-level lyrics (Apple Music TTML): Chinese characters, spaced Latin
    /// syllables, a translated line and a background-harmony line. Used by --demo so the
    /// karaoke layout can be verified without a real media session.
    /// </summary>
    private void ApplyDemoLyrics(double positionSeconds)
    {
        const string demoTtml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xml:lang="zh">
              <body>
                <div>
                  <p begin="00:00:00.000" end="00:00:06.000" ttm:agent="v1"><span begin="00:00:00.000" end="00:00:00.600">明</span><span begin="00:00:00.600" end="00:00:01.200">月</span><span begin="00:00:01.200" end="00:00:01.800">光</span><span begin="00:00:01.800" end="00:00:02.400">，</span><span begin="00:00:02.400" end="00:00:03.000">为</span><span begin="00:00:03.000" end="00:00:03.600">何</span><span begin="00:00:03.600" end="00:00:04.200">又</span><span begin="00:00:04.200" end="00:00:04.800">照</span><span begin="00:00:04.800" end="00:00:05.400">地</span><span begin="00:00:05.400" end="00:00:06.000">堂</span></p>
                  <p begin="00:00:06.000" end="00:00:12.000" ttm:agent="v1"><span begin="00:00:06.000" end="00:00:06.500">宁</span><span begin="00:00:06.500" end="00:00:07.000">愿</span><span begin="00:00:07.000" end="00:00:07.500">在</span><span begin="00:00:07.500" end="00:00:08.000">公</span><span begin="00:00:08.000" end="00:00:08.500">园</span><span begin="00:00:08.500" end="00:00:09.000">躲</span><span begin="00:00:09.000" end="00:00:09.500">藏</span><span begin="00:00:09.500" end="00:00:10.000">，</span><span begin="00:00:10.000" end="00:00:10.500">不</span><span begin="00:00:10.500" end="00:00:11.000">想</span><span begin="00:00:11.000" end="00:00:11.500">喝</span><span begin="00:00:11.500" end="00:00:12.000">汤</span></p>
                  <p begin="00:00:12.000" end="00:00:18.000" ttm:agent="v1"><span begin="00:00:12.000" end="00:00:12.545">任</span><span begin="00:00:12.545" end="00:00:13.091">由</span><span begin="00:00:13.091" end="00:00:13.636">目</span><span begin="00:00:13.636" end="00:00:14.182">光</span><span begin="00:00:14.182" end="00:00:14.727">，</span><span begin="00:00:14.727" end="00:00:15.273">留</span><span begin="00:00:15.273" end="00:00:15.818">在</span><span begin="00:00:15.818" end="00:00:16.364">漫</span><span begin="00:00:16.364" end="00:00:16.909">画</span><span begin="00:00:16.909" end="00:00:17.455">一</span><span begin="00:00:17.455" end="00:00:18.000">角</span></p>
                  <p begin="00:00:18.000" end="00:00:24.000" ttm:agent="v1"><span begin="00:00:18.000" end="00:00:18.462">为</span><span begin="00:00:18.462" end="00:00:18.923">何</span><span begin="00:00:18.923" end="00:00:19.385">望</span><span begin="00:00:19.385" end="00:00:19.846">母</span><span begin="00:00:19.846" end="00:00:20.308">亲</span><span begin="00:00:20.308" end="00:00:20.769">一</span><span begin="00:00:20.769" end="00:00:21.231">眼</span><span begin="00:00:21.231" end="00:00:21.692">，</span><span begin="00:00:21.692" end="00:00:22.154">就</span><span begin="00:00:22.154" end="00:00:22.615">如</span><span begin="00:00:22.615" end="00:00:23.077">罚</span><span begin="00:00:23.077" end="00:00:23.538">留</span><span begin="00:00:23.538" end="00:00:24.000">堂</span></p>
                  <p begin="00:00:24.000" end="00:00:30.000" ttm:agent="v1"><span begin="00:00:24.000" end="00:00:24.400">孩</span><span begin="00:00:24.400" end="00:00:24.800">童</span><span begin="00:00:24.800" end="00:00:25.200">只</span><span begin="00:00:25.200" end="00:00:25.600">盼</span><span begin="00:00:25.600" end="00:00:26.000">望</span><span begin="00:00:26.000" end="00:00:26.400">欢</span><span begin="00:00:26.400" end="00:00:26.800">乐</span><span begin="00:00:26.800" end="00:00:27.200">，</span><span begin="00:00:27.200" end="00:00:27.600">大</span><span begin="00:00:27.600" end="00:00:28.000">人</span><span begin="00:00:28.000" end="00:00:28.400">只</span><span begin="00:00:28.400" end="00:00:28.800">知</span><span begin="00:00:28.800" end="00:00:29.200">道</span><span begin="00:00:29.200" end="00:00:29.600">期</span><span begin="00:00:29.600" end="00:00:30.000">望</span></p>
                  <p begin="00:00:30.000" end="00:00:36.000" ttm:agent="v1"><span begin="00:00:30.000" end="00:00:30.429">为</span><span begin="00:00:30.429" end="00:00:30.857">何</span><span begin="00:00:30.857" end="00:00:31.286">都</span><span begin="00:00:31.286" end="00:00:31.714">不</span><span begin="00:00:31.714" end="00:00:32.143">大</span><span begin="00:00:32.143" end="00:00:32.571">懂</span><span begin="00:00:32.571" end="00:00:33.000">得</span><span begin="00:00:33.000" end="00:00:33.429">，</span><span begin="00:00:33.429" end="00:00:33.857">努</span><span begin="00:00:33.857" end="00:00:34.286">力</span><span begin="00:00:34.286" end="00:00:34.714">体</span><span begin="00:00:34.714" end="00:00:35.143">恤</span><span begin="00:00:35.143" end="00:00:35.571">对</span><span begin="00:00:35.571" end="00:00:36.000">方</span></p>
                  <p begin="00:00:36.000" end="00:00:42.000" ttm:agent="v1"><span begin="00:00:36.000" end="00:00:36.429">大</span><span begin="00:00:36.429" end="00:00:36.857">门</span><span begin="00:00:36.857" end="00:00:37.286">外</span><span begin="00:00:37.286" end="00:00:37.714">有</span><span begin="00:00:37.714" end="00:00:38.143">蟋</span><span begin="00:00:38.143" end="00:00:38.571">蟀</span><span begin="00:00:38.571" end="00:00:39.000">，</span><span begin="00:00:39.000" end="00:00:39.429">回</span><span begin="00:00:39.429" end="00:00:39.857">响</span><span begin="00:00:39.857" end="00:00:40.286">却</span><span begin="00:00:40.286" end="00:00:40.714">如</span><span begin="00:00:40.714" end="00:00:41.143">同</span><span begin="00:00:41.143" end="00:00:41.571">幻</span><span begin="00:00:41.571" end="00:00:42.000">觉</span></p>
                  <p begin="00:00:42.000" end="00:00:48.000" ttm:agent="v1"><span begin="00:00:42.000" end="00:00:43.000">Shall</span><span begin="00:00:43.000" end="00:00:44.000">we</span><span begin="00:00:44.000" end="00:00:45.000">talk,</span><span begin="00:00:45.000" end="00:00:46.000">shall</span><span begin="00:00:46.000" end="00:00:47.000">we</span><span begin="00:00:47.000" end="00:00:48.000">talk</span></p>
                  <p begin="00:00:48.000" end="00:00:54.000" ttm:agent="v1"><span begin="00:00:48.000" end="00:00:48.545">就</span><span begin="00:00:48.545" end="00:00:49.091">当</span><span begin="00:00:49.091" end="00:00:49.636">重</span><span begin="00:00:49.636" end="00:00:50.182">新</span><span begin="00:00:50.182" end="00:00:50.727">手</span><span begin="00:00:50.727" end="00:00:51.273">拖</span><span begin="00:00:51.273" end="00:00:51.818">手</span><span begin="00:00:51.818" end="00:00:52.364">去</span><span begin="00:00:52.364" end="00:00:52.909">上</span><span begin="00:00:52.909" end="00:00:53.455">学</span><span begin="00:00:53.455" end="00:00:54.000">堂</span></p>
                  <p begin="00:00:54.000" end="00:01:00.000" ttm:agent="v1"><span begin="00:00:54.000" end="00:00:54.375">陪</span><span begin="00:00:54.375" end="00:00:54.750">我</span><span begin="00:00:54.750" end="00:00:55.125">讲</span><span begin="00:00:55.125" end="00:00:55.500">，</span><span begin="00:00:55.500" end="00:00:55.875">陪</span><span begin="00:00:55.875" end="00:00:56.250">我</span><span begin="00:00:56.250" end="00:00:56.625">讲</span><span begin="00:00:56.625" end="00:00:57.000">出</span><span begin="00:00:57.000" end="00:00:57.375">我</span><span begin="00:00:57.375" end="00:00:57.750">们</span><span begin="00:00:57.750" end="00:00:58.125">最</span><span begin="00:00:58.125" end="00:00:58.500">后</span><span begin="00:00:58.500" end="00:00:58.875">何</span><span begin="00:00:58.875" end="00:00:59.250">以</span><span begin="00:00:59.250" end="00:00:59.625">生</span><span begin="00:00:59.625" end="00:01:00.000">疏</span></p>
                  <p begin="00:01:00.000" end="00:01:06.000" ttm:agent="v1"><span begin="00:01:00.000" end="00:01:00.429">谁</span><span begin="00:01:00.429" end="00:01:00.857">怕</span><span begin="00:01:00.857" end="00:01:01.286">讲</span><span begin="00:01:01.286" end="00:01:01.714">，</span><span begin="00:01:01.714" end="00:01:02.143">谁</span><span begin="00:01:02.143" end="00:01:02.571">会</span><span begin="00:01:02.571" end="00:01:03.000">可</span><span begin="00:01:03.000" end="00:01:03.429">悲</span><span begin="00:01:03.429" end="00:01:03.857">得</span><span begin="00:01:03.857" end="00:01:04.286">过</span><span begin="00:01:04.286" end="00:01:04.714">孤</span><span begin="00:01:04.714" end="00:01:05.143">独</span><span begin="00:01:05.143" end="00:01:05.571">探</span><span begin="00:01:05.571" end="00:01:06.000">戈</span></p>
                  <p begin="00:01:06.000" end="00:01:12.000" ttm:agent="v1"><span begin="00:01:06.000" end="00:01:06.429">难</span><span begin="00:01:06.429" end="00:01:06.857">得</span><span begin="00:01:06.857" end="00:01:07.286">可</span><span begin="00:01:07.286" end="00:01:07.714">以</span><span begin="00:01:07.714" end="00:01:08.143">同</span><span begin="00:01:08.143" end="00:01:08.571">座</span><span begin="00:01:08.571" end="00:01:09.000">，</span><span begin="00:01:09.000" end="00:01:09.429">何</span><span begin="00:01:09.429" end="00:01:09.857">以</span><span begin="00:01:09.857" end="00:01:10.286">要</span><span begin="00:01:10.286" end="00:01:10.714">忌</span><span begin="00:01:10.714" end="00:01:11.143">讳</span><span begin="00:01:11.143" end="00:01:11.571">赤</span><span begin="00:01:11.571" end="00:01:12.000">裸</span></p>
                  <p begin="00:01:12.000" end="00:01:18.000" ttm:agent="v1"><span begin="00:01:12.000" end="00:01:12.400">如</span><span begin="00:01:12.400" end="00:01:12.800">果</span><span begin="00:01:12.800" end="00:01:13.200">心</span><span begin="00:01:13.200" end="00:01:13.600">声</span><span begin="00:01:13.600" end="00:01:14.000">真</span><span begin="00:01:14.000" end="00:01:14.400">有</span><span begin="00:01:14.400" end="00:01:14.800">疗</span><span begin="00:01:14.800" end="00:01:15.200">效</span><span begin="00:01:15.200" end="00:01:15.600">，</span><span begin="00:01:15.600" end="00:01:16.000">谁</span><span begin="00:01:16.000" end="00:01:16.400">怕</span><span begin="00:01:16.400" end="00:01:16.800">暴</span><span begin="00:01:16.800" end="00:01:17.200">露</span><span begin="00:01:17.200" end="00:01:17.600">更</span><span begin="00:01:17.600" end="00:01:18.000">多</span></p>
                </div>
              </body>
            </tt>
            """;

        var doc = TtmlParser.Parse(demoTtml);
        if (doc.IsEmpty) return;

        // Invalidate the in-flight lookup for this demo track so an empty online result
        // cannot clear the preview lyrics when it finishes.
        var key = _snapshot is null ? "demo" : LyricsService.TrackKey(_snapshot.Track);
        _lyricsKey = key + "#demo";

        _lyrics = new LyricsResult(new LrcDocument(), LyricsSourceKind.AmllTtml, "Demo TTML", doc);
        LyricLines = doc.Lines.Select(line => new LyricLineViewModel(line)).ToList();
        RefreshLyricTranslationVisibility();

        var seconds = LyricsAdjustedPosition(
            TimeSpan.FromSeconds(KaraokeMath.AtLeastZero(positionSeconds))).TotalSeconds;
        var index = IndexAtPosition(seconds);
        LyricIndex = index;
        CurrentLyricWords = index >= 0 ? LyricLines[index].Words : Array.Empty<TtmlWord>();
        CurrentLyricText = index >= 0 ? LyricLines[index].Text : string.Empty;
        LyricsStatus = Localization.Get("Lyrics_Amll");
        OnPropertyChanged(nameof(HasLyrics));
    }

    private static string CreateDemoArtwork()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ThumbCacheDir);
            var path = Path.Combine(AppPaths.ThumbCacheDir, "demo-art-shallwetalk.jpg");
            if (File.Exists(path)) return path;

            using var bmp = new System.Drawing.Bitmap(512, 512);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            var rect = new System.Drawing.Rectangle(0, 0, 512, 512);
            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(rect,
                System.Drawing.Color.FromArgb(255, 8, 13, 25),
                System.Drawing.Color.FromArgb(255, 22, 44, 68), 62f))
                g.FillRectangle(bg, rect);

            // Soft studio spotlight, upper right.
            for (var i = 14; i >= 1; i--)
            {
                var size = 250 + i * 26;
                using var halo = new System.Drawing.SolidBrush(
                    System.Drawing.Color.FromArgb(4 + (15 - i), 126, 196, 224));
                g.FillEllipse(halo, 356 - size / 2f, 150 - size / 2f, size, size);
            }

            // Cool moon-like disc so the cover still reads at thumbnail size.
            using (var disc = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(216, 94, 184, 184),
                System.Drawing.Color.FromArgb(236, 228, 242, 248),
                System.Drawing.Color.FromArgb(196, 128, 168, 198), 55f))
                g.FillEllipse(disc, 216, 94, 184, 184);
            using (var shade = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(26, 90, 130, 160)))
                g.FillEllipse(shade, 258, 132, 96, 78);

            // Lower half: deep water with two soft swells.
            using (var sea = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 300, 512, 212),
                System.Drawing.Color.FromArgb(0, 12, 26, 42),
                System.Drawing.Color.FromArgb(255, 7, 15, 29), 90f))
                g.FillRectangle(sea, 0, 300, 512, 212);
            using (var wave = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(64, 84, 158, 196)))
            {
                g.FillEllipse(wave, -140, 372, 540, 220);
                g.FillEllipse(wave, 176, 414, 560, 240);
            }

            // Fine highlight arc around the disc.
            using (var arc = new System.Drawing.Pen(System.Drawing.Color.FromArgb(64, 220, 240, 250), 3f))
                g.DrawArc(arc, 150, 52, 250, 250, 196, 148);

            using var titleFont = new System.Drawing.Font("Segoe UI Semibold", 29,
                System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var titleBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(244, 248, 252, 255));
            g.DrawString("Shall We Talk", titleFont, titleBrush, 38, 396);
            using var smallFont = new System.Drawing.Font("Microsoft YaHei UI", 15,
                System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            using var smallBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(194, 205, 224, 240));
            g.DrawString("\u9648\u5955\u8FC5 \u00B7 Eason Chan", smallFont, smallBrush, 40, 440);

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
            return path;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Demo artwork failed: {ex.Message}");
            return string.Empty;
        }
    }

    // Helpers
    /// <summary>Get artwork by path, with a small in-memory LRU cache.</summary>
    private ImageSource? GetArtwork(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_artworkCache.TryGetValue(path, out var cached))
        {
            _artworkCache.Remove(path);
            _artworkCache[path] = cached;
            return cached;
        }
        var img = LoadImage(path);
        if (img is null) return null;
        if (_artworkCache.Count >= ArtworkCacheMax)
        {
            // 淘汰最早插入的一项（Dictionary 保持插入序）
            using var en = _artworkCache.Keys.GetEnumerator();
            if (en.MoveNext()) _artworkCache.Remove(en.Current);
        }
        _artworkCache[path] = img;
        return img;
    }

    private static ImageSource? LoadImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 512;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"LoadImage failed for {path}: {ex.Message}");
            return null;
        }
    }

    private void RaiseAllText()
    {
        LyricsStatus = _lyrics.Source switch
        {
            LyricsSourceKind.LocalFile => Localization.Get("Lyrics_Local"),
            LyricsSourceKind.Cider => Localization.Get("Lyrics_FromCider"),
            LyricsSourceKind.Online => Localization.Get("Lyrics_Online"),
            _ => Localization.Get("LyricsUnavailable"),
        };
        OnPropertyChanged(nameof(LyricsStatus));
        // 语言切换后刷新含本地化文案的临时状态（复制/下载/合并胶囊）
        PollFileCopy();
        PollDownloadProgress();
        RebuildCompactItems();
    }

    /// <summary>Begin a user drag on the progress slider.</summary>
    public void BeginSeek() => _suppressSeek++;

    /// <summary>Seek to the given fraction (0..1) after a drag.</summary>
    public async Task EndSeekAsync(double fraction)
    {
        _suppressSeek--;
        if (_snapshot is null || _snapshot.DurationSeconds <= 0) return;
        var target = KaraokeMath.ClampFraction(fraction) * _snapshot.DurationSeconds;
        _restoredMode = false;
        _interpolatedPosition = target;
        _lastPositionTime = DateTime.UtcNow;
        await _coordinator.SeekAsync(target);
    }

    /// <summary>相对跳转指定秒数（正数前进、负数后退），夹在 0..总时长之间。</summary>
    public async Task SeekRelativeAsync(double deltaSeconds)
    {
        if (!CanSeek || DurationSeconds <= 0) return;
        var current = _suppressSeek > 0 ? Position.TotalSeconds : _interpolatedPosition;
        var target = MediaControlMath.SeekTarget(current, DurationSeconds, deltaSeconds);
        _suppressSeek++;
        try
        {
            await _coordinator.SeekAsync(target);
            _interpolatedPosition = target;
            _lastPositionTime = DateTime.UtcNow;
        }
        finally
        {
            _suppressSeek--;
        }
    }

    /// <summary>按固定步长调整音量（0..1 之间）。</summary>
    public void StepVolume(double delta)
    {
        if (!HasVolumeControl) return;
        Volume = MediaControlMath.VolumeTarget(Volume, delta);
    }
    /// <summary>
    /// 乐观播放/暂停：点击按钮后立即切换本地状态，避免快照状态延迟期间
    /// 本地进度继续推进，导致暂停后歌词高亮/进度条“跳回”暂停点。
    /// </summary>
    private async Task TogglePlayPauseLocalAsync()
    {
        if (_toggleInFlight) return; // 防连点：命令在途时忽略再次点击
        if (Status != PlaybackStatus.Playing && Status != PlaybackStatus.Paused) return;
        _toggleInFlight = true;
        var target = Status == PlaybackStatus.Playing ? PlaybackStatus.Paused : PlaybackStatus.Playing;
        try
        {
            _pauseLock = target == PlaybackStatus.Paused; // 暂停则锁定，播放则解除
            SetStatusLocal(target); // 立即切换按钮状态，避免延迟感
            _optimisticStatus = target;
            _statusOverrideActive = true;
            _statusOverrideUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            var ok = await _coordinator.TogglePlayPauseAsync();
            if (!ok)
            {
                // 部分播放器/Cider 版本不支持 playpause 端点：回退到明确的 play/pause
                ok = target == PlaybackStatus.Paused
                    ? await _coordinator.PauseAsync()
                    : await _coordinator.PlayAsync();
            }
            if (!ok) AppLogger.Warn("Play/pause command returned failure; waiting for player state to settle.");
        }
        finally
        {
            _toggleInFlight = false;
        }
        // 保护期不在此结束：等快照确认目标状态或超时后再恢复快照驱动，防止按钮被打回
    }

    private void SetStatusLocal(PlaybackStatus value)
    {
        if (Status == value) return;
        var wasPlaying = Status == PlaybackStatus.Playing;
        Status = value;
        if (!wasPlaying && value == PlaybackStatus.Playing) OnResumePlaying();
        _wave.SetPlaying(value == PlaybackStatus.Playing);
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        if (value == PlaybackStatus.Paused) SavePlaybackState(); // 暂停即保存，退出/崩溃后可恢复
    }

    /// <summary>从暂停/停止进入播放时重置本地时钟基准：把“暂停时长”排除在进度与卡拉OK推进之外，
    /// 否则恢复播放瞬间会把整段暂停时间加上去（歌词先猛跳、随后被快照拉回，表现为“放大缩小跳动”）。
    /// 自由时钟（播放器不报 SMTC 进度）模式下，也以当前插值位置为新起点，暂停时长不再计入。</summary>
    private void OnResumePlaying()
    {
        _lastPositionTime = DateTime.UtcNow;
        if (_useFreeClock || _trackStartTime == default || _trackStartTime == DateTime.MinValue)
            _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(KaraokeMath.AtLeastZero(_interpolatedPosition));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _progressTimer.Stop();
        _widgetTimer.Stop();
        _copyToastTimer.Stop();
        _coordinator.SnapshotChanged -= OnSnapshotChanged;
        _coordinator.MediaEnded -= OnMediaEnded;
        _coordinator.SessionsChanged -= OnSessionsChanged;
        _keyboard.StateChanged -= OnKeyboardStateChanged;
        if (_messengerMonitor is not null) _messengerMonitor.Changed -= OnMessengerUnreadsChanged;
        _settings.Changed -= OnPageSettingsChanged;
        Localization.LanguageChanged -= OnLanguageChanged;
        // 释放效率工具服务（Stop/Dispose 幂等，App 退出时再次调用安全）
        _keyboard.Dispose();
        _clipboard.Dispose();
        _wave.Stop();
    }
}

/// <summary>通知记录项：系统通知中心历史服务使用的通用数据结构。</summary>
public sealed class EventHistoryItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string Body { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Type { get; init; } = "info";
    public DateTime TimeUtc { get; init; }
    public string TimeText { get; init; } = "";
    /// <summary>通知来源（exe/AppName），用于勿扰白名单匹配。</summary>
    public string Source { get; set; } = "";
    /// <summary>是否已读。</summary>
    public bool Read { get; set; } = false;
}

/// <summary>多播放器选择器中的一行（AppId + 名称 + 是否当前跟随）。</summary>
public sealed class MediaSessionItem : ObservableObject
{
    private bool _isCurrent;

    public string AppId { get; }
    public string AppName { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public MediaSessionItem(string appId, string appName, bool isCurrent)
    {
        AppId = appId;
        AppName = appName;
        _isCurrent = isCurrent;
    }
}
