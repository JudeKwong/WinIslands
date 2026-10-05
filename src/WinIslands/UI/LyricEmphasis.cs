using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinIslands.UI;

/// <summary>
/// 展开歌词「当前行」的 iOS 式强调（2.1.2）。
///
/// 旧实现用 FontSize 动画把当前行放大：FontSize 影响测量/布局，放大的行会
/// 挤压相邻行、宽度变宽触发换行，导致歌词列表回流跳动（「歌词放大缩小跳动」）。
///
/// 本类改为只缩放渲染（RenderTransform），行高/布局完全不变：
///   - 当前行从小到大平滑放大（带轻微 Q 弹，跟随 iOS），过去行平滑缩回；
///   - 页面不回流、不换行，垂直滚动位置稳定，文字过渡丝滑。
/// 每个元素持有自己的 ScaleTransform（ConditionalWeakTable 随元素自动回收），
/// 动画可随时打断（BeginAnimation 重新起播），停止自然连续。
/// </summary>
public static class LyricEmphasis
{
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.RegisterAttached(
        "IsCurrent", typeof(bool), typeof(LyricEmphasis),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsCurrentChanged));

    public static void SetIsCurrent(DependencyObject o, bool v) => o.SetValue(IsCurrentProperty, v);
    public static bool GetIsCurrent(DependencyObject o) => (bool)o.GetValue(IsCurrentProperty);

    /// <summary>当前行的目标缩放倍率（如 1.23 = 13px 视觉放大到 16px）。由样式按用户字号计算写入。</summary>
    public static readonly DependencyProperty TargetScaleProperty = DependencyProperty.RegisterAttached(
        "TargetScale", typeof(double), typeof(LyricEmphasis), new PropertyMetadata(1.18));

    public static void SetTargetScale(DependencyObject o, double v) => o.SetValue(TargetScaleProperty, v);
    public static double GetTargetScale(DependencyObject o) => (double)o.GetValue(TargetScaleProperty);

    /// <summary>过渡时长（毫秒）。</summary>
    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.RegisterAttached(
        "DurationMs", typeof(double), typeof(LyricEmphasis), new PropertyMetadata(240.0));

    public static void SetDurationMs(DependencyObject o, double v) => o.SetValue(DurationMsProperty, v);
    public static double GetDurationMs(DependencyObject o) => (double)o.GetValue(DurationMsProperty);

    private static readonly ConditionalWeakTable<FrameworkElement, ScaleTransform> Scales = new();

    /// <summary>全局低功耗覆盖（2.4.0）：由 IslandWindow 在设置变化时同步，统一限制本组件的动画帧率。</summary>
    public static bool LowPowerModeOverride;

    // 进入：轻 Q 弹（阻尼 14，刚度 180，负责展开时「涨到目标」的顺滑手感）；
    // 退出：更高阻尼无回弹（防止过去行缩回时弹跳）。
    private static readonly SoftSpringEase CachedInEase = Freeze(new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 });
    private static readonly SoftSpringEase CachedOutEase = Freeze(new SoftSpringEase { Damping = 18, Stiffness = 150, Mass = 1 });

    private static SoftSpringEase Freeze(SoftSpringEase e)
    {
        e.Freeze();
        return e;
    }

    /// <summary>按 基础字号/当前行字号 计算渲染缩放倍率（纯函数，便于单元测试）。</summary>
    internal static double ComputeTargetScale(double baseSize, double currentSize)
    {
        if (!double.IsFinite(baseSize) || !double.IsFinite(currentSize) || baseSize <= 0)
            return 1.18;                                   // 非法输入兜底
        return Math.Clamp(currentSize / baseSize, 1.0, 1.35);
    }

    private static void OnIsCurrentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        try
        {
            var scale = GetScale(fe);
            var target = (bool)e.NewValue ? ReadTargetScale(fe) : 1.0;
            var ms = Math.Clamp(ReadDuration(fe), 60, 900);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            var ease = (bool)e.NewValue ? CachedInEase : CachedOutEase;
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            AnimationFrameRate.Apply(anim, LowPowerModeOverride); // 2.4.0：低功耗/降频时统一限制帧率
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            // BeginAnimation 需要两个独立动画实例（同一实例不能同时动画两个 DP）
            var animY = new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            AnimationFrameRate.Apply(animY, LowPowerModeOverride); // 2.4.0
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error("LyricEmphasis failed", ex);
        }
    }

    private static ScaleTransform GetScale(FrameworkElement fe)
    {
        if (fe.RenderTransform is ScaleTransform st) return st;
        if (Scales.TryGetValue(fe, out var cached)) return cached;
        var created = new ScaleTransform(1, 1);
        Scales.Add(fe, created);
        fe.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        fe.RenderTransform = created;
        return created;
    }

    private static double ReadTargetScale(FrameworkElement fe)
        => Math.Clamp((double)fe.GetValue(TargetScaleProperty), 1.0, 1.5);

    private static double ReadDuration(FrameworkElement fe)
        => (double)fe.GetValue(DurationMsProperty);
}
