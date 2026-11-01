using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 边缘拖拽缩放（IslandResizeMath）的命中与尺寸计算测试。
/// 交互规则：左右边缘以中心为锚点对称伸缩（宽度变化为位移的两倍），下边缘跟随纵向位移。
/// </summary>
public sealed class IslandResizeMathTests
{
    private const double Left = 100, Top = 50, Width = 360, Height = 72;

    [Theory]
    [InlineData(280, 86, IslandResizeEdges.None)]          // 中心：不是抓取区
    [InlineData(103, 86, IslandResizeEdges.Left)]          // 左边缘内侧
    [InlineData(457, 86, IslandResizeEdges.Right)]         // 右边缘内侧
    [InlineData(280, 119, IslandResizeEdges.Bottom)]       // 下边缘内侧
    [InlineData(103, 119, IslandResizeEdges.Left | IslandResizeEdges.Bottom)]   // 左下角
    [InlineData(457, 119, IslandResizeEdges.Right | IslandResizeEdges.Bottom)]  // 右下角
    public void HitTest_ReturnsExpectedEdges(double x, double y, IslandResizeEdges expected)
    {
        Assert.Equal(expected, IslandResizeMath.HitTest(x, y, Left, Top, Width, Height, locked: false));
    }

    [Fact]
    public void HitTest_IsDisabledWhenLocked()
    {
        Assert.Equal(IslandResizeEdges.None,
            IslandResizeMath.HitTest(103, 119, Left, Top, Width, Height, locked: true));
    }

    [Fact]
    public void HitTest_RejectsPointsFarOutside()
    {
        Assert.Equal(IslandResizeEdges.None,
            IslandResizeMath.HitTest(50, 86, Left, Top, Width, Height, locked: false));
        Assert.Equal(IslandResizeEdges.None,
            IslandResizeMath.HitTest(280, 200, Left, Top, Width, Height, locked: false));
    }

    [Fact]
    public void HitTest_AllowsSmallOvershootPastEdge()
    {
        // 允许略微超出边缘（贴合圆角），但超出抓取容差即不命中
        Assert.Equal(IslandResizeEdges.Right,
            IslandResizeMath.HitTest(461, 86, Left, Top, Width, Height, locked: false));
        Assert.Equal(IslandResizeEdges.None,
            IslandResizeMath.HitTest(465, 86, Left, Top, Width, Height, locked: false));
    }

    [Fact]
    public void ComputeSize_LeftEdge_GrowsSymmetrically()
    {
        // 左边缘向左拖 20 → 宽度增加 40（中心不动）
        var (w, h) = IslandResizeMath.ComputeSize(
            360, 72, -20, 0, IslandResizeEdges.Left, 240, 520, 48, 140);
        Assert.Equal(400, w);
        Assert.Equal(72, h);
    }

    [Fact]
    public void ComputeSize_RightEdge_GrowsSymmetrically()
    {
        var (w, h) = IslandResizeMath.ComputeSize(
            360, 72, 20, 0, IslandResizeEdges.Right, 240, 520, 48, 140);
        Assert.Equal(400, w);
        Assert.Equal(72, h);
    }

    [Fact]
    public void ComputeSize_BottomEdge_FollowsDelta()
    {
        var (w, h) = IslandResizeMath.ComputeSize(
            360, 72, 0, 30, IslandResizeEdges.Bottom, 240, 520, 48, 140);
        Assert.Equal(360, w);
        Assert.Equal(102, h);
    }

    [Fact]
    public void ComputeSize_Corner_ChangesBoth()
    {
        var (w, h) = IslandResizeMath.ComputeSize(
            360, 72, 10, 20, IslandResizeEdges.Right | IslandResizeEdges.Bottom, 240, 520, 48, 140);
        Assert.Equal(380, w);
        Assert.Equal(92, h);
    }

    [Fact]
    public void ComputeSize_ClampsToLimits()
    {
        var (downW, upH) = IslandResizeMath.ComputeSize(
            360, 72, 500, -500, IslandResizeEdges.Right | IslandResizeEdges.Bottom, 240, 520, 48, 140);
        Assert.Equal(520, downW); // 向右拖到底：宽度放大到上限
        Assert.Equal(48, upH);    // 向上拖到底：高度收缩到下限

        var (upW, downH) = IslandResizeMath.ComputeSize(
            360, 72, -500, 500, IslandResizeEdges.Right | IslandResizeEdges.Bottom, 240, 520, 48, 140);
        Assert.Equal(240, upW);  // 向左拖到底：宽度收缩到下限
        Assert.Equal(140, downH); // 向下拖到底：高度放大到上限

        var (bigW, bigH) = IslandResizeMath.ComputeSize(
            360, 72, 500, 500, IslandResizeEdges.Right | IslandResizeEdges.Bottom, 240, 520, 48, 140);
        Assert.Equal(520, bigW);
        Assert.Equal(140, bigH);
    }

    [Fact]
    public void ComputeSize_NoEdges_KeepsSize()
    {
        var (w, h) = IslandResizeMath.ComputeSize(
            400, 300, 50, 50, IslandResizeEdges.None, 240, 520, 48, 400);
        Assert.Equal(400, w);
        Assert.Equal(300, h);
    }

    [Fact]
    public void ComputeSize_InvalidLimits_AreNormalized()
    {
        var (w, h) = IslandResizeMath.ComputeSize(
            360, 72, 0, 0, IslandResizeEdges.None, 300, 200, 100, 50);
        Assert.Equal(300, w);
        Assert.Equal(100, h);
    }

    [Fact]
    public void MaxWidthForWindow_NonFiniteWidth_FallsBackToPreferred()
    {
        Assert.Equal(520, IslandResizeMath.MaxWidthForWindow(double.NaN, 520, 240));
        Assert.Equal(520, IslandResizeMath.MaxWidthForWindow(double.PositiveInfinity, 520, 240));
    }

    [Fact]
    public void MaxWidthForWindow_NeverBelowMinimum()
    {
        Assert.Equal(240, IslandResizeMath.MaxWidthForWindow(100, 520, 240));
        Assert.Equal(400, IslandResizeMath.MaxWidthForWindow(432, 520, 240));
        Assert.Equal(520, IslandResizeMath.MaxWidthForWindow(1000, 520, 240));
    }
}
