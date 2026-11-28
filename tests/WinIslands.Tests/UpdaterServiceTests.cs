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

    [Theory]
    [InlineData("Beta", true)]
    [InlineData("beta", true)]
    [InlineData(" Beta ", true)]
    [InlineData("Stable", false)]
    [InlineData("", false)]
    public void IsBetaChannel_RecognizesChannel(string channel, bool expected)
        => Assert.Equal(expected, UpdaterService.IsBetaChannel(channel));

    [Fact]
    public void PickNewestRelease_ChoosesHighestVersion()
    {
        var releases = new[]
        {
            new ReleasePayload { Tag = "v3.1.8" },
            new ReleasePayload { Tag = "v3.2.0-beta1", IsPrerelease = true },
            new ReleasePayload { Tag = "v3.1.9" },
        };
        Assert.Equal("v3.2.0-beta1", UpdaterService.PickNewestRelease(releases)!.Tag);
    }

    [Fact]
    public void PickNewestRelease_PrefersStableOnSameVersion()
    {
        var releases = new[]
        {
            new ReleasePayload { Tag = "v3.2.0-beta2", IsPrerelease = true },
            new ReleasePayload { Tag = "v3.2.0", IsPrerelease = false },
        };
        Assert.Equal("v3.2.0", UpdaterService.PickNewestRelease(releases)!.Tag);
    }

    [Fact]
    public void PickNewestRelease_IgnoresInvalidTags()
    {
        var releases = new[]
        {
            new ReleasePayload { Tag = "nightly" },
            new ReleasePayload { Tag = "v3.1.9" },
        };
        Assert.Equal("v3.1.9", UpdaterService.PickNewestRelease(releases)!.Tag);
        Assert.Null(UpdaterService.PickNewestRelease(Array.Empty<ReleasePayload>()));
    }

    [Fact]
    public void RollbackPoint_SerializesRoundTrip()
    {
        var point = new RollbackPoint
        {
            Version = "3.1.8",
            BackupPath = @"C:\temp\WinIslands-3.1.8-win-x64.bak",
            InstallPath = @"C:\Apps\WinIslands.exe",
            CreatedUtc = new DateTime(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(point);
        var back = System.Text.Json.JsonSerializer.Deserialize<RollbackPoint>(json)!;
        Assert.Equal(point.Version, back.Version);
        Assert.Equal(point.BackupPath, back.BackupPath);
        Assert.Equal(point.InstallPath, back.InstallPath);
        Assert.Equal(point.CreatedUtc, back.CreatedUtc);
    }
}
