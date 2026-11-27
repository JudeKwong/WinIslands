using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

public sealed class UpdaterServiceTests
{
    [Theory]
    [InlineData("v3.1.7", "3.1.7")]
    [InlineData("3.1.7", "3.1.7")]
    [InlineData("V3.1.7-beta1", "3.1.7")]
    [InlineData(" v3.1.7-rc2 ", "3.1.7")]
    public void NormalizeVersion_StripsPrefixAndPrerelease(string tag, string expected)
        => Assert.Equal(expected, UpdaterService.NormalizeVersion(tag));

    [Theory]
    [InlineData("3.1.7", 3001007)]
    [InlineData("v3.1.7", 3001007)]
    [InlineData("3.1.7-beta1", 3001007)]
    [InlineData("3.2.0", 3002000)]
    public void ParseVersion_ReturnsComparableNumber(string tag, long expected)
        => Assert.Equal(expected, UpdaterService.ParseVersion(tag));

    [Fact]
    public void ParseVersion_NewerPatchIsGreater()
        => Assert.True(UpdaterService.ParseVersion("v3.1.8") > UpdaterService.ParseVersion("3.1.7"));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void ParseVersion_RejectsGarbage(string tag)
        => Assert.Equal(0, UpdaterService.ParseVersion(tag));

    [Fact]
    public void CurrentVersion_IsNonZero()
        => Assert.True(UpdaterService.ParseVersion(UpdaterService.CurrentVersion()) > 0);
}
