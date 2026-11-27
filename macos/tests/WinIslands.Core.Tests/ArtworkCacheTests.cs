using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class ArtworkCacheTests
{
    [Fact]
    public void CacheKey_IsStableAndIgnoresEmptyParts()
    {
        var a = ArtworkCache.CacheKey("track", "", "artist");
        var b = ArtworkCache.CacheKey("track", "artist");
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10L * 1024 * 1024, true)]
    [InlineData(10L * 1024 * 1024 + 1, false)]
    public void IsArtworkSizeAllowed_RejectsEmptyAndOversizedPayloads(long bytes, bool expected)
        => Assert.Equal(expected, ArtworkCache.IsArtworkSizeAllowed(bytes));
}