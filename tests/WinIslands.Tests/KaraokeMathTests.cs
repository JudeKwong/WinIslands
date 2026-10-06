using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.3.6：卡拉OK过渡字颜色通道混合的纯函数测试。
/// 线性插值端点精确、中间截断取整、越界钳制、非法进度按未点亮兜底（绝不出黑色字节）。
/// </summary>
public sealed class KaraokeMathTests
{
    [Fact]
    public void BlendChannel_AtZero_ReturnsFrom() => Assert.Equal(10, KaraokeMath.BlendChannel(10, 200, 0.0));

    [Fact]
    public void BlendChannel_AtOne_ReturnsTo() => Assert.Equal(200, KaraokeMath.BlendChannel(10, 200, 1.0));

    [Fact]
    public void BlendChannel_HalfWay_Truncates() => Assert.Equal(127, KaraokeMath.BlendChannel(0, 255, 0.5)); // 127.5 → 截断 127

    [Fact]
    public void BlendChannel_BelowZero_ClampsToFrom() => Assert.Equal(10, KaraokeMath.BlendChannel(10, 200, -0.5));

    [Fact]
    public void BlendChannel_AboveOne_ClampsToTo() => Assert.Equal(200, KaraokeMath.BlendChannel(10, 200, 1.5));

    [Fact]
    public void BlendChannel_NonFinite_FallsBackToFrom() => Assert.Equal(10, KaraokeMath.BlendChannel(10, 200, double.NaN));

    [Fact]
    public void BlendChannel_ReversedRange_StillLinear() => Assert.Equal(150, KaraokeMath.BlendChannel(200, 100, 0.5)); // 200-50=150

    [Fact]
    public void MonotonicFill_NeverRegresses() {
        double max = 0;
        var seq = new[] { 0.1, 0.3, 0.25, 0.4, 0.39, 0.5, 0.05 };
        var last = -1.0;
        foreach (var v in seq) {
            var r = KaraokeMath.MonotonicFill(v, max, out max);
            Assert.True(r >= last - 1e-12, $"fill regressed at {v}");
            Assert.Equal(max, r, 9);
            last = r;
        }
        Assert.Equal(0.5, max, 9);
    }

    [Fact]
    public void MonotonicFill_FreezeDuringStallCycle() {
        double max = 0;
        Assert.Equal(0.42, KaraokeMath.MonotonicFill(0.42, max, out max), 9);
        Assert.Equal(0.42, KaraokeMath.MonotonicFill(0.38, max, out max), 9); // pull-back freezes
        Assert.Equal(0.42, KaraokeMath.MonotonicFill(0.40, max, out max), 9); // resume but still behind
        Assert.Equal(0.55, KaraokeMath.MonotonicFill(0.55, max, out max), 9); // catch up and advance
    }

    [Fact]
    public void MonotonicFill_NonFinite_FreezeAtPeakAndNeverPolluteState() {
        double max = 0.3;
        Assert.Equal(0.3, KaraokeMath.MonotonicFill(double.NaN, max, out max), 9);
        Assert.Equal(0.3, KaraokeMath.MonotonicFill(double.PositiveInfinity, max, out max), 9);
        Assert.Equal(0.3, KaraokeMath.MonotonicFill(double.NegativeInfinity, max, out max), 9);
        Assert.Equal(0.3, max, 9); // state stays finite & clean
        Assert.Equal(0.4, KaraokeMath.MonotonicFill(0.4, max, out max), 9); // still advances afterwards
    }

    [Fact]
    public void SmoothStep_EndpointsAndMidpoint() {
        Assert.Equal(0.0, KaraokeMath.SmoothStep(0.0), 12);
        Assert.Equal(1.0, KaraokeMath.SmoothStep(1.0), 12);
        Assert.Equal(0.5, KaraokeMath.SmoothStep(0.5), 12); // 0.5^2*(3-1) = 0.5
    }

    [Fact]
    public void SmoothStep_QuarterPoint() {
        // 0.25^2 * (3 - 0.5) = 0.0625 * 2.5 = 0.15625
        Assert.Equal(0.15625, KaraokeMath.SmoothStep(0.25), 12);
    }

    [Fact]
    public void SmoothStep_ClampsOutOfRange() {
        Assert.Equal(0.0, KaraokeMath.SmoothStep(-0.5), 12);
        Assert.Equal(1.0, KaraokeMath.SmoothStep(1.5), 12);
        Assert.Equal(double.NaN, KaraokeMath.SmoothStep(double.NaN)); // NaN propagates (BlendChannel then falls back to unlit)
    }

    [Fact]
    public void SmoothStep_MonotonicAcrossSample() {
        var prev = 0.0;
        for (var i = 0; i <= 100; i++) {
            var v = KaraokeMath.SmoothStep(i / 100.0);
            Assert.True(v >= prev - 1e-12, $"not monotonic at {i}");
            prev = v;
        }
        Assert.Equal(1.0, prev, 12);
    }

    [Fact]
    public void ClampTickDelta_WithinRange_PassesThrough() => Assert.Equal(0.02, KaraokeMath.ClampTickDelta(0.02), 12);

    [Fact]
    public void ClampTickDelta_BelowFloor_ClampsToFloor() => Assert.Equal(0.001, KaraokeMath.ClampTickDelta(-5.0), 12);

    [Fact]
    public void ClampTickDelta_AboveCeiling_ClampsToCeiling() => Assert.Equal(0.05, KaraokeMath.ClampTickDelta(0.5), 12);

    [Fact]
    public void ClampTickDelta_BoundaryValues_Unaffected() {
        Assert.Equal(0.001, KaraokeMath.ClampTickDelta(0.001), 12);
        Assert.Equal(0.05, KaraokeMath.ClampTickDelta(0.05), 12);
    }

    [Fact]
    public void ClampTickDelta_NonFinite_MatchesOldMathSemantics() {
        Assert.True(double.IsNaN(KaraokeMath.ClampTickDelta(double.NaN)));
        Assert.Equal(0.05, KaraokeMath.ClampTickDelta(double.PositiveInfinity), 12);
        Assert.Equal(0.001, KaraokeMath.ClampTickDelta(double.NegativeInfinity), 12);
    }
}
