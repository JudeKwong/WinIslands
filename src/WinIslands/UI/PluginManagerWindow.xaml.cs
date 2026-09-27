using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinIslands.Services;
using Color = System.Windows.Media.Color;
using CheckBox = System.Windows.Controls.CheckBox;

namespace WinIslands.UI;

public partial class PluginManagerWindow : Window
{
    private readonly PluginService _plugins;
    private readonly ThemeService _theme;

    public PluginManagerWindow(PluginService plugins, ThemeService theme)
    {
        _plugins = plugins;
        _theme = theme;
        InitializeComponent();
        ApplyTheme();
        RefreshList();
    }

    private void RefreshList() => PluginList.ItemsSource = _plugins.Plugins.ToList();

    private void ApplyTheme()
    {
        var dark = _theme.IsDark;
        RootBorder.Background = new SolidColorBrush(dark
            ? Color.FromArgb(0xEE, 0x1B, 0x1B, 0x26)
            : Color.FromArgb(0xEE, 0xF5, 0xF5, 0xFA));
        RootBorder.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        var text = new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF2, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x24));
        var secondary = new SolidColorBrush(dark ? Color.FromRgb(0xB8, 0xB8, 0xC8) : Color.FromRgb(0x5B, 0x5B, 0x68));
        var control = new SolidColorBrush(dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        var border = new SolidColorBrush(dark ? Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        Resources["TextPrimaryBrush"] = text;
        Resources["TextSecondaryBrush"] = secondary;
        Resources["ControlBgBrush"] = control;
        Resources["ControlBorderBrush"] = border;
        Foreground = text;
    }

    private void PluginEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not PluginManifest plugin) return;
        _plugins.SetEnabled(plugin.Id, cb.IsChecked == true);
        RefreshList();
    }

    private void OpenPluginFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && Directory.Exists(path))
            OpenFolder(path);
    }

    private void OpenUserFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.PluginsDir);
        OpenFolder(AppPaths.PluginsDir);
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _plugins.Reload();
        RefreshList();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true, ArgumentList = { path } });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Open plugin folder failed: {ex.Message}");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
