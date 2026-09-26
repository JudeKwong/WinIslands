using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class ScreenCaptureMonitorTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(8, true)]
    public void Refreshes_Recording_Process_Cache_On_Expected_Scans(int tick, bool expected)
        => Assert.Equal(expected, ScreenCaptureMonitor.ShouldRefreshProcessCache(tick));
}