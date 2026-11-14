using WinIslands.UI;
using Xunit;

namespace WinIslands.Tests;

/// <summary>LiquidMusic 歌词滚动移植的纯函数校验。</summary>
public class LiquidMusicLyricsTests
{
    [Theory]
    [InlineData(0.0, 180.0)]
    [InlineData(10.0, 180.0)]
    [InlineData(100.0, 180.0)]
    [InlineData(200.0, 320.0)]
    [InlineData(325.0, 520.0)]
    [InlineData(1000.0, 520.0)]
    [InlineData(-200.0, 320.0)]
    public void TweenDuration_MatchesLiquidMusicFormula(double delta, double expected)
        => Assert.Equal(expected, IslandWindow.LyricsTweenDuration(delta), 6);

    [Fact]
    public void TweenEase_IsEaseOutCubic()
    {
        Assert.Equal(0.0, IslandWindow.LyricsTweenEase(0.0), 6);
        Assert.Equal(1.0, IslandWindow.LyricsTweenEase(1.0), 6);
        Assert.Equal(0.875, IslandWindow.LyricsTweenEase(0.5), 6); // 1 - 0.5^3
        var prev = -1.0;
        for (var p = 0.0; p <= 1.0; p += 0.01)
        {
            var e = IslandWindow.LyricsTweenEase(p);
            Assert.True(e >= prev);
            prev = e;
        }
        Assert.Equal(1.0, IslandWindow.LyricsTweenEase(double.NaN), 6);
    }

    [Fact]
    public void FocusScaleAndOpacity_MatchLiquidMusicFormula()
    {
        Assert.Equal(1.0, IslandWindow.LyricsFocusScale(0.0, past: false), 6);
        Assert.Equal(1.0, IslandWindow.LyricsFocusOpacity(0.0, past: false), 6);
        Assert.Equal(0.88, IslandWindow.LyricsFocusScale(0.5, past: false), 6);
        Assert.Equal(0.6, IslandWindow.LyricsFocusOpacity(0.5, past: false), 6);
        Assert.Equal(0.78, IslandWindow.LyricsFocusScale(10.0, past: false), 6);
        Assert.Equal(0.24, IslandWindow.LyricsFocusOpacity(10.0, past: false), 6);
        // 已唱过的行：opacity * 0.3，scale <= 0.86
        Assert.Equal(0.86, IslandWindow.LyricsFocusScale(0.5, past: true), 6);
        Assert.Equal(0.18, IslandWindow.LyricsFocusOpacity(0.5, past: true), 6);
        Assert.Equal(0.072, IslandWindow.LyricsFocusOpacity(10.0, past: true), 6);
    }
}