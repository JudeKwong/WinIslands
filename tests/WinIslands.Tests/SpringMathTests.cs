using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.8.7：SpringMath.ClampZeroToQuarter 分支链——逐位等价 Math.Max(0.0, Math.Min(0.25, x))。
/// </summary>
public sealed class SpringMathTests
{
    [Fact]
    public void ClampZeroToQuarter_Specials_BitwiseMatchesDoubleClamp()
    {
        var spec = new double[]
        {
            double.NaN,
            BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001UL)), // 负 NaN / 大负载
            double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 0.25, -0.25, 0.03, 0.249, 0.251, 1e300, -1e300
        };
        foreach (var x in spec)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(0.0, Math.Min(0.25, x))),
                BitConverter.DoubleToInt64Bits(SpringMath.ClampZeroToQuarter(x)));

        // 稠密扫描：跨 0 与 0.25 阈值的 20 万点
        for (var i = 0; i <= 200_000; i++)
        {
            var x = -0.5 + (1.0 * i) / 200_000.0; // -0.5..0.5
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(0.0, Math.Min(0.25, x))),
                BitConverter.DoubleToInt64Bits(SpringMath.ClampZeroToQuarter(x)));
        }
    }

    [Fact]
    public void ClampZeroToQuarter_RandomSweep_BitwiseMatchesDoubleClamp()
    {
        var rnd = new Random(28701);
        for (var i = 0; i < 300_000; i++)
        {
            var x = BitConverter.Int64BitsToDouble(rnd.NextInt64());
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(0.0, Math.Min(0.25, x))),
                BitConverter.DoubleToInt64Bits(SpringMath.ClampZeroToQuarter(x)));
        }
    }

    [Fact]
    public void ClampZeroToQuarter_Behavioural()
    {
        Assert.Equal(0.0, SpringMath.ClampZeroToQuarter(-1.0), 12);
        Assert.Equal(0.0, SpringMath.ClampZeroToQuarter(0.0), 12);
        Assert.Equal(0.0, SpringMath.ClampZeroToQuarter(-0.0), 12);
        Assert.Equal(0.06, SpringMath.ClampZeroToQuarter(0.06), 12);
        Assert.Equal(0.25, SpringMath.ClampZeroToQuarter(0.35), 12);
        Assert.Equal(0.25, SpringMath.ClampZeroToQuarter(double.PositiveInfinity), 12);
        Assert.Equal(0.0, SpringMath.ClampZeroToQuarter(double.NegativeInfinity), 12);
        Assert.True(double.IsNaN(SpringMath.ClampZeroToQuarter(double.NaN)));
    }
}
