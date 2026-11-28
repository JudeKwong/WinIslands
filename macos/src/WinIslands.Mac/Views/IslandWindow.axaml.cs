using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using WinIslands.Mac.Services;
using WinIslands.Mac.UI;
using WinIslands.Services;

namespace WinIslands.Mac.Views;

/// <summary>
/// 灵动岛浮窗（macOS 版）：
///   - 紧凑/展开两态，窗口尺寸用弹簧动画过渡（SpringEase 移植；动效与时长读设置）
///   - 手动拖动窗口（PointToScreen 计算屏幕位移，避免窗口移动反馈抵消）；双击/滚轮按设置响应
///   - 尺寸动画期间 16ms 帧定时器重算居中位置，保证左右对称展开
/// </summary>
public partial class IslandWindow : Window
{
    private const double CompactWidth = 300, CompactHeight = 42;
    private const double ExpandedWidth = 380, ExpandedMediaHeight = 238;
    private const int DragThreshold = 4;

    private readonly IslandViewModel _vm;
    private readonly SettingsService _settings;

    private Border? _border;
    private Grid? _compactPanel;
    private Grid? _expandedPanel;
    private Grid? _compactMedia;
    private Grid? _compactIdle;
    private Grid? _compactPush;
    private Grid? _expandedMedia;
    private Grid? _expandedPush;

    private CancellationTokenSource? _animCts;
    private DispatcherTimer? _recenterTimer;
    private DispatcherTimer? _clickTimer;
    private DispatcherTimer? _hoverExpandTimer;
    private DispatcherTimer? _hoverCollapseTimer;
    private bool _pressActive;
    private bool _dragging;
    private PixelPoint _dragStartPos;
    private PixelPoint _dragStartScreen;
    private bool _reallyClosing;
    private bool _appliedOnce;

    public IslandWindow(IslandViewModel vm, SettingsService settings)
    {
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        InitializeComponent();

        _border = this.FindControl<Border>("IslandBorder");
        _compactPanel = this.FindControl<Grid>("CompactPanel");
        _expandedPanel = this.FindControl<Grid>("ExpandedPanel");
        _compactMedia = this.FindControl<Grid>("CompactMedia");
        _compactIdle = this.FindControl<Grid>("CompactIdle");
        _compactPush = this.FindControl<Grid>("CompactPush");
        _expandedMedia = this.FindControl<Grid>("ExpandedMedia");
        _expandedPush = this.FindControl<Grid>("ExpandedPush");

        if (_border is not null)
        {
            _border.PointerPressed += OnBorderPointerPressed;
            _border.PointerMoved += OnBorderPointerMoved;
            _border.PointerReleased += OnBorderPointerReleased;
            _border.PointerWheelChanged += OnWheel;
            _border.PointerEntered += OnPointerEnter;
            _border.PointerExited += OnPointerExit;
        }

        _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer!.Stop();
            if (_settings.Current.HoverExpandEnabled && !_vm.IsExpanded)
                _vm.ToggleExpandCommand.Execute(null);
        };
        _hoverCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _hoverCollapseTimer.Tick += (_, _) =>
        {
            _hoverCollapseTimer!.Stop();
            if (_settings.Current.HoverExpandEnabled && _vm.IsExpanded)
                _vm.ToggleExpandCommand.Execute(null);
        };

