using System;

namespace WinIslands.UI;

/// <summary>
/// 展开/收起的交叉淡入曲线（2.0.9）。
///
/// 背景：旧实现让展开内容在卡片形变的 72% 时就已完全不透明——卡片还没长好，
/// 内容就已全实地“悬”在卡片顶部并向下方留白，观感就是“文字从上面出现”。
/// iOS 的做法是内容淡入跟随形变：形状先长到位、内容再逐渐浮现，
/// 收起时内容先淡出、形状再收拢，避免底部行被收缩边缘“剪断”。
/// 本类集中这些纯函数，便于单元测试。
/// </summary>
public static class CrossFadeCurves
{
    /// <summary>展开时内容淡入的响应系数：&gt;1 表示略滞后于卡片形变（形状先导、内容跟随后浮现）。</summary>
    public const double ExpandContentFactor = 1.06;

    /// <summary>收起时内容淡出的响应系数：&lt;1 表示先于形变淡出（内容退场后再收拢，底部行不被剪断）。</summary>
    public const double CollapseContentFactor = 0.76;

    /// <summary>内容透明度弹簧的响应时长（秒）：由卡片形变响应时长换算。</summary>
    public static double FadeResponse(double shapeResponse, bool expand)
    {
        // NotFinite (NaN/+/-Inf) or negative: fall back to 0.03 floor so UI stays safe.
        if (!double.IsFinite(shapeResponse) || shapeResponse < 0)
            return 0.03;
        return Math.Max(0.03, shapeResponse * (expand ? ExpandContentFactor : CollapseContentFactor));
    }

    /// <summary>
    /// 胶囊行（紧凑内容）在当前展开内容透明度 v 下的目标不透明度。
    /// 展开：胶囊行在内容浮现的前 25% 期间让位（1→0），两手交接丝滑。
    /// 收起：内容淡出一半时胶囊行即开始浮现（0→1），与内容淡出重叠，消除中途打断时的空洞期。
    /// </summary>
    public static double PillOpacity(double expandedOpacity, bool expand)
    {
        // 非法输入（NaN/Inf）：按 0 兜底，胶囊行保持可见的最安全落点
        var v = double.IsFinite(expandedOpacity) ? Math.Clamp(expandedOpacity, 0.0, 1.0) : 0.0;
        if (expand)
        {
            // v: 0→1，胶囊行在 v∈[0, 0.25] 内平滑淡出让位（SmoothStep：两端零斜率，iOS 交叉淡出手感）
            return 1.0 - SmoothStep(Math.Clamp(v / 0.25, 0.0, 1.0));
        }
        // 收起：v 由 1→0，胶囊行在 v∈[0.5, 0] 内平滑淡入（SmoothStep：与内容淡出重叠交叉，不线性硬切）
        return SmoothStep(Math.Clamp((0.5 - v) / 0.5, 0.0, 1.0));
    }

    // Reappear window constant shared by PillOpacity (collapse) so mid-flight
    // retargets (expand -> collapse) keep the pill row visible continuously.
    public const double CollapsePillReappearAt = 0.5;

    /// <summary>Expand: content starts slightly smaller and higher, growing into place.</summary>
    public const double ExpandParallaxScaleFrom = 0.92;
    public const double ExpandParallaxYFrom = 0;

    /// <summary>Collapse: content gently shrinks and drifts upward while fading out.</summary>
    public const double CollapseParallaxScaleTo = 0.93;
    public const double CollapseParallaxYTo = 0;

    /// <summary>
    /// Content parallax (2.1.0): drives the expanded content's scale + vertical
    /// offset from the same spring value that drives its opacity, so the text
    /// grows/retreats in sync with the card morph instead of statically fading.
    /// Returns (Scale, TranslateY).
    /// </summary>
    public static (double Scale, double TranslateY) ContentParallax(double expandedOpacity, bool expand)
    {
        var t = double.IsFinite(expandedOpacity) ? Math.Clamp(expandedOpacity, 0.0, 1.0) : (expand ? 0.0 : 1.0);
        // iOS 节奏：生长用二次缓出（起步快、收尾缓），收拢用缓入（先缓后快），
        // 内容随卡片形变始终非线性推进，避免线性淡入/缩放的“机械感”。
        var grow = expand ? EaseOutQuad(t) : 1.0 - EaseOutQuad(1.0 - t);
        if (expand)
        {
            var scale = ExpandParallaxScaleFrom + (1.0 - ExpandParallaxScaleFrom) * grow;
            var y = ExpandParallaxYFrom * (1.0 - grow);
            return (scale, y);
        }
        var cs = CollapseParallaxScaleTo + (1.0 - CollapseParallaxScaleTo) * grow;
        var cy = CollapseParallaxYTo * (1.0 - grow);
        return (cs, cy);
    }

    /// <summary>Smoothstep 缓动：0→1 平滑插值，两端零斜率（iOS 交叉淡入/淡出曲线）。</summary>
    public static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        return x * x * (3.0 - 2.0 * x);
    }

    /// <summary>二次缓出：起步快、收尾慢（iOS 内容“生长”节奏）。</summary>
    public static double EaseOutQuad(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * (2.0 - t);
    }
}