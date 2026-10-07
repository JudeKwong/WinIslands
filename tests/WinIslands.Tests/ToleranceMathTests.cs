using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// ToleranceMath 容差比较测试（2.7.7）：
/// NearWithin 必须与 Math.Abs(a - b) &lt; eps 在全部输入上布尔一致——
/// 单次差值 + 双边界分支链替换绝对值调用，动画热路径行为逐点不变。
/// </summary>
public sealed class ToleranceMathTests
{
    [Fact]
    public void NearWithin_Specials_BooleanIdenticalToAbsForm()
    {
        // 14 个特殊值全配对（NaN/±Inf/±0/极值/Epsilon/边界 0.5/-0.5），4 档容差
        var specials = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 2.0
        };
        foreach (var a in specials)
            foreach (var b in specials)
                foreach (var eps in new[] { 0.5, 1e-9, 1e-300, 1.0 })
                    Assert.Equal(Math.Abs(a - b) < eps, ToleranceMath.NearWithin(a, b, eps));
    }

    [Fact]
    public void NearWithin_DenseGrid_BooleanIdenticalToAbsForm()
    {
        // [-2, 2] 201x201 网格 x 4 档容差：覆盖边界内外、跨零点与有限域
        for (var i = 0; i <= 200; i++)
        {
            var a = -2.0 + 4.0 * i / 200.0;
            for (var j = 0; j <= 200; j++)
            {
                var b = -2.0 + 4.0 * j / 200.0;
                foreach (var eps in new[] { 0.5, 1e-9, 1e-300, 1.0 })
                    Assert.Equal(Math.Abs(a - b) < eps, ToleranceMath.NearWithin(a, b, eps));
            }
        }
    }

    [Fact]
    public void NearWithin_TieBoundaries_Exact()
    {
        // 恰好等于 ±eps 不算接近（与 |d| < eps 一致）；紧邻内侧算接近；NaN 永不接近
        Assert.False(ToleranceMath.NearWithin(0.5, 0.0, 0.5));
        Assert.False(ToleranceMath.NearWithin(-0.5, 0.0, 0.5));
        Assert.True(ToleranceMath.NearWithin(0.4999999999999999, 0.0, 0.5));
        Assert.True(ToleranceMath.NearWithin(-0.4999999999999999, 0.0, 0.5));
        Assert.False(ToleranceMath.NearWithin(double.NaN, 0.0, 0.5));
        Assert.False(ToleranceMath.NearWithin(double.PositiveInfinity, 0.0, 0.5));
        Assert.False(ToleranceMath.NearWithin(double.NegativeInfinity, 0.0, 0.5));
    }
    // ── 2.8.4：AbsAtLeast（|d| ≥ t 双边界分支链，布尔逐位等价 Math.Abs(d) >= t）────────────────
    [Fact]
    public void AbsAtLeast_SpecialsAndThresholds_BooleanIdenticalToAbsForm()
    {
        var spec = new double[]
        {
            double.NaN,
            BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001UL)), // 负 NaN / 大负载
            double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue,
            double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0,
            1e-300, -1e-300, 1e300, -1e300, 0.1, -0.1
        };
        var ts = new double[] { 0.5, 1e-9, 1e-300, 1.0, 0.0, -0.0, double.PositiveInfinity, double.NegativeInfinity, double.NaN };
        foreach (var d in spec)
            foreach (var t in ts)
                Assert.Equal(Math.Abs(d) >= t, ToleranceMath.AbsAtLeast(d, t));
    }

    [Fact]
    public void AbsAtLeast_RandomSweep_BooleanIdenticalToAbsForm()
    {
        var rnd = new Random(28401);
        for (var i = 0; i < 300_000; i++)
        {
            var d = BitConverter.Int64BitsToDouble(rnd.NextInt64());
            var t = Math.Pow(10, rnd.Next(-300, 2)) * (rnd.Next(2) == 0 ? 1.0 : -1.0);
            Assert.Equal(Math.Abs(d) >= t, ToleranceMath.AbsAtLeast(d, t));
        }
        var extremes = new double[] { double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.0 };
        for (var i = 0; i < 60_000; i++)
        {
            var d = extremes[rnd.Next(extremes.Length)];
            var t = extremes[rnd.Next(extremes.Length)];
            Assert.Equal(Math.Abs(d) >= t, ToleranceMath.AbsAtLeast(d, t));
        }
    }

    [Fact]
    public void AbsAtLeast_DenseNeighbourhood_BooleanIdenticalToAbsForm()
    {
        for (var i = 0; i <= 100_000; i++)
        {
            var d = -1.0 + 2.0 * i / 100_000.0;
            Assert.Equal(Math.Abs(d) >= 0.5, ToleranceMath.AbsAtLeast(d, 0.5));
        }
    }

    [Fact]
    public void AbsAtLeast_EndpointSemantics()
    {
        // 恰在 ±t 上算越闸（>= 语义）
        Assert.True(ToleranceMath.AbsAtLeast(0.5, 0.5));
        Assert.True(ToleranceMath.AbsAtLeast(-0.5, 0.5));
        // 紧邻内侧不越闸
        Assert.False(ToleranceMath.AbsAtLeast(0.4999999999999999, 0.5));
        Assert.False(ToleranceMath.AbsAtLeast(-0.4999999999999999, 0.5));
        // ±0 在正阈值下不越闸
        Assert.False(ToleranceMath.AbsAtLeast(0.0, 1e-9));
        Assert.False(ToleranceMath.AbsAtLeast(-0.0, 1e-9));
        // NaN 永不越闸（含 t=NaN）
        Assert.False(ToleranceMath.AbsAtLeast(double.NaN, 0.5));
        Assert.False(ToleranceMath.AbsAtLeast(double.NaN, double.NaN));
        // ±Inf 恒越闸；t=+Inf 时仅 ±Inf 越闸
        Assert.True(ToleranceMath.AbsAtLeast(double.PositiveInfinity, 1.0));
        Assert.True(ToleranceMath.AbsAtLeast(double.NegativeInfinity, 1.0));
        Assert.False(ToleranceMath.AbsAtLeast(5.0, double.PositiveInfinity));
        Assert.True(ToleranceMath.AbsAtLeast(double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(ToleranceMath.AbsAtLeast(double.NegativeInfinity, double.PositiveInfinity));
    }
}
