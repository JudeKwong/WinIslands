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
    public void Collapse_PillAppearsOnlyAtFinalFifth_NoEarlyPopIn()
    {
        // 收起：内容仍可见时胶囊行保持隐藏；内容淡出到最后 1/5 才浮现，配合卡片收拢
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(1.0, expand: false), 6);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(0.5, expand: false), 6);
        Assert.Equal(0.0, CrossFadeCurves.PillOpacity(0.2, expand: false), 6);
        Assert.InRange(CrossFadeCurves.PillOpacity(0.1, expand: false), 0.45, 0.55);
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
}