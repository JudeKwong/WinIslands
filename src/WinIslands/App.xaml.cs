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
                var body = string.IsNullOrWhiteSpace(e.Body) ? e.Title : e.Title + "\n" + e.Body;
                _vm?.ShowEventCard("app:" + e.AppUserModelId, appName, body, "\uE7E7", "info", 6);
            });
        };
        _vm = new IslandViewModel(_coordinator, _settings, _lyrics,
            _wave, _keyboard, _clipboard, _messenger);
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
        _tray.UpdateRequested += async (_, _) =>
        {
            try
            {
                if (_updater is null) return;
                var found = await _updater.CheckAsync();
                MessageBox.Show(found ? Localization.Get("Update_Found") : Localization.Get("Update_None"),
                    Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { AppLogger.Error("Check update failed", ex); }
        };
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
            if (_settings is null || !_settings.Current.LockScreenAutoHideEnabled) return;
            var locked = args.Reason is SessionSwitchReason.SessionLock;
            var unlocked = args.Reason is SessionSwitchReason.SessionUnlock;
            if (!locked && !unlocked) return;
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
            if (e.Mode != PowerModes.Resume) return;
            try { SpringTicker.ResetBaseline(); }
            catch (Exception ex) { AppLogger.Error("PowerModeChanged resume resync failed", ex); }
        };
        SystemEvents.PowerModeChanged += _powerModeHandler;

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
            _ = CapturePreviewAsync(screenshotArg["--screenshot=".Length..]);

        // ── 展开→收起圆角回归测试：渲染真实 Card 视觉并记录最终几何，仅用于本地诊断。──
        // ── 展开→收起逐帧诊断：捕获整个收起动画期间每一帧的圆角/裁剪状态。──
        var collapseFramesArg = e.Args.FirstOrDefault(a => a.StartsWith("--collapse-frames-diag=", StringComparison.OrdinalIgnoreCase));
        if (collapseFramesArg is not null)
            _ = CaptureCollapseFramesAsync(collapseFramesArg["--collapse-frames-diag=".Length..]);
        var roundTripArg = e.Args.FirstOrDefault(a => a.StartsWith("--roundtrip-diag=", StringComparison.OrdinalIgnoreCase));
        if (roundTripArg is not null)
            _ = CaptureRoundTripAsync(roundTripArg["--roundtrip-diag=".Length..]);
    }

    /// <summary>Renders the first island window at 2x and exits. Used for documentation previews.</summary>
    private async Task CapturePreviewAsync(string rawPath)
    {
        try
        {
            // Let bindings, lyrics, and open/expand animations settle before rendering.
            await Task.Delay(1500);
            var window = _windows.FirstOrDefault();
            if (window is null)
            {
                AppLogger.Error("Screenshot failed: no island window is available.");
                Shutdown(1);
                return;
            }

            // The screenshot path is diagnostic-only: keep the requested expansion even though
            // the mouse is not over the card. Normal hover behavior remains unchanged.
            window.KeepExpandedForPreview = true;
            _vm!.IsExpanded = true;

            // Force the real window to the foreground on its own desktop, then capture the exact
            // client rectangle. RenderTargetBitmap cannot reliably capture WPF layered windows.
            window.Visibility = System.Windows.Visibility.Visible;
            window.WindowState = System.Windows.WindowState.Normal;
            window.Topmost = true;
            window.ShowActivated = true;
            window.Show();
            window.Activate();
            window.Focus();
            window.UpdateLayout();
            // Diagnostics can start faster than the clock/lyrics services on a warm machine.
            // Wait for the real content to be ready so a preview never captures a half-initialized card.
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (window.IsLoaded && !string.IsNullOrWhiteSpace(_vm.ClockText) &&
                    !string.IsNullOrWhiteSpace(_vm.DateText) && _vm.LyricIndex >= 0)
                    break;
                await Task.Delay(100);
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == System.IntPtr.Zero)
            {
                AppLogger.Error("Screenshot failed: island window has no HWND.");
                Shutdown(1);
                return;
            }

            await Task.Delay(500);

            AppLogger.Info($"Preview state: expanded={_vm!.IsExpanded}, window={window.ActualWidth:0}x{window.ActualHeight:0}, visible={window.IsVisible}, clock='{_vm!.ClockText}', date='{_vm!.DateText}', lyricIndex={_vm!.LyricIndex}, scroll={window.LyricsScroll.VerticalOffset:0.0}/{window.LyricsScroll.ScrollableHeight:0.0}/{window.LyricsScroll.ViewportHeight:0.0}");
            if (!GetWindowRect(hwnd, out var rect))
            {
                AppLogger.Error("Screenshot failed: GetWindowRect returned false.");
                Shutdown(1);
                return;
            }

            var width = Math.Max(1, rect.Right - rect.Left);
            var height = Math.Max(1, rect.Bottom - rect.Top);
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            var path = System.IO.Path.GetFullPath(rawPath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            AppLogger.Info($"Preview screenshot saved: {path} ({width}x{height})");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Screenshot failed", ex);
            Shutdown(1);
            return;
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// 本地回归诊断：连续执行 5 次展开→收起、快速切换、减少动态效果三种路径，
    /// 每次收起后校验卡片宽度稳定、未超出宿主窗口、四角圆角未被裁成直角。
    /// 只在 --roundtrip-diag 参数下运行；通过时退出码 0，失败时退出码 1。
    /// </summary>
    private async Task CaptureRoundTripAsync(string rawDir)
    {
        var failures = new List<string>();
        var passed = false;
        try
        {
            var dir = Path.GetFullPath(rawDir);
            Directory.CreateDirectory(dir);
            await Task.Delay(1800);
            var window = _windows.FirstOrDefault();
            if (window is null || _vm is null || _settings is null)
            {
                failures.Add("no island window / view model / settings available");
                return;
            }

            window.KeepExpandedForPreview = true;

            void Check(string stage, IslandWindow.CardDiagnosticResult r)
            {
                if (!double.IsFinite(r.Width) || !double.IsFinite(r.WindowWidth))
                {
                    failures.Add($"{stage}: invalid measurement ({r.Detail})");
                    return;
                }
                if (!r.CornersTransparent)
                    failures.Add($"{stage}: corners are not transparent or card is clipped ({r.Detail})");
                if (!r.ExpandedLayoutOk)
                    failures.Add($"{stage}: expanded cover/progress overlap ({r.Detail})");
                if (r.Width > r.WindowWidth + 0.5)
                    failures.Add($"{stage}: card width {r.Width:0.##} exceeds window width {r.WindowWidth:0.##}");
                if (!r.RadiusMatches)
                    failures.Add($"{stage}: radius mismatch card={r.Radius:0.###}, glass={r.GlassRadius:0.###}, clip={r.ClipRadius:0.###}, expected={r.ExpectedRadius:0.###} ({r.Detail})");
                if (double.IsFinite(r.ClipWidth) && Math.Abs(r.ClipWidth - r.Width) > 1.5)
                    failures.Add($"{stage}: clip width {r.ClipWidth:0.##} does not match card width {r.Width:0.##}");
            }

            var collapsedHeights = new List<double>();
            for (var i = 1; i <= 5; i++)
            {
                _vm.IsExpanded = true;
                await Task.Delay(1700);
                Check($"cycle{i}-expanded",
                    window.CaptureCardDiagnostic(Path.Combine(dir, $"roundtrip-{i}-expanded.png"), $"cycle{i}-expanded"));

                _vm.IsExpanded = false;
                await Task.Delay(1900);
                var collapsed = window.CaptureCardDiagnostic(
                    Path.Combine(dir, $"roundtrip-{i}-collapsed.png"), $"cycle{i}-collapsed");
                Check($"cycle{i}-collapsed", collapsed);
                collapsedHeights.Add(collapsed.Height);

                // 收尾回调/延迟布局完成后再次测量，确认尺寸不会回弹或继续膨胀。
                await Task.Delay(800);
                var late = window.CaptureCardDiagnostic(
                    Path.Combine(dir, $"roundtrip-{i}-collapsed-late.png"), $"cycle{i}-collapsed-late");
                Check($"cycle{i}-collapsed-late", late);
                if (double.IsFinite(late.Width) && Math.Abs(late.Width - collapsed.Width) > 0.5)
                    failures.Add($"cycle{i}: width changed after settle ({collapsed.Width:0.##} -> {late.Width:0.##})");
            }
            // 紧凑态宽度会随当前歌词文本长度按设计变化；跨轮次只校验高度稳定。
            // 每轮宽度是否在收敛后继续变化，由上方 collapsed-late 对比单独校验。
            var minHeight = double.MaxValue;
            var maxHeight = double.MinValue;
            foreach (var h in collapsedHeights)
            {
                if (!double.IsFinite(h)) continue;
                minHeight = Math.Min(minHeight, h);
                maxHeight = Math.Max(maxHeight, h);
            }
            if (collapsedHeights.Count >= 5 && maxHeight - minHeight > 2.0)
                failures.Add($"collapsed height drifted across cycles ({minHeight:0.##}..{maxHeight:0.##}); expected stable size");

            // 快速展开后立即收起：最常见的手势路径。
            _vm.IsExpanded = true;
            await Task.Delay(120);
            _vm.IsExpanded = false;
            await Task.Delay(2300);
            Check("fast-toggle-collapsed",
                window.CaptureCardDiagnostic(Path.Combine(dir, "roundtrip-fast-collapsed.png"), "fast-toggle-collapsed"));

            // 减少动态效果路径：没有弹簧补间，直接写最终几何。
            var originalReduceMotion = _settings.Current.ReduceMotion;
            _settings.Current.ReduceMotion = true;
            _vm.IsExpanded = true;
            await Task.Delay(500);
            _vm.IsExpanded = false;
            await Task.Delay(1400);
            Check("reduce-motion-collapsed",
                window.CaptureCardDiagnostic(Path.Combine(dir, "roundtrip-reduce-motion-collapsed.png"), "reduce-motion-collapsed"));
            _settings.Current.ReduceMotion = originalReduceMotion;

            passed = failures.Count == 0;
        }
        catch (Exception ex)
        {
            failures.Add("exception: " + ex);
        }
        finally
        {
            foreach (var failure in failures)
                AppLogger.Error("Round-trip FAIL: " + failure);
            if (passed)
                AppLogger.Info("Round-trip diagnostic PASSED: 5 cycles + fast toggle + reduce motion; pill shape stable.");
            else
                AppLogger.Error($"Round-trip diagnostic FAILED: {failures.Count} issue(s).");
            Shutdown(passed ? 0 : 1);
        }
    }
    /// <summary>
    /// Collapse-frame regression diagnostic: samples every 16 ms for 1.5 s, covering the
    /// full spring plus the 1.2 s compact-restore window. Any frame whose three radius
    /// consumers disagree with the live card height fails the run.
    /// </summary>
    private async Task CaptureCollapseFramesAsync(string rawDir)
    {
        var failures = new List<string>();
        var passed = false;
        try
        {
            var dir = Path.GetFullPath(rawDir);
            Directory.CreateDirectory(dir);
            await Task.Delay(1800);
            var window = _windows.FirstOrDefault();
            if (window is null || _vm is null)
            {
                failures.Add("no island window available");
                return;
            }

            window.KeepExpandedForPreview = true;
            _vm.IsExpanded = true;
            await Task.Delay(1900);

            var samples = new List<string>
            {
                "ms,width,height,cardRadius,glassRadius,clipRadius,expectedRadius,radiusOk,fitsWindow"
            };

            void Record(double ms, IslandWindow.CardShapeProbe probe)
            {
                samples.Add($"{ms:0.###},{probe.Width:0.###},{probe.Height:0.###},{probe.Radius:0.###}," +
                            $"{probe.GlassRadius:0.###},{probe.ClipRadius:0.###},{probe.ExpectedRadius:0.###}," +
                            $"{probe.RadiusMatches},{probe.FitsWindow}");
                if (!probe.RadiusMatches)
                    failures.Add($"at {ms:0} ms: radius mismatch ({probe.Detail})");
                if (!probe.FitsWindow)
                    failures.Add($"at {ms:0} ms: card does not fit window ({probe.Detail})");
            }

            var expanded = window.CaptureCardShapeProbe();
            AppLogger.Info($"Collapse probe [expanded]: {expanded.Detail}");
            Record(0, expanded);

            _vm.IsExpanded = false;
            var start = DateTime.UtcNow;
            var nextLogMs = 0.0;
            var frame = 0;
            while ((DateTime.UtcNow - start).TotalMilliseconds < 1500)
            {
                await Task.Delay(16);
                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                var probe = window.CaptureCardShapeProbe();
                frame++;
                Record(elapsed, probe);
                if (elapsed >= nextLogMs)
                {
                    AppLogger.Info($"Collapse probe [{elapsed:0} ms #{frame}]: {probe.Detail}");
                    nextLogMs += 100;
                }
            }

            File.WriteAllLines(Path.Combine(dir, "collapse-frames.csv"), samples);
            var final = window.CaptureCardDiagnostic(Path.Combine(dir, "frame-final.png"), "frames-final");
            AppLogger.Info($"Collapse-frame final: {final.Detail}");
            if (!final.RadiusMatches)
                failures.Add($"final: radius mismatch ({final.Detail})");
            if (!final.CornersTransparent)
                failures.Add($"final: corners are not transparent or card is clipped ({final.Detail})");

            passed = failures.Count == 0;
        }
        catch (Exception ex)
        {
            failures.Add("exception: " + ex);
        }
        finally
        {
            foreach (var failure in failures.Take(40))
                AppLogger.Error("Collapse-frame FAIL: " + failure);
            AppLogger.Info(passed
                ? "Collapse-frame diagnostic PASSED: every sampled frame kept a height-derived radius for 1.5 s."
                : $"Collapse-frame diagnostic FAILED: {failures.Count} issue(s).");
            Shutdown(passed ? 0 : 1);
        }
    }
    /// <summary>检查更新；有新版时弹通知（可带下载链接）。</summary>
    private async Task CheckForUpdatesAsync(bool showWhenUpToDate)
    {
        try
        {
            if (_updater is null) return;
            var hasNew = await _updater.CheckAsync();
            if (!hasNew && showWhenUpToDate)
                _ = Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("update:none",
                    Localization.Get("Update_Title"), Localization.Get("Update_None"), "\uE72E", "info", 5));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Update check failed", ex);
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        try
        {
            var text = await DiagnosticsCommand.RunAsync(_settings!, _cider);
            var path = Path.Combine(AppPaths.AppDataDir, "diagnostics.txt");
            await File.WriteAllTextAsync(path, text);
            MessageBox.Show($"诊断信息已写入：\n{path}\n\n---\n{text}",
                "WinIslands 诊断", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Diagnostics failed", ex);
        }
    }

    private void RecreateWindows()
    {
        if (_settings is null || _vm is null || _theme is null) return;

        var screens = ScreenHelper.ResolveScreens(_settings.Current);

        // Recreate only when the monitor selection actually changed.
        if (_windows.Count == screens.Count &&
            _windows.Select(w => w.Screen.DeviceName).SequenceEqual(screens.Select(s => s.DeviceName)))
        {
            foreach (var w in _windows) w.Reposition();
            return;
        }

        foreach (var w in _windows)
        {
            w.Close();
        }

        _windows.Clear();
        foreach (var screen in screens)
        {
            var win = new IslandWindow(_vm, _theme, _settings, screen);
            _windows.Add(win);
            // Force the window to load now so IsVisible changes (which can happen before
            // the first Show) can drive it; OnLoaded re-applies the correct visibility.
            win.Show();
            if (!_vm.IsVisible) win.Hide();
        }
    }

    /// <summary>定时明暗切换：计算当前应处的明/暗状态并应用到主题（跨天与勿扰同理，起止相同视为不生效）。</summary>
    private void RefreshScheduledTheme(AppSettings s)
    {
        if (_theme is null) return;
        var enabled = s.ThemeScheduledEnabled && s.Theme == ThemeMode.Auto;
        bool? dark = null;
        if (enabled)
        {
            var start = TimeSpan.FromHours(Math.Clamp(s.ThemeScheduleDarkStartHour, 0, 23));
            var end = TimeSpan.FromHours(Math.Clamp(s.ThemeScheduleDarkEndHour, 0, 23));
            var now = DateTime.Now.TimeOfDay;
            dark = start == end ? false : start < end ? now >= start && now < end : now >= start || now < end;
        }
        if (dark == _lastScheduledDark && _theme.OverrideDark == dark) return;   // 无变化，避免重复刷新
        _lastScheduledDark = dark;
        _theme.OverrideDark = dark;
        _theme.Apply(s);
    }

    private void OpenPluginManager()
    {
        if (_plugins is null) return;
        if (_pluginManager is { IsLoaded: true })
        {
            _pluginManager.Activate();
            return;
        }
        if (_theme is null) return;
        _pluginManager = new PluginManagerWindow(_plugins, _theme);
        _pluginManager.Closed += (_, _) => _pluginManager = null;
        _pluginManager.Show();
    }


    private static void OpenPluginFolder()
    {
        Directory.CreateDirectory(AppPaths.PluginsDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
            ArgumentList = { AppPaths.PluginsDir },
        });
    }

    /// <summary>打开插件开发指南：优先使用内嵌指南，回退到插件目录 README，最后打开项目主页。</summary>
    private static void OpenPluginGuide()
    {
        try
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("WinIslands.PluginDevelopment.md");
            if (stream is not null)
            {
                var path = Path.Combine(Path.GetTempPath(), "WinIslands-Plugin-Development.md");
                using (var file = File.Create(path)) stream.CopyTo(file);
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "notepad.exe",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true,
                    });
                }
                return;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Open embedded plugin guide failed: {ex.Message}");
        }

        foreach (var candidate in new[]
        {
            Path.Combine(AppPaths.PortablePluginsDir, "README.md"),
            Path.Combine(AppPaths.PluginsDir, "README.md"),
        })
        {
            if (!File.Exists(candidate)) continue;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = true,
            });
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/JudeKwong/WinIslands",
            UseShellExecute = true,
        });
    }

    private void OpenSettings()
    {
        if (_settings is null) return;
        var vm = new SettingsViewModel(_settings, _mediaApps, _plugins);
        var win = new SettingsWindow(vm, _settings, _cider,
            _updater, _vm, _plugins, OpenPluginManager, OpenPluginFolder, OpenPluginGuide);
        win.ShowDialog();
    }

    /// <summary>#9 断开蓝牙设备：解除配对即断开；失败则回退打开蓝牙设置页。</summary>
    private void DisconnectBluetooth(string deviceName)
    {
        _ = DisconnectBluetoothAsync(deviceName);
    }

    private async System.Threading.Tasks.Task DisconnectBluetoothAsync(string deviceName)
    {
        try
        {
            if (_bluetooth is not null && await _bluetooth.DisconnectAsync(deviceName)) return;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"BT disconnect fallback: {ex.Message}");
        }
        OpenBluetoothSettings();
    }

    /// <summary>打开 Windows 蓝牙设置页。</summary>
    private static void OpenBluetoothSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Open bluetooth settings failed: {ex.Message}");
        }
    }

    /// <summary>将 RSS/邮件设置应用到后台轮询服务（开启/地址/间隔等变化时即时生效）。</summary>
    private void ApplyRssMail(AppSettings s)
    {
        _rssMail?.Configure(
            s.RssNotifyEnabled, s.RssUrls, s.RssIntervalMinutes,
            s.MailNotifyEnabled, s.MailPop3Server, s.MailPop3Port, s.MailUseSsl,
            s.MailUser, s.MailPassword, s.MailCheckMinutes);
    }

    private void ToggleLyricsWindow()
    {
        if (_lyricsWindow is { IsVisible: true })
        {
            _lyricsWindow.Hide();
            _settings!.Update(s => s.StandaloneLyricsWindow = false);
            _tray?.SetLyricsChecked(false);
        }
        else
        {
            ShowLyricsWindow();
        }
    }

    private void ShowLyricsWindow()
    {
        if (_vm is null) return;
        if (_lyricsWindow is null)
        {
            _lyricsWindow = new LyricsWindow(_vm, _settings!);
        }

        _lyricsWindow.PositionNearBottom();
        _lyricsWindow.Show();
        _tray?.SetLyricsChecked(true);
    }

    /// <summary>迷你播放器显隐策略：开关开启且正在播放媒体时显示，否则隐藏并保存位置。</summary>
    private void UpdateMiniPlayerVisibility()
    {
        if (_settings is null || _vm is null || _theme is null) return;
        var s = _settings.Current;
        if (s.MiniPlayerEnabled && _vm.HasMedia)
        {
            _miniPlayer ??= new MiniPlayerWindow(_vm, _theme, _settings);
            if (!_miniPlayer.IsVisible)
            {
                _miniPlayer.PositionFromSettings();
                _miniPlayer.Show();
            }
        }
        else if (_miniPlayer?.IsVisible == true)
        {
            SaveMiniPlayerPosition();
            _miniPlayer.Hide();
        }
    }

    /// <summary>持久化迷你播放器位置（直接写 Current 并落盘，不触发 Changed 避免递归）。</summary>
    private void SaveMiniPlayerPosition()
    {
        if (_miniPlayer is null || _settings is null) return;
        try
        {
            _settings.Current.MiniPlayerLeft = _miniPlayer.Left;
            _settings.Current.MiniPlayerTop = _miniPlayer.Top;
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"保存迷你播放器位置失败: {ex.Message}");
        }
    }

    /// <summary>键盘指示灯轮询仅在 CapsLock 组件启用时运行（后台减负）。</summary>
    private void UpdateKeyboardPolling()
    {
        if (_keyboard is null || _settings is null) return;
        var comp = _settings.Current.Components ?? new ComponentFlags();
        _keyboard.SetPolling(comp.CapsLockWhenIdle || comp.CapsLockWhenPlaying);
    }

    /// <summary>普通“已复制”提示开启时才监听剪贴板；不记录历史。</summary>
    private void UpdateClipboardPolling()
    {
        if (_clipboard is null || _settings is null) return;
        _clipboard.SetPolling(_settings.Current.CopyToastEnabled);
    }

    /// <summary>检测到复制文本后只显示一次轻提示，不保留文本内容。</summary>
    private void OnClipboardEntryAdded(ClipboardText entry)
    {
        try
        {
            if (_settings is null || string.IsNullOrWhiteSpace(entry.Text)) return;
            if (_settings.Current.CopyToastEnabled)
                Dispatcher.BeginInvoke(() => _vm?.ShowCopyToast(Localization.Get("Clipboard_CopiedTitle")));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"剪贴板提示失败: {ex.Message}");
        }
    }

    // ── 崩溃自动恢复（1.2.0）──
    /// <summary>崩溃后自动重启：如用户开启了 AutoRestartOnCrash，在崩溃前启动新实例。</summary>
    private void TryAutoRestartOnCrash()
    {
        try
        {
            if (_settings is null || !_settings.Current.AutoRestartOnCrash) return;
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            AppLogger.Info($"Auto-restart enabled; launching new instance: {exePath}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Auto-restart failed", ex);
        }
    }

    private void WriteCrashMarker()
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.WriteAllText(AppPaths.CrashMarkerFile,
                "{\"crashedAt\":\"" + DateTime.UtcNow.ToString("o") + "\"}");
        }
        catch { /* 标记写入失败不影响主流程 */ }
    }

    /// <summary>启动时检测崩溃标记：有则提示「已恢复」并清理；岛位/播放信息由设置持久化自动恢复。</summary>
    private void TryShowCrashRecoveryCard()
    {
        try
        {
            if (!File.Exists(AppPaths.CrashMarkerFile)) return;
            File.Delete(AppPaths.CrashMarkerFile);
            Dispatcher.BeginInvoke(() =>
                _vm?.ShowEventCard("crash:recovered", Localization.Get("Crash_RecoveredTitle"),
                    Localization.Get("Crash_RecoveredBody"), "\uE7BA", "warning", 8));
            AppLogger.Info("Crash recovery marker found; showing recovery card");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"TryShowCrashRecoveryCard failed: {ex.Message}");
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
            _themeScheduleTimer.Stop();
            _stateSaveTimer.Stop();
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
