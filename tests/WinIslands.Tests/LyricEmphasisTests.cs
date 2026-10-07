using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.1.2：当前行歌词强调改为「渲染级缩放」——缩放倍率纯函数测试。
/// 倍率必须在安全区间（1.0~1.35），非法输入兜底，杜绝 NaN/无限大。
/// </summary>
public sealed class LyricEmphasisTests
{
    [Theory]
    [InlineData(13, 16, 16.0 / 13.0)]     // 默认 13 → 16：约 1.23
    [InlineData(16, 19, 19.0 / 16.0)]     // 大字号的线性比例
    [InlineData(20, 20, 1.0)]             // 当前行字号不放大时保持原样
    [InlineData(26, 29, 29.0 / 26.0)]     // 大字号下的比例（约 1.115）
    public void ComputeTargetScale_LinearRatio(double baseSize, double currentSize, double expected)
    {
        var ratio = LyricEmphasis.ComputeTargetScale(baseSize, currentSize);
        Assert.InRange(ratio, 1.0, 1.35);
        Assert.Equal(expected, ratio, 4);
    }

    [Fact]
    public void ComputeTargetScale_NeverBelowOne()
    {
        // 当前行字号小于基础字号（异常设置）：仍按 1.0，不缩小
        Assert.Equal(1.0, LyricEmphasis.ComputeTargetScale(16, 10), 6);
    }

    [Fact]
    public void ComputeTargetScale_CapsAtUpperBound()
    {
        // 极端放大（如 9 → 20）：封顶 1.35，避免放大到布局无法容纳
        Assert.Equal(1.35, LyricEmphasis.ComputeTargetScale(9, 30), 6);
    }

    [Fact]
    public void ComputeTargetScale_InvalidInputs_FallBack()
    {
        // NaN / 0 / 负数：兜底到 1.18，绝不产生 NaN
        Assert.Equal(1.18, LyricEmphasis.ComputeTargetScale(double.NaN, 16), 6);
        Assert.Equal(1.18, LyricEmphasis.ComputeTargetScale(0, 16), 6);
        Assert.Equal(1.18, LyricEmphasis.ComputeTargetScale(-5, 16), 6);
        Assert.Equal(1.18, LyricEmphasis.ComputeTargetScale(13, double.PositiveInfinity), 6);
    }

    // ---- 2.9.0：目标缩放钳制走 WaveMath.ClampRange 分支链，与 Math.Clamp 逐位一致 ----
    [Fact]
    public void ComputeTargetScale_BitIdenticalToMathClamp()
    {
        var currents = new[] { 0.5, 1.0, 1.35, 1.3500000000001, 2.0, double.Epsilon, 1e-308, 6.25 };
        var bases = new[] { 1.0, 8.0, 13.0, 16.0, 26.0, 100.0, 320.0 };
        foreach (var bs in bases)
        {
            foreach (var cs in currents)
            {
                var actual = LyricEmphasis.ComputeTargetScale(bs, cs);
                var expected = Math.Clamp(cs / bs, 1.0, 1.35);
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
            }
        }
        Assert.Equal(1.0, LyricEmphasis.ComputeTargetScale(16, 16), 12);
        Assert.Equal(1.35, LyricEmphasis.ComputeTargetScale(100, 135), 12);
        Assert.Equal(1.0, LyricEmphasis.ComputeTargetScale(10, 5), 12);
        Assert.Equal(1.35, LyricEmphasis.ComputeTargetScale(10, 1000), 12);
    }
    // ── 2.3.0：真实 IOSSpring 物理引擎参数换算 ─────────────────────────
    [Theory]
    [InlineData(240)]
    [InlineData(120)]
    [InlineData(60)]
    [InlineData(900)]
    [InlineData(500)]
    public void EnterParams_FiniteAndInRange(double ms)
    {
        var (zeta, response) = LyricEmphasis.ComputeEnterParams(ms);
        Assert.Equal(0.78, zeta, 6);
        Assert.InRange(response, 0.06, 0.9);
    }

    [Fact]
    public void EnterParams_DefaultDurationMapsToResponse()
    {
        // 默认 240ms → 响应 0.24s，直接用作弹簧感知收敛时长
        var (_, response) = LyricEmphasis.ComputeEnterParams(240);
        Assert.Equal(0.24, response, 4);
    }

    [Fact]
    public void ExitParams_DampedAndQuicker()
    {
        // 退出：阻尼更高（无回弹）、收敛比进入更快（×0.72）
        var (zeta, response) = LyricEmphasis.ComputeExitParams(240);
        Assert.Equal(0.90, zeta, 6);
        Assert.Equal(0.1728, response, 4);
    }

