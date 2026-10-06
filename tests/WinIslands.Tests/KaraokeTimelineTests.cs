using WinIslands.Services;
using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 逐字卡拉OK时间轴回归测试：NeedsAnimation 的判定必须与 RenderWords 的渲染时间轴
/// （含字间 lead 预亮）完全一致，否则会在字/句边界“动一下停一下”或提前停动画；
/// 墙钟外推必须限幅，防止 ViewModel 停更时歌词漂到句尾再跳回。
/// </summary>
public class KaraokeTimelineTests
{
    private static readonly TtmlWord[] Words =
    {
        new("作", 0.0, 0.5),
        new("词", 0.5, 1.0),
        new("林", 1.0, 1.4),
        new("夕", 1.4, 1.8),
    };

    [Fact]
    public void BuildWordTimeline_FirstWordNoLead_SubsequentWordsHaveLead()
    {
        var starts = new double[4];
        var denoms = new double[4];
        KaraokeTextBlock.BuildWordTimeline(Words, starts, denoms);

        // 句首第一个字不提前（保证换句时第一个字保持未点亮）
        Assert.Equal(0.0, starts[0], 6);
        Assert.Equal(0.5, denoms[0], 6);
        // 后续字在其开始前最多提前 45ms 起笔
        Assert.Equal(0.5 - 0.045, starts[1], 6);
        Assert.Equal(0.5 + 0.045, denoms[1], 6);
        Assert.Equal(1.0 - 0.045, starts[2], 6);
        Assert.Equal(0.4 + 0.045, denoms[2], 6);
        // lead 不为负、时长不为 0
        for (var i = 0; i < 4; i++)
        {
            Assert.True(starts[i] >= 0);
            Assert.True(denoms[i] > 0);
            Assert.True(starts[i] < Words[i].EndSec);
        }
    }

    [Fact]
    public void NeedsAnimation_MatchesRenderTimeline_AtWordBoundaries()
    {
        var starts = new double[4];
        var denoms = new double[4];
        KaraokeTextBlock.BuildWordTimeline(Words, starts, denoms);

        // 第一字开始前：不点亮（静态即可）
        Assert.False(KaraokeTextBlock.NeedsAnimationFor(-0.01, starts, denoms, 1.0));
        // 第一字刚开始：需要动画（raw = 0）
        Assert.True(KaraokeTextBlock.NeedsAnimationFor(0.0, starts, denoms, 1.0));
        // 第二个字 lead 预亮窗口（word2.begin - 0.03）：渲染已经起笔，必须判定为仍需动画
        //（旧实现用 w.BeginSec 判定，会在该窗口停动画 → 字边界“停一下再动一下”）
        Assert.True(KaraokeTextBlock.NeedsAnimationFor(0.47, starts, denoms, 1.0));
        // 词已全部点亮（第二个字 raw >= 1）
        Assert.False(KaraokeTextBlock.NeedsAnimationFor(10.0, starts, denoms, 1.0));
    }

    [Fact]
    public void NeedsAnimation_AlwaysConsistentWithRenderFormula_AcrossWholeTimeline()
    {
        var starts = new double[4];
        var denoms = new double[4];
        KaraokeTextBlock.BuildWordTimeline(Words, starts, denoms);
        const double speed = 1.0;
        for (var pos = -0.1; pos <= 2.2; pos += 0.001)
        {
            var expected = false; // 渲染公式：存在任意一个字 raw ∈ [0,1) 即需要动画
            for (var i = 0; i < starts.Length; i++)
            {
                var raw = (pos - starts[i]) / denoms[i] * speed;
                if (pos >= starts[i] && raw < 1) { expected = true; break; }
            }
            Assert.Equal(expected, KaraokeTextBlock.NeedsAnimationFor(pos, starts, denoms, speed));
        }
    }

