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

    protected override Freezable CreateInstanceCore() =>
        new SpringEase { Damping = Damping, Stiffness = Stiffness, Mass = Mass };

    public double Ease(double normalizedTime)
    {
        // 2.2.4：非法参数（0/负/NaN）回退安全默认，动画永不产生 NaN/Inf
        if (_lastD != Damping || _lastK != Stiffness || _lastM != Mass)
        {
            SpringEaseMath.Sanitize(Damping, Stiffness, Mass, out _dd, out _kk, out _mm);
            _lastD = Damping; _lastK = Stiffness; _lastM = Mass;
        }
        return SpringEaseMath.Evaluate(normalizedTime, _dd, _kk, _mm);
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

    protected override Freezable CreateInstanceCore() =>
        new SoftSpringEase { Damping = Damping, Stiffness = Stiffness, Mass = Mass };

    public double Ease(double normalizedTime)
    {
        // 2.2.4：同上——参数消毒后求值，t=1 精确归 1（消除动画收尾跳变）
        if (_lastD2 != Damping || _lastK2 != Stiffness || _lastM2 != Mass)
        {
            SpringEaseMath.Sanitize(Damping, Stiffness, Mass, out _dd2, out _kk2, out _mm2);
            _lastD2 = Damping; _lastK2 = Stiffness; _lastM2 = Mass;
        }
        return SpringEaseMath.Evaluate(normalizedTime, _dd2, _kk2, _mm2);
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
        var t = Math.Clamp(normalized, 0.0, 1.0);
        if (t <= 0.0) return 0.0;
        if (t >= 1.0) return 1.0;
        var tt = t * 1.7;
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
