using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinIslands.Diagnostics;
using WinIslands.Services;
using Color = System.Windows.Media.Color;

namespace WinIslands.UI;

/// <summary>Read-only media source report shared by settings and troubleshooting.</summary>
public partial class MediaSourceCheckWindow : Window
{
    private readonly SettingsService _settings;
    private readonly CiderMediaProvider? _cider;
    private readonly ThemeService _theme;
    private CancellationTokenSource? _cts;

    public MediaSourceCheckWindow(SettingsService settings, CiderMediaProvider? cider, ThemeService theme)
    {
        _settings = settings;
        _cider = cider;
        _theme = theme;
        InitializeComponent();
        ApplyPalette();
        SourceInitialized += (_, _) =>
        {
            try
            {
                WindowEffects.ApplyDarkMode(new WindowInteropHelper(this).Handle, theme.IsDark);
            }
            catch { }
        };
        Loaded += async (_, _) => await RunCheckAsync();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    private void ApplyPalette()
    {
        Resources["CheckTextBrush"] = _theme.TextPrimary;
        Resources["CheckMutedBrush"] = _theme.TextSecondary;
        Resources["CheckAccentBrush"] = _theme.AccentBrush;
        Resources["CheckCardBrush"] = Tint(_theme.IsDark ? Colors.White : Colors.Black, _theme.IsDark ? (byte)0x1C : (byte)0x0E);
        Resources["CheckBorderBrush"] = Tint(_theme.IsDark ? Colors.White : Colors.Black, _theme.IsDark ? (byte)0x28 : (byte)0x18);
        Resources["CheckControlBrush"] = Tint(_theme.IsDark ? Colors.White : Colors.Black, _theme.IsDark ? (byte)0x1E : (byte)0x0C);
        Frosted.TintBrush = _theme.CardBackground;
        Frosted.IsDark = _theme.IsDark;
        Foreground = _theme.TextPrimary;
    }

    private static SolidColorBrush Tint(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private async Task RunCheckAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        BtnRefresh.IsEnabled = false;
        BtnCopy.IsEnabled = false;
        TxtStatus.Text = "正在检查…";
        TxtResult.Text = "正在读取媒体会话与本机播放器状态。";
        try
        {
            TxtResult.Text = await DiagnosticsCommand.RunMediaSummaryAsync(_settings, _cider, _cts.Token);
            TxtStatus.Text = TxtResult.Text.Contains("未检测到媒体会话", StringComparison.Ordinal)
                ? "未发现正在播放的媒体"
                : "检查完成";
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "检查超时";
            TxtResult.Text = "检查超时。请确认播放器正在运行，然后重试。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("Media source check failed", ex);
            TxtStatus.Text = "检查失败";
            TxtResult.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
            BtnCopy.IsEnabled = !string.IsNullOrWhiteSpace(TxtResult.Text);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunCheckAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TxtResult.Text);
            TxtStatus.Text = "结果已复制";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Copy media report failed: {ex.Message}");
            TxtStatus.Text = "复制失败，可手动选择文本";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
