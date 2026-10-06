using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media;

namespace WinIslands.UI;

/// <summary>
/// 真实 iOS 弹簧物理引擎（阻尼简谐振荡器，解析解）。
///
/// 与旧的 SpringEase 不同：SpringEase 是把弹簧形状硬映射到固定时长上（时长到了就钳制），
/// 这不是 iOS 的做法。iOS 的弹簧是物理模拟——从初始位置/速度出发自由振荡，
/// 直到自然收敛（速度≈0 才结束），并且动画可以被中途打断，打断时以当前值 + 当前速度
/// 继续运动（速度连续、不跳变）。本类实现了这套完整模型。
///
/// 参数采用 UIKit 惯例：
///   DampingRatio（阻尼比 ζ）—— ζ<1 欠阻尼（轻微过冲回弹，iOS 展开手感），
///                                ζ=1 临界阻尼（无回弹，文本/透明度过渡），
///                                ζ>1 过阻尼（无回弹、更慢）。
///   Response（响应时长，秒）  —— 感知上的收敛时长，换算成固有频率 ω0。
/// </summary>
public sealed class IOSSpring
{
    // ── 物理参数（由 Configure 换算） ─────────────────────────────
    public double Mass { get; private set; } = 1;
    /// <summary>固有角频率 ω0 (rad/s)。</summary>
    public double Omega0 { get; private set; }
    /// <summary>阻尼比 ζ。</summary>
    public double Zeta { get; private set; }
    /// <summary>阻尼角频率 ωd = ω0·√(1−ζ²)。</summary>
    public double OmegaD { get; private set; }

    // ── 运行状态 ────────────────────────────────────────────────
    /// <summary>当前值（目标单位，如像素/透明度）。</summary>
    public double Value { get; private set; }
    /// <summary>当前速度（单位/秒）。</summary>
    public double Velocity { get; private set; }
    /// <summary>目标值（平衡位置）。</summary>
    public double Target { get; private set; }
    public bool IsActive { get; private set; }

    // 当前一次求解的初始条件与流逝时间
    private double _elapsed;
    private double _y0;   // 初始偏移（相对当前 Target）
    private double _v0;   // 初始速度

    private Action<double>? _onUpdate;
    private Action? _onCompleted;
    private bool _notifyCompleted;

    // 收敛判定阈值（单位：目标值单位）
    private double _settleOffsetEpsilon = 0.5;
    private double _settleVelocityEpsilon = 2.5;

    // 2.0.8：当前运动的自适应收敛阈值（按跨度缩小）。
    // iOS 弹簧收敛到目标后才结束；固定阈值对小量程（如透明度 0~1）会在半途被钳制瞬移，造成"尾部跳变"。
    // 这里按当前运动跨度线性缩放，像素级大跨度保持原阈值，小跨度收紧，让每个弹簧真正收敛到自然终点。
    private double _offsetEps = 0.5;
    private double _velEps = 2.5;

    /// <summary>创建并按 UIKit 参数配置弹簧并启动。</summary>
    public static IOSSpring Create(double dampingRatio, double responseSeconds,
        double from, double to, Action<double>? onUpdate = null, Action? onCompleted = null,
        double initialVelocity = 0, double mass = 1)
    {
        var s = new IOSSpring();
        s.Configure(dampingRatio, responseSeconds, mass);
        s.SetCallbacks(onUpdate, onCompleted);
        s.Start(from, to, initialVelocity);
        return s;
    }

    /// <summary>换算物理参数。response 秒为感知收敛时长。</summary>
    public void Configure(double dampingRatio, double responseSeconds, double mass = 1)
    {
        Mass = Math.Max(0.01, mass);
        Zeta = Math.Clamp(dampingRatio, 0.01, 2.0);
        var response = Math.Max(0.03, responseSeconds);

        // 标准 iOS 换算：ωd = 2π/response，ω0 = ωd/√(1−ζ²)；ζ≥1 时取 ω0 = 2π/response。
        if (Zeta < 1.0)
        {
            var root = Math.Sqrt(1 - Zeta * Zeta);
            Omega0 = (2 * Math.PI / response) / root;
            OmegaD = 2 * Math.PI / response;
        }
        else
        {
            Omega0 = 2 * Math.PI / response;
            OmegaD = 0;
        }
    }