        _vm.PropertyChanged += OnVmPropertyChanged;
        Opened += (_, _) =>
        {
            ApplyMode(initial: true);
            if (!_vm.IslandVisible) Hide(); // 无媒体且“无媒体时隐藏”开启：启动即隐藏
        };
        Closing += (_, e) =>
        {
            if (_reallyClosing) return;
            // 关闭按钮/⌘W：隐藏而非退出（退出走托盘菜单）
            e.Cancel = true;
            Hide();
        };
    }

    // 与 Avalonia.NameGenerator 生成代码等价（生成器需 Roslyn 4.14+，本机 SDK 为 4.11）
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>真正退出前调用（绕过“关闭即隐藏”逻辑）。</summary>
    public void CloseForQuit()
    {
        _reallyClosing = true;
        Close();
    }


    private void OnPointerEnter(object? sender, PointerEventArgs e)
    {
        _hoverCollapseTimer?.Stop();
        if (!_settings.Current.HoverExpandEnabled || _vm.IsExpanded) return;
        if (_hoverExpandTimer is null) return;
        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.HoverExpandDelayMs, 0, 1500));
        _hoverExpandTimer.Start();
    }

    private void OnPointerExit(object? sender, PointerEventArgs e)
    {
        _hoverExpandTimer?.Stop();
        if (!_settings.Current.HoverExpandEnabled || !_vm.IsExpanded) return;
        if (_hoverCollapseTimer is null) return;
        _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.HoverCollapseDelayMs, 0, 2000));
        _hoverCollapseTimer.Start();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandViewModel.IsExpanded):
            case nameof(IslandViewModel.Mode):
                ApplyMode(initial: false);
                break;
            case nameof(IslandViewModel.IslandVisible):
                if (_vm.IslandVisible) Show();
                else Hide();
                break;
        }
    }

    // ── 布局/尺寸动画 ─────────────────────────────────────────

    private void ApplyMode(bool initial)
    {
        var mode = _vm.Mode;
        var expanded = _vm.IsExpanded;

        if (_compactMedia is not null) _compactMedia.IsVisible = mode == IslandMode.Media;
        if (_compactIdle is not null) _compactIdle.IsVisible = mode == IslandMode.Idle;
        if (_compactPush is not null) _compactPush.IsVisible = mode == IslandMode.Push;
        if (_expandedMedia is not null) _expandedMedia.IsVisible = expanded && mode == IslandMode.Media;
        if (_expandedPush is not null) _expandedPush.IsVisible = expanded && mode == IslandMode.Push;

        var (targetW, targetH) = ResolveTargetSize(mode, expanded);

        if (initial || !_appliedOnce)
        {
            _appliedOnce = true;
            Width = targetW;
            Height = targetH;
            if (_compactPanel is not null) { _compactPanel.IsVisible = !expanded; _compactPanel.Opacity = expanded ? 0 : 1; }
            if (_expandedPanel is not null) { _expandedPanel.IsVisible = expanded; _expandedPanel.Opacity = expanded ? 1 : 0; }
            PositionWindow();
            return;
        }

        _ = AnimateToAsync(targetW, targetH, expanded);
    }

    private (double W, double H) ResolveTargetSize(IslandMode mode, bool expanded)
    {
        if (!expanded) return (CompactWidth, CompactHeight);
        if (mode == IslandMode.Media) return (ExpandedWidth, ExpandedMediaHeight);
        // 推送卡片：按内容行数估算高度
        var h = 74.0;
        if (_vm.PushBodyVisible) h += 36;
        if (_vm.PushImageVisible) h += 106;
        if (_vm.PushProgressVisible) h += 18;
        if (_vm.PushButtonsVisible) h += 42;
        if (_vm.PushInputVisible) h += 44;
        return (ExpandedWidth, Math.Max(96, h));
    }

    private async Task AnimateToAsync(double toW, double toH, bool expanded)
    {
        _animCts?.Cancel();
        var cts = new CancellationTokenSource();
        _animCts = cts;

        if (_compactPanel is not null) _compactPanel.IsVisible = true;
        if (_expandedPanel is not null) _expandedPanel.IsVisible = true;

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.IslandAnimationDuration, 200, 1600));
        var ease = ResolveEase();

        var fromW = Width;
        var fromH = Height;

        var sizeAnim = new Animation
        {
            Duration = duration,
            Easing = ease,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(WidthProperty, fromW), new Setter(HeightProperty, fromH) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(WidthProperty, toW), new Setter(HeightProperty, toH) },
                },
            },
        };

        var fadeDuration = TimeSpan.FromMilliseconds(expanded ? 200 : 140);
        var compactFade = FadeAnimation(_compactPanel?.Opacity ?? 1, expanded ? 0 : 1, fadeDuration);
        var expandedFade = FadeAnimation(_expandedPanel?.Opacity ?? 0, expanded ? 1 : 0, fadeDuration);

        StartRecenter();
        try
        {
            await Task.WhenAll(
                sizeAnim.RunAsync(this, cts.Token),
                compactFade.RunAsync(_compactPanel!, cts.Token),
                expandedFade.RunAsync(_expandedPanel!, cts.Token));
        }
        catch (OperationCanceledException) { }
        finally
        {
            StopRecenter();
            if (!cts.IsCancellationRequested)
            {
                if (_compactPanel is not null) { _compactPanel.IsVisible = !expanded; _compactPanel.Opacity = expanded ? 0 : 1; }
                if (_expandedPanel is not null) { _expandedPanel.IsVisible = expanded; _expandedPanel.Opacity = expanded ? 1 : 0; }
                PositionWindow();
            }
        }
    }

    private static Animation FadeAnimation(double from, double to, TimeSpan duration) => new()
    {
        Duration = duration,
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, from) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, to) } },
        },
    };

    private Easing ResolveEase() => _settings.Current.AnimationStyle switch
    {
        "Soft" => new SoftSpringEase(),
        "Elastic" => new SpringEase { Damping = 7, Stiffness = 180 },
        "Fade" => new CubicEaseOut(),
        _ => new SpringEase(),
    };

    // ── 位置 ─────────────────────────────────────────────────

    /// <summary>默认：工作区顶部居中（macOS 即菜单栏下方）；OffsetX/OffsetY 为用户拖动后的偏移。</summary>
    private void PositionWindow()
    {
        if (_dragging) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var wa = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var s = _settings.Current;

        var wPx = (int)Math.Round(Width * scaling);
        var hPx = (int)Math.Round(Height * scaling);
        var left = s.Position == IslandPosition.Right
            ? wa.X + wa.Width - wPx - (int)Math.Round(16 * scaling)
            : wa.X + (wa.Width - wPx) / 2;
        left += (int)Math.Round(s.OffsetX * scaling);
        var top = wa.Y + (int)Math.Round(Math.Max(0, s.OffsetY) * scaling);
        Position = new PixelPoint(left, top);
    }

    private void StartRecenter()
    {
        _recenterTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
            (_, _) => PositionWindow());
        _recenterTimer.Start();
    }

    private void StopRecenter() => _recenterTimer?.Stop();

    private void SavePositionFromDrag()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var wa = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var centerX = wa.X + (wa.Width - (int)Math.Round(Width * scaling)) / 2;
        var x = (Position.X - centerX) / scaling;
        var y = (Position.Y - wa.Y) / scaling;
        _settings.Update(s =>
        {
            s.OffsetX = Math.Round(x, 1);
            s.OffsetY = Math.Round(Math.Max(-40, y), 1);
        });
    }

    // ── 指针交互 ─────────────────────────────────────────────

    private void OnBorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _pressActive = true;
        _dragging = false;
        _dragStartPos = Position;
        _dragStartScreen = this.PointToScreen(e.GetPosition(this));
    }

    private void OnBorderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressActive) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressActive = false; return; }
        var cur = this.PointToScreen(e.GetPosition(this));
        var dx = cur.X - _dragStartScreen.X;
        var dy = cur.Y - _dragStartScreen.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < DragThreshold) return;
        _dragging = true;
        StopRecenter();
        Position = new PixelPoint(_dragStartPos.X + dx, _dragStartPos.Y + dy);
    }

    private void OnBorderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressActive) return;
        _pressActive = false;
        if (_dragging)
        {
            _dragging = false;
            SavePositionFromDrag();
            return;
        }
        // 单击/双击仲裁：250ms 内无第二次点击 → 单击（切换展开）；否则执行双击动作
        if (_clickTimer is not null && _clickTimer.IsEnabled)
        {
            _clickTimer.Stop();
            ExecuteDoubleClickAction();
            return;
        }
        if (_clickTimer is null)
        {
            _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _clickTimer.Tick += (_, _) =>
            {
                _clickTimer!.Stop();
                _vm.ToggleExpandCommand.Execute(null);
            };
        }
        _clickTimer.Start();
    }

    private void ExecuteDoubleClickAction()
    {
        switch ((_settings.Current.DoubleClickAction ?? "").Trim())
        {
            case "OpenSettings":
                _vm.OpenSettingsCommand.Execute(null);
                break;
            case "None":
                break;
            default:
                _vm.PlayPauseCommand.Execute(null);
                break;
        }
    }

    private async void OnWheel(object? sender, PointerWheelEventArgs e) => await _vm.AdjustVolumeAsync(e.Delta.Y > 0 ? 0.05 : -0.05);
}
