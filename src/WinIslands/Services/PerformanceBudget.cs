using System.Text.Json;

namespace WinIslands.Services;

public sealed record PerformanceBudgetLimits(
    double PortableExeMaxMiB,
    double InstallerMaxMiB,
    long StartupMaxMilliseconds,
    double WorkingSetMaxMiB,
    double PrivateMemoryMaxMiB)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static PerformanceBudgetLimits Fallback { get; } = new(90, 160, 12_000, 350, 300);

    public static PerformanceBudgetLimits Default { get; } = LoadEmbedded();

    private static PerformanceBudgetLimits LoadEmbedded()
    {
        try
        {
            using var stream = typeof(PerformanceBudgetLimits).Assembly
                .GetManifestResourceStream("WinIslands.performance-budget.json");
            if (stream is null) return Fallback;
            var limits = JsonSerializer.Deserialize<PerformanceBudgetLimits>(stream, Options);
            return limits ?? Fallback;
        }
        catch
        {
            return Fallback;
        }
    }
}

public readonly record struct RuntimePerformanceSample(
    long StartupMilliseconds,
    long WorkingSetBytes,
    long PrivateMemoryBytes)
{
    public double WorkingSetMiB => WorkingSetBytes / 1024d / 1024d;
    public double PrivateMemoryMiB => PrivateMemoryBytes / 1024d / 1024d;
}

public sealed record PerformanceBudgetResult(IReadOnlyList<string> Violations)
{
    public bool WithinBudget => Violations.Count == 0;
}

public static class PerformanceBudget
{
    public static PerformanceBudgetResult Evaluate(
        RuntimePerformanceSample sample,
        PerformanceBudgetLimits? limits = null)
    {
        limits ??= PerformanceBudgetLimits.Default;
        var violations = new List<string>();

        if (sample.StartupMilliseconds < 0 || sample.WorkingSetBytes <= 0 || sample.PrivateMemoryBytes <= 0)
            violations.Add("Runtime performance sample contains invalid values.");
        if (sample.StartupMilliseconds > limits.StartupMaxMilliseconds)
            violations.Add($"Startup {sample.StartupMilliseconds} ms exceeds {limits.StartupMaxMilliseconds} ms.");
        if (sample.WorkingSetMiB > limits.WorkingSetMaxMiB)
            violations.Add($"Working set {sample.WorkingSetMiB:0.0} MiB exceeds {limits.WorkingSetMaxMiB:0.0} MiB.");
        if (sample.PrivateMemoryMiB > limits.PrivateMemoryMaxMiB)
            violations.Add($"Private memory {sample.PrivateMemoryMiB:0.0} MiB exceeds {limits.PrivateMemoryMaxMiB:0.0} MiB.");

        return new PerformanceBudgetResult(violations);
    }

    public static PerformanceBudgetResult EvaluateFile(string label, long bytes, double maxMiB)
    {
        var actualMiB = bytes / 1024d / 1024d;
        return actualMiB <= maxMiB
            ? new PerformanceBudgetResult(Array.Empty<string>())
            : new PerformanceBudgetResult(new[] { $"{label} {actualMiB:0.0} MiB exceeds {maxMiB:0.0} MiB." });
    }
}
