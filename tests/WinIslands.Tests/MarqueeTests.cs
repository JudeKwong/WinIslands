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

    // ── 2.8.5：MarqueeMath 移除两个必然空操作的 Math.Max（ScrollRange/BuildSpec），
    //    与旧公式逐位一致（DoubleToInt64Bits 全比较）────────────────────────────
    [Fact]
    public void ScrollRange_BitwiseIdenticalToRemovedMaxForm()
    {
        // 参考 = 旧公式 Math.Max(1.0, Guarded(w) + GapPx)；新实现 = Guarded(w) + GapPx
        static double Guarded(double textWidth)
            => double.IsFinite(textWidth) && textWidth > 0 ? textWidth : 0.0;
        static double Reference(double textWidth)
            => Math.Max(1.0, Guarded(textWidth) + MarqueeMath.GapPx);

        var specials = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 27.0, 28.0, 28.000000000000004, 100.0,
            1e300, -1e300, 2000.0, 123456.789
        };
        foreach (var w in specials)
            Assert.Equal(BitConverter.DoubleToInt64Bits(Reference(w)),
                         BitConverter.DoubleToInt64Bits(MarqueeMath.ScrollRange(w)));

        var rnd = new Random(28501);
        for (var i = 0; i < 300_000; i++)
        {
            var w = BitConverter.Int64BitsToDouble(rnd.NextInt64());
            Assert.Equal(BitConverter.DoubleToInt64Bits(Reference(w)),
                         BitConverter.DoubleToInt64Bits(MarqueeMath.ScrollRange(w)));
        }
        // 稠密扫描：跨过 1.0 阈值邻域与 GapPx 邻域
        for (var i = 0; i <= 200_000; i++)
        {
            var w = -10.0 + 40.0 * i / 200_000.0;
            Assert.Equal(BitConverter.DoubleToInt64Bits(Reference(w)),
                         BitConverter.DoubleToInt64Bits(MarqueeMath.ScrollRange(w)));
        }
    }

    [Fact]
    public void BuildSpec_BitwiseIdenticalToRemovedMaxForm()
    {
        static double Guarded(double textWidth)
            => double.IsFinite(textWidth) && textWidth > 0 ? textWidth : 0.0;
        static double RefRange(double textWidth)
            => Math.Max(1.0, Guarded(textWidth) + MarqueeMath.GapPx);
        static double RefCruise(double textWidth)
            => Math.Max(0.0, RefRange(textWidth) * (1.0 - 2.0 * MarqueeMath.RampPortion));

        var specials = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 27.0, 28.0, 100.0, 120.0, 500.0, 600.0, 2000.0, 1e300, -1e300
        };
        foreach (var w in specials)
        {
            var spec = MarqueeMath.BuildSpec(w);
            Assert.Equal(BitConverter.DoubleToInt64Bits(RefCruise(w) / MarqueeMath.SpeedPxPerSec),
                         BitConverter.DoubleToInt64Bits(spec.CruiseSec));
            Assert.Equal(BitConverter.DoubleToInt64Bits((RefRange(w) - RefCruise(w)) / 2.0),
                         BitConverter.DoubleToInt64Bits(spec.RampDist));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(MarqueeMath.StartHoldSec + MarqueeMath.RampSec * 2.0
                                               + RefCruise(w) / MarqueeMath.SpeedPxPerSec),
                BitConverter.DoubleToInt64Bits(spec.TotalForwardSec));
        }

        var rnd = new Random(28502);
        for (var i = 0; i < 200_000; i++)
        {
            var w = BitConverter.Int64BitsToDouble(rnd.NextInt64());
            var spec = MarqueeMath.BuildSpec(w);
            Assert.Equal(BitConverter.DoubleToInt64Bits(RefCruise(w) / MarqueeMath.SpeedPxPerSec),
                         BitConverter.DoubleToInt64Bits(spec.CruiseSec));
        }
    }

}