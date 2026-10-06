using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.4.5：卡拉OK帧率上限缓存测试。
/// TickAnimation 每帧直接读缓存，不再走三元判断 + Current 调用；
/// 缓存必须与低功耗开关保持同步，且默认值等于非低功耗硬件帧率。
/// </summary>
public sealed class KaraokeFrameRateCacheTests
{
    [Fact]
    public void Cache_Default_MatchesNonLowPowerTarget()
    {
        var before = KaraokeTextBlock.LowPowerModeOverride;
        try
        {
            KaraokeTextBlock.LowPowerModeOverride = false;
            Assert.Equal(AnimationFrameRate.Current(lowPowerMode: false), KaraokeTextBlock.CachedFrameFps);
        }
        finally
        {
            KaraokeTextBlock.LowPowerModeOverride = before;
        }
    }

    [Fact]
    public void Cache_EnableLowPower_CapsAtSixty()
    {
        var before = KaraokeTextBlock.LowPowerModeOverride;
        try
        {
            KaraokeTextBlock.LowPowerModeOverride = true;
            Assert.Equal(AnimationFrameRate.StandardForLowPower, KaraokeTextBlock.CachedFrameFps);
        }
        finally
        {
            KaraokeTextBlock.LowPowerModeOverride = before;
        }
    }

    [Fact]
    public void Cache_DisableLowPower_RestoresHardwareTarget()
    {
        var before = KaraokeTextBlock.LowPowerModeOverride;
        try
        {
            KaraokeTextBlock.LowPowerModeOverride = true;
            Assert.Equal(AnimationFrameRate.StandardForLowPower, KaraokeTextBlock.CachedFrameFps);
            KaraokeTextBlock.LowPowerModeOverride = false;
            Assert.Equal(AnimationFrameRate.Current(lowPowerMode: false), KaraokeTextBlock.CachedFrameFps);
        }
        finally
        {
            KaraokeTextBlock.LowPowerModeOverride = before;
        }
    }

    [Fact]
    public void Cache_SameValueNoOp_KeepsConsistency()
    {
        var before = KaraokeTextBlock.LowPowerModeOverride;
        try
        {
            KaraokeTextBlock.LowPowerModeOverride = false;
            var v1 = KaraokeTextBlock.CachedFrameFps;
            KaraokeTextBlock.LowPowerModeOverride = false; // 无变化，不应破坏缓存
            Assert.Equal(v1, KaraokeTextBlock.CachedFrameFps);
        }
        finally
        {
            KaraokeTextBlock.LowPowerModeOverride = before;
        }
    }
}
