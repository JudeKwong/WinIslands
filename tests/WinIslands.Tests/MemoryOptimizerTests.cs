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

    [Fact]
    public void ShouldTrim_Idle_UsesLowerThresholdsAndShorterInterval()
    {
        // 2.2.12: idle=true trims at WS>=64MB && private>=28MB after 60s,
        // where the active path (96/40/120s) would still refuse
        const long idleInterval = 60L * 1000L;
        Assert.True(MemoryOptimizer.ShouldTrim(66 * Mb, 30 * Mb, 0, idleInterval, idle: true));
        Assert.False(MemoryOptimizer.ShouldTrim(66 * Mb, 30 * Mb, 0, idleInterval, idle: false));
        Assert.False(MemoryOptimizer.ShouldTrim(63 * Mb, 30 * Mb, 0, idleInterval, idle: true));   // WS below idle bar
        Assert.False(MemoryOptimizer.ShouldTrim(66 * Mb, 27 * Mb, 0, idleInterval, idle: true));   // private below idle bar
    }

    [Fact]
    public void ShouldTrim_Idle_StillThrottled()
    {
        // 2.2.12: idle mode keeps its own 60s throttle, so repeated requests are still debounced
        const long idleInterval = 60L * 1000L;
        Assert.False(MemoryOptimizer.ShouldTrim(200 * Mb, 200 * Mb, 0, idleInterval - 1, idle: true));
        Assert.True(MemoryOptimizer.ShouldTrim(200 * Mb, 200 * Mb, 0, idleInterval, idle: true));
    }

    [Fact]
    public void ShouldTrim_DefaultOverload_MatchesActivePath()
    {
        // 2.2.12: the 4-arg overload keeps the exact active thresholds for callers who do not opt into idle mode
        const long activeInterval = Interval;
        Assert.False(MemoryOptimizer.ShouldTrim(200 * Mb, 39 * Mb, 0, activeInterval));
        Assert.True(MemoryOptimizer.ShouldTrim(97 * Mb, 41 * Mb, 0, activeInterval));
        Assert.Equal(
            MemoryOptimizer.ShouldTrim(97 * Mb, 41 * Mb, 0, activeInterval),
            MemoryOptimizer.ShouldTrim(97 * Mb, 41 * Mb, 0, activeInterval, idle: false));
    }
}
