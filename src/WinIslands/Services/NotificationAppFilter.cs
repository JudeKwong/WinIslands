namespace WinIslands.Services;

/// <summary>通知来源过滤模式：全部允许 / 仅允许列表 / 禁止列表。</summary>
public enum NotificationAppFilterMode
{
    All = 0,
    Whitelist = 1,
    Blacklist = 2,
}

/// <summary>
/// 通知来源过滤：按应用显示名或 AppUserModelId 匹配，大小写不敏感、支持片段包含。
/// 纯逻辑，便于单元测试。
/// </summary>
public static class NotificationAppFilter
{
    /// <summary>使用用户设置判断某个应用的通知是否允许显示。</summary>
    public static bool IsAllowed(AppSettings? settings, string? appDisplayName, string? appUserModelId)
        => IsAllowed(settings?.NotificationAppFilterMode ?? NotificationAppFilterMode.All,
            settings?.NotificationAppFilters, appDisplayName, appUserModelId);

    public static bool IsAllowed(NotificationAppFilterMode mode, IEnumerable<string>? filters,
        string? appDisplayName, string? appUserModelId)
    {
        var list = filters?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList()
            ?? new List<string>();
        // 未配置任何条目时不做拦截，避免用户误把全部通知静音。
        if (mode == NotificationAppFilterMode.All || list.Count == 0) return true;

        var matched = MatchesAny(list, appDisplayName, appUserModelId);
        return mode == NotificationAppFilterMode.Whitelist ? matched : !matched;
    }

    /// <summary>列表中的任意条目命中应用显示名或 AUMID 即视为匹配。</summary>
    public static bool MatchesAny(IEnumerable<string> filters, string? appDisplayName, string? appUserModelId)
    {
        var displayName = appDisplayName ?? string.Empty;
        var aumid = appUserModelId ?? string.Empty;
        foreach (var filter in filters)
        {
            var needle = filter?.Trim();
            if (string.IsNullOrEmpty(needle)) continue;
            if (displayName.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            if (aumid.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}