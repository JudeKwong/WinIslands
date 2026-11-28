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
using ThemeMode = WinIslands.Services.ThemeMode;
using WinIslands.UI;

namespace WinIslands;

public partial class App : Application
{
    /// <summary>检查更新；有新版时在灵动岛与托盘提示。</summary>
    private async Task CheckForUpdatesAsync(bool showWhenUpToDate)
    {
        try
        {
            if (_updater is null) return;
            var hasNew = await _updater.CheckAsync();
            if (hasNew && _updater.LastAvailable is { } info)
            {
                var title = Localization.Get("Update_Title");
                var message = string.Format(Localization.Get("Update_AvailableMessage"), info.Version);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _vm?.ShowEventCard("update:available", title, message, "\uE895", "info", 8);
                    _tray?.Notify(title, message);
                });
            }
            else if (!hasNew && showWhenUpToDate)
            {
                _ = Dispatcher.BeginInvoke(() => _vm?.ShowEventCard("update:none",
                    Localization.Get("Update_Title"), Localization.Get("Update_None"), "\uE72E", "info", 5));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Update check failed", ex);
        }
    }

    /// <summary>托盘/菜单触发的交互式检查：发现新版本时询问是否下载并安装。</summary>
    private async Task CheckForUpdatesInteractiveAsync()
    {
        if (_updater is null) return;
        try
        {
            var found = await _updater.CheckAsync();
            if (!found || _updater.LastAvailable is not { } info)
            {
                MessageBox.Show(Localization.Get("Update_None"), Localization.Get("Update_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(string.Format(Localization.Get("Update_ConfirmInstall"), info.Version),
                    Localization.Get("Update_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await DownloadAndInstallUpdateAsync(info);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Check update failed", ex);
            MessageBox.Show(ex.Message, Localization.Get("Update_Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>下载、校验并安装更新；成功后正常退出，由独立脚本替换并重启。</summary>
    private async Task DownloadAndInstallUpdateAsync(UpdateInfo info)
    {
        if (_updater is null) return;
        var lastBucket = -1;
        var progress = new Progress<double>(p =>
        {
            var pct = (int)Math.Round(p * 100);
            var bucket = pct / 10;
            if (bucket == lastBucket) return;
            lastBucket = bucket;
            _vm?.ShowEventCard("update:download", Localization.Get("Update_Downloading"),
                string.Format(Localization.Get("Update_DownloadingPercent"), pct), "\uE896", "info", 30);
        });
        try
        {
            var file = await _updater.DownloadAsync(info, progress);
            _vm?.ShowEventCard("update:verified", Localization.Get("Update_Title"),
                Localization.Get("Update_Verified"), "\uE73E", "success", 6);
            var needsElevation = !UpdaterService.CurrentInstallIsWritable();
            if (!_updater.LaunchInstaller(file, needsElevation))
            {
                MessageBox.Show(Localization.Get("Update_LaunchFailed"), Localization.Get("Update_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await Task.Delay(400);
            Shutdown();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Download update failed", ex);
            MessageBox.Show(ex.Message, Localization.Get("Update_Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var enabled = s.ThemeScheduledEnabled && s.Theme == WinIslands.Services.ThemeMode.Auto;
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
    private static readonly RestartStormGuard RestartGuard = new(maxRestarts: 3, window: TimeSpan.FromMinutes(15));

    /// <summary>崩溃后自动重启：如用户开启了 AutoRestartOnCrash，在崩溃前启动新实例。
    /// 15 分钟内最多自动重启 3 次，超过后停止重启并记录，避免“启动即崩溃”的死循环。</summary>
    private void TryAutoRestartOnCrash()
    {
        try
        {
            if (_settings is null || !_settings.Current.AutoRestartOnCrash) return;
            if (!RestartGuard.TryRecord(DateTime.UtcNow))
            {
                AppLogger.Warn($"Auto-restart suppressed: {RestartGuard.CountInWindow(DateTime.UtcNow)} restarts within 15 minutes.");
                return;
            }
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

    // ── 3.1.7 后台活动闸门：锁屏 / 全屏 / 省电时暂停轮询 ──────────────

    /// <summary>
    /// 注册可暂停的后台服务，并让灵动岛可见性/展开状态实时驱动闸门。
    /// 统一在这里接线，避免各服务各自判断系统状态。
    /// </summary>
    private void WireBackgroundActivityGate()
    {
        var gate = BackgroundActivityGate.Current;
        if (_clipboard is not null) gate.Register(_clipboard);
        if (_keyboard is not null) gate.Register(_keyboard);
        if (_systemNotifications is not null) gate.Register(_systemNotifications);

        if (_vm is not null)
        {
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(IslandViewModel.IsVisible) or nameof(IslandViewModel.IsExpanded))
                    SyncActivityGate();
            };
            SyncActivityGate();
        }

        gate.Changed += (_, _) =>
        {
            if (gate.BackgroundSuspended == _gateSuspendedLogged) return;
            _gateSuspendedLogged = gate.BackgroundSuspended;
            AppLogger.Info(gate.BackgroundSuspended
                ? "Background activity suspended (lock/fullscreen/battery saver)."
                : "Background activity resumed.");
        };
    }

    private void SyncActivityGate()
    {
        if (_vm is null) return;
        BackgroundActivityGate.Current.SetIslandState(_vm.IsVisible || _vm.IsExpanded, _vm.IsExpanded);
    }

    /// <summary>
    /// 采样电源状态：AC 供电 / 电池 / 省电模式（SYSTEM_POWER_STATUS.SystemStatusFlag bit0）。
    /// 省电模式开启时，未展开的灵动岛相关轮询暂停。
    /// </summary>
    private void RefreshPowerState()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return;
            var onBattery = status.ACLineStatus == 0;
            var batterySaver = (status.SystemStatusFlag & 0x01) != 0;
            BackgroundActivityGate.Current.SetPowerState(onBattery, batterySaver);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("GetSystemPowerStatus failed: " + ex.Message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

}
