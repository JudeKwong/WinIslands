using Avalonia.Animation.Easings;

namespace WinIslands.Mac.UI;

/// <summary>
/// iOS 风格阻尼弹簧缓冲（Avalonia 移植）：开始时快速加速、接近目标时减速并带轻微过冲回弹；
/// 速度全程非线性（不再均速、不生硬）。
/// 公式：x(t) = 1 - e^(-zeta*omega0*t) * (cos(omegaD*t) + (zeta*omega0/omegaD)*sin(omegaD*t))
/// </summary>
public sealed class SpringEase : Easing
{
    public double Damping { get; init; } = 12;
    public double Stiffness { get; init; } = 200;
    public double Mass { get; init; } = 1;

    public override double Ease(double progress)
    {
        var t = Math.Max(0.0, Math.Min(1.0, progress)) * 1.7;
        var omega0 = Math.Sqrt(Stiffness / Mass);
        var zeta = Damping / (2 * Math.Sqrt(Stiffness * Mass));
        var z2 = 1 - zeta * zeta;
        var omegaD = omega0 * Math.Sqrt(z2 > 0 ? z2 : 0.0001);
        var decay = Math.Exp(-zeta * omega0 * t);
        return 1 - decay * (Math.Cos(omegaD * t) + (zeta * omega0 / omegaD) * Math.Sin(omegaD * t));
    }
}

/// <summary>
/// 柔和弹簧：阻尼更大、刚度更低，回弹更少、收尾更软（Soft 动效）。
/// </summary>
public sealed class SoftSpringEase : Easing
{
    public double Damping { get; init; } = 16;
    public double Stiffness { get; init; } = 150;
    public double Mass { get; init; } = 1;

    public override double Ease(double progress)
    {
        var t = Math.Max(0.0, Math.Min(1.0, progress)) * 1.7;
        var omega0 = Math.Sqrt(Stiffness / Mass);
        var zeta = Damping / (2 * Math.Sqrt(Stiffness * Mass));
        var z2 = 1 - zeta * zeta;
        var omegaD = omega0 * Math.Sqrt(z2 > 0 ? z2 : 0.0001);
        var decay = Math.Exp(-zeta * omega0 * t);
        return 1 - decay * (Math.Cos(omegaD * t) + (zeta * omega0 / omegaD) * Math.Sin(omegaD * t));
    }
}
