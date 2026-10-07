using System;

namespace WinIslands.UI;

/// <summary>
/// 2.8.6: IOSSpring 参数归一化的纯数值函数（分支链，便于单元测试）。
/// </summary>
internal static class IOSSpringMath
{
    /// <summary>
    /// Normalize UIKit-style spring parameters (2.8.6): the floor/clamp calls
    /// become branch chains that are bitwise identical to the runtime
    /// Math.Max(0.01, mass) / Math.Clamp(dampingRatio, 0.01, 2.0) /
    /// Math.Max(0.03, responseSeconds) - NaN passes through with identical
    /// bits, +/-Inf clamps to the endpoints, +/-0 and exact ties follow the
    /// IEEE semantics verified on this machine - while each call site drops
    /// the range-check call. Returns (Mass, Zeta, Response) matching the old
    /// Configure body on every input, so the spring pose is unchanged.
    /// </summary>
    internal static (double Mass, double Zeta, double Response) NormalizeParams(
        double dampingRatio, double responseSeconds, double mass)
    {
        var m = SpringMath.MaxFloor(mass, 0.01);                // == Math.Max(0.01, mass)
        var z = WaveMath.ClampRange(dampingRatio, 0.01, 2.0);   // == Math.Clamp(dampingRatio, 0.01, 2.0)
        var r = SpringMath.MaxFloor(responseSeconds, 0.03);     // == Math.Max(0.03, responseSeconds)
        return (m, z, r);
    }
}
