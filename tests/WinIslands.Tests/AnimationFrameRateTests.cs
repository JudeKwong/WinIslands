using WinIslands.UI;

namespace WinIslands.Tests;

public sealed class AnimationFrameRateTests
{
    [Theory]
    [InlineData(0x00000000, 60)]
    [InlineData(0x00010000, 60)]
    [InlineData(0x00020000, 120)]
    [InlineData(0x00030000, 120)]
    public void Resolve_UsesHardwareTier(int tier, int expected)
        => Assert.Equal(expected, AnimationFrameRate.Resolve(tier));

    [Fact]
    public void Current_LowPowerAlwaysUsesSixty()
        => Assert.Equal(60, AnimationFrameRate.Current(lowPowerMode: true));

    [Theory]
    [InlineData(120, 80, 60, 120)]
    [InlineData(0, 80, 60, 80)]
    [InlineData(0, 0, 60, 60)]
    public void ResolveAnimationFrom_PreservesCurrentVisualPosition(double actual, double current, double fallback, double expected)
        => Assert.Equal(expected, WinIslands.UI.IslandWindow.ResolveAnimationFrom(actual, current, fallback));

    [Fact]
    public void ShouldProcessFrame_FirstFrameIsImmediate()
    {
        var next = 1.0;
        Assert.True(AnimationFrameRate.ShouldProcessFrame(1.0, ref next, 120));
        Assert.Equal(1.0 + 1.0 / 120.0, next, 6);
    }

    [Fact]
    public void ShouldProcessFrame_RejectsFramesInsideBudget()
    {
        var next = 1.0;
        Assert.True(AnimationFrameRate.ShouldProcessFrame(1.0, ref next, 120));
        Assert.False(AnimationFrameRate.ShouldProcessFrame(1.004, ref next, 120));
        Assert.Equal(1.0 + 1.0 / 120.0, next, 6);
    }

    [Fact]
    public void ShouldProcessFrame_ResynchronizesAfterStall()
    {
        var next = 1.0;
        Assert.True(AnimationFrameRate.ShouldProcessFrame(2.0, ref next, 120));
        Assert.Equal(2.0 + 1.0 / 120.0, next, 6);
    }

    [Fact]
    public void ShouldProcessFrame_NonFiniteClock_PassesThroughWithoutPollutingDeadline()
    {
        // 2.4.3: NaN/+Inf clock inputs must process the frame (return true)
        // and leave the deadline untouched - otherwise +Inf would poison
        // nextFrameSeconds forever and every later frame would be rejected,
        // silently freezing karaoke/low-power pacing with no self-recovery.
        var next = 1.0;
        Assert.True(AnimationFrameRate.ShouldProcessFrame(double.NaN, ref next, 120));
        Assert.Equal(1.0, next, 9); // deadline not corrupted
        Assert.True(AnimationFrameRate.ShouldProcessFrame(double.PositiveInfinity, ref next, 120));
        Assert.Equal(1.0, next, 9); // deadline still not corrupted
        // a valid clock right after resumes normal pacing from the intact deadline
        Assert.True(AnimationFrameRate.ShouldProcessFrame(1.0, ref next, 120));
        Assert.Equal(1.0 + 1.0 / 120.0, next, 9);
        // and steady-state throttling still rejects in-budget frames afterwards
        Assert.False(AnimationFrameRate.ShouldProcessFrame(1.004, ref next, 120));
    }


    [Fact]
    public void LowPowerCeilingConstant_IsSixty()
        => Assert.Equal(60, AnimationFrameRate.StandardForLowPower);

    [Theory]
    [InlineData(30, 1.0 / 30.0)]
    [InlineData(60, 1.0 / 60.0)]
    [InlineData(120, 1.0 / 120.0)]
    public void ShouldProcessFrame_ConstantInterval(int fps, double interval)
    {
        var next = 1.0;
        Assert.True(AnimationFrameRate.ShouldProcessFrame(1.0, ref next, fps));
        Assert.Equal(1.0 + interval, next, 9);
        Assert.True(AnimationFrameRate.ShouldProcessFrame(1.0 + interval, ref next, fps));
        Assert.Equal(1.0 + 2.0 * interval, next, 9);
    }

    [Theory]
    [InlineData(20, 1.0 / 30.0)]   // 低于下限 -> Clamp 到 30
    [InlineData(75, 1.0 / 75.0)]   // 非档位值 -> 直接除法
    [InlineData(240, 1.0 / 120.0)] // 高于上限 -> Clamp 到 120
    public void ShouldProcessFrame_UnlistedRateFallsBackToClampedInterval(int fps, double interval)
    {
        var next = 1.0;
        Assert.True(AnimationFrameRate.ShouldProcessFrame(1.0, ref next, fps));
        Assert.Equal(1.0 + interval, next, 9);
    }

}

