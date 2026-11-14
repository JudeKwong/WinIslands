using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>xiaotang-daozai 页面切页弹簧：验证 46px 滑入、连点打断与帧率一致性。</summary>
public sealed class DaozaiPageSpringTests
{
    private static void Advance(DaozaiPageSpring spring, double dt, double seconds)
    {
        var frames = (int)Math.Round(seconds / dt);
        for (var i = 0; i < frames; i++) spring.Step(dt);
    }

    [Fact]
    public void PageSlide_ReachesTargetWithinDaozaiTransitionWindow()
    {
        var spring = new DaozaiPageSpring(0.0, 260.0, 0.92);
        spring.Start(46.0, 0.0);
        Advance(spring, 1.0 / 60.0, 0.55);

        Assert.InRange(spring.Position, -0.05, 0.05);
        Assert.True(Math.Abs(spring.Velocity) < 0.1);

        Advance(spring, 1.0 / 60.0, 0.3);
        Assert.True(spring.Settled(46.0));
    }

    [Fact]
    public void PageSlide_IsFrameRateIndependent()
    {
        var slow = new DaozaiPageSpring(0.0, 260.0, 0.92);
        var fast = new DaozaiPageSpring(0.0, 260.0, 0.92);
        slow.Start(46.0, 0.0);
        fast.Start(46.0, 0.0);

        Advance(slow, 1.0 / 30.0, 0.55);
        Advance(fast, 1.0 / 144.0, 0.55);

        Assert.InRange(Math.Abs(slow.Position - fast.Position), 0.0, 0.01);
    }

    [Fact]
    public void PageDot_UndershootsSlightlyAndSettles()
    {
        var dot = new DaozaiPageSpring(4.0, 380.0, 0.86);
        dot.Retarget(16.0);
        Advance(dot, 1.0 / 60.0, 0.55);

        Assert.InRange(dot.Position, 14.0, 17.0);
        Advance(dot, 1.0 / 60.0, 0.5);
        Assert.True(dot.Settled(16.0));
        Assert.InRange(dot.Position, 15.9, 16.1);
    }
    [Fact]
    public void Begin_PreservesVelocityForInterruptedPageTransition()
    {
        var spring = new DaozaiPageSpring(0.0, 260.0, 0.92);
        spring.Start(46.0, 0.0);
        spring.Step(1.0 / 60.0);
        var velocity = spring.Velocity;

        spring.Begin(-46.0, 0.0);

        Assert.Equal(-46.0, spring.Position);
        Assert.Equal(velocity, spring.Velocity);
    }
}
