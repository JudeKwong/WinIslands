using System.Diagnostics;
using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.4.6: SpringTicker clock conversion tests.
/// OnRendering / Add / ResetBaseline all go through CurrentSeconds() (raw ticks / constant frequency);
/// it must stay finite, non-negative, monotonic, and scale-consistent with the system stopwatch.
/// </summary>
public sealed class SpringTickerClockTests
{
    [Fact]
    public void CurrentSeconds_IsFiniteNonNegative()
    {
        var s = SpringTicker.CurrentSeconds();
        Assert.True(double.IsFinite(s));
        Assert.True(s >= 0);
    }

    [Fact]
    public void CurrentSeconds_AdvancesMonotonically()
    {
        var a = SpringTicker.CurrentSeconds();
        Thread.Sleep(3);
        var b = SpringTicker.CurrentSeconds();
        Assert.True(b > a);
    }

    [Fact]
    public void CurrentSeconds_ScaleMatchesRawTicks()
    {
        // Same-clock delta comparison: current seconds and system ticks share the same
        // tick source, so their deltas over one window must agree to 1/Frequency scale.
        // (Absolute timestamps are NOT comparable - SpringTicker's clock is relative to
        // process start while GetTimestamp() is an absolute system count.)
        var s1 = SpringTicker.CurrentSeconds();
        var t1 = Stopwatch.GetTimestamp();
        Thread.Sleep(20);
        var t2 = Stopwatch.GetTimestamp();
        var s2 = SpringTicker.CurrentSeconds();
        var dtTicks = t2 - t1;
        Assert.True(dtTicks > 0);
        var ratio = ((s2 - s1) * Stopwatch.Frequency) / dtTicks;
        Assert.InRange(ratio, 0.99, 1.01);
    }
    [Fact]
    public void TicksScale_MatchesElapsedTotalSeconds()
    {
        // 2.5.1: wave clock now reads raw ticks (ElapsedTicks / Frequency) instead of
        // constructing a TimeSpan per frame; the conversion must agree with
        // Elapsed.TotalSeconds within double precision over a real elapsed window.
        var sw = Stopwatch.StartNew();
        Thread.Sleep(25);
        var fromTicks = sw.ElapsedTicks / (double)Stopwatch.Frequency;
        var fromTimeSpan = sw.Elapsed.TotalSeconds;
        Assert.True(double.IsFinite(fromTicks) && fromTicks > 0);
        // TimeSpan 路径内部按 100ns 刻度 (long) 截断，只保证约 1us 级一致；
        // 直读刻度（double）实际更精确，这里验证两条路径不偏离即可。
        Assert.True(Math.Abs(fromTimeSpan - fromTicks) < 1e-6);
    }
}
