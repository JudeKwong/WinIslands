using System.Windows.Media;
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
    public void SmoothStep_ClampBranchChain_BitIdenticalToMathClamp()
    {
        // 2.6.8: SmoothStep now clamps [0,1] with a two-comparison branch chain
        // instead of Math.Clamp(t,0,1), saving one range-check call per transition
        // character per frame. For every t the two forms select the same branch
        // value: NaN and +/-Inf fall through to the original value in both, so the
        // outputs are bit-identical (DoubleToInt64Bits dense-sweep verified below).
        var specials = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            -1e308, -0.0, 0.0, 1.0 - 1e-16, 1.0, 1.0 + 1e-16, 1e308 };
        foreach (var s in specials)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(SmoothStepOldClamp(s)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.SmoothStep(s)));
        }
        var rng = new Random(268);
        for (var i = 0; i < 30000; i++)
        {
            // sweep the whole domain including both out-of-range tails
            var r = rng.NextDouble() * 4.0 - 1.5;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(SmoothStepOldClamp(r)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.SmoothStep(r)));
        }
    }

    private static double SmoothStepOldClamp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
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

    // 2.5.8: BlendColor single-pass whole-color blend (per-character karaoke hot path)
    // must stay byte-identical to the per-channel BlendChannel composition.
    private static Color Kc(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    [Fact]
    public void BlendColor_Endpoints_Exact()
    {
        var from = Kc(10, 20, 30, 40);
        var to = Kc(250, 240, 230, 220);
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, 0.0));
        Assert.Equal(to, KaraokeMath.BlendColor(from, to, 1.0));
    }

    [Fact]
    public void BlendColor_HalfWay_TruncatesPerChannel()
    {
        var from = Kc(10, 20, 30, 40);
        var to = Kc(250, 240, 230, 220);
        Assert.Equal(Kc(130, 130, 130, 130), KaraokeMath.BlendColor(from, to, 0.5));
    }

    [Fact]
    public void BlendColor_OutOfRange_ClampsLikePerChannel()
    {
        var from = Kc(10, 20, 30, 40);
        var to = Kc(250, 240, 230, 220);
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, -0.5));
        Assert.Equal(to, KaraokeMath.BlendColor(from, to, 1.5));
    }

    [Fact]
    public void BlendColor_NonFinite_FallsBackToFrom()
    {
        var from = Kc(10, 20, 30, 40);
        var to = Kc(250, 240, 230, 220);
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, double.NaN));
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, double.PositiveInfinity));
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, double.NegativeInfinity));
    }

    [Fact]
    public void BlendColor_MatchesPerChannel_AcrossSweep()
    {
        var pairs = new[]
        {
            (Kc(0, 0, 0, 0), Kc(255, 255, 255, 255)),
            (Kc(10, 20, 30, 40), Kc(250, 240, 230, 220)),
            (Kc(200, 100, 50, 25), Kc(5, 155, 205, 250)),
        };
        var fracs = new[] { 0.0, 0.001, 0.05, 0.25, 0.37, 0.5, 0.63, 0.73, 0.99, 0.999, 1.0, -1.0, 2.0 };
        foreach (var (from, to) in pairs)
        {
            foreach (var f in fracs)
            {
                var expected = Color.FromArgb(
                    KaraokeMath.BlendChannel(from.A, to.A, f),
                    KaraokeMath.BlendChannel(from.R, to.R, f),
                    KaraokeMath.BlendChannel(from.G, to.G, f),
                    KaraokeMath.BlendChannel(from.B, to.B, f));
                Assert.Equal(expected, KaraokeMath.BlendColor(from, to, f));
            }
        }
    }

    [Fact]
    public void BlendColor_DenseFractionSweep_MatchesWholeLineLerpFormula()
    {
        // 2.6.0: the whole-line (non-TTML) karaoke path used to carry its own
        // 4-channel Lerp; it now shares KaraokeMath.BlendColor. This dense sweep
        // (257 fractions per pair) locks byte-level equivalence with the old
        // truncating per-channel formula so the dedup cannot drift visually.
        var pairs = new[]
        {
            (Kc(0, 0, 0, 0), Kc(255, 255, 255, 255)),
            (Kc(10, 20, 30, 40), Kc(250, 240, 230, 220)),
            (Kc(200, 100, 50, 25), Kc(5, 155, 205, 250)),
        };
        foreach (var (from, to) in pairs)
        {
            for (var k = 0; k <= 256; k++)
            {
                var f = k / 256.0;
                var expected = Color.FromArgb(
                    (byte)(from.A + (to.A - from.A) * f),
                    (byte)(from.R + (to.R - from.R) * f),
                    (byte)(from.G + (to.G - from.G) * f),
                    (byte)(from.B + (to.B - from.B) * f));
                Assert.Equal(expected, KaraokeMath.BlendColor(from, to, f));
            }
        }
    }

    [Fact]
    public void BlendColor_NonFinite_WholeLinePathFallsBackToBase()
    {
        // 2.6.0: the old whole-line Lerp had no finiteness guard - a NaN blink
        // would cast to byte 0 (black flash). The shared BlendColor guards all
        // four channels at once: NaN/Infinity fall back to the base color.
        var from = Kc(30, 60, 120, 240);
        var to = Kc(250, 240, 230, 220);
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, double.NaN));
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, double.PositiveInfinity));
        Assert.Equal(from, KaraokeMath.BlendColor(from, to, double.NegativeInfinity));
    }

    [Fact]
    public void BlendColor_DenseSweep_StaysWithinChannelRange()
    {
        // 2.6.0: interpolation invariant - every channel of the blended color
        // stays within [min(from,to), max(from,to)] for every dense fraction,
        // for both forward and reversed color pairs (no overshoot bytes).
        var pairs = new[]
        {
            (Kc(0, 0, 0, 0), Kc(255, 255, 255, 255)),
            (Kc(250, 240, 230, 220), Kc(10, 20, 30, 40)),
            (Kc(77, 88, 99, 111), Kc(200, 10, 130, 99)),
        };
        foreach (var (from, to) in pairs)
        {
            for (var k = 0; k <= 256; k++)
            {
                var c = KaraokeMath.BlendColor(from, to, k / 256.0);
                Assert.InRange(c.A, Math.Min(from.A, to.A), Math.Max(from.A, to.A));
                Assert.InRange(c.R, Math.Min(from.R, to.R), Math.Max(from.R, to.R));
                Assert.InRange(c.G, Math.Min(from.G, to.G), Math.Max(from.G, to.G));
                Assert.InRange(c.B, Math.Min(from.B, to.B), Math.Max(from.B, to.B));
            }
        }
    }

    [Fact]
    public void WholeLineSplit_DenseSweep_BitIdenticalToOldFormula()
    {
        // 2.6.4: the new helper computes fraction*length once and reuses it for
        // both the Floor input and the blend remainder; the old inline code
        // multiplied twice. Every output must be bit-identical (same expression).
        for (var len = 1; len <= 24; len++)
        {
            for (var i = 0; i <= 2000; i++)
            {
                var f = i / 2000.0;
                var oldLit = Math.Min((int)Math.Floor(f * len), len);
                var oldBlend = f * len - oldLit;
                if (oldLit >= len) oldBlend = 1;
                var (lit, blend) = KaraokeMath.WholeLineSplit(f, len);
                Assert.Equal(oldLit, lit);
                Assert.Equal(BitConverter.DoubleToInt64Bits(oldBlend), BitConverter.DoubleToInt64Bits(blend));
            }
        }
    }

    [Fact]
    public void WholeLineSplit_Endpoints_Exact()
    {
        var (lit0, blend0) = KaraokeMath.WholeLineSplit(0.0, 9);
        Assert.Equal(0, lit0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.0), BitConverter.DoubleToInt64Bits(blend0));
        var (lit1, blend1) = KaraokeMath.WholeLineSplit(1.0, 9);
        Assert.Equal(9, lit1);
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0), BitConverter.DoubleToInt64Bits(blend1));
    }

    [Fact]
    public void WholeLineSplit_NonFiniteFraction_MatchesOldFormula()
    {
        // NaN / +-Inf are never produced by the clamped _currentFraction in the
        // live path; the helper must still stay byte-identical to the old inline
        // expression for the same inputs on the same runtime.
        foreach (var f in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            const int len = 7;
            var oldLit = Math.Min((int)Math.Floor(f * len), len);
            var oldBlend = f * len - oldLit;
            if (oldLit >= len) oldBlend = 1;
            var (lit, blend) = KaraokeMath.WholeLineSplit(f, len);
            Assert.Equal(oldLit, lit);
            Assert.Equal(BitConverter.DoubleToInt64Bits(oldBlend), BitConverter.DoubleToInt64Bits(blend));
        }
    }

    [Fact]
    public void WholeLineSplit_ZeroOrNegativeLength_MatchesOldFormula()
    {
        // The live call site never passes length <= 0 (empty text returns early),
        // but the helper must remain a faithful projection of the old expression.
        foreach (var len in new[] { 0, -1, -5 })
        {
            foreach (var f in new[] { 0.0, 0.3, 1.0, double.NaN })
            {
                var oldLit = Math.Min((int)Math.Floor(f * len), len);
                var oldBlend = f * len - oldLit;
                if (oldLit >= len) oldBlend = 1;
                var (lit, blend) = KaraokeMath.WholeLineSplit(f, len);
                Assert.Equal(oldLit, lit);
                Assert.Equal(BitConverter.DoubleToInt64Bits(oldBlend), BitConverter.DoubleToInt64Bits(blend));
            }
        }
    }

    [Fact]
    public void WholeLineBlend_RawVsClamped_ByteIdenticalForAllFiniteFractions()
    {
        // 2.6.6: the render hot path used to wrap the whole-line blend remainder
        // in Math.Clamp(blend, 0, 1) before BlendColor; WholeLineSplit already
        // guarantees blend in [0,1) whenever lit < length (scaled - floor(scaled)
        // for a finite scaled), so the clamp was pure range-check overhead. This
        // dense sweep (lengths 1-64 x 4097 finite fractions) proves the raw and
        // clamped paths emit byte-identical ARGB for every reachable input.
        var bs = Kc(30, 60, 120, 240);
        var hl = Kc(250, 240, 230, 220);
        for (var len = 1; len <= 64; len++)
        {
            for (var i = 0; i <= 4096; i++)
            {
                var f = i / 4096.0;
                var (lit, blend) = KaraokeMath.WholeLineSplit(f, len);
                if (lit >= len) continue; // 完全点亮时渲染层直接取底色，不调用 BlendColor
                var clamped = KaraokeMath.BlendColor(bs, hl, Math.Clamp(blend, 0, 1));
                var raw = KaraokeMath.BlendColor(bs, hl, blend);
                Assert.True(
                    clamped.A == raw.A && clamped.R == raw.R && clamped.G == raw.G && clamped.B == raw.B,
                    $"byte mismatch length={len} fraction={f} blend={blend}");
            }
        }
    }

    [Fact]
    public void WholeLineSplit_BlendInsideUnitInterval_WhenLitCountBelowLength()
    {
        // 2.6.6: the invariant that makes the render-side clamp removable - for
        // every finite fraction, whenever lit < length the blend remainder
        // (scaled - floor(scaled)) lies in [0,1), so no extra range clamp is ever
        // needed before BlendColor on the karaoke hot path.
        for (var len = 1; len <= 64; len++)
        {
            for (var i = 0; i <= 8192; i++)
            {
                var f = i / 8192.0;
                var (lit, blend) = KaraokeMath.WholeLineSplit(f, len);
                if (lit < len)
                {
                    Assert.InRange(blend, 0.0, 1.0);
                }
            }
        }
    }

    [Fact]
    public void WholeLineBlend_NonFiniteFraction_RawPathFallsBackToBaseColor()
    {
        // 2.6.6: NaN/+/-Inf fractions never reach the live path (_currentFraction
        // is clamped to [0,1] upstream), but the raw path stays safe: unlike the
        // old clamp (which would map +Inf to 1.0 and flash the full highlight),
        // BlendColor's IsFinite guard falls back to the base color. The NaN case
        // is bit-identical to the old clamped path (Math.Clamp(NaN,0,1) == NaN).
        var bs = Kc(30, 60, 120, 240);
        var hl = Kc(250, 240, 230, 220);
        var (lit, blend) = KaraokeMath.WholeLineSplit(double.NaN, 7);
        Assert.True(lit < 7);
        Assert.Equal(bs, KaraokeMath.BlendColor(bs, hl, Math.Clamp(blend, 0, 1)));
        Assert.Equal(bs, KaraokeMath.BlendColor(bs, hl, blend));
        Assert.Equal(bs, KaraokeMath.BlendColor(bs, hl, double.PositiveInfinity));
        Assert.Equal(bs, KaraokeMath.BlendColor(bs, hl, double.NegativeInfinity));
    }
}
