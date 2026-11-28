using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using Application = System.Windows.Application;
using Localization = WinIslands.UI.Localization;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using WinIslands.Diagnostics;
using WinIslands.Services;
using WinIslands.UI;
using Accessibility = WinIslands.UI.Accessibility;

namespace WinIslands;

public partial class App : Application
{
    private SettingsService? _settings;
    private ThemeService? _theme;
    private CiderMediaProvider? _cider;
    private MediaCoordinator? _coordinator;
    private LyricsService? _lyrics;
    private IslandViewModel? _vm;
    private readonly List<IslandWindow> _windows = new();
    private LyricsWindow? _lyricsWindow;
    private MiniPlayerWindow? _miniPlayer;
    private TrayIcon? _tray;
    private SingleInstance? _singleInstance;
    private AppSettings? _lastPositionSettings;
    private BluetoothMonitor? _bluetooth;
    private IncomingCallMonitor? _callMonitor;
    private MessengerUnreadMonitor? _messenger;
    private SystemNotificationMonitor? _systemNotifications;
    private NetworkStatusMonitor? _network;
    private GlobalHotkeyService? _hotkeys;
    private QuickLauncherWindow? _launcher;
    private PluginService? _plugins;
    private PluginManagerWindow? _pluginManager;
    private IslandApiServer? _islandApi;
    private MediaAppRegistry? _mediaApps;
    private FullScreenMonitor? _fullScreenMonitor;
    private SessionSwitchEventHandler? _sessionSwitchHandler;   // 锁屏自动隐藏：SessionSwitch 订阅句柄
    private PowerModeChangedEventHandler? _powerModeHandler;   // 2.2.7 系统挂起/恢复：动画时钟重同步句柄
    private readonly DispatcherTimer _themeScheduleTimer = new() { Interval = TimeSpan.FromSeconds(30) }; // 定时明暗切换：每 30 秒检查一次
    private readonly DispatcherTimer _stateSaveTimer = new() { Interval = TimeSpan.FromSeconds(30) }; // 周期性保存状态（崩溃恢复）
    private bool? _lastScheduledDark;   // 上次应用的定时深色状态，避免无变化时重复 Apply
    private CalendarService? _calendar;
    private RssMailService? _rssMail;
    private DispatcherTimer? _powerStateTimer;   // 3.1.7 省电模式采样（30s，仅变化时通知后台闸门）
    private bool _gateSuspendedLogged;
    private AppHealthMonitor? _healthMonitor;

