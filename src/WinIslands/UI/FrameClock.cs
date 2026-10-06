using System;

namespace WinIslands.UI;

/// <summary>
/// 弹簧帧节拍器（2.2.7 从 SpringTicker 抽出）：单一渲染钩子内的帧间隔状态机。
/// 职责：帧间隔平滑（EWMA）、低功耗 60 FPS 降频、巨帧/挂起恢复自动重同步。
/// 纯逻辑、无 UI 依赖，便于单元测试；SpringTicker 每帧调用 <see cref="Step"/>。
/// </summary>
internal sealed class FrameClock
{
    private double _lastSeconds;
    private double _smoothDt;      // 帧间隔指数移动平均（秒），掉帧时弹簧步伐平滑用
    private double _nextFrameTime; // 低功耗降频的帧截止时刻

    /// <summary>一帧超过该秒数视为“挂起/调试断点/合成器冻结”恢复：直接重建基线，不再回放积压。</summary>
    private const double SuspendGapSeconds = 0.5;

    /// <summary>2.2.19: cold-start pacing - the first step after ResetBaseline
    /// (fresh animation session; a compositor hiccup right at expand/collapse
    /// kick-off) is capped at this small fixed step so a slow first frame can
    /// never make the spring jump once. The EWMA baseline is seeded from the
    /// paced step (matching how iOS CoreAnimation starts its first frame from
    /// a known pose).</summary>
    private const double WarmUpCapSeconds = 1.0 / 120.0;

    /// <summary>重建帧节拍基线（新动画会话 / 系统恢复时调用）。</summary>
    public void ResetBaseline(double now)
    {
        _lastSeconds = now;
        _smoothDt = 0;
        _nextFrameTime = now;
    }

    /// <summary>
    /// 推进一帧，返回用于弹簧积分的实际步长（秒）。
    /// 低功耗降频跳过本帧时返回 0；挂起/巨帧恢复时自动重同步并返回一小步长。
    /// </summary>
    public double Step(double now, bool capAt60Fps)
    {
        if (capAt60Fps && !AnimationFrameRate.ShouldProcessFrame(now, ref _nextFrameTime, AnimationFrameRate.StandardForLowPower))
        {
            _lastSeconds = now; // 跳过的帧也推进基准，避免恢复后 dt 巨帧
            return 0.0;
        }

        var dt = now - _lastSeconds;
        _lastSeconds = now;
        // 防御：时钟倒退 / 非有限值 → 一小步，绝不产生负步长或巨步长
        if (!double.IsFinite(dt) || dt <= 0) dt = 1.0 / 60.0;

        // 挂起恢复 / 调试断点释放：帧间隔超过阈值 → 重建基线并回退到一小步。
        // 旧实现只把单帧 dt 钳到 1/60，但平滑基线仍是挂起前的旧值；
        // 2.2.7 直接重建基线，弹簧从恢复后的第一帧平滑起步、不积压回放。
        if (dt > SuspendGapSeconds)
        {
            ResetBaseline(now);
            dt = 1.0 / 120.0;
        }

        // 帧间隔平滑：偶发一帧卡顿（GC/合成器抖动）时弹簧步伐保持平顺 ——
        // iOS 的 CoreAnimation 由渲染服务统一调度，单帧抖动不会让动画跳一下；
        // 对真实帧间隔做指数移动平均（EWMA），并限制单帧步长不超过均值的 1.5 倍。
        // 2.2.19: ResetBaseline 之后的第一帧若偏慢（合成器刚挂接 / 渲染钩子起步），
        // 先限幅到固定小步（WarmUpCapSeconds），再以此为种子建立 EWMA 基线；
        // 之后节奏照常爬升，动画开头轻柔起步、绝不“冲一下”。
        if (_smoothDt <= 0)
        {
            if (dt > WarmUpCapSeconds) dt = WarmUpCapSeconds;
            _smoothDt = dt;
        }
        else
        {
            _smoothDt += (dt - _smoothDt) * 0.15; // 2.3.4: one fewer multiply-add per compositor frame (identical EWMA)
        }
        var maxStep = Math.Max(1.0 / 240.0, _smoothDt * 1.5);
        if (dt > maxStep) dt = maxStep;
        return dt;
    }
}
