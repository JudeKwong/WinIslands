using System;
using System.Collections.Generic;
using System.Linq;

namespace WinIslands.Services;

/// <summary>
/// Validates and de-duplicates user-configured global hotkeys. The detector is
/// pure so the settings window and unit tests share exactly one rule set.
/// </summary>
public static class HotkeyConflictDetector
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>Gestures that map to the same key combination, in canonical form.</summary>
    public static IReadOnlyList<string> FindConflicts(IEnumerable<(string Action, string Gesture)> bindings)
        => FindGrouped(bindings, invalid: false);

    /// <summary>Gestures that cannot be parsed by the hotkey service.</summary>
    public static IReadOnlyList<string> FindInvalid(IEnumerable<(string Action, string Gesture)> bindings)
        => FindGrouped(bindings, invalid: true);

    private static IReadOnlyList<string> FindGrouped(
        IEnumerable<(string Action, string Gesture)> bindings, bool invalid)
    {
        if (bindings is null) return Array.Empty<string>();

        var rows = bindings
            .Where(b => !string.IsNullOrWhiteSpace(b.Gesture))
            .Select(b => (b.Action, Raw: b.Gesture.Trim()))
            .ToList();

        if (invalid)
        {
            return rows
                .Where(b => !GlobalHotkeyService.TryParse(b.Raw, out _, out _))
                .Select(b => b.Raw)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return rows
            .Select(b => (b.Action, Canonical: Canonicalize(b.Raw)))
            .Where(b => b.Canonical.Length > 0)
            .GroupBy(b => b.Canonical, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.First().Canonical)
            .ToList();
    }

    /// <summary>Builds a deterministic token (modifier order and aliases do not matter).</summary>
    internal static string Canonicalize(string gesture)
    {
        if (!GlobalHotkeyService.TryParse(gesture, out var modifiers, out var vk)) return string.Empty;
        var parts = new List<string>(5);
        if ((modifiers & ModControl) != 0) parts.Add("CTRL");
        if ((modifiers & ModAlt) != 0) parts.Add("ALT");
        if ((modifiers & ModShift) != 0) parts.Add("SHIFT");
        if ((modifiers & ModWin) != 0) parts.Add("WIN");
        parts.Add("VK" + vk.ToString("X2"));
        return string.Join("+", parts);
    }
}
