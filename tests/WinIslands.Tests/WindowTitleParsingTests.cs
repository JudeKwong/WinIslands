using WinIslands.Services;

namespace WinIslands.Tests;

public class WindowTitleParsingTests
{
    [Theory]
    [InlineData("Artist - Title", "Artist", "Title")]
    [InlineData("  Artist – Title  ", "Artist", "Title")]
    [InlineData("Artist — Title", "Artist", "Title")]
    [InlineData("NoDashHere", "", "NoDashHere")]
    [InlineData("Spotify - Artist - Title", "Artist", "Title")]
    public void ParseTitle_Variants(string input, string artist, string title)
    {
        var (a, t) = WindowTitleMediaProvider.ParseTitle(input);
        Assert.Equal(artist, a);
        Assert.Equal(title, t);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 15)]
    [InlineData(3, 15)]
    public void ResolveCacheDuration_UsesAdaptiveIntervals(int players, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), WindowTitleMediaProvider.ResolveCacheDuration(players));
}