    [Fact]
    public void DurationParams_ClampedToSafeWindow()
    {
        // DurationMs 越界（内部误传 10 / 5000）→ 钳制到 60~900ms，绝不产生 NaN
        var (_, rLo) = LyricEmphasis.ComputeEnterParams(10);
        var (_, rHi) = LyricEmphasis.ComputeEnterParams(5000);
        Assert.Equal(0.06, rLo, 4);
        Assert.Equal(0.9, rHi, 4);
    }

    [Fact]
    public void DurationParams_InvalidInputFallsBack()
    {
        // NaN / Inf：兜底到默认 240ms，保持稳定不崩
        var (z1, r1) = LyricEmphasis.ComputeEnterParams(double.NaN);
        Assert.Equal(0.78, z1, 6);
        Assert.Equal(0.24, r1, 4);
        var (z2, r2) = LyricEmphasis.ComputeExitParams(double.PositiveInfinity);
        Assert.Equal(0.90, z2, 6);
        Assert.True(double.IsFinite(r2));
    }
    // ── 2.3.1：渲染写入去重 ────────────────────────────────────────────
    [Fact]
    public void ShouldWriteScale_FirstWriteAlways()
    {
        // NaN（尚未写入）→ 必写，保证每段弹簧的首帧落盘
        Assert.True(LyricEmphasis.ShouldWriteScale(1.0, double.NaN));
    }

    [Fact]
    public void ShouldWriteScale_DuplicateSkipped()
    {
        // 同值 / 亚阈值变化 → 不写，收敛尾部不产生无效属性写入
        Assert.False(LyricEmphasis.ShouldWriteScale(1.2, 1.2));
        Assert.False(LyricEmphasis.ShouldWriteScale(1.2 + 0.0002, 1.2));
    }

    [Fact]
    public void ShouldWriteScale_ThresholdReached()
    {
        // 达到阈值 → 写（差值取明显大于阈值的值，避免二进制浮点边界）
        Assert.True(LyricEmphasis.ShouldWriteScale(1.2 + 0.002, 1.2));
        Assert.True(LyricEmphasis.ShouldWriteScale(1.2 - 0.002, 1.2));
    }

    [Fact]
    public void ShouldWriteScale_InvalidValueDropped()
    {
        // 非法值直接丢弃（引擎已兜底），绝不写入渲染
        Assert.False(LyricEmphasis.ShouldWriteScale(double.NaN, 1.2));
        Assert.False(LyricEmphasis.ShouldWriteScale(double.PositiveInfinity, 1.2));
    }
    // ── 2.3.2：归一化进度映射（缩放 + 不透明度统一由同一个弹簧驱动） ──
    [Fact]
    public void MapProgress_Zero_BaseState()
    {
        // p=0 → 未强调：scale=1.0, opacity=0.28
        var (scale, opacity) = LyricEmphasis.MapProgress(0.0, 1.23);
        Assert.Equal(1.0, scale, 6);
        Assert.Equal(0.28, opacity, 6);
    }

    [Fact]
    public void MapProgress_One_FullEmphasis()
    {
        // p=1 → 强调完成：scale=targetScale, opacity=1.0
        var (scale, opacity) = LyricEmphasis.MapProgress(1.0, 1.23);
        Assert.Equal(1.23, scale, 6);
        Assert.Equal(1.0, opacity, 6);
    }

    [Fact]
    public void MapProgress_Mid_Linear()
    {
        // p=0.5, target=1.2 → scale=1.1, opacity=0.64（均匀线性，打断时值连续）
        var (scale, opacity) = LyricEmphasis.MapProgress(0.5, 1.2);
        Assert.Equal(1.1, scale, 6);
        Assert.Equal(0.64, opacity, 6);
    }

    [Fact]
    public void MapProgress_Clamped()
    {
        // 越界进度钳制到 [0,1]
        var (sLo, _) = LyricEmphasis.MapProgress(-2, 1.2);
        Assert.Equal(1.0, sLo, 6);
        var (sHi, _) = LyricEmphasis.MapProgress(3, 1.2);
        Assert.Equal(1.2, sHi, 6);
    }

    [Fact]
    public void MapProgress_InvalidFallsBack()
    {
        // NaN 进度 → 0；非法 targetScale → 1.18 兜底，绝不产生 NaN
        var (s1, o1) = LyricEmphasis.MapProgress(double.NaN, 1.2);
        Assert.Equal(1.0, s1, 6);
        Assert.Equal(0.28, o1, 6);
        var (s2, _) = LyricEmphasis.MapProgress(1.0, double.PositiveInfinity);
        Assert.Equal(1.18, s2, 6);
    }
    // ── 2.8.4：ShouldWriteScale 双边界分支链 + MapProgress 分支链钳制（逐位等价 Math 形式）────────────────
    private static bool ShouldWriteScaleRef(double value, double lastWritten)
    {
        if (double.IsNaN(lastWritten)) return true;
        if (!double.IsFinite(value)) return false;
        return Math.Abs(value - lastWritten) >= LyricEmphasis.ScaleWriteEpsilon;
    }

