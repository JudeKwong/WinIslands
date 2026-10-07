using System;

namespace WinIslands.UI;

/// <summary>
/// 逐字卡拉OK渲染的纯数值工具（2.3.6）：颜色通道混合。
/// 过渡字每帧按填充进度从底色向高亮色插值；提取为纯函数便于单元测试，
/// 并对非法进度（NaN/Infinity）与越界进度做钳制，杜绝黑色字节（旧代码 NaN 强转 byte 得 0）。
/// </summary>
internal static class KaraokeMath
{
    /// <summary>
    /// 混合单个颜色通道：frac=0 返回 from，frac=1 返回 to，中间线性插值（与旧实现一致地截断取整）。
    /// 非法进度按 from（未点亮）兜底；越界进度钳制到端点，绝不产生越界字节值。
    /// </summary>
    internal static byte BlendChannel(byte from, byte to, double frac)
    {
        if (!double.IsFinite(frac)) return from;
        if (frac <= 0.0) return from;
        if (frac >= 1.0) return to;
        return (byte)(from + (to - from) * frac);
    }

    /// <summary>
    /// Blend a whole ARGB color (2.5.8): one finiteness check + one range
    /// clamp for all four channels in a single pass, replacing the old 4x
    /// BlendChannel + Color.FromArgb composition on the per-character karaoke
    /// transition hot path. Byte-identical to the per-channel composition
    /// (exact endpoints, truncated mid-points, out-of-range clamps, NaN/KInf
    /// falls back to `from`).
    /// </summary>
    internal static System.Windows.Media.Color BlendColor(System.Windows.Media.Color from, System.Windows.Media.Color to, double frac)
    {
        if (!double.IsFinite(frac) || frac <= 0.0) return from;
        if (frac >= 1.0) return to;
        return System.Windows.Media.Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * frac),
            (byte)(from.R + (to.R - from.R) * frac),
            (byte)(from.G + (to.G - from.G) * frac),
            (byte)(from.B + (to.B - from.B) * frac));
    }

    /// <summary>
    /// Monotonic fill clamp (2.4.0): raw is never allowed to be lower than the historical peak -
    /// during stall fallback or frozen pauses the highlight only advances, never regresses,
    /// eliminating visible "over-shoot then pull-back" jumps. Non-finite values (NaN/Infinity)
    /// freeze at the peak so NaN never poisons the state array (old code could leave NaN in
    /// _wordFillMax permanently, making later highlights disappear).
    /// </summary>
    internal static double MonotonicFill(double raw, double maxSeen, out double newMax)
    {
        if (!double.IsFinite(raw))
        {
            newMax = maxSeen;
            return maxSeen;
        }
        if (raw < maxSeen)
        {
            newMax = maxSeen;
            return maxSeen;
        }
        newMax = raw;
        return raw;
    }

    /// <summary>ease-in-out (smoothstep, 2.4.0): slow start - fast middle - slow end,
    /// combined with per-word cross-fade for silky continuous per-character advance.
    /// Input is clamped to [0,1] first via a two-comparison branch chain (2.6.8),
    /// bit-identical to the old Math.Clamp(t,0,1) on every branch (NaN and +/-Inf
    /// fall through to the original value exactly as before).</summary>
    internal static double SmoothStep(double t)
    {
        if (t < 0.0) t = 0.0;
        else if (t > 1.0) t = 1.0;
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// Whole-line karaoke tick clamp (2.4.7): bounds the inter-frame delta to
    /// [0.001, 0.05] with a two-comparison branch chain instead of Math.Min/Math.Max,
    /// so the 60/120fps hot path pays two compares rather than two range-check calls.
    /// Semantics byte-identical to the old expression (NaN propagates, +/-Inf clamp).
    /// </summary>
    internal static double ClampTickDelta(double rawDt)
    {
        if (rawDt > 0.05) return 0.05;
        if (rawDt < 0.001) return 0.001;
        return rawDt;
    }

    /// <summary>
    /// Whole-line karaoke character split (2.6.4): given the already-clamped
    /// highlight fraction and the string length, split it into the fully-lit
    /// character count and the in-between blend remainder. The old code
    /// multiplied fraction*length twice per frame per visible line (once as the
    /// Math.Floor input, once for the blend remainder); this helper computes
    /// the scaled position exactly once and reuses it, so every output is
    /// bit-identical to the old formula (same expression order, verified with
    /// DoubleToInt64Bits over a dense sweep).
    /// </summary>
    internal static (int LitChars, double Blend) WholeLineSplit(double fraction, int length)
    {
        var scaled = fraction * length; // 旧代码此处重复计算 fraction*length（Floor 输入一次、blend 余数一次）
        var lit = MinInt((int)Math.Floor(scaled), length); // 2.9.4: 整型分支链，逐位等价 Math.Min(int,int)
        var blend = lit >= length ? 1.0 : scaled - lit;
        return (lit, blend);
    }

    /// <summary>
    /// Math.Max(x, 0.0) / Math.Max(0.0, x) as a single-comparison branch chain
    /// (2.8.0): x <= 0 ? 0 : x. Bit-identical to the runtime Math.Max on every
    /// double input - NaN passes through with the same bits (incl. custom NaN
    /// payloads), -0.0 maps to +0.0, +-Inf clamp to the endpoint, so the
    /// wall-clock extrapolation pose is unchanged frame by frame while the
    /// per-frame hot path drops one range-check call. Used by TickAnimation's
    /// sinceUpdate and ClampWallClockLead's two inner floors.
    /// </summary>
    internal static double AtLeastZero(double x) => x <= 0.0 ? 0.0 : x;

    /// <summary>
    /// Math.Max(d, 0.001) as a branch chain (2.8.0):
    /// d >= 0.001 || d != d ? d : 0.001. Bit-identical to the runtime Math.Max
    /// on every double input - NaN passes through with the same bits (the
    /// self-compare guard), -Inf / -0 / below-floor values land on the floor,
    /// +Inf stays. Used by BuildWordTimeline's word-duration floor and
    /// NeedsAnimationFor's per-word per-frame check.
    /// </summary>
    internal static double MaxDurationFloor(double d) => d >= 0.001 || d != d ? d : 0.001;

    /// <summary>
    /// Math.Min(0.045, y) as a single-comparison branch chain (2.8.0):
    /// y >= 0.045 ? 0.045 : y. Bit-identical to the runtime Math.Min on every
    /// double input - NaN passes through with the same bits, -Inf stays, +Inf
    /// lands on the cap, and the exact-cap tie returns the 0.045 literal just
    /// like Math.Min's val1 on equality. Used by BuildWordTimeline's per-word
    /// cross-lead cap.
    /// </summary>
    internal static double MinLeadCap(double y) => y >= 0.045 ? 0.045 : y;

    /// <summary>
    /// Math.Min(a, b) as a branch chain (2.8.0) bit-identical to the runtime
    /// Math.Min on this target (IEEE 754-2019 minimum semantics): the first NaN
    /// argument wins with its payload intact (both-NaN returns a), a mixed
    /// -0/+0 pair returns -0 regardless of order, ordered values return the
    /// smaller, and equal values return a (bit-identical to b on equality).
    /// The live call site only passes AtLeastZero outputs (>= +0.0, no -0, no
    /// NaN), so the NaN/-0 arms are defensive; the common path is two NaN
    /// self-compares plus one ordered compare. Used by ClampWallClockLead's
    /// outer min.
    /// </summary>
    internal static double MinNonNegative(double a, double b)
    {
        if (a != a) return a;
        if (b != b) return b;
        if (a < b) return a;
        if (b < a) return b;
        if (a == 0.0)
        {
            var ai = BitConverter.DoubleToInt64Bits(a);
            var bi = BitConverter.DoubleToInt64Bits(b);
            return (ai < 0 || bi < 0) ? -0.0 : a;
        }
        return a;
    }

    /// <summary>
    /// Math.Abs(x) &gt; t as a branch chain (2.8.3): x &gt; t || x &lt; -t. For every
    /// double pair (x, t) the |x| &gt; t <-> x &gt; t || x &lt; -t equivalence holds
    /// exactly - NaN never triggers (both comparisons false), +-Inf lands on the
    /// appropriate comparison, +0/-0 and the exact +-t ties stay below the gate -
    /// so the hard/soft position-sync gates in KaraokeTextBlock.OnPositionChanged
    /// are boolean-identical to the Math.Abs form while saving a sign-mask +
    /// compare pair per media position update.
    /// </summary>
    internal static bool AbsGreaterThan(double x, double t) => x > t || x < -t;

    /// <summary>
    /// Math.Clamp(speed <= 0 ? 1.0 : speed, 0.2, 3.0) as a branch chain (2.8.9).
    /// The guard folds first (speed &lt;= 0 -> 1.0, NaN &lt;= 0 is false so NaN passes
    /// through), then a low/high double-comparison chain clamps to [0.2, 3.0].
    /// Bit-identical to the runtime Math.Clamp on every double input - NaN passes
    /// through with its payload, +-Inf land on the endpoints, -0.0 folds to 1.0 via
    /// the guard exactly like the ternary, and exact ties at 0.2/3.0 return the
    /// value (Math.Clamp returns `value` when it equals min/max). Used by
    /// KaraokeTextBlock.RefreshTarget's per-refresh speed-scale rebuild gate.
    /// </summary>
    internal static double ClampSpeedScale(double speed)
    {
        var x = speed <= 0.0 ? 1.0 : speed;
        return x < 0.2 ? 0.2 : x > 3.0 ? 3.0 : x;
    }

    /// <summary>
    /// Math.Clamp(f, 0, 1) as a branch chain (2.8.9): f &lt; 0 ? 0 : f &gt; 1 ? 1 : f.
    /// Bit-identical to the runtime Math.Clamp on every double input - NaN passes
    /// through with its payload, +-Inf land on the endpoints, -0.0 stays -0.0
    /// (Math.Clamp returns `value` when neither comparison is true). Used by
    /// KaraokeTextBlock.RefreshTarget's per-refresh target-fraction write.
    /// </summary>
    internal static double ClampFraction(double f) => f < 0.0 ? 0.0 : f > 1.0 ? 1.0 : f;

    /// <summary>
    /// Per-line karaoke highlight fraction (2.9.2): folds the old two-step
    /// expression Math.Clamp((posSec - startSec) / Math.Max(0.1, nextStartSec - startSec), 0, 1)
    /// into one pure function. The duration floor goes through SpringMath.MaxFloor
    /// (bit-identical to Math.Max(0.1, x), verified by SpringMathTests) and the ratio
    /// goes through ClampFraction (bit-identical to Math.Clamp(&#x2026;, 0, 1), verified by
    /// ClampFraction_MatchesMathClamp_BitForBit), so every input - incl. NaN payloads,
    /// +-Inf durations, -0.0 positions and exact 0.1/0/1 ties - yields the same bits as
    /// the old formula while the 60/120fps lyric-refresh hot path drops one Math.Max
    /// and one Math.Clamp range-check call per frame.
    /// </summary>
    internal static double LineFraction(double posSec, double startSec, double nextStartSec)
    {
        var duration = SpringMath.MaxFloor(nextStartSec - startSec, 0.1); // == Math.Max(0.1, nextStart - start)
        return ClampFraction((posSec - startSec) / duration);             // == Math.Clamp(&#x2026;, 0, 1)
    }

    /// <summary>
    /// Math.Min(int, int) as a single-comparison branch chain (2.9.4):
    /// a &lt; b ? a : b. For every int pair the result is bit-identical to the
    /// runtime Math.Min - on equality (a == b) the bits are the same either way,
    /// int.MinValue/int.MaxValue and every other value fall through the ordered
    /// comparison exactly as Math.Min's (val1 &lt; val2) ? val1 : val2 fold. Used by
    /// WholeLineSplit's per-frame lit-character clamp (the 60/120fps karaoke
    /// refresh drops one Math.Min range check per visible line) and by the four
    /// timeline-rebuild array-length guards (FillScaledDenoms / FillInverseDenoms /
    /// FillScaledAndInverse / FillScaledInverseEnds).
    /// </summary>
    internal static int MinInt(int a, int b) => a < b ? a : b;

}
