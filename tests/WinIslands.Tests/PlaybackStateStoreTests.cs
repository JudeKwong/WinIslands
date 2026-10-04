using System.Text.Json;
using WinIslands.Services;

namespace WinIslands.Tests;

/// <summary>
/// 播放状态持久化回归测试：
/// 1) 暂停后退出程序再打开，应恢复暂停时的曲目与位置（不再先跳 0 再跳回）；
/// 2) 过期 / 脏数据必须优雅返回 null，绝不抛异常、绝不恢复错曲目。
/// </summary>
public class PlaybackStateStoreTests : IDisposable
{
    private readonly string _dir;

    public PlaybackStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "WinIslandsStateTests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", _dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void SaveLoad_Roundtrip_PreservesTrackPositionAndStatus()
    {
        var state = new PlaybackStateStore
        {
            TrackKey = "EasonChan-ShallWeTalk",
            PositionSeconds = 73.5,
            Status = "Paused",
        };
        state.Save();

        var loaded = PlaybackStateStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("EasonChan-ShallWeTalk", loaded!.TrackKey);
        Assert.Equal(73.5, loaded.PositionSeconds, 6);
        Assert.Equal("Paused", loaded.Status);
        Assert.True((DateTime.UtcNow - loaded.SavedAtUtc).TotalSeconds < 10);
    }

    [Fact]
    public void Load_NoFile_ReturnsNull()
        => Assert.Null(PlaybackStateStore.Load());

    [Fact]
    public void Load_StateWithEmptyTrackKey_ReturnsNull()
    {
        WriteState(new PlaybackStateStore { TrackKey = "", PositionSeconds = 10, Status = "Paused" });
        Assert.Null(PlaybackStateStore.Load());
    }

    [Fact]
    public void Load_StateWithZeroPosition_ReturnsNull()
    {
        WriteState(new PlaybackStateStore { TrackKey = "Track", PositionSeconds = 0, Status = "Playing" });
        Assert.Null(PlaybackStateStore.Load());
    }

    [Fact]
    public void Load_StateOlderThanOneHour_ReturnsNull()
    {
        var stale = new PlaybackStateStore { TrackKey = "Old-Track", PositionSeconds = 42, Status = "Playing" };
        stale.SavedAtUtc = DateTime.UtcNow.AddHours(-2);
        WriteState(stale);
        Assert.Null(PlaybackStateStore.Load());
    }

    [Fact]
    public void Load_StateSavedJustNow_IsRestored()
    {
        var fresh = new PlaybackStateStore { TrackKey = "Fresh-Track", PositionSeconds = 8.25, Status = "Playing" };
        fresh.SavedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        WriteState(fresh);
        var loaded = PlaybackStateStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("Fresh-Track", loaded!.TrackKey);
        Assert.Equal(8.25, loaded.PositionSeconds, 6);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        File.WriteAllText(PlaybackStateStore.FilePath, "{ not valid json !!");
        Assert.Null(PlaybackStateStore.Load());
    }

    [Fact]
    public void Save_CreatesFileInAppDataDir()
    {
        var state = new PlaybackStateStore { TrackKey = "K", PositionSeconds = 1, Status = "Playing" };
        state.Save();
        Assert.True(File.Exists(PlaybackStateStore.FilePath));
        Assert.StartsWith(_dir, PlaybackStateStore.FilePath);
    }

    private static void WriteState(PlaybackStateStore state)
        => File.WriteAllText(PlaybackStateStore.FilePath, JsonSerializer.Serialize(state));
}
