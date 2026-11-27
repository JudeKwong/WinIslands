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
/// 透明宿主窗口提供固定画布（点击穿透），仅内部卡片（Card）形变：
/// 紧凑 = 340x56 胶囊；展开宽度统一 419，音乐页 140，功能页按内容自适应高度向下生长。
/// 动画只作用于单个元素，由 WPF 合成线程 60fps 驱动，避免窗口级 Resize 卡顿。
/// 点击穿透通过 WM_NCHITTEST 显式处理：卡片内可交互，卡片外穿透。
/// </summary>
public partial class IslandWindow : Window, INotifyPropertyChanged
{
    // 紧凑尺寸来自设置（可调）；展开尺寸统一，按参考画布整体放大后留足边距。
    /// <summary>字号缩放系数：整张卡片 LayoutTransform 缩放，逻辑尺寸 = 视觉尺寸 / 缩放比。</summary>
    private double FontScale => Math.Clamp(_settings.Current.FontScale, 0.8, 1.4);

    // 参考项目的展开画布：页面内容按 399×126 排版（DaozaiPageHost）。
    private const double ReferencePageWidth = 399.0;
    private const double ReferencePageHeight = 126.0;
    /// <summary>展开内容整体放大比例：画布连同字体/图标一起放大，不再挤在原来的小尺寸里。</summary>
    internal const double ExpandedContentScale = 1.15;
    /// <summary>
    /// 展开卡片统一视觉尺寸：放大后的画布 + 边框(2) + 内容边距(20/12) + 滚动区边距(8) + 页头(22) + 余量。
    /// 高度只是下限，打开时按实际内容测量，内容更高会自动加高，保证组件完整显示。
    /// </summary>
    private const double ReferenceExpandedWidth = 496.0;
    private const double ReferenceExpandedHeight = 190.0;
    // 打开/收起动画参数（对齐参考实现的二阶弹簧：ω0=√k、response=2π/ω0）。
    private const double OpenWidthStiffness = 330.0;
    private const double OpenWidthZeta = 0.70;
    private const double OpenHeightStiffness = 290.0;
    private const double OpenHeightZeta = 0.74;
    private const double CloseStiffness = 620.0;
    private const double CloseZeta = 1.0;
    private static double ResponseFromStiffness(double stiffness) => 2.0 * Math.PI / Math.Sqrt(stiffness);
    /// <summary>展开内容在 ContentGrid 基础留白外额外增加的左右间距。</summary>
    private const double ExpandedContentExtraHorizontalMargin = 8.0;
    // 通知条与轻提示尺寸常量。
    private const double ReferenceNoticeWidth = 334.0;
    private const double ReferenceNoticeHeight = 44.0;
    private const double CopyToastHeight = 22.0;
    private const double CopyToastBottomGap = 8.0;
    private double ExpandedHeight => ReferenceExpandedHeight / FontScale;
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
            // 回归修复：动画期间 PillRow 带有显式 Width（固定行宽避免逐帧重排）。
            // FrameworkElement.Measure 会把显式 Width 直接当成 DesiredSize 返回，
            // 于是 (DesiredSize + 56) 每次调用都会把紧凑宽度再撑大约 56px；
            // 多次展开/收起后卡片会一路涨到 800px，超出宿主窗口被裁成直角。
            // 测量自然内容宽度前必须临时解除显式宽度，避免把上一轮结果当成内容。
            var pinnedWidth = PillRow.Width;
            PillRow.Width = double.NaN;
            PillRow.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = PillRow.DesiredSize.Width;
            PillRow.Width = pinnedWidth;
            // 字号缩放：逻辑宽度按比例缩小，卡片渲染时再放大，最终视觉宽度不变
            return double.IsFinite(w) && w >= 20
                ? Math.Clamp((w + 56) / FontScale, 240 / FontScale, MaxCompactVisualWidth / FontScale)
                : fallback; // 总留白 56（左侧 22 + 右侧 24，右侧略多）
        }
        catch
        {
            return fallback;
        }
    }

    private double _noPushCompactW;
    private bool _noPushWValid;

    /// <summary>通知态固定使用参考项目的 334px 宽度，不再按内容自适应撑宽。</summary>
    private double PushCardCompactWidth() => ReferenceNoticeWidth / FontScale;

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
            if (_vm.HasActivePush) return ReferenceNoticeWidth / FontScale;
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
            if (_vm.HasActivePush) return ReferenceNoticeHeight / FontScale;
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
    private double ExpandedWidth => ReferenceExpandedWidth / FontScale;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    private readonly IslandViewModel _vm;
    private readonly ThemeService _theme;
    private readonly SettingsService _settings;
    private readonly System.Windows.Forms.Screen _screen;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _compactRestoreTimer;
    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _pageResizeTimer;
    private readonly DispatcherTimer _memoryTrimTimer;
    private readonly EventHandler _onThemeChanged;      // 具名处理器：窗口关闭时可退订，防泄漏
    private readonly EventHandler<AppSettings> _onSettingsChanged;
    // 页面切换过渡常量。
    private const double DaozaiPageSlideDistance = 46.0;
    private const double DaozaiPageOldSlideDistance = 18.0;
    private const double DaozaiPageTransitionSeconds = 0.55;
    private const double DaozaiPageGhostSeconds = 0.12;
    private const int DaozaiWheelIdleResetMs = 180;
    private const int DaozaiWheelThreshold = 8;
    private const int DaozaiWheelNavigationIntervalMs = 200;

    private double _pageWheelDelta;
    private long _pageWheelLastTick;
    private long _pageWheelNavigationTick;
    private readonly DaozaiPageSpring _pageSlideSpring = new(0.0, 260.0, 0.92);
    private readonly Dictionary<IslandPage, DaozaiPageSpring> _pageDotSprings = new();
    private readonly Dictionary<IslandPage, Border> _pageDotVisuals = new();
    private readonly System.Diagnostics.Stopwatch _pageTransitionClock = System.Diagnostics.Stopwatch.StartNew();
    private IslandPage? _pageTransitionFrom;
    private IslandPage? _pageTransitionTo;
    private int _pageTransitionDirection;
    private double _pageTransitionStartSeconds;
    private double _pageTransitionLastSeconds;
    private bool _pageTransitionRendering;
    private bool _pageTransitionVisualComplete;

    private bool _waveRendering;                  // 波纹渲染中（已挂接合成帧事件）
    private DispatcherTimer? _waveTimer;                  // 低功耗模式：波纹定时器（~120fps）
    private double _lastWaveTime;
    private double _nextWaveFrameTime;
    private string _cachedWaveStyle = "Bars";
    private string? _appliedWaveStyle;
    private double _cachedWaveHeight = 1.0;
    private int _cachedWaveFps = 120;                 // 当前波形帧率目标
    private bool _cachedWaveLowPower;                 // 2.3.9: 当前波形低功耗开关（OnWaveFrame 热路径直接读取，免每帧 setting 属性访问）
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
    // v2.5.5: 相位偏移打包为单数组（Math.SinCos 生成）——逐帧每元素一次数组读取、静态初始化三角调用减半
    private static readonly (double Sin, double Cos)[] WaveOffsets09 = WaveMath.BuildWaveOffsets(0.9, 32);
    private static readonly (double Sin, double Cos)[] WaveOffsets13 = WaveMath.BuildWaveOffsets(1.3, 32);
    private bool _animationSurfaceActive;
    private bool _cardLayoutRoundingBeforeAnimation = true;
    private bool _cardSnapsBeforeAnimation = true;
    private Storyboard? _currentStoryboard;
    // 3.1.7：展开/收起唯一状态权威。旧动画迟到的完成回调会被代数校验丢弃。
    private readonly IslandMotionStateMachine _motion = new();
    private IslandMotionTicket _cardTicket = IslandMotionTicket.None;
    // 收尾后的渲染帧守护：连续若干帧校验圆角，发现退化立即强制修正。
    private int _shapeGuardFrames;
    private bool _shapeGuardHooked;
    private RectangleGeometry? _cardClip;   // 圆角裁剪几何：动画中亚像素尺寸把圆角渲染成方框，用它从根源裁出圆角边界
    private bool _visibilityRefreshQueued;
    private bool _compactAnimationValid;
    private double _compactAnimationWidth;
    private double _compactAnimationHeight;
    // ── iOS 真弹簧物理动画（2.0.5）：卡片尺寸 / 展开淡入 / 窗口位置 / 推送卡片 ──
    private IOSSpring? _cardWSpring;      // 卡片宽度弹簧
    private IOSSpring? _cardHSpring;      // 卡片高度弹簧
    private IOSSpring? _cardFadeSpring;   // 展开↔收起内容交叉淡入弹簧（与尺寸同步，文字不"额外弹出"）
    private bool _cardAnimating;          // 卡片弹簧进行中（声波 / 取色渲染让路）
    private bool _cardSizeTickPending;    // 本帧已有尺寸变化，待宽高都推进后统一提交裁剪
    private double _cardTargetW;
    private double _cardTargetH;
    private Action? _cardSpringCompleted; // 卡片弹簧收敛后的完成回调（兼容旧 Storyboard 语义）
    private bool _expandedFadeIn = true;   // 2.0.9：当前淡入方向（true=展开，false=收起），供胶囊行交叉淡入曲线使用
    // 展开目标高度：音乐页固定为参考高度，其他页按内容测量；保留缓存/脏标记，
    // 避免展开动画期间反复布局。内容变化置脏，下一次展开或切换页面时重新测量。
    private double _expandedTargetHeight = double.NaN;
    private bool _expandedHeightValid;
    private bool _expandedHeightDirty = true;
    private IOSSpring? _posLSpring;       // 窗口 Left 弹簧
    private IOSSpring? _posTSpring;       // 窗口 Top 弹簧
    private IOSSpring? _pushOpacitySpring;// 推送卡片透明度弹簧
    private IOSSpring? _pushScaleSpring;  // 推送卡片缩放弹簧
    private Storyboard? _coverTransitionStoryboard;
    private Storyboard? _pushCardStoryboard;
    private Storyboard? _copyToastStoryboard;
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
    private double _lastContentScale = 1.0;  // dedup cache (2.2.17)
    private double _lastContentY = 0.0;
    private bool _contentParallaxWrote;

    private void ApplyContentParallax(double v, bool expand)
    {
        if (ExpandedScale is null || ExpandedTranslate is null) return;
        var (scale, y) = CrossFadeCurves.ContentParallax(v, expand);
        // 2.2.17: shared sub-pixel dedup - the content pose used to be rewritten
        // every frame (even the constant-zero Y), forcing render-transform
        // invalidation at rest. Static/tail frames now skip the write entirely.
        if (!CrossFadeCurves.ShouldWriteParallax(ref _contentParallaxWrote,
                ref _lastContentScale, ref _lastContentY, scale, y))
            return;
        ExpandedScale.ScaleX = scale;
        ExpandedScale.ScaleY = scale;
        ExpandedTranslate.Y = y;
    }

    /// <summary>Snap parallax transforms back to neutral at settle.</summary>
    private void ResetContentParallax()
    {
        if (ExpandedScale is not null) ExpandedScale.ScaleX = ExpandedScale.ScaleY = 1;
        if (ExpandedTranslate is not null) ExpandedTranslate.Y = 0;
        if (PillRowScale is not null) PillRowScale.ScaleX = PillRowScale.ScaleY = 1;
        if (PillRowTranslate is not null) PillRowTranslate.Y = 0;
        _pillParallaxWrote = false;
        _lastPillScale = 1;
        _lastPillY = 0;
        _contentParallaxWrote = false;
        _lastContentScale = 1;
        _lastContentY = 0;
    }

    /// <summary>Pill-row iOS parallax (2.2.15): the compact pill row lifts away as the
    /// card expands and drops back to rest while collapsing, driven by the same
    /// fade-spring value as its own opacity (moves WITH the card, never pops on its
    /// own timeline). Dedup-guarded so static frames do not rewrite transforms.</summary>
    private double _lastPillScale = 1.0;   // dedup cache (2.2.15)
    private double _lastPillY = 0.0;
    private bool _pillParallaxWrote;

    private void ApplyPillRowParallax(double v, bool expand)
    {
        if (PillRowScale is null || PillRowTranslate is null) return;
        var (scale, y) = CrossFadeCurves.PillRowParallax(v, expand);
        // 2.2.17: shared sub-pixel dedup (same thresholds as content parallax).
        if (!CrossFadeCurves.ShouldWriteParallax(ref _pillParallaxWrote,
                ref _lastPillScale, ref _lastPillY, scale, y))
            return;
        PillRowScale.ScaleX = scale;
        PillRowScale.ScaleY = scale;
        PillRowTranslate.Y = y;
    }
    private Storyboard? _positionStoryboard;   // 位置动画独占：连续重定位先停旧动画
    private HwndSource? _hwndSource;
    private CoverFullScreenWindow? _coverFullWindow;   // #2 封面沉浸：全屏封面预览窗口

    // ── #8 动态主题：封面取色背景缓慢呼吸（60fps 合成帧驱动，仅在展开+取色开启时运行）──
    private System.Windows.Media.Color? _tintCoverColor;   // 已采样的封面主色（变化时重建 brush）
    private LinearGradientBrush? _tintBrush;               // 封面取色渐变（缓存，避免每帧重建 GC）
    private GradientStop? _tintStop0;
    private GradientStop? _tintStop1;
    private long _tintPhaseTicks;
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

    // 2.4.4：动画时长 / 低功耗缩放缓存——设置变更时经 Changed 事件刷新，
    // 展开/收起、尺寸、位置、歌词滚动等动画启动点不再每帧走 _settings.Current
    // 属性链 + Math.Clamp + 除法，直接命中缓存常量。
    private double _animDurScale = 1.0;   // Clamp(Duration,300,1400) / 700
    private double _animLowPowerMp = 1.0; // LowPowerMode ? 0.65 : 1.0
    private int _animBaseMs = 700;        // Clamp(Duration,300,1400)


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
        // 页面可见性由过渡代码独占控制，防止绑定异步覆盖首帧过渡。
        ResetPageTransitionVisuals();

        // 展开内容交错过渡区块（功能 2）：为各区块挂载位移变换，供展开/收起错峰动画使用
        _cascadeBlocks = BuildCascadeBlocks(ExpandedPushCard, HeroCard, ArtTitleGrid, ProgressGrid,
            ControlsGrid, LyricQuickOpsPanel, LyricsScroll, QuickActionsPanel);

        // 圆角裁剪：ClipToBounds 只是矩形裁剪，动画中亚像素尺寸会导致圆角边缘被矩形截断（"圆角变方框"）。
        // 用 RectangleGeometry 做 Card.Clip，从根源保证任何子内容都画不出圆角边界。
        var initialClipHeight = ResolveCardClipLength(Card.Height, Card.ActualHeight);
        var clipRadius = ComputeRuntimeRadius(initialClipHeight);
        _cardClip = new RectangleGeometry
        {
            Rect = new Rect(0, 0,
                ResolveCardClipLength(Card.Width, Card.ActualWidth),
                initialClipHeight),
            RadiusX = clipRadius,
            RadiusY = clipRadius,
        };
        Card.Clip = _cardClip;
        Card.SizeChanged += OnCardSizeChanged;

        // 悬停交互：进入 200ms 后展开，移开 120ms 后收起，展开/收起全程可被下一次悬停打断。
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (Card.IsMouseOver || KeepExpandedForPreview) return;
            if (_vm.IsExpanded) _vm.IsExpanded = false;
        };

        _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (!Card.IsMouseOver || _resizing || _mouseDownOnCard || _draggedCard || _fileDragArmed) return;
            if (_vm.IsExpanded) return;
            _collapseTimer.Stop();
            _vm.IsExpanded = true;
        };

        // 切页过渡结束后按新页面内容重新测量展开高度，避免窗口过小导致组件显示不全；
        // 延后到过渡结束再调整窗口，避免切页时先闪到旧页/胶囊行。
        _pageResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(620) };
        _pageResizeTimer.Tick += (_, _) =>
        {
            _pageResizeTimer.Stop();
            if (!_vm.IsExpanded) return;
            _expandedHeightDirty = true;
            ApplySize();
        };

        // 收起动画可能被快速切换打断导致 Card 尺寸残留：动画结束后兜底恢复精确紧凑尺寸
        _compactRestoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _compactRestoreTimer.Tick += (_, _) =>
        {
            _compactRestoreTimer.Stop();
            // 注意：这里不要碰 _memoryTrimTimer——内存修剪是常驻节电服务，
            // 若在每次收起时被 Stop，空闲内存回收将永久停用（2.1.5 修复）。
            if (IsLoaded && !_vm.IsExpanded) RestoreCompactLayout();
        };

        _memoryTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _memoryTrimTimer.Tick += (_, _) =>
        {
            // 2.2.0：播放中也可修剪——只要没有正在运行的弹簧动画（卡片/位置/推送）且未展开，
            // 后台 GC 不会造成可见卡顿；媒体播放由外部进程驱动，不受影响。
            if (_vm.IsExpanded) return;
            if (_cardAnimating || SpringTicker.ActiveCount > 0) return;
            MemoryOptimizer.RequestTrim(idle: true); // 2.2.12：空闲态（无媒体/未展开时才进入）用更积极的回收门限，压低后台占用
        };
        _memoryTrimTimer.Start();


        // 声音波纹：挂接合成帧事件，按显示器刷新率驱动，空闲时摘除不占 CPU
        if (WaveBar1 is not null)
        {
            _waveBarsExpanded.AddRange(new[] { WaveBar1, WaveBar2, WaveBar3, WaveBar4, WaveBar5, WaveBar6, WaveBar7 });
            _waveBarsCompact.AddRange(new[] { WaveBarC1, WaveBarC2, WaveBarC3, WaveBarC4, WaveBarC5, WaveBarC6, WaveBarC7 });
        }
        InitWaveVisualStyles();

        // 悬停展开（200ms 意图延迟），移开后延迟 120ms 收起；与参考实现一致，单击不再切换展开。
        Card.MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_resizing || _mouseDownOnCard || _draggedCard || _fileDragArmed) return;
            if (!_vm.IsExpanded) _hoverExpandTimer.Start();
        };

        Card.MouseLeave += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (_resizing || KeepExpandedForPreview) return; // 预览截图保持展开，不影响正常悬停收回
            Cursor = null;
            if (_vm.IsExpanded) _collapseTimer.Start();
        };

        // 单击只参与双击判定，不再负责展开/收起；窗口内第二次单击执行快捷动作。
        _clickDebounce.Tick += (_, _) =>
        {
            _clickDebounce.Stop();
            _pendingClick = false;
        };

        // 点击展开/收起；解锁状态下支持鼠标拖动
        Card.PreviewMouseLeftButtonDown += OnCardMouseLeftButtonDown;
        Card.PreviewMouseMove += OnCardMouseMove;
        Card.PreviewMouseLeftButtonUp += OnCardMouseLeftButtonUp;
        InitializeEdgeResize(); // 边缘拖拽缩放（拖住左右/下边缘调整尺寸）
        // LiquidMusic 歌词：手动滚动时也实时更新逐行焦点缩放/透明度
        LyricsScroll.ScrollChanged += (_, _) => ApplyLyricsFocusStyles(_lyricsActiveIndex);
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
            // 2.3.0：低功耗模式同步到弹簧 / 逐字卡拉OK合成帧驱动（设置即改即生效）
            SpringTicker.CapAt60Fps = _settings.Current.LowPowerMode;
            KaraokeTextBlock.LowPowerModeOverride = _settings.Current.LowPowerMode;
            FadeOnTextChange.LowPowerModeOverride = _settings.Current.LowPowerMode; // 2.4.0
            LyricEmphasis.LowPowerModeOverride = _settings.Current.LowPowerMode; // 2.4.0
            Marquee.LowPowerModeOverride = _settings.Current.LowPowerMode; // 2.2.5
            RefreshAnimationConstants(); // 2.4.4：动画常量随设置即时刷新
        };
        // 2.3.0：启动时立即按当前低功耗设置同步一次（不等待设置变更事件）
        SpringTicker.CapAt60Fps = _settings.Current.LowPowerMode;
        SpringTicker.FrameUpdated += OnSpringFrameUpdated;
        KaraokeTextBlock.LowPowerModeOverride = _settings.Current.LowPowerMode;
        FadeOnTextChange.LowPowerModeOverride = _settings.Current.LowPowerMode; // 2.4.0
        LyricEmphasis.LowPowerModeOverride = _settings.Current.LowPowerMode; // 2.4.0
        Marquee.LowPowerModeOverride = _settings.Current.LowPowerMode; // 2.2.5
        RefreshAnimationConstants(); // 2.4.4：启动即按当前设置缓存动画常量
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.PageTransitionStarted += OnPageTransitionStarted;
        _theme.ThemeChanged += _onThemeChanged;
        _settings.Changed += _onSettingsChanged;


        // 展开画布整体放大：参考实现的 399×126 排版按统一比例放大，
        // 字体与图标随之变大，组件不会再挤在一起或显示不全。
        DaozaiPageHost.LayoutTransform = new ScaleTransform(ExpandedContentScale, ExpandedContentScale);

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

    /// <summary>2.4.4：刷新动画时长 / 低功耗缩放缓存。设置会话内任何一次保存都会触发。</summary>
    private void RefreshAnimationConstants()
    {
        ComputeAnimationConstants(_settings.Current, out _animDurScale, out _animLowPowerMp, out _animBaseMs);
    }

    /// <summary>
    /// 2.4.4：把动画时长与低功耗缩放换算为缓存常量（纯函数，便于单测）。
    /// durScale 供弹簧响应时间缩放（默认 700ms ↔ ×1.0），baseMs 供传统 Storyboard 时长，
    /// lowPowerMp 供低功耗模式整体加快（×0.65）。
    /// </summary>
    internal static void ComputeAnimationConstants(AppSettings s, out double durScale, out double lowPowerMp, out int baseMs)
    {
        var clamped = Math.Clamp(s.IslandAnimationDuration, 300, 1400);
        baseMs = clamped;
        durScale = clamped / 700.0;
        lowPowerMp = s.LowPowerMode ? 0.65 : 1.0;
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
            _vm.PageTransitionStarted -= OnPageTransitionStarted;
            _theme.ThemeChanged -= _onThemeChanged;
            _settings.Changed -= _onSettingsChanged;
            SpringTicker.FrameUpdated -= OnSpringFrameUpdated;

            _collapseTimer.Stop();
            _hoverExpandTimer.Stop();
            _pageResizeTimer.Stop();
            _compactRestoreTimer.Stop();
            _memoryTrimTimer.Stop();
            StopPageTransition();
            _pageDotSprings.Clear();
            _pageDotVisuals.Clear();
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

    // ── 经典黑皮肤色值直通（颜色/按钮/页码豆统一取色）──
    public bool IsDaozai => _theme.IsDaozai;
    public bool KeepExpandedForPreview { get; set; }

    private double _previewScale = 1.0;
    /// <summary>
    /// Diagnostic-only (--screenshot-scale): lays the whole island out at an integer scale so
    /// store/promo screenshots stay vector-crisp instead of being upscaled from 100% DPI.
    /// </summary>
    public double PreviewScale
    {
        get => _previewScale;
        set
        {
            var scale = double.IsFinite(value) && value > 0.05 ? value : 1.0;
            if (Math.Abs(scale - _previewScale) < 0.001) return;
            _previewScale = scale;
            Root.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
                ? Transform.Identity
                : new ScaleTransform(scale, scale);
            EnsureWindowSizeFits();
            Reposition();
        }
    }

    public bool PageNavVisible => _vm.IsExpanded && _vm.HasMultiplePages;
    public Brush DaozaiSurfaceBrush => _theme.DaozaiSurfaceBrush;
    public Brush DaozaiButtonBrush => _theme.DaozaiButtonBrush;
    public Brush DaozaiButtonHoverBrush => _theme.DaozaiButtonHoverBrush;
    public Brush DaozaiDotBrush => _theme.DaozaiDotBrush;
    public Brush DaozaiGreenBrush => _theme.DaozaiGreenBrush;
    public Brush DaozaiRedBrush => _theme.DaozaiRedBrush;
    public Brush DaozaiBlueBrush => _theme.DaozaiBlueBrush;
    public Brush DaozaiPurpleBrush => _theme.DaozaiPurpleBrush;

    // 歌词样式直通：展开歌词使用固定度量；
    // standalone lyrics continue to expose the user-adjustable settings.
    // 展开态使用紧凑的视觉歌词度量。
    // Divide by FontScale because Card.LayoutTransform also scales the whole island;
    // this keeps the rendered text at the reference 10px/12px visual size.
    // Standalone lyrics continue to use the user's larger font-size settings.
    private const double ReferenceLyricBaseFontSize = 9.5;
    private const double ReferenceLyricCurrentFontSize = 11.0;
    private const double ReferenceLyricTextLineHeight = 12.0;
    private const double ReferenceLyricRowPadding = 0.5;
    private const double ReferenceLyricTranslationFontSize = 7.5;
    private const double ReferenceLyricsStatusFontSize = 9.5;
    public double LyricBaseFontSize => ReferenceLyricBaseFontSize / FontScale;
    public double LyricCurrentFontSize => ReferenceLyricCurrentFontSize / FontScale;
    public double LyricLineHeight => ReferenceLyricTextLineHeight / FontScale;
    public Thickness LyricLineMargin => new(0, ReferenceLyricRowPadding / FontScale, 0, ReferenceLyricRowPadding / FontScale);
    public double LyricTranslationFontSize => ReferenceLyricTranslationFontSize / FontScale;
    public double LyricsStatusFontSize => ReferenceLyricsStatusFontSize / FontScale;
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
    private Point _lastClickUp;

    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 边缘拖拽缩放优先于“点击展开/收起”：命中边缘时进入缩放，不再切换展开状态
        if (TryBeginEdgeResize(e.GetPosition(this)))
        {
            _mouseDownOnCard = false;
            _draggedCard = false;
            CancelPendingClick();
            e.Handled = true;
            return;
        }

        _mouseDownOnCard = true;
        _draggedCard = false;
        _downPoint = e.GetPosition(this);
        _hoverExpandTimer.Stop(); // 按下后不再触发悬停展开；拖动/点击行为由后续逻辑判定
    }

    private void OnCardMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 边缘缩放拖拽：实时预览尺寸
        if (_resizing)
        {
            UpdateEdgeResize(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        // 未按下时：靠近左右/下边缘切换为缩放光标，提示此处可拖动调整大小
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            UpdateEdgeResizeCursor(e.GetPosition(this));
            return;
        }

        if (!_mouseDownOnCard) return;
        if (_fileDragArmed) return; // 文件中转站组件：拖动由组件自己的拖出逻辑处理
        if (_settings.Current.IsLocked) return; // 上锁不可拖动

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _downPoint.X) > 4 || Math.Abs(pos.Y - _downPoint.Y) > 4)
        {
            _hoverExpandTimer.Stop();
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
        // 结束边缘缩放：写入设置并关闭对应方向的自动调整
        if (_resizing)
        {
            EndEdgeResize();
            e.Handled = true;
            return;
        }

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

        // 单击：挂起以等待可能的双击；单击本身不改变展开状态
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
                _hoverExpandTimer.Stop();
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
        // 灵动岛本体只使用纯黑/主题色平面，不再叠加毛玻璃折射层。
        // 3.1.8 高对比度：关闭光学层，避免半透明叠加降低可读性。
        if (_theme.IsDaozai || Accessibility.IsHighContrast) { GlassLayer.Opacity = 0; return; }
        var b = new SolidColorBrush(_theme.TintColor);
        b.Freeze();
        GlassLayer.Background = b;
    }

    /// <summary>智能透明度分层：非纯黑主题展开时玻璃层平滑升不透明度，收起回落；
    /// 纯黑灵动岛始终为 0，不叠加任何光学层。动画时长跟随当前动效皮肤。</summary>
    private void AnimateGlass(bool expanded)
    {
        if (GlassLayer is null) return;
        var glassTarget = _theme.IsDaozai ? 0.0 : (expanded ? GlassTargetOpacity : 0.0);

        _glassAnimSb?.Stop();
        _glassAnimSb = null;
        if (_settings.Current.ReduceMotion || !IsLoaded)
        {
            GlassLayer.Opacity = glassTarget;
            return;
        }

        var (styleEase, styleMs) = GetSizeAnimationStyle(expanded);
        var dur = (int)Math.Clamp(styleMs * 0.72, 200, 900);
        var sb = new Storyboard();
        AddAnim(sb, GlassLayer, UIElement.OpacityProperty, glassTarget, dur, styleEase);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode);
        _glassAnimSb = sb;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_glassAnimSb, sb)) return;
            _glassAnimSb = null;
            GlassLayer.BeginAnimation(UIElement.OpacityProperty, null);
            GlassLayer.Opacity = glassTarget;
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDaozai)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiSurfaceBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiButtonBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiButtonHoverBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiDotBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiGreenBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiRedBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiBlueBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DaozaiPurpleBrush)));
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricTranslationFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricsStatusFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricKaraokeSpeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLyricBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLyricHighlightBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLyricBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLyricHighlightBrush)));

    }

    /// <summary>按设置调整窗口与卡片尺寸（紧凑/展开）。仅当窗口尺寸真正变化时才重定位，
    /// 避免上锁/其它设置变更把用户拖动后的位置弹回默认。</summary>
    /// <summary>
    /// 确保透明窗口尺寸足够容纳当前卡片（含紧凑态自动宽度），
    /// 否则卡片超出窗口边界会被裁剪。紧凑卡片视觉宽度 = CompactWidth × FontScale。
    /// </summary>
    private double MeasureExpandedTargetHeight()
    {
        // 展开内容（页头 + 推送详情 + 页面画布 + 分页圆点）全部参与测量：
        // 卡片高度取「实测内容高度」与统一最小高度的较大值，任何页面都不会被裁掉。
        var minHeight = ExpandedHeight;
        var contentWidth = Math.Max(120, ExpandedWidth - 20 - ExpandedContentExtraHorizontalMargin);
        ExpandedContent.Width = contentWidth;
        ExpandedContent.Measure(new System.Windows.Size(contentWidth, double.PositiveInfinity));
        var measured = ExpandedContent.DesiredSize.Height;
        if (!double.IsFinite(measured) || measured <= 0) measured = minHeight;
        var maxHeight = Math.Max(minHeight, Math.Clamp(_settings.Current.MaxExpandedHeight, 240, 1200) / FontScale);
        return Math.Clamp(measured + 4, minHeight, maxHeight);
    }

    private void EnsureWindowSizeFits(double? knownCompactWidth = null, double? expandedVisualHeight = null)
    {
        if (!IsLoaded) return;
        // 展开：统一宽度 + 实测高度（失败时回退到统一高度）；紧凑：至少容纳展开宽度，
        // 推送卡片更宽时继续放大窗口，避免右侧组件被裁切。
        var compactWidth = knownCompactWidth ?? CompactWidth;
        var compactHeight = CompactHeight;
        var w = _vm.IsExpanded ? ReferenceExpandedWidth : Math.Max(ReferenceExpandedWidth, compactWidth * FontScale);
        // 宿主窗口必须始终容纳当前（含动画中的）卡片；否则 Root 会把卡片左右裁掉，
        // 圆角因此变成直角（快速展开/收起 + 宽度变化时最容易复现）。
        if (Card is not null && double.IsFinite(Card.ActualWidth) && Card.ActualWidth > 0)
            w = Math.Max(w, Card.ActualWidth);
        var h = _vm.IsExpanded
            ? Math.Max(ReferenceExpandedHeight, expandedVisualHeight ?? ReferenceExpandedHeight)
            : Math.Max(ReferenceExpandedHeight, compactHeight * FontScale);
        w *= _previewScale;
        h *= _previewScale;
        if (Math.Abs(Width - w) > 0.5 || Math.Abs(Height - h) > 0.5)
        {
            Width = w;
            Height = h;
            Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public void ApplySize()
    {
        if (_resizing) return; // 边缘缩放拖拽中：不被内容变化触发的重排覆盖实时尺寸
        // 窗口固定为能容纳最大推送卡片/展开内容的大小：推送时只需动画 Card 形变，避免窗口级 Resize 卡顿
        var targetCompactWidth = CompactWidth;
        var targetCompactHeight = CompactHeight;
        if (_vm.IsExpanded)
        {
            if (!_expandedHeightValid || _expandedHeightDirty)
            {
                _expandedTargetHeight = MeasureExpandedTargetHeight();
                _expandedHeightValid = true;
                _expandedHeightDirty = false;
            }
            EnsureWindowSizeFits(targetCompactWidth, _expandedTargetHeight * FontScale);
        }
        else
        {
            EnsureWindowSizeFits(targetCompactWidth);
        }
        if (!_vm.IsExpanded)
        {
            // 3.1.7：动画进行中不再和弹簧抢写尺寸；仅在没有卡片动效时直接落位。
            if (!IsAnyCardMotionInFlight)
            {
                Card.Width = targetCompactWidth;
                Card.Height = targetCompactHeight;
                FinalizeCardShape(targetCompactWidth, targetCompactHeight, force: true);
                ArmShapeGuard(3);
            }
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
    /// <summary>Rebuilds the LyricLineText style using fixed reference metrics and current brushes.
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
            var targetScale = Math.Min(LyricEmphasis.ComputeTargetScale(baseSize, currentSize), 1.06);
            const double emphasisMs = 240;

            var style = new Style(typeof(AppleMusicLyricLine));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.FontSizeProperty, baseSize));
            style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, baseBrush));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, margin));
            style.Setters.Add(new Setter(AppleMusicLyricLine.LineHeightProperty, lineHeight));
            style.Setters.Add(new Setter(UIElement.OpacityProperty, LyricEmphasis.OpacityBase)); // 2.3.2：与渲染映射常量保持一致
            style.Setters.Add(new Setter(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5)));
            style.Setters.Add(new Setter(LyricEmphasis.TargetScaleProperty, targetScale));
            style.Setters.Add(new Setter(LyricEmphasis.DurationMsProperty, emphasisMs));

            // 2.3.2：不透明度已由 LyricEmphasis 的归一化进度弹簧统一驱动（与缩放完全同步），
            // 此处不再追加固定时长的淡入淡出 Storyboard，避免文字"额外弹出"与双重动画。
            var trigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding(nameof(LyricLineViewModel.IsCurrent)) { Mode = BindingMode.OneWay },
                Value = true,
            };
            trigger.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty, FontWeights.Bold));
            trigger.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, highlightBrush));
            trigger.Setters.Add(new Setter(LyricEmphasis.IsCurrentProperty, true));
            style.Triggers.Add(trigger);

            // 背景和声：比主歌词更小、更淡，避免抢主行视觉焦点
            var backgroundTrigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding(nameof(LyricLineViewModel.IsBackground)) { Mode = BindingMode.OneWay },
                Value = true,
            };
            backgroundTrigger.Setters.Add(new Setter(System.Windows.Controls.Control.FontSizeProperty, Math.Max(8.0, baseSize * 0.85)));
            backgroundTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, LyricEmphasis.OpacityBase * 0.7));
            style.Triggers.Add(backgroundTrigger);

            Resources["LyricLineText"] = style;
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateLyricLineStyle failed", ex);
        }
    }


    private void PageDot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is IslandPage page)
        {
            var current = _vm.ActivePage;
            var currentIndex = current is null ? 0 : Math.Max(0, _vm.Pages.IndexOf(current));
            var nextPageIndex = Math.Max(0, _vm.Pages.IndexOf(page));
            _vm.ActivatePage(page, nextPageIndex > currentIndex ? 1 : -1);
        }
        e.Handled = true;
    }

    /// <summary>
    /// 滚轮切页：停顿 180ms 重置、方向反转重置、阈值 8、
    /// 两次切页至少间隔 200ms；负方向滚轮进入下一页。
    /// </summary>
    private void Card_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_vm.IsExpanded || !_vm.HasMultiplePages || e.Delta == 0) return;

        var now = Environment.TickCount64;
        if (_pageWheelDelta != 0
            && (now - _pageWheelLastTick > DaozaiWheelIdleResetMs
                || Math.Sign(_pageWheelDelta) != Math.Sign(e.Delta)))
        {
            _pageWheelDelta = 0;
        }

        _pageWheelLastTick = now;
        _pageWheelDelta += e.Delta;
        e.Handled = true;

        if (Math.Abs(_pageWheelDelta) < DaozaiWheelThreshold) return;
        _pageWheelDelta = 0;
        if (_pageWheelNavigationTick != 0 && now - _pageWheelNavigationTick < DaozaiWheelNavigationIntervalMs) return;

        _vm.NavigatePage(e.Delta < 0 ? 1 : -1);
        _pageWheelNavigationTick = now;
    }

    /// <summary>
    /// 键盘切页：展开且非输入状态时，←/→ 直接切页；
    /// 与参考项目一致，方向键会清空滚轮余量，不额外加 200ms 间隔。
    /// </summary>
    private void Card_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_vm.IsExpanded || !_vm.HasMultiplePages) return;
        if (e.Key != Key.Left && e.Key != Key.Right) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        _pageWheelDelta = 0;
        _vm.NavigatePage(e.Key == Key.Right ? 1 : -1);
        e.Handled = true;
    }
    private void OnPageTransitionStarted(IslandPage fromPage, IslandPage toPage, int direction)
    {
        if (!IsLoaded || !_vm.IsExpanded || _settings.Current.ReduceMotion) return;

        var carry = 0.0;
        if (_pageTransitionRendering && _pageTransitionFrom is not null)
        {
            var elapsed = _pageTransitionClock.Elapsed.TotalSeconds - _pageTransitionStartSeconds;
            if (elapsed < 0.4) carry = _pageSlideSpring.Position * 0.3;
        }

        // WPF 保留可视化树；动画被打断时必须先清掉上一次残留的旧页/新页，
        // 否则第三次快速滚动会短暂看到上上页，最后才回到目标页。
        StopPageTransition(resetVisuals: true);
        _pageTransitionFrom = fromPage;
        _pageTransitionTo = toPage;
        _pageTransitionDirection = direction;
        _pageTransitionStartSeconds = _pageTransitionClock.Elapsed.TotalSeconds;
        _pageTransitionLastSeconds = _pageTransitionStartSeconds;
        _pageTransitionVisualComplete = false;
        _pageSlideSpring.Begin(direction * DaozaiPageSlideDistance + carry, 0.0); // 与参考代码一致：保留速度，只改位置和目标

        var oldVisual = GetPageVisual(fromPage);
        var newVisual = GetPageVisual(toPage);
        if (oldVisual is null || newVisual is null) return;

        oldVisual.Value.Element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
        oldVisual.Value.Element.Opacity = 1;
        oldVisual.Value.Element.IsHitTestVisible = false;
        oldVisual.Value.Transform.X = 0;
        // 切页绘制顺序：先画旧页，再画新页，避免反向切换时旧页盖住新页。
        System.Windows.Controls.Panel.SetZIndex(oldVisual.Value.Element, 0);

        newVisual.Value.Element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
        newVisual.Value.Element.Opacity = 0;
        newVisual.Value.Element.IsHitTestVisible = false;
        newVisual.Value.Transform.X = _pageSlideSpring.Position;
        System.Windows.Controls.Panel.SetZIndex(newVisual.Value.Element, 1);

        PageTransitionTitleSnapshot.Text = fromPage.Title;
        PageTransitionTitleSnapshot.Foreground = fromPage.AccentBrush;
        PageTransitionTitleSnapshot.Visibility = IsDaozai && fromPage.Key != "Music" ? Visibility.Visible : Visibility.Collapsed;
        PageTransitionTitleSnapshot.Opacity = 1;
        PageTransitionTitleTranslate.X = 0;
        ActivePageTitleText.Opacity = 0;
        ActivePageTitleTranslate.X = 0;

        CollectPageDots();
        RetargetPageDots();
        CompositionTarget.Rendering += OnPageTransitionFrame;
        _pageTransitionRendering = true;
        UpdatePageTransitionFrame();
    }

    private void OnPageTransitionFrame(object? sender, EventArgs e) => UpdatePageTransitionFrame();

    private void UpdatePageTransitionFrame()
    {
        if (!_pageTransitionRendering) return;

        var now = _pageTransitionClock.Elapsed.TotalSeconds;
        var dt = Math.Max(0, now - _pageTransitionLastSeconds);
        _pageTransitionLastSeconds = now;
        _pageSlideSpring.Step(dt);
        foreach (var spring in _pageDotSprings.Values) spring.Step(dt);

        var elapsed = now - _pageTransitionStartSeconds;
        var oldAlpha = 1.0 - SmoothStep(0.0, 0.16, elapsed);
        var newAlpha = SmoothStep(0.03, 0.28, elapsed);
        var oldX = -_pageTransitionDirection * DaozaiPageOldSlideDistance * EaseOutCubic(elapsed / 0.3);
        var newX = _pageTransitionVisualComplete ? 0.0 : _pageSlideSpring.Position;

        if (!_pageTransitionVisualComplete && elapsed >= DaozaiPageTransitionSeconds)
        {
            _pageTransitionVisualComplete = true;
            newX = 0;
            newAlpha = 1;
        }

        if (_pageTransitionFrom is { } fromPage)
        {
            var oldVisual = GetPageVisual(fromPage);
            if (oldVisual is not null)
            {
                if (_pageTransitionVisualComplete)
                {
                    oldVisual.Value.Element.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                    oldVisual.Value.Element.Opacity = 0;
                }
                else
                {
                    oldVisual.Value.Element.Opacity = oldAlpha;
                    oldVisual.Value.Transform.X = oldX;
                }
            }
        }

        if (_pageTransitionTo is { } toPage)
        {
            var newVisual = GetPageVisual(toPage);
            if (newVisual is not null)
            {
                newVisual.Value.Element.Opacity = Math.Max(0, Math.Min(1, newAlpha));
                newVisual.Value.Transform.X = newX;
                newVisual.Value.Element.IsHitTestVisible = elapsed >= DaozaiPageGhostSeconds;
            }
        }

        PageTransitionTitleSnapshot.Opacity = _pageTransitionVisualComplete ? 0 : oldAlpha;
        PageTransitionTitleTranslate.X = _pageTransitionVisualComplete ? 0 : oldX;
        ActivePageTitleText.Opacity = _pageTransitionVisualComplete ? 1 : newAlpha;
        ActivePageTitleTranslate.X = 0;

        foreach (var (page, dot) in _pageDotVisuals)
        {
            if (_pageDotSprings.TryGetValue(page, out var spring))
                dot.Width = Math.Max(0, spring.Position);
        }

        if (_pageTransitionVisualComplete && _pageDotSprings.Values.All(s => s.Settled(16.0)))
            StopPageTransition();
    }

    private (FrameworkElement Element, TranslateTransform Transform)? GetPageVisual(IslandPage page) => page.Key switch
    {
        "Music" => (MusicPagePanel, MusicPageTranslate),
        _ => null,
    };

    private void StopPageTransition(bool resetVisuals = true)
    {
        if (_pageTransitionRendering) CompositionTarget.Rendering -= OnPageTransitionFrame;
        _pageTransitionRendering = false;
        _pageTransitionFrom = null;
        _pageTransitionTo = null;
        _pageTransitionVisualComplete = false;
        if (resetVisuals) ResetPageTransitionVisuals();
    }

    private void ResetPageTransitionVisuals()
    {
        var activeKey = _vm.ActivePage?.Key;
        ResetPageVisual(MusicPagePanel, MusicPageTranslate, activeKey == "Music");

        ActivePageTitleText.Opacity = 1;
        ActivePageTitleTranslate.X = 0;
        PageTransitionTitleSnapshot.Opacity = 0;
        PageTransitionTitleSnapshot.Visibility = Visibility.Collapsed;
        PageTransitionTitleTranslate.X = 0;

        foreach (var (page, dot) in _pageDotVisuals)
            dot.Width = page.Key == activeKey ? 16 : 4;
    }

    private static void ResetPageVisual(FrameworkElement element, TranslateTransform transform, bool visible)
    {
        element.Opacity = 1;
        element.IsHitTestVisible = true;
        transform.X = 0;
        element.SetCurrentValue(UIElement.VisibilityProperty, visible ? Visibility.Visible : Visibility.Collapsed);
        System.Windows.Controls.Panel.SetZIndex(element, 0);
    }

    private void CollectPageDots()
    {
        if (PageDots is null) return;
        _pageDotVisuals.Clear();
        foreach (var button in FindVisualChildren<System.Windows.Controls.Button>(PageDots))
        {
            if (button.Tag is not IslandPage page) continue;
            var dot = FindVisualChildren<Border>(button).FirstOrDefault(b => b.Name == "Dot");
            if (dot is not null) _pageDotVisuals[page] = dot;
        }
    }

    private void RetargetPageDots()
    {
        var activeKey = _vm.ActivePage?.Key;
        foreach (var (page, dot) in _pageDotVisuals)
        {
            if (!_pageDotSprings.TryGetValue(page, out var spring))
            {
                spring = new DaozaiPageSpring(Math.Max(0, dot.Width), 380.0, 0.86);
                _pageDotSprings[page] = spring;
            }
            spring.Retarget(page.Key == activeKey ? 16.0 : 4.0);
        }
    }

    private static double SmoothStep(double start, double end, double value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return 1.0 - Math.Pow(1.0 - t, 3.0);
    }
    /// <summary>按真实布局边距更新紧凑内容宽度，避免右侧文字和媒体按钮被裁切。</summary>
    private void UpdateCompactContentWidth(double? knownCompactWidth = null)
    {
        var horizontalChrome = ContentGrid.Margin.Left + ContentGrid.Margin.Right
            + PillRow.Margin.Left + PillRow.Margin.Right;
        PillRow.Width = Math.Max(80, (knownCompactWidth ?? CompactWidth) - horizontalChrome);
    }

    /// <summary>卡片尺寸变化时同步圆角裁剪几何（防止亚像素尺寸导致圆角变方框）。</summary>
    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 动画期间宽、高由不同弹簧推进；SizeChanged 只负责非动画态。动画帧末统一
        // 在 OnSpringFrameUpdated 中提交裁剪，避免先到的宽度帧搭配旧高度写出矩形几何。
        if (!_cardAnimating)
            FinalizeCardShape(Card.Width, Card.Height);
        RefreshCopyToastTop();
    }

    /// <summary>裁剪尺寸优先使用目标/弹簧尺寸，布局尚未完成时再用 Actual 尺寸兜底。</summary>
    internal static double ResolveCardClipLength(double preferred, double fallback)
    {
        if (double.IsFinite(preferred) && preferred > 0) return preferred;
        if (double.IsFinite(fallback) && fallback > 0) return fallback;
        return 1;
    }

    /// <summary>同步裁剪矩形。尺寸变化时立即执行，避免收回动画结束后仍沿用展开态的旧矩形。</summary>
    private bool SyncCardClipRect(double? preferredWidth = null, double? preferredHeight = null)
    {
        if (_cardClip is null || Card is null) return false;
        var w = ResolveCardClipLength(preferredWidth ?? Card.Width, Card.ActualWidth);
        var h = ResolveCardClipLength(preferredHeight ?? Card.Height, Card.ActualHeight);
        if (Math.Abs(_cardClip.Rect.Width - w) < 0.5
            && Math.Abs(_cardClip.Rect.Height - h) < 0.5)
            return false;
        _cardClip.Rect = new Rect(0, 0, w, h);
        return true;
    }

    /// <summary>重建圆角裁剪 RectangleGeometry 并强制重绘卡片/玻璃层，让动画收尾后圆角干净利落。</summary>
    private void FinalizeCardShape(double? preferredWidth = null, double? preferredHeight = null, bool force = false)
    {
        if (_cardClip is null || Card is null) return;
        try
        {
            var w = ResolveCardClipLength(preferredWidth ?? Card.Width, Card.ActualWidth);
            var h = ResolveCardClipLength(preferredHeight ?? Card.Height, Card.ActualHeight);
            var r = ComputeRuntimeRadius(h);
            var rounded = new CornerRadius(r);
            var rectChanged = force
                || Math.Abs(_cardClip.Rect.Width - w) >= 0.001
                || Math.Abs(_cardClip.Rect.Height - h) >= 0.001;
            var radiusChanged = force
                || Math.Abs(_cardClip.RadiusX - r) >= 0.05
                || Math.Abs(_cardClip.RadiusY - r) >= 0.05
                || Math.Abs(Card.CornerRadius.TopLeft - r) >= 0.05
                || (GlassLayer is not null && Math.Abs(GlassLayer.CornerRadius.TopLeft - r) >= 0.05);
            if (!rectChanged && !radiusChanged)
                return;

            if (force)
            {
                // Rebuild on completion: WPF can cache the old rectangular clip geometry.
                var rebuiltClip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
                _cardClip = rebuiltClip;
                Card.Clip = rebuiltClip;
            }
            else if (rectChanged)
            {
                _cardClip.Rect = new Rect(0, 0, w, h);
            }

            // There must be exactly one radius writer. It always follows the current
            // animated height, so a shrinking card can never briefly become rectangular.
            _cardClip.RadiusX = r;
            _cardClip.RadiusY = r;
            Card.CornerRadius = rounded;
            if (GlassLayer is not null) GlassLayer.CornerRadius = rounded;
            _lastAppliedCardRadius = r;
            Card.InvalidateVisual();
            if (GlassLayer is not null) GlassLayer.InvalidateVisual();
        }
        catch { /* transient layout exceptions are ignored */ }
    }

    /// <summary>
    /// 动画收尾后的「圆角守护」（3.1.7）。
    /// ScheduleCardShapeReconcile 走 Loaded 优先级，布局繁忙时可能被推迟数百毫秒，
    /// 这段时间里若裁剪/圆角残留旧值，用户看到的就是四角变直角的长方形。
    /// 守护挂在 CompositionTarget.Rendering 上：不依赖布局队列，连续若干帧校验，
    /// 发现圆角与当前高度不匹配就当场强制重建裁剪，杜绝长时间直角。
    /// </summary>
    private void ArmShapeGuard(int frames = 6)
    {
        _shapeGuardFrames = Math.Max(_shapeGuardFrames, frames);
        if (_shapeGuardHooked) return;
        _shapeGuardHooked = true;
        CompositionTarget.Rendering += OnShapeGuardRendering;
    }

    private void OnShapeGuardRendering(object? sender, EventArgs e)
    {
        if (!IsLoaded || Card is null) return;
        if (_cardAnimating || _currentStoryboard is not null || _motion.InFlight) return;
        if (_shapeGuardFrames > 0)
        {
            _shapeGuardFrames--;
            var w = double.IsFinite(Card.Width) && Card.Width > 0 ? Card.Width : Card.ActualWidth;
            var h = double.IsFinite(Card.Height) && Card.Height > 0 ? Card.Height : Card.ActualHeight;
            if (w > 0 && h > 0)
            {
                var expected = ComputeRuntimeRadius(h);
                var clip = Card.Clip as RectangleGeometry;
                var mismatch = clip is null
                    || Math.Abs(clip.Rect.Width - w) >= 0.5
                    || Math.Abs(clip.Rect.Height - h) >= 0.5
                    || Math.Abs(clip.RadiusX - expected) >= 0.5
                    || Math.Abs(Card.CornerRadius.TopLeft - expected) >= 0.5;
                if (mismatch) FinalizeCardShape(w, h, force: true);
            }
            return;
        }
        CompositionTarget.Rendering -= OnShapeGuardRendering;
        _shapeGuardHooked = false;
    }

    private bool IsAnyCardMotionInFlight => _motion.InFlight || _cardAnimating || _currentStoryboard is not null;

    /// <summary>动画收尾后再等一次 WPF 布局，用最终 Actual 尺寸校正裁剪，消除收回后的裁边残留。</summary>
    private void ScheduleCardShapeReconcile()
    {
        ArmShapeGuard();
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded || IsAnyCardMotionInFlight) return;
                Card.UpdateLayout();
                // Actual 可能仍停留在展开态；收尾使用目标尺寸并强制校正裁剪与圆角。
                FinalizeCardShape(Card.Width, Card.Height, force: true);
                ArmShapeGuard();
            }, DispatcherPriority.Loaded);
        }
        catch { /* 窗口已关闭时忽略 */ }
    }

    /// <summary>Full rendered diagnostic result, including the radius expected from the current height.</summary>
    internal readonly record struct CardDiagnosticResult(
        double Width, double Height, double WindowWidth, double OffsetX, double Radius, double ClipWidth,
        double GlassRadius, double ClipRadius, double ExpectedRadius, bool RadiusMatches,
        bool CornersTransparent, bool ExpandedLayoutOk, string Detail);

    /// <summary>Lightweight per-frame geometry probe used while the spring is moving.</summary>
    internal readonly record struct CardShapeProbe(
        double Width, double Height, double Radius, double GlassRadius, double ClipRadius,
        double ExpectedRadius, bool RadiusMatches, bool FitsWindow, string Detail);

    /// <summary>
    /// Reads the live geometry without forcing layout. This is deliberately used between
    /// animation ticks so the test observes the same values that the next composition pass
    /// will consume, rather than a repaired post-layout state.
    /// </summary>
    internal CardShapeProbe CaptureCardShapeProbe()
    {
        try
        {
            var width = Card.ActualWidth > 1 ? Card.ActualWidth : Math.Max(0, Card.Width);
            var height = Card.ActualHeight > 1 ? Card.ActualHeight : Math.Max(0, Card.Height);
            var radiusHeight = double.IsFinite(height) && height > 0 ? height : Card.Height;
            var expectedRadius = ComputeRuntimeRadius(radiusHeight);
            var clip = Card.Clip as RectangleGeometry;
            var cardRadius = Card.CornerRadius.TopLeft;
            var glassRadius = GlassLayer?.CornerRadius.TopLeft ?? double.NaN;
            var clipRadius = clip?.RadiusX ?? double.NaN;
            var cardOk = Math.Abs(cardRadius - expectedRadius) <= 0.75;
            var glassOk = GlassLayer is null || Math.Abs(glassRadius - expectedRadius) <= 0.75;
            var clipOk = clip is null || Math.Abs(clipRadius - expectedRadius) <= 0.75;
            var radiusOk = cardOk && glassOk && clipOk;

            double offsetX = 0, offsetY = 0;
            try
            {
                var origin = Card.TransformToAncestor(Root).Transform(new System.Windows.Point(0, 0));
                offsetX = origin.X;
                offsetY = origin.Y;
            }
            catch { /* visual tree not connected yet */ }

            var fitsWindow = offsetX >= -0.5 && offsetY >= -0.5
                && offsetX + width <= ActualWidth + 0.5
                && offsetY + height <= ActualHeight + 0.5;

            var detail =
                $"expanded={_vm.IsExpanded}, card={Card.Width:0.###}x{Card.Height:0.###}, " +
                $"actual={Card.ActualWidth:0.###}x{Card.ActualHeight:0.###}, " +
                $"radius={cardRadius:0.###}, glass={glassRadius:0.###}, clipRadius={clipRadius:0.###}, " +
                $"expected={expectedRadius:0.###}, radiusOk={radiusOk}, fitsWindow={fitsWindow}, " +
                $"offset={offsetX:0.###},{offsetY:0.###}, animating={_cardAnimating}";

            return new CardShapeProbe(width, height, cardRadius, glassRadius, clipRadius,
                expectedRadius, radiusOk, fitsWindow, detail);
        }
        catch (Exception ex)
        {
            return new CardShapeProbe(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, false, false, ex.Message);
        }
    }

    /// <summary>
    /// 校验展开页封面与进度区域没有几何相交。不可见或尚未测量时视为不适用。
    /// </summary>
    private (bool Ok, string Detail) CaptureExpandedCoverProgressLayout()
    {
        try
        {
            if (!_vm.IsExpanded || BigArt is null || ProgressGrid is null)
                return (true, "coverProgress=not-applicable");

            if (BigArt.Visibility != Visibility.Visible || ProgressGrid.Visibility != Visibility.Visible)
                return (true,
                    $"coverProgress=not-applicable(art={BigArt.Visibility},progress={ProgressGrid.Visibility})");

            if (BigArt.ActualWidth < 1 || BigArt.ActualHeight < 1
                || ProgressGrid.ActualWidth < 1 || ProgressGrid.ActualHeight < 1)
                return (true, "coverProgress=not-measured");

            var art = BigArt.TransformToAncestor(Card).TransformBounds(
                new Rect(0, 0, BigArt.ActualWidth, BigArt.ActualHeight));
            var progress = ProgressGrid.TransformToAncestor(Card).TransformBounds(
                new Rect(0, 0, ProgressGrid.ActualWidth, ProgressGrid.ActualHeight));
            var overlap = Rect.Intersect(art, progress);
            var ok = overlap.IsEmpty || overlap.Width <= 1 || overlap.Height <= 1;
            var overlapText = overlap.IsEmpty ? "none" : $"{overlap.Width:0.#}x{overlap.Height:0.#}";
            return (ok,
                $"art={art.X:0.#},{art.Y:0.#},{art.Width:0.#}x{art.Height:0.#}, " +
                $"progress={progress.X:0.#},{progress.Y:0.#},{progress.Width:0.#}x{progress.Height:0.#}, " +
                $"overlap={overlapText}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Expanded cover/progress layout probe failed", ex);
            return (true, "coverProgress=probe-failed: " + ex.Message);
        }
    }
    /// <summary>
    /// Full diagnostic snapshot: renders the host window, verifies corner transparency,
    /// and checks that all three radius consumers match the current card height.
    /// </summary>
    internal CardDiagnosticResult CaptureCardDiagnostic(string path, string stage)
    {
        try
        {
            UpdateLayout();
            Root.UpdateLayout();
            Card.UpdateLayout();
            var probe = CaptureCardShapeProbe();
            var clip = Card.Clip as RectangleGeometry;
            var cardW = Math.Max(1, (int)Math.Ceiling(probe.Width > 1 ? probe.Width : Card.Width));
            var cardH = Math.Max(1, (int)Math.Ceiling(probe.Height > 1 ? probe.Height : Card.Height));
            var winW = Math.Max(1, (int)Math.Ceiling(Root.ActualWidth));
            var winH = Math.Max(1, (int)Math.Ceiling(Root.ActualHeight));
            double offsetX = 0, offsetY = 0;
            try
            {
                var origin = Card.TransformToAncestor(Root).Transform(new System.Windows.Point(0, 0));
                offsetX = origin.X;
                offsetY = origin.Y;
            }
            catch { /* visual tree not connected yet */ }

            var bitmap = new RenderTargetBitmap(winW, winH, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Root);

            var stride = winW * 4;
            var pixels = new byte[stride * winH];
            bitmap.CopyPixels(pixels, stride, 0);
            var x0 = (int)Math.Round(offsetX);
            var y0 = (int)Math.Round(offsetY);
            var x1 = x0 + cardW - 2;
            var y1 = y0 + cardH - 2;
            var inBounds = x0 >= 0 && y0 >= 0 && x1 < winW && y1 < winH;
            byte AlphaAt(int x, int y) => inBounds ? pixels[y * stride + x * 4 + 3] : (byte)255;
            var cornerAlpha = inBounds
                ? new[] { AlphaAt(x0 + 1, y0 + 1), AlphaAt(x1, y0 + 1), AlphaAt(x0 + 1, y1), AlphaAt(x1, y1) }
                : new byte[] { 255, 255, 255, 255 };
            var cornersOk = inBounds
                && cornerAlpha[0] <= 24 && cornerAlpha[1] <= 24 && cornerAlpha[2] <= 24 && cornerAlpha[3] <= 24;
            var layout = CaptureExpandedCoverProgressLayout();
            var detail = probe.Detail + $", corners=[{string.Join(",", cornerAlpha)}]"
                + $", coverProgressOk={layout.Ok}, {layout.Detail}";
            AppLogger.Info($"Card shape [{stage}]: {detail}");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
                encoder.Save(stream);

            return new CardDiagnosticResult(
                probe.Width, probe.Height, ActualWidth, offsetX, probe.Radius,
                clip?.Rect.Width ?? double.NaN, probe.GlassRadius, probe.ClipRadius,
                probe.ExpectedRadius, probe.RadiusMatches, cornersOk && probe.FitsWindow,
                layout.Ok, detail);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Card diagnostic capture failed: {stage}", ex);
            return new CardDiagnosticResult(double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false, false, true, ex.Message);
        }
    }

    private void ApplyAppearance()
    {
        try { System.Windows.Documents.TextElement.SetFontFamily(Card, new System.Windows.Media.FontFamily(_settings.Current.FontFamily)); } catch { /* 非法字体名忽略 */ }
        var hh = Card.ActualHeight > 4 ? Card.ActualHeight : (double.IsFinite(CompactHeight) ? CompactHeight : 56);
        var rounded = new CornerRadius(ComputeRuntimeRadius(hh));
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
        Card.BorderThickness = _theme.IsDaozai ? new Thickness(0) : new Thickness(1);
        // 灵动岛使用纯黑平面，不额外叠加玻璃投影。
        if (Card.Effect is System.Windows.Media.Effects.DropShadowEffect)
            Card.Effect = null;
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
            && ToleranceMath.NearWithin(targetWidth, _compactAnimationWidth, 0.5)
            && ToleranceMath.NearWithin(targetHeight, _compactAnimationHeight, 0.5))
            return;

        if (_currentStoryboard is null
            && ToleranceMath.NearWithin(Card.ActualWidth, targetWidth, 0.5)
            && ToleranceMath.NearWithin(Card.ActualHeight, targetHeight, 0.5))
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
            FinalizeCardShape(targetWidth, targetHeight, force: true); // 收尾：强制校正最终几何
        };
        sb.Begin();
    }

    /// <summary>第三方应用上岛：推送卡片淡入 + 轻微缩放的丝滑动画。</summary>
    private void PlayPushCardAnimation()
    {
        if (!IsLoaded || CompactPushCard is null || !_vm.HasActivePush) return;
        // 先清掉上一条通知可能仍在运行的动画，避免透明度停在 0。
        _pushCardStoryboard?.Stop();
        _pushCardStoryboard = null;
        CompactPushCard.BeginAnimation(UIElement.OpacityProperty, null);
        CompactPushScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactPushScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CompactPushCard.Opacity = 0;
        CompactPushScale.ScaleX = CompactPushScale.ScaleY = 0.94;
        // 默认 Spring 动效：真实弹簧淡入 + 轻微缩放，推送卡片自然"上岛"
        if (_settings.Current.AnimationStyle == "Spring")
        {
            EnsurePushSprings();
            var lmPush = _animLowPowerMp;
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

    /// <summary>
    /// 普通通知铃铛摆动：参考实现为 0.95 秒、两圈衰减正弦，角度峰值约 0.48rad。
    /// 按 60fps 采样成关键帧，结束后回到静止角度。
    /// </summary>
    private void PlayNoticeBellSwing()
    {
        if (!IsLoaded || !_vm.HasActivePush) return;
        const double duration = 0.95;
        const int steps = 57;
        foreach (var rotate in new[] { CompactNoticeBellRotate, ExpandedNoticeBellRotate })
        {
            if (rotate is null) continue;
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = 0;
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(duration),
                FillBehavior = FillBehavior.Stop,
            };
            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;
                var angle = Math.Sin(t * Math.PI * 4.0) * (180.0 / Math.PI) * 0.48 * Math.Sqrt(Math.Max(0.0, 1.0 - t));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(angle, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t * duration))));
            }
            rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        }
    }

    /// <summary>微信/QQ 通知上的「查看」：切回对应聊天窗口并收起通知。</summary>
    private void ViewPushMessenger_Click(object sender, RoutedEventArgs e)
    {
        _vm.ActivateActivePushMessenger();
        e.Handled = true;
    }

    /// <summary>关闭当前紧凑通知；若队列还有下一条，会自动接着显示。</summary>
    private void DismissPush_Click(object sender, RoutedEventArgs e)
    {
        _vm.DismissActivePush();
        e.Handled = true;
    }

    /// <summary>复制成功浮层位置：紧凑态在岛下方；展开态放在 140px 画布内，避免被宿主窗口裁剪。</summary>
    private void RefreshCopyToastTop()
    {
        if (CopyToastTranslate is null) return;
        var cardVisualHeight = Card is not null && Card.ActualHeight > 4
            ? Card.ActualHeight * FontScale
            : (_vm.IsExpanded ? ReferenceExpandedHeight : CompactHeight * FontScale);
        CopyToastTranslate.Y = _vm.IsExpanded
            ? Math.Max(4, ReferenceExpandedHeight - CopyToastHeight - CopyToastBottomGap)
            : Math.Max(4, cardVisualHeight + CopyToastBottomGap);
    }

    /// <summary>复制成功“闪现”提示：2.4 秒总时长，160ms 淡入，2.1s 后 300ms 淡出，并带 6px 上浮。</summary>
    private void PlayCopyToastAnimation()
    {
        if (CopyToast is null || CopyToastTranslate is null) return;
        _copyToastStoryboard?.Stop();
        _copyToastStoryboard = null;
        CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
        CopyToastTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        if (!_vm.HasCopyToast)
        {
            CopyToast.Opacity = 0;
            return;
        }

        CopyToast.Opacity = 0;
        var targetToastY = CopyToastTranslate.Y;
        var sb = new Storyboard();
        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), CachedCubicEaseOut));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2100))));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400)), CachedCubicEaseOut));
        Storyboard.SetTarget(opacity, CopyToast);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(opacity);
        AddAnim(sb, CopyToastTranslate, TranslateTransform.YProperty, targetToastY, 300, CachedCubicEaseOut, null, targetToastY + 6);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode);
        _copyToastStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_copyToastStoryboard, sb)) return;
            _copyToastStoryboard = null;
            CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
            CopyToastTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            CopyToast.Opacity = _vm.HasCopyToast ? 1 : 0;
            if (_vm.HasCopyToast) RefreshCopyToastTop();
        };
        sb.Begin();
    }


    /// <summary>右键菜单主题色（圆角毛玻璃）。</summary>
    private void ApplyMenuTheme()
    {
        void Add(string key, Brush b) { b.Freeze(); Resources[key] = b; }
        SolidColorBrush bg, border, text, hover, glassTint;
        if (_theme.IsDark)
        {
            bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x0A, 0x12, 0x14, 0x1A));
            border = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            text = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF5, 0xFA));
            hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            glassTint = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x16, 0x1C));
        }
        else
        {
            bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x08, 0xF7, 0xF9, 0xFD));
            border = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x28, 0x00, 0x00, 0x00));
            text = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x24));
            hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1C, 0x00, 0x00, 0x00));
            glassTint = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF4, 0xF9));
        }
        Add("MenuBgBrush", bg);
        Add("MenuBorderBrush", border);
        Add("MenuTextBrush", text);
        Add("MenuHoverBrush", hover);
        Add("MenuGlassTintBrush", glassTint);
        Resources["MenuGlassIsDark"] = _theme.IsDark;

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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageNavVisible)));
                AnimateSize();
                RefreshCopyToastTop();
                ApplyCoverTint(); // 展开/收起时同步封面取色呼吸（#8 动态主题）
                if (_vm.IsExpanded && _vm.LyricIndex >= 0)
                    Dispatcher.BeginInvoke(() => ScrollLyricsTo(_vm.LyricIndex), DispatcherPriority.Loaded);
                if (!_vm.IsExpanded)
                {
                    StopLyricsScroll(); // 收起时立即停止歌词补间，避免在隐藏状态下继续写入滚动位置
                    StopPageTransition(); // 收起时立即恢复单页可见，避免下次展开残留过渡状态
                    _compactRestoreTimer.Start(); // 收起后兜底恢复精确尺寸，避免多次切换后上下间距异常
                }
                break;
            case nameof(IslandViewModel.HasMultiplePages):
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageNavVisible)));
                break;
            case nameof(IslandViewModel.Pages):
                _pageDotSprings.Clear();
                _pageDotVisuals.Clear();
                StopPageTransition();
                break;
            case nameof(IslandViewModel.ActivePage):
                _expandedHeightDirty = true; // 新页面内容高度不同，过渡结束后重新测量
                if (!_pageTransitionRendering) ResetPageTransitionVisuals(); // 过渡中由动画独占控制
                // 参考项目切页只跑页面过渡，不重新跑展开动画；
                // 已展开时再调用 AnimateSize() 会先露出胶囊行/旧内容，造成闪页。
                if (_vm.IsExpanded)
                {
                    Dispatcher.BeginInvoke(ExpandedContent.ScrollToTop, DispatcherPriority.Loaded);
                    _pageResizeTimer.Stop();
                    _pageResizeTimer.Start(); // 过渡结束（约 0.55s）后再调整窗口高度
                }
                break;
            case nameof(IslandViewModel.LyricIndex):
                if (_vm.LyricIndex >= 0) QueueLyricsScroll(_vm.LyricIndex);
                break;
            case nameof(IslandViewModel.ShowLyricTranslation):
                // 翻译行显隐会改变当前行高度：切换后按新的行高重新把当前句对齐到视口中心
                if (_vm.IsExpanded && _vm.LyricIndex >= 0)
                    Dispatcher.BeginInvoke(() => ScrollLyricsTo(_vm.LyricIndex), DispatcherPriority.Loaded);
                break;
            case nameof(IslandViewModel.CompactItems):
                // 组件列表变化（如音量指示出现/消失、临时状态胶囊增删）时平滑调整尺寸
                if (!_vm.IsExpanded && _vm.IsVisible) AnimateCompactSize();
                break;
            case nameof(IslandViewModel.CurrentLyricText):
                // 当前歌词行变化时，若处于紧凑态则平滑调整宽度，避免长歌词被裁切/遮挡
                if (!_vm.IsExpanded && _vm.IsVisible) AnimateCompactSize();
                break;
            case nameof(IslandViewModel.HasCopyToast):
                RefreshCopyToastTop();
                PlayCopyToastAnimation();
                break;
            case nameof(IslandViewModel.HasActivePush):
                _expandedHeightDirty = true; // 推送内容会影响展开卡片高度，下次展开前重测（2.1.6）
                ApplySize();           // 确保窗口足够大（首次）
                AnimateCompactSize();  // 尺寸变化：弹簧动画，丝滑
                PlayPushCardAnimation(); // 上岛卡片：淡入 + 缩放动画
                PlayNoticeBellSwing();   // 普通通知：铃铛衰减摆动
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

}