    /// <summary>设置逐帧回调与结束回调（可在 Start 之前或之后调用）。</summary>
    public void SetCallbacks(Action<double>? onUpdate, Action? onCompleted)
    {
        _onUpdate = onUpdate;
        _onCompleted = onCompleted;
    }

    /// <summary>开始运动：以 from/初速度 向 to 收敛。若已在运动中则以当前值/速度为初始条件改目标（速度连续打断）。</summary>
    public void Start(double from, double to, double initialVelocity = 0)
    {
        Value = from;
        Velocity = initialVelocity;
        Target = to;
        _elapsed = 0;
        _y0 = from - to;
        _v0 = initialVelocity;
        UpdateEpsilon(Math.Abs(from - to));
        _notifyCompleted = false;
        IsActive = true;
        SpringTicker.Add(this);
    }

    /// <summary>中途改目标：以当前 Value/Velocity 作为新初始条件，向新目标继续运动（iOS 打断语义）。</summary>
    public void Retarget(double to)
    {
        if (!double.IsFinite(to)) return; // 防御：非法目标忽略，防止数值污染扩散
        if (!IsActive)
        {
            Start(Value, to, 0);
            return;
        }
        _elapsed = 0;
        _y0 = Value - to;
        _v0 = Velocity;
        UpdateEpsilon(Math.Abs(Value - to));
        Target = to;
        _notifyCompleted = false;
    }

    /// <summary>手动结束并停在当前值。</summary>
    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        SpringTicker.Remove(this);
    }

    /// <summary>强制瞬移到目标并结束。</summary>
    public void Complete()
    {
        if (!IsActive) return;
        var done = Value != Target;
        Value = Target;
        Velocity = 0;
        IsActive = false;
        SpringTicker.Remove(this);
        // 2.0.8：收敛/强制结束时把最终值写回 UI，避免界面停留在最后一帧的旧值上
        // （旧版 Complete 不回调，元素会停在收敛前一刻的数值，视觉上略有残留）。
        if (done)
        {
            try { _onUpdate?.Invoke(Target); } catch { /* 单帧回调异常不影响引擎 */ }
        }
        if (_notifyCompleted) return;
        _notifyCompleted = true;
        try { _onCompleted?.Invoke(); } catch { /* 由调用方兜底 */ }
    }

    // ── 每帧推进（由 SpringTicker 调用） ──────────────────────────
    internal void Tick(double dt)
    {
        if (!IsActive) return;
                // 2.2.2: clamp single-frame dt to 0.1s so a lag spike or debugger
        // resume cannot push the analytic solution into a huge jump.
        if (!double.IsFinite(dt) || dt <= 0) return;
        _elapsed += Math.Min(dt, 0.1);
        Solve(_elapsed);

        // 数值防护：任何 NaN/Inf 都不允许进入 UI 回调或收敛判定，静默停止避免污染扩散
        if (!double.IsFinite(Value) || !double.IsFinite(Velocity))
        {
            Value = double.IsFinite(Target) ? Target : 0;
            Velocity = 0;
            IsActive = false;
            SpringTicker.Remove(this);
            return;
        }

        // 收敛判定：偏移与速度都足够小（阈值按运动跨度自适应）→ 瞬移到目标并结束
        if (Math.Abs(Value - Target) < _offsetEps && Math.Abs(Velocity) < _velEps)
        {
            Complete();
            return;
        }
        try { _onUpdate?.Invoke(Value); } catch { /* 单帧回调异常不影响引擎 */ }
    }

    /// <summary>按当前运动跨度更新收敛阈值（2.0.8）。</summary>
    private void UpdateEpsilon(double span)
    {
        if (!double.IsFinite(span) || span < 0)
        {
            _offsetEps = _settleOffsetEpsilon;
            _velEps = _settleVelocityEpsilon;
            return;
        }
        // span≥100 单位（像素级尺寸/位移）→ 保持原阈值 0.5 / 2.5；
        // span<100（如透明度 0~1、缩放、圆角小变化）→ 按比例收紧，弹簧真正收敛到自然终点。
        var s = Math.Min(1.0, span / 100.0);
        _offsetEps = Math.Max(0.0005, _settleOffsetEpsilon * s);
        _velEps = Math.Max(0.05, _settleVelocityEpsilon * s);
    }
    /// <summary>阻尼简谐振荡器解析解：由初始偏移 y0、初速 v0 求 t 时刻的偏移与速度。</summary>
    private void Solve(double t)
    {
        if (Zeta < 1.0 - 1e-9)
        {
            // 欠阻尼：y = e^(−ζω0t)·(A·cos(ωd·t) + B·sin(ωd·t))
            var alpha = Zeta * Omega0;
            var a = _y0;
            var b = (_v0 + alpha * _y0) / OmegaD;
            var decay = Math.Exp(-alpha * t);
            var ct = Math.Cos(OmegaD * t);
            var st = Math.Sin(OmegaD * t);
            var y = decay * (a * ct + b * st);
            var v = decay * ((b * OmegaD - a * alpha) * ct - (a * OmegaD + b * alpha) * st);
            Value = Target + y;
            Velocity = v;
        }
        else if (Zeta > 1.0 + 1e-9)
        {
            // 过阻尼：y = C1·e^(−λ1·t) + C2·e^(−λ2·t)
            var root = Math.Sqrt(Zeta * Zeta - 1);
            var l1 = Omega0 * (Zeta + root);
            var l2 = Omega0 * (Zeta - root);
            var c2 = (_v0 + l1 * _y0) / (l1 - l2);
            var c1 = _y0 - c2;
            var e1 = Math.Exp(-l1 * t);
            var e2 = Math.Exp(-l2 * t);
            var y = c1 * e1 + c2 * e2;
            var v = -l1 * c1 * e1 - l2 * c2 * e2;
            Value = Target + y;
            Velocity = v;
        }
        else
        {
            // 临界阻尼：y = e^(−ω0·t)·(y0 + (v0 + ω0·y0)·t)
            var k = _v0 + Omega0 * _y0;
            var decay = Math.Exp(-Omega0 * t);
            var y = decay * (_y0 + k * t);
            var v = decay * (_v0 - k * Omega0 * t);
            Value = Target + y;
            Velocity = v;
        }
    }
}

