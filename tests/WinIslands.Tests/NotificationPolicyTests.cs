using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class NotificationPolicyTests
{
    [Theory]
    [InlineData(0, 6, 6)]
    [InlineData(-1, 9, 9)]
    [InlineData(12, 6, 12)]
    [InlineData(1, 6, 2)]
    [InlineData(999, 6, 30)]
    [InlineData(0, 0, 6)]
    public void Duration_UsesExplicitThenDefaultAndClamps(int requested, int configured, int expected)
        => Assert.Equal(expected, NotificationPolicy.ResolveDurationSeconds(requested, configured));

    [Fact]
    public void FoldedId_IsStableForSameSourceAndTitle()
    {
        var a = NotificationPolicy.BuildCardId("app:mail", "  New Mail ", true);
        var b = NotificationPolicy.BuildCardId("app:mail", "new mail", true);
        Assert.Equal(a, b);
        Assert.NotEqual(a, NotificationPolicy.BuildCardId("app:mail", "Other", true));
    }

    [Fact]
    public void FoldDisabled_CreatesUniqueCards()
    {
        var a = NotificationPolicy.BuildCardId("app:mail", "New Mail", false);
        var b = NotificationPolicy.BuildCardId("app:mail", "New Mail", false);
        Assert.NotEqual(a, b);
        Assert.StartsWith("sys:app:mail:", a);
    }

    [Fact]
    public void MessengerCards_KeepAppLevelId()
    {
        Assert.Equal("sys:msg:qq:42", NotificationPolicy.BuildCardId("msg:qq:42", "A", true));
    }

    [Fact]
    public void Overflow_DropsLowestRank()
    {
        var ranks = new[] { 4, 9, 2, 7 };
        var idx = NotificationPolicy.FindOverflowIndex(ranks, x => x, _ => false);
        Assert.Equal(2, idx);
    }

    [Fact]
    public void Overflow_IgnoresExpiredEntries()
    {
        var items = new[] { (rank: 5, expired: true), (rank: 2, expired: false), (rank: 6, expired: false) };
        var idx = NotificationPolicy.FindOverflowIndex(items, x => x.rank, x => x.expired);
        Assert.Equal(1, idx);
    }

    [Fact]
    public void ShouldFold_RequiresSameIdOrTypeAndTitle()
    {
        var a = new IslandPush { Id = "1", Type = "info", Title = "Hello" };
        var b = new IslandPush { Id = "2", Type = "info", Title = " hello " };
        var c = new IslandPush { Id = "3", Type = "warning", Title = "Hello" };
        Assert.True(NotificationPolicy.ShouldFold(a, b, true));
        Assert.False(NotificationPolicy.ShouldFold(a, c, true));
        Assert.False(NotificationPolicy.ShouldFold(a, b, false));
    }
}
