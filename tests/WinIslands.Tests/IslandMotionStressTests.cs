using WinIslands.UI;
using Xunit;

namespace WinIslands.Tests;

/// <summary>
/// 3.1.8 稳定性压力回归：连续快速展开/收起 2000 次，
/// 任意旧动画凭证都不能改写最终形态，避免「收回后仍是展开尺寸」或圆角丢失。
/// </summary>
public sealed class IslandMotionStressTests
{
    [Fact]
    public void RapidToggles_OnlyLatestTicketSettles()
    {
        var random = new Random(20261128);
        var sm = new IslandMotionStateMachine();
        var stale = new List<IslandMotionTicket>();

        for (var i = 0; i < 2000; i++)
        {
            var target = random.Next(2) == 0;
            var ticket = sm.Begin(target);
            stale.Add(ticket);

            // 模拟旧动画迟到回调：绝大多数应该被拒绝。
            if (stale.Count > 4)
            {
                var old = stale[random.Next(stale.Count - 1)];
                Assert.False(sm.TrySettle(old));
            }

            // 有 20% 概率动画立即完成（正常路径）。
            if (random.Next(5) == 0)
            {
                Assert.True(sm.TrySettle(ticket));
                Assert.False(sm.InFlight);
                Assert.Equal(IslandMotionStateMachine.StateOf(target), sm.Settled);
            }
        }

        // 收尾：最后一次 Begin 必须能正常提交，且不再处于进行中。
        var finalTicket = sm.Begin(true);
        Assert.True(sm.TrySettle(finalTicket));
        Assert.False(sm.InFlight);
        Assert.Equal(IslandMotionState.Expanded, sm.Settled);
        Assert.Equal(IslandMotionState.Expanded, sm.Target);
    }

    [Fact]
    public void Cancel_InvalidatesAllOutstandingTickets()
    {
        var sm = new IslandMotionStateMachine();
        var first = sm.Begin(true);
        var second = sm.Begin(false);
        sm.Cancel();

        Assert.False(sm.TrySettle(first));
        Assert.False(sm.TrySettle(second));
        Assert.False(sm.InFlight);
        Assert.Equal(sm.Settled, sm.Target);
    }

    [Fact]
    public void Reset_AllowsCleanRestartAfterRapidSequence()
    {
        var sm = new IslandMotionStateMachine();
        for (var i = 0; i < 500; i++) sm.Begin(i % 2 == 0);
        sm.Reset(false);

        Assert.False(sm.InFlight);
        Assert.Equal(IslandMotionState.Compact, sm.Settled);
        Assert.Equal(IslandMotionState.Compact, sm.Target);

        var ticket = sm.Begin(true);
        Assert.True(sm.TrySettle(ticket));
        Assert.Equal(IslandMotionState.Expanded, sm.Settled);
    }
}