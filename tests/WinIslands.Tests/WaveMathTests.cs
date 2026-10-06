using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.3.5：声波纹渲染「亚像素写入去重」的纯函数测试。
/// 阈值判断必须稳定：非法输入丢弃、首次写入必写、阈值边界精确且任一常见条高/位移都远小于 1 物理像素。
/// </summary>
public sealed class WaveMathTests
{
    [Fact]
    public void ShouldWriteEased_MovementAboveThreshold_Writes()
    {
        // 单帧位移 0.5*0.2=0.1，远大于 ScaleEpsilon → 必须写
        Assert.True(WaveMath.ShouldWriteEased(0.5, 1.0, 0.2, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_MovementBelowThreshold_Skip()
    {
        // 单帧位移 0.1*0.002=0.0002 < 0.0004 → 跳过写入（亚像素）
        Assert.False(WaveMath.ShouldWriteEased(1.0, 1.1, 0.002, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_ZeroDelta_Skip()
    {
        Assert.False(WaveMath.ShouldWriteEased(0.7, 0.7, 0.5, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_AtEpsilon_Writes()
    {
        // |(0.01-0)*0.04| = 0.0004，恰好等于阈值 → 写
        Assert.True(WaveMath.ShouldWriteEased(0.0, 0.01, 0.04, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_NonFiniteCurrent_MustWrite()
    {
        // 尚未有合法值（NaN/Infinity）：调用方收到 true 后直接以目标值复位
        Assert.True(WaveMath.ShouldWriteEased(double.NaN, 0.5, 0.3, WaveMath.ScaleEpsilon));
        Assert.True(WaveMath.ShouldWriteEased(double.PositiveInfinity, 0.5, 0.3, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_NonFiniteTargetOrAlpha_Discard()
    {
        // 目标/步长非法：丢弃（保持上次写入），绝不产生 NaN 写入
        Assert.False(WaveMath.ShouldWriteEased(0.5, double.NaN, 0.3, WaveMath.ScaleEpsilon));
        Assert.False(WaveMath.ShouldWriteEased(0.5, 0.6, double.PositiveInfinity, WaveMath.ScaleEpsilon));
        Assert.False(WaveMath.ShouldWriteEased(0.5, 0.6, double.NaN, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void Epsilons_ArePositiveAndSubVisual()
    {
        // 缩放阈值 < 1%：对任意常见条高都远小于 1 物理像素
        Assert.True(WaveMath.ScaleEpsilon > 0 && WaveMath.ScaleEpsilon < 0.01);
        // 位移阈值 < 1 DIP：约 1/20 像素
        Assert.True(WaveMath.OffsetEpsilon > 0 && WaveMath.OffsetEpsilon < 1.0);
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.0005, true)]    // 0.0005 >= 0.0004：边界外→写
    [InlineData(0.0, 1.0, 0.00039, false)]  // 0.00039 < 0.0004：略低于阈值→跳过
    [InlineData(-0.3, 0.9, 0.0004, true)]   // |1.2*0.0004|=0.00048→写
    public void ShouldWriteEased_Boundary(double current, double target, double alpha, bool expected)
    {
        Assert.Equal(expected, WaveMath.ShouldWriteEased(current, target, alpha, WaveMath.ScaleEpsilon));
    }
}
