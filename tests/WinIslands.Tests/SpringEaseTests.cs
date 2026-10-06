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
    [Fact]
    public void Presanitized_BitIdenticalToEvaluate_ForSanitizedParams()
    {
        // 2.6.5: for any sanitized params (finite, d>=0, k>0, m>0) x any t (endpoints/out-of-range/NaN/+-Inf),
        // the pre-sanitized fast path is bit-identical to Evaluate with internal sanitize (DoubleToInt64Bits).
        foreach (var (d, k, m) in new[]
        {
            (12.0, 200.0, 1.0),
            (16.0, 150.0, 1.0),
            (0.0, 200.0, 1.0),
            (100.0, 1e-9, 1e-9),
            (1.0, 1.0, 1e-9),
            (120.0, 1200.0, 8.0),
        })
        foreach (var t in new[]
        {
            0.0, -5.0, 1.0, 3.0, double.NaN,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0001, 0.001, 0.01, 0.03, 0.05, 0.1, 0.2, 0.35, 0.5, 0.8, 0.999, 0.999999999
        })
        {
            var a = SpringEaseMath.Evaluate(t, d, k, m);
            var b = SpringEaseMath.EvaluatePresanitized(t, d, k, m);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
        }
    }

    [Fact]
    public void Evaluate_RefactoredDelegation_BitIdenticalToOldBody()
    {
        // 2.6.5: Evaluate refactored to "local sanitize + delegate to the pre-sanitized fast path";
        // for arbitrary raw params (NaN/Inf/negative/zero) it stays bit-identical to the old inline body.
        foreach (var (d, k, m) in new[]
        {
            (12.0, 200.0, 1.0),
            (16.0, 150.0, 1.0),
            (0.0, 200.0, 1.0),
            (double.NaN, double.NaN, double.NaN),
            (double.PositiveInfinity, 0.0, 0.0),
            (-1.0, -5.0, -2.0),
            (100.0, 1e-9, 1e-9),
            (12.0, double.NegativeInfinity, 1.0),
        })
        foreach (var t in new[] { 0.0, -0.5, 0.001, 0.05, 0.1, 0.2, 0.5, 0.9, 1.0, 2.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var fresh = SpringEaseMath.Evaluate(t, d, k, m);
            var old = OldEvaluateWithInternalSanitize(t, d, k, m);
            Assert.Equal(BitConverter.DoubleToInt64Bits(fresh), BitConverter.DoubleToInt64Bits(old));

            static double OldEvaluateWithInternalSanitize(double normalized, double damp, double stiffness, double mass)
            {
                var d0 = double.IsFinite(damp) && damp >= 0 ? damp : 12.0;
                var k0 = double.IsFinite(stiffness) && stiffness > 0 ? stiffness : 200.0;
                var m0 = double.IsFinite(mass) && mass > 0 ? mass : 1.0;
                var t0 = Math.Clamp(normalized, 0.0, 1.0);
                if (t0 <= 0.0) return 0.0;
                if (t0 >= 1.0) return 1.0;
                var tt = t0 * 1.7;
                var omega0 = Math.Sqrt(k0 / m0);
                var zeta = d0 / (2 * Math.Sqrt(k0 * m0));
                var z2 = 1 - zeta * zeta;
                var omegaD = omega0 * Math.Sqrt(z2 > 0 ? z2 : 0.0001);
                var decay = Math.Exp(-zeta * omega0 * tt);
                var (st, ct) = Math.SinCos(omegaD * tt);
                var v = 1 - decay * (ct + (zeta * omega0 / omegaD) * st);
                return v < 0 ? 0 : v;
            }
        }
    }

    [Fact]
    public void Presanitized_ExactEndpoints_AndOvershootShape()
    {
        // 2.6.5: pre-sanitized fast path lands exactly on endpoints and keeps the Q-bounce shape.
        Assert.Equal(0.0, SpringEaseMath.EvaluatePresanitized(0.0, 12, 200, 1));
        Assert.Equal(0.0, SpringEaseMath.EvaluatePresanitized(-1.0, 12, 200, 1));
        Assert.Equal(1.0, SpringEaseMath.EvaluatePresanitized(1.0, 12, 200, 1));
        Assert.Equal(1.0, SpringEaseMath.EvaluatePresanitized(5.0, 12, 200, 1));
        Assert.True(SpringEaseMath.EvaluatePresanitized(0.1, 12, 200, 1) >= 1.0, "overshoot at t=0.1");
        Assert.True(SpringEaseMath.EvaluatePresanitized(0.25, 12, 200, 1) < 1.02, "settles down");
    }

    [Fact]
    public void Prepared_BitIdenticalToPresanitized_ForSanitizedParams()
    {
        // 2.6.7: Prepare + EvaluatePrepared must be bit-identical to the
        // pre-sanitized path for every sanitized param combo, across a dense t
        // grid plus endpoints/out-of-range/NaN/+-Inf (DoubleToInt64Bits compared).
        foreach (var (d, k, m) in new[]
        {
            (12.0, 200.0, 1.0),
            (16.0, 150.0, 1.0),
            (0.0, 200.0, 1.0),
            (1.0, 1.0, 1e-9),
            (120.0, 1200.0, 8.0),
            (35.0, 90.0, 0.25),
        })
        {
            var c = SpringEaseMath.Prepare(d, k, m);
            for (var i = 0; i <= 5000; i++)
            {
                var t = i / 5000.0;
                var a = SpringEaseMath.EvaluatePresanitized(t, d, k, m);
                var b = SpringEaseMath.EvaluatePrepared(c, t);
                Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
            }
            foreach (var t in new[] { 0.0, -1.0, 1.0, 2.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var a = SpringEaseMath.EvaluatePresanitized(t, d, k, m);
                var b = SpringEaseMath.EvaluatePrepared(c, t);
                Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
            }
        }
    }

    [Fact]
    public void Prepared_ClampBranchChain_BitIdenticalToOldMathClamp()
    {
        // 2.6.9: EvaluatePrepared switched from Math.Clamp(normalized,0,1) to
        // the same two-comparison branch chain used by the karaoke SmoothStep,
        // saving one range-check call per active spring Ease per frame. Both
        // forms select the same branch for every t (NaN and +/-Inf fall through
        // to the original value), so outputs stay bit-identical (DoubleToInt64Bits
        // dense-sweep verified below).
        var combos = new[]
        {
            (12.0, 200.0, 1.0),
            (16.0, 150.0, 1.0),
            (0.0, 200.0, 1.0),
            (120.0, 1200.0, 8.0),
            (35.0, 90.0, 0.25),
        };
        var specials = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            -1e308, -0.0, 0.0, 1.0 - 1e-16, 1.0, 1.0 + 1e-16, 1e308 };
        foreach (var (d, k, m) in combos)
        {
            var c = SpringEaseMath.Prepare(d, k, m);
            foreach (var s in specials)
            {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(EvaluatePreparedOldMathClamp(c, s)),
                    BitConverter.DoubleToInt64Bits(SpringEaseMath.EvaluatePrepared(c, s)));
            }
            var rng = new Random(269);
            for (var i = 0; i < 30000; i++)
            {
                // sweep the whole domain including both out-of-range tails
                var r = rng.NextDouble() * 4.0 - 1.5;
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(EvaluatePreparedOldMathClamp(c, r)),
                    BitConverter.DoubleToInt64Bits(SpringEaseMath.EvaluatePrepared(c, r)));
            }
        }
    }

    private static double EvaluatePreparedOldMathClamp(in SpringEaseMath.SpringCoeffs c, double normalized)
    {
        var t = Math.Clamp(normalized, 0.0, 1.0);
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        var tt = t * 1.7;
        var decay = Math.Exp(c.NegZetaOmega0 * tt);
        var (st, ct) = Math.SinCos(c.OmegaD * tt);
        var v = 1 - decay * (ct + c.RatioZetaOmega0OverOmegaD * st);
        return v < 0 ? 0 : v;
    }

    [Fact]
    public void Prepare_CoefficientFolding_BitIdenticalToOldDerivation()
    {
        // 2.6.7: the folded coefficients must be bit-identical to the old inline
        // derivations: omegaD == omega0*Sqrt(z2>0?z2:0.0001), ratio == (zeta*omega0)/omegaD,
        // and negZm == (-zeta)*omega0 (IEEE keeps the sign bit independent, so
        // -(zeta*omega0) is bit-identical to (-zeta)*omega0).
        var combos = new[]
        {
            (12.0, 200.0, 1.0),
            (16.0, 150.0, 1.0),
            (0.0, 200.0, 1.0),
            (0.0, 1.0, 1e-9),
            (100.0, 1e-9, 1e-9),
            (42.0, 500.0, 3.0),
        };
        foreach (var (d, k, m) in combos)
        {
            var c = SpringEaseMath.Prepare(d, k, m);
            var omega0 = Math.Sqrt(k / m);
            var zeta = d / (2 * Math.Sqrt(k * m));
            var z2 = 1 - zeta * zeta;
            var omegaD = omega0 * Math.Sqrt(z2 > 0 ? z2 : 0.0001);
            var negZmOld = (-zeta) * omega0;
            var ratioOld = (zeta * omega0) / omegaD;
            Assert.Equal(BitConverter.DoubleToInt64Bits(omegaD), BitConverter.DoubleToInt64Bits(c.OmegaD));
            Assert.Equal(BitConverter.DoubleToInt64Bits(ratioOld), BitConverter.DoubleToInt64Bits(c.RatioZetaOmega0OverOmegaD));
            Assert.Equal(BitConverter.DoubleToInt64Bits(negZmOld), BitConverter.DoubleToInt64Bits(c.NegZetaOmega0));
            // the folded -(zeta*omega0) form is itself bit-identical to (-zeta)*omega0
            Assert.Equal(BitConverter.DoubleToInt64Bits(negZmOld), BitConverter.DoubleToInt64Bits(-(zeta * omega0)));
        }
    }

    [Fact]
    public void Prepared_ExactEndpoints_AndOvershootShape()
    {
        // 2.6.7: the prepared fast path lands exactly on endpoints and keeps the
        // Q-bounce shape, same as the pre-sanitized path it replaces.
        var c = SpringEaseMath.Prepare(12, 200, 1);
        Assert.Equal(0.0, SpringEaseMath.EvaluatePrepared(c, 0.0));
        Assert.Equal(0.0, SpringEaseMath.EvaluatePrepared(c, -1.0));
        Assert.Equal(1.0, SpringEaseMath.EvaluatePrepared(c, 1.0));
        Assert.Equal(1.0, SpringEaseMath.EvaluatePrepared(c, 5.0));
        Assert.True(SpringEaseMath.EvaluatePrepared(c, 0.1) >= 1.0, "overshoot at t=0.1");
        Assert.True(SpringEaseMath.EvaluatePrepared(c, 0.25) < 1.02, "settles down");
    }

    [Fact]
    public void SpringEase_InstanceBitIdenticalToMath_AcrossParamChanges()
    {
        // 2.6.7: the instance path (which caches Prepare on parameter change) must
        // be bit-identical to SpringEaseMath.Evaluate for the same sanitized params,
        // including after mutating Damping/Stiffness/Mass (re-prepare path).
        var s = new SpringEase();
        foreach (var (d, k, m) in new[]
        {
            (12.0, 200.0, 1.0),
            (16.0, 150.0, 1.0),
            (30.0, 260.0, 2.0),
            (0.0, 200.0, 1.0),
        })
        {
            s.Damping = d; s.Stiffness = k; s.Mass = m;
            for (var i = 0; i <= 2000; i++)
            {
                var t = i / 2000.0;
                var a = s.Ease(t);
                var b = SpringEaseMath.Evaluate(t, d, k, m);
                Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
            }
        }
    }
}
