using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

/// <summary>3.1.7 首次启动引导的显示策略。</summary>
public class OnboardingPolicyTests
{
    [Fact]
    public void FreshSettings_NormalLaunch_Shows()
    {
        var settings = new AppSettings();

        Assert.True(OnboardingPolicy.ShouldShow(settings, Array.Empty<string>()));
        Assert.True(OnboardingPolicy.ShouldShow(settings, null));
    }

    [Fact]
    public void CompletedSettings_NeverShows()
    {
        var settings = new AppSettings { OnboardingCompleted = true };

        Assert.False(OnboardingPolicy.ShouldShow(settings, Array.Empty<string>()));
        Assert.False(OnboardingPolicy.ShouldShow(settings, null));
    }

    [Theory]
    [InlineData("--diagnose")]
    [InlineData("--settings")]
    [InlineData("--demo")]
    [InlineData("--collapsed")]
    [InlineData("--expand")]
    [InlineData("--stress-island")]
    [InlineData("--screenshot=C:\\temp\\island.png")]
    [InlineData("--screenshot-scale=2")]
    [InlineData("--page=music")]
    [InlineData("--demo-at=0.5")]
    public void AutomationAndDiagnostics_NeverShows(string arg)
    {
        var settings = new AppSettings();

        Assert.False(OnboardingPolicy.ShouldShow(settings, new[] { arg }));
        Assert.False(OnboardingPolicy.ShouldShow(settings, new[] { "--verbose", arg, "--extra" }));
    }

    [Fact]
    public void UnrelatedArguments_StillShows()
    {
        var settings = new AppSettings();

        Assert.True(OnboardingPolicy.ShouldShow(settings, new[] { "--tray", "foo" }));
        Assert.True(OnboardingPolicy.ShouldShow(settings, new string[] { "", "   " }));
    }
}
