using WinIslands.Services;
using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.4.4：动画时长 / 低功耗缩放常量缓存的纯函数测试。
/// 语义必须与旧实现完全一致：时长 Clamp 到 [300,1400]，durScale=clamped/700，
/// 低功耗整体缩放 ×0.65。设置变化后这些常量经 RefreshAnimationConstants 刷新。
/// </summary>
public sealed class AnimationConstantsTests
{
    [Fact]
    public void Compute_DefaultDuration_IsOneToOne()
    {
        var s = new AppSettings { IslandAnimationDuration = 700, LowPowerMode = false };
        IslandWindow.ComputeAnimationConstants(s, out var scale, out var lm, out var ms);
        Assert.Equal(700, ms);
        Assert.Equal(1.0, scale, 9);
        Assert.Equal(1.0, lm, 9);
    }

    [Fact]
    public void Compute_BottomClamp_ThreeHundred()
    {
        var s = new AppSettings { IslandAnimationDuration = 300, LowPowerMode = false };
        IslandWindow.ComputeAnimationConstants(s, out var scale, out _, out var ms);
        Assert.Equal(300, ms);
        Assert.Equal(300.0 / 700.0, scale, 9);
    }

    [Fact]
    public void Compute_BelowBottomClamp_ToThreeHundred()
    {
        var s = new AppSettings { IslandAnimationDuration = 100, LowPowerMode = false };
        IslandWindow.ComputeAnimationConstants(s, out var scale, out _, out var ms);
        Assert.Equal(300, ms);
        Assert.Equal(300.0 / 700.0, scale, 9);
    }

    [Fact]
    public void Compute_TopClamp_FourteenHundred()
    {
        var s = new AppSettings { IslandAnimationDuration = 1400, LowPowerMode = false };
        IslandWindow.ComputeAnimationConstants(s, out var scale, out _, out var ms);
        Assert.Equal(1400, ms);
        Assert.Equal(2.0, scale, 9);
    }

    [Fact]
    public void Compute_AboveTopClamp_ToFourteenHundred()
    {
        var s = new AppSettings { IslandAnimationDuration = 2000, LowPowerMode = false };
        IslandWindow.ComputeAnimationConstants(s, out var scale, out _, out var ms);
        Assert.Equal(1400, ms);
        Assert.Equal(2.0, scale, 9);
    }

    [Fact]
    public void Compute_LowPowerMode_SpeedsUp()
    {
        var s = new AppSettings { IslandAnimationDuration = 700, LowPowerMode = true };
        IslandWindow.ComputeAnimationConstants(s, out _, out var lm, out _);
        Assert.Equal(0.65, lm, 9);
    }

    [Fact]
    public void Compute_NonLowPower_IsNormal()
    {
        var s = new AppSettings { IslandAnimationDuration = 900, LowPowerMode = false };
        IslandWindow.ComputeAnimationConstants(s, out var scale, out var lm, out var ms);
        Assert.Equal(900, ms);
        Assert.Equal(900.0 / 700.0, scale, 9);
        Assert.Equal(1.0, lm, 9);
    }
}
