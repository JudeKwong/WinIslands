using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

/// <summary>3.1.7 后台活动闸门：锁屏/全屏/省电暂停，岛隐藏时降频。</summary>
public class BackgroundActivityGateTests
{
    private sealed class Probe : IBackgroundPausable
    {
        public bool Suspended { get; private set; }
        public int Calls { get; private set; }
        public void SetBackgroundSuspended(bool suspended) { Suspended = suspended; Calls++; }
    }

    [Fact]
    public void Default_IsFullSpeed()
    {
        var gate = new BackgroundActivityGate();
        Assert.False(gate.BackgroundSuspended);
        Assert.Equal(1.0, gate.PollingMultiplier);
        Assert.True(gate.ShouldRunTick(1));
    }

    [Fact]
    public void HiddenIsland_HalvesPollingRate()
    {
        var gate = new BackgroundActivityGate();
        gate.SetIslandState(visible: false, expanded: false);

        Assert.False(gate.BackgroundSuspended);
        Assert.Equal(2.0, gate.PollingMultiplier);
        Assert.False(gate.ShouldRunTick(1));
        Assert.True(gate.ShouldRunTick(2));
        Assert.False(gate.ShouldRunTick(3));
        Assert.True(gate.ShouldRunTick(4));
    }

    [Fact]
    public void SessionLock_SuspendsEverything()
    {
        var gate = new BackgroundActivityGate();
        gate.SetSessionLocked(true);
        Assert.True(gate.BackgroundSuspended);
        Assert.Equal(0, gate.PollingMultiplier);
        Assert.False(gate.ShouldRunTick(100));

        gate.SetSessionLocked(false);
        Assert.False(gate.BackgroundSuspended);
    }

    [Fact]
    public void FullScreen_SuspendsEverything()
    {
        var gate = new BackgroundActivityGate();
        gate.SetFullScreen(true);
        Assert.True(gate.BackgroundSuspended);

        gate.SetFullScreen(false);
        Assert.False(gate.BackgroundSuspended);
    }

    [Fact]
    public void BatterySaver_SuspendsOnlyWhenCollapsed()
    {
        var gate = new BackgroundActivityGate();
        gate.SetPowerState(onBattery: true, batterySaver: true);
        Assert.True(gate.BackgroundSuspended);

        // 用户主动展开时恢复，保证交互不受省电策略影响。
        gate.SetIslandState(visible: true, expanded: true);
        Assert.False(gate.BackgroundSuspended);
        Assert.Equal(1.0, gate.PollingMultiplier);
    }

    [Fact]
    public void Pausable_ReceivesCurrentStateOnRegister()
    {
        var gate = new BackgroundActivityGate();
        gate.SetSessionLocked(true);
        var probe = new Probe();

        gate.Register(probe);

        Assert.True(probe.Suspended);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void Pausable_IsNotifiedOnEveryEffectiveChange()
    {
        var gate = new BackgroundActivityGate();
        var probe = new Probe();
        gate.Register(probe);

        gate.SetSessionLocked(true);
        Assert.True(probe.Suspended);

        gate.SetFullScreen(true);   // 已处于暂停：不重复通知
        Assert.Equal(2, probe.Calls);

        gate.SetSessionLocked(false);
        Assert.True(probe.Suspended); // 全屏仍在

        gate.SetFullScreen(false);
        Assert.False(probe.Suspended);
    }

    [Fact]
    public void Changed_Event_FiresOnStateChangeOnly()
    {
        var gate = new BackgroundActivityGate();
        var count = 0;
        gate.Changed += (_, _) => count++;

        gate.SetSessionLocked(true);
        gate.SetSessionLocked(true);
        gate.SetFullScreen(true);
        gate.SetSessionLocked(false);

        Assert.Equal(3, count);
    }
}
