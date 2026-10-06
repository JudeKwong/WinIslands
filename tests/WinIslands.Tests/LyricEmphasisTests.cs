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
}
