using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class MediaCoordinatorTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void PollInterval_AdaptsToMediaActivity(bool active, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), MediaCoordinator.ResolvePollInterval(active));

    [Fact]
    public void IsRedundant_SuppressesSubFramePositionNoise()
        => Assert.True(MediaCoordinator.IsRedundant(Snapshot(10.0), Snapshot(10.1)));

    [Fact]
    public void IsRedundant_AllowsMeaningfulPositionChange()
        => Assert.False(MediaCoordinator.IsRedundant(Snapshot(10.0), Snapshot(10.5)));

    [Fact]
    public void IsRedundant_AllowsStatusChange()
    {
        var next = Snapshot(10.1) with { Status = PlaybackStatus.Paused };
        Assert.False(MediaCoordinator.IsRedundant(Snapshot(10.0), next));
    }

    private static MediaSnapshot Snapshot(double position) => new()
    {
        Track = new TrackInfo("Track", "Artist", "Album", "Artist", "Test", "Test", "", "", TimeSpan.FromMinutes(3)),
        Source = MediaSourceKind.Smtc,
        Status = PlaybackStatus.Playing,
        PositionSeconds = position,
        DurationSeconds = 180,
        Volume = 0.5,
    };

    [Fact]
    public void ResolveActiveSmtcSnapshot_NoSession_ClearsStaleResidue()
    {
        // 回归：媒体应用退出后即使 LastSnapshot 仍残留旧曲目，也必须视为 null，立即清除岛上媒体信息
        var stale = Snapshot(42.0);
        Assert.Null(MediaCoordinator.ResolveActiveSmtcSnapshot(hasActiveSession: false, stale));
    }

    [Fact]
    public void ResolveActiveSmtcSnapshot_ActiveSession_KeepsSnapshot()
    {
        var snap = Snapshot(42.0);
        Assert.Same(snap, MediaCoordinator.ResolveActiveSmtcSnapshot(hasActiveSession: true, snap));
        Assert.Null(MediaCoordinator.ResolveActiveSmtcSnapshot(hasActiveSession: true, null));
    }
}