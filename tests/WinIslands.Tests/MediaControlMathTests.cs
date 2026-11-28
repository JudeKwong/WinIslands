using WinIslands.UI;

namespace WinIslands.Tests;

public sealed class MediaControlMathTests
{
    [Theory]
    [InlineData(100, 200, 15, 115)]
    [InlineData(100, 200, -15, 85)]
    [InlineData(10, 200, -15, 0)]
    [InlineData(190, 200, 15, 200)]
    [InlineData(0, 0, 15, 0)]
    [InlineData(0, -5, 15, 0)]
    public void SeekTarget_ClampsWithinTrack(double position, double duration, double delta, double expected)
        => Assert.Equal(expected, MediaControlMath.SeekTarget(position, duration, delta));

    [Fact]
    public void SeekTarget_HandlesNaN()
    {
        Assert.Equal(0, MediaControlMath.SeekTarget(double.NaN, 100, double.NaN));
        Assert.Equal(0, MediaControlMath.SeekTarget(50, double.NaN, 15));
        Assert.Equal(15, MediaControlMath.SeekTarget(double.NaN, 100, 15));
    }

    [Fact]
    public void SeekTarget_DoesNotOverflowLargeDurations()
    {
        Assert.Equal(3600, MediaControlMath.SeekTarget(3595, 3600, 15));
        Assert.Equal(0, MediaControlMath.SeekTarget(5, 3600, -15));
    }

    [Theory]
    [InlineData(0.50, 0.05, 0.55)]
    [InlineData(0.50, -0.05, 0.45)]
    [InlineData(0.98, 0.05, 1.0)]
    [InlineData(0.02, -0.05, 0.0)]
    [InlineData(0.50, double.NaN, 0.50)]
    public void VolumeTarget_ClampsWithinUnitRange(double volume, double delta, double expected)
        => Assert.Equal(expected, MediaControlMath.VolumeTarget(volume, delta), 6);

    [Fact]
    public void VolumeTarget_HandlesNaNVolume()
        => Assert.Equal(0.05, MediaControlMath.VolumeTarget(double.NaN, 0.05), 6);
}