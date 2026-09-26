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
}