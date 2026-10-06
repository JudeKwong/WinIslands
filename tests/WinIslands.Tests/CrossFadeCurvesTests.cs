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
}
