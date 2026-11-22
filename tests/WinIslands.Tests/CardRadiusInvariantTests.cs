using WinIslands.UI;
using Xunit;

namespace WinIslands.Tests;

/// <summary>
/// 3.1.7 圆角退化回归：任何非法高度/设置值都不能产生 NaN 或 0 圆角，
/// 否则 WPF 会把卡片渲染成四角直角的长方形。
/// </summary>
public class CardRadiusInvariantTests
{
    [Theory]
    [InlineData(48, 33)]
    [InlineData(72, 33)]
    [InlineData(760, 33)]
    [InlineData(4, 33)]
    [InlineData(0, 33)]
    [InlineData(-20, 33)]
    [InlineData(double.NaN, 33)]
    [InlineData(double.PositiveInfinity, 33)]
    public void Radius_IsAlwaysFiniteAndBounded(double height, double setting)
    {
        var r = IslandWindow.ComputeCardRuntimeRadius(height, setting);
        Assert.True(double.IsFinite(r));
        Assert.InRange(r, 4.0, 40.0);
    }

    [Theory]
    [InlineData(48, double.NaN)]
    [InlineData(48, 0)]
    [InlineData(48, -10)]
    [InlineData(48, 999)]
    public void Radius_GuardsCorruptSettingValue(double height, double setting)
    {
        var r = IslandWindow.ComputeCardRuntimeRadius(height, setting);
        Assert.True(double.IsFinite(r));
        Assert.InRange(r, 4.0, 40.0);
    }

    [Fact]
    public void CompactPill_IsFullyRounded()
    {
        // 48px 胶囊：圆角 = 高度/2，视觉上是完整药丸而不是圆角矩形。
        Assert.Equal(24.0, IslandWindow.ComputeCardRuntimeRadius(48, 33), 3);
    }

    [Fact]
    public void ExpandedCard_RespectsSettingCap()
    {
        Assert.Equal(33.0, IslandWindow.ComputeCardRuntimeRadius(760, 33), 3);
        Assert.Equal(20.0, IslandWindow.ComputeCardRuntimeRadius(760, 20), 3);
    }
}