    // ── 效率工具 / 波纹 / 更新服务（共享实例：设置页与灵动岛组件共用同一份数据）──
    private AudioWaveService? _wave;
    private KeyboardIndicatorMonitor? _keyboard;
    private ClipboardCopyMonitor? _clipboard;
    private UpdaterService? _updater;

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 窗口创建前强制 PerMonitorV2，避免启动瞬间按系统缩放渲染导致文字先大后恢复正常
        try { SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2); }
        catch { /* manifest 已声明，忽略 */ }

        // 3.1.8 可访问性：为所有窗口补齐自动化名称，并跟踪最近窗口用于读屏播报
        WinIslands.UI.Accessibility.Register();
        // 高对比度切换时重算主题（岛、迷你播放器会随 ThemeChanged 自动刷新）
        WinIslands.UI.Accessibility.HighContrastChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            try { if (_settings is not null) _theme?.Apply(_settings.Current); } catch { }
        }));
        // ── Crash safety: log everything, never show a raw crash dialog. ──
        // 3.1.2beta2 稳定性：托管异常链写 minidump + first-chance SEH 诊断日志，
        // 让 0xc0000005/0x80131506 这类此前不可复现的崩溃可被定位（配合 OS 的 WER LocalDumps）。
        CrashDump.RegisterFirstChanceLogging();
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled dispatcher exception", args.Exception);
            CrashDump.TryWriteDump("dispatcher"); // 诊断：保留崩溃时刻进程快照
            WriteCrashMarker(); // 崩溃自动恢复（1.2.0）：下次启动提示已恢复
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled AppDomain exception", args.ExceptionObject as Exception);
            CrashDump.TryWriteDump("appdomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };


AppPaths.EnsureDirectories();
        AppLogger.Info($"WinIslands starting. PID={Environment.ProcessId}");

        // ── Single instance ──
        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire())
        {
            AppLogger.Info("Another instance is running; signaling show and exiting.");
            Shutdown();
            return;
        }

        _singleInstance.ShowRequested += (_, _) => Dispatcher.BeginInvoke(() => _vm?.ForceShow());

        // ── UI 健康监测：心跳延迟 / 卡死时写诊断，不打断用户 ──
        _healthMonitor = new AppHealthMonitor(Dispatcher);
        _healthMonitor.Start();

        // ── Services ──
        _settings = new SettingsService();
        _lastPositionSettings = _settings.Current.Clone();
        _theme = new ThemeService();
        _theme.Apply(_settings.Current);
        Localization.CurrentLanguage = _settings.Current.Language;

        _cider = new CiderMediaProvider(_settings);
        _mediaApps = new MediaAppRegistry();
        var smtc = new SmtcMediaProvider(_settings, _mediaApps, preferredAppId: "Cider"); // Cider session priority, avoid other active sessions
        var title = new WindowTitleMediaProvider();
        _coordinator = new MediaCoordinator(_settings, smtc, _cider, title, Dispatcher);
        _lyrics = new LyricsService(_settings, _cider);

        // ── 蓝牙监控：连接/断开事件以 iOS 风格卡片在灵动岛上展示（1.2.4：设备名 + 电量，读不到电量时只显示名称）──
        _bluetooth = new BluetoothMonitor();
        _bluetooth.DeviceConnected += async (_, name) =>
        {
            var battery = await _bluetooth.GetBatteryLevelAsync(name); // 异步读电量，失败返回 null，不阻塞 UI
            _ = Dispatcher.BeginInvoke(() => _vm?.ShowDeviceEvent("bt:conn:" + name, connected: true, deviceName: name, batteryPercent: battery));
        };
        _bluetooth.DeviceDisconnected += (_, name) => Dispatcher.BeginInvoke(() =>
            _vm?.ShowDeviceEvent("bt:disc:" + name, connected: false, deviceName: name, batteryPercent: null));
        // 来电提醒：微信/QQ 语音视频通话窗口检测（仅本机，不上传数据）
        _callMonitor = new IncomingCallMonitor();
        _callMonitor.CallStarted += (appName, title, kind) =>
        {
            if (!_settings!.Current.CallNotifyEnabled) return;
            var isIncoming = kind == CallKind.Incoming;
            var isWechat = appName.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
                || appName.Contains("Weixin", StringComparison.OrdinalIgnoreCase)
                || appName.Contains("微信", StringComparison.Ordinal);
            var messengerType = isWechat ? "wechat" : appName.Contains("QQ", StringComparison.OrdinalIgnoreCase) ? "qq" : "info";
            var icon = isWechat ? "\uE8BD" : messengerType == "qq" ? "\U0001F427" : "\uE8F2";
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard(
                "call:" + appName,
                Localization.Get(isIncoming ? "Call_NotifyTitle" : "Call_Title"),
                isIncoming ? $"{appName} · {Localization.Get("Call_Body")}（{Localization.Get("Call_Coming")}）" : $"{appName} · {Localization.Get("Call_Body")}",
                icon, messengerType, 8));
        };
        // 断网 / 网络恢复提醒（每次状态变化只提示一次；去抖在服务内部）
        _network = new NetworkStatusMonitor();
        _network.NetworkLost += (_, _) =>
        {
            if (_settings!.Current.NetworkNotifyEnabled)
                Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("net:lost",
                    Localization.Get("Network_LostTitle"), Localization.Get("Network_LostBody"), "\uE945", "error", 6));
        };
        _network.NetworkRestored += (_, _) =>
        {
            if (_settings!.Current.NetworkNotifyEnabled)
                Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("net:back",
                    Localization.Get("Network_BackTitle"), Localization.Get("Network_BackBody"), "\uE945", "success", 6));
        };

        // ── 效率工具 / 波纹 / 更新服务 ──
        _wave = new AudioWaveService();
        _keyboard = new KeyboardIndicatorMonitor();
        _clipboard = new ClipboardCopyMonitor();
        _updater = new UpdaterService();
        if (_settings.Current.WaveVisualizerEnabled) _wave.Start();
        _wave.SetSyncEnabled(_settings.Current.WaveVisualizerEnabled && _settings.Current.WaveSyncEnabled);
        _wave.SetSensitivity(_settings.Current.WaveSensitivity);
        // 复制监听只服务于普通的“已复制”轻提示，不保留任何剪贴板内容。
        _clipboard.EntryAdded += OnClipboardEntryAdded;
        UpdateClipboardPolling();
        UpdateKeyboardPolling();

        _messenger = new MessengerUnreadMonitor();
        _messenger.UnreadAdded += (_, unread) => Dispatcher.BeginInvoke(() => _vm?.ShowMessengerUnread(unread));
        _systemNotifications = new SystemNotificationMonitor();
        _systemNotifications.NotificationReceived += (_, e) =>
        {
            if (!NotificationAppFilter.IsAllowed(_settings.Current, e.AppDisplayName, e.AppUserModelId))
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (e.Source.HasValue)
                {
                    _vm?.ShowMessengerNotification(e.Source.Value, e.Title, e.Body, e.AppUserModelId);
                    return;
                }

                var appName = string.IsNullOrWhiteSpace(e.AppDisplayName)
                    ? (string.IsNullOrWhiteSpace(e.AppUserModelId) ? Localization.Get("Notification_DefaultTitle") : e.AppUserModelId)
                    : e.AppDisplayName;
                // 标题用通知标题，副标题用应用名，正文放通知内容；
                // 这样「同来源同标题折叠」才有意义（不同应用同名标题不会互相合并）。
                var notificationTitle = string.IsNullOrWhiteSpace(e.Title) ? appName : e.Title.Trim();
                _vm?.ShowEventCard("app:" + e.AppUserModelId, notificationTitle, appName, "\uE7E7", "info", null, e.Body);
            });
        };
        _vm = new IslandViewModel(_coordinator, _settings, _lyrics,
            _wave, _keyboard, _clipboard, _messenger);
        WireBackgroundActivityGate();
        if (_settings.Current.CallNotifyEnabled) _messenger.Start();
        if (_settings.Current.NotificationTakeoverEnabled) _ = _systemNotifications.StartAsync();
        TryShowCrashRecoveryCard(); // 崩溃自动恢复（1.2.0）：上次异常退出 → 提示已恢复
        _vm.OpenSettingsRequested += (_, _) => OpenSettings();
        _vm.ToggleLyricsWindowRequested += (_, _) => ToggleLyricsWindow();
        // 低电量 / 开始充电 / 充电完成 / 磁盘不足：统一以灵动岛卡片展示（iOS 风格）。
        _vm.LowBatteryRequested += percent =>
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("battery:low",
                Localization.Get("LowBattery_Title"), $"{percent}%", "\uEBA0", "warning", 6));
        _vm.ChargingStartedRequested += percent =>
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("battery:charging",
                Localization.Get("Events_ChargingStarted"), $"{percent}%", "\uEBA0", "success", 5));
        _vm.ChargedRequested += percent =>
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("battery:charged",
                Localization.Get("Charged_Title"),
                string.Format(Localization.Get("Charged_Body"), percent), "\uEBA0", "success", 6));
        _vm.DiskLowRequested += gb =>
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("disk:low",
                Localization.Get("Disk_Title"),
                string.Format(Localization.Get("Disk_Body"), gb), "\uEDA2", "warning", 6));

        // ── 本地组件插件：独立进程运行，stdout 返回组件 JSON ──
        _plugins = new PluginService();
        _plugins.ComponentsChanged += (_, e) =>
            Dispatcher.BeginInvoke(() => _vm?.SetPluginComponents(e.PluginId, e.Components));
        _plugins.Start();

        // ── 上岛 API：第三方软件推送信息到灵动岛 ──
        _islandApi = new IslandApiServer(_settings);
        _islandApi.PushReceived += push => Dispatcher.BeginInvoke(() => _vm?.PushIsland(push));
        _islandApi.PushRemoved += id => Dispatcher.BeginInvoke(() => _vm?.RemoveIslandPush(id));
        // #10 上岛按钮回调：notify 动作点击→ 向 WebSocket 订阅端广播 push_button 事件
        if (_vm is not null)
        {
            _vm.PushActionRequested += (button, pushId) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(pushId) && _vm.ActivePush is not null) pushId = _vm.ActivePush.Id;
                    _islandApi?.BroadcastPushButton(pushId ?? string.Empty, button.Label, button.Value);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"push_button 广播失败: {ex.Message}");
                }
            };
        }
        if (_settings.Current.IslandApiEnabled) _islandApi.Start();

        // ── 全屏自动隐藏（视频/游戏/演示等全屏时隐藏灵动岛，退出恢复）──
        _fullScreenMonitor = new FullScreenMonitor { HideOnMaximize = _settings.Current.HideOnMaximizeEnabled };
        _fullScreenMonitor.FullScreenChanged += full => Dispatcher.BeginInvoke(() =>
        {
            // 3.1.7：全屏时无论是否自动隐藏岛，都暂停后台轮询（游戏/视频/演示场景）。
            BackgroundActivityGate.Current.SetFullScreen(full);
            if (_settings!.Current.FullScreenAutoHideEnabled)
            {
                if (_vm is not null) _vm.FullScreenHidden = full;
                _vm?.UpdateVisibility();
            }
        });
        if (_settings.Current.FullScreenAutoHideEnabled) _fullScreenMonitor.Start();

        // ── 日历事件提醒（.ics 本地解析；事件到点弹右上角横幅，默认关）──
        _calendar = new CalendarService();
        _calendar.Reminder += ev => Dispatcher.BeginInvoke(() =>
        {
            if (!_settings!.Current.CalendarEnabled) return;
            var start = ev.Start.LocalDateTime;
            var isAllDay = start.Date == start && ev.End - ev.Start >= TimeSpan.FromDays(1);
            var body = isAllDay
                ? $"{start:yyyy-MM-dd}  {ev.Title}"
                : $"{start:HH:mm} ~ {ev.End.LocalDateTime:HH:mm}  {ev.Title}";
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("cal:" + ev.Id,
                Localization.Get("Calendar_ReminderTitle"), body, "\uE787", "info", 8));
        });
        _calendar.Refresh(_settings.Current.CalendarIcsPath);

        // ── RSS 订阅 / 邮件提醒（后台轮询；默认关，仅开启后联网）──
        _rssMail = new RssMailService();
        _rssMail.RssItemReceived += (title, summary, link) => Dispatcher.BeginInvoke(() =>
        {
            if (!_settings!.Current.RssNotifyEnabled) return;
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("rss:" + title,
                Localization.Get("Rss_NotifyTitle"),
                string.IsNullOrEmpty(summary) ? title : summary, "\uE8A5", "info", 8));
        });
        _rssMail.MailReceived += (subject, from, date) => Dispatcher.BeginInvoke(() =>
        {
            if (!_settings!.Current.MailNotifyEnabled) return;
            var body = string.IsNullOrEmpty(from)
                ? date
                : (string.IsNullOrEmpty(date) ? from : $"{from} · {date}");
            Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("mail:" + (subject ?? from ?? date),
                Localization.Get("Mail_NotifyTitle"),
                string.IsNullOrEmpty(subject) ? body : $"{subject}\n{body}", "\uE715", "info", 8));
        });
        ApplyRssMail(_settings.Current);

        // ── Island windows (one per selected monitor) ──
        RecreateWindows();

        // ── 迷你播放器（独立悬浮小窗，按需创建并跟随媒体状态显隐）──
        if (_vm is null) return;
            _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandViewModel.HasMedia)) UpdateMiniPlayerVisibility();
        };
        UpdateMiniPlayerVisibility();

        // ── Tray ──
        _tray = new TrayIcon(_settings);
        _tray.ShowHideRequested += (_, _) => _vm.ToggleUserVisible();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.ToggleLyricsRequested += (_, _) => ToggleLyricsWindow();
        _tray.AutoStartRequested += (_, _) =>
        {
            var enable = !AutoStart.IsEnabled();
            AutoStart.SetEnabled(enable);
            _settings.Update(s => s.StartWithWindows = enable);
            _tray.SetAutoStartChecked(enable);
        };
        _tray.DoNotDisturbRequested += (_, _) =>
        {
            var on = !DoNotDisturb.IsActive(_settings!.Current);
            _settings.Update(s => s.DoNotDisturbManual = on);
            _tray.SetDoNotDisturbChecked(on);
        };
        _tray.UpdateRequested += async (_, _) => await CheckForUpdatesInteractiveAsync();
        _tray.LogsRequested += (_, _) =>
        {
            try { new LogViewerWindow().Show(); }
            catch (Exception ex) { AppLogger.Error("Open log viewer failed", ex); }
        };
        _tray.PluginManagerRequested += (_, _) => OpenPluginManager();
        _tray.PluginsRequested += (_, _) =>
        {
            OpenPluginFolder();
            _plugins?.Reload();
        };
        _tray.ExitRequested += (_, _) => Shutdown();

        // ── 全局快捷键（35 全局快捷键大全：组合键可在设置中自定义，见 GlobalHotkeyService）──
        _hotkeys = new GlobalHotkeyService(_settings.Current);
        _hotkeys.PlayPausePressed += () => _vm?.PlayPauseCommand.Execute(null);
        _hotkeys.NextPressed += () => _ = _coordinator?.NextAsync();
        _hotkeys.PreviousPressed += () => _ = _coordinator?.PreviousAsync();
        _hotkeys.ToggleVisibilityPressed += () => _vm?.ToggleUserVisible();
        _hotkeys.ExpandPressed += () =>
        {
            if (_vm is not null) _vm.IsExpanded = !_vm.IsExpanded; // 展开/收起
        };
        // 快速启动器与剪贴板面板按需创建，避免未使用窗口占用启动内存。
        _hotkeys.LauncherPressed += () => Dispatcher.BeginInvoke(() =>
        {
            _launcher ??= new QuickLauncherWindow(_theme);
            _launcher.Toggle();
        });
        _hotkeys.SetEnabled(_settings.Current.GlobalHotkeysEnabled);

        // ── Settings changed → re-apply live ──
        _settings.Changed += (_, s) =>
        {
            Localization.CurrentLanguage = s.Language;
            RefreshScheduledTheme(s);
            _vm?.RebuildQuickActions(); // 快捷操作按钮：开关/勾选/顺序变化即时生效

            if (AutoStart.IsEnabled() != s.StartWithWindows)
                AutoStart.SetEnabled(s.StartWithWindows);

            if (s.StandaloneLyricsWindow && _lyricsWindow is null)
                ShowLyricsWindow();
            if (!s.StandaloneLyricsWindow && _lyricsWindow is { IsVisible: true })
                _lyricsWindow.Hide();
            if (_lyricsWindow is { IsVisible: true })
                _lyricsWindow.ApplySettings(); // #5 歌词小窗不透明度/锁定实时生效

            // 仅位置/显示器类设置变更时重定位；锁定等其它变更保留拖动后的位置
            var posChanged = _lastPositionSettings is null
                || s.Position != _lastPositionSettings.Position
                || s.Monitor != _lastPositionSettings.Monitor
                || s.MonitorIndex != _lastPositionSettings.MonitorIndex
                || s.OffsetX != _lastPositionSettings.OffsetX
                || s.OffsetY != _lastPositionSettings.OffsetY;
            _lastPositionSettings = s.Clone();
            if (posChanged) RecreateWindows();

            // 通知监控开关
            if (s.BluetoothNotifyEnabled) _bluetooth?.Start(); else _bluetooth?.Stop();
            if (s.CallNotifyEnabled) _callMonitor?.Start(s.CallNotifyApps); else _callMonitor?.Stop();
            if (s.CallNotifyEnabled) _messenger?.Start(); else _messenger?.Stop();
            if (s.NotificationTakeoverEnabled) _ = _systemNotifications?.StartAsync(); else _systemNotifications?.Stop();

            // 日历提醒：路径/开关变化立即重新解析；总开关关闭或无路径时停止轮询
            _calendar?.Refresh(s.CalendarIcsPath);
            _calendar?.SetEnabled(s.CalendarEnabled && !string.IsNullOrWhiteSpace(s.CalendarIcsPath));

            // RSS / 邮件提醒：开关、地址、间隔变化立即生效
            ApplyRssMail(s);

            // 全局快捷键：组合键 / 开关变化实时重新注册
            _hotkeys?.Apply(s);

            // 上岛 API：启用/端口变化时重启
            if (_islandApi is not null)
            {
                var running = _islandApi.IsRunning;
                if (s.IslandApiEnabled && !running) _islandApi.Start();
                else if (!s.IslandApiEnabled && running) _islandApi.Stop();
            }

            // 全屏自动隐藏：开关变化即时生效（关闭时立即恢复显示）
            if (_fullScreenMonitor is not null)
            {
                _fullScreenMonitor.HideOnMaximize = s.HideOnMaximizeEnabled;
                if (s.FullScreenAutoHideEnabled && !_fullScreenMonitor.IsRunning) _fullScreenMonitor.Start();
                else if (!s.FullScreenAutoHideEnabled && _fullScreenMonitor.IsRunning)
                {
                    _fullScreenMonitor.Stop();
                    if (_vm is not null) _vm.FullScreenHidden = false;
                    _vm?.UpdateVisibility();
                }
            }

            // 锁屏自动隐藏：开关关闭时立即恢复显示
            if (!s.LockScreenAutoHideEnabled && _vm is not null && _vm.LockScreenHidden)
            {
                _vm.LockScreenHidden = false;
                _vm.UpdateVisibility();
            }

            // 波纹可视化开关
            if (s.WaveVisualizerEnabled) _wave?.Start(); else _wave?.Stop();
            // 轻量化：波纹关闭时不再启用 WASAPI 回环采集
            _wave?.SetSyncEnabled(s.WaveVisualizerEnabled && s.WaveSyncEnabled);
            _wave?.SetSensitivity(s.WaveSensitivity);

            UpdateClipboardPolling();
            UpdateKeyboardPolling();

            // 尺寸设置变更 → 应用到灵动岛
            foreach (var w in _windows) w.ApplySize();

            _vm?.UpdateVisibility();
            UpdateMiniPlayerVisibility();
        };

        // ── 定时明暗切换：到点自动切换深/浅主题（仅 Theme=Auto 且开关开启时生效）──
        _themeScheduleTimer.Tick += (_, _) => RefreshScheduledTheme(_settings!.Current);
        // 周期性保存播放状态与设置（崩溃恢复：每 30 秒落盘一次，崩溃时丢失最多 30 秒进度）
        _stateSaveTimer.Tick += (_, _) =>
        {
            try
            {
                _settings?.Save();
                _vm?.SavePlaybackState();
            }
            catch (Exception ex) { AppLogger.Warn($"Periodic state save failed: {ex.Message}"); }
        };
        _stateSaveTimer.Start();
        RefreshScheduledTheme(_settings.Current);   // 启动即应用一次
        if (_settings.Current.ThemeScheduledEnabled) _themeScheduleTimer.Start();

        _calendar?.SetEnabled(_settings.Current.CalendarEnabled && !string.IsNullOrWhiteSpace(_settings.Current.CalendarIcsPath));

        // ── 锁屏自动隐藏：锁屏/远程桌面断开时隐藏灵动岛，解锁后恢复 ──
        _sessionSwitchHandler = (_, args) =>
        {
            var locked = args.Reason is SessionSwitchReason.SessionLock;
            var unlocked = args.Reason is SessionSwitchReason.SessionUnlock;
            if (!locked && !unlocked) return;
            // 3.1.7：锁屏时暂停所有后台轮询，与「是否自动隐藏岛」设置无关。
            BackgroundActivityGate.Current.SetSessionLocked(locked);
            if (_settings is null || !_settings.Current.LockScreenAutoHideEnabled) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm is null) return;
                _vm.LockScreenHidden = locked;
                _vm.UpdateVisibility();
            });
        };
        SystemEvents.SessionSwitch += _sessionSwitchHandler;

        // ── 多显示器 DPI/分辨率变化动态适配：显示器插拔或分辨率/DPI 变化时重建窗口 ──
        // ── 2.2.7 系统挂起/恢复：重建动画时钟基线 ──
        // 挂起期间渲染钩子停止，恢复瞬间 Stopwatch 与首帧间隔会出现巨帧；
        // 恢复时立即重置弹簧节拍器基线，动画从恢复后的第一帧平滑起步、不跳变。
        _powerModeHandler = (_, e) =>
        {
            // 3.1.7：挂起视为锁屏，恢复后重新采样电源与省电模式。
            BackgroundActivityGate.Current.SetSessionLocked(e.Mode == PowerModes.Suspend);
            if (e.Mode != PowerModes.Resume) return;
            try { SpringTicker.ResetBaseline(); }
            catch (Exception ex) { AppLogger.Error("PowerModeChanged resume resync failed", ex); }
            RefreshPowerState();
        };
        SystemEvents.PowerModeChanged += _powerModeHandler;

        // 省电模式 / 电池状态：每 30 秒采样一次（GetSystemPowerStatus 开销极低），
        // 仅在状态变化时通知闸门，省电时暂停后台轮询。
        RefreshPowerState();
        _powerStateTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _powerStateTimer.Tick += (_, _) => RefreshPowerState();
        _powerStateTimer.Start();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    AppLogger.Info("Display settings changed; recreating island windows.");
                    RecreateWindows();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Display settings change handling failed", ex);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        };

        // Sync registry state once at startup (in case settings were edited externally).
        if (AutoStart.IsEnabled() != _settings.Current.StartWithWindows)
            AutoStart.SetEnabled(_settings.Current.StartWithWindows);

        // ── Start media pipeline (critical path: needed for initial island display) ──
        _coordinator.Start();
        _vm.UpdateVisibility();

        // ── 非关键服务延迟启动：先让灵动岛窗口渲染到屏幕，再初始化后台监控服务 ──
        // 使用 Background 优先级：确保 UI 渲染和媒体管线优先完成
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_settings!.Current.BluetoothNotifyEnabled) _bluetooth?.Start();
                if (_settings.Current.CallNotifyEnabled) _callMonitor?.Start(_settings.Current.CallNotifyApps);
                if (_settings.Current.CallNotifyEnabled) _messenger?.Start();
                if (_settings.Current.NotificationTakeoverEnabled) _ = _systemNotifications?.StartAsync();
                _network?.Start(); // 网络监控很轻量，始终启动；是否弹横幅由 NetworkNotifyEnabled 开关控制

                if (_settings.Current.StandaloneLyricsWindow)
                    ShowLyricsWindow();

                // 启动时自动检查新版本（可选；需联网，默认关闭）
                if (_settings.Current.AutoUpdateCheck)
                    _ = CheckForUpdatesAsync(showWhenUpToDate: false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Deferred service startup failed", ex);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);

        // ── Diagnostics mode: report and exit. ──
        if (e.Args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
            _ = RunDiagnosticsAsync();

        // ── 展开页面预览（UI 截图/诊断用）──
        var pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (pageArg is not null)
        {
            var key = pageArg["--page=".Length..];
            var page = _vm.Pages.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (page is not null) _vm.ActivatePage(page);
        }

        // ── Demo mode: show a fake track for preview / testing. ──
        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            var demoStart = 12.4;
            var demoAtArg = e.Args.FirstOrDefault(a => a.StartsWith("--demo-at=", StringComparison.OrdinalIgnoreCase));
            if (demoAtArg is not null &&
                double.TryParse(demoAtArg["--demo-at=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedDemoStart))
                demoStart = parsedDemoStart;
            _vm.InjectDemoMedia(demoStart);
            _vm.ForceShow(); // Demo previews must stay visible for screenshots/manual inspection.
        }

        // Set expansion after the demo snapshot so startup previews open on the requested page.
        if (e.Args.Contains("--expand", StringComparer.OrdinalIgnoreCase))
            _vm.IsExpanded = true;

        // ── Open the settings window at startup (also handy from the CLI). ──
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(OpenSettings);

        // ── Offscreen preview capture: render the real island window and exit. ──
        var screenshotArg = e.Args.FirstOrDefault(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase));
        if (screenshotArg is not null)
        {
            var scaleArg = e.Args.FirstOrDefault(a => a.StartsWith("--screenshot-scale=", StringComparison.OrdinalIgnoreCase));
            var previewScale = 1.0;
            if (scaleArg is not null &&
                double.TryParse(scaleArg["--screenshot-scale=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedScale))
                previewScale = Math.Clamp(parsedScale, 1.0, 6.0);
            var forceExpanded = !e.Args.Contains("--collapsed", StringComparer.OrdinalIgnoreCase);
            _ = CapturePreviewAsync(screenshotArg["--screenshot=".Length..], previewScale, forceExpanded);
        }

        // ── 展开→收起圆角回归测试：渲染真实 Card 视觉并记录最终几何，仅用于本地诊断。──
        // ── 展开→收起逐帧诊断：捕获整个收起动画期间每一帧的圆角/裁剪状态。──
        var collapseFramesArg = e.Args.FirstOrDefault(a => a.StartsWith("--collapse-frames-diag=", StringComparison.OrdinalIgnoreCase));
        if (collapseFramesArg is not null)
            _ = CaptureCollapseFramesAsync(collapseFramesArg["--collapse-frames-diag=".Length..]);
        var roundTripArg = e.Args.FirstOrDefault(a => a.StartsWith("--roundtrip-diag=", StringComparison.OrdinalIgnoreCase));
        if (roundTripArg is not null)
            _ = CaptureRoundTripAsync(roundTripArg["--roundtrip-diag=".Length..]);

        // 首次正常启动：等主界面与后台服务完成首帧后再显示引导，避免阻塞启动。
        // CI performance gate: sample the first idle frame, memory and startup duration, then exit.
        var startupBenchmarkArg = e.Args.FirstOrDefault(a => a.StartsWith("--startup-benchmark=", StringComparison.OrdinalIgnoreCase));
        if (startupBenchmarkArg is not null)
            _ = CaptureStartupBenchmarkAsync(startupBenchmarkArg["--startup-benchmark=".Length..]);

        if (_settings is not null && OnboardingPolicy.ShouldShow(_settings.Current, e.Args))
            Dispatcher.BeginInvoke(new Action(ShowOnboarding), DispatcherPriority.ApplicationIdle);
    }

    private void ShowOnboarding()
    {
        if (_settings is null || _theme is null) return;
        try
        {
            var win = new OnboardingWindow(_settings, _cider, _theme);
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Show onboarding failed", ex);
            try { _settings.Update(s => s.OnboardingCompleted = true); } catch { }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("WinIslands exiting.");
        try { if (File.Exists(AppPaths.CrashMarkerFile)) File.Delete(AppPaths.CrashMarkerFile); } catch { /* 正常退出清理崩溃标记 */ }
        try
        {
            SaveMiniPlayerPosition();
            _settings?.Save();
            _vm?.SavePlaybackState(); // 退出前保存播放位置，重启后恢复（暂停时不再跳回开头）
            _pluginManager?.Close();
            _plugins?.Dispose();
            _vm?.Dispose();
            _wave?.Dispose();
            _keyboard?.Dispose();
            _clipboard?.Dispose();
            _coordinator?.Dispose();
            _bluetooth?.Dispose();
            _network?.Dispose();
            _hotkeys?.Dispose();
            _islandApi?.Dispose();
            _callMonitor?.Dispose();
        _messenger?.Dispose();
        _systemNotifications?.Dispose();
            _fullScreenMonitor?.Dispose();
            _calendar?.Dispose();
            _rssMail?.Dispose();
            _tray?.Dispose();
            _singleInstance?.Dispose();
            if (_sessionSwitchHandler is not null)
                SystemEvents.SessionSwitch -= _sessionSwitchHandler;
            if (_powerModeHandler is not null)
                SystemEvents.PowerModeChanged -= _powerModeHandler;
            _healthMonitor?.Dispose();
            _healthMonitor = null;
            _themeScheduleTimer.Stop();
            _stateSaveTimer.Stop();
            _powerStateTimer?.Stop();
            _miniPlayer?.Close();
            foreach (var w in _windows) w.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error during shutdown", ex);
        }
        finally
        {
            AppLogger.Shutdown();
        }

        base.OnExit(e);
    }
}
