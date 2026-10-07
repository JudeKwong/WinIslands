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

    /// <summary>
    /// 2.9.5: 2*PI/response - the UIKit natural-frequency base (rad/s). Hoists
    /// the duplicated (2*PI/response) expression out of Configure: the old body
    /// recomputed it up to three times per Configure, now it is computed once
    /// and reused. Bitwise identical on every input because the very same
    /// expression yields the very same bits, and both Omega0 branches divide
    /// or assign exactly that value as before.
    /// </summary>
    internal static double AngularBase(double response) => 2 * Math.PI / response;

    /// <summary>
    /// 2.9.5: root of the underdamped discriminant, sqrt(1 - zeta*zeta).
    /// Centralises Configure's inline expression so the two damping branches
    /// share one documented, tested form (same expression, same bits).
    /// </summary>
    internal static double UnderdampedRoot(double zeta) => Math.Sqrt(1 - zeta * zeta);

    /// <summary>
    /// 2.9.5: root of the overdamped discriminant, sqrt(zeta*zeta - 1).
    /// Centralises RebuildCoefficients' inline expression (same expression,
    /// same bits) so coefficient rebuilds use the shared tested form.
    /// </summary>
    internal static double OverdampedRoot(double zeta) => Math.Sqrt(zeta * zeta - 1);
}
