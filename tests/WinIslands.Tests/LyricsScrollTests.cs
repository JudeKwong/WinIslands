using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.3.3：歌词自动滚动的两个纯函数测试：
///   - ComputeLyricsScrollTarget：居中公式 + 双向钳制（旧实现只有下界，最后一句会越界）
///   - ShouldWriteScrollOffset：亚像素写入去重（仿 2.3.1 ShouldWriteScale 模式）
/// </summary>
public sealed class LyricsScrollTests
{
    // ── ComputeLyricsScrollTarget ──────────────────────────────
    [Fact]
    public void ComputeLyricsScrollTarget_CentersLine()
    {
        // offset=100, relY=40, viewport=200, line=20 → 100+40-100+10 = 50
        Assert.Equal(50.0, IslandWindow.ComputeLyricsScrollTarget(100, 40, 200, 20, 500), 6);
    }

    [Fact]
    public void ComputeLyricsScrollTarget_ClampsTop()
    {
        // 居中公式得到负值（第一句）→ 钳制到 0
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(0, 0, 200, 20, 500), 6);
    }

    [Fact]
    public void ComputeLyricsScrollTarget_ClampsBottom()
    {
        // 最后一句：居中公式超出 maxOffset → 钳制到 maxOffset，不再越界请求
        Assert.Equal(320.0, IslandWindow.ComputeLyricsScrollTarget(300, 200, 200, 20, 320), 6);
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(300, 80, 200, 20, 0), 6);
    }

    [Fact]
    public void ComputeLyricsScrollTarget_ZeroScrollableStaysZero()
    {
        // 内容不足一屏（maxOffset=0）：永远停在顶部，不产生负值
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(10, 5, 300, 20, 0), 6);
    }

    [Fact]
    public void ComputeLyricsScrollTarget_InvalidInputs_FallBack()
    {
        // NaN/无限/非法视口与行高/负 maxOffset → 0 兜底，绝不产生 NaN
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(double.NaN, 40, 200, 20, 500), 6);
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(100, double.PositiveInfinity, 200, 20, 500), 6);
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(100, 40, 0, 20, 500), 6);
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(100, 40, -200, 20, 500), 6);
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(100, 40, 200, -5, 500), 6);
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(100, 40, 200, 20, double.NegativeInfinity), 6);
        Assert.Equal(0.0, IslandWindow.ComputeLyricsScrollTarget(100, 40, 200, 20, -1), 6);
    }

    // ── ShouldWriteScrollOffset ────────────────────────────────
    [Fact]
    public void ShouldWriteScrollOffset_FirstWriteAlways()
    {
        // NaN（尚未写入）→ 必写
        Assert.True(IslandWindow.ShouldWriteScrollOffset(12.34, double.NaN));
    }

    [Fact]
    public void ShouldWriteScrollOffset_SubPixelDeltaSkipped()
    {
        // 同值 / 亚阈值变化 → 不写，收敛尾部不触发 ScrollViewer 布局
        Assert.False(IslandWindow.ShouldWriteScrollOffset(100.0, 100.0));
        Assert.False(IslandWindow.ShouldWriteScrollOffset(100.1, 100.0));
        Assert.False(IslandWindow.ShouldWriteScrollOffset(100.24, 100.0));
    }

    [Fact]
    public void ShouldWriteScrollOffset_ThresholdReached()
    {
        // 达到阈值（0.25）→ 写
        Assert.True(IslandWindow.ShouldWriteScrollOffset(100.5, 100.0));
        Assert.True(IslandWindow.ShouldWriteScrollOffset(99.5, 100.0));
    }

    [Fact]
    public void ShouldWriteScrollOffset_InvalidValueDropped()
    {
        // 非法值直接丢弃，绝不写入
        Assert.False(IslandWindow.ShouldWriteScrollOffset(double.NaN, 100.0));
        Assert.False(IslandWindow.ShouldWriteScrollOffset(double.PositiveInfinity, 100.0));
    }
}
