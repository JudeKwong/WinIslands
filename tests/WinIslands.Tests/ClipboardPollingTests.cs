using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class ClipboardPollingTests
{
    [Theory]
    [InlineData(10u, 11u, true)]
    [InlineData(10u, 10u, false)]
    [InlineData(10u, 0u, true)]
    public void ShouldReadClipboard_DetectsSequenceChanges(uint last, uint current, bool expected)
        => Assert.Equal(expected, ClipboardHistoryService.ShouldReadClipboard(last, current));

    [Fact]
    public void ShouldLogPollError_LogsWhenErrorChanges()
        => Assert.True(ClipboardHistoryService.ShouldLogPollError(
            "ExternalException", DateTime.UtcNow, DateTime.UtcNow, "COMException", TimeSpan.FromMinutes(5)));

    [Fact]
    public void ShouldLogPollError_ThrottlesRepeatedError()
    {
        var now = DateTime.UtcNow;
        Assert.False(ClipboardHistoryService.ShouldLogPollError(
            "COMException", now, now.AddMinutes(1), "COMException", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ShouldLogPollError_LogsAfterInterval()
    {
        var now = DateTime.UtcNow;
        Assert.True(ClipboardHistoryService.ShouldLogPollError(
            "COMException", now, now.AddMinutes(5), "COMException", TimeSpan.FromMinutes(5)));
    }
}