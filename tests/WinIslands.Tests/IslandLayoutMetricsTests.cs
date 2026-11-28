using WinIslands.UI;
using Xunit;

namespace WinIslands.Tests;

/// <summary>
/// 展开/收起几何回归：覆盖常见 DPI（100%–200%）与显示器分辨率（含竖屏、小屏、超宽屏），
/// 确保展开窗口不会超出工作区被裁切，圆角始终有效。
/// </summary>
public class IslandLayoutMetricsTests
{
    private const double RefW = 496;
    private const double RefH = 190;

    [Theory]
    [InlineData(1.0, 1920, 1080)]
    [InlineData(1.0, 1366, 768)]
    [InlineData(1.0, 1280, 720)]
    [InlineData(1.0, 1024, 768)]
    [InlineData(1.25, 1920, 1080)]
    [InlineData(1.25, 1536, 864)]
    [InlineData(1.5, 1920, 1080)]
    [InlineData(1.5, 2560, 1440)]
    [InlineData(1.75, 1920, 1080)]
    [InlineData(2.0, 3840, 2160)]
    [InlineData(2.0, 2560, 1600)]
    [InlineData(1.0, 1080, 1920)]   // 竖屏
    [InlineData(1.5, 1080, 1920)]   // 竖屏 + 150%
    [InlineData(1.0, 800, 600)]     // 极小工作区
    [InlineData(1.0, 640, 480)]     // 极端小工作区
    [InlineData(1.0, 512, 384)]     // 自动收缩下限
    [InlineData(1.0, 3440, 1440)]   // 超宽屏
    public void ExpandedLayout_NeverExceedsWorkArea(double dpi, double pixelWidth, double pixelHeight)
    {
        var workW = pixelWidth / dpi;
        var workH = pixelHeight / dpi;
        var layout = IslandLayoutMetrics.ComputeExpanded(RefW, RefH, 1.0, dpi, workW, workH);

        Assert.True(layout.FitsWorkArea(workW), $"window {layout.WindowWidth} > work {workW}");
        Assert.Equal(layout.VisualWidth * dpi, layout.PhysicalWidth, 6);
        Assert.Equal(layout.VisualHeight * dpi, layout.PhysicalHeight, 6);
        Assert.InRange(layout.FitScale, IslandLayoutMetrics.MinFitScale, 1.0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void ExpandedLayout_IsDipStableAcrossDpi(double dpi)
    {
        // 同一块 1920×1080 物理屏，DPI 升高只会减少 DIP 工作区，但 496 DIP 仍应完整放下。
        var layout = IslandLayoutMetrics.ComputeExpanded(RefW, RefH, 1.0, dpi, 1920 / dpi, 1080 / dpi);
        Assert.Equal(RefW, layout.VisualWidth, 3);
        Assert.Equal(RefH, layout.VisualHeight, 3);
        Assert.False(layout.IsClamped);
    }

    [Fact]
    public void ExpandedLayout_SmallScreen_ClampsAndStaysReadable()
    {
        var layout = IslandLayoutMetrics.ComputeExpanded(RefW, RefH, 1.0, 1.0, 420, 600);
        Assert.True(layout.IsClamped);
        Assert.True(layout.VisualWidth < RefW);
        Assert.True(layout.VisualWidth >= RefW * IslandLayoutMetrics.MinFitScale - 0.01);
        Assert.True(layout.FitsWorkArea(420));
    }

    [Fact]
    public void ExpandedLayout_FontScaleAffectsLogicalNotVisualSize()
    {
        var normal = IslandLayoutMetrics.ComputeExpanded(RefW, RefH, 1.0, 1.0, 1920, 1080);
        var large = IslandLayoutMetrics.ComputeExpanded(RefW, RefH, 1.25, 1.0, 1920, 1080);
        Assert.Equal(normal.VisualWidth, large.VisualWidth, 6);
        Assert.Equal(normal.WindowWidth, large.WindowWidth, 6);
        Assert.True(large.WindowHeight < normal.WindowHeight); // 逻辑高度 = 视觉 / 字号缩放
    }

    [Fact]
    public void ExpandedLayout_InvalidInputs_FallBackSafely()
    {
        var layout = IslandLayoutMetrics.ComputeExpanded(double.NaN, -1, 0, double.NaN, double.NaN, double.NaN);
        Assert.True(double.IsFinite(layout.WindowWidth));
        Assert.True(double.IsFinite(layout.WindowHeight));
        Assert.True(layout.WindowWidth > 0);
    }

    [Theory]
    [InlineData(190, 33, 33)]
    [InlineData(48, 33, 24)]
    [InlineData(20, 33, 10)]
    [InlineData(10, 33, 5)]
    public void SafeCornerRadius_NeverExceedsHalfHeight(double height, double requested, double expected)
    {
        Assert.Equal(expected, IslandLayoutMetrics.SafeCornerRadius(requested, height), 6);
    }

    [Fact]
    public void SafeCornerRadius_InvalidValues_ReturnZero()
    {
        Assert.Equal(0, IslandLayoutMetrics.SafeCornerRadius(33, 0));
        Assert.Equal(0, IslandLayoutMetrics.SafeCornerRadius(33, double.NaN));
        Assert.Equal(0, IslandLayoutMetrics.SafeCornerRadius(double.NaN, 100));
    }

    [Fact]
    public void HeaderFits_AccountsForSettingsButton()
    {
        // 参考展开宽度下，日期+时间+齿轮必须完整放下。
        Assert.True(IslandLayoutMetrics.HeaderFits(496, 60, 54, 20));
        Assert.False(IslandLayoutMetrics.HeaderFits(120, 60, 54, 20));
    }
}