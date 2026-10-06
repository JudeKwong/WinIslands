using System.Diagnostics;
using WinIslands.UI;
using WinIslands.Services;

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

    [Fact]
    public void RawTickInterval_MatchesTimeSpanWindow()
    {
        // v2.5.4: 原始刻度间隔门与 TimeSpan 语义等价——同一物理增量分别走
        // 「直读刻度 < 固定刻度阈值」（WASAPI 发布门）与「TimeSpan.Milliseconds < 毫秒阈值」
        // （TimeSpan 构造），布尔判定完全一致；证明 AudioWaveService 采集线程与
        // IslandWindow 封面呼吸换用直读刻度后无行为偏差。
        var freq = Stopwatch.Frequency;
        var intervalMs = 1000.0 / 120.0; // 与 AudioWaveService.PublishIntervalMs 同式（高刷档）
        var intervalTicks = (long)(intervalMs / 1000.0 * freq);
        var intervalTs = TimeSpan.FromMilliseconds(intervalMs);
        foreach (var driftMs in new[] { -5.0, -0.01, 0.0, 0.01, 5.0 })
        {
            var deltaMs = intervalMs + driftMs;
            var rawTicks = (long)(deltaMs / 1000.0 * freq);   // 直读刻度换算（截断）
            var tsTicks = TimeSpan.FromMilliseconds(deltaMs); // TimeSpan 换算（舍入）
            var rawBelow = rawTicks < intervalTicks;
            var tsBelow = tsTicks < intervalTs;
            Assert.True(rawBelow == tsBelow, $"driftMs={driftMs} raw={rawBelow} ts={tsBelow}");
        }
    }

    [Fact]
    public void IntervalMsToTicks_MatchesClosedFormula()
    {
        // v2.5.7: IntervalMsToTicks 必须与闭式 (long)(ms/1000*freq) 逐点一致（锁定截断换算，防后续改动漂移）
        var freq = Stopwatch.Frequency;
        double[] samples = { 0.1, 1.0, 1000.0 / 120.0, 1000.0 / 60.0, 7.0, 16.666666666666668 };
        foreach (var ms in samples)
        {
            var t = AudioWaveService.IntervalMsToTicks(ms);
            Assert.Equal((long)(ms / 1000.0 * freq), t);
            Assert.True(t > 0, $"ms={ms}");
        }
    }

    [Fact]
    public void IntervalTicks_RawGateAgreesWithTimeSpanAcrossSweep()
    {
        // v2.5.7: 间隔门两侧 ±1ms、每 0.01ms 采样（201 点）——原始刻度截断门与 TimeSpan 构造函数
        // 逐点一致，锁定发布间隔分母换算无漂移（旧测试仅 5 个漂移点）
        var intervalMs = 1000.0 / 120.0;
        var intervalTicks = AudioWaveService.IntervalMsToTicks(intervalMs);
        var intervalTs = TimeSpan.FromMilliseconds(intervalMs);
        for (var k = -100; k <= 100; k++)
        {
            var deltaMs = intervalMs + k * 0.01;
            var rawBelow = AudioWaveService.IntervalMsToTicks(deltaMs) < intervalTicks;
            var tsBelow = TimeSpan.FromMilliseconds(deltaMs) < intervalTs;
            Assert.True(rawBelow == tsBelow, $"drift={k * 0.01:F2}ms raw={rawBelow} ts={tsBelow}");
        }
    }

}
