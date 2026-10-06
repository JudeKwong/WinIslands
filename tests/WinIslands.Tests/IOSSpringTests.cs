using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// iOS 真弹簧物理引擎测试：解析解正确性 / 打断速度连续 / 临界与过阻尼无回弹 / 收敛。
/// </summary>
public sealed class IOSSpringTests
{
    private const double Dt = 1.0 / 120.0; // 120fps 半步长，检验高频稳定性

    private static void Drain(IOSSpring s, double seconds)
    {
        var steps = (int)Math.Round(seconds / Dt);
        for (var i = 0; i < steps && s.IsActive; i++) s.Tick(Dt);
    }

    [Fact]
    public void Underdamped_SettlesToTarget()
    {
        var s = IOSSpring.Create(0.82, 0.5, from: 0, to: 100);
        Drain(s, 2.0);
        Assert.False(s.IsActive);
        Assert.InRange(s.Value, 99.5, 100.5);
    }

    [Fact]
    public void Underdamped_ConvergesOnTime_NoRunaway()
    {
        // 展开手感：ζ=0.82 / response 0.52s，应在约 1s 内收敛，且全程有界
        var s = IOSSpring.Create(0.82, 0.52, from: 0, to: 340);
        var maxAbs = 0.0;
        for (var i = 0; i < 240 && s.IsActive; i++)
        {
            s.Tick(Dt);
            maxAbs = Math.Max(maxAbs, Math.Abs(s.Value));
        }
        Assert.False(s.IsActive);
        Assert.True(maxAbs < 345, $"overshoot too large: {maxAbs}");
    }

    [Fact]
    public void Retarget_KeepsPositionContinuous()
    {
        // 动画中途打断：改目标前后值连续（无跳变），最终收敛到新目标
        var s = IOSSpring.Create(0.82, 0.5, from: 0, to: 100);
        for (var i = 0; i < 30; i++) s.Tick(Dt); // 大约 0.25s 后打断
        var before = s.Value;
        var vBefore = s.Velocity;
        Assert.True(s.IsActive);
        Assert.InRange(before, 5, 105); // ζ=0.82 允许 ~1% 设计过冲

        s.Retarget(0); // 反向收回
        Assert.Equal(before, s.Value, 6);          // 位置不跳
        Assert.Equal(vBefore, s.Velocity, 6);      // 速度不跳

        var prev = s.Value;
        s.Tick(Dt);
        Assert.InRange(Math.Abs(s.Value - prev), 0.0, 5.0); // 首帧运动连续（不突变）

        Drain(s, 2.0);
        Assert.False(s.IsActive);
        Assert.InRange(s.Value, -0.5, 0.5);
    }

    [Fact]
    public void CriticalDamped_NoOvershoot()
    {
        var s = IOSSpring.Create(1.0, 0.4, from: 0, to: 1);
        while (s.IsActive) s.Tick(Dt);
        Assert.False(s.IsActive);
        Assert.Equal(1.0, s.Value, 6);
        // 全程单调不越过目标
        var s2 = IOSSpring.Create(1.0, 0.4, from: 0, to: 1);
        for (var i = 0; i < 240 && s2.IsActive; i++)
        {
            s2.Tick(Dt);
            Assert.True(s2.Value <= 1.0 + 1e-9, "critical damped overshot");
        }
    }

    [Fact]
    public void Overdamped_NoOvershoot()
    {
        var s = IOSSpring.Create(1.3, 0.4, from: 0, to: 1);
        for (var i = 0; i < 600 && s.IsActive; i++)
        {
            s.Tick(Dt);
            Assert.True(s.Value <= 1.0 + 1e-9, "overdamped overshot");
        }
        Assert.False(s.IsActive);
    }

    [Fact]
    public void InitialVelocity_PushesValueInVelocityDirection()
    {
        // 目标在反方向，但初始速度很大 → 先沿速度方向运动（iOS 打断语义的关键）
        var s = IOSSpring.Create(0.9, 0.5, from: 0, to: 0, initialVelocity: 500);
        s.Tick(Dt);
        Assert.True(s.Value > 1, "initial velocity should push value first");
        s.Stop();
    }

    [Fact]
    public void RetargetOnInactive_StartsFromCurrentValue()
    {
        var s = IOSSpring.Create(1.0, 0.3, from: 10, to: 20);
        Drain(s, 1.0);
        Assert.False(s.IsActive);
        s.Retarget(30); // 已静止后改目标：从当前值重新出发
        Assert.True(s.IsActive);
        Assert.Equal(20, s.Value, 6);
        Drain(s, 1.0);
        Assert.InRange(s.Value, 29.5, 30.5);
    }

