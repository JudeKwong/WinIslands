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

public partial class IslandWindow
{
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
        ExpandedContent.Width = Math.Max(120, ExpandedWidth - 20 - ExpandedContentExtraHorizontalMargin);
        if (!_expandedHeightValid || _expandedHeightDirty)
        {
            _expandedTargetHeight = MeasureExpandedTargetHeight();
            _expandedHeightValid = true;
            _expandedHeightDirty = false;
        }
        var targetHeight = _expandedTargetHeight;
        // 先按目标卡片高度调整宿主，再启动形变动画，避免底部组件被宿主窗口裁剪。
        EnsureWindowSizeFits(null, targetHeight * FontScale);

        // 重新显示胶囊行：动画期间淡出，与展开内容交叉过渡
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        PillRow.Visibility = Visibility.Visible;
        PillRow.Opacity = 1;

        AnimateCard(ExpandedWidth, targetHeight, expand: true);
    }

    private void Collapse()
    {
        // 宿主窗口保持固定展开画布，直到卡片收回完成；提前缩窗口会把动画中的
        // 圆角卡片裁成直角。
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
        ExpandedContent.Width = Math.Max(120, Math.Max(Card.ActualWidth, CompactWidth) - 20 - ExpandedContentExtraHorizontalMargin);

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
        FinalizeCardShape(Card.Width, Card.Height, force: true); // 恢复像素对齐后强制重建圆角裁剪
    }
    private void AnimateCard(double width, double height, bool expand, Action? onCompleted = null)
    {
        var fromWidth = ResolveAnimationFrom(Card.ActualWidth, Card.Width, width);
        var fromHeight = ResolveAnimationFrom(Card.ActualHeight, Card.Height, height);
        _currentStoryboard?.Stop();
        _currentStoryboard = null;
        _compactAnimationValid = false;

        // 3.1.7：本次请求的凭证。之后所有收尾/迟到回调都必须通过代数校验。
        var ticket = _motion.Begin(expand);
        _cardTicket = ticket;

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
            FinalizeCardShape(width, height, force: true);
            _motion.TrySettle(ticket);
            ArmShapeGuard();
            ScheduleCardShapeReconcile();
            onCompleted?.Invoke();
            return;
        }

        // 默认 Spring 动效 → 真实 iOS 弹簧物理（可打断、速度连续、自然收敛），
        // 内容交叉淡入由同一弹簧驱动，文字随卡片一起动，不再"额外弹出"。
        if (_settings.Current.AnimationStyle == "Spring")
        {
            AnimateCardSpring(width, height, expand, fromWidth, fromHeight, ticket, onCompleted);
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
            // 防旧动画完成回调覆盖新动画状态（快速连续展开/收起时尺寸错乱）。
            // 3.1.7：除 Storyboard 身份外，还要求本次请求仍是状态机的最新代数。
            if (!ReferenceEquals(_currentStoryboard, sb) || !_motion.IsCurrent(ticket)) return;
            try
            {
                _currentStoryboard = null;
                _motion.TrySettle(ticket);
                // 关键：清除动画对 Card 尺寸的 HoldEnd 锁定，否则之后设置本地尺寸（含自动重算）不生效
                Card.BeginAnimation(FrameworkElement.WidthProperty, null);
                Card.BeginAnimation(FrameworkElement.HeightProperty, null);
                // 必须写回最终尺寸：清除动画后若只依赖本地值，Card 会回退到紧凑时设置的本地尺寸
                Card.Width = width;
                Card.Height = height;
                FinalizeCardShape(width, height, force: true);
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
                ScheduleCardShapeReconcile();
                ArmShapeGuard();
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
        // Radius is not a separate spring: derive it from the live animated height on
        // every size tick. This keeps the pill fully rounded throughout collapse/expand.
        // 宽、高各自写入后只记脏；SpringTicker 在本帧所有弹簧推进完后统一提交一次
        // FinalizeCardShape，确保裁剪矩形和圆角始终来自同一帧的宽高组合。
        _cardWSpring.SetCallbacks(v => { Card.Width = v; _cardSizeTickPending = true; }, OnCardSpringSettled);
        _cardHSpring.SetCallbacks(v => { Card.Height = v; _cardSizeTickPending = true; }, OnCardSpringSettled);
        _cardFadeSpring.SetCallbacks(v => { ExpandedContent.Opacity = v; PillRow.Opacity = CrossFadeCurves.PillOpacity(v, _expandedFadeIn); ApplyContentParallax(v, _expandedFadeIn); ApplyPillRowParallax(v, _expandedFadeIn); }, OnCardSpringSettled);
    }

    private void StopCardSprings()
    {
        _cardWSpring?.Stop();
        _cardHSpring?.Stop();
        _cardFadeSpring?.Stop();
        _cardSizeTickPending = false;
        _cardAnimating = false;
        _cardSpringCompleted = null;
    }

    /// <summary>所有弹簧完成本帧推进后，把宽、高合并成一次裁剪/圆角提交。</summary>
    private void OnSpringFrameUpdated()
    {
        if (!_cardSizeTickPending) return;
        _cardSizeTickPending = false;
        if (!IsLoaded || _cardClip is null || Card is null) return;
        FinalizeCardShape(Card.Width, Card.Height);
    }

    /// <summary>
    /// iOS 液态形变圆角（2.0.7）：紧凑胶囊 = 高度/2（完整药丸），展开卡片 = 用户设置半径（封顶）。
    /// 纯函数，便于单元测试。
    /// </summary>
    /// <summary>岛仔皮肤：胶囊/展开都用参考项目的圆角规则（圆角=min(高/2,33)）。</summary>
    private double ComputeRuntimeRadius(double height)
    {
        if (!_theme.IsDaozai) return ComputeCardRuntimeRadius(height, _settings.Current.CornerRadius);
        var h = double.IsFinite(height) ? Math.Max(4.0, height) : 4.0;
        // 纯黑灵动岛：圆角 = min(高/2, 33)
        return Math.Min(h / 2.0, 33.0);
    }

    internal static double ComputeCardRuntimeRadius(double height, double settingRadius)
    {
        // 3.1.7：非有限设置值（损坏的 settings.json）此前会传播成 NaN 圆角，
        // WPF 会把 NaN 当作 0，表现为整张卡片四角变直角。这里统一兜底。
        var s = double.IsFinite(settingRadius) ? Math.Clamp(settingRadius, 16.0, 40.0) : 33.0;
        var h = double.IsFinite(height) ? Math.Max(4.0, height) : 4.0; // 非法高度按最小高度兜底，绝不产生 NaN
        var r = Math.Min(h / 2.0, s);
        return Math.Clamp(double.IsFinite(r) ? r : s, 4.0, s);
    }

    /// <summary>逐帧统一应用圆角：裁剪几何 + 卡片边框 + 玻璃层三者同步，形变全程圆角连续（消除"圆角变方框"）。</summary>
    private double _lastAppliedCardRadius = double.NaN;   // 2.1.2：圆角写入去抖，收敛/静止帧不再刷布局

    /// <summary>
    /// iOS 真弹簧展开/收起（默认 Spring 动效）：
    /// 尺寸用欠阻尼/近临界弹簧（展开 ζ=0.86 轻微 Q 弹、收起 ζ=0.97 无回弹，响应 0.66s/0.56s 慢而自然），
    /// 透明度用临界阻尼弹簧，且与尺寸共用同一帧循环——内容随卡片生长/收拢自然浮现/隐去，
    /// 文字不再"额外弹出"。动画可中途打断：以当前值+速度连续改目标（iOS 打断语义）。
    /// </summary>
    private void AnimateCardSpring(double width, double height, bool expand,
        double fromWidth, double fromHeight, IslandMotionTicket ticket, Action? onCompleted)
    {
        BeginAnimationSurface();
        EnsureCardSprings();

        // 用户时长调节：默认 700ms 保持参考实现的原始节奏；低功耗模式整体加快。
        var durScale = _animDurScale;
        var lm = _animLowPowerMp;
        // 打开动画对齐参考实现：宽度 k=330/ζ=0.70、高度 k=290/ζ=0.74（欠阻尼，末段轻微过冲后回落），
        // 收起 k=620/ζ=1.0（临界阻尼，干净收回）。二阶弹簧 ω0=√k，response=2π/ω0。
        var widthZeta = expand ? OpenWidthZeta : CloseZeta;
        var widthResponse = (expand ? ResponseFromStiffness(OpenWidthStiffness) : ResponseFromStiffness(CloseStiffness)) * durScale * lm;
        var heightZeta = expand ? OpenHeightZeta : CloseZeta;
        var heightResponse = (expand ? ResponseFromStiffness(OpenHeightStiffness) : ResponseFromStiffness(CloseStiffness)) * durScale * lm;
        var fadeResponse = Math.Min(widthResponse, heightResponse);

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
        ExpandedContent.Width = Math.Max(120, (expand ? ExpandedWidth : Math.Max(Card.ActualWidth, CompactWidth)) - 20 - ExpandedContentExtraHorizontalMargin);

        _expandedFadeIn = expand; // 2.0.9：记录淡入方向，交叉淡入曲线据此解耦胶囊行与内容
        var fadeTo = expand ? 1.0 : 0.0;
        var wasAnimating = _cardAnimating; // 记录进入前的动画状态，用于打断判断
        _cardAnimating = true;
        _cardTargetW = width;
        _cardTargetH = height;
        _cardSpringCompleted = onCompleted; // 收尾时统一回调（支持打断重定向后仍生效）

        // iOS 分层弹簧（2.0.7）：圆角弹簧比尺寸快约 18%——"形状先导、尺寸跟随"，形变观感更接近 iOS 灵动岛液态形变；
        // 透明度：展开内容稍快浮现、收起内容先淡出再收拢（与尺寸共用帧循环，文字不额外弹出）。

        if (wasAnimating && _cardWSpring!.IsActive && _cardHSpring!.IsActive)
        {
            // 打断重定向（如展开一半点收起）：先重设弹簧参数再改目标——
            // 展开（欠阻尼、Q弹）与收起（近临界、收尾柔和）的手感参数不同，
            // 只 Retarget 会沿用旧方向参数导致方向切换后过冲/发僵（2.1.5 修复）。
            // Retarget 本身保留当前值+速度，改参数后速度依然连续，完整 iOS 打断语义。
            _cardWSpring.Configure(widthZeta, widthResponse);
            _cardHSpring.Configure(heightZeta, heightResponse);
            _cardFadeSpring!.Configure(1.0, CrossFadeCurves.FadeResponse(fadeResponse, expand));
            _cardWSpring.Retarget(width);
            _cardHSpring.Retarget(height);
            _cardFadeSpring!.Retarget(fadeTo);
            return;
        }

        // 全新开始：一次写到位，避免首帧闪烁
        ExpandedContent.Opacity = expand ? 0 : 1;
        PillRow.Opacity = expand ? 1 : 0;
        var fadeFrom = ExpandedContent.Opacity;
        _cardWSpring!.Configure(widthZeta, widthResponse);
        _cardWSpring.Start(fromWidth, width);
        _cardHSpring!.Configure(heightZeta, heightResponse);
        _cardHSpring.Start(fromHeight, height);
        _cardFadeSpring!.Configure(1.0, CrossFadeCurves.FadeResponse(fadeResponse, expand)); // 临界阻尼：展开内容略滞后形变浮现（形状先导、内容跟随），收起先淡出再收拢；无回弹
        _cardFadeSpring.Start(fadeFrom, fadeTo);
    }

    /// <summary>统一恢复紧凑态布局：快速展开/收回后避免尺寸、层级或宽度残留导致胶囊显示不全。</summary>
    private void RestoreCompactLayout(bool clearAnimations = true)
    {
        if (!IsLoaded || _vm.IsExpanded || _cardAnimating) return;

        if (clearAnimations)
        {
            Card.BeginAnimation(FrameworkElement.WidthProperty, null);
            Card.BeginAnimation(FrameworkElement.HeightProperty, null);
            PillRow.BeginAnimation(UIElement.OpacityProperty, null);
            ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ExpandedScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ExpandedTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        }

        Card.Width = CompactWidth;
        Card.Height = CompactHeight;
        ContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ContentGrid.RowDefinitions[1].Height = GridLength.Auto;
        ContentGrid.VerticalAlignment = VerticalAlignment.Center;
        PillRow.Visibility = Visibility.Visible;
        PillRow.Opacity = 1;
        ExpandedContent.Visibility = Visibility.Collapsed;
        ExpandedContent.Opacity = 0;
        ExpandedContent.Width = double.NaN;
        UpdateCompactContentWidth(CompactWidth);
        ResetContentParallax();
        // 等卡片尺寸与内容布局都落定后再校正宿主窗口，避免动画尾部出现裁边。
        EnsureWindowSizeFits(CompactWidth);
        FinalizeCardShape(CompactWidth, CompactHeight, force: true);
        ScheduleCardShapeReconcile();
    }

    /// <summary>卡片三个弹簧全部收敛后的统一收尾（与旧 Storyboard 路径一致的最终状态）。</summary>
    private void OnCardSpringSettled()
    {
        if (!_cardAnimating) return;
        // 3.1.7：旧请求的迟到收尾直接丢弃，绝不把旧目标几何写回卡片。
        if (!_motion.IsCurrent(_cardTicket)) return;
        if (_cardWSpring?.IsActive == true || _cardHSpring?.IsActive == true || _cardFadeSpring?.IsActive == true)
            return; // 还有弹簧在收敛，等最后一个结束再收尾
        try
        {
            _cardAnimating = false;
            _cardSizeTickPending = false;
            _motion.TrySettle(_cardTicket);
            Card.BeginAnimation(FrameworkElement.WidthProperty, null);
            Card.BeginAnimation(FrameworkElement.HeightProperty, null);
            // 收尾前先把宿主窗口扩到最终卡片宽度，避免窗口尺寸落后导致左右裁边。
            EnsureWindowSizeFits(_cardTargetW);
            Card.Width = _cardTargetW;
            Card.Height = _cardTargetH;
            _lastAppliedCardRadius = double.NaN;
            // 收尾强制写最终几何：圆角弹簧最后一帧与目标可能差 <0.5px，
            // 普通 Finalize 会早退，残留旧裁剪就会让紧凑态四角变方。
            FinalizeCardShape(_cardTargetW, _cardTargetH, force: true);
            ArmShapeGuard();
            ResetContentParallax(); // 收尾：圆角裁剪与最终尺寸精确对齐
            if (_vm.IsExpanded)
            {
                PillRow.Visibility = Visibility.Collapsed;
                PillRow.Opacity = 0;
                ExpandedContent.Opacity = 1;
                RestoreExpandedContentAutoWidth(); // 下一帧再恢复自适应宽度，避免同帧重排闪动（2.1.5）
                ScheduleCardShapeReconcile();
            }
            else
            {
                RestoreCompactLayout(clearAnimations: false);
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
            var durScale2 = _animDurScale;
            var lm2 = _animLowPowerMp;
            var resp2 = CrossFadeCurves.CompactShapeResponseSec * durScale2 * lm2;
            if (_cardWSpring?.IsActive == true) { _cardWSpring.Configure(0.96, resp2); _cardWSpring.Retarget(targetWidth); }
            if (_cardHSpring?.IsActive == true) { _cardHSpring.Configure(0.96, resp2); _cardHSpring.Retarget(targetHeight); }
            _cardTargetW = targetWidth;
            _cardTargetH = targetHeight;
            return;
        }
        if (ToleranceMath.NearWithin(Card.ActualWidth, targetWidth, 0.5)
            && ToleranceMath.NearWithin(Card.ActualHeight, targetHeight, 0.5))
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
        var durScale = _animDurScale;
        var lm = _animLowPowerMp;
        var response = CrossFadeCurves.CompactShapeResponseSec * durScale * lm; // 紧凑尺寸调整略放慢，贴合自然手感
        _cardWSpring!.Configure(0.96, response);
        _cardWSpring.Start(ResolveAnimationFrom(Card.ActualWidth, Card.Width, targetWidth), targetWidth);
        _cardHSpring!.Configure(0.96, response);
        _cardHSpring.Start(ResolveAnimationFrom(Card.ActualHeight, Card.Height, targetHeight), targetHeight);
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
        var baseMs = _animBaseMs;
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
            var durScale = _animDurScale;
            var lm = _animLowPowerMp;
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
}
