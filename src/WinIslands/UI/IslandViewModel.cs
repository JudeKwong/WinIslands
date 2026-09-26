using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using WinIslands.Services;

namespace WinIslands.UI;

/// <summary>
/// View model for the island window: maps coordinator snapshots to bindable state,
/// interpolates playback position for a smooth progress bar, and manages lyrics.
/// </summary>
public sealed class IslandViewModel : ObservableObject, IDisposable
{
    private int _disposed;
    private readonly MediaCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly LyricsService _lyricsService;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _widgetTimer;
    private readonly WeatherService _weather = new();
    private int _weatherTick;
    private DateTime _trackStartTime = DateTime.UtcNow;
    private bool _useFreeClock;   // ���������� SMTC ���ȣ��� Cider��ʱ�ñ���ʱ���ƽ�����OK

    private MediaSnapshot? _snapshot;
    private LyricsResult _lyrics = LyricsResult.Empty;
    private Dictionary<double, TtmlLine> _ttmlLineIndex = new(); // ��ǰ��ʵ� TTML �����������п�ʼ�룬˫��ϲ���ʱ��ȡ���ִʣ�
    private DateTime _lastPositionTime;
    private double _interpolatedPosition;
    private int _lyricIndex = -1;
    private DateTime? _positionStaleSinceUtc; // �ϱ�λ�����Ի��˵���ʼʱ�̣���˲�� 0/����λ�ô�ؿ�ͷ��
    private bool _expanded;
    private bool _visible;
    private bool _userHidden;
    private bool _suppressVolume;
    private int _suppressSeek;
    private string _lyricsKey = string.Empty;
    private readonly Dictionary<string, double> _lyricTimeOffsets = new();  // #4 ���ʱ��΢������Ŀ key -> ƫ����
    private string? _restoredTrackKey;       // �ϴ��˳�ʱ�������Ŀ����������ָ�λ�ã�
    private double _restoredPosition;        // �ϴ��˳�ʱ�����λ�ã��룩
    private bool _karaokeFrozen;             // ��ͣʱ�����Ƿ��Ѷ��ᣨ����λ��У������������
    private bool _restoredMode;                // ����ָ������λָ�λ�ã��ݲ����ɻ���/����λ��
    private bool _toggleInFlight;               // ����/��ͣ������;���������ظ�������
    private bool _statusOverrideActive;         // �ֹ�״̬�����ڣ��ڼ䲻�����մ��
    private PlaybackStatus _optimisticStatus;   // �ֹ�Ŀ��״̬���ȴ�����ȷ�ϣ�
    private DateTime _statusOverrideUntilUtc;   // �����ڽ�ֹʱ��
    private bool _pauseLock;                       // ��ͣ�������ڼ䲻����ջָ����š����ƽ����
    private PlaybackStatus _restoredStatus = PlaybackStatus.Closed; // �ϴ��˳���״̬����������ָ���

    // ���� �ϵ����ͣ�������������͵��鶯��������
    private readonly List<IslandPush> _pushes = new();
    private IslandPush? _activePush;
    private string _pushInputValue = "";   // �ϵ������ǰ����

    // ���� Ч�ʹ��� / ���� / ����ָʾ�� ����
    private readonly AudioWaveService _wave;
    private readonly KeyboardIndicatorMonitor _keyboard;
    private readonly ClipboardHistoryService _clipboard;
    private readonly TodoService _todo;
    private readonly ScheduleService _schedule;
    private readonly PomodoroService _pomodoro;
    private int _capsLockSecondsLeft;   // ����ָʾ��ʣ����ʾ�������� _widgetTimer ÿ��ݼ���
    private int _volumeTempSecondsLeft;          // ����ָʾʣ����ʾ����
    private double _lastVolumeTempValue = -1;    // �ϴ���ѯ����ϵͳ�������仯ʱ�����ϵ���
    private bool _lastVolumeTempMuted;           // �ϴ���ѯ���ľ���״̬
    // ���� �ಥ����ѡ���������㲥���� / �������л�ý����Դ������
    private readonly ObservableCollection<MediaSessionItem> _mediaSessions = new();
    private MediaSessionItem? _selectedMediaSession;
    private bool _suppressSessionSwitch;

    // ���� ֪ͨ��ʷ��չ����Ƭ�ײ��б��#10���� ShowEventCard ��¼���ɵ�����µ����� ����
    private readonly ObservableCollection<EventHistoryItem> _notificationHistory = new();
    private readonly Dictionary<string, DateTime> _lastEventTimes = new(); // 通知去抖：记录每种类型最后触发时间
    public ObservableCollection<EventHistoryItem> NotificationHistory => _notificationHistory;

