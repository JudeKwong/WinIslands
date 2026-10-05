using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using WinIslands.Services;

namespace WinIslands.UI;

/// <summary>
/// iOS 风格灵动岛窗口。
/// 窗口尺寸固定（400x400、透明、点击穿透），仅内部卡片（Card）形变：
/// 紧凑 = 340x56 胶囊，展开 = 400x~384 卡片向下生长。
/// 动画只作用于单个元素，由 WPF 合成线程 60fps 驱动，避免窗口级 Resize 卡顿。
/// 点击穿透通过 WM_NCHITTEST 显式处理：卡片内可交互，卡片外穿透。
/// </summary>
public partial class IslandWindow : Window, INotifyPropertyChanged
{
    // 尺寸来自设置（可调），带安全钳制
    /// <summary>字号缩放系数：整张卡片 LayoutTransform 缩放，逻辑尺寸 = 视觉尺寸 / 缩放比。</summary>
    private double FontScale => Math.Clamp(_settings.Current.FontScale, 0.8, 1.4);
    private double ManualCompactW => Math.Clamp(_settings.Current.CompactWidth / FontScale, 240 / FontScale, 520 / FontScale);
    private double ManualCompactH => Math.Clamp(_settings.Current.CompactHeight / FontScale, 48 / FontScale, 140 / FontScale);
    /// <summary>
    /// 紧凑态最大视觉宽度（使用时除以 FontScale 得到逻辑上限）：
    /// 无推送时保持 800 上限避免岛过宽；有上岛推送时放宽到所在显示器工作区宽度（留边距），
    /// 保证长通知出现时右侧组件（媒体按钮/时钟等）不被 ClipToBounds 裁切、文字完整显示。
    /// </summary>
    private double MaxCompactVisualWidth
    {
        get
        {
            if (!_vm.HasActivePush) return 800;
            try
            {
                var workW = ScreenHelper.DpiWorkArea(_screen).Width;
                return Math.Max(800, workW - 48); // 左右各留 24 边距，避免贴到屏幕边缘
            }
            catch
            {
                return 800;
            }
        }
    }

    /// <summary>
    /// 实测紧凑内容宽度（岛可见时精确贴合组件）。
    /// 岛隐藏/未布局时返回「估算与手动值取较大者」，避免启动瞬间 Card 过窄导致组件挤压、显示不完整。
    /// </summary>
    private double MeasureCompactWidthNow()
    {
        var fallback = Math.Max(_vm.EstimatedCompactWidth, ManualCompactW);
        try
        {
            if (!IsLoaded || !_vm.IsVisible) return fallback;
            PillRow.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = PillRow.DesiredSize.Width;
            // 字号缩放：逻辑宽度按比例缩小，卡片渲染时再放大，最终视觉宽度不变
            return w >= 20 ? Math.Clamp((w + 56) / FontScale, 240 / FontScale, MaxCompactVisualWidth / FontScale) : fallback; // 总留白 56（左侧 22 + 右侧 24，右侧略多）
        }
        catch
        {
            return fallback;
        }
    }

    private double _noPushCompactW;
    private bool _noPushWValid;

    /// <summary>
    /// 推送卡片在紧凑态所需宽度：单行显示（图标 30 + 间距 8 + 标题 + 单行摘要上限 190），
    /// 摘要过长由 TextTrimming 省略，整体宽度紧凑、不大幅撑宽灵动岛。
    /// </summary>
    private double PushCardCompactWidth()
    {
        var p = _vm.ActivePush;
        if (p is null) return 0;
        double need = 38; // 图标 30 + 间距 8
        need += Math.Min(TextW(p.Title, 13, 7), 240); // 标题（SemiBold），上限 240 与 XAML MaxWidth 一致
        if (!string.IsNullOrEmpty(p.Subtitle) || !string.IsNullOrEmpty(p.Body))
        {
            var summary = !string.IsNullOrEmpty(p.Subtitle) ? p.Subtitle : p.Body;
            need += 8 + Math.Min(TextW(summary, 11.5, 6.2), 200); // 摘要单行上限 200，超出省略（与 XAML MaxWidth 一致）
        }
        return (Math.Min(need, 520) + 48) / FontScale; // +48：左右内边距(12+12) + 余量
    }

