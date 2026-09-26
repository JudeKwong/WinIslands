using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class MemoryOptimizerTests
{
    private const long Mb = 1024L * 1024L;
    private const long Interval = 3L * 60L * 1000L;

    [Fact]
    public void ShouldTrim_RequiresBothMemoryThresholds()
    {
        Assert.False(MemoryOptimizer.ShouldTrim(200 * Mb, 139 * Mb, 0, Interval));
        Assert.False(MemoryOptimizer.ShouldTrim(159 * Mb, 200 * Mb, 0, Interval));
    }

    [Fact]
    public void ShouldTrim_AllowsWhenHighAndIntervalElapsed()
        => Assert.True(MemoryOptimizer.ShouldTrim(160 * Mb, 140 * Mb, 0, Interval));

    [Fact]
    public void ShouldTrim_ThrottlesRepeatedRequests()
        => Assert.False(MemoryOptimizer.ShouldTrim(200 * Mb, 200 * Mb, 0, Interval - 1));
}