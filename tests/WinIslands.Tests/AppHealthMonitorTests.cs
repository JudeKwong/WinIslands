using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

/// <summary>UI 卡死检测 / 重启风暴保护的纯逻辑回归测试。</summary>
public class AppHealthMonitorTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Watchdog_BeforeFirstBeat_IsHealthy()
    {
        var wd = new UiHeartbeatWatchdog();
        Assert.False(wd.HasBeat);
        Assert.Equal(UiHealthStatus.Healthy, wd.Evaluate(T0));
    }

    [Theory]
    [InlineData(0, UiHealthStatus.Healthy)]
    [InlineData(1, UiHealthStatus.Healthy)]
    [InlineData(2.99, UiHealthStatus.Healthy)]
    [InlineData(3, UiHealthStatus.Degraded)]
    [InlineData(11.99, UiHealthStatus.Degraded)]
    [InlineData(12, UiHealthStatus.Unresponsive)]
    [InlineData(120, UiHealthStatus.Unresponsive)]
    public void Watchdog_UsesThresholds(double ageSeconds, UiHealthStatus expected)
    {
        var wd = new UiHeartbeatWatchdog();
        wd.Beat(T0);
        Assert.Equal(expected, wd.Evaluate(T0.AddSeconds(ageSeconds)));
    }

    [Fact]
    public void Watchdog_BeatRecoversAndCountsBeats()
    {
        var wd = new UiHeartbeatWatchdog();
        wd.Beat(T0);
        Assert.Equal(UiHealthStatus.Unresponsive, wd.Evaluate(T0.AddSeconds(20)));
        wd.Beat(T0.AddSeconds(21));
        Assert.Equal(UiHealthStatus.Healthy, wd.Evaluate(T0.AddSeconds(21)));
        Assert.Equal(2, wd.BeatCount);
    }

    [Fact]
    public void Watchdog_ClockGoingBackwards_ClampsAgeToZero()
    {
        var wd = new UiHeartbeatWatchdog();
        wd.Beat(T0);
        Assert.Equal(TimeSpan.Zero, wd.SinceLastBeat(T0.AddSeconds(-5)));
        Assert.Equal(UiHealthStatus.Healthy, wd.Evaluate(T0.AddSeconds(-5)));
    }

    [Fact]
    public void Watchdog_CustomThresholds_AreNormalized()
    {
        var wd = new UiHeartbeatWatchdog(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Equal(wd.DegradedAfter, wd.UnresponsiveAfter);
    }

    [Fact]
    public void RestartStormGuard_BlocksFourthRestartInsideWindow()
    {
        var guard = new RestartStormGuard(3, TimeSpan.FromMinutes(15));
        Assert.True(guard.TryRecord(T0));
        Assert.True(guard.TryRecord(T0.AddMinutes(1)));
        Assert.True(guard.TryRecord(T0.AddMinutes(2)));
        Assert.False(guard.TryRecord(T0.AddMinutes(3)));
        Assert.Equal(3, guard.CountInWindow(T0.AddMinutes(3)));
    }

    [Fact]
    public void RestartStormGuard_ExpiresOldEntries()
    {
        var guard = new RestartStormGuard(2, TimeSpan.FromMinutes(10));
        Assert.True(guard.TryRecord(T0));
        Assert.True(guard.TryRecord(T0.AddMinutes(1)));
        Assert.False(guard.TryRecord(T0.AddMinutes(2)));
        Assert.True(guard.TryRecord(T0.AddMinutes(12))); // 第一次已过期
    }

    [Fact]
    public void RestartStormGuard_IsThreadSafe()
    {
        var guard = new RestartStormGuard(50, TimeSpan.FromMinutes(10));
        var allowed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (guard.TryRecord(T0)) Interlocked.Increment(ref allowed);
        });
        Assert.Equal(50, allowed);
    }
}