using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class MemoryOptimizerTests
{
    private const long Mb = 1024L * 1024L;
    private const long Interval = 2L * 60L * 1000L;

    [Fact]
    public void ShouldTrim_RequiresBothMemoryThresholds()
    {
        Assert.False(MemoryOptimizer.ShouldTrim(200 * Mb, 39 * Mb, 0, Interval)); // 私有内存低于 40MB
        Assert.False(MemoryOptimizer.ShouldTrim(95 * Mb, 200 * Mb, 0, Interval));  // 工作集低于 96MB
    }

    [Fact]
    public void ShouldTrim_AllowsWhenHighAndIntervalElapsed()
        => Assert.True(MemoryOptimizer.ShouldTrim(97 * Mb, 41 * Mb, 0, Interval));   // 双阈值均达线

    [Fact]
    public void ShouldTrim_ThrottlesRepeatedRequests()
        => Assert.False(MemoryOptimizer.ShouldTrim(200 * Mb, 200 * Mb, 0, Interval - 1));
}