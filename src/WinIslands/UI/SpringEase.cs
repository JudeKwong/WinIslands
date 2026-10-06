using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace WinIslands.UI;

/// <summary>
/// iOS 风格阻尼弹簧缓冲：开始时快速加速、接近目标时减速并带轻微过冲回弹；
/// 速度全程非线性（不再均速、不生硬）。
/// 公式：x(t) = 1 - e^(-zeta*omega0*t) * (cos(omegaD*t) + (zeta*omega0/omegaD)*sin(omegaD*t))
/// </summary>
public sealed class SpringEase : Freezable, IEasingFunction
{
    public double Damping { get; set; } = 12;
    public double Stiffness { get; set; } = 200;
    public double Mass { get; set; } = 1;

    private double _lastD = -1, _lastK = -1, _lastM = -1;
    private double _dd = 12, _kk = 200, _mm = 1;
    // 2.6.7：常量系数（ω0/ζ/ωD/ζω0/ωD 与 −ζω0）在参数变化时随消毒一起预计算一次，
    // 逐帧求值直接复用，不再重复 sqrt/除法/乘法推导（输出逐位一致）。
    private SpringEaseMath.SpringCoeffs _coeffs;

    protected override Freezable CreateInstanceCore() =>
        new SpringEase { Damping = Damping, Stiffness = Stiffness, Mass = Mass };

    public double Ease(double normalizedTime)
    {
        // 2.2.4：非法参数（0/负/NaN）回退安全默认，动画永不产生 NaN/Inf
        if (_lastD != Damping || _lastK != Stiffness || _lastM != Mass)
        {
            SpringEaseMath.Sanitize(Damping, Stiffness, Mass, out _dd, out _kk, out _mm);
            _coeffs = SpringEaseMath.Prepare(_dd, _kk, _mm);
            _lastD = Damping; _lastK = Stiffness; _lastM = Mass;
        }
        // 2.6.5：参数已由 Sanitize 消毒并缓存（_dd/_kk/_mm），走预消毒快路径，
        // 每帧免去 Evaluate 内部的 3 次 IsFinite + 3 次取值域判断；输出逐位一致。
        // 2.6.7：系数已随消毒预计算（_coeffs），逐帧只做 Exp/SinCos/乘加。
        return SpringEaseMath.EvaluatePrepared(_coeffs, normalizedTime);
    }
}

/// <summary>
/// 柔和弹簧（Soft 动效的轮、3 动效的轮）：阻尼更大、刚度更低，回弹更少、收尾更软。
/// </summary>
public sealed class SoftSpringEase : Freezable, IEasingFunction
{
    public double Damping { get; set; } = 16;
    public double Stiffness { get; set; } = 150;
    public double Mass { get; set; } = 1;

    private double _lastD2 = -1, _lastK2 = -1, _lastM2 = -1;
    private double _dd2 = 16, _kk2 = 150, _mm2 = 1;
    // 2.6.7：同上——常量系数随消毒预计算（_coeffs2），逐帧求值直接复用。
    private SpringEaseMath.SpringCoeffs _coeffs2;

    protected override Freezable CreateInstanceCore() =>
        new SoftSpringEase { Damping = Damping, Stiffness = Stiffness, Mass = Mass };

    public double Ease(double normalizedTime)
    {
        // 2.2.4：同上——参数消毒后求值，t=1 精确归 1（消除动画收尾跳变）
        if (_lastD2 != Damping || _lastK2 != Stiffness || _lastM2 != Mass)
        {
            SpringEaseMath.Sanitize(Damping, Stiffness, Mass, out _dd2, out _kk2, out _mm2);
            _coeffs2 = SpringEaseMath.Prepare(_dd2, _kk2, _mm2);
            _lastD2 = Damping; _lastK2 = Stiffness; _lastM2 = Mass;
        }
        // 2.6.5：同上——参数已消毒缓存（_dd2/_kk2/_mm2），走预消毒快路径。
        // 2.6.7：系数已随消毒预计算（_coeffs2），逐帧求值直接复用。
        return SpringEaseMath.EvaluatePrepared(_coeffs2, normalizedTime);
    }
}

/// <summary>
/// 弹簧缓动公共数学（2.2.4 抽出，便于单元测试）：
/// 参数消毒 + 归一化求值，任何非法输入都不会产生 NaN/Inf。
/// </summary>
internal static class SpringEaseMath
{
    public static void Sanitize(double damping, double stiffness, double mass,
        out double d, out double k, out double m)
    {
        m = double.IsFinite(mass) && mass > 0 ? mass : 1.0;
        k = double.IsFinite(stiffness) && stiffness > 0 ? stiffness : 200.0;
        d = double.IsFinite(damping) && damping >= 0 ? damping : 12.0;
    }

