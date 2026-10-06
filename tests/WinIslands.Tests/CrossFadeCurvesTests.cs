using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 展开/收起交叉淡入曲线测试（2.0.9）：
/// 内容淡入跟随形变（不"从上面出现"）、收起先淡出、胶囊行与内容交接丝滑。
/// </summary>
public sealed class CrossFadeCurvesTests
{
    [Fact]
    public void Expand_ContentFade_TrailsShape()
    {
        // 展开时内容淡入必须略滞后于形变（系数 >1），形状先导、内容随后浮现
        var shape = 0.62;
        var fade = CrossFadeCurves.FadeResponse(shape, expand: true);
        Assert.True(fade > shape, $"expand fade should trail shape: {fade} vs {shape}");
        Assert.Equal(shape * CrossFadeCurves.ExpandContentFactor, fade, 3);
    }

    [Fact]
    public void Collapse_ContentFade_LeadsShape()
    {
        // 收起时内容必须先于形变淡出（系数 <1），底部行不被收缩边缘剪断
        var shape = 0.52;
        var fade = CrossFadeCurves.FadeResponse(shape, expand: false);
        Assert.True(fade < shape, $"collapse fade should lead shape: {fade} vs {shape}");
        Assert.Equal(shape * CrossFadeCurves.CollapseContentFactor, fade, 3);
    }

    [Fact]
    public void FadeResponse_NeverBelowFloor()
    {
        Assert.True(CrossFadeCurves.FadeResponse(0.0, true) >= 0.03);
        Assert.True(CrossFadeCurves.FadeResponse(double.NaN, false) >= 0.03);
        Assert.True(CrossFadeCurves.FadeResponse(-5, true) >= 0.03);
    }

