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
}