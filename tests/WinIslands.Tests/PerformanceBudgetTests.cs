using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

public sealed class PerformanceBudgetTests
{
    private static readonly PerformanceBudgetLimits Limits = new(
        PortableExeMaxMiB: 90,
        InstallerMaxMiB: 160,
        StartupMaxMilliseconds: 12_000,
        WorkingSetMaxMiB: 350,
        PrivateMemoryMaxMiB: 300);

    [Fact]
    public void EmbeddedDefaults_AreLoaded()
    {
        var limits = PerformanceBudgetLimits.Default;

        Assert.Equal(90, limits.PortableExeMaxMiB);
        Assert.Equal(160, limits.InstallerMaxMiB);
        Assert.Equal(12_000, limits.StartupMaxMilliseconds);
        Assert.Equal(350, limits.WorkingSetMaxMiB);
        Assert.Equal(300, limits.PrivateMemoryMaxMiB);
    }

    [Fact]
    public void HealthySample_IsWithinBudget()
    {
        var result = PerformanceBudget.Evaluate(
            new RuntimePerformanceSample(4_000, 180L * 1024 * 1024, 120L * 1024 * 1024),
            Limits);

        Assert.True(result.WithinBudget);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EveryRuntimeDimension_CanFailIndependently()
    {
        var result = PerformanceBudget.Evaluate(
            new RuntimePerformanceSample(13_000, 360L * 1024 * 1024, 301L * 1024 * 1024),
            Limits);

        Assert.False(result.WithinBudget);
        Assert.Contains(result.Violations, value => value.Contains("Startup", StringComparison.Ordinal));
        Assert.Contains(result.Violations, value => value.Contains("Working set", StringComparison.Ordinal));
        Assert.Contains(result.Violations, value => value.Contains("Private memory", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactBoundaries_AreAccepted()
    {
        var result = PerformanceBudget.Evaluate(
            new RuntimePerformanceSample(12_000, 350L * 1024 * 1024, 300L * 1024 * 1024),
            Limits);

        Assert.True(result.WithinBudget);
    }

    [Fact]
    public void InvalidRuntimeSample_IsRejected()
    {
        var result = PerformanceBudget.Evaluate(new RuntimePerformanceSample(-1, 0, 0), Limits);

        Assert.False(result.WithinBudget);
    }

    [Fact]
    public void FileBudget_RejectsOversizedArtifact()
    {
        var result = PerformanceBudget.EvaluateFile("Portable", 91L * 1024 * 1024, 90);

        Assert.False(result.WithinBudget);
        Assert.Contains("Portable", Assert.Single(result.Violations), StringComparison.Ordinal);
    }
}