    /// <summary>估算多行文本的最宽单行宽度：中文/全角按 cjkPx，ASCII 按 asciiPx（换行符按行分离取最大值）。</summary>
    private static double TextW(string? s, double cjkPx, double asciiPx)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double max = 0;
        foreach (var line in s.Split('\n'))
        {
            double w = 0;
            foreach (var ch in line) w += ch > 0x2E7F ? cjkPx : asciiPx;
            if (w > max) max = w;
        }
        return max;
    }


    /// <summary>
    /// 紧凑宽度：有推送时取「实测（含推送卡片实际布局宽）」与「估算（无推送基准 + 推送宽）」的较大者。
    /// 实测保证任何文本都放得下（不依赖估算精度），估算兜底布局时序（首帧未布局时实测可能偏小）。
    /// </summary>
    private double CompactWidth
    {
        get
        {
            if (!_settings.Current.CompactWidthAuto) return ManualCompactW;
            var autoW = MeasureCompactWidthNow();
            if (_vm.HasActivePush)
            {
                var estBase = _noPushWValid ? _noPushCompactW : Math.Max(autoW, ManualCompactW);
                var estimated = estBase + PushCardCompactWidth();
                return Math.Clamp(Math.Max(autoW, estimated), 240 / FontScale, MaxCompactVisualWidth / FontScale);
            }
            _noPushCompactW = autoW;
            _noPushWValid = true;
            return autoW;
        }
    }
    private double _noPushCompactH;   // 无上岛推送时的紧凑高度（缓存）
    private bool _noPushHValid;

    /// <summary>
    /// 紧凑高度：有上岛推送时以推送内容高度为准（无推送基准高度 与「推送卡片高度 + 上下内边距(6+6)」取较大者），
    /// 保证副标题/正文/进度/按钮完整显示，不再被 ClipToBounds 上下裁切、文字上移。
    /// </summary>
    private double CompactHeight
    {
        get
        {
            if (!_settings.Current.CompactHeightAuto) return ManualCompactH; // 手动模式：高度恒定
            if (_vm.HasActivePush)
            {
                var pushVisualH = _vm.PushCompactHeight;                          // 推送卡片内容高度（视觉 DIP）
                var baseVisualH = _noPushHValid ? _noPushCompactH * FontScale
                                                : Math.Clamp(_vm.EstimatedCompactHeight, 48, 224);
                var visual = Math.Clamp(Math.Max(baseVisualH, pushVisualH + 12), 48, 236); // +12 = ContentGrid 上下 Margin 6+6
                return visual / FontScale;
            }
            _noPushCompactH = _vm.EstimatedCompactHeight / FontScale;
            _noPushHValid = true;
            return _noPushCompactH;
        }
    }
    private double ExpandedWidth => _settings.Current.ExpandedWidthAuto
        ? _vm.EstimatedExpandedWidth / FontScale
        : Math.Clamp(_settings.Current.ExpandedWidth / FontScale, CompactWidth, 800 / FontScale);
    private double MaxExpandedHeight => _settings.Current.MaxExpandedHeightAuto
        ? _vm.EstimatedExpandedHeight / FontScale
        : Math.Clamp(_settings.Current.MaxExpandedHeight / FontScale, 240 / FontScale, 620 / FontScale);

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    private readonly IslandViewModel _vm;
    private readonly ThemeService _theme;
    private readonly SettingsService _settings;
    private readonly System.Windows.Forms.Screen _screen;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _compactRestoreTimer;
    private readonly DispatcherTimer _memoryTrimTimer;
    private readonly EventHandler _onThemeChanged;      // 具名处理器：窗口关闭时可退订，防泄漏
    private readonly EventHandler<AppSettings> _onSettingsChanged;
    private NotifyCollectionChangedEventHandler? _historyChangedHandler;
    private bool _waveRendering;                  // 波纹渲染中（已挂接合成帧事件）
    private DispatcherTimer? _waveTimer;                  // 低功耗模式：波纹定时器（~120fps）
    private double _lastWaveTime;
    private double _nextWaveFrameTime;
    private string _cachedWaveStyle = "Bars";
    private string? _appliedWaveStyle;
    private double _cachedWaveHeight = 1.0;
    private int _cachedWaveFps = 120;                 // 当前波形帧率目标
    private readonly System.Diagnostics.Stopwatch _waveClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<ScaleTransform> _waveBarsExpanded = new();
    private readonly List<ScaleTransform> _waveBarsCompact = new();
    // 备选波纹样式（频谱/环形/粒子）
    private readonly List<ScaleTransform> _waveSpectrumExpanded = new();
    private readonly List<ScaleTransform> _waveSpectrumCompact = new();
    private ScaleTransform? _waveRingScaleExpanded;
    private ScaleTransform? _waveRingScaleCompact;
    private readonly List<TranslateTransform> _waveParticleTransformsExpanded = new();
    private readonly List<TranslateTransform> _waveParticleTransformsCompact = new();
    private static readonly double[] WaveSin09 = Enumerable.Range(0, 32).Select(i => Math.Sin(i * 0.9)).ToArray();
    private static readonly double[] WaveCos09 = Enumerable.Range(0, 32).Select(i => Math.Cos(i * 0.9)).ToArray();
    private static readonly double[] WaveSin13 = Enumerable.Range(0, 32).Select(i => Math.Sin(i * 1.3)).ToArray();
    private static readonly double[] WaveCos13 = Enumerable.Range(0, 32).Select(i => Math.Cos(i * 1.3)).ToArray();
    private bool _animationSurfaceActive;
    private bool _cardLayoutRoundingBeforeAnimation = true;
    private bool _cardSnapsBeforeAnimation = true;
    private Storyboard? _currentStoryboard;
    private RectangleGeometry? _cardClip;   // 圆角裁剪几何：动画中亚像素尺寸把圆角渲染成方框，用它从根源裁出圆角边界
    private bool _visibilityRefreshQueued;
    private bool _compactAnimationValid;
    private double _compactAnimationWidth;
    private double _compactAnimationHeight;
    // ── iOS 真弹簧物理动画（2.0.5）：卡片尺寸 / 展开淡入 / 窗口位置 / 推送卡片 ──
    private IOSSpring? _cardWSpring;      // 卡片宽度弹簧
    private IOSSpring? _cardHSpring;      // 卡片高度弹簧
    private IOSSpring? _cardFadeSpring;   // 展开↔收起内容交叉淡入弹簧（与尺寸同步，文字不"额外弹出"）
    private IOSSpring? _cardRSpring;      // 卡片圆角弹簧（2.0.7）：圆角随形变连续过渡，消除"圆角变方框"与形变结束时圆角闪变
    private bool _cardAnimating;          // 卡片弹簧进行中（声波 / 取色渲染让路）
    private double _cardTargetW;
    private double _cardTargetH;
    private Action? _cardSpringCompleted; // 卡片弹簧收敛后的完成回调（兼容旧 Storyboard 语义）
    private bool _expandedFadeIn = true;   // 2.0.9：当前淡入方向（true=展开，false=收起），供胶囊行交叉淡入曲线使用
    // 2.1.6：展开目标高度缓存——展开动画期间内容（歌词行/封面/推送）变化时复用已测高度，
    // 不再每次 Expand() 重新 Measure，避免卡片在展开途中“呼吸”抖动；内容变化置脏标记，
    // 缓存只在 Expand() 时消费，下一次展开前重新测量（展开中更新的脏标记零成本）。
    private double _expandedTargetHeight = double.NaN;
    private bool _expandedHeightValid;
    private bool _expandedHeightDirty = true;
    private IOSSpring? _posLSpring;       // 窗口 Left 弹簧
    private IOSSpring? _posTSpring;       // 窗口 Top 弹簧
    private IOSSpring? _pushOpacitySpring;// 推送卡片透明度弹簧
    private IOSSpring? _pushScaleSpring;  // 推送卡片缩放弹簧
    private Storyboard? _coverTransitionStoryboard;
    private Storyboard? _pushCardStoryboard;
    private Storyboard? _glassAnimSb;               // 玻璃分层不透明度动画（可随时重开/停止）
    /// <summary>展开态玻璃叠加目标不透明度：从基础 88% 叠加到 ≈97%（随用户 Opacity 缩放）。</summary>
    private double GlassTargetOpacity
    {
        get
        {
            var op = Math.Clamp(_theme.Opacity, 0.3, 1.0);
            var denom = 1.0 - 0.88 * op;
            if (denom < 0.05) return 1.0;
            return Math.Clamp(0.09 * op / denom, 0, 1);
        }
    }

    /// <summary>2.2.0: ReduceMotion 开关，供文本切换过渡判断是否退化为直接切换。</summary>
    internal bool IsReduceMotionEnabled => _settings.Current.ReduceMotion;

    /// <summary>展开内容交错过渡区块（自上而下）：上岛推送 / Hero / 封面标题 / 进度 / 控制 / 歌词快捷 / 歌词 / 快捷操作。
    /// 1.2.1：展开时依次淡入上移、收起时反向淡出下移，仿 iOS 灵动岛错峰进出。</summary>
    private (FrameworkElement El, TranslateTransform Tr)[] _cascadeBlocks = Array.Empty<(FrameworkElement, TranslateTransform)>();

    /// <summary>为展开内容各区块挂接位移变换（供交错过渡动画使用）。</summary>
    private static (FrameworkElement, TranslateTransform)[] BuildCascadeBlocks(params FrameworkElement?[] els)
    {
        var list = new List<(FrameworkElement, TranslateTransform)>(els.Length);
        foreach (var el in els)
        {
            if (el is null) continue;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            if (el.RenderTransform is not TranslateTransform tr)
            {
                tr = new TranslateTransform();
                el.RenderTransform = tr;
            }
            list.Add((el, tr));
        }
        return list.ToArray();
    }

    /// <summary>ReduceMotion / 兜底：直接设置所有交错区块的透明度与位移，跳过动画。</summary>
    private void ApplyCascadeState(double opacity, double y)
    {
        foreach (var (el, tr) in _cascadeBlocks)
        {
            el.Opacity = opacity;
            tr.Y = y;
        }
    }
    /// <summary>
    /// iOS content parallax (2.1.0): the expanded content's scale + vertical offset
    /// are driven by the same spring as its opacity, so text visibly "grows into place"
    /// while expanding and gently retreats while collapsing - matching the card morph
    /// instead of statically fading in/out.
    /// </summary>
    private void ApplyContentParallax(double v, bool expand)
    {
        if (ExpandedScale is null || ExpandedTranslate is null) return;
        var (scale, y) = CrossFadeCurves.ContentParallax(v, expand);
        ExpandedScale.ScaleX = scale;
        ExpandedScale.ScaleY = scale;
        ExpandedTranslate.Y = y;
    }

    /// <summary>Snap parallax transforms back to neutral at settle.</summary>
    private void ResetContentParallax()
    {
        if (ExpandedScale is null || ExpandedTranslate is null) return;
        ExpandedScale.ScaleX = ExpandedScale.ScaleY = 1;
        ExpandedTranslate.Y = 0;
    }
    private Storyboard? _positionStoryboard;   // 位置动画独占：连续重定位先停旧动画
    private HwndSource? _hwndSource;
    private CoverFullScreenWindow? _coverFullWindow;   // #2 封面沉浸：全屏封面预览窗口

    // ── #8 动态主题：封面取色背景缓慢呼吸（60fps 合成帧驱动，仅在展开+取色开启时运行）──
    private System.Windows.Media.Color? _tintCoverColor;   // 已采样的封面主色（变化时重建 brush）
    private LinearGradientBrush? _tintBrush;               // 封面取色渐变（缓存，避免每帧重建 GC）
    private GradientStop? _tintStop0;
    private GradientStop? _tintStop1;
    private DateTime _tintPhaseUtc;
    // 封面取色缓存：避免展开/收起时重复采样同一封面
    private ImageSource? _lastSampledArtwork;
    private System.Windows.Media.Color? _lastSampledColor;                        // 呼吸相位起点
    private bool _tintRenderingSubscribed;
    private DispatcherTimer? _tintTimer;
    private static readonly CubicEase CachedCubicEaseOut = CreateCubicEase(EasingMode.EaseOut);
    private static readonly CubicEase CachedCubicEaseIn = CreateCubicEase(EasingMode.EaseIn);
    private static readonly SpringEase CachedSpringEase = FreezeEase(new SpringEase { Damping = 11, Stiffness = 220, Mass = 1 });
    // 收起专用弹簧：高阻尼（ζ≈0.86，过冲量 <1%）——收紧时丝滑收敛、几乎无 Q 弹，接近 Apple 收起质感
    private static readonly SpringEase CachedCollapseEase = FreezeEase(new SpringEase { Damping = 26, Stiffness = 230, Mass = 1 });
    private static readonly SoftSpringEase CachedSoftEase = FreezeEase(new SoftSpringEase { Damping = 15, Stiffness = 220, Mass = 1 });
    private static readonly SoftSpringEase CachedSoftEaseSmooth = FreezeEase(new SoftSpringEase { Damping = 18, Stiffness = 250, Mass = 1 });
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DependencyProperty, PropertyPath> AnimationPathCache = new();
    private static readonly ElasticEase CachedElasticEase = FreezeEase(new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut });

    private static T FreezeEase<T>(T ease) where T : Freezable
    {
        ease.Freeze();
        return ease;
    }

    private static CubicEase CreateCubicEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }
    // 缓存上岛推送画刷（避免每次属性访问都 new SolidColorBrush）
    private Brush? _cachedPushBg, _cachedPushBorder, _cachedPushFg, _cachedPushSecondary;
    private bool _pushDarkCache;
    // 缓存歌词画刷（避免每次访问都 new SolidColorBrush）
    private Brush? _cachedExpLyricBase, _cachedExpLyricHL, _cachedCmpLyricBase, _cachedCmpLyricHL;
    private string? _cachedLyricBaseHex, _cachedLyricHLHex;
    private bool _lyricBrushDark;


    public System.Windows.Forms.Screen Screen { get; }

    public IslandWindow(IslandViewModel vm, ThemeService theme, SettingsService settings, System.Windows.Forms.Screen screen)
    {
        _vm = vm;
        _theme = theme;
        _settings = settings;
        _screen = screen;
        Screen = screen;

        DataContext = vm;
        InitializeComponent();

        // 展开内容交错过渡区块（功能 2）：为各区块挂载位移变换，供展开/收起错峰动画使用
        _cascadeBlocks = BuildCascadeBlocks(ExpandedPushCard, HeroCard, ArtTitleGrid, ProgressGrid,
            ControlsGrid, LyricQuickOpsPanel, LyricsScroll, QuickActionsPanel);

        // 圆角裁剪：ClipToBounds 只是矩形裁剪，动画中亚像素尺寸会导致圆角边缘被矩形截断（"圆角变方框"）。
        // 用 RectangleGeometry 做 Card.Clip，从根源保证任何子内容都画不出圆角边界。
        var clipRadius = Math.Clamp(_settings.Current.CornerRadius, 16, 40);
        _cardClip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, Math.Max(1, Card.ActualWidth), Math.Max(1, Card.ActualHeight)),
            RadiusX = clipRadius,
            RadiusY = clipRadius,
        };
        Card.Clip = _cardClip;
        Card.SizeChanged += OnCardSizeChanged;

        // 收起延迟（鼠标移出展开态 700ms 后收起）
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            _vm.IsExpanded = false;
        };

        // 收起动画可能被快速切换打断导致 Card 尺寸残留：动画结束后兜底恢复精确紧凑尺寸
        _compactRestoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _compactRestoreTimer.Tick += (_, _) =>
        {
            _compactRestoreTimer.Stop();
            // 注意：这里不要碰 _memoryTrimTimer——内存修剪是常驻节电服务，
            // 若在每次收起时被 Stop，空闲内存回收将永久停用（2.1.5 修复）。
            if (IsLoaded && !_vm.IsExpanded)
            {
                Card.BeginAnimation(FrameworkElement.WidthProperty, null);
                Card.BeginAnimation(FrameworkElement.HeightProperty, null);
                Card.Width = CompactWidth;
                Card.Height = CompactHeight;
                ContentGrid.RowDefinitions[1].Height = GridLength.Auto;
            }
        };

        _memoryTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _memoryTrimTimer.Tick += (_, _) =>
        {
            // 2.2.0：播放中也可修剪——只要没有正在运行的弹簧动画（卡片/位置/推送）且未展开，
            // 后台 GC 不会造成可见卡顿；媒体播放由外部进程驱动，不受影响。
            if (_vm.IsExpanded) return;
            if (_cardAnimating || SpringTicker.ActiveCount > 0) return;
            MemoryOptimizer.RequestTrim();
        };
        _memoryTrimTimer.Start();


        // 声音波纹：挂接合成帧事件，按显示器刷新率驱动，空闲时摘除不占 CPU
        if (WaveBar1 is not null)
        {
            _waveBarsExpanded.AddRange(new[] { WaveBar1, WaveBar2, WaveBar3, WaveBar4, WaveBar5, WaveBar6, WaveBar7 });
            _waveBarsCompact.AddRange(new[] { WaveBarC1, WaveBarC2, WaveBarC3, WaveBarC4, WaveBarC5, WaveBarC6, WaveBarC7 });
        }
        InitWaveVisualStyles();

        // 悬停不展开；移出时若已展开则延迟收起
        Card.MouseLeave += (_, _) =>
        {
            if (_vm.IsExpanded) _collapseTimer.Start();
        };

        // 双击检测：单击延迟 280ms 后切换展开/收起；窗口内第二次单击则执行快捷动作
        _clickDebounce.Tick += (_, _) =>
        {
            _clickDebounce.Stop();
            if (!_pendingClick) return;
            _pendingClick = false;
            if (_toggleDoneOnDown)
            {
                // #7 点击抢先：MouseDown 已立即切换，这里只是等待双击窗口，不再重复切换
                _toggleDoneOnDown = false;
                return;
            }
            _collapseTimer.Stop();
            _vm.IsExpanded = !_vm.IsExpanded;
        };

        // 点击展开/收起；解锁状态下支持鼠标拖动
        Card.PreviewMouseLeftButtonDown += OnCardMouseLeftButtonDown;
        Card.PreviewMouseMove += OnCardMouseMove;
        Card.PreviewMouseLeftButtonUp += OnCardMouseLeftButtonUp;
        Card.PreviewMouseUp += OnCardMiddleMouseUp;   // 中键快捷操作

        // 进度条拖拽 seek
        ProgressSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _vm.BeginSeek()));
        ProgressSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, _) => await _vm.EndSeekAsync(ProgressSlider.Value)));

        _onThemeChanged = (_, _) => ApplyTheme();
        _onSettingsChanged = (_, _) =>
        {
            _expandedHeightDirty = true; // 设置变化（展开宽度/字号等）影响展开高度，下次展开前重测（2.1.6）
            ApplyExpandedSectionVisibility();
            ApplyAppearance();
            RefreshWave();
            ApplyCoverTint();
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MarqueeEnabled)));
        };
        _vm.PropertyChanged += OnVmPropertyChanged;
        _theme.ThemeChanged += _onThemeChanged;
        _settings.Changed += _onSettingsChanged;
        _historyChangedHandler = (_, _) => RefreshNotificationHistoryProps();
        _vm.NotificationHistory.CollectionChanged += _historyChangedHandler;
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshNotificationHistoryProps();

        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => ScheduleVisibilityRefresh();
        // 触摸屏交互：滑动切歌、点击展开/收起
        TouchDown += OnTouchDown;
        TouchUp += OnTouchUp;
        ManipulationStarted += OnManipulationStarted;
        ManipulationCompleted += OnManipulationCompleted;
        ManipulationDelta += OnManipulationDelta;
        // 触摸滚动：展开后可用手指滚动歌词
        PreviewTouchDown += (_, _) => { /* 确保触摸事件不被子元素吞掉 */};
        DpiChanged += (_, _) =>
        {
            _expandedHeightDirty = true; // DPI 变化影响字体渲染尺寸，展开目标高度缓存失效，下次展开前重测（2.1.7）
            Reposition();
            ApplySize(); // DPI 变化时重新计算卡片尺寸（多显示器不同缩放比场景）
            ApplyAppearance(); // 刷新外观确保字体渲染正确
        };
        Closed += OnWindowClosed; // 关闭时退订外部事件源，避免 RecreateWindows 重建后事件泄漏
    }

    /// <summary>窗口关闭：退订外部事件并停止本窗口定时器 / 渲染循环，防止内存与 CPU 泄漏。</summary>
    // ── 定位 ──────────────────────────────────────────────────
    private DateTime _touchStartTime;
    private Point _touchStartPoint;
    private bool _touchManipulating;

    private void OnTouchDown(object? sender, TouchEventArgs e)
    {
        try
        {
            var tp = e.GetTouchPoint(this);
            _touchStartTime = DateTime.Now;
            _touchStartPoint = tp.Position;
            _touchManipulating = false;
            // 启用操作：允许滑动手势
            if (_vm.IsExpanded)
            {
                // 展开状态下允许触摸滚动
                e.Handled = false;
            }
        }
        catch { }
    }

    private void OnTouchUp(object? sender, TouchEventArgs e)
    {
        try
        {
            if (_touchManipulating) return; // 滑动手势已处理，不再触发点击
            var tp = e.GetTouchPoint(this);
            var delta = tp.Position - _touchStartPoint;
            var duration = DateTime.Now - _touchStartTime;
            // 短时间、小位移 = 点击（展开/收起）
            if (duration < TimeSpan.FromMilliseconds(300) && Math.Abs(delta.X) < 20 && Math.Abs(delta.Y) < 20)
            {
                _vm.IsExpanded = !_vm.IsExpanded;
            }
        }
        catch { }
    }

    private void OnManipulationStarted(object? sender, ManipulationStartedEventArgs e)
    {
        _touchManipulating = true;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        // 展开状态下允许触摸垂直滚动歌词
        if (_vm.IsExpanded)
        {
            e.Handled = false; // 让内部 ScrollViewer 处理
        }
    }

    private void OnManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        try
        {
            // 水平滑动手势：左滑 = 下一首，右滑 = 上一首
            var totalX = e.TotalManipulation.Translation.X;
            var totalY = e.TotalManipulation.Translation.Y;
            if (Math.Abs(totalX) > Math.Abs(totalY) && Math.Abs(totalX) > 50)
            {
                if (totalX < 0)
                    _vm.NextCommand.Execute(null);
                else
                    _vm.PreviousCommand.Execute(null);
                e.Handled = true;
            }
        }
        catch { }
        finally
        {
            _touchManipulating = false;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        EndAnimationSurface();
        _glassAnimSb?.Stop();
        try
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _theme.ThemeChanged -= _onThemeChanged;
            _settings.Changed -= _onSettingsChanged;
            if (_historyChangedHandler is not null)
                _vm.NotificationHistory.CollectionChanged -= _historyChangedHandler;
            Localization.LanguageChanged -= OnLanguageChanged;
            _collapseTimer.Stop();
            _compactRestoreTimer.Stop();
            _memoryTrimTimer.Stop();
            StopLyricsScroll();
            CancelPendingClick();
            StopWaveRender();
            SubscribeTintRendering(false); // 显式退订封面取色合成帧，防窗口销毁后事件泄漏
            // 关闭可能存在的全屏封面预览窗口，避免孤儿窗口
            if (_coverFullWindow is { } cfw)
            {
                try { cfw.Close(); } catch { /* ignore */ }
                _coverFullWindow = null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Island window cleanup failed", ex);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Theme passthrough for XAML bindings.
    public Brush TextPrimary => _theme.TextPrimary;
    public Brush TextSecondary => _theme.TextSecondary;
    public Brush AccentBrush => _theme.AccentBrush;
    public Brush AccentBorderBrush => _theme.AccentBorderBrush;
    public Brush CardBackground => _theme.CardBackground;
    public Brush CardBorder => _theme.CardBorder;
    public Brush ButtonHoverBrush => _theme.ButtonHoverBrush;
    public Brush SliderTrackBrush => _theme.SliderTrackBrush;
    public Brush SliderThumbBrush => _theme.SliderThumbBrush;

    // Lyric style passthrough (1.2.3): adjustable lyric font size, current-line size,
    // line spacing, karaoke speed and highlight/base colors.
    public double LyricBaseFontSize => Math.Max(9, _settings.Current.LyricFontSize);
    public double LyricCurrentFontSize => Math.Max(12, Math.Max(LyricBaseFontSize + 3, _settings.Current.LyricCurrentFontSize));
    public double LyricLineHeight
    {
        get
        {
            var s = Math.Clamp(_settings.Current.LyricLineSpacing, 0.5, 2.5);
            return Math.Max(16, LyricBaseFontSize * 1.55 * s);
        }
    }
    public Thickness LyricLineMargin
    {
        get
        {
            var s = Math.Clamp(_settings.Current.LyricLineSpacing, 0.5, 2.5);
            return new Thickness(0, 2.5 * s, 0, 2.5 * s);
        }
    }
    public double LyricKaraokeSpeed => Math.Clamp(_settings.Current.KaraokeSpeed, 0.2, 3.0);

    private static System.Windows.Media.Color BrushColor(Brush b) => (b as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.White;
    private static Brush FreezeBrush(System.Windows.Media.Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static System.Windows.Media.Color ParseHexColor(string? hex, System.Windows.Media.Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex.Trim()); }
            catch { /* invalid hex -> fallback */ }
        }
        return fallback;
    }
    public Brush ExpandedLyricBaseBrush
    {
        get
        {
            var dark = _theme.IsDark;
            var hex = _settings.Current.LyricBaseColor ?? "";
            if (_cachedExpLyricBase is null || _lyricBrushDark != dark || _cachedLyricBaseHex != hex)
            {
                _lyricBrushDark = dark;
                _cachedLyricBaseHex = hex;
                _cachedExpLyricBase = FreezeBrush(ParseHexColor(hex, BrushColor(_theme.TextSecondary)));
            }
            return _cachedExpLyricBase;
        }
    }
    public Brush ExpandedLyricHighlightBrush
    {
        get
        {
            var dark = _theme.IsDark;
            var hex = _settings.Current.LyricHighlightColor ?? "";
            if (_cachedExpLyricHL is null || _lyricBrushDark != dark || _cachedLyricHLHex != hex)
            {
                _cachedLyricHLHex = hex;
                _cachedExpLyricHL = FreezeBrush(ParseHexColor(hex, BrushColor(_theme.TextPrimary)));
            }
            return _cachedExpLyricHL;
        }
    }
    public Brush CompactLyricBaseBrush
    {
        get
        {
            var dark = _theme.IsDark;
            if (_cachedCmpLyricBase is null || _lyricBrushDark != dark)
            {
                var bc = BrushColor(_theme.TextSecondary);
                _cachedCmpLyricBase = FreezeBrush(System.Windows.Media.Color.FromArgb(96, bc.R, bc.G, bc.B));
            }
            return _cachedCmpLyricBase;
        }
    }
    public Brush CompactLyricHighlightBrush
    {
        get
        {
            var dark = _theme.IsDark;
            if (_cachedCmpLyricHL is null || _lyricBrushDark != dark)
                _cachedCmpLyricHL = FreezeBrush(BrushColor(_theme.TextPrimary));
            return _cachedCmpLyricHL;
        }
    }

    public bool NotificationHistoryVisible => _settings.Current.NotificationHistoryEnabled && _vm.NotificationHistory.Count > 0;
    public string NotificationHistoryTitle => Localization.Get("Notifications_History");
    public string NotificationHistoryClearText => Localization.Get("Notifications_HistoryClear");


    // ── 上岛推送卡片主题（#17：第三方可指定 dark / light，auto 跟随应用明暗）──
    /// <summary>推送卡片是否按深色渲染（auto 跟随应用主题）。</summary>
    private bool PushDark() => _vm.ActivePushTheme?.Trim().ToLowerInvariant() switch
    {
        "dark" => true,
        "light" => false,
        _ => _theme.IsDark,
    };

    public Brush PushCardBackground
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushBg is null || _pushDarkCache != dark)
            {
                _pushDarkCache = dark;
                _cachedPushBg = FreezeBrush(dark ? System.Windows.Media.Color.FromArgb(0xE6, 0x1B, 0x1B, 0x26) : System.Windows.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            }
            return _cachedPushBg;
        }
    }
    public Brush PushCardBorder
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushBorder is null || _pushDarkCache != dark)
            {
                _cachedPushBorder = FreezeBrush(dark ? System.Windows.Media.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : System.Windows.Media.Color.FromArgb(0x40, 0x00, 0x00, 0x00));
            }
            return _cachedPushBorder;
        }
    }
    public Brush PushCardForeground
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushFg is null || _pushDarkCache != dark)
            {
                _cachedPushFg = FreezeBrush(dark ? System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF7) : System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x2A));
            }
            return _cachedPushFg;
        }
    }
    public Brush PushCardSecondary
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushSecondary is null || _pushDarkCache != dark)
            {
                _cachedPushSecondary = FreezeBrush(dark ? System.Windows.Media.Color.FromArgb(0xCC, 0xC8, 0xC8, 0xD4) : System.Windows.Media.Color.FromArgb(0xCC, 0x55, 0x55, 0x60));
            }
            return _cachedPushSecondary;
        }
    }

    /// <summary>上岛推送主题变化时刷新推送卡片画刷。</summary>
    private void RaisePushThemeProps()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardBackground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardBorder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardForeground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardSecondary)));
    }

    // ── 展开卡片分区块开关（来自设置，绑定到展开内容）──
    // 歌曲相关区域仅在“有媒体播放”时显示；只有上岛推送时展开态以上岛内容为主，避免空歌曲区
    public bool ExpandedShowArtTitle => _vm.HasMedia && _settings.Current.ExpandedShowArtTitle
        && _settings.Current.ExpandedCardStyle != "Hero"; // Hero 大卡片模板下隐藏经典小封面区
    /// <summary>媒体大卡片模板（Hero）：大封面背景 + 歌名/歌手/专辑叠加。</summary>
    public bool ExpandedHeroCard => _vm.HasMedia && _settings.Current.ExpandedCardStyle == "Hero";

    public bool ExpandedShowProgress => _vm.HasMedia && _settings.Current.ExpandedShowProgress;
    public bool ExpandedShowControls => _vm.HasMedia && _settings.Current.ExpandedShowControls;
    public bool ExpandedShowLyrics => _vm.HasMedia && _settings.Current.ExpandedShowLyrics;

    /// <summary>多媒体来源选择器可见性（#3：有媒体且多个会话并存时显示）。</summary>
    public bool MediaSessionPickerVisible => _vm.HasMedia && _vm.HasMultipleSessions;
    /// <summary>歌词来源一键切换按钮可见性（设置中开启「歌词来源切换」后显示，便于快速换源）。</summary>
    public bool LyricSourcePickVisible => _settings.Current.LyricsSourcePick;

    // ── 单行模式：紧凑态所有组件一行显示 ──
    public bool SingleLineMode => _settings.Current.SingleLineMode;
    /// <summary>跑马灯开关（歌名/歌词超宽时横向滚动）。</summary>
    public bool MarqueeEnabled => _settings.Current.MarqueeTextEnabled;
    // 声音波纹：播放中 + 开启波纹设置 + 岛可见才显示（空闲时停止计时器）
    public bool HasWave => IsVisible && _vm.IsVisible && WindowState != WindowState.Minimized && _vm.HasMedia && _vm.IsPlaying && _settings.Current.WaveVisualizerEnabled;

    // 上岛推送内容：单行模式下只显示图标+标题（隐藏正文/进度/按钮）
    public bool PushShowBody => _vm.ActivePushHasBody && !SingleLineMode;
    public bool PushShowProgress => _vm.ActivePushHasProgress && !SingleLineMode;
    public bool PushShowButtons => _vm.ActivePushHasButtons && !SingleLineMode;
    public bool PushShowInput => _vm.HasPushInput && !SingleLineMode;

    // ── 点击展开 / 解锁拖动 / 右键菜单 ─────────────────────────

    private Point _downPoint;
    private bool _mouseDownOnCard;
    private bool _draggedCard;
    private readonly DispatcherTimer _clickDebounce = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private bool _pendingClick;
    private bool _toggleDoneOnDown;   // #7 点击抢先：MouseDown 已切换，双击窗口到期后不再重复切换
    private bool _isExpandedBeforeToggle; // #7 修复：拖动开始时还原按下时已切换的展开状态
    private Point _lastClickUp;

    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownOnCard = true;
        _draggedCard = false;
        _downPoint = e.GetPosition(this);

        // #7 点击抢先（A方案）：按下立即切换展开/收起，不等 280ms 双击窗口，手感跟手。
        // 交互元素（按钮/滑块，按钮自己处理点击）、上岛推送整卡回跳、封面沉浸大图各自处理，不在此切换。
        if (!IsInteractiveElement(e.OriginalSource) && !IsWithinPushCard(e.OriginalSource) && !IsCoverElement(e.OriginalSource))
        {
            _toggleDoneOnDown = true;
            _isExpandedBeforeToggle = _vm.IsExpanded;
            _collapseTimer.Stop();
            _vm.IsExpanded = !_vm.IsExpanded;
        }
    }

    private void OnCardMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_mouseDownOnCard || e.LeftButton != MouseButtonState.Pressed) return;
        if (_fileDragArmed) return; // 文件中转站组件：拖动由组件自己的拖出逻辑处理
        if (_settings.Current.IsLocked) return; // 上锁不可拖动

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _downPoint.X) > 4 || Math.Abs(pos.Y - _downPoint.Y) > 4)
        {
            // #7 修复：解锁拖动时按下已立即切换展开，这里还原，避免「想拖动却展开」
            if (_toggleDoneOnDown)
            {
                _toggleDoneOnDown = false;
                _vm.IsExpanded = _isExpandedBeforeToggle;
            }
            _mouseDownOnCard = false;
            _draggedCard = true;
            CancelPendingClick();
            _collapseTimer.Stop();
            try { DragMove(); } catch { /* ignore */ }
            e.Handled = true;
        }
    }

    private void OnCardMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedCard)
        {
            _draggedCard = false;
            CancelPendingClick();
            SnapAndPersistPosition();
            return;
        }
        if (!_mouseDownOnCard) return;
        _mouseDownOnCard = false;

        // 点击按钮/滑块不触发展开切换（按钮自己处理点击）
        if (IsInteractiveElement(e.OriginalSource)) return;

        // #2 封面沉浸：点击展开态的大封面/大卡（BigArt/HeroCard）打开全屏封面预览
        if (IsCoverElement(e.OriginalSource))
        {
            CancelPendingClick();
            OpenCoverFullScreen();
            e.Handled = true;
            return;
        }

        // 上岛推送整卡点击回跳：点在推送卡片上且配置了 click 时，执行回跳而不展开
        if (_vm.ActivePushHasClick && IsWithinPushCard(e.OriginalSource))
        {
            CancelPendingClick();
            _vm.ExecutePushClick();
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(this);
        // 双击：与上一次单击距离相近且在窗口期内 → 执行快捷动作
        if (_pendingClick && _clickDebounce.IsEnabled &&
            Math.Abs(pos.X - _lastClickUp.X) < 24 && Math.Abs(pos.Y - _lastClickUp.Y) < 24)
        {
            CancelPendingClick();
            ExecuteDoubleClickAction();
            e.Handled = true;
            return;
        }

        // 单击：挂起，等待双击窗口超时后再切换展开/收起
        _pendingClick = true;
        _lastClickUp = pos;
        _clickDebounce.Stop();
        _clickDebounce.Start();
        e.Handled = true;
    }

    /// <summary>点击番茄钟组件：暂停/继续。</summary>
    private void OnCardMiddleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        CancelPendingClick();
        ExecuteQuickAction(_settings.Current.MiddleClickAction);
    }

    private void CancelPendingClick()
    {
        _pendingClick = false;
        _clickDebounce.Stop();
        _toggleDoneOnDown = false;
    }

    /// <summary>双击快捷动作（在设置-通用中配置）：播放/暂停、展开/收起、显示桌面、隐藏/显示、切歌、打开设置或无动作。</summary>
    private void ExecuteDoubleClickAction() => ExecuteQuickAction(_settings.Current.DoubleClickAction);

    /// <summary>中键快捷动作（在设置-通用中配置，与双击动作同值域）。</summary>
    private void ExecuteMiddleClickAction() => ExecuteQuickAction(_settings.Current.MiddleClickAction);

    /// <summary>按动作名执行快捷操作；未知动作回退为播放/暂停。</summary>
    private void ExecuteQuickAction(string action)
    {
        switch (action)
        {
            case "OpenSettings":
                _vm.OpenSettingsCommand.Execute(null);
                break;
            case "ToggleExpand":
                _collapseTimer.Stop();
                _vm.IsExpanded = !_vm.IsExpanded;
                break;
            case "ShowDesktop":
                Services.SystemShell.ShowDesktop();
                break;
            case "ToggleVisible":
                _vm.ToggleUserVisible();
                break;
            case "NextTrack":
                if (_vm.CanNext) _vm.NextCommand.Execute(null);
                break;
            case "PrevTrack":
                if (_vm.CanPrevious) _vm.PreviousCommand.Execute(null);
                break;
            case "None":
                break;
            default: // PlayPause
                if (_vm.CanPlayPause)
                    _vm.PlayPauseCommand.Execute(null);
                break;
        }
    }

    /// <summary>判断点击源是否位于封面沉浸元素（展开大封面 BigArt / 媒体大卡 HeroCard）上。</summary>
    private static bool IsCoverElement(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is Border b && (b.Name == "BigArt" || b.Name == "HeroCard"))
                return true;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>#2 封面沉浸：打开全屏封面预览（同屏最大化，点击/Esc/右键关闭）。</summary>
    private void OpenCoverFullScreen()
    {
        try
        {
            if (!_vm.HasMedia || _vm.Artwork is null) return;
            if (_coverFullWindow is { IsVisible: true })
            {
                _coverFullWindow.Close();
                return;
            }
            _coverFullWindow = new CoverFullScreenWindow(_vm.Artwork, _screen);
            _coverFullWindow.Closed += (_, _) => _coverFullWindow = null;
            _coverFullWindow.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Cover fullscreen failed: {ex.Message}");
        }
    }

    /// <summary>判断点击源是否位于上岛推送卡片内部。</summary>
    private bool IsWithinPushCard(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (ReferenceEquals(d, CompactPushCard) || ReferenceEquals(d, ExpandedPushCard))
                return true;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static bool IsInteractiveElement(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or Slider
                or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton
                or System.Windows.Controls.TextBox)   // 上岛输入框：点击输入不触发展开/收起
                return true;
            // 文件中转站组件：整个组件视为交互元素（点击不展开、拖动交给拖出逻辑）
            if (d is FrameworkElement { Tag: string tag } && tag == "FileTransfer") return true;
            // Run/Inline 等 ContentElement 不是 Visual，VisualTreeHelper.GetParent 会抛异常，
            // 需沿逻辑树向上（歌词 Run → TextBlock），到达 UIElement 后继续沿视觉树。
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        MenuLock.Header = _settings.Current.IsLocked
            ? Localization.Get("Island_Unlock")
            : Localization.Get("Island_Lock");
        MenuOnlineLyrics.Header = Localization.Get("Island_OnlineLyrics");
        MenuOnlineLyrics.IsChecked = _settings.Current.OnlineLyricsEnabled;
    }

    /// <summary>点击番茄钟组件：暂停/继续。</summary>
    private void TimerItem_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleTimerPause();
        e.Handled = true;
    }

    /// <summary>点击输入法组件：切换中/英输入法。</summary>
    private void InputMethodItem_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleInputMethod();
        e.Handled = true;
    }

    /// <summary>点击快捷开关（Button.Tag: wifi / bluetooth / night / mute）。</summary>

    /// <summary>插件组件点击：执行插件声明的 URL / 程序 / 命令动作。</summary>
    private void PluginItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IslandViewModel.IslandComponent item && item.Click is not null)
            _vm.ExecutePushAction(item.Click);
        e.Handled = true;
    }

    // 记录每个插件组件最近一次渲染的文本，用于判断“内容是否真的变了”
    private readonly Dictionary<string, string> _pluginFadeLastText = new();

    /// <summary>
    /// 插件组件加载（重建/内容变化）时做一次短暂的淡入过渡（C1）。
    /// 只有当该组件的文本与上次不同时才动画，避免无变化的闪烁；新出现的组件同样淡入。
    /// </summary>
    private void PluginItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.DataContext is not IslandViewModel.IslandComponent item || !item.IsPlugin) return;
        if (!item.Kind.StartsWith("Plugin:", StringComparison.Ordinal)) return;

        // 组件 ToolTip 为空时回退为完整文本，配合超长截断
        if (border.ToolTip is null or "")
        {
            try { border.ToolTip = item.Text; } catch { }
        }

        var key = item.Kind;
        var text = item.Text ?? string.Empty;
        if (_pluginFadeLastText.TryGetValue(key, out var old) && old == text)
            return; // 内容未变化，跳过动画
        _pluginFadeLastText[key] = text;

        border.BeginAnimation(UIElement.OpacityProperty, null);
        var fade = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        border.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void QuickToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string which)
            _vm.ToggleQuickSwitch(which);
        e.Handled = true;
    }

    /// <summary>歌词翻译开关：显示 / 隐藏翻译行。</summary>
    private void LyricTranslate_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleLyricTranslation();
        e.Handled = true;
    }

    /// <summary>歌词来源一键切换（多歌词源）：点击循环 Auto → 本地 → AMLL → Cider → 在线，并立即重载歌词。</summary>
    private void LyricSourceSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.CycleLyricsSource();
        e.Handled = true;
    }

    /// <summary>复制当前歌词句到剪贴板。</summary>
    private void CopyCurrentLyric_Click(object sender, RoutedEventArgs e)
    {
        _vm.CopyCurrentLyric();
        e.Handled = true;
    }

    // #4 歌词时间微调：本曲歌词提前 / 延后 0.5 秒（立即生效并保存）
    private void LyricOffsetDown_Click(object sender, RoutedEventArgs e)
    {
        _vm.AdjustLyricTime(-0.5);
        e.Handled = true;
    }

    private void LyricOffsetUp_Click(object sender, RoutedEventArgs e)
    {
        _vm.AdjustLyricTime(0.5);
        e.Handled = true;
    }


    /// <summary>多播放器切换（#3）：点击循环切换到下一个可用媒体来源。</summary>
    private void MediaSessionCycle_Click(object sender, RoutedEventArgs e)
    {
        _vm.CycleMediaSession();
        e.Handled = true;
    }

    /// <summary>快捷操作按钮点击：按 Tag（操作键）执行对应系统动作。</summary>
    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key) _vm.ExecuteQuickAction(key);
    }

    /// <summary>上岛推送按钮点击：执行动作（打开 URL / 启动程序）后关闭当前推送。</summary>
    private void PushButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IslandPushButton button)
        {
            _vm.ExecutePushAction(button);
        }
        e.Handled = true;
    }

    /// <summary>上岛输入框提交：把用户输入按推送方配置的动作执行（默认 notify 回传）。</summary>
    private void PushInputSubmit_Click(object sender, RoutedEventArgs e)
    {
        // 提交输入执行推送动作（默认 notify 回传），随后关闭当前推送卡片
        _vm.SubmitPushInput();
        _vm.DismissActivePush();
        e.Handled = true;
    }

    private void MenuOnlineLyrics_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.OnlineLyricsEnabled = !s.OnlineLyricsEnabled);
        _ = _vm.RefreshLyricsAsync();
        Card_ContextMenuOpening(sender, null!);
    }

    private void MenuCenterAlign_Click(object sender, RoutedEventArgs e)
    {
        // 上下不变，左右居中；居中后的位置持久化（拖动过再居中对齐同样生效）
        var work = ScreenHelper.DpiWorkArea(_screen);
        var cardPos = Card.TransformToAncestor(this).Transform(new Point(0, 0));
        var cardCenterInWindow = cardPos.X + Card.ActualWidth / 2;
        var left = work.Left + work.Width / 2 - cardCenterInWindow;
        _settings.Update(s =>
        {
            s.IslandManualLeft = left;
            s.IslandManualTop ??= Top;
        });
        AnimatePosition(left, Top);
    }

    private void MenuLock_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.IsLocked = !s.IsLocked);
        Card_ContextMenuOpening(sender, null!);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 点击穿透：卡片外返回 HTTRANSPARENT
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        ApplyTheme();
        ApplySize();
        ApplyCardAlignment();
        Reposition();
        if (_vm.IsVisible) ShowIsland(instant: true);
        else Hide();
    }

    /// <summary>刷新玻璃分层底色为当前主题底色（冻结缓存，避免每帧重建）。</summary>
    private void ApplyGlassLayer()
    {
        if (GlassLayer is null) return;
        var b = new SolidColorBrush(_theme.TintColor);
        b.Freeze();
        GlassLayer.Background = b;
    }

    /// <summary>智能透明度分层：展开时玻璃层平滑升不透明度（卡片更实），收起回落（更通透）；
    /// 封面取色生效时玻璃归零，避免双重叠加。动画时长跟随当前动效皮肤，连贯不生硬。</summary>
    private void AnimateGlass(bool expanded)
    {
        if (GlassLayer is null) return;
        var tintActive = expanded && _settings.Current.CoverTintBackground && _vm.Artwork != null;
        var target = tintActive ? 0 : (expanded ? GlassTargetOpacity : 0);
        _glassAnimSb?.Stop();
        _glassAnimSb = null;
        if (_settings.Current.ReduceMotion)
        {
            GlassLayer.Opacity = target;
            return;
        }
        var (styleEase, styleMs) = GetSizeAnimationStyle(expanded);
        var dur = (int)Math.Clamp(styleMs * 0.72, 200, 900);
        var sb = new Storyboard();
        AddAnim(sb, GlassLayer, UIElement.OpacityProperty, target, dur, styleEase);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps（跟随显示器刷新率）
        _glassAnimSb = sb;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_glassAnimSb, sb)) return;
            _glassAnimSb = null;
            GlassLayer.BeginAnimation(UIElement.OpacityProperty, null);
            GlassLayer.Opacity = target;
        };
        sb.Begin();
    }

    /// <summary>主题切换平滑过渡（1.2.1）：明暗/主题色变化时，卡片背景与边框做 EaseOut 颜色插值，
    /// 避免深浅色切换闪变。封面取色生效时背景由取色渐变接管（已有呼吸动画），跳过背景只动画边框；
    /// ReduceMotion / 未加载时直接切新主题。时长跟随当前动效皮肤，与其他动画节奏一致。</summary>
    private void AnimateThemeColors(System.Windows.Media.Color? prevBg, System.Windows.Media.Color? prevBd)
    {
        try
        {
            var tintActive = _settings.Current.CoverTintBackground && _vm.IsExpanded && _vm.Artwork != null;
            if (!IsLoaded || _settings.Current.ReduceMotion)
            {
                if (!tintActive) Card.Background = _theme.CardBackground;
                Card.BorderBrush = _theme.CardBorder;
                return;
            }
            var (ease, ms) = GetSizeAnimationStyle(_vm.IsExpanded);
            var dur = TimeSpan.FromMilliseconds(Math.Clamp(ms * 0.34, 180, 460));
            if (!tintActive && prevBg is System.Windows.Media.Color pb && _theme.CardBackground is SolidColorBrush nb)
                AnimateSolidBrush(Card, Border.BackgroundProperty, pb, nb.Color, dur, ease);
            if (prevBd is System.Windows.Media.Color pbd && _theme.CardBorder is SolidColorBrush nbd)
                AnimateSolidBrush(Card, Border.BorderBrushProperty, pbd, nbd.Color, dur, ease);
        }
        catch
        {
            // 插值动画异常时直接应用新主题，绝不影响主流程
            if (!(_settings.Current.CoverTintBackground && _vm.IsExpanded && _vm.Artwork != null))
                Card.Background = _theme.CardBackground;
            Card.BorderBrush = _theme.CardBorder;
        }
    }

    /// <summary>把旧颜色安装到临时 brush 上并播放到新颜色的插值动画（HoldEnd 保色，对象由动画持有）。</summary>
    private void AnimateSolidBrush(DependencyObject target, DependencyProperty prop, System.Windows.Media.Color from, System.Windows.Media.Color to, TimeSpan dur, IEasingFunction ease)
    {
        var brush = new SolidColorBrush(from);
        target.SetValue(prop, brush);
        var anim = new ColorAnimation(to, dur) { EasingFunction = ease };
        anim.Completed += (_, _) =>
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void ApplyTheme()
    {
        // 主题切换平滑过渡（1.2.1）：先记录当前卡片背景/边框颜色，供插值动画使用
        var prevBg = (Card.Background as SolidColorBrush)?.Color;
        var prevBd = (Card.BorderBrush as SolidColorBrush)?.Color;
        ApplyMenuTheme();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextPrimary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextSecondary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonHoverBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SliderTrackBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SliderThumbBrush)));
        RaisePushThemeProps();
        ApplyAppearance();
        RefreshWave();
        ApplyCoverTint(forceRebuild: true); // 主题变化时强制重建取色渐变（基色随新主题）
        AnimateThemeColors(prevBg, prevBd); // 背景/边框颜色插值过渡，深浅色切换不闪变
        ApplyGlassLayer();
        AnimateGlass(_vm.IsExpanded); // 主题/明暗切换后玻璃底色与不透明度同步刷新
    }

    /// <summary>展开卡片分区块的可见性随设置即时刷新。</summary>
    private void ApplyExpandedSectionVisibility()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowArtTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedHeroCard)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowControls)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowLyrics)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SingleLineMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowBody)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowButtons)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowInput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaSessionPickerVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricSourcePickVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricBaseFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricCurrentFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricLineHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricLineMargin)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricKaraokeSpeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLyricBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLyricHighlightBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLyricBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLyricHighlightBrush)));
        RefreshNotificationHistoryProps();
    }

    /// <summary>按设置调整窗口与卡片尺寸（紧凑/展开）。仅当窗口尺寸真正变化时才重定位，
    /// 避免上锁/其它设置变更把用户拖动后的位置弹回默认。</summary>
    /// <summary>
    /// 确保透明窗口尺寸足够容纳当前卡片（含紧凑态自动宽度），
    /// 否则卡片超出窗口边界会被裁剪。紧凑卡片视觉宽度 = CompactWidth × FontScale。
    /// </summary>
    private void EnsureWindowSizeFits(double? knownCompactWidth = null)
    {
        if (!IsLoaded) return;
        var settingExpanded = Math.Clamp(_settings.Current.ExpandedWidth, 300, 620);
        var settingMaxH = Math.Clamp(_settings.Current.MaxExpandedHeight, 240, 620);
        var compactWidth = knownCompactWidth ?? CompactWidth;
        var w = Math.Max(settingExpanded, Math.Max(compactWidth * FontScale + 8, 640)) + 24;
        var h = Math.Max(settingMaxH, 220) + 24;
        if (Math.Abs(Width - w) > 0.5 || Math.Abs(Height - h) > 0.5)
        {
            Width = w;
            Height = h;
            Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public void ApplySize()
    {
        // 窗口固定为能容纳最大推送卡片/展开内容的大小：推送时只需动画 Card 形变，避免窗口级 Resize 卡顿
        var targetCompactWidth = CompactWidth;
        var targetCompactHeight = CompactHeight;
        EnsureWindowSizeFits(targetCompactWidth);
        if (!_vm.IsExpanded)
        {
            Card.Width = targetCompactWidth;
            Card.Height = targetCompactHeight;
        }
        // 自动调节尺寸时：胶囊行左侧额外留白（左侧横向距离更大），手动模式保持对称
        PillRow.Margin = _settings.Current.CompactWidthAuto
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(0);
        // 60fps 优化：紧凑行固定为紧凑内容宽度，展开/收起动画期间不随 Card 宽度变化逐帧重排
        UpdateCompactContentWidth(targetCompactWidth);
    }

    /// <summary>应用外观参数：圆角 / 字体 / 字号缩放。字号缩放作用于整张卡片（LayoutTransform），
    /// 逻辑尺寸同步除以缩放比，最终视觉尺寸与设置一致、不溢出不裁剪。</summary>
    /// <summary>Rebuilds the LyricLineText style from user settings (font sizes, spacing, colors).
    /// WPF cannot bind DoubleAnimation.To, so the current-line grow/shrink storyboard is built in code.</summary>
    private void UpdateLyricLineStyle()
    {
        try
        {
            if (LyricsList is null) return;
            var baseSize = LyricBaseFontSize;
            var currentSize = LyricCurrentFontSize;
            var lineHeight = LyricLineHeight;
            var margin = LyricLineMargin;
            var baseBrush = ExpandedLyricBaseBrush;
            var highlightBrush = ExpandedLyricHighlightBrush;

            // 2.1.2：当前行强调改为「渲染级缩放」（LyricEmphasis），不再动画 FontSize。
            // FontSize 会触发测量/布局，放大的行挤压相邻行、触发换行，列表回流导致跳动；
            // 只缩放 RenderTransform 时行高不变、页面不回流，文字过渡丝滑且零布局开销。
            var targetScale = LyricEmphasis.ComputeTargetScale(baseSize, currentSize);
            const double emphasisMs = 240;

            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.FontSizeProperty, baseSize));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, baseBrush));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, margin));
            style.Setters.Add(new Setter(TextBlock.LineHeightProperty, lineHeight));
            style.Setters.Add(new Setter(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight));
            style.Setters.Add(new Setter(TextBlock.OpacityProperty, 0.28));
            style.Setters.Add(new Setter(TextBlock.RenderTransformOriginProperty, new Point(0.5, 0.5)));
            style.Setters.Add(new Setter(LyricEmphasis.TargetScaleProperty, targetScale));
            style.Setters.Add(new Setter(LyricEmphasis.DurationMsProperty, emphasisMs));

            var inSb = new Storyboard();
            var fadeIn = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 } };
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath(TextBlock.OpacityProperty));
            inSb.Children.Add(fadeIn);

            var outSb = new Storyboard();
            var fadeOut = new DoubleAnimation { To = 0.28, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new SoftSpringEase { Damping = 16, Stiffness = 160, Mass = 1 } };
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(TextBlock.OpacityProperty));
            outSb.Children.Add(fadeOut);

            var trigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding(nameof(LyricLineViewModel.IsCurrent)) { Mode = BindingMode.OneWay },
                Value = true,
            };
            AnimationFrameRate.Apply(inSb, _settings.Current.LowPowerMode);
            AnimationFrameRate.Apply(outSb, _settings.Current.LowPowerMode);
            trigger.EnterActions.Add(new BeginStoryboard { Storyboard = inSb });
            trigger.ExitActions.Add(new BeginStoryboard { Storyboard = outSb });
            trigger.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, highlightBrush));
            trigger.Setters.Add(new Setter(LyricEmphasis.IsCurrentProperty, true));
            style.Triggers.Add(trigger);

            Resources["LyricLineText"] = style;
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateLyricLineStyle failed", ex);
        }
    }
    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshNotificationHistoryProps();

    private void RefreshNotificationHistoryProps()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationHistoryVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationHistoryTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationHistoryClearText)));
    }

    private void NotificationHistory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EventHistoryItem item)
            _vm.ReplayNotification(item);
    }

    private void ClearNotificationHistory_Click(object sender, RoutedEventArgs e)
        => _vm.ClearNotificationHistory();

    /// <summary>按真实布局边距更新紧凑内容宽度，避免右侧文字和媒体按钮被裁切。</summary>
    private void UpdateCompactContentWidth(double? knownCompactWidth = null)
    {
        var horizontalChrome = ContentGrid.Margin.Left + ContentGrid.Margin.Right
            + PillRow.Margin.Left + PillRow.Margin.Right;
        PillRow.Width = Math.Max(80, (knownCompactWidth ?? CompactWidth) - horizontalChrome);
    }

    /// <summary>卡片尺寸变化时同步圆角裁剪几何（防止亚像素尺寸导致圆角变方框）。</summary>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e) => FinalizeCardShape();

    /// <summary>重建圆角裁剪 RectangleGeometry 并强制重绘卡片/玻璃层，让动画收尾后圆角干净利落。</summary>
    private void FinalizeCardShape()
    {
        if (_cardClip is null || Card is null) return;
        try
        {
            var w = Math.Max(1, Card.ActualWidth);
            var h = Math.Max(1, Card.ActualHeight);
            var r = ComputeCardRuntimeRadius(h, _settings.Current.CornerRadius);
            if (Math.Abs(_cardClip.Rect.Width - w) < 0.5
                && Math.Abs(_cardClip.Rect.Height - h) < 0.5
                && Math.Abs(_cardClip.RadiusX - r) < 0.5)
                return; // 几何未变化，无需重绘
            _cardClip.Rect = new Rect(0, 0, w, h);
            // 形变中圆角由 _cardRSpring 驱动；空闲时（或设置变化）按最终尺寸直接写，避免两者抢写打架
            if (_cardRSpring is not { IsActive: true })
            {
                _cardClip.RadiusX = r;
                _cardClip.RadiusY = r;
                Card.CornerRadius = new CornerRadius(r);
                if (GlassLayer is not null) GlassLayer.CornerRadius = new CornerRadius(r);
            }
            Card.InvalidateVisual();
            if (GlassLayer is not null) GlassLayer.InvalidateVisual();
        }
        catch { /* 布局过程中的瞬时异常直接忽略 */ }
    }

    private void ApplyAppearance()
    {
        try { System.Windows.Documents.TextElement.SetFontFamily(Card, new System.Windows.Media.FontFamily(_settings.Current.FontFamily)); } catch { /* 非法字体名忽略 */ }
        var hh = Card.ActualHeight > 4 ? Card.ActualHeight : (double.IsFinite(CompactHeight) ? CompactHeight : 56);
        var rounded = new CornerRadius(ComputeCardRuntimeRadius(hh, _settings.Current.CornerRadius));
        Card.CornerRadius = rounded;
        // 同步圆角裁剪几何：设置变化即刻生效，防止旧几何残留导致方角
        if (_cardClip is not null)
        {
            _cardClip.RadiusX = rounded.TopLeft;
            _cardClip.RadiusY = rounded.TopLeft;
        }
        // 玻璃分层与卡片同步圆角，避免展开时矩形四角露出（深浅色方框的根因）
        if (GlassLayer is not null) GlassLayer.CornerRadius = rounded;
        // 字体缩放 = 1 时清空 LayoutTransform（走普通布局路径，动画期间布局更轻、更快）；
        // 只有用户设置缩放时才使用 ScaleTransform，避免无谓的变换开销。
        Card.LayoutTransform = Math.Abs(FontScale - 1.0) < 0.001 ? null : new ScaleTransform(FontScale, FontScale);
        UpdateLyricLineStyle();
        ApplySize();
    }

    /// <summary>推送到达/更新/过期时：Card 尺寸用弹簧动画平滑过渡到新大小（丝滑不生硬）。</summary>
    private void AnimateCompactSize()
    {
        if (!IsLoaded) return;
        if (_vm.IsExpanded) { ApplySize(); return; }
        var targetWidth = CompactWidth;
        var targetHeight = CompactHeight;
        EnsureWindowSizeFits(targetWidth); // 先扩宽窗口，避免卡片动画期间超出窗口被裁切
        UpdateCompactContentWidth(targetWidth);
        if (_currentStoryboard is not null && _compactAnimationValid
            && Math.Abs(targetWidth - _compactAnimationWidth) < 0.5
            && Math.Abs(targetHeight - _compactAnimationHeight) < 0.5)
            return;

        if (_currentStoryboard is null
            && Math.Abs(Card.ActualWidth - targetWidth) < 0.5
            && Math.Abs(Card.ActualHeight - targetHeight) < 0.5)
        {
            Card.Width = targetWidth;
            Card.Height = targetHeight;
            return;
        }

        if (_settings.Current.ReduceMotion)
        {
            _currentStoryboard?.Stop();
            _currentStoryboard = null;
            _compactAnimationValid = false;
            Card.Width = targetWidth;
            Card.Height = targetHeight;
            return;
        }

        // 默认 Spring 动效：真实 iOS 弹簧物理平滑调整紧凑尺寸
        if (_settings.Current.AnimationStyle == "Spring")
        {
            AnimateCompactSizeSpring(targetWidth, targetHeight);
            return;
        }

        BeginAnimationSurface();

        var (styleEase, styleMs) = GetSizeAnimationStyle(expand: false);
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0;
        var fromWidth = ResolveAnimationFrom(Card.ActualWidth, Card.Width, targetWidth);
        var fromHeight = ResolveAnimationFrom(Card.ActualHeight, Card.Height, targetHeight);
        var dur = (int)Math.Clamp(360 * (styleMs / 680.0), 220, 460) * lm;
        // 停止前一个动画（AnimateCard 或 AnimateCompactSize），避免两个 Storyboard 同时写 Card 尺寸
        _currentStoryboard?.Stop();
        var sb = new Storyboard();
        AddAnim(sb, Card, FrameworkElement.WidthProperty, targetWidth, (int)dur, styleEase, from: fromWidth);
        AddAnim(sb, Card, FrameworkElement.HeightProperty, targetHeight, (int)dur, styleEase, from: fromHeight);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps（跟随显示器刷新率）
        _compactAnimationValid = true;
        _compactAnimationWidth = targetWidth;
        _compactAnimationHeight = targetHeight;
        _currentStoryboard = sb; // 更新引用：防止 AnimateCard 完成回调覆盖新尺寸
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_currentStoryboard, sb)) return;
            _currentStoryboard = null;
            _compactAnimationValid = false;
            Card.BeginAnimation(FrameworkElement.WidthProperty, null);
            Card.BeginAnimation(FrameworkElement.HeightProperty, null);
            Card.Width = targetWidth;
            Card.Height = targetHeight;
            FinalizeCardShape(); // 收尾：圆角裁剪与最终尺寸精确对齐
        };
        sb.Begin();
    }

    /// <summary>第三方应用上岛：推送卡片淡入 + 轻微缩放的丝滑动画。</summary>
    private void PlayPushCardAnimation()
    {
        if (!IsLoaded || CompactPushCard is null || !_vm.HasActivePush) return;
        CompactPushCard.Opacity = 0;
        CompactPushScale.ScaleX = CompactPushScale.ScaleY = 0.94;
        // 默认 Spring 动效：真实弹簧淡入 + 轻微缩放，推送卡片自然"上岛"
        if (_settings.Current.AnimationStyle == "Spring")
        {
            _pushCardStoryboard?.Stop();
            _pushCardStoryboard = null;
            CompactPushCard.BeginAnimation(UIElement.OpacityProperty, null);
            CompactPushScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactPushScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            EnsurePushSprings();
            var lmPush = _settings.Current.LowPowerMode ? 0.65 : 1.0;
            _pushOpacitySpring!.Configure(1.0, 0.3 * lmPush);
            _pushOpacitySpring.Start(0, 1);
            _pushScaleSpring!.Configure(0.84, 0.5 * lmPush);
            _pushScaleSpring.Start(0.94, 1);
            return;
        }
        var sb = new Storyboard();
        var (styleEase, styleMs) = GetSizeAnimationStyle(expand: true);
        var smooth = CachedCubicEaseOut;
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0;
        var scaleDur = (int)(Math.Min(340, styleMs) * lm);
        AddAnim(sb, CompactPushCard, UIElement.OpacityProperty, 1, (int)(220 * lm), smooth);
        AddAnim(sb, CompactPushScale, ScaleTransform.ScaleXProperty, 1, scaleDur, styleEase);
        AddAnim(sb, CompactPushScale, ScaleTransform.ScaleYProperty, 1, scaleDur, styleEase);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps（跟随显示器刷新率）
        _pushCardStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pushCardStoryboard, sb)) return;
            _pushCardStoryboard = null;
            CompactPushCard.BeginAnimation(UIElement.OpacityProperty, null);
            CompactPushScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CompactPushScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CompactPushCard.Opacity = 1;
            CompactPushScale.ScaleX = CompactPushScale.ScaleY = 1;
        };
        sb.Begin();
    }
    /// <summary>右键菜单主题色（圆角液态玻璃）。</summary>
    private void ApplyMenuTheme()
    {
        void Add(string key, Brush b) { b.Freeze(); Resources[key] = b; }
        SolidColorBrush bg, border, text, hover;
        if (_theme.IsDark)
        {
            bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0x1B, 0x1B, 0x26));
            border = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
            text = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF7));
            hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0xF5, 0xF5, 0xFA));
            border = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            text = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x24));
            hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        }
        Add("MenuBgBrush", bg);
        Add("MenuBorderBrush", border);
        Add("MenuTextBrush", text);
        Add("MenuHoverBrush", hover);

        // 直接设置菜单背景/前景，保证即使资源查找失败也不会出现白底
        if (IslandMenu is not null)
        {
            IslandMenu.Background = bg;
            IslandMenu.Foreground = text;
            IslandMenu.BorderBrush = border;
        }
    }
    private void ApplyCardAlignment()
    {
        Card.HorizontalAlignment = _settings.Current.Position == IslandPosition.Right
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Center;
        Card.Margin = _settings.Current.Position == IslandPosition.Right
            ? new Thickness(0, 0, 4, 0)
            : new Thickness(0);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandViewModel.IsVisible):
                if (_vm.IsVisible) ShowIsland(instant: false);
                else HideIsland();
                ApplySize(); // 岛显示/隐藏时重新测量自动尺寸
                RefreshWave(); // 若启动时已有媒体在播放，确保波纹定时器在岛显示后启动
                break;
            case nameof(IslandViewModel.IsExpanded):
                AnimateSize();
                ApplyCoverTint(); // 展开/收起时同步封面取色呼吸（#8 动态主题）
                if (_vm.IsExpanded && _vm.LyricIndex >= 0)
                    Dispatcher.BeginInvoke(() => ScrollLyricsTo(_vm.LyricIndex), DispatcherPriority.Loaded);
                if (!_vm.IsExpanded) _compactRestoreTimer.Start(); // 收起后兜底恢复精确尺寸，避免多次切换后上下间距异常
                break;
            case nameof(IslandViewModel.LyricIndex):
                if (_vm.LyricIndex >= 0) QueueLyricsScroll(_vm.LyricIndex);
                break;
            case nameof(IslandViewModel.CompactItems):
                // 组件列表变化（如音量指示出现/消失、临时状态胶囊增删）时平滑调整尺寸
                if (!_vm.IsExpanded && _vm.IsVisible) AnimateCompactSize();
                break;
            case nameof(IslandViewModel.CurrentLyricText):
                // 当前歌词行变化时，若处于紧凑态则平滑调整宽度，避免长歌词被裁切/遮挡
                if (!_vm.IsExpanded && _vm.IsVisible) AnimateCompactSize();
                break;
            case nameof(IslandViewModel.HasActivePush):
                _expandedHeightDirty = true; // 推送内容会影响展开卡片高度，下次展开前重测（2.1.6）
                ApplySize();           // 确保窗口足够大（首次）
                AnimateCompactSize();  // 尺寸变化：弹簧动画，丝滑
                PlayPushCardAnimation(); // 上岛卡片：淡入 + 缩放动画
                ApplyExpandedSectionVisibility();
                RaisePushThemeProps();
                break;
            case nameof(IslandViewModel.HasMedia):
                _expandedHeightDirty = true; // 媒体信息区出现/消失影响展开高度（2.1.6）
                ApplyExpandedSectionVisibility();
                RefreshWave();
                break;
            case nameof(IslandViewModel.HasMultipleSessions):
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaSessionPickerVisible)));
                break;
            case nameof(IslandViewModel.IsPlaying):
                RefreshWave();
                break;
            case nameof(IslandViewModel.Artwork):
                _expandedHeightDirty = true; // 封面加载完成可能改变展开内容高度（2.1.6）
                ApplyCoverTint();
                PlayCoverTransition(); // 切歌：封面交叉淡入 + 轻微缩放
                break;
        }
    }

    /// <summary>切歌时封面过渡：紧凑封面 / 展开大封面 / Hero 背景统一做「淡入 + 轻微缩放」，
    /// 与 CoverTint 背景呼吸互补，换曲衔接丝滑不生硬。</summary>
    private void PlayCoverTransition()
    {
        if (!IsLoaded) return;
        if (_settings.Current.ReduceMotion) return;
        var smooth = CachedCubicEaseOut;
        var (_, styleMs) = GetSizeAnimationStyle(expand: true);
        var dur = (int)Math.Clamp(styleMs * 0.42, 180, 420);
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0;
        _coverTransitionStoryboard?.Stop();
        var sb = new Storyboard();

        if (BigArt is not null)
        {
            BigArt.BeginAnimation(UIElement.OpacityProperty, null);
            BigArt.Opacity = 0.35;
            AddAnim(sb, BigArt, UIElement.OpacityProperty, 1, (int)(dur * lm), smooth);
        }
        if (BigArtScale is not null)
        {
            BigArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BigArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BigArtScale.ScaleX = BigArtScale.ScaleY = 1.06;
            AddAnim(sb, BigArtScale, ScaleTransform.ScaleXProperty, 1, (int)(dur * lm), smooth);
            AddAnim(sb, BigArtScale, ScaleTransform.ScaleYProperty, 1, (int)(dur * lm), smooth);
        }
        if (HeroCard is not null)
        {
            HeroCard.BeginAnimation(UIElement.OpacityProperty, null);
            HeroCard.Opacity = 0.35;
            AddAnim(sb, HeroCard, UIElement.OpacityProperty, 1, (int)(dur * lm), smooth);
        }
        foreach (var b in FindVisualChildren<System.Windows.Controls.Border>(PillRow))
        {
            if (!ReferenceEquals(b.Tag, "SongCover")) continue;
            b.BeginAnimation(UIElement.OpacityProperty, null);
            b.Opacity = 0.35;
            AddAnim(sb, b, UIElement.OpacityProperty, 1, (int)(dur * lm), smooth);
        }

        if (sb.Children.Count == 0) return;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_coverTransitionStoryboard, sb)) return;
            _coverTransitionStoryboard = null;
            if (BigArt is not null) { BigArt.BeginAnimation(UIElement.OpacityProperty, null); BigArt.Opacity = 1; }
            if (BigArtScale is not null) { BigArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); BigArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); BigArtScale.ScaleX = BigArtScale.ScaleY = 1; }
            if (HeroCard is not null) { HeroCard.BeginAnimation(UIElement.OpacityProperty, null); HeroCard.Opacity = 1; }
            foreach (var b in FindVisualChildren<System.Windows.Controls.Border>(PillRow))
            {
                if (!ReferenceEquals(b.Tag, "SongCover")) continue;
                b.BeginAnimation(UIElement.OpacityProperty, null);
                b.Opacity = 1;
            }
        };
        _coverTransitionStoryboard = sb;
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode);
        sb.Begin();
    }
    /// <summary>从可视树上收集指定类型子元素（浅层遍历，仅用于切歌时的封面定位）。</summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    // ── 声音波纹 / 封面取色 ─────────────────────────────────────

    private void ScheduleVisibilityRefresh()
    {
        if (_visibilityRefreshQueued) return;
        _visibilityRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _visibilityRefreshQueued = false;
            RefreshWave();
            ApplyCoverTint();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }
    private void RefreshWave()
    {
        var on = HasWave;
        _cachedWaveStyle = _settings.Current.WaveStyle ?? "Bars";
        _cachedWaveHeight = Math.Clamp(_settings.Current.WaveHeight, 0.25, 2.0);
        ApplyWaveStyleVisibility();
        var lowPower = _settings.Current.LowPowerMode;
        _cachedWaveFps = AnimationFrameRate.Current(lowPower);
        // 三态：关闭 / 普通（CompositionTarget.Rendering 跟随显示器）/ 低功耗定时器（~120fps）
        var wantTimer = on && lowPower;
        var wantComposition = on && !lowPower;
        var isTimer = _waveTimer is not null;
        var isComposition = _waveRendering && !isTimer;
        if (wantTimer != isTimer || wantComposition != isComposition)
        {
            StopWaveRender();
            _nextWaveFrameTime = _waveClock.Elapsed.TotalSeconds;
            if (wantTimer)
            {
                _waveRendering = true;
                _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };     // CompositionTarget.Rendering（跟随显示器刷新率）
                _waveTimer.Tick += (_, _) => OnWaveFrame(null, EventArgs.Empty);
                _waveTimer.Start();
            }
            else if (wantComposition)
            {
                _waveRendering = true;
                CompositionTarget.Rendering += OnWaveFrame;
            }
        }
        if (!on)
        {
            foreach (var sc in _waveBarsExpanded) sc.ScaleY = 0.16;
            foreach (var sc in _waveBarsCompact) sc.ScaleY = 0.16;
            foreach (var sc in _waveSpectrumExpanded) sc.ScaleY = 0.05;
            foreach (var sc in _waveSpectrumCompact) sc.ScaleY = 0.05;
            if (_waveRingScaleExpanded is not null) { _waveRingScaleExpanded.ScaleX = _waveRingScaleExpanded.ScaleY = 1.0; }
            if (_waveRingScaleCompact is not null) { _waveRingScaleCompact.ScaleX = _waveRingScaleCompact.ScaleY = 1.0; }
            foreach (var tr in _waveParticleTransformsExpanded) tr.Y = 0;
            foreach (var tr in _waveParticleTransformsCompact) tr.Y = 0;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWave)));
    }
    /// <summary>停止波纹渲染（摘除合成帧事件 + 停止降帧定时器）。</summary>
    private void StopWaveRender()
    {
        _waveRendering = false;
        if (_waveTimer is not null)
        {
            _waveTimer.Stop();
            _waveTimer = null;
        }
        CompositionTarget.Rendering -= OnWaveFrame;
    }
    /// <summary>合成帧回调：按帧间隔指数平滑，随真实音频电平起伏，动画连贯不卡顿。</summary>
    private void OnWaveFrame(object? sender, EventArgs e)
    {
        try
        {
            // 卡片弹簧与声波纹共用同一合成帧（开销极低）：动画期间波纹继续跟随音频，
            // 避免用户展开时看到波纹“冻住”（iOS 风格：形变与声波并行、互不打断）。
            // 仅旧 Storyboard 动效皮肤路径仍让路（该路径确实是多动画争抢）。
            if (_currentStoryboard is not null) return;
            if (!IsLoaded || !HasWave)
            {
                RefreshWave();
                return;
            }
            var now = _waveClock.Elapsed.TotalSeconds;
            if (!_settings.Current.LowPowerMode)
            {
                if (!AnimationFrameRate.ShouldProcessFrame(now, ref _nextWaveFrameTime, _cachedWaveFps)) return;
            }

            var dt = Math.Min(0.05, Math.Max(0.001, now - _lastWaveTime));
            _lastWaveTime = now;

            var level = Math.Clamp(_vm.WaveLevel, 0, 1);
            var height = _cachedWaveHeight;
            var alpha = 1.0 - Math.Exp(-dt * 22.0); // 帧率无关的指数平滑
            // 1.2.1 性能优化：只更新当前可见的波纹集合（展开=大波纹、紧凑=小波纹），
            // 隐藏面板每帧的 ScaleTransform 更新全部省掉，降低媒体播放时的 CPU 占用
            var expanded = _vm.IsExpanded;
            switch (_cachedWaveStyle)
            {
                case "Spectrum":
                    UpdateWaveSet(expanded ? _waveSpectrumExpanded : _waveSpectrumCompact, level, now, alpha, height, bias: 1);
                    break;
                case "Ring":
                    UpdateRingVisual(expanded ? _waveRingScaleExpanded : _waveRingScaleCompact, level, now, alpha);
                    break;
                case "Particles":
                    UpdateParticlesVisual(expanded ? _waveParticleTransformsExpanded : _waveParticleTransformsCompact, level, now, alpha, expanded ? 8.0 : 5.0);
                    break;
                default:
                    UpdateWaveSet(expanded ? _waveBarsExpanded : _waveBarsCompact, level, now, alpha, height);
                    break;
            }
        }
        catch
        {
            // 渲染异常绝不影响主流程
        }
    }

    private void UpdateWaveSet(IReadOnlyList<ScaleTransform> bars, double level, double t, double alpha, double height, double bias = 0)
    {
        var n = bars.Count;
        var sinBase = Math.Sin(t * 6.0);
        var cosBase = Math.Cos(t * 6.0);
        var isPlaying = _vm.IsPlaying;
        for (var i = 0; i < n; i++)
        {
            var sc = bars[i];
            double target;
            if (isPlaying)
            {
                var wave = WaveValue(sinBase, cosBase, i, WaveSin09, WaveCos09);
                if (bias > 0)
                    target = Math.Clamp((0.05 + (0.14 + 0.66 * level) * wave * (0.55 + 0.45 * (double)i / n)) * height, 0.05, 1.0);
                else
                    target = Math.Clamp((0.10 + (0.12 + 0.72 * level) * wave) * height, 0.08, 1.0);
            }
            else
            {
                target = bias > 0 ? 0.05 : 0.08;
            }
            sc.ScaleY += (target - sc.ScaleY) * alpha;
        }
    }

    private void ApplyWaveStyleVisibility()
    {
        var style = _cachedWaveStyle;
        if (string.Equals(_appliedWaveStyle, style, StringComparison.Ordinal)) return;
        _appliedWaveStyle = style;
        var bars = style == "Bars" ? Visibility.Visible : Visibility.Collapsed;
        var spec = style == "Spectrum" ? Visibility.Visible : Visibility.Collapsed;
        var ring = style == "Ring" ? Visibility.Visible : Visibility.Collapsed;
        var part = style == "Particles" ? Visibility.Visible : Visibility.Collapsed;
        if (WaveBarsPanelCompact is not null) WaveBarsPanelCompact.Visibility = bars;
        if (WaveBarsPanelExpanded is not null) WaveBarsPanelExpanded.Visibility = bars;
        if (WaveSpectrumHostCompact is not null) WaveSpectrumHostCompact.Visibility = spec;
        if (WaveSpectrumHostExpanded is not null) WaveSpectrumHostExpanded.Visibility = spec;
        if (WaveRingHostCompact is not null) WaveRingHostCompact.Visibility = ring;
        if (WaveRingHostExpanded is not null) WaveRingHostExpanded.Visibility = ring;
        if (WaveParticlesHostCompact is not null) WaveParticlesHostCompact.Visibility = part;
        if (WaveParticlesHostExpanded is not null) WaveParticlesHostExpanded.Visibility = part;
    }

    /// <summary>构建频谱/环形/粒子三种备选波纹（启动时一次性创建，颜色随主题绑定）。</summary>
    private void InitWaveVisualStyles()
    {
        try
        {
            BuildSpectrumBars(WaveSpectrumHostCompact, _waveSpectrumCompact, 12, 1.8, 1.2);
            BuildSpectrumBars(WaveSpectrumHostExpanded, _waveSpectrumExpanded, 16, 2.2, 1.2);
            _waveRingScaleCompact = BuildRing(WaveRingHostCompact, 12);
            _waveRingScaleExpanded = BuildRing(WaveRingHostExpanded, 16);
            BuildParticles(WaveParticlesHostCompact, _waveParticleTransformsCompact, 8);
            BuildParticles(WaveParticlesHostExpanded, _waveParticleTransformsExpanded, 10);
            ApplyWaveStyleVisibility();
        }
        catch
        {
            // 备选样式构建失败时仅保留默认柱状，不影响主流程
        }
    }

    /// <summary>频谱条：窄条下对齐，右高左低频段分布，随节奏起伏。</summary>
    private void BuildSpectrumBars(Grid? host, List<ScaleTransform> list, int count, double barW, double gap)
    {
        if (host is null) return;
        var left = (host.Width - (count * barW + (count - 1) * gap)) / 2.0;
        for (var i = 0; i < count; i++)
        {
            var sc = new ScaleTransform(1, 0.05);
            var bar = new Border
            {
                Width = barW,
                Height = host.Height,
                CornerRadius = new CornerRadius(Math.Max(0.3, barW / 2)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(left, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1.0),
                RenderTransform = sc,
            };
            BindingOperations.SetBinding(bar, Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
            host.Children.Add(bar);
            list.Add(sc);
            left += barW + gap;
        }
    }

    /// <summary>环形波纹：圆点中心，随节奏缩放。</summary>
    private ScaleTransform? BuildRing(Grid? host, double diameter)
    {
        if (host is null) return null;
        var sc = new ScaleTransform(1, 1);
        var ring = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = sc,
        };
        BindingOperations.SetBinding(ring, Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
        host.Children.Add(ring);
        return sc;
    }

    /// <summary>粒子波纹：散布小圆点，随节奏上下脉冲。</summary>
    private void BuildParticles(Grid? host, List<TranslateTransform> list, int count)
    {
        if (host is null) return;
        var spacing = host.Width / count;
        for (var i = 0; i < count; i++)
        {
            var tr = new TranslateTransform(0, 0);
            var p = new System.Windows.Shapes.Ellipse
            {
                Width = 2.5,
                Height = 2.5,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(spacing * (i + 0.5) - 1.25, 0, 0, 0),
                RenderTransform = tr,
            };
            BindingOperations.SetBinding(p, System.Windows.Shapes.Ellipse.FillProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
            host.Children.Add(p);
            list.Add(tr);
        }
    }

    private void UpdateRingVisual(ScaleTransform? ring, double level, double t, double alpha)
    {
        if (ring is null) return;
        double target = 1.0;
        if (_vm.IsPlaying)
        {
            var wave = 0.5 + 0.5 * Math.Sin(t * 6.0);
            target = 1.0 + 0.24 * level * wave;
        }
        ring.ScaleX += (target - ring.ScaleX) * alpha;
        ring.ScaleY = ring.ScaleX;
    }

    private void UpdateParticlesVisual(IReadOnlyList<TranslateTransform> parts, double level, double t, double alpha, double maxY)
    {
        var n = parts.Count;
        var sinBase = Math.Sin(t * 6.0);
        var cosBase = Math.Cos(t * 6.0);
        var isPlaying = _vm.IsPlaying;
        for (var i = 0; i < n; i++)
        {
            var tr = parts[i];
            double target = 0;
            if (isPlaying)
            {
                var wave = WaveValue(sinBase, cosBase, i, WaveSin13, WaveCos13);
                target = -wave * level * maxY;
            }
            tr.Y += (target - tr.Y) * alpha;
        }
    }

    private static double WaveValue(double sinBase, double cosBase, int index, double[] sinOffsets, double[] cosOffsets)
        => 0.5 + 0.5 * (sinBase * cosOffsets[index] - cosBase * sinOffsets[index]);

    private void ApplyCoverTint(bool forceRebuild = false)
    {
        try
        {
            var src = _vm.Artwork;
            if (src is null || !_settings.Current.CoverTintBackground || !IsVisible || !_vm.IsExpanded)
            {
                ClearCoverTint();
                SubscribeTintRendering(false);
                return;
            }
            // 缓存封面取色结果：同封面不重复采样（避免展开/收起时重新 RenderTargetBitmap）
            if (!ReferenceEquals(src, _lastSampledArtwork))
            {
                _lastSampledColor = SampleCoverColor(src);
                _lastSampledArtwork = src;
            }
            var color = _lastSampledColor;
            if (color is null)
            {
                ClearCoverTint();
                SubscribeTintRendering(false);
                return;
            }

            // 封面主色变化（换曲）时重建渐变；同曲只复用并更新 Alpha（呼吸）
            if (_tintBrush is null || _tintCoverColor != color || forceRebuild) // 主题切换时强制重建（基色随新主题）
            {
                _tintCoverColor = color;
                var baseColor = (_theme.CardBackground as SolidColorBrush)?.Color
                    ?? System.Windows.Media.Color.FromArgb(0xF0, 0x14, 0x14, 0x1E);
                _tintBrush = new LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(1, 1),
                };
                _tintStop0 = new GradientStop(System.Windows.Media.Color.FromArgb(0xE6, color.Value.R, color.Value.G, color.Value.B), 0);
                _tintStop1 = new GradientStop(baseColor, 1);
                _tintBrush.GradientStops.Add(_tintStop0);
                _tintBrush.GradientStops.Add(_tintStop1);
            }
            Card.Background = _tintBrush;
            // 封面取色生效时玻璃层归零，避免叠加
            _glassAnimSb?.Stop();
            _glassAnimSb = null;
            if (GlassLayer is not null) GlassLayer.Opacity = 0;
            _tintPhaseUtc = DateTime.UtcNow;
            SubscribeTintRendering(true);
        }
        catch
        {
            ClearCoverTint();
            SubscribeTintRendering(false);
        }
    }

    /// <summary>订阅 / 取消合成帧驱动（空闲时不占 CPU）。</summary>
    private void SubscribeTintRendering(bool subscribe)
    {
        if (subscribe == _tintRenderingSubscribed) return;
        _tintRenderingSubscribed = subscribe;
        if (_tintTimer is null)
        {
            _tintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _tintTimer.Tick += OnTintFrame;
        }
        if (subscribe) _tintTimer.Start();
        else _tintTimer.Stop();
    }

    private void OnTintFrame(object? sender, EventArgs e)
    {
        // 展开/收起动画期间暂停封面取色呼吸，减少渲染线程争抢（动画结束即恢复）
        if (_cardAnimating || _currentStoryboard is not null) return;
        if (!_vm.IsExpanded || !_settings.Current.CoverTintBackground || _vm.Artwork is null || _tintStop0 is null)
        {
            SubscribeTintRendering(false);
            return;
        }
        var c = _tintCoverColor;
        if (c is null) { SubscribeTintRendering(false); return; }
        var t = (DateTime.UtcNow - _tintPhaseUtc).TotalSeconds;
        var alpha = 0.85 + 0.06 * (0.5 + 0.5 * Math.Sin(t * 0.35)); // 0.85..0.97 慢周期
        var a = (byte)Math.Round(alpha * 255);
        if (_tintStop0.Color.A != a)
            _tintStop0.Color = System.Windows.Media.Color.FromArgb(a, c.Value.R, c.Value.G, c.Value.B);
    }

    /// <summary>恢复 Card 背景为绑定的主题色（移除封面取色）。</summary>
    private void ClearCoverTint()
    {
        try
        {
            Card.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(CardBackground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
            });
        }
        catch
        {
            Card.Background = _theme.CardBackground;
        }
        // 无封面取色时重新启用玻璃分层（展开态更实、紧凑态通透）
        if (GlassLayer is not null && (_vm.IsExpanded || !_settings.Current.CoverTintBackground))
            AnimateGlass(_vm.IsExpanded);
    }

    /// <summary>把封面渲染到 1x1 位图采样主色（RGBA）。</summary>
    private static System.Windows.Media.Color? SampleCoverColor(ImageSource src)
    {
        try
        {
            var rtb = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
            var img = new System.Windows.Controls.Image { Source = src, Stretch = Stretch.UniformToFill };
            rtb.Render(img);
            var px = new byte[4];
            rtb.CopyPixels(px, 4, 0);
            if (px[3] < 40) return null; // 透明/未加载完成，放弃取色
            return System.Windows.Media.Color.FromArgb(255, px[2], px[1], px[0]);
        }
        catch
        {
            return null;
        }
    }

    // ── 点击穿透 ──────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            var x = (short)(lParam.ToInt64() & 0xFFFF);
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var local = PointFromScreen(new Point(x, y));
            handled = true;
            return IsPointOverCard(local) ? (IntPtr)HTCLIENT : (IntPtr)HTTRANSPARENT;
        }

        return IntPtr.Zero;
    }

    private bool IsPointOverCard(Point local)
    {
        var pos = Card.TransformToAncestor(this).Transform(new Point(0, 0));
        return local.X >= pos.X && local.Y >= pos.Y &&
               local.X <= pos.X + Card.ActualWidth &&
               local.Y <= pos.Y + Card.ActualHeight;
    }

    // ── 显示 / 隐藏 ────────────────────────────────────────────

    private void ShowIsland(bool instant)
    {
        if (!IsLoaded) return;
        if (!IsVisible)
        {
            // 先恢复外观与尺寸，避免隐藏期间被压缩导致重新显示时两侧被裁切
            ApplyAppearance();
            ApplySize();
            Reposition();
            Show();
            // 显示后以 Loaded 优先级再定位一次，确保窗口刚 Show 时尺寸已生效
            Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (instant)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        BeginOpacity(1, 280);
    }

    private void HideIsland()
    {
        if (!IsVisible) return;
        var sb = new Storyboard();
        // 非线性淡出：先快后慢（EaseIn），消失过程不匀速、不生硬
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 },
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            if (!_vm.IsVisible) Hide();
        };
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps（跟随显示器刷新率）
        sb.Begin();
    }

    private void BeginOpacity(double to, int ms)
    {
        var sb = new Storyboard();
        var fade = new DoubleAnimation(Opacity, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 },
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
        };
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps（跟随显示器刷新率）
        sb.Begin();
    }

    // ── iOS 风格形变动画 ──────────────────────────────────────

    private void AnimateSize()
    {
        if (!IsLoaded) return;
        if (_vm.IsExpanded) Expand();
        else Collapse();
    }

    private void Expand()
    {
        // 胶囊行保持可见（动画淡出），展开内容覆盖全卡片（动画淡入），两者重叠交叉过渡，
        // 避免 Card 深色背景透过内容间隙产生"黑掉"现象
        AnimateGlass(true); // 智能透明度：展开态更实
        ContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ContentGrid.RowDefinitions[1].Height = GridLength.Auto;
        ContentGrid.VerticalAlignment = VerticalAlignment.Center;
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);

        // 先测量展开内容自然高度（ScrollViewer 内容总高），得到卡片目标高度（2.1.6 结果缓存）。
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;
        // 60fps 优化：展开内容固定目标宽度，展开动画期间不随卡片宽度逐帧重排（内容只布局一次）
        ExpandedContent.Width = Math.Max(120, ExpandedWidth - 20);
        if (!_expandedHeightValid || _expandedHeightDirty)
        {
            ExpandedContent.Measure(new System.Windows.Size(ExpandedContent.Width, double.PositiveInfinity));
            var ch = ExpandedContent.DesiredSize.Height;
            _expandedTargetHeight = Math.Clamp(ch + 24, 200, MaxExpandedHeight);
            _expandedHeightValid = true;
            _expandedHeightDirty = false;
        }
        var targetHeight = _expandedTargetHeight;

        // 重新显示胶囊行：动画期间淡出，与展开内容交叉过渡
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        PillRow.Visibility = Visibility.Visible;
        PillRow.Opacity = 1;

        AnimateCard(ExpandedWidth, targetHeight, expand: true);
    }

    private void Collapse()
    {
        // 先恢复胶囊行（紧凑行占满并垂直居中），再缩回紧凑尺寸
        AnimateGlass(false); // 智能透明度：紧凑态更通透
        ContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ContentGrid.RowDefinitions[1].Height = GridLength.Auto; // 展开行恢复自适应
        // 保持垂直居中：收回后组件上下对称（此前设为 Top 会导致贴顶、下方留白，展开收回后距离不同）
        ContentGrid.VerticalAlignment = VerticalAlignment.Center;

        // 清除展开动画的残留（HoldEnd 会把 PillRow.Opacity 锁在 0，直接设本地值无效）
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);
        PillRow.Visibility = Visibility.Visible;
        PillRow.Opacity = 0; // 与展开内容交叉淡入（AnimateCard 中由 0→1 平滑过渡），避免瞬现"文字重新弹出"
        // 展开内容保持可见以播放「自下而上」的交错淡出动画，动画结束后由 AnimateCard 回调隐藏
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.Opacity = 1;
        // 60fps 优化：收起动画期间展开内容固定宽度，不随卡片宽度逐帧重排
        // 保持收起动画启动时的展开宽度：文字不瞬间重排（溢出部分由圆角裁剪收住，且随淡化隐藏）
        ExpandedContent.Width = Math.Max(120, Math.Max(Card.ActualWidth, CompactWidth) - 20);

        // 尺寸收尾由 OnCardSpringSettled 统一写回 _cardTargetW/H（始终是最新目标，兼容收起途中
        // 被紧凑尺寸重定向连续接管），不再用固定 CompactWidth 回调覆盖，避免收回后宽度/高度错位。
        AnimateCard(CompactWidth, CompactHeight, expand: false);
    }

    /// <summary>
    /// 动画：卡片尺寸用 iOS 阻尼弹簧（先快后慢、轻微过冲回弹）；
    /// 展开内容按区块自上而下交错淡入上移、收起时反向交错淡出下移（1.2.1），整体节奏非线性、不生硬。
    /// </summary>
    private void BeginAnimationSurface()
    {
        if (_animationSurfaceActive) return;
        _animationSurfaceActive = true;
        _cardLayoutRoundingBeforeAnimation = Card.UseLayoutRounding;
        _cardSnapsBeforeAnimation = Card.SnapsToDevicePixels;
        // Width/Height are fractional during a spring transition. Disable pixel snapping
        // only for the duration of the motion so the card advances by subpixels instead of
        // rounding every frame, then restore crisp text/edge alignment on completion.
        Card.UseLayoutRounding = false;
        Card.SnapsToDevicePixels = false;
    }

    private void EndAnimationSurface()
    {
        if (!_animationSurfaceActive) return;
        _animationSurfaceActive = false;
        Card.UseLayoutRounding = _cardLayoutRoundingBeforeAnimation;
        Card.SnapsToDevicePixels = _cardSnapsBeforeAnimation;
        FinalizeCardShape(); // 恢复像素对齐后重建圆角裁剪，消除亚像素圆角走样
    }
    private void AnimateCard(double width, double height, bool expand, Action? onCompleted = null)
    {
        var fromWidth = ResolveAnimationFrom(Card.ActualWidth, Card.Width, width);
        var fromHeight = ResolveAnimationFrom(Card.ActualHeight, Card.Height, height);
        _currentStoryboard?.Stop();
        _currentStoryboard = null;
        _compactAnimationValid = false;

        // 减少动态效果：关闭弹簧/交错动画，直接瞬时切换（无障碍 / 省电）
        if (_settings.Current.ReduceMotion)
        {
            StopCardSprings();
            EndAnimationSurface();
            Card.Width = width;
            Card.Height = height;
            PillRow.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
            PillRow.Opacity = expand ? 0 : 1;
            ExpandedContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            ExpandedContent.Opacity = expand ? 1 : 0;
            ExpandedScale.ScaleX = ExpandedScale.ScaleY = 1;
            ExpandedTranslate.Y = 0;
            ApplyCascadeState(expand ? 1 : 0, expand ? 0 : 10);
            FinalizeCardShape();
            onCompleted?.Invoke();
            return;
        }

        // 默认 Spring 动效 → 真实 iOS 弹簧物理（可打断、速度连续、自然收敛），
        // 内容交叉淡入由同一弹簧驱动，文字随卡片一起动，不再"额外弹出"。
        if (_settings.Current.AnimationStyle == "Spring")
        {
            AnimateCardSpring(width, height, expand, fromWidth, fromHeight, onCompleted);
            return;
        }

        BeginAnimationSurface();

        var sb = new Storyboard();
        // 动效皮肤（非 Spring）：Soft=柔和 / Elastic=弹性 / Fade=简洁渐隐
        var (styleEase, styleSizeMs) = GetSizeAnimationStyle(expand);
        var smooth = CachedCubicEaseOut;
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0; // 低功耗模式：动画时间缩短，更快进入空闲

        // 卡片尺寸：动效皮肤曲线（展开/收起时长由皮肤决定）
        AddAnim(sb, Card, FrameworkElement.WidthProperty, width, (int)(styleSizeMs * lm), styleEase, from: fromWidth);
        AddAnim(sb, Card, FrameworkElement.HeightProperty, height, (int)(styleSizeMs * lm), styleEase, from: fromHeight);

        // 内容跟随卡片一起动：清除可能残留的独立缩放/位移动画，只做整体透明度过渡
        ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ExpandedScale.ScaleX = ExpandedScale.ScaleY = 1;
        ExpandedTranslate.Y = 0;
        ApplyCascadeState(1, 0); // 同时清除块元素可能残留的交错状态
        if (expand)
        {
            ExpandedContent.Opacity = 0;
            AddAnim(sb, ExpandedContent, UIElement.OpacityProperty, 1, (int)(360 * lm), smooth);
        }
        else
        {
            ExpandedContent.Opacity = 1;
            AddAnim(sb, ExpandedContent, UIElement.OpacityProperty, 0, (int)(300 * lm), smooth);
        }
        // Content parallax (2.1.0): non-Spring styles get the same grow/retreat
        // feel through storyboard animations on the shared scale/translate transforms.
        if (expand)
        {
            AddAnim(sb, ExpandedScale, ScaleTransform.ScaleXProperty, 1, (int)(320 * lm), smooth);
            AddAnim(sb, ExpandedScale, ScaleTransform.ScaleYProperty, 1, (int)(320 * lm), smooth);
            AddAnim(sb, ExpandedTranslate, TranslateTransform.YProperty, 0, (int)(320 * lm), smooth);
        }
        else
        {
            AddAnim(sb, ExpandedScale, ScaleTransform.ScaleXProperty, CrossFadeCurves.CollapseParallaxScaleTo, (int)(260 * lm), smooth);
            AddAnim(sb, ExpandedScale, ScaleTransform.ScaleYProperty, CrossFadeCurves.CollapseParallaxScaleTo, (int)(260 * lm), smooth);
            AddAnim(sb, ExpandedTranslate, TranslateTransform.YProperty, CrossFadeCurves.CollapseParallaxYTo, (int)(260 * lm), smooth);
        }
        // 胶囊行：展开后快速淡出；收起时与展开内容交叉淡入（0→1 平滑过渡）
        if (expand)
            AddAnim(sb, PillRow, UIElement.OpacityProperty, 0, (int)(220 * lm), smooth);
        else
        {
            PillRow.BeginAnimation(UIElement.OpacityProperty, null);
            PillRow.Visibility = Visibility.Visible;
            PillRow.Opacity = 0;
            AddAnim(sb, PillRow, UIElement.OpacityProperty, 1, (int)(280 * lm), smooth, TimeSpan.FromMilliseconds(40 * lm));
        }

        sb.Completed += (_, _) =>
        {
            // 防旧动画完成回调覆盖新动画状态（快速连续展开/收起时尺寸错乱）
            if (!ReferenceEquals(_currentStoryboard, sb)) return;
            try
            {
                _currentStoryboard = null;
                // 关键：清除动画对 Card 尺寸的 HoldEnd 锁定，否则之后设置本地尺寸（含自动重算）不生效
                Card.BeginAnimation(FrameworkElement.WidthProperty, null);
                Card.BeginAnimation(FrameworkElement.HeightProperty, null);
                // 必须写回最终尺寸：清除动画后若只依赖本地值，Card 会回退到紧凑时设置的本地尺寸
                Card.Width = width;
                Card.Height = height;
                FinalizeCardShape();
                ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                ResetContentParallax(); // 收尾：圆角裁剪与最终尺寸精确对齐
                if (_vm.IsExpanded)
                {
                    PillRow.Visibility = Visibility.Collapsed;
                    PillRow.Opacity = 0;
                    ExpandedContent.Opacity = 1;
                    ExpandedContent.Width = double.NaN; // 恢复自适应布局
                }
                else
                {
                    PillRow.Visibility = Visibility.Visible;
                    PillRow.Opacity = 1;
                    ExpandedContent.Visibility = Visibility.Collapsed;
                    ExpandedContent.Opacity = 0;
                    ExpandedContent.Width = double.NaN; // 恢复自适应布局
                }
                onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Card animation completed failed", ex);
            }
            finally
            {
                EndAnimationSurface();
            }
        };
        _currentStoryboard = sb;
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps（跟随显示器刷新率）
        sb.Begin();
    }

    // ── iOS 真弹簧物理：卡片展开/收起 ──────────────────────────────

    /// <summary>确保卡片三个弹簧（宽/高/淡入）已创建并绑定回调。回调只绑定一次，弹簧可反复 Start/Retarget。</summary>
    private void EnsureCardSprings()
    {
        if (_cardWSpring is not null) return;
        _cardWSpring = new IOSSpring();
        _cardHSpring = new IOSSpring();
        _cardFadeSpring = new IOSSpring();
        _cardRSpring = new IOSSpring();
        _cardWSpring.SetCallbacks(v => Card.Width = v, OnCardSpringSettled);
        _cardHSpring.SetCallbacks(v => Card.Height = v, OnCardSpringSettled);
        _cardFadeSpring.SetCallbacks(v => { ExpandedContent.Opacity = v; PillRow.Opacity = CrossFadeCurves.PillOpacity(v, _expandedFadeIn); ApplyContentParallax(v, _expandedFadeIn); }, OnCardSpringSettled);
        _cardRSpring.SetCallbacks(ApplyCardRadius, OnCardSpringSettled);
    }

    private void StopCardSprings()
    {
        _cardWSpring?.Stop();
        _cardHSpring?.Stop();
        _cardFadeSpring?.Stop();
        _cardRSpring?.Stop();
        _cardAnimating = false;
        _cardSpringCompleted = null; // 动画被强制终止，丢弃未完成的回调
    }

    /// <summary>
    /// iOS 液态形变圆角（2.0.7）：紧凑胶囊 = 高度/2（完整药丸），展开卡片 = 用户设置半径（封顶）。
    /// 纯函数，便于单元测试。
    /// </summary>
    internal static double ComputeCardRuntimeRadius(double height, double settingRadius)
    {
        var s = Math.Clamp(settingRadius, 16.0, 40.0);           // 与设置面板一致：16~40
        var h = double.IsFinite(height) ? Math.Max(4.0, height) : 4.0; // 非法高度按最小高度兜底，绝不产生 NaN
        return Math.Clamp(Math.Min(h / 2.0, s), 4.0, s);
    }

    /// <summary>逐帧统一应用圆角：裁剪几何 + 卡片边框 + 玻璃层三者同步，形变全程圆角连续（消除"圆角变方框"）。</summary>
    private double _lastAppliedCardRadius = double.NaN;   // 2.1.2：圆角写入去抖，收敛/静止帧不再刷布局

    private void ApplyCardRadius(double r)
    {
        // 亚像素内的重复写入跳过：每帧写 CornerRadius 会触发边框/玻璃重绘，
        // 已收敛到目标值或差值 <0.05px 时直接忽略，减少动画尾部与静止时的布局开销。
        if (!double.IsNaN(_lastAppliedCardRadius) && Math.Abs(r - _lastAppliedCardRadius) < 0.05)
            return;
        _lastAppliedCardRadius = r;
        if (_cardClip is not null)
        {
            _cardClip.RadiusX = r;
            _cardClip.RadiusY = r;
        }
        var rounded = new CornerRadius(r);
        Card.CornerRadius = rounded;
        if (GlassLayer is not null) GlassLayer.CornerRadius = rounded;
    }

    /// <summary>
    /// iOS 真弹簧展开/收起（默认 Spring 动效）：
    /// 尺寸用欠阻尼/近临界弹簧（展开 ζ=0.86 轻微 Q 弹、收起 ζ=0.97 无回弹，响应 0.62s/0.52s 慢而自然），
    /// 透明度用临界阻尼弹簧，且与尺寸共用同一帧循环——内容随卡片生长/收拢自然浮现/隐去，
    /// 文字不再"额外弹出"。动画可中途打断：以当前值+速度连续改目标（iOS 打断语义）。
    /// </summary>
    private void AnimateCardSpring(double width, double height, bool expand,
        double fromWidth, double fromHeight, Action? onCompleted)
    {
        BeginAnimationSurface();
        EnsureCardSprings();

        // 用户时长调节：默认 700ms ↔ response≈0.5s；低功耗模式整体加快
        var durScale = Math.Clamp(_settings.Current.IslandAnimationDuration, 300, 1400) / 700.0;
        var lm = _settings.Current.LowPowerMode ? 0.65 : 1.0;
        // 展开：轻微 Q 弹（ζ=0.86，过冲约 0.5%，只有 1-2px 的轻回弹），响应 0.62s 更慢更自然；
        // 收起：近临界（ζ=0.97 几乎无回弹），响应 0.52s 收尾柔和。
        var zeta = expand ? 0.86 : 0.97;
        var response = (expand ? 0.62 : 0.52) * durScale * lm;

        // 清除可能残留的独立缩放/位移动画与交错状态，让内容只随卡片动
        ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ExpandedScale.ScaleX = ExpandedScale.ScaleY = 1;
        ExpandedTranslate.Y = 0;
        ApplyCascadeState(1, 0);
        ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        ExpandedContent.Visibility = Visibility.Visible;
        PillRow.Visibility = Visibility.Visible;
        // 动画期间固定展开内容宽度，避免逐帧重排（60fps 保持布局稳定）
        ExpandedContent.Width = Math.Max(120, (expand ? ExpandedWidth : Math.Max(Card.ActualWidth, CompactWidth)) - 20);

        _expandedFadeIn = expand; // 2.0.9：记录淡入方向，交叉淡入曲线据此解耦胶囊行与内容
        var fadeTo = expand ? 1.0 : 0.0;
        var wasAnimating = _cardAnimating; // 记录进入前的动画状态，用于打断判断
        _cardAnimating = true;
        _cardTargetW = width;
        _cardTargetH = height;
        _cardSpringCompleted = onCompleted; // 收尾时统一回调（支持打断重定向后仍生效）

        // iOS 分层弹簧（2.0.7）：圆角弹簧比尺寸快约 18%——"形状先导、尺寸跟随"，形变观感更接近 iOS 灵动岛液态形变；
        // 透明度：展开内容稍快浮现、收起内容先淡出再收拢（与尺寸共用帧循环，文字不额外弹出）。
        var radiusTo = ComputeCardRuntimeRadius(height, _settings.Current.CornerRadius);
        var radiusFrom = _cardClip?.RadiusX ?? radiusTo;

        if (wasAnimating && _cardWSpring!.IsActive && _cardHSpring!.IsActive)
        {
            // 打断重定向（如展开一半点收起）：先重设弹簧参数再改目标——
            // 展开（欠阻尼、Q弹）与收起（近临界、收尾柔和）的手感参数不同，
            // 只 Retarget 会沿用旧方向参数导致方向切换后过冲/发僵（2.1.5 修复）。
            // Retarget 本身保留当前值+速度，改参数后速度依然连续，完整 iOS 打断语义。
            _cardWSpring.Configure(zeta, response);
            _cardHSpring.Configure(zeta, response);
            _cardFadeSpring!.Configure(1.0, CrossFadeCurves.FadeResponse(response, expand));
            _cardRSpring!.Configure(expand ? 0.90 : 0.96, response * 0.82);
            _cardWSpring.Retarget(width);
            _cardHSpring.Retarget(height);
            _cardFadeSpring!.Retarget(fadeTo);
            _cardRSpring!.Retarget(radiusTo); // 圆角同步连续改道（iOS 打断语义）
            return;
        }

        // 全新开始：一次写到位，避免首帧闪烁
        ExpandedContent.Opacity = expand ? 0 : 1;
        PillRow.Opacity = expand ? 1 : 0;
        var fadeFrom = ExpandedContent.Opacity;
        _cardWSpring!.Configure(zeta, response);
        _cardWSpring.Start(fromWidth, width);
        _cardHSpring!.Configure(zeta, response);
        _cardHSpring.Start(fromHeight, height);
        _cardFadeSpring!.Configure(1.0, CrossFadeCurves.FadeResponse(response, expand)); // 临界阻尼：展开内容略滞后形变浮现（形状先导、内容跟随），收起先淡出再收拢；无回弹
        _cardFadeSpring.Start(fadeFrom, fadeTo);
        _cardRSpring!.Configure(expand ? 0.90 : 0.96, response * 0.82); // 圆角先导：比尺寸快，形变全程连续
        _cardRSpring.Start(radiusFrom, radiusTo);
    }

    /// <summary>卡片三个弹簧全部收敛后的统一收尾（与旧 Storyboard 路径一致的最终状态）。</summary>
    private void OnCardSpringSettled()
    {
        if (!_cardAnimating) return;
        if (_cardWSpring?.IsActive == true || _cardHSpring?.IsActive == true || _cardFadeSpring?.IsActive == true || _cardRSpring?.IsActive == true)
            return; // 还有弹簧在收敛，等最后一个结束再收尾
        try
        {
            _cardAnimating = false;
            Card.BeginAnimation(FrameworkElement.WidthProperty, null);
            Card.BeginAnimation(FrameworkElement.HeightProperty, null);
            Card.Width = _cardTargetW;
            Card.Height = _cardTargetH;
            FinalizeCardShape();
            ResetContentParallax(); // 收尾：圆角裁剪与最终尺寸精确对齐
            if (_vm.IsExpanded)
            {
                PillRow.Visibility = Visibility.Collapsed;
                PillRow.Opacity = 0;
                ExpandedContent.Opacity = 1;
                RestoreExpandedContentAutoWidth(); // 下一帧再恢复自适应宽度，避免同帧重排闪动（2.1.5）
            }
            else
            {
                PillRow.Visibility = Visibility.Visible;
                PillRow.Opacity = 1;
                ExpandedContent.Visibility = Visibility.Collapsed;
                ExpandedContent.Opacity = 0;
                RestoreExpandedContentAutoWidth(); // 下一帧再恢复自适应宽度，避免同帧重排闪动（2.1.5）
            }
            var cb = _cardSpringCompleted; // 先取再清，避免回调里再次触发动画导致重复收尾
            _cardSpringCompleted = null;
            cb?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Card spring settled failed", ex);
        }
        finally
        {
            EndAnimationSurface();
        }
    }

    /// <summary>
    /// 动画收尾后延迟一帧再恢复展开内容的自适应宽度：
    /// 先让动画结束帧以固定宽度稳定渲染，再释放宽度约束交给布局自动伸展，
    /// 避免同一帧内宽度突变触发一次性重排（黑/白方框、文字闪动的根因之一）。
    /// 若下一帧前新一轮卡片动画已开始，则跳过（宽度由动画路径接管）。
    /// </summary>
    private void RestoreExpandedContentAutoWidth()
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (ExpandedContent is null) return;
                if (_cardAnimating) return; // 新一轮动画已开始：宽度由动画路径管理
                ExpandedContent.Width = double.NaN; // 恢复自适应布局
            }, DispatcherPriority.Loaded);
        }
        catch { /* 窗口已关闭时忽略 */ }
    }

    /// <summary>紧凑尺寸变化（无展开时）：真实弹簧平滑调整卡片宽高，避免内容变化时"跳变"。</summary>
    private void AnimateCompactSizeSpring(double targetWidth, double targetHeight)
    {
        if (_cardAnimating)
        {
            // 已有卡片动画（展开/收起/尺寸调整）进行中：先按紧凑方向重设弹簧参数
            // （近临界、更快的收尾手感），再连续改目标——避免打断后沿用展开的
            // 欠阻尼参数产生不必要的过冲（2.1.5）。
            var durScale2 = Math.Clamp(_settings.Current.IslandAnimationDuration, 300, 1400) / 700.0;
            var lm2 = _settings.Current.LowPowerMode ? 0.65 : 1.0;
            var resp2 = 0.46 * durScale2 * lm2;
            if (_cardWSpring?.IsActive == true) { _cardWSpring.Configure(0.96, resp2); _cardWSpring.Retarget(targetWidth); }
            if (_cardHSpring?.IsActive == true) { _cardHSpring.Configure(0.96, resp2); _cardHSpring.Retarget(targetHeight); }
            if (_cardRSpring?.IsActive == true) { _cardRSpring.Configure(0.97, resp2 * 0.85); _cardRSpring.Retarget(ComputeCardRuntimeRadius(targetHeight, _settings.Current.CornerRadius)); }
            _cardTargetW = targetWidth;
            _cardTargetH = targetHeight;
            return;
        }
        if (Math.Abs(Card.ActualWidth - targetWidth) < 0.5 && Math.Abs(Card.ActualHeight - targetHeight) < 0.5)
        {
            Card.Width = targetWidth;
            Card.Height = targetHeight;
            return;
        }

        BeginAnimationSurface();
        EnsureCardSprings();
        _cardAnimating = true;
        _cardTargetW = targetWidth;
        _cardTargetH = targetHeight;
        var durScale = Math.Clamp(_settings.Current.IslandAnimationDuration, 300, 1400) / 700.0;
        var lm = _settings.Current.LowPowerMode ? 0.65 : 1.0;
        var response = 0.46 * durScale * lm; // 紧凑尺寸调整略放慢，贴合自然手感
        _cardWSpring!.Configure(0.96, response);
        _cardWSpring.Start(ResolveAnimationFrom(Card.ActualWidth, Card.Width, targetWidth), targetWidth);
        _cardHSpring!.Configure(0.96, response);
        _cardHSpring.Start(ResolveAnimationFrom(Card.ActualHeight, Card.Height, targetHeight), targetHeight);
        // 圆角随紧凑尺寸同步形变：紧凑态保持药丸形（高/2），收尾圆润不生硬
        _cardRSpring!.Configure(0.97, response * 0.85);
        _cardRSpring.Start(ResolveAnimationFrom(_cardClip?.RadiusX ?? 0, 0, ComputeCardRuntimeRadius(targetHeight, _settings.Current.CornerRadius)), ComputeCardRuntimeRadius(targetHeight, _settings.Current.CornerRadius));
    }

    // ── iOS 真弹簧物理：窗口位置 / 推送卡片 ─────────────────────────

    private void EnsurePositionSprings()
    {
        if (_posLSpring is not null) return;
        _posLSpring = new IOSSpring();
        _posTSpring = new IOSSpring();
        _posLSpring.SetCallbacks(v => Left = v, OnPositionSpringSettled);
        _posTSpring.SetCallbacks(v => Top = v, OnPositionSpringSettled);
    }

    /// <summary>位置弹簧全部收敛后：物理像素取整，避免亚像素位置让窗口边缘发虚、文字发糊。</summary>
    private void OnPositionSpringSettled()
    {
        if (_posLSpring?.IsActive == true || _posTSpring?.IsActive == true) return; // 还有弹簧在收敛，最后一个结束再收尾
        try
        {
            var scale = ScreenHelper.GetDpiScale(_screen);
            Left = Math.Round(Left * scale) / scale;
            Top = Math.Round(Top * scale) / scale;
        }
        catch { /* 取整失败保留当前值 */ }
    }

    private void EnsurePushSprings()
    {
        if (_pushOpacitySpring is not null) return;
        _pushOpacitySpring = new IOSSpring();
        _pushScaleSpring = new IOSSpring();
        _pushOpacitySpring.SetCallbacks(v => { if (CompactPushCard is not null) CompactPushCard.Opacity = v; }, OnPushSpringSettled);
        _pushScaleSpring.SetCallbacks(v => { if (CompactPushScale is not null) { CompactPushScale.ScaleX = v; CompactPushScale.ScaleY = v; } }, OnPushSpringSettled);
    }

    private void OnPushSpringSettled()
    {
        if (_pushOpacitySpring?.IsActive == true || _pushScaleSpring?.IsActive == true) return;
        if (CompactPushCard is null) return;
        CompactPushCard.Opacity = 1;
        if (CompactPushScale is not null) CompactPushScale.ScaleX = CompactPushScale.ScaleY = 1;
    }

    /// <summary>
    /// 动效皮肤（33）：返回 (尺寸缓动, 基准时长毫秒) 元组。
    /// Spring = iOS 阻尼弹簧（默认，轻微过冲回弹）；Soft = 柔和弹簧（回弹更少更软）；
    /// Elastic = 弹性回弹（明显弹跳）；Fade = 简洁渐隐（无回弹，最克制）。
    /// </summary>
    private (IEasingFunction Easing, int SizeMs) GetSizeAnimationStyle(bool expand)
    {
        // 1.2.0：动画时长可由用户微调（300~1400ms，默认 700ms）。
        // 各风格保留相对差异：Spring 全时长 / Soft 略慢 / Elastic 略快 / Fade 最短；
        // 收起时长约为展开的 0.86 倍，让回收更快一点更利落。
        var baseMs = Math.Clamp(_settings.Current.IslandAnimationDuration, 300, 1400);
        static int Ms(double v) => (int)Math.Round(v);
        switch (_settings.Current.AnimationStyle)
        {
            case "Soft":
                return (expand ? CachedSoftEase : CachedCollapseEase, expand ? Ms(baseMs * 1.08) : Ms(baseMs * 0.94));
            case "Elastic":
                return (CachedElasticEase, expand ? Ms(baseMs * 0.97) : Ms(baseMs * 0.90));
            case "Smooth":
                return (expand ? CachedSoftEaseSmooth : CachedCollapseEase, expand ? Ms(baseMs * 1.02) : Ms(baseMs * 0.94));
            case "Fade":
                return (CachedCubicEaseOut, expand ? Ms(baseMs * 0.74) : Ms(baseMs * 0.72));
            default: // Spring
                // 收起改用高阻尼弹簧（无 Q 弹）且时长接近展开（×0.94），更慢更丝滑
                return (expand ? CachedSpringEase : CachedCollapseEase, expand ? baseMs : Ms(baseMs * 0.94));
        }
    }
    internal static double ResolveAnimationFrom(double actual, double current, double fallback)
        => double.IsFinite(actual) && actual > 0.5 ? actual : double.IsFinite(current) && current > 0.5 ? current : fallback;

    private static PropertyPath GetAnimationPath(DependencyProperty property)
        => AnimationPathCache.GetOrAdd(property, static p => new PropertyPath(p));

    private void AddAnim(Storyboard sb, DependencyObject target, DependencyProperty prop, double to, int ms, IEasingFunction easing, TimeSpan? beginTime = null, double? from = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = easing,
            BeginTime = beginTime ?? TimeSpan.Zero,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, GetAnimationPath(prop));
        if (from is double value && double.IsFinite(value)) anim.From = value;
        sb.Children.Add(anim);
    }

    // ── 定位 ──────────────────────────────────────────────────

    public void Reposition()
    {
        if (!IsLoaded) return;
        var s = _settings.Current;
        if (s.IslandManualLeft is double ml && s.IslandManualTop is double mt)
        {
            // 手动定位：只在窗口比工作区小时做越界保护，避免贴边/居中后自动弹回
            var work = ScreenHelper.DpiWorkArea(_screen);
            var w = Math.Max(1.0, ActualWidth);
            var h = Math.Max(1.0, ActualHeight);
            if (w < work.Width) ml = Math.Clamp(ml, work.Left, work.Right - w);
            if (h < work.Height) mt = Math.Clamp(mt, work.Top, work.Bottom - h);
            Left = ml;
            Top = mt;
            ApplyCardAlignment();
            return;
        }
        var pos = ScreenHelper.ComputePosition(_screen, s.Position,
            ActualWidth, ActualHeight, s.OffsetX, s.OffsetY);
        Left = pos.X;
        Top = pos.Y;
        ApplyCardAlignment();
    }

    // ── 拖文件上岛 ──────────────────────────────────────────
    private bool _dragHintOn;

    private void Card_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            ShowDragHint(true);
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Card_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ShowDragHint(false);
    }

    private void Card_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ShowDragHint(false);
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        // 文件中转站：拖入的文件进入组件，可再拖出到其他应用 / 资源管理器（仅存路径引用）
        _vm.AddFilesToTransfer(files);
        e.Handled = true;
    }

    /// <summary>拖入文件时用强调色高亮卡片边框（轻微淡入淡出）。</summary>
    private void ShowDragHint(bool on)
    {
        if (_dragHintOn == on) return;
        _dragHintOn = on;
        try
        {
            // 主题画笔已 Freeze，无法直接 BeginAnimation；这里新建未冻结画笔做颜色渐变
            var accent = (_theme.AccentBorderBrush as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromArgb(160, 108, 92, 231);
            var card = (_theme.CardBorder as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromArgb(60, 255, 255, 255);
            var brush = new SolidColorBrush(on ? card : accent);
            var anim = new ColorAnimation(on ? accent : card, TimeSpan.FromMilliseconds(200));
            if (!on)
            {
                // 还原主题绑定必须等颜色动画播完再执行：SetBinding 会立刻替换 BorderBrush 的本地值，
                // 若在此处直接调用会把刚起步的 200ms 过渡动画截断，边框颜色会瞬间跳变。
                anim.Completed += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_dragHintOn) // 期间又被拖入（on=true）则不恢复，交由下一次动画处理
                        Card.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(CardBorder))
                        {
                            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
                        });
                }));
            }
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            Card.BorderBrush = brush;
        }
        catch { /* 动画失败忽略 */ }
    }

    // ── 文件中转站：把中转文件拖出到其他应用 / 资源管理器 ──
    private bool _fileDragArmed;
    private Point _fileDownPoint;

    private void FileTransferItem_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _fileDragArmed = true;
        _fileDownPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private void FileTransferItem_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_fileDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _fileDownPoint.X) <= 4 && Math.Abs(pos.Y - _fileDownPoint.Y) <= 4) return;
        _fileDragArmed = false;
        var paths = _vm.FileTransferItems.Select(f => f.Path).ToArray();
        if (paths.Length == 0) return;
        try
        {
            // 用真实路径发起系统拖放（复制语义），源文件不会被移动或删除
            var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, paths);
            System.Windows.DragDrop.DoDragDrop(sender is DependencyObject d ? d : Card, data,
                System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
        }
        catch { /* 用户取消拖放等 */ }
        e.Handled = true;
    }

    private void FileTransferItem_Up(object sender, MouseButtonEventArgs e)
    {
        _fileDragArmed = false;
        e.Handled = true;
    }

    /// <summary>点击文件中转组件上的「×」：清空中转站。</summary>
    private void FileTransferClear_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearFileTransfer();
        e.Handled = true;
    }

    // ── 拖动定位：吸附 + 持久化 ─────────────────────────────

    /// <summary>拖动松手后：自动吸附屏幕边缘/居中，并把位置写入设置（上锁/重启后保持）。</summary>
    private void SnapAndPersistPosition()
    {
        if (!IsLoaded) return;
        var s = _settings.Current;
        var work = ScreenHelper.DpiWorkArea(_screen);
        var w = Math.Max(1.0, ActualWidth);
        var h = Math.Max(1.0, ActualHeight);

        var left = Left;
        var top = Top;

        if (s.EdgeSnapEnabled)
        {
            const double snap = 56; // 吸附阈值（DIP）
            left = SnapTo(left, new[] { work.Left, work.Left + (work.Width - w) / 2, work.Right - w }, snap);
            top = SnapTo(top, new[] { work.Top, work.Bottom - h }, snap);
        }

        // 越界保护：窗口比工作区小时才夹紧，避免多显示器负坐标失效
        if (w < work.Width) left = Math.Clamp(left, work.Left, work.Right - w);
        if (h < work.Height) top = Math.Clamp(top, work.Top, work.Bottom - h);

        _settings.Update(s2 =>
        {
            s2.IslandManualLeft = left;
            s2.IslandManualTop = top;
        });
        AnimatePosition(left, top);
    }

    private static double SnapTo(double value, double[] targets, double threshold)
    {
        foreach (var t in targets)
        {
            if (Math.Abs(value - t) <= threshold) return t;
        }
        return value;
    }

    /// <summary>窗口移动用非线性缓动动画（不瞬移，丝滑过渡）。</summary>
    private void AnimatePosition(double left, double top)
    {
        if (!IsLoaded) return;
        if (Math.Abs(Left - left) < 0.5 && Math.Abs(Top - top) < 0.5) return;
        if (_settings.Current.ReduceMotion)
        {
            Left = left;
            Top = top;
            return;
        }
        _positionStoryboard?.Stop(); // 连续重定位先停旧动画，避免并发抖动
        // 默认 Spring 动效：真实弹簧，窗口位置丝滑到位
        if (_settings.Current.AnimationStyle == "Spring")
        {
            EnsurePositionSprings();
            var durScale = Math.Clamp(_settings.Current.IslandAnimationDuration, 300, 1400) / 700.0;
            var lm = _settings.Current.LowPowerMode ? 0.65 : 1.0;
            var response = 0.4 * durScale * lm;
            if (_posLSpring!.IsActive) _posLSpring.Retarget(left);
            else { _posLSpring.Configure(0.9, response); _posLSpring.Start(Left, left); }
            if (_posTSpring!.IsActive) _posTSpring.Retarget(top);
            else { _posTSpring.Configure(0.9, response); _posTSpring.Start(Top, top); }
            return;
        }
        var easing = CachedCubicEaseOut;
        var sb = new Storyboard();
        AddAnim(sb, this, Window.LeftProperty, left, 320, easing);
        AddAnim(sb, this, Window.TopProperty, top, 320, easing);
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_positionStoryboard, sb)) return;
            _positionStoryboard = null;
            BeginAnimation(Window.LeftProperty, null);
            BeginAnimation(Window.TopProperty, null);
            Left = left;
            Top = top;
        };
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode);
        _positionStoryboard = sb;
        sb.Begin();
    }

    // ── 歌词自动滚动 ──────────────────────────────────────────

    private bool _lyricsScrollQueued;
    private IOSSpring? _lyricsScrollSpring;   // 歌词滚动弹簧：临界阻尼，重定向保持速度连续（2.0.6）
    private double _lyricsScrollTarget;
    private void QueueLyricsScroll(int index)
    {
        if (!IsLoaded || !IsVisible || !_vm.IsExpanded) return;
        if (_lyricsScrollQueued) return;
        _lyricsScrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _lyricsScrollQueued = false;
            // 执行时取最新索引：快速切句时排队中的旧索引会被最新句覆盖，滚动始终跟随当前句
            var current = _vm.LyricIndex >= 0 ? _vm.LyricIndex : index;
            ScrollLyricsTo(current);
        }, DispatcherPriority.Loaded);
    }

    private void ScrollLyricsTo(int index)
    {
        if (LyricsList.Items.Count == 0) return;
        if (!_vm.IsExpanded || !IsVisible || !IsLoaded) { StopLyricsScroll(); return; } // 仅在展开且可见时滚动，避免空转
        index = Math.Clamp(index, 0, LyricsList.Items.Count - 1);
        var container = LyricsList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        if (container is null) return;

        var viewer = LyricsScroll;
        var relY = container.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        // 视口相对坐标 + 当前偏移 = 内容坐标；再减去半个视口/加上半个行高使当前句居中
        var target = viewer.VerticalOffset + relY - viewer.ViewportHeight / 2 + container.ActualHeight / 2;
        target = Math.Max(0, target);

        // 目标与当前十分接近：直接落位，不再启动画（避免高频切句时抖动）
        if (Math.Abs(target - viewer.VerticalOffset) < 0.5)
        {
            StopLyricsScroll();
            return;
        }
        _lyricsScrollTarget = target;
        EnsureLyricsScrollSpring();
        if (_lyricsScrollSpring!.IsActive)
        {
            // 快速连续切句：以当前速度重定向到新目标，滚动连续不"一动一停"
            _lyricsScrollSpring.Retarget(target);
        }
        else
        {
            var durScale = Math.Clamp(_settings.Current.IslandAnimationDuration, 300, 1400) / 700.0;
            _lyricsScrollSpring.Configure(1.0, 0.34 * durScale); // 临界阻尼：无回弹、丝滑到位
            _lyricsScrollSpring.Start(viewer.VerticalOffset, target);
        }
    }

    private void EnsureLyricsScrollSpring()
    {
        if (_lyricsScrollSpring is not null) return;
        _lyricsScrollSpring = new IOSSpring();
        _lyricsScrollSpring.SetCallbacks(v =>
        {
            if (LyricsScroll is null || LyricsList.Items.Count == 0 || !_vm.IsExpanded || !IsVisible || !IsLoaded)
            {
                StopLyricsScroll();
                return;
            }
            LyricsScroll.ScrollToVerticalOffset(v);
        }, OnLyricsScrollSettled);
    }

    /// <summary>歌词滚动弹簧收敛后：精确落位到目标偏移，不留亚像素偏差。</summary>
    private void OnLyricsScrollSettled()
    {
        if (_lyricsScrollSpring?.IsActive == true) return;
        if (!IsLoaded || !IsVisible || !_vm.IsExpanded || LyricsScroll is null || LyricsList.Items.Count == 0) return;
        LyricsScroll.ScrollToVerticalOffset(_lyricsScrollTarget);
    }

    /// <summary>停止歌词滚动（收起/隐藏/列表变化时）。</summary>
    private void StopLyricsScroll()
    {
        _lyricsScrollSpring?.Stop();
    }


}


