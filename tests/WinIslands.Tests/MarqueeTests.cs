using System;
using Xunit;
using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 跑马灯时间线数学测试（2.2.20）：
/// iOS 舒缓滚动 —— 每循环停顿 + 缓入/缓出 + 匀速巡航 + 无缝循环。
/// </summary>
public class MarqueeTests
{
    [Fact]
    public void ScrollRange_InvalidInputs_FinitePositive()
    {
        foreach (var w in new double[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -5.0 })
        {
            var r = MarqueeMath.ScrollRange(w);
            Assert.True(double.IsFinite(r) && r > 0, string.Format("range for {0}", w));
        }
        Assert.True(MarqueeMath.ScrollRange(200) >= 200 + MarqueeMath.GapPx - 1e-9, "normal width keeps the exit gap");
    }

    [Fact]
    public void BuildSpec_SmallOverflow_NoNegativeSegments()
    {
        var spec = MarqueeMath.BuildSpec(120); // 刚好超宽
        Assert.True(spec.RampDist > 0, "ramp must be positive");
        Assert.True(spec.CruiseSec >= 0, "cruise must not be negative");
        Assert.True(spec.CruiseSec + spec.RampSec * 2.0 <= spec.TotalForwardSec - MarqueeMath.StartHoldSec + 1e-9, "segments fit inside total");
        Assert.True(spec.RampDist <= MarqueeMath.ScrollRange(120) + 1e-12, "ramp fits inside range");
    }

    [Fact]
    public void BuildSpec_LargeText_RampAndCruiseBalanced()
    {
        var spec = MarqueeMath.BuildSpec(2000);
        var range = MarqueeMath.ScrollRange(2000);
        Assert.True(spec.CruiseSec > 0, "long text must cruise");
        Assert.InRange(spec.RampDist, range * 0.09, range * 0.11); // 每侧 ~10%
        Assert.True(spec.TotalForwardSec > spec.StartHoldSec + spec.RampSec * 2.0, "total covers holds + ramps");
    }

    [Fact]
    public void BuildSpec_SegmentsSumToTotal()
    {
        var spec = MarqueeMath.BuildSpec(600);
        var range = MarqueeMath.ScrollRange(600);
        var segmentSum = spec.RampDist * 2.0 + spec.CruiseSec * MarqueeMath.SpeedPxPerSec;
        Assert.Equal(range, segmentSum, 9);
        Assert.Equal(spec.TotalForwardSec, spec.StartHoldSec + spec.RampSec * 2.0 + spec.CruiseSec, 12);
    }

    [Fact]
    public void BuildSpec_ConstantSpeed_ScalesLinearly()
    {
        // 巡航速度恒定：宽度翻倍 → 巡航时长近似翻倍（增速仅来自巡航段）
        var a = MarqueeMath.BuildSpec(500);
        var b = MarqueeMath.BuildSpec(1000);
        var ra = MarqueeMath.ScrollRange(500);
        var rb = MarqueeMath.ScrollRange(1000);
        var cruA = (ra - a.RampDist * 2.0) / MarqueeMath.SpeedPxPerSec;
        var cruB = (rb - b.RampDist * 2.0) / MarqueeMath.SpeedPxPerSec;
        Assert.InRange(cruB / cruA, 1.8, 2.2);
    }
}