    public IslandViewModel(MediaCoordinator coordinator, SettingsService settings, LyricsService lyricsService,
        AudioWaveService? wave = null, KeyboardIndicatorMonitor? keyboard = null,
        ClipboardHistoryService? clipboard = null, TodoService? todo = null,
        ScheduleService? schedule = null, PomodoroService? pomodoro = null)
    {
        _coordinator = coordinator;
        _settings = settings;
        _lyricsService = lyricsService;

        // Ч�ʹ��� / ���� / ����ָʾ�ƣ�Ĭ���Խ�ʵ����App ��ע�빲��ʵ����
        _wave = wave ?? new AudioWaveService();
        _keyboard = keyboard ?? new KeyboardIndicatorMonitor();
        _clipboard = clipboard ?? new ClipboardHistoryService();
        _todo = todo ?? new TodoService();
        _schedule = schedule ?? new ScheduleService();
        _pomodoro = pomodoro ?? new PomodoroService();
        _wave.Start();
        _wave.SetPlaying(false);
        _keyboard.StateChanged += OnKeyboardStateChanged;
        _clipboard.Changed += RefreshClipboardSummary;
        _todo.Changed += RefreshTodoSummary;
        _schedule.Changed += RefreshScheduleSummary;
        _pomodoro.Tick += RefreshTimerText;
        _pomodoro.Completed += OnPomodoroCompleted;

        // #4 ���ʱ��΢��������Ϊÿ�׸豣���ʱ��ƫ�ƣ��û��ֶ�У׼�ĸ�ʶ��룩
        try
        {
            foreach (var kv in _settings.Current.LyricTimeOffsets)
                _lyricTimeOffsets[kv.Key] = Math.Clamp(kv.Value, -30, 30);
        }
        catch (Exception ex) { AppLogger.Warn($"Load lyric offsets failed: {ex.Message}"); }

        // ���ʱ�ָ��ϴ��˳��Ĳ���λ�ã���ͣ����������ؿ�ͷ��
        var restored = PlaybackStateStore.Load();
        if (restored is not null)
        {
            _restoredTrackKey = restored.TrackKey;
            _restoredPosition = restored.PositionSeconds;
            _restoredStatus = string.Equals(restored.Status, "Paused", StringComparison.OrdinalIgnoreCase)
                ? PlaybackStatus.Paused : PlaybackStatus.Playing;
        }

        // ϵͳ״̬��������CPU/�ڴ棩���� ����ʵ��������ÿ�β������½�
        _cpuCounter = CreateCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = CreateCounter("Memory", "% Committed Bytes In Use", null);

        PlayPauseCommand = new AsyncRelayCommand(_ => TogglePlayPauseLocalAsync());
        NextCommand = new AsyncRelayCommand(_ => _coordinator.NextAsync());
        PreviousCommand = new AsyncRelayCommand(_ => _coordinator.PreviousAsync());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        ToggleLyricsWindowCommand = new RelayCommand(_ => ToggleLyricsWindowRequested?.Invoke(this, EventArgs.Empty));

        _coordinator.SnapshotChanged += OnSnapshotChanged;
        _coordinator.MediaEnded += OnMediaEnded;
        _coordinator.SessionsChanged += OnSessionsChanged;
        RefreshMediaSessions();
        Localization.LanguageChanged += OnLanguageChanged;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) }; // ���Ȳ�ֵ 5Hz��������������ʾ�㹻�����ֿ���OK�ɿؼ��ڲ��� CompositionTarget.Rendering �����ƽ������Ͳ���ʱ CPU ռ��
        _progressTimer.Tick += (_, _) => AdvanceProgress();
        // �������������ý�����ʱ��OnSnapshotChanged�������������/��ý��ʱͣ�ã����ͺ�̨ռ��

        _widgetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _widgetTimer.Tick += async (_, _) =>
        {
            // ʼ����ѯ��������⣺��ʹ�鶯�����أ�����/����/����/��ͼ���¼�ҲҪ�ܴ����ϵ�
            PollVolumeTemp();
            UpdateVolumeTempCountdown();
            PollFileCopy();
            PollDownloadProgress();
            UpdateScreenshotCountdown();
            _widgetTimer.Interval = TimeSpan.FromSeconds(1); // 岛可见时恢复正常 1 秒轮询
            if (!IsVisible) { _widgetTimer.Interval = TimeSpan.FromSeconds(5); return; } // 岛隐藏时降频到 5 秒，减少空闲 CPU 占用
            var nowClock = DateTime.Now.ToString("HH:mm");
            if (ClockText != nowClock) ClockText = nowClock;
            var nowDate = FormatDateText(DateTime.Now);
            if (DateText != nowDate) DateText = nowDate;
            CheckPushExpiry();
            if (_activePush is not null) OnPropertyChanged(nameof(ActivePushProgress)); // v3 ��̬���Ȱ����ƽ�
            UpdateSystemStats();
            UpdateCapsLockCountdown();
            if (ShowIdleClipboard) RefreshClipboardSummary(); // ��̨���������δ��ѡʱ����ѯ
            if (ShowIdleTodo) RefreshTodoSummary();
            if (ShowIdleSchedule) RefreshScheduleSummary();
            if (ShowIdleTimer) RefreshTimerText();
            // ���ᾲ�����֣����ǰ̨���ڻ���״̬������ѡ��������������ʱ�������壬����⿪����С��
            if (ShowIdleMeeting || (_settings.Current.MeetingAssistantEnabled && _settings.Current.MeetingAutoDnd))
            {
                MeetingMonitor.SetCustomKeywords(_settings.Current.MeetingKeywords);
                var meetingChanged = MeetingMonitor.Check();
                var newMeetingText = MeetingMonitor.IsInMeeting
                    ? $"{Localization.Get("Comp_Meeting")} �� {MeetingMonitor.AppName}"
                    : string.Empty;
                if (meetingChanged || MeetingText != newMeetingText)
                {
                    MeetingText = newMeetingText;
                    RebuildCompactItems();
                }
            }
            if (ShowIdleInputMethod) InputMethodText = InputMethodMonitor.GetStatusText();
            if (ShowIdleQuickToggles) RefreshQuickToggles();
            if (ShowIdleWeather && ++_weatherTick % 60 == 1)
            {
                var w = await _weather.GetWeatherAsync(_settings.Current.WeatherCity);
                if (w is not null) // ʧ�ܱ����ֵ�������������к���
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
    /// <summary>#10 �ϵ���ť�ص������ͷ������� notify �����İ�ť�����ʱ��������������ť + ���� ID����</summary>
    public event Action<IslandPushButton, string>? PushActionRequested;

    // ���� ��ݲ�����ť��չ����Ƭ�ײ�һ�ţ�����������������������������������������������������
    private IReadOnlyList<QuickActionItem> _quickActions = Array.Empty<QuickActionItem>();
    public IReadOnlyList<QuickActionItem> QuickActions { get => _quickActions; private set => Set(ref _quickActions, value); }

    /// <summary>�Ƿ��п�ݲ�������ʾ���ܿ��ؿ������й�ѡ�Ĳ�������</summary>
    public bool HasQuickActions => QuickActions.Count > 0;

    /// <summary>�������ؽ���ݲ����б��˳�������е�˳�򣩡�</summary>
    public void RebuildQuickActions()
    {
        var master = _settings.Current.QuickActionsEnabled;
        var shown = _settings.Current.QuickActionsShown ?? new List<string>();
        var items = new List<QuickActionItem>();
        if (master)
        {
            foreach (var key in _settings.Current.QuickActions ?? new List<string>())
            {
                if (!shown.Contains(key, StringComparer.Ordinal)) continue; // δ��ѡ����ʾ
                var (glyph, tipKey) = QuickActionMeta(key);
                if (glyph is null) continue;
                items.Add(new QuickActionItem(key, glyph, Localization.Get(tipKey)));
            }
        }
        QuickActions = items;
        OnPropertyChanged(nameof(HasQuickActions));
    }

    /// <summary>��ݲ����� �� (ͼ������, ��ʾ���ػ���)��δ֪������ null��</summary>
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

    /// <summary>ִ�п�ݲ�������ť�������ȫ��Ϊϵͳ����������������������</summary>
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
                    // ���� PrintScreen ��������ϵͳ��ͼ / ճ����
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

    // ���� �ಥ����ѡ���� ��������������������������������������������������������������������������������
    /// <summary>��ǰ����ý��Ự��SMTC ȫ�� + Cider α�Ự���������㲥����/�����л���Դ��</summary>
    public ObservableCollection<MediaSessionItem> MediaSessions => _mediaSessions;

    /// <summary>�Ƿ�ͬʱ���ڶ������ý����Դ��#3 �ಥ�����л���չ��������ʾѡ��������</summary>
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

    /// <summary>��ǰ��ѡý����Դ���ƣ�#3 ѡ������ť��ʾ����</summary>
    public string SelectedMediaSessionName => _selectedMediaSession?.AppName ?? "";

    /// <summary>ѭ���л�����һ������ý����Դ��#3 �ಥ�����л�����</summary>
    public void CycleMediaSession()
    {
        if (_mediaSessions.Count < 2) return;
        var idx = _mediaSessions.IndexOf(_selectedMediaSession!);
        var next = _mediaSessions[(idx + 1) % _mediaSessions.Count];
        SelectedMediaSession = next;
    }

    /// <summary>ˢ��ý��Ự�б�����ֵ�ǰѡ�У����ᴥ���л�����</summary>
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
            AppLogger.Warn($"ˢ��ý��Ự�б�ʧ��: {ex.Message}");
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
            AppLogger.Warn($"�л�ý����Դʧ��: {ex.Message}");
        }
    }

    // ���� Commands ����������������������������������������������������������������������������������������������
    public AsyncRelayCommand PlayPauseCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ToggleLyricsWindowCommand { get; }

    // ���� Track ����������������������������������������������������������������������������������������������������
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
    /// <summary>�Ƿ��в�����Դ���������Ե�С�ձ��Ƿ���ʾ����</summary>
    public bool HasSourceDetail => !string.IsNullOrEmpty(_sourceDetail);

    private ImageSource? _artwork;
    // ���滺�棺ͬһ·��ֻ����һ�β����ã�SMTC/Cider ÿ���ϱ�ͬһ���棬���ⷴ�� IO + �ڴ涶����
    private readonly Dictionary<string, ImageSource> _artworkCache = new(StringComparer.OrdinalIgnoreCase);
    private const int ArtworkCacheMax = 12; // ���ڷ������ޣ�������̭��ɣ���ֹ��������
    private string _pushImageCacheKey = string.Empty;
    private ImageSource? _pushImageCache;
    public ImageSource? Artwork { get => _artwork; private set => Set(ref _artwork, value); }

    // ���� Playback ����������������������������������������������������������������������������������������������
    private PlaybackStatus _status;
    public PlaybackStatus Status { get => _status; private set => Set(ref _status, value); }

    public bool IsPlaying => Status == PlaybackStatus.Playing;
    public bool IsPaused => Status == PlaybackStatus.Paused;

    private double _durationSeconds;
    public double DurationSeconds { get => _durationSeconds; private set => Set(ref _durationSeconds, value); }

    private TimeSpan _position;
    public TimeSpan Position
    {
        get => _position;
        private set
        {
            if (!Set(ref _position, value)) return;
            OnPropertyChanged(nameof(PositionText));
        }
    }

    /// <summary>��ǰλ���ı�����ȷ���룬�� 1:23 / 1:02:03������ DurationText ͬ��ʽ��</summary>
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
            ShowVolumeTemp((int)Math.Round(_volume * 100), _volume < 0.001); // �϶���������ʱ��ʱ�ϵ�
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768"; // Pause / Play (Segoe MDL2)

    // ���� Lyrics ��������������������������������������������������������������������������������������������������
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
            // ����ʱ�����ͣ���ᣬ�� UpdateKaraokeHighlight ����ǰ����ȷ��λ������һ��
            _karaokeFrozen = false;
            // ����ʱ�����п�ʼ�롹ȡ�þ�����ʱ���ᣨʱ��ƥ�����˫��ϲ��������仯��
            CurrentLyricWords = value >= 0 && value < LyricLines.Count
                ? WordsForLine(_ttmlLineIndex, LyricLines[value].Time.TotalSeconds)
                : Array.Empty<TtmlWord>();
            CurrentLyricText = LyricLines.Count > 0
                ? LyricLines[Math.Clamp(value, 0, LyricLines.Count - 1)].Text
                : string.Empty;
            OnPropertyChanged();
        }
    }

    private string _lyricsStatus = string.Empty;
    public string LyricsStatus { get => _lyricsStatus; private set => Set(ref _lyricsStatus, value); }

    /// <summary>��ǰ������ı�����ս�����δ��ͣʱҲ��ʾ����</summary>
    private string _currentLyricText = string.Empty;
    public string CurrentLyricText { get => _currentLyricText; private set => Set(ref _currentLyricText, value); }

    /// <summary>�Ƿ���ʾ��ʷ����У�չ����ʿ�ݲ��������뿪�أ���</summary>
    private bool _showLyricTranslation = true;
    public bool ShowLyricTranslation
    {
        get => _showLyricTranslation;
        private set
        {
            if (Set(ref _showLyricTranslation, value))
            {
                // ͬ����ÿһ�и�ʣ�������������
                foreach (var l in LyricLines) l.ShowTranslation = value;
                OnPropertyChanged(nameof(LyricTranslateText));
            }
        }
    }
    /// <summary>���뿪�ذ�ť�ı����硸���룺��������</summary>
    public string LyricTranslateText => Localization.Get("Lyric_Translation") + "��" + (ShowLyricTranslation ? Localization.Get("Quick_On") : Localization.Get("Quick_Off"));
    /// <summary>�����Ƶ�ǰ�䡹��ť�ı���</summary>
    public string LyricCopyText => Localization.Get("Lyric_CopyCurrent");
    /// <summary>���뿪����ʾ��</summary>
    public string LyricTranslateHint => Localization.Get("Lyric_TranslateHint");
    /// <summary>���Ƶ�ǰ����ʾ��</summary>
    public string LyricCopyHint => Localization.Get("Lyric_CopyHint");
    /// <summary>#4 �����ǰ 0.5s ��ť��ʾ��</summary>
    public string LyricOffsetDownHint => Localization.Get("Lyric_OffsetDownHint");
    /// <summary>#4 ����Ӻ� 0.5s ��ť��ʾ��</summary>
    public string LyricOffsetUpHint => Localization.Get("Lyric_OffsetUpHint");
    // ���� ����Դһ���л���1.2.0������
    /// <summary>�����Դ�л���ť�ı����硸��ʣ��Զ�������</summary>
    public string LyricsSourceButtonText => Localization.Get("Lyrics_SourceButton") + "��" + CurrentLyricsSourceDisplay;
    /// <summary>��ǰ��ѡ�����Դ���������ֵ����</summary>
    public string CurrentLyricsSource => _settings.Current.LyricsPreferredSource ?? "Auto";
    /// <summary>��ǰ��ѡ�����Դ����ʾ����</summary>
    public string CurrentLyricsSourceDisplay => CurrentLyricsSource.ToUpperInvariant() switch
    {
        "LOCAL" => Localization.Get("Lyrics_SrcLocal"),
        "AMLL" => Localization.Get("Lyrics_SrcAmll"),
        "CIDER" => Localization.Get("Lyrics_SrcCider"),
        "ONLINE" => Localization.Get("Lyrics_SrcOnline"),
        _ => Localization.Get("Lyrics_SrcAuto"),
    };
    /// <summary>��Դ�л���ť��ʾ��</summary>
    public string LyricsSourceButtonHint => Localization.Get("Lyrics_SourceHint");

    /// <summary>ѭ���л������Դ���Զ� �� ���� �� AMLL �� Cider �� ���� �� �Զ����������¼��ص�ǰ������ʡ�</summary>
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
                _lyricsKey = string.Empty; // ǿ�����¼��ظ��
                await LoadLyricsAsync(snapshot);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CycleLyricsSource failed: {ex.Message}");
        }
    }

    /// <summary>���̬���ֿ���OK�ѵ����ַ�����</summary>
    private double _compactHighlightFraction;
    public double CompactHighlightFraction { get => _compactHighlightFraction; private set => Set(ref _compactHighlightFraction, value); }

    /// <summary>��ǰ����OK�����루�����ƫ�ƣ����������ָ������ؼ���ǽ�� 60fps �����ƽ�����</summary>
    private double _karaokePositionSeconds;
    public double KaraokePositionSeconds { get => _karaokePositionSeconds; private set => Set(ref _karaokePositionSeconds, value); }

    /// <summary>��ǰ������ʱ���ᣨAMLL TTML���������ֿ���OK�ؼ�ʹ�ã�����ռ��ϡ�</summary>
    private IReadOnlyList<TtmlWord> _currentLyricWords = Array.Empty<TtmlWord>();
    public IReadOnlyList<TtmlWord> CurrentLyricWords { get => _currentLyricWords; private set => Set(ref _currentLyricWords, value); }

    // ���� Idle widgets����ý��ʱ�����������������������������������������������������������
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

    /// <summary>��ս������һ��˳�������</summary>
    /// <summary>��ս������һ��˳�������Kind=�����ʶ��Icon=��ʾͼ���ַ���֧���û����ƣ���</summary>
    public sealed record IslandComponent(string Kind, string Icon); // "Time" | "Weather" | "Song"
    // ����������������/����/����/���/����������ֻ�ڲ���ʱ��ʾ���̶�����
    public bool ShowCover => HasMedia;
    public bool ShowTitle => HasMedia;
    public bool ShowArtist => HasMedia;
    public bool ShowLyrics => HasMedia;
    public bool ShowCompactProgress => HasMedia;

    // ʱ��/�����������벥�ŷֱ𰴹�ѡ��ʾ
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
    public bool ShowIdleClipboard => HasMedia ? _settings.Current.Components.ClipboardWhenPlaying : _settings.Current.Components.ClipboardWhenIdle;
    public bool ShowIdleTodo => HasMedia ? _settings.Current.Components.TodoWhenPlaying : _settings.Current.Components.TodoWhenIdle;
    public bool ShowIdleTimer => HasMedia ? _settings.Current.Components.TimerWhenPlaying : _settings.Current.Components.TimerWhenIdle;
    public bool ShowIdleSchedule => HasMedia ? _settings.Current.Components.ScheduleWhenPlaying : _settings.Current.Components.ScheduleWhenIdle;
    public bool ShowIdleHoliday => HasMedia ? _settings.Current.Components.HolidayWhenPlaying : _settings.Current.Components.HolidayWhenIdle;
    public bool ShowIdleMeeting => HasMedia ? _settings.Current.Components.MeetingWhenPlaying : _settings.Current.Components.MeetingWhenIdle;
    public bool ShowIdleDisk => HasMedia ? _settings.Current.Components.DiskWhenPlaying : _settings.Current.Components.DiskWhenIdle;
    public bool ShowIdleInputMethod => HasMedia ? _settings.Current.Components.InputMethodWhenPlaying : _settings.Current.Components.InputMethodWhenIdle;
    public bool ShowIdleQuickToggles => HasMedia ? _settings.Current.Components.QuickTogglesWhenPlaying : _settings.Current.Components.QuickTogglesWhenIdle;
    public bool ShowAnyWidget => ShowIdleTime || ShowIdleWeather || ShowIdleDate
        || ShowIdleCpu || ShowIdleRam || ShowIdleGpu || ShowIdleNet || ShowIdleBattery || ShowIdleMic || ShowIdleCam
        || ShowIdleVolume || ShowIdleCapsLock || ShowIdleClipboard || ShowIdleTodo
        || ShowIdleTimer || ShowIdleSchedule || ShowIdleHoliday || ShowIdleMeeting || ShowIdleDisk || ShowIdleInputMethod || ShowIdleQuickToggles;

    // ���� Ч�ʹ��� / ���� �ı� ����
    /// <summary>����ǿ�ȣ�0..1������ AudioWaveService ʵʱ�ɼ�/ģ�⣬UI ��ѯ��</summary>
    public double WaveLevel => _wave.Level;
    public string VolumeText => HasVolumeControl ? $"{(_volume * 100):0}%" : string.Empty;

    private string _capsLockText = string.Empty;
    /// <summary>����ָʾ���ı����硸Caps ������������ N ����Զ���ա�</summary>
    public string CapsLockText { get => _capsLockText; private set => Set(ref _capsLockText, value); }
    private string _screenshotStatusText = string.Empty;
    /// <summary>��ͼ��ʱָʾ�ı����� PrintScreen ����֣�������Զ���ʧ����</summary>
    public string ScreenshotStatusText { get => _screenshotStatusText; private set => Set(ref _screenshotStatusText, value); }
    private int _screenshotSecondsLeft;   // ��ͼָʾʣ����ʾ�������� _widgetTimer ÿ��ݼ���
    private string _recordingText = string.Empty;
    private int _volumeTempGen;              // ����ָʾ���ʼ���������ȡ�����ڵĵ�������
    private string _volumeTempText = string.Empty;
    /// <summary>����/������ʱ�ϵ��ı���������������֣�������Զ���ʧ����</summary>
    public string VolumeTempText { get => _volumeTempText; private set => Set(ref _volumeTempText, value); }

    private double _volumeTempPercent;
    /// <summary>������ʱָʾ�Ľ�����ֵ��0..1���� VolumeTempText ͬ��ˢ�£���</summary>
    public double VolumeTempPercent { get => _volumeTempPercent; private set => Set(ref _volumeTempPercent, value); }

    private bool _volumeTempFading;
    /// <summary>����ָʾ��ʧǰ�ĵ�����־��True ʱ�����˳���������������б��Ƴ�����</summary>
    public bool VolumeTempFading
    {
        get => _volumeTempFading;
        private set { if (_volumeTempFading == value) return; _volumeTempFading = value; OnPropertyChanged(nameof(VolumeTempFading)); }
    }
    private string _usageMergeText = string.Empty;
    /// <summary>��ʹ���С��ϲ������ı�����˷�/����ͷ/����/¼���ϲ�Ϊһ��״̬���ң���</summary>
    public string UsageMergeText { get => _usageMergeText; private set => Set(ref _usageMergeText, value); }
    private string _fileCopyText = string.Empty;
    /// <summary>�ļ�����/�ƶ��������ı���</summary>
    public string FileCopyText { get => _fileCopyText; private set => Set(ref _fileCopyText, value); }
    private string _downloadText = string.Empty;
    /// <summary>���ؽ������ı���</summary>
    public string DownloadText { get => _downloadText; private set => Set(ref _downloadText, value); }
    /// <summary>¼��������ָʾ���硸¼���� �� OBS����ֹͣ¼�ƺ���գ���</summary>
    public string RecordingText { get => _recordingText; private set => Set(ref _recordingText, value); }
    private string _clipboardSummary = string.Empty;
    public string ClipboardSummary { get => _clipboardSummary; private set => Set(ref _clipboardSummary, value); }
    private string _todoSummary = string.Empty;
    public string TodoSummary { get => _todoSummary; private set => Set(ref _todoSummary, value); }
    private string _timerText = string.Empty;
    public string TimerText { get => _timerText; private set => Set(ref _timerText, value); }
    private bool _timerPaused;
    /// <summary>�������Ƿ�����̬ͣ�������ʾ ? ͼ�꣩��</summary>
    public bool TimerPaused { get => _timerPaused; private set => Set(ref _timerPaused, value); }
    private string _timerToolTip = string.Empty;
    /// <summary>�����������ͣ��ʾ�������ͣ/��������</summary>
    public string TimerToolTip { get => _timerToolTip; private set => Set(ref _timerToolTip, value); }
    private string _scheduleSummary = string.Empty;
    public string ScheduleSummary { get => _scheduleSummary; private set => Set(ref _scheduleSummary, value); }

    private IReadOnlyList<IslandComponent> _compactItems = Array.Empty<IslandComponent>();
    public IReadOnlyList<IslandComponent> CompactItems { get => _compactItems; private set => Set(ref _compactItems, value); }

    // ���� �ļ���תվ�����ļ��ϵ� �� �����ʾ �� �ϳ�������Ӧ�� / ��Դ����������������
    /// <summary>��תվ�е��ļ��Name=��ʾ���ļ�����Path=ԭʼ����·�����ϳ�ʱ��ԭ����</summary>
    public sealed record FileTransferItem(string Name, string Path);

    private readonly List<FileTransferItem> _fileTransferItems = new();
    /// <summary>��ǰ��ת�ļ��б��ֻ����·�����ã��������ļ�����</summary>
    public IReadOnlyList<FileTransferItem> FileTransferItems => _fileTransferItems;

    /// <summary>�Ƿ�������ת�ļ������� FileTransfer �������/��ʧ����</summary>
    public bool HasFileTransfer => _fileTransferItems.Count > 0;

    private string _fileTransferSummary = string.Empty;
    /// <summary>�����ʾ���֣����ļ�Ϊ�ļ��������ļ�Ϊ���׸��ļ��� +N����</summary>
    public string FileTransferSummary { get => _fileTransferSummary; private set => Set(ref _fileTransferSummary, value); }

    private string _fileTransferToolTip = string.Empty;
    /// <summary>��ͣ��ʾ���г�ȫ����ת�ļ�����·����</summary>
    public string FileTransferToolTip { get => _fileTransferToolTip; private set => Set(ref _fileTransferToolTip, value); }

    /// <summary>��������ť��ʾ��</summary>
    public string FileTransferRemoveTip => Localization.Get("FileTransfer_Remove");

    /// <summary>��������ļ�������תվ��ȥ�ء����� 20 ��������ˢ�����˳��</summary>
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

    /// <summary>�����תվ�������֮��ʧ����</summary>
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

    /// <summary>�� WidgetOrder �ؽ���ս������˳�򣨲���ʱ��������Ϣ������ʱȥ�����</summary>
    private void RebuildCompactItems()
    {
        // �ֲ������������ Kind ������ʾͼ�꣨�û��Զ������ȣ�����Ĭ�����Σ�
        IslandComponent I(string kind) => new(kind, ComponentIcons.Resolve(kind, _settings.Current.ComponentIcons));

        // ��ȡ˳�򣬲�����ȱʧ����֪��������ݾ�������ֻ�� Time,Weather �������
        // ע�⣺��֪����б���븲���������з�֧�õ��� key������������ʹ��ѡҲ��Զ������ʾ��
        var keys = (_settings.Current.WidgetOrder ?? "Time,Weather,Song")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var known in new[] { "Time", "Weather", "Date", "Cpu", "Ram", "Gpu", "Mic", "Cam", "Net", "Battery", "Song", "Volume", "CapsLock", "ScreenCap", "Recording", "VolumeTemp", "Usage", "FileCopy", "Download", "Clipboard", "Todo", "Timer", "Schedule", "Holiday", "Meeting", "Disk", "InputMethod", "QuickToggles", "FileTransfer" })
            if (!keys.Contains(known)) keys.Add(known);

        // ��ʹ���С��ϲ����ң��ѹ�ѡ�� Mic/Cam/Meeting/Recording �ϲ�Ϊ����״̬���ң�Ĭ�Ϲرգ�
        var mergeItems = _settings.Current.UsageMergeItems ?? new List<string>();
        var mergeEnabled = _settings.Current.UsageMergeEnabled && mergeItems.Count > 0;
        var activeMerge = new List<string>();
        if (mergeEnabled)
        {
            if (mergeItems.Contains("Mic") && ShowIdleMic && !string.IsNullOrEmpty(MicText)) activeMerge.Add(Localization.Get("Comp_Mic"));
            if (mergeItems.Contains("Cam") && ShowIdleCam && !string.IsNullOrEmpty(CamText)) activeMerge.Add(Localization.Get("Comp_Cam"));
            if (mergeItems.Contains("Meeting") && ShowIdleMeeting && !string.IsNullOrEmpty(MeetingText)) activeMerge.Add(Localization.Get("Comp_Meeting"));
            if (mergeItems.Contains("Recording") && _settings.Current.ScreenCaptureNotifyEnabled && !string.IsNullOrEmpty(RecordingText)) activeMerge.Add(Localization.Get("ScreenCap_IslandRecording"));
        }
        var newUsageText = mergeEnabled && activeMerge.Count > 0
            ? Localization.Get("Comp_Usage") + " �� " + string.Join(" �� ", activeMerge)
            : string.Empty;
        if (UsageMergeText != newUsageText) UsageMergeText = newUsageText;

        var items = new List<IslandComponent>();
        var usageInserted = false;
        foreach (var key in keys)
        {
            // �ϲ�ģʽ������ϲ�����ٵ�����ʾ����ʹ���С����ҷ��ڵ�һ������ϲ����λ��
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
            else if (key == "ScreenCap" && _settings.Current.ScreenCaptureNotifyEnabled && !string.IsNullOrEmpty(ScreenshotStatusText)) items.Add(I("ScreenCap"));
            else if (key == "Recording" && _settings.Current.ScreenCaptureNotifyEnabled && !string.IsNullOrEmpty(RecordingText)) items.Add(I("Recording"));
            else if (key == "VolumeTemp" && _settings.Current.VolumeTempIndicatorEnabled && !string.IsNullOrEmpty(VolumeTempText)) items.Add(I("VolumeTemp"));
            else if (key == "FileCopy" && _settings.Current.FileCopyNotifyEnabled && !string.IsNullOrEmpty(FileCopyText)) items.Add(I("FileCopy"));
            else if (key == "Download" && _settings.Current.DownloadProgressEnabled && !string.IsNullOrEmpty(DownloadText)) items.Add(I("Download"));
            else if (key == "Clipboard" && !HasMedia && ShowIdleClipboard && !string.IsNullOrEmpty(ClipboardSummary)) items.Add(I("Clipboard"));
            else if (key == "Todo" && ShowIdleTodo && !string.IsNullOrEmpty(TodoSummary)) items.Add(I("Todo"));
            else if (key == "Timer" && ShowIdleTimer && !string.IsNullOrEmpty(TimerText)) items.Add(I("Timer"));
            else if (key == "Schedule" && ShowIdleSchedule && !string.IsNullOrEmpty(ScheduleSummary)) items.Add(I("Schedule"));
            else if (key == "Holiday" && ShowIdleHoliday && !string.IsNullOrEmpty(HolidayText)) items.Add(I("Holiday"));
            else if (key == "Meeting" && ShowIdleMeeting && !string.IsNullOrEmpty(MeetingText)) items.Add(I("Meeting"));
            else if (key == "Disk" && ShowIdleDisk) items.Add(I("Disk"));
            else if (key == "InputMethod" && ShowIdleInputMethod) items.Add(I("InputMethod"));
            else if (key == "QuickToggles" && ShowIdleQuickToggles) items.Add(I("QuickToggles"));
            else if (key == "FileTransfer" && HasFileTransfer) items.Add(I("FileTransfer"));
        }

        // ����δ�仯ʱ���ؽ�������ÿ�����գ�ÿ�룩���ش������������˸
        if (_compactItems.Count == items.Count
            && _compactItems.Select(i => i.Kind).SequenceEqual(items.Select(i => i.Kind)))
            return;
        CompactItems = items;
    }


    private int _statsTick;
    private float? _cpuValue;
    private long _lastNetDownBytes;
    private long _lastNetUpBytes;
    private DateTime _lastNetTime = DateTime.UtcNow;
    // �����������ߣ����λ������ 32 ���������ʣ�KB/s��
    private const int NetCurveSamples = 32;
    private readonly double[] _netCurveSamples = new double[NetCurveSamples];
    private int _netCurvePos;
    // ���ܼ���������ʵ�����½�ʵ���ĵ�һ�� NextValue() �᷵�� 0������ CPU ��ʾ����
    private readonly System.Diagnostics.PerformanceCounter? _cpuCounter;
    private readonly System.Diagnostics.PerformanceCounter? _ramCounter;
    // GPU ռ�ã���ȡ��GPU Engine������������ 3D ����ʵ������������ʣ�ʵ���������ͣ�仯���趨��ˢ�£�
    private readonly Dictionary<string, System.Diagnostics.PerformanceCounter> _gpuCounters = new();
    private bool _gpuProbed;
    private bool _gpuAvailable;
    private System.Diagnostics.PerformanceCounterCategory? _gpuCategory;

    private System.Diagnostics.PerformanceCounter? CreateCounter(string cat, string name, string? inst)
    {
        try { return inst is null ? new System.Diagnostics.PerformanceCounter(cat, name) : new System.Diagnostics.PerformanceCounter(cat, name, inst); }
        catch { return null; }
    }

    /// <summary>ˢ�� GPU ������ʵ�������ص�ǰ��� 3D ���������ʣ�%���������� GPU Engine ����������ʵ��Ԥ��ʱ���� null��</summary>
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

            // �������˳����̵�����ʵ��
            foreach (var k in _gpuCounters.Keys.ToList())
                if (!names.Contains(k, StringComparer.OrdinalIgnoreCase))
                {
                    try { _gpuCounters[k].Dispose(); } catch { }
                    _gpuCounters.Remove(k);
                }

            // �½�ʵ�����״� NextValue() ���� 0����Ԥ�Ȳ���ʾ
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

    /// <summary>���� CPU/�ڴ�/����/���/���� ��ϵͳ״̬�ı���ÿ��һ�Σ�UI �̣߳���</summary>
    private void UpdateSystemStats()
    {
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            var hasBattery = ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
            var battery = ps.BatteryLifePercent * 100f;
            if (hasBattery)
            {
                // ���Ԥ��ʣ��ʱ�䣨�� �� ʱ:�֣������/δ֪ʱ����ʾ�ٷֱȣ�
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

            // �͵������ѣ�ÿ�������������һ�Σ�
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

            // �͵�����פָʾ��1.2.4�������� �� ��ֵ+2 ��δ�ӵ�Դʱ�����Ͻǽ��ҳ�פ��ʾ�����ٷֱȣ�
            // �����ʵʱˢ�£����ϵ�Դ�������������ֵ+5 ���ϲ���ʧ����һ���Ե������ѽ�����ظ���֪ͨ��
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

            // ��ʼ������ѣ���Դ����˲�䴥��һ�Σ�iOS ����ϵ���Ƭ���γ���Դ��λ���ٴδ�����
            if (hasBattery && _settings.Current.ChargedNotifyEnabled)
            {
                if (!_chargingNotified && acOnline)
                {
                    _chargingNotified = true;
                    ChargingStartedRequested?.Invoke((int)Math.Round(battery));
                }
                if (!acOnline) _chargingNotified = false;
            }

            // ���������ѣ�ÿ�������������һ�Σ����ӵ�Դ�ҵ����ﵽ��ֵʱ��
            if (hasBattery && _settings.Current.ChargedNotifyEnabled && _settings.Current.ChargedThreshold > 0)
            {
                if (!_chargedNotified && acOnline && battery >= _settings.Current.ChargedThreshold)
                {
                    _chargedNotified = true;
                    ChargedRequested?.Invoke((int)Math.Round(battery));
                }
                if (!acOnline || battery < _settings.Current.ChargedThreshold - 5) _chargedNotified = false;
            }

            // ����ʣ��ռ䣨ϵͳ�̣��������������ѿ���ʱ��ѯ����ѯ����С��
            if (ShowIdleDisk || _settings.Current.DiskAlertEnabled)
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
                            // ʣ��ռ������ֵ���ѣ��ָ���λ��ÿ������ֻ����һ�Σ�
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

            // CPU / �ڴ棨ÿ 2 �룻������ʵ�����ã������״β���Ϊ 0��
            if (++_statsTick % 2 == 0)
            {
                if (ShowIdleCpu) // ��̨������CPU ���δ��ѡʱ������
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
                // GPU���� CPU ͬ�����������ʵ����Ԥ�ȣ������ײ���ʾ 0%��
                if (ShowIdleGpu)
                {
                    var gpu = SampleGpuUsage();
                    GpuText = gpu.HasValue ? $"{gpu.Value:0}%" : "--";
                }
                // ��˷�/����ͷռ�ã���˽ע����Start > Stop ��ʾռ���У�����ѡʱ��ѯ��
                if (ShowIdleMic || ShowIdleCam)
                {
                    var (mic, cam) = PrivacyDeviceMonitor.GetUsage();
                    if (ShowIdleMic) MicText = mic ? Localization.Get("Comp_Mic") : string.Empty;
                    if (ShowIdleCam) CamText = cam ? Localization.Get("Comp_Cam") : string.Empty;
                    RebuildCompactItems();
                }
                if (ShowIdleRam) // ��̨�������ڴ����δ��ѡʱ������
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

            // �����ٶȣ�ÿ�룩���������� + �������� + �������ߣ������������ѡʱ������
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

            // �ڼ��յ���ʱ������ѡ��ʾʱ���㣬�����أ�
            if (ShowIdleHoliday)
            {
                var (hName, hDays) = NextHolidayInfo();
                HolidayText = hDays < 0 ? string.Empty : hDays == 0 ? $"���� {hName}" : $"{hName} {hDays} ���";
            }
            else if (HolidayText.Length > 0) HolidayText = string.Empty;
        }
        catch { /* �������ܼ�����/�����쳣 */ }
    }

    private static string FormatKbs(double kbs) =>
        kbs >= 1024 ? $"{kbs / 1024:0.0} MB/s" : $"{kbs:0} KB/s";

    /// <summary>��һ�����в������뻷�λ��壬�������ؽ����ߵ㴮�������������ѡʱ�������壩��</summary>
    private void PushNetSample(double downKbs)
    {
        _netCurveSamples[_netCurvePos] = downKbs;
        _netCurvePos = (_netCurvePos + 1) % NetCurveSamples;
        if (_settings.Current.NetCurveEnabled && ShowIdleNet)
            NetCurvePoints = BuildNetCurvePoints();
    }

    /// <summary>���� 32 �������������ߵ㴮���Զ�����ֵ���ţ���</summary>
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

    // ����ڷ�˳��WidgetOrder �е��±���������У�
    public double WidgetTimeFontSize => HasMedia ? 12.5 : 14; // ����ʱ���ֺ����У������ͻأ

    // ϵͳ״̬/��������ı�
    private string _dateText = string.Empty;
    public string DateText { get => _dateText; private set => Set(ref _dateText, value); }
    private string _cpuText = string.Empty;
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    private string _ramText = string.Empty;
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    private string _gpuText = string.Empty;
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    private string _micText = string.Empty;
    /// <summary>��˷�ռ��ָʾ�ı���ռ������ʾ����˷硹������Ϊ�գ������֮����/��ʧ����</summary>
    public string MicText { get => _micText; private set => Set(ref _micText, value); }
    private string _camText = string.Empty;
    /// <summary>����ͷռ��ָʾ�ı���ռ������ʾ������ͷ��������Ϊ�գ���</summary>
    public string CamText { get => _camText; private set => Set(ref _camText, value); }
    private string _netText = string.Empty;
    public string NetText { get => _netText; private set => Set(ref _netText, value); }
    private string _netTextUp = string.Empty;
    public string NetTextUp { get => _netTextUp; private set => Set(ref _netTextUp, value); }
    private string _netCurvePoints = string.Empty;
    public string NetCurvePoints { get => _netCurvePoints; private set => Set(ref _netCurvePoints, value); }
    private string _batteryText = string.Empty;
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }

    // �͵�����פ������ɫ��1.2.4��iOS ��񣺡�10% �� / ����ȣ�����Ϊͬɫ��͸���ȣ�
    private static readonly SolidColorBrush _lowBatteryRedBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush _lowBatteryOrangeBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A));
    private static readonly SolidColorBrush _lowBatteryRedBgBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0x45, 0x3A));
    private static readonly SolidColorBrush _lowBatteryOrangeBgBrush = FrozenBrush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0x9F, 0x0A));
    private static SolidColorBrush FrozenBrush(System.Windows.Media.Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private bool _showLowBatteryBadge;
    /// <summary>�͵�����פָʾ�Ƿ���ʾ������������ֵ��δ�ӵ�ԴʱΪ true����</summary>
    public bool ShowLowBatteryBadge { get => _showLowBatteryBadge; private set => Set(ref _showLowBatteryBadge, value); }
    private string _lowBatteryBadgeText = string.Empty;
    /// <summary>�͵������ҵ����ı����硸23%������</summary>
    public string LowBatteryBadgeText { get => _lowBatteryBadgeText; private set => Set(ref _lowBatteryBadgeText, value); }
    private SolidColorBrush _lowBatteryBadgeBrush = _lowBatteryRedBrush;
    /// <summary>�͵�������ǰ��/ͼ��ɫ����10% �죬����ȣ������ʵʱ�仯����</summary>
    public SolidColorBrush LowBatteryBadgeBrush { get => _lowBatteryBadgeBrush; private set => Set(ref _lowBatteryBadgeBrush, value); }
    private SolidColorBrush _lowBatteryBadgeBackground = _lowBatteryRedBgBrush;
    /// <summary>�͵������ұ�����ͬɫ��͸���ȣ������ʵʱ�仯����</summary>
    public SolidColorBrush LowBatteryBadgeBackground { get => _lowBatteryBadgeBackground; private set => Set(ref _lowBatteryBadgeBackground, value); }
    private string _inputMethodText = string.Empty;
    /// <summary>���뷨״̬�ı����硸�� �� ΢��ƴ�������</summary>
    public string InputMethodText { get => _inputMethodText; private set => Set(ref _inputMethodText, value); }
    /// <summary>���뷨�����ʾ������л���/Ӣ����</summary>
    public string InputMethodHint => Localization.Get("Comp_InputMethodHint");
    private string _quickWifiText = string.Empty;
    /// <summary>��ݿ��أ�WiFi ״̬�ı����硸WiFi ��������</summary>
    public string QuickWifiText { get => _quickWifiText; private set => Set(ref _quickWifiText, value); }
    private string _quickBtText = string.Empty;
    /// <summary>��ݿ��أ�����״̬�ı���</summary>
    public string QuickBtText { get => _quickBtText; private set => Set(ref _quickBtText, value); }
    private string _quickNightText = string.Empty;
    /// <summary>��ݿ��أ�ҹ��ģʽ״̬�ı���</summary>
    public string QuickNightText { get => _quickNightText; private set => Set(ref _quickNightText, value); }
    private string _quickMuteText = string.Empty;
    /// <summary>��ݿ��أ�����״̬�ı���</summary>
    public string QuickMuteText { get => _quickMuteText; private set => Set(ref _quickMuteText, value); }
    /// <summary>��ݿ��������ʾ����������ؼ�ʱ�л�����</summary>
    public string QuickTogglesHint => Localization.Get("Comp_QuickTogglesHint");
    private string _diskText = string.Empty;
    /// <summary>ϵͳ��ʣ��ռ��ı����硸C: 385GB / 510GB�����޿����̷�ʱΪ�գ���</summary>
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }

    /// <summary>�и�ʱ���������������������֣������� Now Playing �����</summary>
    public event Action<string, string>? NowPlayingRequested;
    /// <summary>�͵��������������������ٷֱȣ���</summary>
    public event Action<int>? LowBatteryRequested;
    /// <summary>�����ɴ����������������ٷֱȣ���</summary>
    public event Action<int>? ChargedRequested;
    /// <summary>��ʼ��紥���������������ٷֱȣ���</summary>
    public event Action<int>? ChargingStartedRequested;
    private bool _lowBatteryNotified;
    private bool _chargedNotified;
    private bool _chargingNotified;
    /// <summary>����ʣ�಻�㴥����������ʣ�� GB����</summary>
    public event Action<int>? DiskLowRequested;
    private bool _diskAlertNotified;
    private string _holidayText = string.Empty;
    public string HolidayText { get => _holidayText; private set => Set(ref _holidayText, value); }
    private string _meetingText = string.Empty;
    /// <summary>������״̬�ı����硸������ �� Microsoft Teams�����ǻ���ʱΪ�գ������֮��ʧ����</summary>
    public string MeetingText { get => _meetingText; private set => Set(ref _meetingText, value); }

    // ���� ũ�� / �����������ؼ��㣬ChineseLunisolarCalendar + �������ƹ�ʽ��������������
    private static readonly string[] LunarMonths =
        { "����", "����", "����", "����", "����", "����", "����", "����", "����", "ʮ��", "����", "����" };
    private static readonly string[] LunarDays =
        { "��һ", "����", "����", "����", "����", "����", "����", "����", "����", "��ʮ",
          "ʮһ", "ʮ��", "ʮ��", "ʮ��", "ʮ��", "ʮ��", "ʮ��", "ʮ��", "ʮ��", "��ʮ",
          "إһ", "إ��", "إ��", "إ��", "إ��", "إ��", "إ��", "إ��", "إ��", "��ʮ" };
    private static readonly string[] SolarTermNames =
        { "С��", "��", "����", "��ˮ", "����", "����", "����", "����", "����", "С��", "â��", "����",
          "С��", "����", "����", "����", "��¶", "���", "��¶", "˪��", "����", "Сѩ", "��ѩ", "����" };
    // 24 �������ڽ��ƹ�ʽ������ƽ������1900-2100 ��� ��1 �죩
    private static readonly double[] SolarTermBase =
        { 0, 21208, 42467, 63836, 85337, 107014, 128867, 150921, 173149, 195551, 218072, 240693,
          263343, 285989, 308563, 331033, 353350, 375494, 397447, 419210, 440795, 462224, 483532, 504758 };

    /// <summary>��������ı������� + ũ�����գ�+ ���ս�������</summary>
    private string FormatDateText(DateTime now)
    {
        var baseText = now.ToString("M��d�� ddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        if (!_settings.Current.ShowLunarOnDate) return baseText;
        try
        {
            var cal = new System.Globalization.ChineseLunisolarCalendar();
            var year = cal.GetYear(now);
            var monthVal = cal.GetMonth(now);
            var leap = cal.IsLeapMonth(year, monthVal);
            var monthNum = leap ? monthVal - 1 : monthVal;
            var lunar = (leap ? "��" : "") + LunarMonths[Math.Clamp(monthNum, 1, 12) - 1]
                + LunarDays[Math.Clamp(cal.GetDayOfMonth(now), 1, 30) - 1];
            var suffix = " ũ��" + lunar;
            var term = SolarTermOf(now);
            if (term.Length > 0) suffix += " �� " + term;
            return baseText + suffix;
        }
        catch { return baseText; } // ����ʧ�����Ž���Ϊ������
    }

    /// <summary>���ս����������ǽ������ؿ��ַ�������</summary>
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

    /// <summary>���ýڼ��ձ�����+����/ũ����������������ز���������������в�����Ŀ����</summary>
    private static readonly (string Name, int Year, int Month, int Day)[] HolidayTable =
    {
        ("Ԫ��", 2026, 1, 1), ("����", 2026, 2, 17), ("����", 2026, 4, 5), ("�Ͷ���", 2026, 5, 1),
        ("����", 2026, 6, 19), ("����", 2026, 9, 25), ("����", 2026, 10, 1),
        ("Ԫ��", 2027, 1, 1), ("����", 2027, 2, 6), ("����", 2027, 4, 5), ("�Ͷ���", 2027, 5, 1),
        ("����", 2027, 6, 9), ("����", 2027, 9, 15), ("����", 2027, 10, 1),
    };

    /// <summary>��һ���ڼ���������ʣ������������Ϊ 0���Ҳ������ؿգ���</summary>
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

    // ���� �ϵ����ͣ�������������͵��鶯����������������������������������������
    public IReadOnlyList<IslandPush> ActivePushes => _pushes;

    public IslandPush? ActivePush
    {
        get => _activePush;
        private set
        {
            if (!Set(ref _activePush, value)) return;
            OnPropertyChanged(nameof(HasActivePush));
            OnPropertyChanged(nameof(ActivePushIcon));
            OnPropertyChanged(nameof(ActivePushTitle));
            OnPropertyChanged(nameof(ActivePushSubtitle));
            OnPropertyChanged(nameof(ActivePushHasSubtitle));
            OnPropertyChanged(nameof(ActivePushBody));
            OnPropertyChanged(nameof(ActivePushHasBody));
            OnPropertyChanged(nameof(ActivePushSummary));
            OnPropertyChanged(nameof(ActivePushHasSummary));
            OnPropertyChanged(nameof(ActivePushHasProgress));
            OnPropertyChanged(nameof(ActivePushProgress));
            OnPropertyChanged(nameof(ActivePushHasImage));
            OnPropertyChanged(nameof(ActivePushImageSource));
            OnPropertyChanged(nameof(ActivePushHasButtons));
            OnPropertyChanged(nameof(ActivePushButtons));
            OnPropertyChanged(nameof(ActivePushHasClick));
            OnPropertyChanged(nameof(ActivePushAccent));
            OnPropertyChanged(nameof(ActivePushTheme));
            OnPropertyChanged(nameof(HasPushInput));
            OnPropertyChanged(nameof(PushInputPlaceholder));
            OnPropertyChanged(nameof(PushInputSubmitLabel));
            OnPropertyChanged(nameof(PushInputValue));
            OnPropertyChanged(nameof(PushInputHasText));
            UpdateVisibility(); // �ϵ���Ƭ��ʾ/��ʧӰ���鶯���ɼ���
        }
    }

    public bool HasActivePush => ActivePush is not null;
    public string ActivePushIcon => string.IsNullOrEmpty(ActivePush?.Icon) ? "\uE7F4" : ActivePush!.Icon;
    public string ActivePushTitle => ActivePush?.Title ?? string.Empty;
    public string ActivePushSubtitle => ActivePush?.Subtitle ?? string.Empty;
    public bool ActivePushHasSubtitle => !string.IsNullOrEmpty(ActivePush?.Subtitle);
    public string ActivePushBody => ActivePush?.Body ?? string.Empty;
    public bool ActivePushHasBody => !string.IsNullOrEmpty(ActivePush?.Body);
    public string ActivePushSummary => !string.IsNullOrEmpty(ActivePush?.Subtitle) ? ActivePush!.Subtitle! : (ActivePush?.Body ?? string.Empty);
    public bool ActivePushHasSummary => !string.IsNullOrEmpty(ActivePushSummary);
    public bool ActivePushHasProgress => ActivePush?.EffectiveProgress is not null;
    public double ActivePushProgress => Math.Clamp(ActivePush?.EffectiveProgress ?? 0, 0, 1);

    // ���� �ϵ������v4������������������ input ʱ����Ƭ����ʾ����� + �ύ��ť ����
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

    /// <summary>�ύ�ϵ���������ݣ������ͷ����õ� action ִ�У�Ĭ�� notify �ش������ύ����������</summary>
    public void SubmitPushInput()
    {
        if (ActivePush?.Input is not IslandPushInput input) return;
        var value = PushInputValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(input.Value)) return; // ����������Ĭ��ֵʱ����
        var b = new IslandPushButton
        {
            Label = string.IsNullOrEmpty(input.SubmitLabel) ? Localization.Get("Push_InputSubmit") : input.SubmitLabel,
            Action = string.IsNullOrEmpty(input.Action) ? "notify" : input.Action,
            Value = string.IsNullOrWhiteSpace(value) ? input.Value : value,
        };
        ExecutePushAction(b);
        PushInputValue = string.Empty;
    }

    /// <summary>�ϵ�����ͼƬ��v3����data URI �� http(s) ���ӡ�</summary>
    public bool ActivePushHasImage => !string.IsNullOrEmpty(ActivePush?.Image);

    /// <summary>�ϵ�����ͼƬԴ��data URI ����Ϊ����λͼ��http(s) ֱ�Ӽ��ء�</summary>
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
    /// <summary>�������������click���Ƿ������á�</summary>
    public bool ActivePushHasClick => ActivePush?.Click is not null;
    /// <summary>ǿ��ɫ��#RRGGBB / #AARRGGBB����δ����ʱΪ���ַ������� UI ������Ĭ��ɫ��</summary>
    public string ActivePushAccent => ActivePush?.Accent ?? string.Empty;

    /// <summary>���Ϳ�Ƭ���⣺dark / light / auto��auto ����Ӧ���������⣩��</summary>
    public string ActivePushTheme => ActivePush?.Theme ?? string.Empty;

    /// <summary>�����ı���ȣ�����/ȫ�ǰ� cjkPx��ASCII �� asciiPx��</summary>
    private static double MeasureText(string s, double cjkPx, double asciiPx)
    {
        double w = 0;
        foreach (var ch in s) w += ch > 0x2E7F ? cjkPx : asciiPx;
        return w;
    }

    /// <summary>��������ı��������п�ȣ����з����з��룬ȡ���ֵ�����������Ϳ�Ƭ�������Ӧ��</summary>
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

    /// <summary>����ͼ�꣨�� WMO weather_code ѡ emoji����</summary>
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

    /// <summary>�������������ػ�����</summary>
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

    /// <summary>���ģʽ�����İ���ͼ�� + �¶� + ���� + ���ո�/���¡�</summary>
    private static string FormatWeatherCompact(WeatherInfo w)
    {
        var range = w.Low < w.High ? $" {w.Low:0}/{w.High:0}��" : string.Empty;
        return $"{WeatherIcon(w.Code)} {w.Temperature:0}�� {WeatherDesc(w.Code)}{range}";
    }

    /// <summary>������ʾ������������Ϣ����� / ʪ�� / ���� / ��ˮ / ����ʱ�䣩��</summary>
    private string FormatWeatherDetail(WeatherInfo w)
    {
        var feels = $"{Localization.Get("Weather_Feels")} {w.FeelsLike:0}��";
        var hum = $"{Localization.Get("Weather_Humidity")} {w.Humidity:0}%";
        var wind = $"{Localization.Get("Weather_Wind")} {w.WindSpeed:0}km/h";
        var precip = $"{Localization.Get("Weather_Precip")} {w.Precipitation:0}mm";
        var updated = $"{Localization.Get("Weather_Updated")} {w.Updated}";
        return $"{WeatherDesc(w.Code)} �� {string.Join(" �� ", feels, hum, wind, precip, updated)}";
    }

    /// <summary>�����տ�ȣ�������������ݣ�ʱ��/����/����/ϵͳ״̬/�����������ۼӣ��ȶ��ɿ��������� UI ����ʱ������</summary>
    public double EstimatedCompactWidth
    {
        get
        {
            double w = 4; // ���ڱ߾�
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
                    case "Holiday": w += Math.Min(MeasureText(HolidayText, 11.5, 6.5) + 8, 120); break;
                    case "Meeting": w += Math.Min(MeasureText(MeetingText, 10.5, 6) + 16, 160); break;
                    case "ScreenCap": w += MeasureText(ScreenshotStatusText, 10.5, 6) + 16; break;
                    case "Recording": w += Math.Min(MeasureText(RecordingText, 10.5, 6) + 16, 180); break;
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
                w += 8; // ������ұ߾ࣨģ�� Margin 0,0,8,0 ���ң������ﰴ���߼���
            }
            if (HasMedia) w += 120; // ����/��ͣ + ��һ�� ��ť
            if (HasActivePush) w += PushCompactWidth; // ���ϵ�����ʱ�������Ϳ���ȣ���֤��������㹻
            var maxW = HasActivePush ? Math.Max(800, System.Windows.SystemParameters.WorkArea.Width - 48) : 800;
            return Math.Clamp(w + 4, 260, maxW);
        }
    }

    /// <summary>�����ո߶ȣ�������ʵ�ʸ߶ȼ��㣨����ȡ�ֶ�����ֵ������������׹��󣩡�</summary>
    public double EstimatedCompactHeight
    {
        get
        {
            double contentH = 40; // �������ݸߣ�ʱ��/����/�ϵ�����/�������棩
            if (HasActivePush) contentH = Math.Max(contentH, _settings.Current.SingleLineMode ? 40 : PushCompactHeight);
            if (HasMedia && _settings.Current.ShowMediaInfo && !_settings.Current.SingleLineMode) contentH = Math.Max(contentH, 68);
            return Math.Clamp(contentH + 12, 48, 224); // ���ݸ� + �����ڱ߾�(6+6)���������Ŀɶ��У����޷ſ�� 224
        }
    }

    /// <summary>����չ����ȣ����� 420�����ϵ�����ʱ��չ�����������ݿ�ȣ�����/������/����/��ť������Ӧ��</summary>
    public double EstimatedExpandedWidth
    {
        get
        {
            var w = Math.Max(420, _settings.Current.ExpandedWidth);
            if (HasActivePush) w = Math.Max(w, PushExpandedCardWidth);
            return Math.Clamp(w, 420, 640);
        }
    }

    /// <summary>����չ���߶ȣ����ϵ���Ƭ + �����������ۼӡ�</summary>
    public double EstimatedExpandedHeight
    {
        get
        {
            double h = 24;
            if (HasActivePush)
            {
                h += 96;
                if (!string.IsNullOrEmpty(ActivePushBody)) h += 34;
                if (ActivePushHasButtons) h += 40;
                if (HasPushInput) h += 42;                    // չ��״�������
                if (ActivePushHasProgress) h += 12;
            }
            if (HasMedia)
            {
                if (_settings.Current.ExpandedShowArtTitle) h += 90;
                if (_settings.Current.ExpandedShowProgress) h += 40;
                if (_settings.Current.ExpandedShowControls) h += 42;
                if (_settings.Current.ExpandedShowLyrics) h += 190;
            }
            return Math.Clamp(h, 200, 620);
        }
    }

    /// <summary>�ϵ�����ʱ�Ľ�տ�ȣ�������ʾ��ͼ�� + ���� + ����ժҪ����ժҪ����ʡ�ԣ���Ƚ�ղ�����ſ��鶯����</summary>
    public double PushCompactWidth
    {
        get
        {
            var baseW = Math.Max(_settings.Current.CompactWidth, 240);
            if (ActivePush is null) return baseW;
            double need = 38; // ͼ�� 30 + ��� 8
            need += Math.Min(MeasureText(ActivePush.Title ?? string.Empty, 13, 7), 240); // �������� 240���� XAML MaxWidth һ��
            if (ActivePushHasSummary)
                need += 8 + Math.Min(MeasureText(ActivePushSummary, 11.5, 6.2), 200); // ժҪ�������� 200������ʡ�ԣ��� XAML MaxWidth һ�£�
            return Math.Clamp(need + 48, 240, 520); // +48�������ڱ߾�(12+12) + ����
        }
    }

    /// <summary>�ϵ�����ʱ�Ľ�ո߶ȣ��̶����иߣ�ͼ�� 30 + ��Ƭ�����ڱ߾� 7+7 �� 44���� 2px ���������Զ�ģʽ����ʾ��ա�</summary>
    public double PushCompactHeight
    {
        get
        {
            return 46;
        }
    }

    /// <summary>
    /// <summary>
    /// �ϵ�����չ����Ƭ���������ݿ�ȣ�ͼ��+���� / ������ / ���� / ��ť / ����� ������ߣ����� 560����
    /// ��չ��̬�������Ӧʹ�ã����̬���� PushCompactWidth�����У���
    /// </summary>
    private double PushExpandedCardWidth
    {
        get
        {
            var p = ActivePush;
            if (p is null) return 420;
            double need = 0;
            need = Math.Max(need, 50 + MeasureText(p.Title ?? string.Empty, 15, 8));             // ͼ��40+���10+����
            if (!string.IsNullOrEmpty(p.Subtitle))
                need = Math.Max(need, MeasureTextML(p.Subtitle, 11.5, 6.2));                    // ������
            if (!string.IsNullOrEmpty(p.Body))
                need = Math.Max(need, Math.Min(MeasureTextML(p.Body, 12, 6.5), 560));           // ����
            if (p.Buttons is { Count: > 0 })
            {
                double btnW = 0;
                foreach (var b in p.Buttons) btnW += MeasureText(b.Label ?? string.Empty, 12, 6.5) + 26;
                btnW += (p.Buttons.Count - 1) * 8;
                need = Math.Max(need, btnW);
            }
            if (p.Input is not null) need = Math.Max(need, 300 + 8 + 72);                       // ����� + �ύ��ť
            var scale = Math.Clamp(_settings.Current.FontScale, 0.8, 1.4);
            return Math.Clamp((Math.Min(need, 560) + 56) / scale, 420, 640);
        }
    }

    /// <summary>
    /// ϵͳ�¼��ԡ��鶯����Ƭ����ʽչʾ��iOS ��񣩣��Զ����ڡ����ɶ����볡�������������̡�
    /// �����ϵ����Ͷ��У�ͬ id ���ǣ�������� CheckPushExpiry �Զ����ա�
    /// </summary>
    public void ShowEventCard(string id, string title, string? subtitle, string icon,
        string type = "info", int durationSeconds = 5, string? body = null)
    {
        // ����ģʽ���鶯���¼���Ƭ��չʾ����������չʾ���ϲ����������ֻ���ܿ��أ�
        if (DoNotDisturb.IsActive(_settings.Current)) return;
        // 通知去抖：同类型通知 2 秒内只保留最新一条（避免快速连续触发导致队列堆积）
        if (_settings.Current.NotificationDebounce)
        {
            _lastEventTimes.TryGetValue(type ?? "info", out var lastTime);
            if (DateTime.Now - lastTime < TimeSpan.FromSeconds(2))
            {
                AppLogger.Info($"Notification debounced: {title} (type={type})");
                return;
            }
            _lastEventTimes[type ?? "info"] = DateTime.Now;
        }
        var dur = Math.Max(2, durationSeconds);

        // ֪ͨ��ʷ����¼ϵͳ�¼���չ����Ƭ�ײ��ؿ��������ÿ���/�������޿��ƣ�
        try
        {
            if (_settings.Current.NotificationHistoryEnabled && !string.IsNullOrWhiteSpace(title))
            {
                _notificationHistory.Insert(0, new EventHistoryItem
                {
                    Id = "sys:" + id,
                    Title = title,
                    Subtitle = subtitle,
                    Body = body ?? string.Empty,
                    Icon = icon,
                    Type = type,
                    TimeUtc = DateTime.Now,
                    TimeText = DateTime.Now.ToString("HH:mm"),
                });
                var max = Math.Max(1, _settings.Current.NotificationHistoryMax);
                while (_notificationHistory.Count > max) _notificationHistory.RemoveAt(_notificationHistory.Count - 1);
            }
        }
        catch (Exception ex) { AppLogger.Warn($"Record notification history failed: {ex.Message}"); }
        PushIsland(new IslandPush
        {
            Id = "sys:" + id,
            Title = title,
            Subtitle = subtitle ?? string.Empty,
            Body = body ?? string.Empty,
            Icon = icon,
            Type = type,
            Priority = "high", // ϵͳ�¼���������ͨ����������չʾ
            DurationSeconds = dur,
            ExpiresAt = DateTime.UtcNow.AddSeconds(dur),
        });
    }

    /// <summary>
    /// �����豸����/�Ͽ���iOS ����¼���Ƭ��1.2.4����
    /// ���� = ������ / �ѶϿ��������� = �豸������������ʱ���ӡ��� ���� xx%����������ֻ��ʾ�豸������
    /// �����ϵ����Ͷ��У������볡 + �ߴ�����Ӧ�����Զ���Ч���κ��쳣���������������̡�
    /// </summary>
    public void ShowDeviceEvent(string id, bool connected, string deviceName, int? batteryPercent)
    {
        try
        {
            if (DoNotDisturb.IsActive(_settings.Current)) return;
            var subtitle = string.IsNullOrWhiteSpace(deviceName) ? string.Empty : deviceName.Trim();
            if (batteryPercent.HasValue && batteryPercent.Value >= 0 && batteryPercent.Value <= 100)
                subtitle = $"{subtitle}  ��  {Localization.Get("Battery_Level")} {batteryPercent.Value}%";
            ShowEventCard(id,
                Localization.Get(connected ? "Events_BluetoothConnected" : "Events_BluetoothDisconnected"),
                subtitle, "\uE702", connected ? "success" : "info", 5);
        }
        catch (Exception ex) { AppLogger.Warn($"ShowDeviceEvent failed: {ex.Message}"); }
    }

    /// <summary>���֪ͨ��ʷ��Ŀ�����¼���Ƭ��ʽ���µ�����ȥ����¼ʱ���ӵ� sys: ǰ׺����</summary>
    public void ReplayNotification(EventHistoryItem item)
    {
        if (item is null) return;
        var baseId = item.Id.StartsWith("sys:", StringComparison.Ordinal) ? item.Id[4..] : item.Id;
        ShowEventCard(baseId, item.Title, item.Subtitle, item.Icon, item.Type, 6, item.Body);
    }

    /// <summary>���֪ͨ��ʷ������ҳ��ť/�Ҽ��˵�����</summary>
    public void ClearNotificationHistory()
    {
        _notificationHistory.Clear();
    }

    /// <summary>�ϵ� API �յ����ͣ�����/�������Ͷ��У�ͬ id ���ǡ�����ԭ����ʱ�䣩���������ȼ�ˢ����ʾ��</summary>
    public void PushIsland(IslandPush push)
    {
        var idx = _pushes.FindIndex(p => string.Equals(p.Id, push.Id, StringComparison.Ordinal));
        if (idx >= 0)
        {
            // �������ݣ�����ԭ����ʱ��
            if (_pushes[idx].ExpiresAt is DateTime e) push.ExpiresAt = e;
            _pushes[idx] = push;
        }
        else
        {
            _pushes.Add(push);
        }
        RecomputeActivePush();
        AppLogger.Info($"Island push: '{push.Title}' (id={push.Id}, priority={push.Priority ?? "normal"})");
    }

    /// <summary>�ϵ� API �Ƴ�/����ָ�����͡�</summary>
    public void RemoveIslandPush(string id)
    {
        var removed = _pushes.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) > 0;
        if (removed) RecomputeActivePush();
    }

    /// <summary>�û����/�رյ�ǰ�ϵ���Ƭ��ֻ�رյ�ǰ���������л��������������ʾ��</summary>
    public void DismissActivePush()
    {
        if (ActivePush is null) return;
        _pushes.RemoveAll(p => ReferenceEquals(p, ActivePush));
        RecomputeActivePush();
    }

    /// <summary>�� ���ȼ��ߡ��͡���� ����� ���Ŷ��в�ѡ����ǰ��ʾ�������ͬʱ�������</summary>
    private void RecomputeActivePush()
    {
        _pushes.RemoveAll(p => p.ExpiresAt is DateTime e && e <= DateTime.UtcNow);

        // 通知队列管理：超过最大深度时丢弃最低优先级的旧通知
        var maxQueue = Math.Clamp(_settings.Current.NotificationQueueMax, 3, 20);
        while (_pushes.Count > maxQueue)
        {
            // 找到优先级最低的（PriorityRank 最小，同优先级取最早插入的）
            var minIdx = 0;
            var minRank = int.MaxValue;
            for (var j = 0; j < _pushes.Count; j++)
            {
                var rank = _pushes[j].PriorityRank + TypePriorityRank(_pushes[j].Type);
                if (rank < minRank) { minRank = rank; minIdx = j; }
            }
            _pushes.RemoveAt(minIdx);
        }

        // 排序：优先级高 > 类型优先级（error > warning > success > info） > 插入顺序
        var sorted = _pushes
            .Select((p, i) => (p, i))
            .OrderByDescending(t => t.p.PriorityRank + TypePriorityRank(t.p.Type))
            .ThenBy(t => t.i)
            .Select(t => t.p)
            .ToList();
        _pushes.Clear();
        _pushes.AddRange(sorted);
        ActivePush = _pushes.FirstOrDefault();
    }

    /// <summary>通知类型优先级数值：error=10 > warning=8 > success=5 > info=3。</summary>
    private static int TypePriorityRank(string? type)
    {
        return (type ?? "").Trim().ToLowerInvariant() switch
        {
            "error" => 10,
            "warning" => 8,
            "success" => 5,
            _ => 3,
        };
    }

    // ���� Ч�ʹ������ˢ�£������¼� �� ժҪ�ı� �� �ؽ������������������������
    /// <summary>�����ӽ׶ν��������ϲ㵯֪ͨ����</summary>
    public event Action<PomodoroPhase>? PomodoroCompletedRequested;

    private void OnKeyboardStateChanged(string key)
    {
        var label = key switch
        {
            "CapsLock" => "Caps",
            "NumLock" => "Num",
            _ => "ScrLk",
        };
        var on = key switch
        {
            "CapsLock" => _keyboard.Current.Caps,
            "NumLock" => _keyboard.Current.Num,
            _ => _keyboard.Current.Scroll,
        };
        CapsLockText = $"{label} {(on ? Localization.Get("On") : Localization.Get("Off"))}";
        _capsLockSecondsLeft = Math.Max(1, _settings.Current.KeyIndicatorSeconds);
        RebuildCompactItems();
    }

    /// <summary>ÿ��ݼ�����ָʾ��ʣ���������� 0 ����գ������ʧ����</summary>
    private void UpdateCapsLockCountdown()
    {
        if (_capsLockSecondsLeft <= 0) return;
        if (--_capsLockSecondsLeft <= 0)
        {
            CapsLockText = string.Empty;
            RebuildCompactItems();
        }
    }
    /// <summary>��ͼ�¼����鶯����ʾ���ѽ�ͼ����ʱָʾ���� ScreenCaptureMonitor �¼�ת������</summary>
    public void NotifyScreenshotTaken()
    {
        if (!_settings.Current.ScreenCaptureNotifyEnabled) return;
        ScreenshotStatusText = Localization.Get("ScreenCap_IslandScreenshot");
        _screenshotSecondsLeft = Math.Max(1, _settings.Current.KeyIndicatorSeconds);
        RebuildCompactItems();
        UpdateVisibility(); // ��ý��������ʱҲҪ����ʱ��ʾ
    }

    /// <summary>¼��״̬�仯������/�˳�¼��ʱ�����鶯����¼���С�ָʾ��</summary>
    public void SetRecordingStatus(bool recording, string app)
    {
        if (!_settings.Current.ScreenCaptureNotifyEnabled) return;
        RecordingText = recording
            ? $"{Localization.Get("ScreenCap_IslandRecording")}{(string.IsNullOrEmpty(app) ? string.Empty : " �� " + app)}"
            : string.Empty;
        RebuildCompactItems();
        UpdateVisibility(); // ¼��״̬�仯ʱ���������ɼ���
    }

    /// <summary>ÿ��ݼ���ͼָʾʣ���������� 0 ����գ������ʧ����</summary>
    private void UpdateScreenshotCountdown()
    {
        if (_screenshotSecondsLeft <= 0) return;
        if (--_screenshotSecondsLeft <= 0)
        {
            ScreenshotStatusText = string.Empty;
            RebuildCompactItems();
            UpdateVisibility(); // ��ʱָʾ��ʧ����ԭ��������ָ�����
        }
    }
    /// <summary>����/����仯����ʾ��ʱ�ϵ�ָʾ��������Զ���ʧ����</summary>
    public void ShowVolumeTemp(int percent, bool muted)
    {
        if (!_settings.Current.VolumeTempIndicatorEnabled) return;
        _volumeTempGen++; // ȡ����һ��δ��ɵĵ�������
        VolumeTempText = muted ? Localization.Get("VolumeTemp_Muted") : $"{percent}%";
        VolumeTempPercent = Math.Clamp(percent / 100.0, 0, 1);
        _volumeTempSecondsLeft = Math.Max(1, _settings.Current.VolumeTempIndicatorSeconds);
        _volumeTempFading = false;
        OnPropertyChanged(nameof(VolumeTempFading));
        RebuildCompactItems();
        UpdateVisibility(); // ��ý��������ʱҲҪ����ʱ��ʾ
    }

    /// <summary>ÿ����ѯϵͳ����������������ָʾʱ�����仯ʱ�ϵ����ޱ仯ʱ�����㿪����</summary>
    private void PollVolumeTemp()
    {
        if (!_settings.Current.VolumeTempIndicatorEnabled) return;
        try
        {
            var v = SystemVolume.GetVolume();
            var muted = SystemVolume.IsMuted();
            if (v.HasValue && (Math.Abs(v.Value - _lastVolumeTempValue) > 0.001 || muted != _lastVolumeTempMuted))
            {
                _lastVolumeTempValue = v.Value;
                _lastVolumeTempMuted = muted;
                ShowVolumeTemp((int)Math.Round(v.Value * 100), muted);
            }
        }
        catch { /* ��Ƶ���񲻿���ʱ���� */ }
    }

    /// <summary>ÿ��ݼ�����ָʾʣ���������� 0 ���Ȳ��ŵ��������Ƴ�������˳�˿������</summary>
    private void UpdateVolumeTempCountdown()
    {
        if (_volumeTempSecondsLeft <= 0) return;
        if (--_volumeTempSecondsLeft <= 0)
        {
            var gen = _volumeTempGen;
            _volumeTempFading = true;
            OnPropertyChanged(nameof(VolumeTempFading));
            // ��������Լ 260ms���������������Ƴ����������˲ʱ��ʧ
            _ = Task.Delay(320).ContinueWith(_ =>
            {
                if (gen != _volumeTempGen) return; // �ڼ������ֱ����ڹ���ȡ����������
                VolumeTempText = string.Empty;
                VolumeTempPercent = 0;
                _volumeTempFading = false;
                OnPropertyChanged(nameof(VolumeTempFading));
                RebuildCompactItems();
                UpdateVisibility();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>ÿ����ǰ̨�����Ƿ����ڸ���/�ƶ��ļ����仯ʱ�����ϵ��ı���</summary>
    private void PollFileCopy()
    {
        if (!_settings.Current.FileCopyNotifyEnabled) return;
        var newText = FileTransferMonitor.IsCopyingOrMoving() ? Localization.Get("FileCopy_IslandText") : string.Empty;
        if (FileCopyText != newText)
        {
            FileCopyText = newText;
            RebuildCompactItems();
            UpdateVisibility(); // ���ƿ�ʼʱ����������ʱ��ʾ������ʱ��ԭ��������ָ�����
        }
    }

    /// <summary>ÿ��ɨ������Ŀ¼�е��������ʱ�ļ����仯ʱ�����ϵ��ı���</summary>
    private void PollDownloadProgress()
    {
        if (!_settings.Current.DownloadProgressEnabled) return;
        var count = DownloadDetector.ActiveDownloadCount();
        var newText = count > 0 ? string.Format(Localization.Get("Download_IslandText"), count) : string.Empty;
        if (DownloadText != newText)
        {
            DownloadText = newText;
            RebuildCompactItems();
            UpdateVisibility(); // ���ؿ�ʼʱ����������ʱ��ʾ������ʱ��ԭ��������ָ�����
        }
    }

    private void RefreshClipboardSummary() { ClipboardSummary = _clipboard.Summary; RebuildCompactItems(); }
    private void RefreshTodoSummary() { TodoSummary = _todo.Summary; RebuildCompactItems(); }
    private void RefreshScheduleSummary() { ScheduleSummary = _schedule.Summary; RebuildCompactItems(); }
    private void RefreshTimerText()
    {
        TimerPaused = _pomodoro.IsPaused;
        TimerToolTip = Localization.Get("Timer_ToggleHint");
        TimerText = _pomodoro.Phase == PomodoroPhase.Stopped ? string.Empty : _pomodoro.ClockText;
        RebuildCompactItems();
    }
    private void OnPomodoroCompleted(PomodoroPhase phase)
    {
        RefreshTimerText();
        PomodoroCompletedRequested?.Invoke(phase);
    }

    /// <summary>����鶯���ϵķ������������ͣ/�����л��������쳣����</summary>
    public void ToggleTimerPause()
    {
        try
        {
            _pomodoro.TogglePause();
            RefreshTimerText();
        }
        catch (Exception ex) { AppLogger.Warn($"Timer toggle failed: {ex.Message}"); }
    }

    /// <summary>ִ���ϵ���Ƭ��ť������url/�������notify �ص������ͷ���#10����</summary>
    public void ExecutePushAction(IslandPushButton button)
    {
        if (button is null) return;
        try
        {
            // #10 notify �������������ӣ������¼��� App ת���� WebSocket ���Ķˣ����ͷ����д���ص���
            if (string.Equals(button.Action, "notify", StringComparison.OrdinalIgnoreCase))
            {
                var pushId = FindPushIdByButton(button);
                PushActionRequested?.Invoke(button, pushId);
                AppLogger.Info($"Island push notify action: {button.Label ?? button.Value} (push={pushId})");
                return;
            }
            if (string.IsNullOrWhiteSpace(button.Value)) return;

            // #16 command ������ִ�б�����������ϵ� API ���ػ����������� Token��������ο������ͷ���
            if (string.Equals(button.Action, "command", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + button.Value)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                AppLogger.Info($"Island push command: {button.Value}");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(button.Value)
            {
                UseShellExecute = true,
            });
            AppLogger.Info($"Island push action: {button.Action} -> {button.Value}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Island push action failed: {ex.Message}");
        }
    }

    /// <summary>���Ұ����ð�ť������ ID������ notify �ص��㲥����</summary>
    private string FindPushIdByButton(IslandPushButton button)
    {
        foreach (var p in _pushes)
        {
            if (p.Buttons is not null && p.Buttons.Contains(button)) return p.Id;
            if (p.Click == button) return p.Id;
        }
        return ActivePush?.Id ?? string.Empty;
    }

    /// <summary>ִ����������������������������� click ʱ����ִ�к�رո������͡�</summary>
    public void ExecutePushClick()
    {
        if (ActivePush?.Click is not IslandPushButton click || string.IsNullOrWhiteSpace(click.Value)) return;
        ExecutePushAction(click);
        DismissActivePush();
    }

    /// <summary>ÿ�����ϵ������Ƿ���ڣ��� _widgetTimer ���ã���</summary>
    private void CheckPushExpiry()
    {
        if (_pushes.Any(p => p.ExpiresAt is DateTime e && e <= DateTime.UtcNow))
            RecomputeActivePush();
    }

    // ���� Visibility / expansion ������������������������������������������������������������������
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value)) return;
            OnPropertyChanged(nameof(ExpandedContentVisibility));
        }
    }

    /// <summary>ȫ������Ƶ/��Ϸ/��ʾ��ʱ�� FullScreenMonitor ��λ���鶯���Զ����أ��˳�ȫ���ָ���</summary>
    public bool FullScreenHidden { get; set; }

    /// <summary>Windows ����ʱ�� SessionSwitch ��λ���鶯���Զ����أ�������ָ���</summary>
    public bool LockScreenHidden { get; set; }

    public bool IsVisible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    // Used by XAML to collapse expanded-only sections without a converter.
    public System.Windows.Visibility ExpandedContentVisibility
        => IsExpanded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // ���� Coordinator events ��������������������������������������������������������������������������
    private void OnSnapshotChanged(object? sender, MediaSnapshot snapshot)
    {
        // �á�����+����+ר�����жϻ��������������� TrackInfo �ṹ��ȣ�
        // ���� URL ���ֶζ�����Ӧ������������������ý��Ȳ��Ѹ�ʴ�ؿ�ͷ����
        var previous = _snapshot;
        var trackChanged = _snapshot is null ||
            LyricsService.TrackKey(_snapshot.Track) != LyricsService.TrackKey(snapshot.Track);
        var firstTrack = _snapshot is null;
        var durationChanged = previous is null || Math.Abs(previous.DurationSeconds - snapshot.DurationSeconds) >= 0.01;
        var volumeChanged = previous is null || previous.HasVolumeControl != snapshot.HasVolumeControl || !Nullable.Equals(previous.Volume, snapshot.Volume);
        _snapshot = snapshot;
        if (firstTrack && !_progressTimer.IsEnabled) _progressTimer.Start(); // ��ý����ܽ��Ȳ�ֵ������ͣ��
        if (trackChanged && !firstTrack && !string.IsNullOrEmpty(snapshot.Track.Title))
            NowPlayingRequested?.Invoke(snapshot.Track.Title, snapshot.Track.Artist);

        Title = snapshot.Track.Title;
        Artist = snapshot.Track.Artist;
        Album = snapshot.Track.Album;
        SourceLabel = snapshot.SourceLabel;
        SourceDetail = snapshot.Track.SourceAppName;
        var prevStatus = Status;
        if (trackChanged) { _statusOverrideActive = false; _pauseLock = false; } // ��������һ״̬��������
        if (_statusOverrideActive)
        {
            // �ֹ�״̬�����ڣ�ֱ������ȷ��Ŀ��״̬��ʱ�����򱣳ְ�ť״̬���������մ��
            if (snapshot.Status == _optimisticStatus || DateTime.UtcNow > _statusOverrideUntilUtc)
            {
                _statusOverrideActive = false;
                Status = snapshot.Status;
            }
        }
        else if (_pauseLock)
        {
            // ��ͣ�������û������ͣ / ����ָ�������ͣ ���� ���Կ����󱨵� Playing
            // ��Cider SMTC ����ͣʱ���Ա� Playing����������ͣ�����ƽ���ʣ�
            // ֱ������ȷ����ͣ/ֹͣ��������������û�������š�
            if (snapshot.Status is PlaybackStatus.Paused or PlaybackStatus.Closed or PlaybackStatus.Stopped)
            {
                _pauseLock = false;
                Status = snapshot.Status;
            }
        }
        else
        {
            Status = snapshot.Status;
        }
        DurationSeconds = snapshot.DurationSeconds;
        // ��ͣʱ����һ��λ�ã�����/�˳���ɻָ���
        if (Status == PlaybackStatus.Paused && prevStatus != PlaybackStatus.Paused) SavePlaybackState();
        if (durationChanged) OnPropertyChanged(nameof(DurationText));
        var hasRealPosition = snapshot.DurationSeconds > 0 || snapshot.PositionSeconds > 0;
        var reported = Math.Max(0, snapshot.PositionSeconds);
        if (snapshot.DurationSeconds > 0) reported = Math.Min(reported, snapshot.DurationSeconds); // �������ϱ�ֵ��Խ��
        if (trackChanged)
        {
            _restoredMode = false;
            // ����ָ���Cider/SMTC ��δ������ʵλ��ʱ�����ϴα����λ����Ϊ��ʼֵ��
            // ������ͣ����������ʾ�� 0 �С�����ʵλ�õ����١���������ͣ�䡣
            // ֻҪ��Ŀƥ�������ϴ�λ�þͻָ�����ʹ��һ֡����ʱ����λ��Ϊ 0��Ҳ�Իָ�λ��Ϊ׼��
            // ֮��λ�������ᰴ��ʵ�ϱ�ֵƽ��У������������ʾ�� 0 ��������
            var restored = _restoredTrackKey is not null
                && LyricsService.TrackKey(snapshot.Track) == _restoredTrackKey
                && _restoredPosition > 0;
            if (restored)
            {
                _restoredMode = true; // ���λָ�λ�ã�ֱ���յ���ʵǰ��λ��/����/seek
                _interpolatedPosition = _restoredPosition;
                if (_restoredStatus == PlaybackStatus.Paused)
                {
                    // �ϴ�����ͣ������Ϊ��ͣ��������ʱ��ֲ�����Cider SMTC ���� Playing��
                    _pauseLock = true;
                    SetStatusLocal(PlaybackStatus.Paused);
                }
                if (snapshot.DurationSeconds > 0) _interpolatedPosition = Math.Min(_interpolatedPosition, snapshot.DurationSeconds);
                _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(_interpolatedPosition);
            }
            else
            {
                _useFreeClock = !hasRealPosition;
                _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(reported);
                _interpolatedPosition = reported;
            }
            _restoredTrackKey = null; // ֻ�ָ�һ��
            _positionStaleSinceUtc = null;
            _karaokeFrozen = false;
            SavePlaybackState();
        }
        else if (_pauseLock)
        {
            // ��ͣ�����ڼ䣺λ�ñ��ֶ��ᣬ�����ɿ���λ�ã�������/��������ͣʱ�����ߣ�
        }
        else if (hasRealPosition)
        {
            var current = _interpolatedPosition;
            var seeking = _suppressSeek > 0; // �û�������ק������
            if (ShouldAdoptReportedPosition(reported, current, seeking))
            {
                _restoredMode = false; // ���յ���ʵǰ��λ�ã��ָ���������
                _useFreeClock = false;
                _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(reported);
                _interpolatedPosition = reported;
                _positionStaleSinceUtc = null;
            }
            else if (_restoredMode)
            {
                // ����ָ������ڣ������� 0/����λ�ã��� Cider SMTC��Ҳ���ָֻ���λ�ã�����������
            }
            else
            {
                // ����˲����ˣ����ֵ�ǰ��ֵ���ȼ����ƽ���������/������ͻȻ���ؿ�ͷ��
                // �����Ի��˳������� ~4 �루�������ز��򲥷����� seek�����ٲ��ɲ�������
                if (_positionStaleSinceUtc is null) _positionStaleSinceUtc = DateTime.UtcNow;
                else if ((DateTime.UtcNow - _positionStaleSinceUtc.Value).TotalSeconds >= 4.0)
                {
                    _useFreeClock = false;
                    _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(reported);
                    _interpolatedPosition = reported;
                    _positionStaleSinceUtc = null;
                }
            }
        }
        // ����ʵ�����ҷ�����Ŀ����������ʱ�Ӽ����ƽ��������ã�
        _lastPositionTime = DateTime.UtcNow;
        CanPlayPause = snapshot.CanPlayPause;
        CanNext = snapshot.CanNext;
        CanPrevious = snapshot.CanPrevious;
        CanSeek = snapshot.CanSeek && snapshot.DurationSeconds > 0;
        HasVolumeControl = snapshot.HasVolumeControl;
        _suppressVolume = true;
        Volume = snapshot.Volume ?? 0;
        _suppressVolume = false;
        var statusChanged = prevStatus != Status;
        if (statusChanged) _wave.SetPlaying(IsPlaying);
        if (volumeChanged) OnPropertyChanged(nameof(VolumeText));

        if (snapshot.Track.ArtworkPath.Length > 0)
            Artwork = GetArtwork(snapshot.Track.ArtworkPath);
        else
            Artwork = null;

        if (trackChanged)
            _ = LoadLyricsAsync(snapshot);

        if (statusChanged)
        {
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PlayPauseGlyph));
        }
        OnPropertyChanged(nameof(Position));
        if (trackChanged) OnPropertyChanged(nameof(HasLyrics));
        if (trackChanged || statusChanged || previous?.Source != snapshot.Source) UpdateVisibility();
    }

    /// <summary>
    /// �ж��Ƿ�Ӧ�������ò������ϱ���λ�ã��룩��
    /// ����λ��ǰ�� / ��΢���ˣ���2s��/ �û�������ק������ʱ���������ã�
    /// ���ŵ���;ʱ˲���ϱ� ~0 ��Ϊ���ڶ������� Cider/SMTC ż������ 0����
    /// ���� false���ɵ��÷����ֵ�ǰ��ֵ���ȼ����ƽ���������/������ͻȻ���ؿ�ͷ��
    /// </summary>
    internal static bool ShouldAdoptReportedPosition(double reported, double current, bool seeking)
    {
        var sane = seeking || reported >= current - 2.0;
        var staleZero = !seeking && reported < 1.0 && current > 10.0;
        return sane && !staleZero;
    }
    private void OnMediaEnded(object? sender, EventArgs e)
    {
        _snapshot = null;
        _progressTimer.Stop(); // ��ý��ʱ����ͣ�ã����� 100ms ��ת
        _restoredMode = false;
        _statusOverrideActive = false;
        _pauseLock = false;
        Title = string.Empty;
        Artist = string.Empty;
        Album = string.Empty;
        Artwork = null;
        Status = PlaybackStatus.Closed;
        DurationSeconds = 0;
        Position = TimeSpan.Zero;
        Progress = 0;
        _interpolatedPosition = 0;
        _positionStaleSinceUtc = null;
        _wave.SetPlaying(false);
        LyricLines = Array.Empty<LyricLineViewModel>();
        _lyrics = LyricsResult.Empty;
        _ttmlLineIndex = new();
        LyricIndex = -1;
        CurrentLyricText = string.Empty;
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        OnPropertyChanged(nameof(HasLyrics));
        UpdateVisibility();
    }

    private async Task LoadLyricsAsync(MediaSnapshot snapshot)
    {
        var key = LyricsService.TrackKey(snapshot.Track);
        if (key == _lyricsKey) return;
        _lyricsKey = key;

        var result = await _lyricsService.GetLyricsAsync(snapshot);
        if (_lyricsKey != key) return; // track changed while loading
        _lyrics = result;
        _ttmlLineIndex = BuildTtmlLineIndex(result.Ttml); // ����ʱ��ȡ���ִ�
        if (_settings.Current.BilingualLyrics)
        {
            // ˫���ʣ�����ʱ����ķ������Զ��ϲ��������·���ʾ��
            // ����ʱ���ᰴ���п�ʼ�롹�� TTML �ж��루�������Ƿ����ı��������뿨��OK����
            var pairs = LrcParser.PairLines(result.Document.Lines, TimeSpan.FromMilliseconds(250), enable: true);
            LyricLines = pairs.Select(x => new LyricLineViewModel(x.Main, x.Translation,
                WordsForLine(_ttmlLineIndex, x.Main.Time.TotalSeconds))).ToList();
        }
        else
        {
            LyricLines = result.Document.Lines
                .Select(l => new LyricLineViewModel(l, null, WordsForLine(_ttmlLineIndex, l.Time.TotalSeconds)))
                .ToList();
        }
        if (LyricLines.Count > 0)
        {
            // ֱ�Ӱ���ǰ���ѻָ��ģ�λ�ö�λ��ǰ�䣬�������˲������ʾ�� 0 ��������
            // #4�������û�У׼��ʱ��ƫ��
            var idx = result.Document.IndexAt(LyricsAdjustedPosition(TimeSpan.FromSeconds(Math.Max(0, _interpolatedPosition))));
            LyricIndex = idx < 0 ? -1 : idx;
            // ��ʽ���루LyricIndex ����δ�仯������ʱ��ȡ�ʼ���˫��ϲ�����
            CurrentLyricWords = idx >= 0 && idx < LyricLines.Count
                ? WordsForLine(_ttmlLineIndex, LyricLines[idx].Time.TotalSeconds)
                : Array.Empty<TtmlWord>();
            CurrentLyricText = LyricLines[Math.Clamp(idx, 0, LyricLines.Count - 1)].Text;
        }
        else
        {
            LyricIndex = -1;
            CurrentLyricWords = Array.Empty<TtmlWord>();
            CurrentLyricText = string.Empty;
        }

        LyricsStatus = result.Source switch
        {
            LyricsSourceKind.LocalFile => Localization.Get("Lyrics_Local"),
            LyricsSourceKind.Cider => Localization.Get("Lyrics_FromCider"),
            LyricsSourceKind.Online => Localization.Get("Lyrics_Online"),
            LyricsSourceKind.AmllTtml => Localization.Get("Lyrics_Amll"),
            _ => Localization.Get("LyricsUnavailable"),
        };
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(LyricOffsetText)); // �������ܴ��ѱ����ƫ��
    }

    // #4 ���� ���ʱ��΢�� ��������������������������������������������������������������������������������
    /// <summary>��ǰ��Ŀ�ĸ��ʱ��ƫ�ƣ��룩��δУ׼Ϊ 0��</summary>
    public double CurrentLyricOffset
    {
        get => _lyricsKey.Length > 0 && _lyricTimeOffsets.TryGetValue(_lyricsKey, out var v) ? v : 0;
    }

    /// <summary>��ʶ���ƫ�Ƶ���ʾ�ı�����ť����ʾ����</summary>
    public string LyricOffsetText
    {
        get
        {
            var off = CurrentLyricOffset;
            if (Math.Abs(off) < 0.001) return Localization.Get("Lyric_Aligned");
            return off > 0 ? $"+{off:0.0}s" : $"{off:0.0}s";
        }
    }

    /// <summary>�Ѳ���λ�õ��Ӹ��ƫ�ƺ����ڸ�ʶ�λ/����OK��</summary>
    private TimeSpan LyricsAdjustedPosition(TimeSpan pos)
        => pos + TimeSpan.FromSeconds(CurrentLyricOffset);

    /// <summary>΢����ǰ��Ŀ�ĸ��ʱ�䣺delta �루��0.5 ��������������Ч���־û������á�</summary>
    public void AdjustLyricTime(double delta)
    {
        if (_lyricsKey.Length == 0) return;
        try
        {
            var off = Math.Clamp(CurrentLyricOffset + delta, -30, 30);
            _lyricTimeOffsets[_lyricsKey] = off;
            _settings.Update(s => s.LyricTimeOffsets = new Dictionary<string, double>(_lyricTimeOffsets));
            OnPropertyChanged(nameof(LyricOffsetText));
            if (HasLyrics && _lyrics.Document.Lines.Count > 0)
            {
                var idx = _lyrics.Document.IndexAt(LyricsAdjustedPosition(Position));
                if (idx != LyricIndex) LyricIndex = idx;
                UpdateKaraokeHighlight();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Lyric time adjust failed: {ex.Message}");
        }
    }

    // ���� Progress interpolation ������������������������������������������������������������������
    private void AdvanceProgress()
    {
        if (_snapshot is null || _suppressSeek > 0) return;

        if (Status == PlaybackStatus.Playing)
        {
            var now = DateTime.UtcNow;
            if (_useFreeClock)
            {
                // ���������� SMTC ���ȣ��� Cider�����ñ���ʱ�Ӵ���Ŀ��ʼ�ƽ�����OK
                _interpolatedPosition = (now - _trackStartTime).TotalSeconds;
            }
            else
            {
                _interpolatedPosition += (now - _lastPositionTime).TotalSeconds;
            }
            _lastPositionTime = now;
        }

        // ʱ�������ã�Cider��ʱ�ö���ʱ������֤����/����OK���ƽ�
        var duration = DurationSeconds > 0 ? DurationSeconds : 300.0;
        Position = TimeSpan.FromSeconds(Math.Max(0, _interpolatedPosition));
        Progress = Math.Clamp(_interpolatedPosition / duration, 0, 1);

        if (HasLyrics)
        {
            var idx = _lyrics.Document.IndexAt(LyricsAdjustedPosition(Position)); // #4 ���Ӹ��ƫ��
            if (idx != LyricIndex) LyricIndex = idx;
            UpdateKaraokeHighlight();
        }
    }

    /// <summary>���ֿ���OK���ѵ�ǰ���ʱ�����ַ����֣��ƽ��ѵ����ַ�����</summary>
    private void UpdateKaraokeHighlight()
    {
        if (LyricIndex < 0 || LyricIndex >= _lyrics.Document.Lines.Count) return;
        var lines = _lyrics.Document.Lines;
        var cur = lines[LyricIndex];
        var nextStart = (LyricIndex + 1 < lines.Count) ? lines[LyricIndex + 1].Time.TotalSeconds : cur.Time.TotalSeconds + 5.0;
        var duration = Math.Max(0.1, nextStart - cur.Time.TotalSeconds);
        var posSec = Position.TotalSeconds + CurrentLyricOffset; // #4 ���Ӹ��ƫ��
        var frac = Math.Clamp((posSec - cur.Time.TotalSeconds) / duration, 0, 1);

        if (Status == PlaybackStatus.Playing)
        {
            // �����У�ʵʱ�ƽ����������� 0..1�����ؼ� 60fps ������
            _karaokeFrozen = false;
            KaraokePositionSeconds = posSec; // �����г�����������λ�ã��ؼ���ǽ�� 60fps �ƽ���
            SetHighlightFraction(frac);
        }
        else if (!_karaokeFrozen)
        {
            // ��ͣ���õ�ǰ������ȷ�ָ��ģ�λ������һ�θ�����Ȼ�󶳽ᣬ
            // ֮���κ�λ��У�������ٸĶ����� �� �ȶ�����ͣʱ�̵����ӡ�
            KaraokePositionSeconds = posSec; // ��ͣ�״Σ�����ǰ��ȷλ����Ⱦһ�κ󶳽�
            SetHighlightFraction(frac);
            _karaokeFrozen = true;
        }
        // �Ѷ��᣺���ֲ������Ȳ����±���Ҳ����������λ�ã�������ͣʱ������

    }

    private void SetHighlightFraction(double frac)
    {
        if (LyricLines.Count > LyricIndex)
        {
            var lvm = LyricLines[LyricIndex];
            if (Math.Abs(lvm.HighlightFraction - frac) > 0.0005) lvm.HighlightFraction = frac;
        }
        if (Math.Abs(CompactHighlightFraction - frac) > 0.0005) CompactHighlightFraction = frac;
    }

    // ���� AMLL TTML ����ʱ���Ḩ�� ������������������������������������������������������������
    /// <summary>�����п�ʼ�롹��round 2 λ������ TTML ����������˫��ϲ���ʱ�䶨λ���ִʡ�</summary>
    private static Dictionary<double, TtmlLine> BuildTtmlLineIndex(TtmlDocument? ttml)
    {
        var map = new Dictionary<double, TtmlLine>();
        if (ttml is null) return map;
        foreach (var line in ttml.Lines)
        {
            var key = Math.Round(line.BeginSec, 2);
            if (!map.ContainsKey(key)) map[key] = line;
        }
        return map;
    }

    /// <summary>���п�ʼ����Ҹ������ִʣ��ݲ� 50ms���Ҳ������ؿ� �� ���Ž���Ϊ���о��֣���</summary>
    private static IReadOnlyList<TtmlWord> WordsForLine(Dictionary<double, TtmlLine> index, double beginSec)
    {
        var key = Math.Round(beginSec, 2);
        if (index.TryGetValue(key, out var line)) return line.Words;
        foreach (var kv in index)
        {
            if (Math.Abs(kv.Key - key) <= 0.05) return kv.Value.Words;
        }
        return Array.Empty<TtmlWord>();
    }

    /// <summary>���浱ǰ����λ�ã��˳�/��ͣ/�и�ʱ���ã����´�����ָ�����</summary>
    public void SavePlaybackState()
    {
        try
        {
            if (_snapshot is null) return;
            new PlaybackStateStore
            {
                TrackKey = LyricsService.TrackKey(_snapshot.Track),
                PositionSeconds = Math.Max(0, _interpolatedPosition),
                Status = Status.ToString(),
            }.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SavePlaybackState failed: {ex.Message}");
        }
    }
    // ���� Visibility ������������������������������������������������������������������������������������������
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
        // ����ʱ�Ƿ��������������פ/�ɿ��أ���Ҫ��ʾ
        var anyIdleComp = comp.TimeWhenIdle || comp.WeatherWhenIdle || comp.CoverWhenIdle
            || comp.TitleWhenIdle || comp.ArtistWhenIdle || comp.LyricsWhenIdle || comp.ProgressWhenIdle
            || comp.DiskWhenIdle;
        var showWidgets = !hasMedia && (_settings.Current.ShowWidgetsWhenNoMedia || alwaysVisible || anyIdleComp);
        ShowIdleWidgets = !hasMedia; // �������ɼ��ԣ��ڲ��������ѡ��

        // ���ϵ�����ʱҲҪ��ʾ�鶯����������������Ϳ�������
        // ��ͼ/¼��/����/����/���ص���ʱָʾ����ʱҲǿ����ʾ�������Զ���ʧ��ָ����أ�
        var anyTempStatus = (ScreenshotStatusText.Length > 0 || RecordingText.Length > 0 || VolumeTempText.Length > 0
            || FileCopyText.Length > 0 || DownloadText.Length > 0);
        var show = !_userHidden && !FullScreenHidden && !LockScreenHidden && (hasMedia || showWidgets || HasActivePush || !_settings.Current.HideWhenNoMedia || anyTempStatus);
        // ��פʱ������ͣ������
        if (!alwaysVisible && hasMedia && Status == PlaybackStatus.Paused && !_settings.Current.ShowWhenPaused)
            show = false;

        // �����������棺����/ǿ����ʾ/ǿ�����𣨶��������ӣ��������ȣ�
        var ruleEval = RuleEngine.Evaluate(_settings.Current, hasMedia, _snapshot?.Track.SourceAppId);
        if (ruleEval.ForceHide) show = false;
        else if (ruleEval.ForceShow) show = true;
        if (ruleEval.ForceCollapse && IsExpanded) IsExpanded = false;

        if (!ShowIdleWeather) { WeatherText = string.Empty; WeatherDetailText = string.Empty; } // �������������ʾʱ�����
        if (!ShowIdleMic) MicText = string.Empty;    // ��˷�/����ͷ�������ѡʱ���
        if (!ShowIdleCam) CamText = string.Empty;

        // ֪ͨ��������ɼ��Ա仯������ֵʵ�ʸı�ʱ���������� GC ������
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
        RaiseVisIfChanged(nameof(ShowIdleClipboard), ShowIdleClipboard);
        RaiseVisIfChanged(nameof(ShowIdleTodo), ShowIdleTodo);
        RaiseVisIfChanged(nameof(ShowIdleTimer), ShowIdleTimer);
        RaiseVisIfChanged(nameof(ShowIdleSchedule), ShowIdleSchedule);
        RaiseVisIfChanged(nameof(ShowIdleHoliday), ShowIdleHoliday);
        RaiseVisIfChanged(nameof(ShowIdleMeeting), ShowIdleMeeting);
        RaiseVisIfChanged(nameof(ShowIdleDisk), ShowIdleDisk);
        RaiseVisIfChanged(nameof(ShowIdleInputMethod), ShowIdleInputMethod);
        RaiseVisIfChanged(nameof(ShowIdleQuickToggles), ShowIdleQuickToggles);
        RaiseVisIfChanged(nameof(HolidayText), HolidayText.Length > 0);
        RaiseVisIfChanged(nameof(VolumeText), VolumeText.Length > 0);
        _visFirst = false;

        IsVisible = show;
    }

    /// <summary>������뷨������л���/Ӣ���뷨������ˢ��״̬�ı���</summary>
    public void ToggleInputMethod()
    {
        InputMethodMonitor.ToggleChineseEnglish();
        InputMethodText = InputMethodMonitor.GetStatusText();
    }

    /// <summary>ˢ�¿�ݿ���״̬�ı���Radio 2 �뻺�棬���౾�ؼ�ʱ��ȡ��ֵ���䲻����֪ͨ����</summary>
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

    /// <summary>�л���ʷ�����ʾ���ء�</summary>
    public void ToggleLyricTranslation()
    {
        ShowLyricTranslation = !ShowLyricTranslation;
    }

    /// <summary>���Ƶ�ǰ��ʾ䵽�����壨�޸��ʱ�޲�������</summary>
    public void CopyCurrentLyric()
    {
        try
        {
            var text = CurrentLyricText;
            if (string.IsNullOrEmpty(text)) return;
            System.Windows.Clipboard.SetText(text);
            AppLogger.Info("Current lyric copied to clipboard.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CopyCurrentLyric failed: {ex.Message}");
        }
    }

    /// <summary>�����ݿ��أ�which: wifi / bluetooth / night / mute����</summary>
    public async void ToggleQuickSwitch(string which)
    {
        try
        {
            switch (which)
            {
                case "wifi":
                    var ok = await QuickSwitchService.SetRadioAsync(false, !QuickSwitchService.IsWifiOn);
                    if (!ok) TryOpenNetworkSettings(); // Radio ���ɿأ�Ӳ��/�������ƣ�ʱ���ף���ϵͳ��������
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
    /// �û���ʽҪ����ʾ���ٴ���� exe / ���̡���ʾ������ͬʱ����Զ������ſأ�
    /// ����ǰ̨����ȫ������ʱ UpdateVisibility �Ի���� show=false������Ϊ������û��Ӧ����
    /// </summary>
    public void ForceShow()
    {
        _userHidden = false;
        FullScreenHidden = false;
        LockScreenHidden = false;
        UpdateVisibility();
    }

    public void ForceHide() { _userHidden = true; UpdateVisibility(); }

    /// <summary>ǿ�����»�ȡ��ǰ��Ŀ�ĸ�ʣ����߸�ʿ��ر仯����ã���</summary>
    public async Task RefreshLyricsAsync()
    {
        _lyricsKey = string.Empty;
        _lyricsService.ClearCache();
        if (_snapshot is not null) await LoadLyricsAsync(_snapshot);
    }

    /// <summary>
    /// Demo mode (--demo): injects a fake track so the island can be previewed
    /// without any media playing. Also writes a sample .lrc for lyrics.
    /// </summary>
    public void InjectDemoMedia()
    {
        var artPath = CreateDemoArtwork();
        var lyricsDir = AppPaths.LyricsDir;
        Directory.CreateDirectory(lyricsDir);
        var lrcPath = Path.Combine(lyricsDir, "Demo Artist - Demo Song.lrc");
        if (!File.Exists(lrcPath))
        {
            File.WriteAllText(lrcPath,
                "[ti:Demo Song]\n[ar:Demo Artist]\n[al:Demo Album]\n" +
                "[00:00.00]Welcome to WinIslands\n[00:04.00]This is a demo track\n[00:08.00]Hover to expand\n[00:12.00]Drag the progress bar to seek\n[00:16.00]Lyrics scroll automatically\n[00:20.00]Enjoy your Dynamic Island\n[00:24.00]Thanks for trying WinIslands\n");
        }

        var track = new TrackInfo("Demo Song", "Demo Artist", "Demo Album", "Demo Artist",
            "Demo", "demo-source", artPath, string.Empty, TimeSpan.FromSeconds(210));
        var snap = new MediaSnapshot
        {
            Track = track,
            Source = MediaSourceKind.Smtc,
            Status = PlaybackStatus.Playing,
            PositionSeconds = 5,
            DurationSeconds = 210,
            CanPlayPause = true,
            CanNext = true,
            CanPrevious = true,
            CanSeek = true,
            HasVolumeControl = true,
            Volume = 0.6,
            HasLyrics = true,
        };
        OnSnapshotChanged(this, snap);
    }

    private static string CreateDemoArtwork()
    {
        try
        {
            var path = Path.Combine(AppPaths.ThumbCacheDir, "demo-art.jpg");
            if (File.Exists(path)) return path;
            using var bmp = new System.Drawing.Bitmap(320, 320);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var rect = new System.Drawing.Rectangle(0, 0, 320, 320);
                var brush = new System.Drawing.Drawing2D.LinearGradientBrush(rect,
                    System.Drawing.Color.FromArgb(255, 99, 102, 241),
                    System.Drawing.Color.FromArgb(255, 34, 211, 238), 45f);
                g.FillRectangle(brush, rect);
                g.DrawString("WinIslands", new System.Drawing.Font("Segoe UI", 28, System.Drawing.FontStyle.Bold),
                    System.Drawing.Brushes.White, 70, 130);
            }

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
            return path;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Demo artwork failed: {ex.Message}");
            return string.Empty;
        }
    }

    // ���� Helpers ������������������������������������������������������������������������������������������������
    /// <summary>��·��ȡ���棺�������л��棬δ���вŽ���һ�β����棨���� LRU ��̭����</summary>
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
            // ��̭��������һ�Dictionary ���ֲ�����
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
        TimerToolTip = Localization.Get("Timer_ToggleHint");
        // �����л���ˢ�º����ػ��İ�����ʱ״̬������/����/�ϲ����ң�
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
        var target = Math.Clamp(fraction, 0, 1) * _snapshot.DurationSeconds;
        _restoredMode = false; // �û� seek ������λ��Ϊ׼
        _interpolatedPosition = target;
        _lastPositionTime = DateTime.UtcNow;
        await _coordinator.SeekAsync(target);
    }

    /// <summary>
    /// �ֹ۲���/��ͣ�������ť�������л�����״̬���������״̬�ӳ��ڼ�
    /// ���ؽ��ȼ����ƽ���������ͣ���ʸ���/�����������ء���ͣ�㡣
    /// </summary>
    private async Task TogglePlayPauseLocalAsync()
    {
        if (_toggleInFlight) return; // �����㣺������;ʱ�����ٴε��
        if (Status != PlaybackStatus.Playing && Status != PlaybackStatus.Paused) return;
        _toggleInFlight = true;
        var target = Status == PlaybackStatus.Playing ? PlaybackStatus.Paused : PlaybackStatus.Playing;
        try
        {
            _pauseLock = target == PlaybackStatus.Paused; // ��ͣ����������������
            SetStatusLocal(target); // �����л���ť״̬�������ӳٸ�
            _optimisticStatus = target;
            _statusOverrideActive = true;
            _statusOverrideUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            var ok = await _coordinator.TogglePlayPauseAsync();
            if (!ok)
            {
                // ���ֲ�����/Cider �汾��֧�� playpause �˵㣺���˵���ȷ�� play/pause
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
        // �����ڲ��ڴ˽������ȿ���ȷ��Ŀ��״̬��ʱ���ٻָ�������������ֹ��ť�����
    }

    private void SetStatusLocal(PlaybackStatus value)
    {
        if (Status == value) return;
        Status = value;
        _wave.SetPlaying(value == PlaybackStatus.Playing);
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PlayPauseGlyph));
        if (value == PlaybackStatus.Paused) SavePlaybackState(); // ��ͣ�����棬�˳�/������ɻָ�
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _progressTimer.Stop();
        _widgetTimer.Stop();
        _coordinator.SnapshotChanged -= OnSnapshotChanged;
        _coordinator.MediaEnded -= OnMediaEnded;
        _coordinator.SessionsChanged -= OnSessionsChanged;
        _keyboard.StateChanged -= OnKeyboardStateChanged;
        _clipboard.Changed -= RefreshClipboardSummary;
        _todo.Changed -= RefreshTodoSummary;
        _schedule.Changed -= RefreshScheduleSummary;
        _pomodoro.Tick -= RefreshTimerText;
        _pomodoro.Completed -= OnPomodoroCompleted;
        Localization.LanguageChanged -= OnLanguageChanged;
        // �ͷ�Ч�ʹ��߷���Stop/Dispose �ݵȣ�App �˳�ʱ�ٴε��ð�ȫ��
        _keyboard.Dispose();
        _clipboard.Dispose();
        _schedule.Dispose();
        _pomodoro.Dispose();
        _wave.Stop();
    }
}

/// <summary>�ಥ����ѡ�����е�һ�У�AppId + ���� + �Ƿ�ǰ���棩��</summary>
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

/// <summary>֪ͨ��ʷ��¼�չ����Ƭ�ײ��б���ʾ����������µ�������</summary>
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

