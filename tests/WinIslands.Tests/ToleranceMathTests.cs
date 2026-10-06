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
}