    [Theory]
    [InlineData(100.0, 50.0, 100.5)]   // 外推超过上限：限幅到 0.5s
    [InlineData(100.0, 10.0, 100.5)]   // 长时间停更：绝不无限前移
    [InlineData(100.0, 0.2, 100.2)]    // 正常间隙：原样推进
    [InlineData(100.0, -5.0, 100.0)]   // 时钟回退：不下探
    public void ClampWallClockLead_CapsExtrapolation(double posBase, double elapsed, double expected)
    {
        Assert.Equal(expected, KaraokeTextBlock.ClampWallClockLead(posBase, elapsed), 6);
    }
    [Theory]
    [InlineData(0.0, 0.5, 0.5)]
    [InlineData(0.2, 0.5, 0.5)]
    [InlineData(0.35, 0.5, 0.5)]
    [InlineData(0.5, 0.5, 0.25)]
    [InlineData(0.64, 0.5, 0.0166666667)]
    [InlineData(0.65, 0.5, 0.0)]
    [InlineData(10.0, 0.5, 0.0)]
    public void StallAwareLead_TightensWhenUpdatesStop(double since, double maxLead, double expected)
    {
        Assert.Equal(expected, KaraokeTextBlock.StallAwareLead(since, maxLead), 4);
    }

    [Fact]
    public void StallAwareLead_MonotonicInStallWindow()
    {
        double prev = double.MaxValue;
        for (var s = 0.35; s <= 0.651; s += 0.001)
        {
            var v = KaraokeTextBlock.StallAwareLead(s, 0.5);
            Assert.True(v <= prev + 1e-9, $"lead grew at since={s}");
            prev = v;
        }
        Assert.Equal(0.0, prev, 6);
    }

    [Fact]
    public void StallAwareLead_InnerWindow_RawFactorMatch()
    {
        // 2.7.0: 内层分支 (grace < since < freeze) 去掉 Math.Min/Math.Max 钳制后，
        // 输出必须与旧式 fullLead * Math.Max(0.0, Math.Min(1.0, f)) 完全一致（逐位）。
        const double grace = 0.35, freeze = 0.65, fullLead = 0.5;
        const int steps = 4096;
        for (var i = 0; i <= steps; i++)
        {
            var since = grace + (freeze - grace) * (i + 0.5) / (steps + 1.0); // 严格开区间内
            var f = (freeze - since) / (freeze - grace);
            var expected = fullLead * Math.Max(0.0, Math.Min(1.0, f));
            var actual = KaraokeTextBlock.StallAwareLead(since, fullLead);
            Assert.Equal(expected, actual); // 二进制相等
            Assert.Equal(fullLead * f, actual); // 与未钳制公式一致
        }
    }

    [Fact]
    public void StallAwareLead_NonFiniteBoundaryBehavior()
    {
        // 早退分支策略不变：-Inf 归全额、+Inf 归零；NaN 落入内层公式，NaN 语义保持一致
        Assert.Equal(0.5, KaraokeTextBlock.StallAwareLead(double.NegativeInfinity, 0.5));
        Assert.Equal(0.0, KaraokeTextBlock.StallAwareLead(double.PositiveInfinity, 0.5));
        Assert.True(double.IsNaN(KaraokeTextBlock.StallAwareLead(double.NaN, 0.5)));
    }

    [Fact]
    public void ClampWallClockLead_RespectsProvidedCap()
    {
        Assert.Equal(100.2, KaraokeTextBlock.ClampWallClockLead(100.0, 0.2, 0.3), 6);
        Assert.Equal(100.3, KaraokeTextBlock.ClampWallClockLead(100.0, 0.5, 0.3), 6);
        Assert.Equal(100.0, KaraokeTextBlock.ClampWallClockLead(100.0, -1.0, 0.3), 6);
        Assert.Equal(100.05, KaraokeTextBlock.ClampWallClockLead(100.0, 0.05, 0.15), 6);
    }
    [Fact]
    public void ApplyMonotonicFill_NeverRegresses()
    {
        double max = 0;
        var seq = new[] { 0.1, 0.3, 0.25, 0.4, 0.39, 0.5, 0.05 };
        var last = -1.0;
        foreach (var v in seq)
        {
            var r = KaraokeMath.MonotonicFill(v, max, out max);
            Assert.True(r >= last - 1e-12, $"fill regressed at {v}");
            Assert.Equal(max, r, 9);
            last = r;
        }
        Assert.Equal(0.5, max, 9);
    }

