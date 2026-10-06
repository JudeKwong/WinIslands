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

    [Fact]
    public void Evaluate_SinCosMerge_MatchesSeparatedTrigWithin15Digits()
    {
        // v2.5.3：欠阻尼分支改用单个 Math.SinCos（一个 FSINCOS 对），
        // 结果与旧的分开 Cos/Sin 公式在所有参数/采样点上 1e-14 内一致（约 15 位有效数字）。
        foreach (var (d, k, m) in new[]
        {
            (12.0, 200.0, 1.0),
            (16.0, 150.0, 1.0),
            (8.0, 240.0, 1.0),
            (0.0, 200.0, 1.0),
            (14.0, 120.0, 2.0),
        })
        foreach (var tq in new[] { 0.0, 0.002, 0.01, 0.03, 0.05, 0.1, 0.2, 0.35, 0.5, 0.8, 0.999, 1.0 })
        {
            var old = OldEvaluate(tq, d, k, m);
            var fresh = SpringEaseMath.Evaluate(tq, d, k, m);
            Assert.True(Math.Abs(old - fresh) <= 1e-14,
                $"d={d} k={k} m={m} t={tq} old={old:R} fresh={fresh:R}");
        }

        static double OldEvaluate(double normalized, double damp, double stiffness, double mass)
        {
            var d0 = double.IsFinite(damp) && damp >= 0 ? damp : 12.0;
            var k0 = double.IsFinite(stiffness) && stiffness > 0 ? stiffness : 200.0;
            var m0 = double.IsFinite(mass) && mass > 0 ? mass : 1.0;
            var t = Math.Clamp(normalized, 0.0, 1.0);
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            var tt = t * 1.7;
            var omega0 = Math.Sqrt(k0 / m0);
            var zeta = d0 / (2 * Math.Sqrt(k0 * m0));
            var z2 = 1 - zeta * zeta;
            var omegaD = omega0 * Math.Sqrt(z2 > 0 ? z2 : 0.0001);
            var decay = Math.Exp(-zeta * omega0 * tt);
            var v = 1 - decay * (Math.Cos(omegaD * tt) + (zeta * omega0 / omegaD) * Math.Sin(omegaD * tt));
            return v < 0 ? 0 : v;
        }
    }

}
