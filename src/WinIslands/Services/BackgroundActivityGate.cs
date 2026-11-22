using System.Windows.Threading;

namespace WinIslands.Services;

/// <summary>可被系统状态暂停/恢复的后台轮询组件。</summary>
public interface IBackgroundPausable
{
    /// <summary>锁屏 / 全屏 / 省电时暂停；恢复后继续。</summary>
    void SetBackgroundSuspended(bool suspended);
}

/// <summary>
/// 全局后台活动闸门（3.1.7）。
///
/// 桌面常驻应用最常见的耗电来源不是功能本身，而是「用户看不见时仍在轮询」：
/// 锁屏、全屏游戏/视频、省电模式、灵动岛隐藏时，剪贴板/键盘/通知中心等定时器
/// 依旧每 0.4~2 秒唤醒一次 UI 线程。该闸门把这些状态集中起来，由 App 一处更新，
/// 各后台服务按闸门暂停或降频。
/// </summary>
public sealed class BackgroundActivityGate
{
    public static BackgroundActivityGate Current { get; } = new();

    private readonly List<WeakReference<IBackgroundPausable>> _pausables = new();
    private bool _sessionLocked;
    private bool _fullScreen;
    private bool _islandVisible = true;
    private bool _expanded;
    private bool _batterySaver;
    private bool _onBattery;
    private bool _appliedSuspended;

    public bool SessionLocked => _sessionLocked;
    public bool FullScreen => _fullScreen;
    public bool IslandVisible => _islandVisible;
    public bool Expanded => _expanded;
    public bool BatterySaver => _batterySaver;
    public bool OnBattery => _onBattery;

    /// <summary>真的没有理由工作时才暂停：锁屏、全屏（岛已隐藏）、省电且未展开。</summary>
    public bool BackgroundSuspended =>
        _sessionLocked || _fullScreen || (_batterySaver && !_expanded);

    /// <summary>1 = 全速；2 = 岛隐藏时降半速；0 = 暂停。</summary>
    public double PollingMultiplier
    {
        get
        {
            if (BackgroundSuspended) return 0;
            return _islandVisible || _expanded ? 1.0 : 2.0;
        }
    }

    public event EventHandler? Changed;

    public void Register(IBackgroundPausable pausable)
    {
        if (pausable is null) return;
        lock (_pausables)
        {
            _pausables.RemoveAll(w => !w.TryGetTarget(out _));
            if (_pausables.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, pausable))) return;
            _pausables.Add(new WeakReference<IBackgroundPausable>(pausable));
        }
        SafeApply(pausable, BackgroundSuspended);
    }

    public void SetIslandState(bool visible, bool expanded)
    {
        if (_islandVisible == visible && _expanded == expanded) return;
        _islandVisible = visible;
        _expanded = expanded;
        Apply();
    }

    public void SetSessionLocked(bool locked)
    {
        if (_sessionLocked == locked) return;
        _sessionLocked = locked;
        Apply();
    }

    public void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen) return;
        _fullScreen = fullScreen;
        Apply();
    }

    public void SetPowerState(bool onBattery, bool batterySaver)
    {
        if (_onBattery == onBattery && _batterySaver == batterySaver) return;
        _onBattery = onBattery;
        _batterySaver = batterySaver;
        Apply();
    }

    /// <summary>降频辅助：返回本次 tick 是否应真正执行（后台暂停时恒为 false）。</summary>
    public bool ShouldRunTick(long tick)
    {
        if (BackgroundSuspended) return false;
        var multiplier = PollingMultiplier;
        if (multiplier <= 1.0) return true;
        var step = (long)Math.Round(multiplier);
        return step <= 1 || tick % step == 0;
    }

    private void Apply()
    {
        var suspended = BackgroundSuspended;
        if (suspended != _appliedSuspended)
        {
            _appliedSuspended = suspended;
            List<IBackgroundPausable> targets;
            lock (_pausables)
            {
                _pausables.RemoveAll(w => !w.TryGetTarget(out _));
                targets = _pausables.Select(w => { w.TryGetTarget(out var t); return t; })
                    .Where(t => t is not null).Select(t => t!).ToList();
            }
            foreach (var target in targets) SafeApply(target, suspended);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void SafeApply(IBackgroundPausable target, bool suspended)
    {
        try { target.SetBackgroundSuspended(suspended); }
        catch (Exception ex) { AppLogger.Warn("Background pause failed: " + ex.Message); }
    }
}