    [Fact]
    public void ApplyMonotonicFill_FreezeDuringStallCycle()
    {
        // 模拟：正常播放推进到 0.42 → 停滞窗口回拉 → 冻结 → 恢复后播放器位置仍落后 → 高亮不得倒退
        double max = 0;
        var r1 = KaraokeMath.MonotonicFill(0.42, max, out max);
        Assert.Equal(0.42, r1, 9);
        var r2 = KaraokeMath.MonotonicFill(0.38, max, out max); // 回拉
        Assert.Equal(0.42, r2, 9);
        var r3 = KaraokeMath.MonotonicFill(0.40, max, out max); // 恢复但落后
        Assert.Equal(0.42, r3, 9);
        var r4 = KaraokeMath.MonotonicFill(0.55, max, out max); // 追平并前进
        Assert.Equal(0.55, r4, 9);
    }

    [Fact]
    public void FillScaledDenoms_ScalesDurations()
    {
        // 2.2.8: scaled = denom / speed, computed once instead of per frame
        var denoms = new double[] { 0.2, 0.3, 0.05 };
        var scaled = new double[3];
        KaraokeTextBlock.FillScaledDenoms(denoms, 1.5, scaled);
        Assert.Equal(0.2 / 1.5, scaled[0], 9);
        Assert.Equal(0.3 / 1.5, scaled[1], 9);
        Assert.Equal(0.05 / 1.5, scaled[2], 9);
    }

    [Fact]
    public void FillScaledDenoms_GuardsNonPositive()
    {
        // 2.2.8: NaN / zero / negative denominators fall back to a positive base
        var denoms = new double[] { 0.0, -1.0, double.NaN, double.PositiveInfinity };
        var scaled = new double[4];
        KaraokeTextBlock.FillScaledDenoms(denoms, 2.0, scaled);
        foreach (var s in scaled) Assert.True(s > 0 && double.IsFinite(s), $"bad scaled {s}");
    }

    [Fact]
    public void NeedsAnimationForScaled_MatchesLegacy()
    {
        // 2.2.8: the precomputed fast path is mathematically identical to the legacy scan
        var words = new[]
        {
            new TtmlWord("A", 1.0, 1.5),
            new TtmlWord("B", 1.6, 2.0),
            new TtmlWord("C", 2.0, 2.0),
        };
        var starts = new double[3];
        var denoms = new double[3];
        KaraokeTextBlock.BuildWordTimeline(words, starts, denoms);
        var scaled = new double[3];
        KaraokeTextBlock.FillScaledDenoms(denoms, 1.0, scaled);
        for (var pos = 0.0; pos < 3.0; pos += 0.013)
        {
            var legacy = KaraokeTextBlock.NeedsAnimationFor(pos, starts, denoms, 1.0);
            var fast = KaraokeTextBlock.NeedsAnimationForScaled(pos, starts, scaled);
            Assert.Equal(legacy, fast);
        }
    }

    [Fact]
    public void FillInverseDenoms_ReciprocalMatchesDivision()
    {
        // 2.2.11: inverse array equals 1/duration; multiply-by-inverse is equivalent to division (12-digit)
        var denoms = new[] { 0.001, 0.1, 0.5, 1.2, 3.7 };
        var scaled = new double[denoms.Length];
        var inv = new double[denoms.Length];
        KaraokeTextBlock.FillScaledDenoms(denoms, 1.0, scaled);
        KaraokeTextBlock.FillInverseDenoms(scaled, inv);
        for (var i = 0; i < denoms.Length; i++)
        {
            Assert.Equal(1.0 / scaled[i], inv[i], 12);
            Assert.True(double.IsFinite(inv[i]));
            var pos = scaled[i] * 0.37;
            Assert.Equal(pos / scaled[i], pos * inv[i], 12);
        }
    }

    [Fact]
    public void FillInverseDenoms_GuardsInvalid_NoNaNOrInf()
    {
        // 2.2.11: zero/negative/NaN/Inf durations fall back to the 0.001s base-length reciprocal 1000/s
        var scaled = new[] { 0.0, -0.5, double.NaN, double.PositiveInfinity, 0.25 };
        var inv = new double[scaled.Length];
        KaraokeTextBlock.FillInverseDenoms(scaled, inv);
        Assert.Equal(1000.0, inv[0], 12);
        Assert.Equal(1000.0, inv[1], 12);
        Assert.Equal(1000.0, inv[2], 12);
        Assert.Equal(1000.0, inv[3], 12);
        Assert.Equal(4.0, inv[4], 12);
        foreach (var v in inv) Assert.True(double.IsFinite(v));
    }

