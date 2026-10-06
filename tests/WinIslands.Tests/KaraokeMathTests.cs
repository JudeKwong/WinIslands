using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.3.6：卡拉OK过渡字颜色通道混合的纯函数测试。
/// 线性插值端点精确、中间截断取整、越界钳制、非法进度按未点亮兜底（绝不出黑色字节）。
/// </summary>
public sealed class KaraokeMathTests
{
    [Fact]
    public void BlendChannel_AtZero_ReturnsFrom() => Assert.Equal(10, KaraokeMath.BlendChannel(10, 200, 0.0));

    [Fact]
    public void BlendChannel_AtOne_ReturnsTo() => Assert.Equal(200, KaraokeMath.BlendChannel(10, 200, 1.0));

    [Fact]
    public void BlendChannel_HalfWay_Truncates() => Assert.Equal(127, KaraokeMath.BlendChannel(0, 255, 0.5)); // 127.5 → 截断 127

    [Fact]
    public void BlendChannel_BelowZero_ClampsToFrom() => Assert.Equal(10, KaraokeMath.BlendChannel(10, 200, -0.5));

    [Fact]
    public void BlendChannel_AboveOne_ClampsToTo() => Assert.Equal(200, KaraokeMath.BlendChannel(10, 200, 1.5));

    [Fact]
    public void BlendChannel_NonFinite_FallsBackToFrom() => Assert.Equal(10, KaraokeMath.BlendChannel(10, 200, double.NaN));

    [Fact]
    public void BlendChannel_ReversedRange_StillLinear() => Assert.Equal(150, KaraokeMath.BlendChannel(200, 100, 0.5)); // 200-50=150
}
