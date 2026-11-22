using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinIslands.Diagnostics;
using WinIslands.Services;
using WinIslands.UI;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace WinIslands.UI;

/// <summary>
/// Three-step first-run wizard. It only configures a small set of safe defaults
/// and can be skipped at any time; closing the window is treated as "later".
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly SettingsService _settings;
    private readonly CiderMediaProvider? _cider;
    private int _step;
    private bool _finished;
    private CancellationTokenSource? _checkCts;

    public OnboardingWindow(SettingsService settings, CiderMediaProvider? cider, ThemeService theme)
    {
        _settings = settings;
        _cider = cider;
        InitializeComponent();

        ApplyPalette(theme);
        ChkCider.IsChecked = settings.Current.CiderEnabled;
        ChkOnlineLyrics.IsChecked = settings.Current.OnlineLyricsEnabled;
        ChkNotifications.IsChecked = settings.Current.NotificationTakeoverEnabled;

        Loaded += (_, _) => BtnNext.Focus();
        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                WindowEffects.ApplyDarkMode(hwnd, theme.IsDark);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Onboarding dark mode failed: {ex.Message}");
            }
        };
        Closed += (_, _) =>
        {
            _checkCts?.Cancel();
            _checkCts?.Dispose();
            _checkCts = null;
        };
    }

    private void ApplyPalette(ThemeService theme)
    {
        Resources["OnbTextBrush"] = theme.TextPrimary;
        Resources["OnbMutedBrush"] = theme.TextSecondary;
        Resources["OnbAccentBrush"] = theme.AccentBrush;
        Resources["OnbCardBrush"] = Tint(theme.IsDark ? Colors.White : Colors.Black, (byte)(theme.IsDark ? 0x1C : 0x0E));
        Resources["OnbCardBorderBrush"] = Tint(theme.IsDark ? Colors.White : Colors.Black, (byte)(theme.IsDark ? 0x28 : 0x18));
        Resources["OnbControlBrush"] = Tint(theme.IsDark ? Colors.White : Colors.Black, (byte)(theme.IsDark ? 0x1E : 0x0C));
        Resources["OnbControlBorderBrush"] = Tint(theme.IsDark ? Colors.White : Colors.Black, (byte)(theme.IsDark ? 0x30 : 0x1C));

        Frosted.TintBrush = theme.CardBackground;
        Frosted.IsDark = theme.IsDark;
        Foreground = theme.TextPrimary;
    }

    private static SolidColorBrush Tint(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        StepWelcome.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepMedia.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepCheck.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtStep.Text = $"第 {_step + 1} 步，共 3 步";
        BtnNext.Content = _step == 2 ? "完成" : "下一步";
        if (_step == 2) BtnCheck.Focus();
        else BtnNext.Focus();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 2)
        {
            ShowStep(_step + 1);
            return;
        }
        Finish(applyChoices: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(applyChoices: true);

    private void Close_Click(object sender, RoutedEventArgs e) => Finish(applyChoices: false);

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        BtnCheck.IsEnabled = false;
        TxtCheckStatus.Text = "正在检查…";
        TxtCheckResult.Text = "正在读取 Windows 媒体会话与本机播放器状态。";
        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        try
        {
            var summary = await DiagnosticsCommand.RunMediaSummaryAsync(_settings, _cider, _checkCts.Token);
            TxtCheckResult.Text = summary;
            TxtCheckStatus.Text = summary.Contains("未检测到媒体会话", StringComparison.Ordinal)
                ? "未发现正在播放的媒体"
                : "检查完成";
        }
        catch (OperationCanceledException)
        {
            TxtCheckResult.Text = "检查超时。请确认播放器正在运行，然后重试。";
            TxtCheckStatus.Text = "检查超时";
        }
        catch (Exception ex)
        {
            AppLogger.Error("Onboarding media check failed", ex);
            TxtCheckResult.Text = "检查失败：" + ex.Message;
            TxtCheckStatus.Text = "检查失败";
        }
        finally
        {
            BtnCheck.IsEnabled = true;
        }
    }

    private void Finish(bool applyChoices)
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _settings.Update(s =>
            {
                if (applyChoices)
                {
                    s.CiderEnabled = ChkCider.IsChecked == true;
                    s.OnlineLyricsEnabled = ChkOnlineLyrics.IsChecked == true;
                    s.NotificationTakeoverEnabled = ChkNotifications.IsChecked == true;
                }
                s.OnboardingCompleted = true;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Save onboarding result failed", ex);
        }
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            _finished = true;
            try
            {
                _settings.Update(s => s.OnboardingCompleted = true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Persist onboarding close failed: {ex.Message}");
            }
        }
        base.OnClosing(e);
    }
}