    [Fact]
    public void Ticker_UnhooksAfterAllSpringsSettle()
    {
        var s = IOSSpring.Create(1.0, 0.2, from: 0, to: 1);
        Assert.True(SpringTicker.ActiveCount > 0);
        s.Complete(); // 强制完成 → 从驱动器摘除
        Assert.Equal(0, SpringTicker.ActiveCount);
    }

    [Fact]
    public void NaN_Input_StopsWithoutCrash()
    {
        // 2.0.7 稳定性：任何 NaN/Inf 输入都必须静默停止，绝不能卡死循环或污染 UI 回调
        var s = IOSSpring.Create(1.0, 0.3, from: 0, to: 10);
        s.Retarget(double.NaN); // 非法目标：应被忽略（防御性）
        Assert.True(s.IsActive);
        Assert.True(double.IsFinite(s.Value), "target NaN must not corrupt position");

        s.Retarget(double.PositiveInfinity);
        Assert.True(double.IsFinite(s.Value), "inf target must not corrupt position");

        // 直接污染运行值（模拟极端数值事故）→ Tick 静默停止
        s.Start(double.NaN, 10);
        s.Tick(Dt);
        Assert.False(s.IsActive);
        Assert.True(double.IsFinite(s.Value));
        s.Complete();
    }

    [Fact]
    public void ComputeCardRuntimeRadius_PillAndExpandedShapes()
    {
        // 紧凑胶囊：圆角 = 高/2（完整药丸）且不超过用户设置
        Assert.Equal(28, IslandWindow.ComputeCardRuntimeRadius(56, 28), 4);
        Assert.Equal(24, IslandWindow.ComputeCardRuntimeRadius(48, 28), 4);
        Assert.Equal(14, IslandWindow.ComputeCardRuntimeRadius(28, 40), 4); // 高度小→按高度
        // 展开大卡片：圆角 = 用户设置（封顶）
        Assert.Equal(28, IslandWindow.ComputeCardRuntimeRadius(300, 28), 4);
        Assert.Equal(40, IslandWindow.ComputeCardRuntimeRadius(400, 99), 4); // 设置越界→钳制
        Assert.Equal(16, IslandWindow.ComputeCardRuntimeRadius(200, 5), 4);  // 设置过低→钳制
        // 极端高度不产生负值/NaN
        Assert.True(double.IsFinite(IslandWindow.ComputeCardRuntimeRadius(double.NaN, 28)));
        Assert.True(IslandWindow.ComputeCardRuntimeRadius(-5, 28) >= 4);
    }
    [Fact]
    public void OpacitySpanSpring_RunsToNaturalEnd_NoEarlySnap()
    {
        // 2.0.8：透明度弹簧（0~1，临界阻尼）不会被固定阈值(0.5)在半路钳制瞬移。
        // 旧行为：offset<0.5 且 vel<2.5 时即 Complete → 最后 ~8% 直接被跳变吸附；
        // 新行为：小跨度阈值按比例收紧，透明度淡入淡出流畅跑到自然终点。
        var s = IOSSpring.Create(1.0, 0.3, from: 0, to: 1);
        for (var i = 0; i < 26; i++) s.Tick(Dt); // 约 0.22s：旧阈值已满足（offset≈0.08, vel≈1/s）
        Assert.True(s.IsActive, "small-span spring must not snap mid-way");

        Drain(s, 2.0);
        Assert.False(s.IsActive);
        Assert.Equal(1.0, s.Value, 6);
    }

    [Fact]
    public void PixelSpanSpring_KeepsOriginalEpsilon()
    {
        // 大跨度（像素尺寸/位移 300px 级）：阈值保持 0.5 / 2.5，收敛速度与旧版一致
        var s = IOSSpring.Create(0.82, 0.5, from: 0, to: 300);
        var steps = 0;
        while (s.IsActive && steps < 400) { s.Tick(Dt); steps++; }
        Assert.False(s.IsActive);
        Assert.InRange(s.Value, 299.5, 300.5);
        Assert.True(steps < 250, $"large-span spring settled too late ({steps} steps)");
    }

    [Fact]
    public void TinyRetarget_TightensEpsilonAndFinishes()
    {
        // 打断到极小跨度：阈值收紧后依然可靠收敛到精确目标
        var s = IOSSpring.Create(1.0, 0.3, from: 10, to: 20);
        Drain(s, 1.0);
        Assert.False(s.IsActive);
        s.Retarget(20.01);
        Assert.True(s.IsActive);
        Drain(s, 1.5);
        Assert.False(s.IsActive);
        Assert.Equal(20.01, s.Value, 6);
    }

