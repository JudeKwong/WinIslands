using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class ClipboardPollingTests
{
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