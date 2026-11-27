using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WinIslands.Mac.Services;
using WinIslands.Mac.UI;
using WinIslands.Services;

namespace WinIslands.Mac;

public partial class App : Application
{
    private IslandViewModel? _vm;
    private MacMediaCoordinator? _coordinator;
    private IslandApiServer? _api;
    private TrayService? _tray;
    private Views.IslandWindow? _islandWindow;
    private Views.SettingsWindow? _settingsWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppPaths.EnsureDirectories();
            var settings = new SettingsService();
            Views.SettingsWindow.ApplyThemeVariant(settings.Current.Theme);

            var cider = new CiderMediaProvider(settings);
            _coordinator = new MacMediaCoordinator(settings, cider, SynchronizationContext.Current);
            var lyrics = new LyricsService(settings, cider);

            // 上岛本地 API（默认开启，仅回环地址；Token 可选）
            if (settings.Current.IslandApiEnabled)
            {
                _api = new IslandApiServer(settings);
                try { _api.Start(); }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Island API start failed: {ex.Message}");
                    _api = null;
                }
            }

            _vm = new IslandViewModel(_coordinator, settings, lyrics, _api, SynchronizationContext.Current);
            _islandWindow = new Views.IslandWindow(_vm, settings);
            _settingsWindow = new Views.SettingsWindow(settings, _api);

            _vm.OpenSettingsRequested += (_, _) => ShowSettings();
            _vm.QuitRequested += (_, _) => Quit(desktop);

            _tray = new TrayService(this, _vm, _islandWindow, ShowSettings, () => Quit(desktop));

            // 菜单栏常驻：仅通过托盘「退出」结束进程（窗口关闭一律改写为隐藏）
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = _islandWindow;
            desktop.Exit += (_, _) => DisposeServices();

            _coordinator.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null) return;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _settingsWindow?.CloseForQuit();
        _islandWindow?.CloseForQuit();
        desktop.Shutdown();
    }

    private void DisposeServices()
    {
        _vm?.Dispose();
        _vm = null;
        _coordinator?.Dispose();
        _coordinator = null;
        _api?.Dispose();
        _api = null;
        _tray?.Dispose();
        _tray = null;
    }
}
