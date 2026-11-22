namespace WinIslands.Services;

/// <summary>
/// Decides whether the first-run onboarding should be shown.
/// Automation and diagnostic launches must never be blocked by a modal window.
/// </summary>
public static class OnboardingPolicy
{
    private static readonly string[] SuppressingFlags =
    {
        "--diagnose",
        "--demo",
        "--settings",
        "--collapsed",
        "--expand",
    };

    private static readonly string[] SuppressingPrefixes =
    {
        "--screenshot=",
        "--screenshot-scale=",
        "--page=",
        "--demo-at=",
        "--collapse-frames-diag=",
        "--roundtrip-diag=",
        "--stress-island",
    };

    public static bool ShouldShow(AppSettings settings, IEnumerable<string>? args)
    {
        if (settings.OnboardingCompleted)
            return false;

        if (args is null)
            return true;

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            if (SuppressingFlags.Any(flag => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (SuppressingPrefixes.Any(prefix => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }
}
