using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using WinIslands.Mac.UI;

namespace WinIslands.Mac.Services;

/// <summary>
/// 菜单栏（macOS 状态栏）常驻图标：显示/隐藏灵动岛、媒体控制、设置、退出。
/// 图标为单色模板（tray.png），随菜单栏明暗自动反色。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TrayIcon _tray;
    private readonly Window _islandWindow;
    private bool _manuallyHidden;

    public TrayService(Application app, IslandViewModel vm, Window islandWindow, Action openSettings, Action quit)
    {
        _islandWindow = islandWindow;

        var menu = new NativeMenu();
        menu.Add(MakeItem("显示 / 隐藏灵动岛", () => ToggleIsland()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(MakeItem("播放 / 暂停", () => vm.PlayPauseCommand.Execute(null)));
        menu.Add(MakeItem("上一首", () => vm.PreviousCommand.Execute(null)));
        menu.Add(MakeItem("下一首", () => vm.NextCommand.Execute(null)));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(MakeItem("设置…", openSettings));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(MakeItem("退出 WinIslands", quit));

        _tray = new TrayIcon
        {
            ToolTipText = "WinIslands",
            Menu = menu,
            IsVisible = true,
        };

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://WinIslands/Assets/tray.png"));
            _tray.Icon = new WindowIcon(stream);
        }
        catch { /* 图标加载失败不致命 */ }

        _tray.Clicked += (_, _) => ToggleIsland();
        TrayIcon.SetIcons(app, new TrayIcons { _tray });

        // 岛因“无媒体自动隐藏”而隐藏时，托盘可强制显示
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IslandViewModel.IslandVisible)) return;
            if (_manuallyHidden) return;
            if (vm.IslandVisible)
            {
                if (!islandWindow.IsVisible) islandWindow.Show();
            }
        };
    }

    private static NativeMenuItem MakeItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private void ToggleIsland()
    {
        if (_islandWindow.IsVisible)
        {
            _manuallyHidden = true;
            _islandWindow.Hide();
        }
        else
        {
            _manuallyHidden = false;
            _islandWindow.Show();
        }
    }

    public void Dispose() => _tray.Dispose();
}
