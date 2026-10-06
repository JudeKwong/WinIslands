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
    public void TicksScale_MatchesTimeSpanConversion()
    {
        // 2.5.1/2.5.2: 直读刻度换算必须与 TimeSpan 构造路径一致——同一 tick 值
        // 分别走「直接除以频率」与「(long)(ticks * TicksPerSecond/Frequency) → TotalSeconds」，
        // 避免两个独立时钟读数之间的竞态；TimeSpan 路径按 100ns 刻度 (long) 截断，
        // 直接除法（double）实际更精确，截断损失 ≤ 1e-7s。
        var sw = Stopwatch.StartNew();
        Thread.Sleep(25);
        var ticks = sw.ElapsedTicks;
        var fromTicks = ticks / (double)Stopwatch.Frequency;
        var fromTimeSpan = new TimeSpan((long)(ticks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency))).TotalSeconds;
        Assert.True(double.IsFinite(fromTicks) && fromTicks > 0);
        Assert.True(Math.Abs(fromTimeSpan - fromTicks) < 1e-6);
    }
}