    [Fact]
    public void ConfigureThenRetarget_KeepsPositionAndVelocityContinuous()
    {
        // 2.1.6：动画中途「重新配置刚度/阻尼后反向改目标」——与 AnimateCardSpring 的
        // wasAnimating 分支完全一致（先 Configure 再 Retarget）。配置变化不得造成跳变，
        // Retarget 后位置/速度连续，最终平滑收敛到新目标。
        var s = IOSSpring.Create(0.86, 0.62, from: 0, to: 340);
        try
        {
            for (var i = 0; i < 40; i++) s.Tick(Dt); // 展开途中（约 0.33s）
            Assert.True(s.IsActive);
            var before = s.Value;
            var vBefore = s.Velocity;

            s.Configure(0.97, 0.52); // 收紧阻尼、加快响应（收起手感）
            Assert.Equal(before, s.Value, 6);     // Configure 不得改变当前状态
            Assert.Equal(vBefore, s.Velocity, 6);

            s.Retarget(0); // 反向收回
            Assert.Equal(before, s.Value, 6);     // 位置不跳
            Assert.Equal(vBefore, s.Velocity, 6); // 速度不跳

            var prev = s.Value;
            s.Tick(Dt);
            // 首帧不跳变：运动方向与 Retarget 保留的速度一致（幅度由加速度接管，可大）
            Assert.True(Math.Abs(s.Value - prev) < 1e-9 || Math.Sign(s.Value - prev) == Math.Sign(vBefore),
                "first frame must move in retained-velocity direction");
            Assert.True(double.IsFinite(s.Value), "value must stay finite");

            Drain(s, 2.0);
            Assert.False(s.IsActive);
            Assert.InRange(s.Value, -0.5, 0.5);
        }
        finally
        {
            s.Complete(); // 断言失败也不泄漏到静态 SpringTicker，保证其它测试计数正确
        }
    }

    [Fact]
    public void ResolveAnimationFrom_NonFiniteFallsBack()
    {
        // 2.1.6：动画起点解析——ActualWidth 为 0/NaN（首帧未布局）时逐级回退到 Width/目标值，
        // 绝不把 0 或 NaN 作为动画起点导致卡片瞬移/黑框。
        Assert.Equal(300, IslandWindow.ResolveAnimationFrom(0, 300, 400), 6);
        Assert.Equal(400, IslandWindow.ResolveAnimationFrom(0, double.NaN, 400), 6);
        Assert.Equal(400, IslandWindow.ResolveAnimationFrom(double.NaN, double.NaN, 400), 6);
        Assert.Equal(300, IslandWindow.ResolveAnimationFrom(300, 1, 400), 6);
        Assert.Equal(400, IslandWindow.ResolveAnimationFrom(-1, -1, 400), 6); // 非法负值 → 回退
    }
    [Fact]
    public void Tick_IgnoresInvalidDt()
    {
        var s = IOSSpring.Create(0.86, 0.5, from: 0, to: 100);
        s.Tick(-1);
        s.Tick(double.NaN);
        s.Tick(0);
        Assert.True(s.IsActive);
        Assert.Equal(0, s.Value, 6);
        Assert.Equal(0, s.Velocity, 6);
        s.Stop();
    }

    [Fact]
    public void Tick_ClampsGiantDt_StaysFiniteAndSettles()
    {
        var s = IOSSpring.Create(0.82, 0.5, from: 0, to: 100);
        s.Tick(10.0);
        Assert.True(double.IsFinite(s.Value));
        Assert.True(double.IsFinite(s.Velocity));
        Assert.InRange(s.Value, 0.0, 110.0);
        Drain(s, 2.0);
        Assert.False(s.IsActive);
        Assert.InRange(s.Value, 99.5, 100.5);
    }

    // ── 2.2.9：解析解系数缓存（RebuildCoefficients）→ 与旧公式逐步逐点一致（同数值说明正确性）──
    private static (double Value, double Vel) LegacyUnderdamped(double zeta, double response, double from, double to, double v0, double t)
    {
        var omegaD = 2 * Math.PI / response;
        var root = Math.Sqrt(1 - zeta * zeta);
        var omega0 = (2 * Math.PI / response) / root;
        var y0 = from - to;
        var alpha = zeta * omega0;
        var a = y0;
        var b = (v0 + alpha * y0) / omegaD;
        var decay = Math.Exp(-alpha * t);
        var ct = Math.Cos(omegaD * t);
        var st = Math.Sin(omegaD * t);
        var y = decay * (a * ct + b * st);
        var v = decay * ((b * omegaD - a * alpha) * ct - (a * omegaD + b * alpha) * st);
        return (to + y, v);
    }

