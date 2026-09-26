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
    }}