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
    /// Input is clamped to [0,1] first (bit-identical to the old behavior).</summary>
    internal static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
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
}