/// <summary>
/// 弹簧帧驱动器：所有活跃弹簧共享一个 CompositionTarget.Rendering 钩子，
/// 空闲时自动摘钩（CPU≈0），有动画时以合成帧率（60/120Hz）推进，天然 60fps+。
/// </summary>
public static class SpringTicker
{
    private static readonly List<IOSSpring> _active = new();
    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static readonly FrameClock _frameClock = new();  // 2.2.7：帧节拍状态机（平滑/降频/挂起重同步）
    private static bool _hooked;
    /// <summary>低功耗模式：将弹簧动画帧率上限降至 60 FPS（App 在设置变化时更新）。</summary>
    public static bool CapAt60Fps;

    public static int ActiveCount => _active.Count;

    internal static void Add(IOSSpring spring)
    {
        if (!_hooked)
        {
            _frameClock.ResetBaseline(_clock.Elapsed.TotalSeconds); // 新的动画会话：重建平滑基线
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
        if (!_active.Contains(spring)) _active.Add(spring);
    }

    internal static void Remove(IOSSpring spring)
    {
        _active.Remove(spring);
        if (_active.Count == 0 && _hooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }

    /// <summary>系统挂起/恢复后由应用层调用：立即重建帧节拍基线，动画从恢复后的第一帧平滑起步、不巨跳。</summary>
    public static void ResetBaseline() => _frameClock.ResetBaseline(_clock.Elapsed.TotalSeconds);

    /// <summary>立即完成所有活跃弹簧（窗口关闭时清理）。</summary>
    public static void CompleteAll()
    {
        var all = _active.ToArray();
        foreach (var s in all) s.Complete();
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (_active.Count == 0) return;
        // 2.2.7：帧间隔状态机收敛到 FrameClock（EWMA 平滑 / 低功耗 60FPS 降频 / 挂起巨帧自动重同步）
        var dt = _frameClock.Step(_clock.Elapsed.TotalSeconds, CapAt60Fps);
        if (dt <= 0) return; // 低功耗降频跳过本帧

        // 倒序遍历，允许 Tick 内部 Complete 摘除
        for (var i = _active.Count - 1; i >= 0; i--)
            _active[i].Tick(dt);
    }
}
