namespace WinIslands.UI;

/// <summary>
/// xiaotang-daozai 页面过渡使用的二阶阻尼弹簧：
/// x'' = -k(x - target) - c·x'，半隐式欧拉 + 最多 1/480 秒子步。
/// </summary>
public sealed class DaozaiPageSpring
{
    private readonly double _stiffness;
    private readonly double _damping;

    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }

    public DaozaiPageSpring(double value, double stiffness, double dampingRatio)
    {
        Position = value;
        Target = value;
        _stiffness = stiffness;
        _damping = 2.0 * dampingRatio * Math.Sqrt(stiffness);
    }

    public void Start(double position, double target)
    {
        Position = position;
        Target = target;
        Velocity = 0;
    }

    /// <summary>对应参考项目的 slide.pos = ...；只改位置和目标，速度保留。</summary>
    public void Begin(double position, double target)
    {
        Position = position;
        Target = target;
    }

    public void Retarget(double target)
    {
        if (Math.Abs(Target - target) < 0.0001 && Math.Abs(Position - target) < 0.0001)
        {
            Position = target;
            Velocity = 0;
        }
        Target = target;
    }

    public void Snap(double value)
    {
        Position = value;
        Target = value;
        Velocity = 0;
    }

    public void Step(double dt)
    {
        if (double.IsNaN(dt) || dt <= 0) return;
        if (dt > 0.1) dt = 0.1;

        var steps = Math.Max(1, (int)Math.Ceiling(dt * 480.0));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var acceleration = -_stiffness * (Position - Target) - _damping * Velocity;
            Velocity += acceleration * h;
            Position += Velocity * h;
        }
    }

    public bool Settled(double scale = 1.0) =>
        Math.Abs(Position - Target) < 0.002 * scale
        && Math.Abs(Velocity) < 0.02 * scale;
}