    [Fact]
    public void Expand_PillYieldsDuringFirstQuarter_ThenStaysHidden()
    {
        // 展开：胶囊行在内容浮现前 25% 内让位；之后保持隐藏，不闪烁
        Assert.Equal(1.0, CrossFadeCurves.PillOpacity(0.0, expand: true), 6);
        Assert.InRange(CrossFadeCurves.PillOpacity(0.125, expand: true), 0.45, 0.55);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(0.25, expand: true), 6);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(0.9, expand: true), 6);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(1.0, expand: true), 6);
    }

    [Fact]
    public void Collapse_PillReappearsBeforeContentFullyFades_NoDeadAir()
    {
        // 收起：胶囊行在内容淡出一半时就开始浮现，与内容淡出重叠，
        // 展开中途点收起时（v 在 0.2~0.5 之间）胶囊行立即接棒，消除空洞期
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(1.0, expand: false), 6);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(0.6, expand: false), 6);
        Assert.InRange(CrossFadeCurves.PillOpacity(0.4, expand: false), 0.09, 0.12); // SmoothStep(0.2)=0.104，2.1.7 变为平滑过渡
        Assert.InRange(CrossFadeCurves.PillOpacity(0.25, expand: false), 0.45, 0.55);
        Assert.InRange(CrossFadeCurves.PillOpacity(0.2, expand: false), 0.63, 0.67); // SmoothStep(0.6)=0.648，2.1.7 变为平滑过渡
        Assert.Equal(1.0, CrossFadeCurves.PillOpacity(0.0, expand: false), 6);
    }

    [Fact]
    public void PillOpacity_ClampsOutOfRangeInputs()
    {
        Assert.Equal(1.0, CrossFadeCurves.PillOpacity(-0.5, expand: true), 6);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(1.6, expand: false), 6);
        // NaN/Inf 按 0 兜底 → 胶囊行保持可见
        Assert.Equal(1.0, CrossFadeCurves.PillOpacity(double.NaN, expand: true), 6);
        Assert.Equal(1.0, CrossFadeCurves.PillOpacity(double.PositiveInfinity, expand: false), 6);
    }

    [Fact]
    public void Parallax_Expand_ContentGrowsIntoPlace()
    {
        // 展开：内容从中心缩放浮现（iOS 真实行为：无垂直漂移，纯中心缩放 + 淡入）
        // 与卡片形变同频，文字不再从上面出现
        var (s0, y0) = CrossFadeCurves.ContentParallax(0.0, expand: true);
        Assert.Equal(CrossFadeCurves.ExpandParallaxScaleFrom, s0, 6);
        Assert.Equal(CrossFadeCurves.ExpandParallaxYFrom, y0, 6);
        var (s1, y1) = CrossFadeCurves.ContentParallax(1.0, expand: true);
        Assert.Equal(1.0, s1, 6);
        Assert.Equal(0.0, y1, 6);
        var (sm, ym) = CrossFadeCurves.ContentParallax(0.5, expand: true);
        Assert.InRange(sm, 0.988, 0.994); // EaseOutQuad(0.5)=0.75 → 0.92+0.08·0.75=0.98，2.1.7 内容生长改为缓出 // 0.92 -> 1.0 的中点
        Assert.Equal(0.0, ym, 6);         // 中心缩放：无垂直位移
    }

    [Fact]
    public void Parallax_Collapse_ContentRetreats()
    {
        // 收起：内容轻微收缩并淡出（中心缩放，无垂直漂移）
        var (s1, y1) = CrossFadeCurves.ContentParallax(1.0, expand: false);
        Assert.Equal(1.0, s1, 6);
        Assert.Equal(0.0, y1, 6);
        var (s0, y0) = CrossFadeCurves.ContentParallax(0.0, expand: false);
        Assert.Equal(CrossFadeCurves.CollapseParallaxScaleTo, s0, 6);
        Assert.Equal(CrossFadeCurves.CollapseParallaxYTo, y0, 6);
    }

    [Fact]
    public void Parallax_ClampsBadInputs()
    {
        // NaN/Inf 按方向终值兜底；越界值钳制到 [0,1]
        var (sn, yn) = CrossFadeCurves.ContentParallax(double.NaN, expand: true);
        Assert.Equal(CrossFadeCurves.ExpandParallaxScaleFrom, sn, 6);
        Assert.Equal(CrossFadeCurves.ExpandParallaxYFrom, yn, 6);
        var (so, yo) = CrossFadeCurves.ContentParallax(double.PositiveInfinity, expand: false);
        Assert.Equal(1.0, so, 6);
        Assert.Equal(0.0, yo, 6);
        var (sx, yx) = CrossFadeCurves.ContentParallax(2.0, expand: true);
        Assert.Equal(1.0, sx, 6);
        Assert.Equal(0.0, yx, 6);
    }

    [Fact]
    public void SmoothStep_EndpointsAndMidpoint()
    {
        // Smoothstep：两端零斜率、严格 0→1，中点恰为 0.5（iOS 交叉淡入/淡出曲线）
        Assert.Equal(0.0, CrossFadeCurves.SmoothStep(0.0), 9);
        Assert.Equal(1.0, CrossFadeCurves.SmoothStep(1.0), 9);
        Assert.Equal(0.5, CrossFadeCurves.SmoothStep(0.5), 9);
        Assert.Equal(1.0, CrossFadeCurves.SmoothStep(1.7), 9); // 越界钳制
        Assert.Equal(0.0, CrossFadeCurves.SmoothStep(-0.3), 9);
    }

    [Fact]
    public void EaseOutQuad_EndpointsAndMidpoint()
    {
        // 二次缓出：0→0、1→1，中点为 0.75（起步快、收尾慢的增长节奏）
        Assert.Equal(0.0, CrossFadeCurves.EaseOutQuad(0.0), 9);
        Assert.Equal(1.0, CrossFadeCurves.EaseOutQuad(1.0), 9);
        Assert.Equal(0.75, CrossFadeCurves.EaseOutQuad(0.5), 9);
        Assert.Equal(1.0, CrossFadeCurves.EaseOutQuad(1.2), 9);
        Assert.Equal(0.0, CrossFadeCurves.EaseOutQuad(-1.0), 9);
    }

    [Fact]
    public void Parallax_Expand_ScaleMonotonicAndEased()
    {
        // 2.1.7：内容生长按 EaseOutQuad 非线性推进（起步快、收尾缓），且随透明度严格单调增
        double prev = -1;
        for (var i = 0; i <= 40; i++)
        {
            var v = i / 40.0;
            var (s, _) = CrossFadeCurves.ContentParallax(v, expand: true);
            Assert.True(s >= prev, $"scale must not decrease at v={v}");
            prev = s;
        }
        // EaseOutQuad 使中段领先线性：v=0.5 时 scale=0.98 > 线性中点 0.96
        var (sm, _) = CrossFadeCurves.ContentParallax(0.5, expand: true);
        Assert.InRange(sm, 0.988, 0.994);
    }

    [Fact]
    public void Parallax_Collapse_ScaleRetreatsEased()
    {
        // 2.1.7：收拢时 opacity v 由 1 → 0，scale 由 1 → 0.93（在 v 增大的方向上单调递增），
        // 即随收起进度以 EaseInQuad 节奏收缩（收起时间轴上看是快起慢落），终值精确。
        var (s1, _) = CrossFadeCurves.ContentParallax(1.0, expand: false);
        Assert.Equal(1.0, s1, 9);
        var (s0, _) = CrossFadeCurves.ContentParallax(0.0, expand: false);
        Assert.Equal(CrossFadeCurves.CollapseParallaxScaleTo, s0, 9);
        double prev = -1;
        for (var i = 0; i <= 40; i++)
        {
            var v = i / 40.0;
            var (s, _) = CrossFadeCurves.ContentParallax(v, expand: false);
            Assert.True(s >= prev, $"scale must not decrease at v={v}");
            prev = s;
        }
        // EaseInQuad(v)：v=0.5 时 scale = 0.93 + 0.07*0.25 = 0.9475（慢于线性推进）
        var (sm, _) = CrossFadeCurves.ContentParallax(0.5, expand: false);
        Assert.InRange(sm, 0.982, 0.988);
    }
    [Fact]
    public void Parallax_ScaleRangesAreSubtle_NoVisibleTextJump()
    {
        // 2.2.5：视差缩放范围收敛到 ±3.5%/±2% 以内，避免文字放大缩小跳动感
        Assert.InRange(CrossFadeCurves.ExpandParallaxScaleFrom, 0.95, 1.0);
        Assert.InRange(CrossFadeCurves.CollapseParallaxScaleTo, 0.95, 1.0);
    }

    [Fact]
    public void PillOpacity_Collapse_MatchesSharedConstantBoundary()
    {
        // 2.2.14: the collapse pill-reappear curve is driven by the shared CollapsePillReappearAt
        // constant - at exactly that boundary the pill is fully faded out (0) and below it reappears
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(CrossFadeCurves.CollapsePillReappearAt, expand: false), 9);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(0.5, expand: false), 9); // 0.5 == CollapsePillReappearAt today
        Assert.Equal(1.0, CrossFadeCurves.PillOpacity(0.0, expand: false), 9);
    }

    [Fact]
    public void Parallax_ScaleGainConstants_ConsistentWithBounds()
    {
        // 2.2.14: precomputed gains equal 1 - From/To, stay small and positive (subtle parallax, no text jump)
        Assert.Equal(1.0 - CrossFadeCurves.ExpandParallaxScaleFrom, CrossFadeCurves.ExpandParallaxScaleGain, 12);
        Assert.Equal(1.0 - CrossFadeCurves.CollapseParallaxScaleTo, CrossFadeCurves.CollapseParallaxScaleGain, 12);
        Assert.InRange(CrossFadeCurves.ExpandParallaxScaleGain, 0.0, 0.1);
        Assert.InRange(CrossFadeCurves.CollapseParallaxScaleGain, 0.0, 0.1);
        var (s0, _) = CrossFadeCurves.ContentParallax(0.0, expand: true);
        Assert.Equal(CrossFadeCurves.ExpandParallaxScaleFrom, s0, 12);
        var (c0, _) = CrossFadeCurves.ContentParallax(0.0, expand: false);
        Assert.Equal(CrossFadeCurves.CollapseParallaxScaleTo, c0, 12);
    }
    [Fact]
    public void SpringResponseConstants_iOSRhythm()
    {
        // 2.2.6: shape-spring base responses are now pure constants - expand 0.66s /
        // collapse 0.56s / compact 0.50s, slower and smoother (iOS liquid morph rhythm);
        // collapse faster than expand, compact resize fastest, clear hierarchy.
        var ex = CrossFadeCurves.ExpandShapeResponseSec;
        var co = CrossFadeCurves.CollapseShapeResponseSec;
        var cp = CrossFadeCurves.CompactShapeResponseSec;
        Assert.InRange(ex, 0.60, 0.72);
        Assert.InRange(co, 0.50, 0.62);
        Assert.InRange(cp, 0.44, 0.56);
        Assert.True(co < ex, "collapse should be faster than expand");
        Assert.True(cp < co, "compact resize should be fastest");
        // collapse ~= expand * 0.86 (reuse the established rhythm ratio)
        Assert.InRange(co / ex, 0.80, 0.90);
    }

    [Fact]
    public void FadeResponse_NewConstants_TrailsAndLeads()
    {
        // 2.2.6: with the new base response constants - expand content fade trails the
        // shape, collapse content fade leads the shape
        var exFade = CrossFadeCurves.FadeResponse(CrossFadeCurves.ExpandShapeResponseSec, expand: true);
        var coFade = CrossFadeCurves.FadeResponse(CrossFadeCurves.CollapseShapeResponseSec, expand: false);
        Assert.True(exFade > CrossFadeCurves.ExpandShapeResponseSec);
        Assert.True(coFade < CrossFadeCurves.CollapseShapeResponseSec);
        Assert.Equal(CrossFadeCurves.ExpandShapeResponseSec * CrossFadeCurves.ExpandContentFactor, exFade, 3);
        Assert.Equal(CrossFadeCurves.CollapseShapeResponseSec * CrossFadeCurves.CollapseContentFactor, coFade, 3);
    }

    [Fact]
    public void PillRowParallax_Expand_SlidesOutAndShrinks()
    {
        var (s0, y0) = CrossFadeCurves.PillRowParallax(0.0, expand: true);
        Assert.Equal(1.0, s0, 6);
        Assert.Equal(0.0, y0, 6);
        var (s1, y1) = CrossFadeCurves.PillRowParallax(1.0, expand: true);
        Assert.Equal(CrossFadeCurves.PillRowParallaxScaleGone, s1, 6);
        Assert.Equal(CrossFadeCurves.PillRowParallaxYTo, y1, 6);
        // monotonic: scale non-increasing and y non-increasing as the card grows
        var prevS = 1.0;
        var prevY = 0.0;
        for (var i = 1; i <= 20; i++)
        {
            var v = i / 20.0;
            var (s, y) = CrossFadeCurves.PillRowParallax(v, expand: true);
            Assert.True(s <= prevS + 1e-9, "expand scale must not grow");
            Assert.True(y <= prevY + 1e-9, "expand y must not go downward");
            prevS = s;
            prevY = y;
        }
    }

    [Fact]
    public void PillRowParallax_Collapse_ReturnsToRest()
    {
        var (s0, y0) = CrossFadeCurves.PillRowParallax(0.0, expand: false);
        Assert.Equal(1.0, s0, 6);
        Assert.Equal(0.0, y0, 6);
        var (sG, yG) = CrossFadeCurves.PillRowParallax(1.0, expand: false);
        Assert.Equal(CrossFadeCurves.PillRowParallaxScaleGone, sG, 6);
        Assert.Equal(CrossFadeCurves.PillRowParallaxYTo, yG, 6);
        // the pill reappears at CollapsePillReappearAt from the lifted pose
        var (sM, yM) = CrossFadeCurves.PillRowParallax(CrossFadeCurves.CollapsePillReappearAt, expand: false);
        Assert.Equal(CrossFadeCurves.PillRowParallaxScaleGone, sM, 6);
        Assert.Equal(CrossFadeCurves.PillRowParallaxYTo, yM, 6);
    }

    [Fact]
    public void PillRowParallax_ClampsOutOfRangeInputs()
    {
        foreach (var expand in new[] { true, false })
        {
            var (sa, ya) = CrossFadeCurves.PillRowParallax(-0.7, expand);
            var (sb, yb) = CrossFadeCurves.PillRowParallax(1.7, expand);
            var (sn, yn) = CrossFadeCurves.PillRowParallax(double.NaN, expand);
            var (si, yi) = CrossFadeCurves.PillRowParallax(double.PositiveInfinity, expand);
            foreach (var v in new[] { sa, sb, sn, si, ya, yb, yn, yi })
                Assert.True(double.IsFinite(v), "pill parallax must stay finite");
        }
        Assert.Equal(1.0, CrossFadeCurves.PillRowParallax(double.NaN, true).Scale, 6);
        Assert.Equal(1.0, CrossFadeCurves.PillRowParallax(double.NaN, false).Scale, 6);
        Assert.Equal(1.0, CrossFadeCurves.PillRowParallax(-0.7, true).Scale, 6);
        Assert.Equal(CrossFadeCurves.PillRowParallaxScaleGone, CrossFadeCurves.PillRowParallax(1.7, true).Scale, 6);
    }

    [Fact]
    public void PillRowParallax_Collapse_BitIdenticalToOldClampedFormula()
    {
        // 2.7.1: the constant-time exit (v >= CollapsePillReappearAt) plus the unclamped eased
        // path must reproduce the old clamped formula bit-for-bit across the whole reachable
        // domain of the collapse branch, including the boundary at CollapsePillReappearAt.
        const double c = CrossFadeCurves.CollapsePillReappearAt;
        for (var i = 0; i <= 30000; i++)
        {
            var v = -1.0 + 3.0 * i / 30000.0; // dense sweep over [-1, 2]
            var qOld = CrossFadeCurves.SmoothStep(1.0 - Math.Clamp(v / c, 0.0, 1.0));
            var oldPose = (CrossFadeCurves.PillRowParallaxScaleGone + CrossFadeCurves.PillRowParallaxScaleGain * qOld,
                           CrossFadeCurves.PillRowParallaxYTo * (1.0 - qOld));
            var (s, y) = CrossFadeCurves.PillRowParallax(v, expand: false);
            Assert.Equal(oldPose.Item1, s);
            Assert.Equal(oldPose.Item2, y);
        }
        // specials: NaN and +/-Inf all fall to v=0 (IsFinite guard) -> rest pose
        Assert.Equal(1.0, CrossFadeCurves.PillRowParallax(double.NaN, false).Scale);
        Assert.Equal(0.0, CrossFadeCurves.PillRowParallax(double.NaN, false).TranslateY);
        Assert.Equal(1.0, CrossFadeCurves.PillRowParallax(double.NegativeInfinity, false).Scale);
        Assert.Equal(1.0, CrossFadeCurves.PillRowParallax(double.PositiveInfinity, false).Scale);
        Assert.Equal(0.0, CrossFadeCurves.PillRowParallax(double.PositiveInfinity, false).TranslateY);
    }
    [Fact]
    public void ParallaxDedup_FirstWriteAlwaysTrue_SubPixelRepeatFalse()
    {
        // 2.2.17: first call always writes; exact or sub-threshold repeats are
        // skipped; any change above the thresholds writes again.
        var wrote = false;
        double lastScale = 1.0, lastY = 0.0;
        Assert.True(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 1.0, 0.0));
        Assert.False(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 1.0, 0.0));
        Assert.False(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 1.0002, 0.02));
        Assert.True(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 1.001, 0.0));
        Assert.False(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 1.001, 0.0));
        Assert.True(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 1.001, 0.1));
    }

    [Fact]
    public void ParallaxDedup_SkipKeepsLastWrittenPose()
    {
        // 2.2.17: a skipped repeat must not mutate the cached pose, so a later
        // large delta still compares against the last WRITTEN values.
        var wrote = false;
        double lastScale = 1.0, lastY = 0.0;
        Assert.True(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 0.965, -5.0));
        Assert.False(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 0.9651, -5.01));
        Assert.Equal(0.965, lastScale, 9);
        Assert.Equal(-5.0, lastY, 9);
        Assert.True(CrossFadeCurves.ShouldWriteParallax(ref wrote, ref lastScale, ref lastY, 1.0, 0.0));
    }

    [Fact]
    public void PillOpacity_ExpandExitAtBoundary_ZeroAtFullExit()
    {
        // 2.2.17: ExpandPillExitAt is the named 0.25 window; pill opacity is exactly
        // 0 at the boundary, > 0 and monotonically decreasing inside the window.
        Assert.Equal(0.25, CrossFadeCurves.ExpandPillExitAt, 12);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(CrossFadeCurves.ExpandPillExitAt, true), 12);
        Assert.True(CrossFadeCurves.PillOpacity(0.1, true) > 0.0);
        Assert.True(CrossFadeCurves.PillOpacity(0.0, true) > CrossFadeCurves.PillOpacity(0.1, true));
    }
    [Fact]
    public void PillOpacity_ExpandEarlyExit_MatchesReference()
    {
        // 2.2.18: the constant-time exit (v >= ExpandPillExitAt => 0) is exactly
        // the old eased result, and inside the window the eased path is unchanged.
        foreach (var v in new double[] { 0.3, 0.5, 0.75, 1.0 })
            Assert.Equal(0.0, CrossFadeCurves.PillOpacity(v, true), 12);
        for (var i = 0; i <= 24; i++)
        {
            var v = i / 100.0;
            var expected = 1.0 - CrossFadeCurves.SmoothStep(v / CrossFadeCurves.ExpandPillExitAt);
            Assert.Equal(expected, CrossFadeCurves.PillOpacity(v, true), 12);
        }
    }

    [Fact]
    public void PillOpacity_CollapseEarlyExit_MatchesReference()
    {
        // 2.2.18: collapse exit mirrors expand - at/after CollapsePillReappearAt the
        // pill is still hidden (0), inside the window the eased path is unchanged.
        foreach (var v in new double[] { 0.5, 0.75, 1.0 })
            Assert.Equal(0.0, CrossFadeCurves.PillOpacity(v, false), 12);
        for (var i = 0; i <= 50; i++)
        {
            var v = i / 100.0;
            var expected = CrossFadeCurves.SmoothStep((CrossFadeCurves.CollapsePillReappearAt - v) / CrossFadeCurves.CollapsePillReappearAt);
            Assert.Equal(expected, CrossFadeCurves.PillOpacity(v, false), 12);
        }
    }

    [Fact]
    public void ContentParallax_YStaysZero_BothDirections()
    {
        // 2.2.18: the Y component is reserved (currently 0) for both directions
        // across the whole fade - the dead multiply removal is behavior-neutral.
        for (var i = 0; i <= 100; i++)
        {
            var v = i / 100.0;
            Assert.Equal(0.0, CrossFadeCurves.ContentParallax(v, true).TranslateY, 12);
            Assert.Equal(0.0, CrossFadeCurves.ContentParallax(v, false).TranslateY, 12);
        }
    }


    [Fact]
    public void Parallax_TerminalExit_MatchesEasedPoseExactly()
    {
        // 2.3.4: constant-time terminal exits must equal the eased curves' final
        // values exactly - no discontinuity at the settle point.
        var (se, ye) = CrossFadeCurves.ContentParallax(1.0, expand: true);
        Assert.Equal(1.0, se, 12);
        Assert.Equal(0.0, ye, 12);
        var (sc, yc) = CrossFadeCurves.ContentParallax(0.0, expand: false);
        Assert.Equal(CrossFadeCurves.CollapseParallaxScaleTo, sc, 12);
        Assert.Equal(0.0, yc, 12);
        var (pe, pye) = CrossFadeCurves.PillRowParallax(1.0, expand: true);
        Assert.Equal(CrossFadeCurves.PillRowParallaxScaleGone, pe, 12);
        Assert.Equal(CrossFadeCurves.PillRowParallaxYTo, pye, 12);
        var (pc, pyc) = CrossFadeCurves.PillRowParallax(0.0, expand: false);
        Assert.Equal(1.0, pc, 12);
        Assert.Equal(0.0, pyc, 12);
    }

    [Fact]
    public void Parallax_JustBelowTerminal_IsContinuousWithTerminal()
    {
        // 2.3.4: the eased value one step below each terminal exit must be a hair
        // away from the constant-time result (continuous, no jump at the cutover).
        const double step = 1e-6;
        var (s1, _) = CrossFadeCurves.ContentParallax(1.0 - step, expand: true);
        Assert.InRange(1.0 - s1, 0.0, 1e-3);
        var (s2, _) = CrossFadeCurves.ContentParallax(0.0 + step, expand: false);
        Assert.InRange(s2 - CrossFadeCurves.CollapseParallaxScaleTo, 0.0, 1e-3);
        var (p1, _) = CrossFadeCurves.PillRowParallax(1.0 - step, expand: true);
        Assert.InRange(p1 - CrossFadeCurves.PillRowParallaxScaleGone, 0.0, 1e-3);
        var (p2, _) = CrossFadeCurves.PillRowParallax(0.0 + step, expand: false);
        Assert.InRange(1.0 - p2, 0.0, 1e-3);
    }

}
