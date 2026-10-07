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
    [Fact]
    public void AtLeastZero_MatchesMathMax_BitForBit()
    {
        // 2.8.0: 逐字时间轴每帧的 Math.Max(x, 0.0) 改为单比较分支链 x <= 0 ? 0 : x；
        // 必须在全部 double 输入上与运行时逐位一致——NaN（含自定义负载）原样透传、
        // -0.0 映射为 +0.0、±Inf 钳到端点、正数原样返回。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var specials = new[]
        {
            double.NaN, customNan, -customNan,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5,
            double.Epsilon, -double.Epsilon,
            double.MaxValue, double.MinValue, double.MaxValue / 2, -double.MaxValue / 2,
            1e-300, -1e-300, 1e300, -1e300,
        };
        foreach (var x in specials)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(x, 0.0)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.AtLeastZero(x)));
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(0.0, x)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.AtLeastZero(x)));
        }
        var rng = new Random(0x2E80);
        for (var i = 0; i < 30001; i++)
        {
            var x = (i % 3) switch
            {
                0 => NextSigned(rng, 1e6),
                1 => rng.NextDouble() * 2.0 - 1.0,
                _ => rng.NextDouble() < 0.25 ? double.NaN : NextSigned(rng, 1e3),
            };
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(x, 0.0)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.AtLeastZero(x)));
        }
    }

    [Fact]
    public void MaxDurationFloor_MatchesMathMax_BitForBit()
    {
        // 2.8.0: BuildWordTimeline 的时长下限 Math.Max(d, 0.001) 改为
        // d >= 0.001 || d != d ? d : 0.001；NaN 透传、与 0.001 的精确 tie 返回 d（即 0.001）、
        // 低于下限的值落在下限、+Inf 保持，与运行时逐位一致。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var specials = new[]
        {
            double.NaN, customNan, -customNan,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 0.001, -0.001, 0.002, 0.000999999999999, 0.001000000000001,
            double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue,
            1e-300, -1e-300, 1e300, -1e300,
        };
        foreach (var d in specials)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(d, 0.001)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MaxDurationFloor(d)));
        }
        // 在 0.001 附近的密集扫描：覆盖 tie 两侧及跨数量级邻居
        for (var i = -2048; i <= 2048; i++)
        {
            var d = 0.001 + i * 1e-9;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(d, 0.001)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MaxDurationFloor(d)));
            var alt = 0.001 + i * 1e-15;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(alt, 0.001)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MaxDurationFloor(alt)));
            var exp = 0.001 * Math.Pow(2.0, i / 256.0);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(exp, 0.001)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MaxDurationFloor(exp)));
        }
        var rng = new Random(0x2E81);
        for (var i = 0; i < 30001; i++)
        {
            var d = (i % 3) switch
            {
                0 => NextSigned(rng, 1e4),
                1 => rng.NextDouble() * 0.02 - 0.005,
                _ => rng.NextDouble() < 0.25 ? double.NaN : rng.NextDouble() * 1e-3,
            };
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(d, 0.001)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MaxDurationFloor(d)));
        }
    }

    [Fact]
    public void MinLeadCap_MatchesMathMin_BitForBit()
    {
        // 2.8.0: BuildWordTimeline 的字间 lead 上限 Math.Min(0.045, y) 改为
        // y >= 0.045 ? 0.045 : y；NaN 透传、与 0.045 的精确 tie 返回 0.045 字面量（与
        // Math.Min 的 val1-on-equality 一致）、低于上限的值原样返回，逐位一致。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var specials = new[]
        {
            double.NaN, customNan, -customNan,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 0.045, -0.045, 0.044999999999999, 0.045000000000001, 0.09, 1.0,
            double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue,
            1e-300, -1e-300, 1e300, -1e300,
        };
        foreach (var y in specials)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Min(0.045, y)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MinLeadCap(y)));
        }
        // 0.045 两侧密集扫描：tie 两侧各 4096 点
        for (var i = -4096; i <= 4096; i++)
        {
            var y = 0.045 + i * 1e-12;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Min(0.045, y)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MinLeadCap(y)));
        }
        var rng = new Random(0x2E82);
        for (var i = 0; i < 30001; i++)
        {
            var y = (i % 3) switch
            {
                0 => NextSigned(rng, 1e2),
                1 => rng.NextDouble() * 0.09,
                _ => rng.NextDouble() < 0.25 ? double.NaN : rng.NextDouble() * 1e-3,
            };
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Min(0.045, y)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MinLeadCap(y)));
        }
    }

    [Fact]
    public void MinNonNegative_MatchesMathMin_BitForBit()
    {
        // 2.8.0: ClampWallClockLead 外层 Math.Min(a, b) 改为 MinNonNegative 分支链。
        // 真实路径两个输入都来自 AtLeastZero（恒 >= +0.0，不含 -0.0），因此
        // a < b ? a : b 与 Math.Min 在全部可达对上逐位一致；NaN 输入按 Math.Min 的
        // 语义归一为规范 NaN。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var vals = new[]
        {
            0.0, -0.0, 0.001, 0.045, 0.5, 1.0, 2.0, 1e-300, 1e300, double.Epsilon,
            double.MaxValue, double.PositiveInfinity, double.NaN, customNan,
        };
        foreach (var a in vals)
        {
            foreach (var b in vals)
            {
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(Math.Min(a, b)),
                    BitConverter.DoubleToInt64Bits(KaraokeMath.MinNonNegative(a, b)));
            }
        }
        // 大量随机非负对 + NaN 混入
        var rng = new Random(0x2E83);
        for (var i = 0; i < 60000; i++)
        {
            var a = rng.NextDouble() < 0.25 ? double.NaN : rng.NextDouble() * 1e6;
            var b = rng.NextDouble() < 0.25 ? double.NaN : rng.NextDouble() * 1e6;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Min(a, b)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.MinNonNegative(a, b)));
        }
    }

    [Fact]
    public void AbsGreaterThan_MatchesMathAbs_BooleanIdentical()
    {
        // 2.8.3: OnPositionChanged 的硬/软同步判定 Math.Abs(delta) > t 改走分支链
        // x > t || x < -t——|x| > t <-> x > t || x < -t 对全部 double 输入对恒成立：
        // NaN 两侧比较均假（永不触发同步）、±Inf 落在两侧比较、+0/-0 与恰在阈值上不越闸。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var specials = new[]
        {
            double.NaN, customNan, -customNan,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5,
            double.Epsilon, -double.Epsilon,
            double.MaxValue, double.MinValue, double.MaxValue / 2, -double.MaxValue / 2,
            1e-300, -1e-300, 1e300, -1e300,
        };
        // 两个真实阈值 × 21 特异值
        foreach (var t in new[] { 0.30, 0.80 })
        {
            foreach (var x in specials)
            {
                Assert.Equal(Math.Abs(x) > t, KaraokeMath.AbsGreaterThan(x, t));
            }
        }
        // 21×21 全组合（覆盖 NaN/±Inf/±0 阈值等异端）
        foreach (var a in specials)
        {
            foreach (var b in specials)
            {
                Assert.Equal(Math.Abs(a) > b, KaraokeMath.AbsGreaterThan(a, b));
            }
        }
        // 大量随机对：x 秒级偏差，t 阈值 [0,5)
        var rng = new Random(0xA38D);
        for (var i = 0; i < 300000; i++)
        {
            var x = NextSigned(rng, 2.0);
            var t = rng.NextDouble() * 5.0;
            Assert.Equal(Math.Abs(x) > t, KaraokeMath.AbsGreaterThan(x, t));
        }
        for (var i = 0; i < 60000; i++)
        {
            var x = NextSigned(rng, 1e300);
            var t = NextSigned(rng, 1e300);
            Assert.Equal(Math.Abs(x) > t, KaraokeMath.AbsGreaterThan(x, t));
        }
        // 每个真实阈值两侧稠密扫描（±0.00005 邻域，100001 点）
        foreach (var t in new[] { 0.30, 0.80 })
        {
            for (var i = 0; i <= 100000; i++)
            {
                var x = t + (i - 50000) * 1e-9;
                Assert.Equal(Math.Abs(x) > t, KaraokeMath.AbsGreaterThan(x, t));
            }
        }
    }

    [Fact]
    public void AbsGreaterThan_ExactTiesAndZeros_StayBelowGate()
    {
        // 2.8.3: 恰在 ±t 上、±0、NaN 均不越闸；刚越阈值与 ±Inf 越闸。
        foreach (var t in new[] { 0.30, 0.80 })
        {
            Assert.False(KaraokeMath.AbsGreaterThan(t, t));
            Assert.False(KaraokeMath.AbsGreaterThan(-t, t));
            Assert.False(KaraokeMath.AbsGreaterThan(0.0, t));
            Assert.False(KaraokeMath.AbsGreaterThan(-0.0, t));
            Assert.True(KaraokeMath.AbsGreaterThan(t * 1.0000001, t));
            Assert.True(KaraokeMath.AbsGreaterThan(-(t * 1.0000001), t));
            Assert.False(KaraokeMath.AbsGreaterThan(double.NaN, t));
            Assert.False(KaraokeMath.AbsGreaterThan(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(KaraokeMath.AbsGreaterThan(double.NegativeInfinity, double.NegativeInfinity)); // Math.Abs(-Inf) > -Inf = +Inf > -Inf = true
            Assert.True(KaraokeMath.AbsGreaterThan(double.PositiveInfinity, 0.0));
            Assert.True(KaraokeMath.AbsGreaterThan(double.NegativeInfinity, 0.0));
        }
    }

    [Fact]
    public void AtLeastZero_ZeroFirstArgOrder_MatchesMathMax_BitForBit()
    {
        // 2.8.3: OnPositionChanged 的外推上限 StallAwareLead(Math.Max(0.0, elapsed), ...) 改走
        // 同一分支链 AtLeastZero——Math.Max 首参为常量 0.0 时与 Math.Max(x, 0.0) 位形一致
        // （IEEE 754-2019 maximum：混合 ±0 恒为 +0、NaN 原样透传、相等返回首参同为 +0）。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var specials = new[]
        {
            double.NaN, customNan, -customNan,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5,
            double.Epsilon, -double.Epsilon,
            double.MaxValue, double.MinValue, double.MaxValue / 2, -double.MaxValue / 2,
            1e-300, -1e-300, 1e300, -1e300,
        };
        foreach (var x in specials)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(0.0, x)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.AtLeastZero(x)));
        }
        var rng = new Random(0x7A21);
        for (var i = 0; i < 30001; i++)
        {
            var x = NextSigned(rng, 1e6 * rng.NextDouble());
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Max(0.0, x)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.AtLeastZero(x)));
        }
    }

    [Fact]
    public void ClampSpeedScale_MatchesMathClamp_BitForBit()
    {
        // 2.8.9: KaraokeTextBlock 调速倍率钳制 Math.Clamp(speed<=0 ? 1.0 : speed, 0.2, 3.0)
        // 改为单比较守卫 + 双边界分支链；全部 double 输入上与运行时逐位一致——NaN 经
        // NaN <= 0 为 false 原样透传、-Inf/<=0 折叠到 1.0、+Inf 落 3.0、恰在 0.2/3.0
        // 的 tie 返回原值（Math.Clamp 相等时返回 value）。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var specials = new[]
        {
            double.NaN, customNan, -customNan,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 1.0, -1.0, 2.0, 0.2, 3.0, 0.15, 0.25, 2.99, 3.01,
            double.Epsilon, -double.Epsilon,
            double.MaxValue, double.MinValue, double.MaxValue / 2, -double.MaxValue / 2,
            1e-300, -1e-300, 1e300, -1e300,
        };
        foreach (var s in specials)
        {
            var exp = Math.Clamp(s <= 0 ? 1.0 : s, 0.2, 3.0);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(exp),
                BitConverter.DoubleToInt64Bits(KaraokeMath.ClampSpeedScale(s)));
        }
        // 0.2 / 3.0 / 0 三个边界两侧稠密扫描
        foreach (var edge in new[] { 0.2, 3.0, 0.0 })
        {
            for (var i = -4096; i <= 4096; i++)
            {
                var s = edge + i * 1e-9;
                var exp = Math.Clamp(s <= 0 ? 1.0 : s, 0.2, 3.0);
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(exp),
                    BitConverter.DoubleToInt64Bits(KaraokeMath.ClampSpeedScale(s)));
            }
        }
        var rng = new Random(0x2E89);
        for (var i = 0; i < 60001; i++)
        {
            var s = (i % 4) switch
            {
                0 => NextSigned(rng, 10.0),
                1 => rng.NextDouble() * 4.0 - 0.5,
                2 => rng.NextDouble() < 0.25 ? double.NaN : NextSigned(rng, 1e4),
                _ => rng.NextDouble() * 1e-6 - 5e-7,
            };
            var exp = Math.Clamp(s <= 0 ? 1.0 : s, 0.2, 3.0);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(exp),
                BitConverter.DoubleToInt64Bits(KaraokeMath.ClampSpeedScale(s)));
        }
    }

    [Fact]
    public void ClampFraction_MatchesMathClamp_BitForBit()
    {
        // 2.8.9: KaraokeTextBlock 整行均分模式目标高亮分数钳制 Math.Clamp(f, 0, 1)
        // 改为双比较分支链；全部 double 输入上与运行时逐位一致——NaN 原样透传
        // （Math.Clamp 未命中比较时返回 value）、±Inf 落端点、-0.0 保持 -0.0、恰在 0/1
        // 的 tie 返回原值。
        var customNan = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000001234UL));
        var specials = new[]
        {
            double.NaN, customNan, -customNan,
            double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 0.999999999999, 1.000000000001,
            double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue,
            double.MaxValue / 2, -double.MaxValue / 2, 1e-300, -1e-300, 1e300, -1e300,
        };
        foreach (var f in specials)
        {
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Clamp(f, 0, 1)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.ClampFraction(f)));
        }
        // 0 与 1 两侧稠密扫描（跨 ±0 邻域）
        foreach (var edge in new[] { 0.0, 1.0 })
        {
            for (var i = -4096; i <= 4096; i++)
            {
                var f = edge + i * 1e-9;
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(Math.Clamp(f, 0, 1)),
                    BitConverter.DoubleToInt64Bits(KaraokeMath.ClampFraction(f)));
            }
        }
        var rng = new Random(0x2E8A);
        for (var i = 0; i < 60001; i++)
        {
            var f = (i % 4) switch
            {
                0 => NextSigned(rng, 2.0),
                1 => rng.NextDouble() * 1.2 - 0.1,
                2 => rng.NextDouble() < 0.25 ? double.NaN : NextSigned(rng, 1e6),
                _ => rng.NextDouble() * 1e-6 - 5e-7,
            };
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Clamp(f, 0, 1)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.ClampFraction(f)));
        }
    }

    [Fact]
    public void LineFraction_MatchesOldFormula_BitForBit()
    {
        // 2.9.2: Math.Clamp((posSec - startSec) / Math.Max(0.1, nextStartSec - startSec), 0, 1)
        static double Old(double pos, double start, double next)
            => Math.Clamp((pos - start) / Math.Max(0.1, next - start), 0, 1);
        var specials = new[]
        {
            0.0, -0.0, 0.1, -0.1, 0.09999999999999999, 0.10000000000000001,
            0.20000000000000001, 0.5, 1.0, 2.0, 5.0, -5.0, 300.0,
            double.MaxValue, double.MinValue, 1e-308, -1e-308,
            double.PositiveInfinity, double.NegativeInfinity, double.NaN, -double.NaN,
        };
        foreach (var pos in specials)
            foreach (var start in specials)
                foreach (var next in specials)
                    Assert.Equal(
                        BitConverter.DoubleToInt64Bits(Old(pos, start, next)),
                        BitConverter.DoubleToInt64Bits(KaraokeMath.LineFraction(pos, start, next)));
        var rng = new Random(0x3B2F);
        for (var i = 0; i < 60001; i++)
        {
            var pos = (i % 3) switch
            {
                0 => NextSigned(rng, 600.0),
                1 => rng.NextDouble() < 0.2 ? double.NaN : NextSigned(rng, 1e6),
                _ => rng.NextDouble() * 1e-6 - 5e-7,
            };
            var start = (i % 2) switch
            {
                0 => NextSigned(rng, 600.0),
                _ => rng.NextDouble() < 0.15 ? double.PositiveInfinity : NextSigned(rng, 1e6),
            };
            var next = rng.NextDouble() < 0.1 ? double.NaN : NextSigned(rng, 1200.0);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Old(pos, start, next)),
                BitConverter.DoubleToInt64Bits(KaraokeMath.LineFraction(pos, start, next)));
        }
    }

    private static double NextSigned(Random rng, double magnitude)
    {
        var v = rng.NextDouble() * magnitude;
        return (rng.Next(2) == 0) ? -v : v;
    }
}
