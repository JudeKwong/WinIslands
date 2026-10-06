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

    /// <summary>展开时卡片形变弹簧的基础响应时长（秒）：0.66s 略慢于历史值，形状缓缓生长、内容跟随后浮现（iOS 液态形变节奏）。</summary>
    public const double ExpandShapeResponseSec = 0.66;

    /// <summary>收起时卡片形变弹簧的基础响应时长（秒）：0.56s 略慢于历史值，收尾柔和不赶（仍快于展开约 15%，回收更利落）。</summary>
    public const double CollapseShapeResponseSec = 0.56;

    /// <summary>紧凑态内容变化（切歌/封面/推送）时卡片尺寸弹簧的基础响应时长（秒）：0.50s 平缓过渡，不跳变。</summary>
    public const double CompactShapeResponseSec = 0.50;

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
            // 2.2.18: constant-time exit - past the exit window the pill row is
            // fully gone (SmoothStep(1)=1 => 0), so skip the easing math entirely.
            if (v >= ExpandPillExitAt) return 0.0;
            return 1.0 - SmoothStep(v / ExpandPillExitAt);
        }
        // 收起：v 由 1→0，胶囊行在 v∈[0.5, 0] 内平滑淡入（SmoothStep：与内容淡出重叠交叉，不线性硬切）
        // 2.2.14: use the shared CollapsePillReappearAt constant so the pill-reintroduce point can never drift from its single definition
        // 2.2.18: constant-time exit - the pill has not started reappearing while
        // still inside the content-fade region, so return 0 without the easing math.
        if (v >= CollapsePillReappearAt) return 0.0;
        return SmoothStep((CollapsePillReappearAt - v) / CollapsePillReappearAt);
    }

    // Reappear window constant shared by PillOpacity (collapse) so mid-flight
    // retargets (expand -> collapse) keep the pill row visible continuously.
    public const double CollapsePillReappearAt = 0.5;
    /// <summary>2.2.17: pill-exit window (expand) - the compact pill row finishes
    /// handing off to the expanded content by 25% of the fade, named to mirror
    /// CollapsePillReappearAt so the whole curve family stays constants-driven.</summary>
    public const double ExpandPillExitAt = 0.25;

    /// <summary>2.2.17: sub-pixel write-dedup thresholds shared by content + pill
    /// parallax. Poses that differ by less than these are visually identical, so
    /// the renderer skips transform invalidation on static/tail frames.</summary>
    public const double ParallaxScaleEps = 0.0005;
    public const double ParallaxYEps = 0.05;

    /// <summary>2.2.17: returns true when the new parallax pose should be written.
    /// Sub-pixel repeats return false and leave the cached pose untouched; the
    /// first call always writes (transforms may not start at identity).</summary>
    public static bool ShouldWriteParallax(ref bool wrote, ref double lastScale,
        ref double lastY, double scale, double y)
    {
        if (wrote && Math.Abs(scale - lastScale) < ParallaxScaleEps && Math.Abs(y - lastY) < ParallaxYEps)
            return false;
        wrote = true;
        lastScale = scale;
        lastY = y;
        return true;
    }

    /// <summary>Expand: content starts slightly smaller, growing into place（极轻微中心生长，消除文字缩放跳动感）。</summary>
    public const double ExpandParallaxScaleFrom = 0.965;
    public const double ExpandParallaxYFrom = 0;

    /// <summary>Collapse: content gently shrinks while fading out（极轻微收缩，不产生文字缩放跳动）。</summary>
    public const double CollapseParallaxScaleTo = 0.98;
    public const double CollapseParallaxYTo = 0;

    /// <summary>2.2.14: precomputed parallax scale gains (1.0 - ScaleFrom/ScaleTo) so per-frame
    /// ContentParallax never repeats floating-point subtraction on the animation hot path.</summary>
    public const double ExpandParallaxScaleGain = 1.0 - ExpandParallaxScaleFrom;
    public const double CollapseParallaxScaleGain = 1.0 - CollapseParallaxScaleTo;

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
            var scale = ExpandParallaxScaleFrom + ExpandParallaxScaleGain * grow; // 2.2.14: precomputed gain
            return (scale, 0.0); // 2.2.18: Y stays 0 (reserved for layered drift) - dead multiply removed
        }
        var cs = CollapseParallaxScaleTo + CollapseParallaxScaleGain * grow;       // 2.2.14: precomputed gain
        return (cs, 0.0); // 2.2.18: same - Y reserved, no dead multiply
    }

    /// <summary>Pill-row parallax (2.2.15): the compact pill row slides up and shrinks
    /// slightly while handing off to the expanded content (expand), and drops back to
    /// rest when the card collapses. Driven by the SAME fade-spring value as its own
    /// opacity, so the pill text moves WITH the card - no extra pop, no separate
    /// timeline. Returns (Scale, TranslateY); never NaN/Inf.</summary>
    public const double PillRowParallaxScaleGone = 0.96; // scale at the "lifted away" pose
    public const double PillRowParallaxYTo = -5.0;       // px, negative = slides upward
    /// <summary>2.2.15: precomputed gain so the per-frame hot path never repeats a subtraction.</summary>
    public const double PillRowParallaxScaleGain = 1.0 - PillRowParallaxScaleGone;

    public static (double Scale, double TranslateY) PillRowParallax(double expandedOpacity, bool expand)
    {
        var v = double.IsFinite(expandedOpacity) ? Math.Clamp(expandedOpacity, 0.0, 1.0) : 0.0;
        double scale, y;
        if (expand)
        {
            // v: 0 -> 1, the card grows and the pill lifts away: scale 1 -> 0.96, y 0 -> -5.
            var q = SmoothStep(v);
            scale = 1.0 - PillRowParallaxScaleGain * q;
            y = PillRowParallaxYTo * q;
        }
        else
        {
            // The pill reappears at CollapsePillReappearAt from the lifted pose and drops
            // back to rest (v -> 0): scale 0.96 -> 1, y -5 -> 0.
            var q = SmoothStep(1.0 - Math.Clamp(v / CollapsePillReappearAt, 0.0, 1.0));
            scale = PillRowParallaxScaleGone + PillRowParallaxScaleGain * q;
            y = PillRowParallaxYTo * (1.0 - q);
        }
        return (scale, y);
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