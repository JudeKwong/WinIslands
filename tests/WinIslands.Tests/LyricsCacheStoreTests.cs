using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class LyricsCacheStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinIslandsLyricsCache-" + Guid.NewGuid().ToString("N"));

    private static TrackInfo Track(string title, string artist = "Artist", string album = "Album")
        => new(title, artist, album, "", "x", "x", "", "", TimeSpan.Zero);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Key_Is_Stable_And_Source_Sensitive()
    {
        var track = Track("Song", "Singer", "Album");
        var a = LyricsCacheStore.KeyFor(LyricsSourceKind.Online, track);
        var b = LyricsCacheStore.KeyFor(LyricsSourceKind.Online, track);
        var c = LyricsCacheStore.KeyFor(LyricsSourceKind.AmllTtml, track);

        Assert.Equal(a, b);
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, c);
        Assert.All(a, ch => Assert.True(char.IsAsciiHexDigitLower(ch)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void Freshness_Uses_Ttl(int ageDays, bool expected)
    {
        var now = new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
        var saved = now.AddDays(-ageDays);
        Assert.Equal(expected, LyricsCacheStore.IsFresh(saved, now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Write_Then_Read_RoundTrips()
    {
        var store = new LyricsCacheStore(_dir);
        var track = Track("Song", "Singer", "Album");
        const string lrc = "[00:01.00]hello\n[00:02.00]world";

        store.Write(LyricsSourceKind.Online, track, lrc);

        Assert.True(store.TryRead(LyricsSourceKind.Online, track, out var read));
        Assert.Equal(lrc, read);
        Assert.False(store.TryRead(LyricsSourceKind.AmllTtml, track, out _));
    }

    [Fact]
    public void Expired_Entry_Is_Rejected()
    {
        var store = new LyricsCacheStore(_dir, ttl: TimeSpan.Zero);
        var track = Track("Song");
        store.Write(LyricsSourceKind.Online, track, "[00:01.00]hello");

        Assert.False(store.TryRead(LyricsSourceKind.Online, track, out _));
    }

    [Fact]
    public void Oversized_Text_Is_Not_Written()
    {
        var store = new LyricsCacheStore(_dir);
        var big = new string('x', LyricsCacheStore.MaxTextLength + 1);
        store.Write(LyricsSourceKind.Online, Track("Song"), big);

        var (count, _) = store.GetStats();
        Assert.Equal(0, count);
    }

    [Fact]
    public void Prune_Keeps_Newest_Entries()
    {
        var store = new LyricsCacheStore(_dir, maxEntries: 3);
        for (var i = 0; i < 6; i++)
            store.Write(LyricsSourceKind.Online, Track("Song" + i), "[00:01.00]line");

        var (count, _) = store.GetStats();
        Assert.Equal(3, count);
    }

    [Fact]
    public void Clear_Removes_All_Entries()
    {
        var store = new LyricsCacheStore(_dir);
        store.Write(LyricsSourceKind.Online, Track("A"), "[00:01.00]a");
        store.Write(LyricsSourceKind.AmllTtml, Track("B"), "[00:01.00]b");

        store.Clear();

        Assert.Equal(0, store.GetStats().Count);
    }
}