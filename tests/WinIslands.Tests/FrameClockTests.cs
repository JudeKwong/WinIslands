using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 弹簧帧节拍器测试（2.2.7）：
/// 正常节奏平滑推进 / 低功耗 60FPS 降频 / 挂起恢复巨帧自动重同步 / 时钟倒退与基线重置防护。
/// </summary>
public sealed class FrameClockTests
{
    private const double Frame120 = 1.0 / 120.0;

    private static FrameClock New()
    {
        var fc = new FrameClock();
        fc.ResetBaseline(0.0);
        return fc;
    }

    [Fact]
    public void NormalCadence_AdvancesSmoothly()
    {
        var fc = New();
        var now = 0.0;
        var dtSum = 0.0;
        var lastDt = -1.0;
        for (var i = 0; i < 240; i++)
        {
            now += Frame120;
            var dt = fc.Step(now, capAt60Fps: false);
            Assert.True(dt is > 0 and <= Frame120 * 2.0, $"dt out of range: {dt}");
            dtSum += dt;
            if (i >= 200) lastDt = dt;
        }
        Assert.InRange(lastDt, Frame120 * 0.8, Frame120 * 1.2); // 稳定在 120fps 节奏
        Assert.InRange(dtSum, 1.8, 2.2); // 2 秒内推进的总时长 ≈ 2 秒（不掉帧、不积压）
    }

    [Fact]
    public void HugeGap_ResyncsBaseline_NoBacklog()
    {
        var fc = New();
        var now = 0.0;
        for (var i = 0; i < 10; i++) { now += Frame120; fc.Step(now, false); }

        // 挂起 5 秒后恢复：第一帧必须回退到小步长，不积压回放
        now += 5.0;
        var dt1 = fc.Step(now, false);
        Assert.InRange(dt1, 0.0001, 0.05);

        // 后续帧立即恢复正常节奏
        var dt2 = fc.Step(now + Frame120, false);
        Assert.InRange(dt2, Frame120 * 0.5, Frame120 * 2.0);
    }

    [Fact]
    public void LowPowerCap_TicksAt60Fps()
    {
        var fc = New();
        var now = 0.0;
        var ticked = 0;
        for (var i = 0; i < 240; i++) // 2 秒 @120Hz 采样
        {
            now += Frame120;
            if (fc.Step(now, capAt60Fps: true) > 0) ticked++;
        }
        Assert.InRange(ticked, 110, 130); // ≈120 帧实际推进 = 60FPS
    }

    [Fact]
    public void BackwardsClock_ClampedToSmallStep()
    {
        var fc = New();
        var dt = fc.Step(-1.0, false); // 时钟倒退：不产生负步长或巨步长
        Assert.True(dt > 0 && dt <= 1.0 / 30.0, $"dt={dt}");
    }

    [Fact]
    public void ResetBaseline_StartsSmall()
    {
        var fc = New();
        fc.Step(1.0, false);          // 先走一大步（触发重同步）
        fc.ResetBaseline(2.0);        // 显式重置基线（系统恢复回调路径）
        var now = 2.0 + Frame120;
        var dt = fc.Step(now, false);
        Assert.InRange(dt, Frame120 * 0.5, Frame120 * 2.0);
    }
    [Fact]
    public void FirstStep_AfterColdStart_IsPaced_NoBurst()
    {
        // 2.2.19: a slow first frame (compositor hiccup right at animation
        // kick-off) must not make the spring jump once - the first step is
        // paced to a small fixed step, never the raw wall-clock gap.
        var fc = New();
        var dt = fc.Step(0.20, false); // 200ms gap on the very first frame
        Assert.True(dt > 0 && dt <= 1.0 / 60.0, string.Format("first step must be paced, got {0}", dt));
        // the cadence ramps back up on the next frame (no backlog replay)
        var dt2 = fc.Step(0.20 + 1.0 / 120.0, false);
        Assert.True(dt2 > 0 && dt2 <= 1.0 / 30.0, string.Format("second step out of range: {0}", dt2));
    }

    [Fact]
    public void LagSpike_IsClamped_NextFrameResumesCadence()
    {
        // 2.3.4: a single laggy frame (~33ms) is smoothed by the EWMA + max-step
        // clamp - it must not replay in one jump, and the very next frame returns
        // to the 120fps cadence.
        var fc = New();
        var now = 0.0;
        for (var i = 0; i < 10; i++) { now += Frame120; fc.Step(now, false); }
        var dtLag = fc.Step(now + Frame120 * 4.0, false); // one 4x-laggy frame
        Assert.True(dtLag > 0 && dtLag <= Frame120 * 3.0, string.Format("lag frame must be clamped, got {0}", dtLag));
        var dtNext = fc.Step(now + Frame120 * 4.0 + Frame120, false);
        Assert.InRange(dtNext, Frame120 * 0.5, Frame120 * 2.0);
    }


    [Fact]
    public void NonFiniteInput_NeverCorruptsPacing()
    {
        // 2.4.2: a NaN clock input must fall back to the fixed small step and
        // never leak NaN/Infinity into the EWMA or spring integrator; the very
        // next normal frame resumes cadence.
        var fc = New();
        var dt = fc.Step(double.NaN, false);
        Assert.True(dt > 0 && dt <= 1.0 / 30.0, string.Format("NaN clock returned {0}", dt));
        var dt2 = fc.Step(1.0 / 120.0, false);
        Assert.True(dt2 > 0 && dt2 <= 1.0 / 30.0, string.Format("post-NaN frame returned {0}", dt2));
    }

    [Fact]
    public void InfinityGap_ResyncsBaselineLikeSuspend()
    {
        // 2.4.2: a +Inf clock gap is a total suspend - it takes the same path
        // as a >0.5s huge frame (baseline rebuilt, small resync step) instead of
        // being clamped into an ordinary 1/60 frame like the old gate.
        var fc = New();
        var now = 0.0;
        for (var i = 0; i < 10; i++) { now += Frame120; fc.Step(now, false); }
        var dt1 = fc.Step(double.PositiveInfinity, false);
        Assert.InRange(dt1, 0.0001, 0.05);
        // after an explicit baseline rebuild the next frame is back at 120fps pace
        fc.ResetBaseline(now + Frame120);
        var dt2 = fc.Step(now + Frame120 + Frame120, false);
        Assert.InRange(dt2, Frame120 * 0.5, Frame120 * 2.0);
    }

}