    [Fact]
    public void ShouldWriteScale_BranchChain_EquivalentToAbsForm()
    {
        var vals = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 1.18, 1.23, 0.5, -0.5, 2.0, -2.0
        };
        foreach (var v in vals)
            foreach (var lw in vals)
                Assert.Equal(ShouldWriteScaleRef(v, lw), LyricEmphasis.ShouldWriteScale(v, lw));
        // 阈值边界：与 Math.Abs 参考形式逐位一致（1.0±eps 的差受浮点舍入影响，直接用等价断言而非假设 tie 方向）
        var near = new double[]
        {
            1.0 + LyricEmphasis.ScaleWriteEpsilon, 1.0 - LyricEmphasis.ScaleWriteEpsilon,
            1.0 + LyricEmphasis.ScaleWriteEpsilon / 2, 1.0 - LyricEmphasis.ScaleWriteEpsilon / 2,
            1.0, 1.0001, 0.9999
        };
        foreach (var v in near)
            Assert.Equal(ShouldWriteScaleRef(v, 1.0), LyricEmphasis.ShouldWriteScale(v, 1.0));
        // 首次写入（lastWritten=NaN）必写；非法 value 丢弃
        Assert.True(LyricEmphasis.ShouldWriteScale(1.1, double.NaN));
        Assert.False(LyricEmphasis.ShouldWriteScale(double.PositiveInfinity, 1.0));
    }

    private static (double Scale, double Opacity) MapProgressRef(double progress, double targetScale)
    {
        var p = Math.Clamp(double.IsFinite(progress) ? progress : 0.0, 0.0, 1.0);
        var ts = Math.Clamp(double.IsFinite(targetScale) ? targetScale : 1.18, 1.0, 1.5);
        var scale = 1.0 + (ts - 1.0) * p;
        var opacity = LyricEmphasis.OpacityBase + (LyricEmphasis.OpacityCurrent - LyricEmphasis.OpacityBase) * p;
        return (scale, opacity);
    }

    [Fact]
    public void MapProgress_BranchClamps_BitwiseIdenticalToClampForm()
    {
        var vals = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, 0.0, -0.0, 1.0, -1.0,
            0.5, -0.5, 1.18, 1.23, 2.0, -2.0
        };
        foreach (var p in vals)
            foreach (var ts in vals)
            {
                var a = LyricEmphasis.MapProgress(p, ts);
                var b = MapProgressRef(p, ts);
                Assert.Equal(BitConverter.DoubleToInt64Bits(b.Scale), BitConverter.DoubleToInt64Bits(a.Scale));
                Assert.Equal(BitConverter.DoubleToInt64Bits(b.Opacity), BitConverter.DoubleToInt64Bits(a.Opacity));
            }
        // 边界：恰在钳制边界与内部线性映射
        var mid = LyricEmphasis.MapProgress(0.5, 1.2);
        Assert.Equal(1.1, mid.Scale, 10);
        var under = LyricEmphasis.MapProgress(-0.5, 1.2);
        Assert.Equal(1.0, under.Scale, 10);
        var over = LyricEmphasis.MapProgress(1.5, 1.2);
        Assert.Equal(1.2, over.Scale, 10);
    }

    // ---- 2.9.3：ClampDuration 走 WaveMath.ClampRange 分支链，与 Math.Clamp(·,60,900) 逐位一致 ----
    [Fact]
    public void DurationClamp_BitIdenticalToMathClamp()
    {
        static double Reference(double ms) => Math.Clamp(double.IsFinite(ms) ? ms : 240.0, 60.0, 900.0);
        var specials = new[]
        {
            double.NaN, -double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            0.0, -0.0, double.Epsilon, -double.Epsilon, 10.0, 59.0, 60.0, 61.0, 100.0, 240.0,
            500.0, 899.0, 900.0, 901.0, 5000.0, double.MaxValue, double.MinValue, 1e-300, -1e-300,
        };
        foreach (var ms in specials)
        {
            var actual = LyricEmphasis.ClampDuration(ms);
            var expected = Reference(ms);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        }
        for (var ms = 59.0; ms <= 61.0; ms += 0.1)
        {
            var actual = LyricEmphasis.ClampDuration(ms);
            var expected = Reference(ms);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        }
        for (var ms = 899.0; ms <= 901.0; ms += 0.1)
        {
            var actual = LyricEmphasis.ClampDuration(ms);
            var expected = Reference(ms);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        }
        for (var i = 0; i < 60000; i++)
        {
            var ms = BitConverter.Int64BitsToDouble(Random.Shared.NextInt64());
            var actual = LyricEmphasis.ClampDuration(ms);
            var expected = Reference(ms);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
        }
    }

}
