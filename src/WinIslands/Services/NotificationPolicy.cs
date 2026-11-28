using System.Security.Cryptography;
using System.Text;

namespace WinIslands.Services;

/// <summary>
/// Notification timing, folding and queue rules shared by the island view model and tests.
/// Kept free of WPF/Windows dependencies so the behaviour is deterministic and testable.
/// </summary>
public static class NotificationPolicy
{
    public const int MinDurationSeconds = 2;
    public const int MaxDurationSeconds = 30;
    public const int MinQueueDepth = 3;
    public const int MaxQueueDepth = 20;

    /// <summary>Resolve an explicit per-notification duration, falling back to the user default.</summary>
    public static int ResolveDurationSeconds(int requestedSeconds, int configuredDefaultSeconds)
    {
        var seconds = requestedSeconds > 0 ? requestedSeconds : configuredDefaultSeconds;
        return Math.Clamp(seconds <= 0 ? 6 : seconds, MinDurationSeconds, MaxDurationSeconds);
    }

    /// <summary>Same as above for optional per-push durations (null = use the configured default).</summary>
    public static int ResolveDurationSeconds(int? requestedSeconds, int configuredDefaultSeconds)
        => ResolveDurationSeconds(requestedSeconds ?? 0, configuredDefaultSeconds);

    /// <summary>
    /// Stable card id used when folding is enabled. Different titles from the same source
    /// remain separate; repeated deliveries of the same source+title increment the queue count.
    /// Messenger cards keep their existing app-level key.
    /// </summary>
    public static string BuildCardId(string sourceId, string? title, bool foldEnabled)
    {
        var source = (sourceId ?? string.Empty).Trim();
        if (source.StartsWith("msg:", StringComparison.OrdinalIgnoreCase))
            return "sys:" + source;

        var folded = "sys:" + source + ":" + NormalizeTitle(title);
        return foldEnabled ? folded : folded + ":" + Guid.NewGuid().ToString("N");
    }

    public static string NormalizeTitle(string? title)
    {
        var normalized = (title ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return "untitled";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    public static int ClampQueueDepth(int configured) => Math.Clamp(configured, MinQueueDepth, MaxQueueDepth);

    /// <summary>Returns the index that should be dropped when the queue is over capacity.</summary>
    public static int FindOverflowIndex<T>(IReadOnlyList<T> items, Func<T, int> rank, Func<T, bool> expired)
    {
        if (items.Count == 0) return -1;
        var index = -1;
        var lowestRank = int.MaxValue;
        for (var i = 0; i < items.Count; i++)
        {
            if (expired(items[i])) continue;
            var itemRank = rank(items[i]);
            if (index < 0 || itemRank < lowestRank)
            {
                index = i;
                lowestRank = itemRank;
            }
        }
        return index;
    }

    /// <summary>True when a new notification should replace/merge into the existing queue entry.</summary>
    public static bool ShouldFold(IslandPush existing, IslandPush incoming, bool foldEnabled)
    {
        if (!foldEnabled) return false;
        return string.Equals(existing.Id, incoming.Id, StringComparison.Ordinal)
            || (string.Equals(existing.Type, incoming.Type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Title?.Trim(), incoming.Title?.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
