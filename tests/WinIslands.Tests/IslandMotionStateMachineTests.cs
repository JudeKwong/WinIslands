using WinIslands.UI;
using Xunit;

namespace WinIslands.Tests;

/// <summary>
/// 3.1.7 展开/收起状态机回归测试。
/// 核心契约：只有最新一次请求可以提交最终形态，旧动画的迟到回调一律作废。
/// </summary>
public class IslandMotionStateMachineTests
{
    [Fact]
    public void Begin_ReturnsCurrentTicket()
    {
        var sm = new IslandMotionStateMachine();
        var ticket = sm.Begin(true);

        Assert.True(ticket.IsValid);
        Assert.True(sm.InFlight);
        Assert.Equal(IslandMotionState.Expanded, sm.Target);
        Assert.True(sm.IsCurrent(ticket));
    }

    [Fact]
    public void NewerRequest_InvalidatesOlderTicket()
    {
        var sm = new IslandMotionStateMachine();
        var expand = sm.Begin(true);
        var collapse = sm.Begin(false);

        Assert.False(sm.IsCurrent(expand));
        Assert.True(sm.IsCurrent(collapse));
        Assert.False(sm.TrySettle(expand));   // 旧动画迟到：必须被丢弃
        Assert.True(sm.InFlight);             // 仍在新请求中
        Assert.True(sm.TrySettle(collapse));
        Assert.False(sm.InFlight);
        Assert.Equal(IslandMotionState.Compact, sm.Settled);
    }

    [Fact]
    public void Settle_IsIdempotent_ForCurrentTicket()
    {
        var sm = new IslandMotionStateMachine();
        var ticket = sm.Begin(true);
        Assert.True(sm.TrySettle(ticket));
        Assert.True(sm.TrySettle(ticket));
        Assert.Equal(IslandMotionState.Expanded, sm.Settled);
        Assert.False(sm.InFlight);
    }

    [Fact]
    public void Cancel_InvalidatesEverything()
    {
        var sm = new IslandMotionStateMachine();
        var ticket = sm.Begin(true);
        sm.Cancel();

        Assert.False(sm.IsCurrent(ticket));
        Assert.False(sm.TrySettle(ticket));
        Assert.False(sm.InFlight);
        Assert.Equal(IslandMotionState.Compact, sm.Target);
    }

    [Fact]
    public void Reset_ForcesStateWithoutAnimation()
    {
        var sm = new IslandMotionStateMachine();
        sm.Begin(true);
        sm.Reset(false);

        Assert.False(sm.InFlight);
        Assert.Equal(IslandMotionState.Compact, sm.Settled);
        Assert.Equal(IslandMotionState.Compact, sm.Target);
    }

    [Fact]
    public void RapidToggle_OnlyLastRequestWins()
    {
        var sm = new IslandMotionStateMachine();
        IslandMotionTicket last = IslandMotionTicket.None;

        // 模拟用户快速连点 50 次：每次开始新请求，并让所有旧请求都「迟到收尾」。
        for (var i = 0; i < 50; i++)
        {
            var previous = last;
            last = sm.Begin(i % 2 == 0);
            if (previous.IsValid)
                Assert.False(sm.TrySettle(previous));
        }

        Assert.True(sm.TrySettle(last));
        Assert.Equal(IslandMotionState.Compact, sm.Settled); // i=49 -> false
        Assert.False(sm.InFlight);
    }

    [Fact]
    public void RandomSequence_AlwaysSettlesToFinalTarget()
    {
        var rng = new Random(20261122);
        var sm = new IslandMotionStateMachine();
        var expected = IslandMotionState.Compact;
        var tickets = new List<IslandMotionTicket>();

        for (var i = 0; i < 500; i++)
        {
            var expanded = rng.Next(2) == 0;
            expected = IslandMotionStateMachine.StateOf(expanded);
            tickets.Add(sm.Begin(expanded));

            // 随机让一个旧凭证尝试收尾（模拟 Storyboard/弹簧的迟到回调）
            if (tickets.Count > 1)
            {
                var stale = tickets[rng.Next(tickets.Count - 1)];
                Assert.False(sm.TrySettle(stale));
            }
        }

        var last = tickets[^1];
        Assert.True(sm.IsCurrent(last));
        Assert.True(sm.TrySettle(last));
        Assert.Equal(expected, sm.Settled);
        Assert.False(sm.InFlight);
    }

    [Fact]
    public void RandomSequence_GenerationIsMonotonic()
    {
        var sm = new IslandMotionStateMachine();
        var previous = sm.Generation;
        for (var i = 0; i < 100; i++)
        {
            var ticket = sm.Begin(i % 2 == 0);
            Assert.True(ticket.Generation > previous);
            previous = ticket.Generation;
        }
    }
}