    /// <summary>0..1 归一化求值：t=0 → 0，t=1 → 1（精确归位，消除收尾跳变）；
    /// 中途允许轻微过冲（Q 弹），负值钳到 0。</summary>
    public static double Evaluate(double normalized, double damp, double stiffness, double mass)
    {
        // 内部再消毒一次：即使调用方直接传原始参数（不经过 Sanitize），也绝不产生 NaN/Inf
        var d0 = double.IsFinite(damp) && damp >= 0 ? damp : 12.0;
        var k0 = double.IsFinite(stiffness) && stiffness > 0 ? stiffness : 200.0;
        var m0 = double.IsFinite(mass) && mass > 0 ? mass : 1.0;
        return EvaluatePresanitized(normalized, d0, k0, m0);
    }

    /// <summary>
    /// 预消毒求值快路径（2.6.5）：调用方保证 d/k/m 已消毒（有限、d≥0、k>0、m>0），
    /// 直接进入公式主体——每帧省 3 次 IsFinite + 3 次取值域比较（SpringEase/SoftSpringEase
    /// 在参数变化时已用 Sanitize 消毒并缓存，逐帧走此路径）。t 仍按原式钳制到 [0,1]，
    /// 端点精确归位；对已消毒输入与旧 Evaluate（消毒后路径）逐位一致（DoubleToInt64Bits 验证）。
    /// </summary>
    public static double EvaluatePresanitized(double normalized, double d, double k, double m)
        => EvaluatePrepared(Prepare(d, k, m), normalized);

    /// <summary>
    /// 2.6.7：弹簧常量系数集合——与 t 无关的中间量在参数变化时一次预计算，
    /// 逐帧求值全部复用（每帧省 2 次 sqrt、1 次除法与若干乘法/比较；输出逐位一致）。
    /// </summary>
    internal readonly struct SpringCoeffs
    {
        /// <summary>阻尼振荡角频率 ωD（旧公式逐帧 ω0·Sqrt(z2&gt;0?z2:0.0001)，现预计算）。</summary>
        internal readonly double OmegaD;
        /// <summary>旧公式逐帧 (ζ·ω0)/ωD，用于 sin 项系数。</summary>
        internal readonly double RatioZetaOmega0OverOmegaD;
        /// <summary>旧公式逐帧 (−ζ)·ω0（衰减指数系数；IEEE 符号对称下与 −(ζ·ω0) 逐位一致）。</summary>
        internal readonly double NegZetaOmega0;

        internal SpringCoeffs(double omegaD, double ratio, double negZm)
        {
            OmegaD = omegaD;
            RatioZetaOmega0OverOmegaD = ratio;
            NegZetaOmega0 = negZm;
        }
    }

    /// <summary>
    /// 2.6.7：预计算弹簧常量系数。推导顺序与旧逐帧内联公式完全相同，因此逐位一致：
    /// ω0=Sqrt(k/m)；ζ=d/(2·Sqrt(k·m))；z2=1−ζ²；ωD=ω0·Sqrt(z2&gt;0?z2:0.0001)；
    /// zm=ζ·ω0；ratio=(ζ·ω0)/ωD=zm/ωD；negZm=−zm（≡(−ζ)·ω0，IEEE 符号位独立）。
    /// </summary>
    public static SpringCoeffs Prepare(double d, double k, double m)
    {
        var omega0 = Math.Sqrt(k / m);
        var zeta = d / (2 * Math.Sqrt(k * m));
        var z2 = 1 - zeta * zeta;
        var omegaD = omega0 * Math.Sqrt(z2 > 0 ? z2 : 0.0001);
        var zm = zeta * omega0;
        var ratio = zm / omegaD;
        var negZm = -zm;
        return new SpringCoeffs(omegaD, ratio, negZm);
    }

    /// <summary>
    /// 2.6.7：预计算系数求值快路径（逐帧热路径）。调用方保证 c 由 Prepare 产出；
    /// t 仍按原式钳制到 [0,1]、端点精确归位，中途保留 Q 弹过冲；对任意 t 与旧逐帧
    /// 公式逐位一致（t=0→0、t=1→1；2.6.9 起钳制改用与 Math.Clamp 逐位一致的双比较分支链，NaN/±Inf 仍与原路径一致）。
    /// </summary>
    public static double EvaluatePrepared(in SpringCoeffs c, double normalized)
    {
        var t = normalized;
        if (t < 0.0) t = 0.0;
        else if (t > 1.0) t = 1.0; // 2.6.9: 分支链钳制，与 Math.Clamp 逐位一致
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        var tt = t * 1.7;
        var decay = Math.Exp(c.NegZetaOmega0 * tt);
        var (st, ct) = Math.SinCos(c.OmegaD * tt);
        var v = 1 - decay * (ct + c.RatioZetaOmega0OverOmegaD * st);
        return v < 0 ? 0 : v;
    }
}
