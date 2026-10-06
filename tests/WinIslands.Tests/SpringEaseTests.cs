using System;
using Xunit;
using WinIslands.UI;

namespace WinIslands.Tests;

public class SpringEaseTests
{
    [Fact]
    public void Ease_EndpointsExact()
    {
        var s = new SpringEase();
        Assert.Equal(0.0, s.Ease(0.0), 9);
        Assert.Equal(0.0, s.Ease(-5.0), 9);
        Assert.Equal(1.0, s.Ease(1.0), 9);
        Assert.Equal(1.0, s.Ease(3.0), 9);
    }

    [Fact]
    public void SoftEase_EndpointsExact()
    {
        var s = new SoftSpringEase();
        Assert.Equal(0.0, s.Ease(0.0), 9);
        Assert.Equal(1.0, s.Ease(1.0), 9);
        Assert.Equal(1.0, s.Ease(2.0), 9);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(0.0, 200.0, 0.0)]
    [InlineData(double.NaN, 200.0, 1.0)]
    [InlineData(12.0, 0.0, 1.0)]
    [InlineData(12.0, -5.0, 1.0)]
    [InlineData(12.0, 200.0, 0.0)]
    [InlineData(-3.0, 200.0, 1.0)]
    public void Sanitize_InvalidParamsFallBack(double d, double k, double m)
    {
        SpringEaseMath.Sanitize(d, k, m, out var od, out var ok, out var om);
        Assert.True(double.IsFinite(od) && od >= 0, $"d={od}");
        Assert.True(double.IsFinite(ok) && ok > 0, $"k={ok}");
        Assert.True(double.IsFinite(om) && om > 0, $"m={om}");
    }

    [Fact]
    public void Evaluate_NeverNaN_ForAnyInput()
    {
        foreach (var d in new[] { 12.0, 0.0, 100.0, -1.0, double.NaN, double.PositiveInfinity })
        foreach (var k in new[] { 200.0, 0.0, 1e-9, -1.0, double.NaN })
        foreach (var m in new[] { 1.0, 0.0, 1e-9, -2.0, double.NaN })
        foreach (var t in new[] { 0.0, 0.25, 0.5, 0.9, 1.0, 1.5 })
        {
            var v = SpringEaseMath.Evaluate(t, d, k, m);
            Assert.True(double.IsFinite(v), $"v={v} d={d} k={k} m={m} t={t}");
            Assert.True(v >= -1e-12, $"v={v}");
        }
    }

    [Fact]
    public void Evaluate_MatchesCurveShape_ForDefaults()
    {
        // 曲线形状与旧公式一致：起始段单调上升、中途轻微过冲（Q 弹）、终点精确归 1
        var v001 = SpringEaseMath.Evaluate(0.01, 12, 200, 1);
        var v003 = SpringEaseMath.Evaluate(0.03, 12, 200, 1);
        var v005 = SpringEaseMath.Evaluate(0.05, 12, 200, 1);
        Assert.True(v001 < v003 && v003 < v005, "early monotonic rise");
        Assert.InRange(v005, 0.3, 0.6);
        Assert.InRange(SpringEaseMath.Evaluate(0.1, 12, 200, 1), 0.9, 1.15);
        Assert.InRange(SpringEaseMath.Evaluate(0.2, 12, 200, 1), 1.0, 1.15); // 过冲
        Assert.InRange(SpringEaseMath.Evaluate(0.25, 12, 200, 1), 0.9, 1.02);
        Assert.Equal(1.0, SpringEaseMath.Evaluate(1.0, 12, 200, 1), 9);
    }
}
