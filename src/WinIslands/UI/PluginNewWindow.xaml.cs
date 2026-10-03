using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinIslands.Services;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace WinIslands.UI;

/// <summary>新建插件向导：输入 ID 与名称，自动生成最小模板（plugin.json + run.ps1）。</summary>
public partial class PluginNewWindow : Window
{
    private readonly PluginService _plugins;
    private readonly Action _onChanged;

    public PluginNewWindow(PluginService plugins, Action onChanged, bool dark)
    {
        _plugins = plugins;
        _onChanged = onChanged;
        InitializeComponent();
        ApplyTheme(dark);
        TitleText.Text = "新建插件";
        IdBox.Focus();
    }

    private void ApplyTheme(bool dark)
    {
        var bg = new SolidColorBrush(dark
            ? Color.FromArgb(0xEE, 0x1B, 0x1B, 0x26)
            : Color.FromArgb(0xEE, 0xF5, 0xF5, 0xFA));
        var border = new SolidColorBrush(dark
            ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        var text = new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF2, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x24));
        var secondary = new SolidColorBrush(dark ? Color.FromRgb(0xB8, 0xB8, 0xC8) : Color.FromRgb(0x5B, 0x5B, 0x68));
        var control = new SolidColorBrush(dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        var controlBorder = new SolidColorBrush(dark ? Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        RootBorder.Background = bg;
        RootBorder.BorderBrush = border;
        Foreground = text;
        Resources["TextPrimaryBrush"] = text;
        Resources["TextSecondaryBrush"] = secondary;
        Resources["ControlBgBrush"] = control;
        Resources["ControlBorderBrush"] = controlBorder;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var error = _plugins.CreateTemplatePlugin(IdBox.Text ?? "", NameBox.Text ?? "");
        if (error is null)
        {
            _onChanged();
            Close();
            return;
        }
        MessageBox.Show(this, error, "WinIslands", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