    [Fact]
    public void FillInverseDenoms_EmptyInputsNoThrow()
    {
        // 2.2.11: empty or mismatched arrays are safe no-ops
        KaraokeTextBlock.FillInverseDenoms(Array.Empty<double>(), Array.Empty<double>());
        var inv = new double[2];
        KaraokeTextBlock.FillInverseDenoms(new[] { 0.1, 0.2, 0.3 }, inv);
        Assert.Equal(10.0, inv[0], 12);
        Assert.Equal(5.0, inv[1], 12);
    }

    [Fact]
    public void FillScaledAndInverse_SinglePassMatchesTwoStep()
    {
        // 2.2.13: the single-pass combined fill is equivalent to FillScaledDenoms + FillInverseDenoms (12-digit)
        var denoms = new[] { 0.001, 0.1, 0.5, 1.2, 3.7, 0.05 };
        const double scale = 1.25;
        var scaledA = new double[denoms.Length];
        var invA = new double[denoms.Length];
        KaraokeTextBlock.FillScaledDenoms(denoms, scale, scaledA);
        KaraokeTextBlock.FillInverseDenoms(scaledA, invA);
        var scaledB = new double[denoms.Length];
        var invB = new double[denoms.Length];
        KaraokeTextBlock.FillScaledAndInverse(denoms, scale, scaledB, invB);
        for (var i = 0; i < denoms.Length; i++)
        {
            Assert.Equal(scaledA[i], scaledB[i], 12);
            Assert.Equal(invA[i], invB[i], 12);
            Assert.Equal(1.0 / scaledB[i], invB[i], 12);
        }
    }

    [Fact]
    public void FillScaledAndInverse_GuardsInvalid_NoNaNOrInf()
    {
        // 2.2.13: zero/negative/NaN/Inf durations become the 0.001/s base; inverse stays finite
        var denoms = new[] { 0.0, -0.5, double.NaN, double.PositiveInfinity, 0.25 };
        const double scale = 2.0;
        var scaled = new double[denoms.Length];
        var inv = new double[denoms.Length];
        KaraokeTextBlock.FillScaledAndInverse(denoms, scale, scaled, inv);
        for (var i = 0; i < denoms.Length; i++)
        {
            Assert.True(scaled[i] > 0 && double.IsFinite(scaled[i]), "bad scaled " + scaled[i]);
            Assert.True(double.IsFinite(inv[i]) && inv[i] > 0, "bad inv " + inv[i]);
            Assert.Equal(1.0 / scaled[i], inv[i], 12);
        }
        Assert.Equal(0.001 / scale, scaled[0], 12);
        Assert.Equal(scale / 0.001, inv[0], 12);
    }

