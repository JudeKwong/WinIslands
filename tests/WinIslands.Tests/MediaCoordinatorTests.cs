using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class MediaCoordinatorTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void PollInterval_AdaptsToMediaActivity(bool active, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), MediaCoordinator.ResolvePollInterval(active));
}