    private static (double Value, double Vel) LegacyOverdamped(double zeta, double response, double from, double to, double v0, double t)
    {
        var root = Math.Sqrt(zeta * zeta - 1);
        var omega0 = 2 * Math.PI / response;
        var l1 = omega0 * (zeta + root);
        var l2 = omega0 * (zeta - root);
        var y0 = from - to;
        var c2 = (v0 + l1 * y0) / (l1 - l2);
        var c1 = y0 - c2;
        var e1 = Math.Exp(-l1 * t);
        var e2 = Math.Exp(-l2 * t);
        var y = c1 * e1 + c2 * e2;
        var v = -l1 * c1 * e1 - l2 * c2 * e2;
        return (to + y, v);
    }

    private static (double Value, double Vel) LegacyCritical(double response, double from, double to, double v0, double t)
    {
        var omega0 = 2 * Math.PI / response;
        var y0 = from - to;
        var k = v0 + omega0 * y0;
        var decay = Math.Exp(-omega0 * t);
        var y = decay * (y0 + k * t);
        var v = decay * (v0 - k * omega0 * t);
        return (to + y, v);
    }

    [Fact]
    public void CachedCoefficients_Underdamped_MatchesLegacyFormula()
    {
        const double zeta = 0.82, response = 0.5, from = 0, to = 100, v0 = 30;
        var s = IOSSpring.Create(zeta, response, from, to, initialVelocity: v0);
        try
        {
            for (var i = 1; i <= 30 && s.IsActive; i++)
            {
                s.Tick(Dt);
                var t = i * Dt;
                var exp = LegacyUnderdamped(zeta, response, from, to, v0, t);
                Assert.Equal(exp.Value, s.Value, 12);
                Assert.Equal(exp.Vel, s.Velocity, 12);
            }
        }
        finally { s.Stop(); }
    }

    [Fact]
    public void CachedCoefficients_Overdamped_MatchesLegacyFormula()
    {
        const double zeta = 1.6, response = 0.4, from = 0, to = 100, v0 = -20;
        var s = IOSSpring.Create(zeta, response, from, to, initialVelocity: v0);
        try
        {
            for (var i = 1; i <= 40 && s.IsActive; i++)
            {
                s.Tick(Dt);
                var t = i * Dt;
                var exp = LegacyOverdamped(zeta, response, from, to, v0, t);
                Assert.Equal(exp.Value, s.Value, 12);
                Assert.Equal(exp.Vel, s.Velocity, 12);
            }
        }
        finally { s.Stop(); }
    }

    [Fact]
    public void CachedCoefficients_CriticalDamped_MatchesLegacyFormula()
    {
        const double response = 0.3, from = 0, to = 1, v0 = 0;
        var s = IOSSpring.Create(1.0, response, from, to, initialVelocity: v0);
        try
        {
            for (var i = 1; i <= 30 && s.IsActive; i++)
            {
                s.Tick(Dt);
                var t = i * Dt;
                var exp = LegacyCritical(response, from, to, v0, t);
                Assert.Equal(exp.Value, s.Value, 12);
                Assert.Equal(exp.Vel, s.Velocity, 12);
            }
        }
        finally { s.Stop(); }
    }

    [Fact]
    public void CachedCoefficients_RetargetRecomputes_MatchLegacy()
    {
        // 改目标后系数以新初始条件（当前值/当前速度）重算，后续历程与旧公式仍一致
        const double zeta = 0.82, response = 0.5;
        var s = IOSSpring.Create(zeta, response, from: 0, to: 100);
        try
        {
            for (var i = 0; i < 20; i++) s.Tick(Dt);
            var vBefore = s.Velocity;
            s.Retarget(50);
            var y0 = s.Value - 50;
            s.Tick(Dt);
            var exp = LegacyUnderdamped(zeta, response, from: y0 + 50, to: 50, v0: vBefore, t: Dt);
            Assert.Equal(exp.Value, s.Value, 12);
            Assert.Equal(exp.Vel, s.Velocity, 12);
        }
        finally { s.Stop(); }
    }

    [Fact]
    public void CachedCoefficients_ConfigureMidFlight_MatchesLegacy()
    {
        // 运行中 Configure（参数变化）后系数随新参数重建，不使用旧系数
        const double zeta0 = 0.82, response0 = 0.5;
        var s = IOSSpring.Create(zeta0, response0, from: 0, to: 100);
        try
        {
            for (var i = 0; i < 24; i++) s.Tick(Dt);
            var vBefore = s.Velocity;
            var y0 = s.Value;
            s.Configure(0.97, 0.52);
            s.Retarget(0);
            s.Tick(Dt);
            var zeta2 = 0.97; var resp2 = 0.52;
            var exp = LegacyUnderdamped(zeta2, resp2, from: y0 + 0, to: 0, v0: vBefore, t: Dt);
            Assert.Equal(exp.Value, s.Value, 12);
            Assert.Equal(exp.Vel, s.Velocity, 12);
        }
        finally { s.Stop(); }
    }
}