    [Fact]
    public void FillScaledAndInverse_EmptyAndMismatched_NoThrow()
    {
        // 2.2.13: empty or length-mismatched inputs are safe no-ops / bounded writes
        KaraokeTextBlock.FillScaledAndInverse(Array.Empty<double>(), 1.0, Array.Empty<double>(), Array.Empty<double>());
        var scaled = new double[2];
        var inv = new double[2];
        KaraokeTextBlock.FillScaledAndInverse(new[] { 0.1, 0.2, 0.3 }, 1.0, scaled, inv);
        Assert.Equal(10.0, inv[0], 12);
        Assert.Equal(5.0, inv[1], 12);
    }
    [Fact]
    public void FillScaledInverseEnds_SinglePassEqualsExistingAndEndsAdd()
    {
        // 2.6.2: scaled/inv 与 FillScaledAndInverse 输出逐位一致；ends[i] == starts[i] + scaled[i] 逐位一致
        var denoms = new[] { 0.001, 0.1, 0.5, 1.2, 3.7, 0.05 };
        var starts = new[] { 1.0, 1.5, -2.25, 10.0, 0.125, 4.0 };
        const double scale = 1.25;
        var scaledA = new double[denoms.Length];
        var invA = new double[denoms.Length];
        var scaledB = new double[denoms.Length];
        var invB = new double[denoms.Length];
        var ends = new double[denoms.Length];
        KaraokeTextBlock.FillScaledAndInverse(denoms, scale, scaledA, invA);
        KaraokeTextBlock.FillScaledInverseEnds(denoms, starts, scale, scaledB, invB, ends);
        for (var i = 0; i < denoms.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(scaledA[i]), BitConverter.DoubleToInt64Bits(scaledB[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(invA[i]), BitConverter.DoubleToInt64Bits(invB[i]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(starts[i] + scaledB[i]), BitConverter.DoubleToInt64Bits(ends[i]));
        }
    }

    [Fact]
    public void FillScaledInverseEnds_GuardsInvalidDenoms_AllFinite()
    {
        // 2.6.2: invalid durations fall back to the 0.001/s base; finite starts keep ends finite
        var denoms = new[] { 0.0, -0.5, double.NaN, double.PositiveInfinity, 0.25 };
        var starts = new[] { -3.0, 0.0, 1.5, 10.0, 2.5 };
        const double scale = 2.0;
        var scaled = new double[denoms.Length];
        var inv = new double[denoms.Length];
        var ends = new double[denoms.Length];
        KaraokeTextBlock.FillScaledInverseEnds(denoms, starts, scale, scaled, inv, ends);
        for (var i = 0; i < denoms.Length; i++)
        {
            Assert.True(scaled[i] > 0 && double.IsFinite(scaled[i]), "bad scaled " + scaled[i]);
            Assert.True(double.IsFinite(inv[i]) && inv[i] > 0, "bad inv " + inv[i]);
            Assert.True(double.IsFinite(ends[i]), "bad end " + ends[i]);
        }
        Assert.Equal(0.001 / scale, scaled[0], 12);
        Assert.Equal(scale / 0.001, inv[0], 12);
    }

    [Fact]
    public void FillScaledInverseEnds_NonFiniteStartPropagatesLikeOldFormula()
    {
        // 2.6.2: NaN/Inf starts propagate into ends exactly like the old start + scaled[i] expression
        var denoms = new[] { 0.2, 0.3 };
        var starts = new[] { double.NaN, double.PositiveInfinity };
        var scaled = new double[2];
        var inv = new double[2];
        var ends = new double[2];
        KaraokeTextBlock.FillScaledInverseEnds(denoms, starts, 1.0, scaled, inv, ends);
        Assert.Equal(BitConverter.DoubleToInt64Bits(starts[0] + scaled[0]), BitConverter.DoubleToInt64Bits(ends[0]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(starts[1] + scaled[1]), BitConverter.DoubleToInt64Bits(ends[1]));
    }

    [Fact]
    public void FillScaledInverseEnds_EmptyAndMismatchedNoThrow()
    {
        // 2.6.2: empty or length-mismatched arrays are safe no-ops / bounded writes
        KaraokeTextBlock.FillScaledInverseEnds(Array.Empty<double>(), Array.Empty<double>(), 1.0, Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
        var starts = new[] { 1.0, 2.0, 3.0 };
        var denoms = new[] { 0.1, 0.2, 0.3 };
        var scaled = new double[2];
        var inv = new double[2];
        var ends = new double[2];
        KaraokeTextBlock.FillScaledInverseEnds(denoms, starts, 1.0, scaled, inv, ends);
        Assert.Equal(10.0, inv[0], 12);
        Assert.Equal(5.0, inv[1], 12);
        Assert.Equal(1.0 + 0.1, ends[0], 12);
        Assert.Equal(2.0 + 0.2, ends[1], 12);
    }


    [Fact]
    public void FractionSettlePredicate_BranchChain_MatchesAbsForm()
    {
        // 2.7.4: KaraokeTextBlock 整行均分模式的收敛判定（Math.Abs(current - target) < 0.002）
        // 改为分支比较链（delta 落在 (-0.002, +0.002) 内才收敛）；必须在全部输入上与
        // Math.Abs 式布尔逐点一致——含 NaN/±Inf（永不收敛）与 ±0.002 精确边界（永不收敛）。
        const double eps = 0.002;
        var specials = new[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 1.0, -1.0, double.MaxValue, double.MinValue,
            double.Epsilon, -double.Epsilon, double.MaxValue / 2, -double.MaxValue / 2,
        };
        for (var i = 0; i < specials.Length; i++)
        {
            for (var j = 0; j < specials.Length; j++)
            {
                var delta = specials[i] - specials[j];
                Assert.Equal(Math.Abs(delta) < eps, delta < eps && delta > -eps);
            }
        }
        // 精确边界：|delta| == ±eps 永不收敛；内部点收敛（用变量避免 CS1718 同变量比较警告）
        var tiePlus = eps;
        Assert.False(tiePlus < eps && tiePlus > -eps, "delta == +eps must not settle");
        Assert.Equal(Math.Abs(tiePlus) < eps, tiePlus < eps && tiePlus > -eps);
        var tieMinus = -eps;
        Assert.False(tieMinus < eps && tieMinus > -eps, "delta == -eps must not settle");
        Assert.Equal(Math.Abs(tieMinus) < eps, tieMinus < eps && tieMinus > -eps);
        var inside = eps * 0.5;
        Assert.True(inside < eps && inside > -eps, "interior must settle");
        Assert.Equal(Math.Abs(inside) < eps, inside < eps && inside > -eps);
    }

    [Fact]
    public void FractionSettlePredicate_DenseSweep_MatchesAbsForm()
    {
        // 2.7.4: 有限域随机密集扫描 + 指数步长边界局部扫描，证明分支链与 Math.Abs 式
        // 在贴近 ±0.002 边界、跨数量级与 NaN 混入等每个可达输入上布尔完全一致。
        const double eps = 0.002;
        var rng = new Random(0x2E74);
        for (var i = 0; i < 30000; i++)
        {
            var current = NextSigned(rng, 1e4);
            var target = (i % 4) switch
            {
                0 => NextSigned(rng, 1e4),
                1 => current + NextSigned(rng, eps * 2), // 贴近边界的差值
                2 => current,
                _ => rng.NextDouble() < 0.5 ? double.NaN : NextSigned(rng, 1e4),
            };
            var delta = current - target;
            Assert.Equal(Math.Abs(delta) < eps, delta < eps && delta > -eps);
        }
        for (var scale = -54; scale <= -10; scale++)
        {
            var step = Math.Pow(2.0, scale);
            for (var sign = -1.0; sign <= 1.0; sign += 2.0)
            {
                for (var k = -3; k <= 3; k++)
                {
                    var delta = sign * (eps + k * step);
                    Assert.Equal(Math.Abs(delta) < eps, delta < eps && delta > -eps);
                }
            }
        }
    }

    private static double NextSigned(Random rng, double magnitude)
    {
        var v = rng.NextDouble() * magnitude;
        return (rng.Next(2) == 0) ? -v : v;
    }
    [Fact]
    public void FractionClamp_BranchChain_MatchesMathClamp()
    {
        // 2.7.5: 整行均分渲染的分数钳制 Math.Clamp(x, 0, 1) 改为双比较分支链
        //（x < 0 ? 0 : x > 1 ? 1 : x）；该表达式必须在全部 double 输入上与 Math.Clamp
        // 逐位一致——NaN 原样透传、±Inf 钳到端点、±0 保持。
        var specials = new[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, 1.0, -1.0, 0.5, double.Epsilon, -double.Epsilon,
            double.MaxValue, double.MinValue, double.MaxValue / 2, -double.MaxValue / 2,
        };
        for (var i = 0; i < specials.Length; i++)
        {
            var x = specials[i];
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Clamp(x, 0.0, 1.0)),
                BitConverter.DoubleToInt64Bits(x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x));
        }
        var rng = new Random(0x2E75);
        for (var i = 0; i < 30001; i++)
        {
            var x = (i % 3) switch
            {
                0 => NextSigned(rng, 1e6),
                1 => rng.NextDouble() * 2.0 - 0.5, // 跨越 [0,1] 边界内外
                _ => rng.NextDouble() < 0.25 ? double.NaN : NextSigned(rng, 1e3),
            };
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Clamp(x, 0.0, 1.0)),
                BitConverter.DoubleToInt64Bits(x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x));
        }
    }}
