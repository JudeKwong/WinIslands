using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// iOS 真弹簧物理引擎测试：解析解正确性 / 打断速度连续 / 临界与过阻尼无回弹 / 收敛。
/// </summary>
public sealed class IOSSpringTests
{
    private const double Dt = 1.0 / 120.0; // 120fps 半步长，检验高频稳定性

    private static void Drain(IOSSpring s, double seconds)
    {
        var steps = (int)Math.Round(seconds / Dt);
        for (var i = 0; i < steps && s.IsActive; i++) s.Tick(Dt);
    }

    [Fact]
    public void Underdamped_SettlesToTarget()
    {
        var s = IOSSpring.Create(0.82, 0.5, from: 0, to: 100);
        Drain(s, 2.0);
        Assert.False(s.IsActive);
        Assert.InRange(s.Value, 99.5, 100.5);
    }

    [Fact]
    public void Underdamped_ConvergesOnTime_NoRunaway()
    {
        // 展开手感：ζ=0.82 / response 0.52s，应在约 1s 内收敛，且全程有界
        var s = IOSSpring.Create(0.82, 0.52, from: 0, to: 340);
        var maxAbs = 0.0;
        for (var i = 0; i < 240 && s.IsActive; i++)
        {
            s.Tick(Dt);
            maxAbs = Math.Max(maxAbs, Math.Abs(s.Value));
        }
        Assert.False(s.IsActive);
        Assert.True(maxAbs < 345, $"overshoot too large: {maxAbs}");
    }

    [Fact]
    public void Retarget_KeepsPositionContinuous()
    {
        // 动画中途打断：改目标前后值连续（无跳变），最终收敛到新目标
        var s = IOSSpring.Create(0.82, 0.5, from: 0, to: 100);
        for (var i = 0; i < 30; i++) s.Tick(Dt); // 大约 0.25s 后打断
        var before = s.Value;
        var vBefore = s.Velocity;
        Assert.True(s.IsActive);
        Assert.InRange(before, 5, 105); // ζ=0.82 允许 ~1% 设计过冲

        s.Retarget(0); // 反向收回
        Assert.Equal(before, s.Value, 6);          // 位置不跳
        Assert.Equal(vBefore, s.Velocity, 6);      // 速度不跳

        var prev = s.Value;
        s.Tick(Dt);
        Assert.InRange(Math.Abs(s.Value - prev), 0.0, 5.0); // 首帧运动连续（不突变）

        Drain(s, 2.0);
        Assert.False(s.IsActive);
        Assert.InRange(s.Value, -0.5, 0.5);
    }

    [Fact]
    public void CriticalDamped_NoOvershoot()
    {
        var s = IOSSpring.Create(1.0, 0.4, from: 0, to: 1);
        while (s.IsActive) s.Tick(Dt);
        Assert.False(s.IsActive);
        Assert.Equal(1.0, s.Value, 6);
        // 全程单调不越过目标
        var s2 = IOSSpring.Create(1.0, 0.4, from: 0, to: 1);
        for (var i = 0; i < 240 && s2.IsActive; i++)
        {
            s2.Tick(Dt);
            Assert.True(s2.Value <= 1.0 + 1e-9, "critical damped overshot");
        }
    }

    [Fact]
    public void Overdamped_NoOvershoot()
    {
        var s = IOSSpring.Create(1.3, 0.4, from: 0, to: 1);
        for (var i = 0; i < 600 && s.IsActive; i++)
        {
            s.Tick(Dt);
            Assert.True(s.Value <= 1.0 + 1e-9, "overdamped overshot");
        }
        Assert.False(s.IsActive);
    }

    [Fact]
    public void InitialVelocity_PushesValueInVelocityDirection()
    {
        // 目标在反方向，但初始速度很大 → 先沿速度方向运动（iOS 打断语义的关键）
        var s = IOSSpring.Create(0.9, 0.5, from: 0, to: 0, initialVelocity: 500);
        s.Tick(Dt);
        Assert.True(s.Value > 1, "initial velocity should push value first");
        s.Stop();
    }

    [Fact]
    public void RetargetOnInactive_StartsFromCurrentValue()
    {
        var s = IOSSpring.Create(1.0, 0.3, from: 10, to: 20);
        Drain(s, 1.0);
        Assert.False(s.IsActive);
        s.Retarget(30); // 已静止后改目标：从当前值重新出发
        Assert.True(s.IsActive);
        Assert.Equal(20, s.Value, 6);
        Drain(s, 1.0);
        Assert.InRange(s.Value, 29.5, 30.5);
    }

    [Fact]
    public void Ticker_UnhooksAfterAllSpringsSettle()
    {
        var s = IOSSpring.Create(1.0, 0.2, from: 0, to: 1);
        Assert.True(SpringTicker.ActiveCount > 0);
        s.Complete(); // 强制完成 → 从驱动器摘除
        Assert.Equal(0, SpringTicker.ActiveCount);
    }
}
