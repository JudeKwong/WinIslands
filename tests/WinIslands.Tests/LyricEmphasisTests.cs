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
}
