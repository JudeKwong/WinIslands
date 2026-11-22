using WinIslands.Services;

namespace WinIslands.Tests;

public class NotificationAppFilterTests
{
    [Fact]
    public void AllMode_AlwaysAllows()
    {
        Assert.True(NotificationAppFilter.IsAllowed(
            NotificationAppFilterMode.All, new[] { "QQ" }, "QQ", "Tencent.QQ"));
    }

    [Fact]
    public void Whitelist_AllowsOnlyMatchingApps()
    {
        var filters = new[] { "QQ", "WeChat" };
        Assert.True(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Whitelist, filters, "QQ", "Tencent.QQ"));
        Assert.False(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Whitelist, filters, "Steam", "Valve.Steam"));
    }

    [Fact]
    public void Blacklist_BlocksMatchingApps()
    {
        var filters = new[] { "Steam" };
        Assert.False(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Blacklist, filters, "Steam", "Valve.Steam"));
        Assert.True(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Blacklist, filters, "QQ", "Tencent.QQ"));
    }

    [Fact]
    public void Matching_IsCaseInsensitive_AndPartial()
    {
        Assert.True(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Whitelist,
            new[] { "wechat" }, "WeChat", "Tencent.WeChat"));
        Assert.True(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Whitelist,
            new[] { "tencent.qq" }, "QQ", "Tencent.QQ"));
    }

    [Fact]
    public void EmptyFilterList_NeverBlocks()
    {
        Assert.True(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Whitelist,
            Array.Empty<string>(), "Steam", "Valve.Steam"));
        Assert.True(NotificationAppFilter.IsAllowed(NotificationAppFilterMode.Blacklist,
            new[] { "  " }, "Steam", "Valve.Steam"));
    }
}