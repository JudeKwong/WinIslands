using System;
using System.Threading;

namespace WinIslands.Services;

public enum PomodoroPhase { Stopped, Work, Break }

/// <summary>
/// 番茄钟/倒计时：1 秒驱动，支持暂停/继续，组件显示 mm:ss，到期触发事件（由上层弹通知）。
/// 跨平台版：用 System.Timers.Timer 替代 WPF DispatcherTimer；可选传入 SynchronizationContext，
/// 让事件回到 UI 线程（Avalonia 传入 AvaloniaSynchronizationContext）。
/// </summary>
public sealed class PomodoroService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly SynchronizationContext? _sync;
    private DateTime _endTime;
    private TimeSpan _remaining;

    public PomodoroService(SynchronizationContext? syncContext = null)
    {
        _sync = syncContext;
        _timer = new System.Timers.Timer(1000) { AutoReset = true };
        _timer.Elapsed += (_, _) => OnTick();
    }

    /// <summary>把事件派发到 UI 线程（未提供 SynchronizationContext 时直接执行）。</summary>
    private void Raise(Action action)
    {
        if (_sync is null || ReferenceEquals(_sync, SynchronizationContext.Current)) action();
        else _sync.Post(_ => action(), null);
    }

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Stopped;

    /// <summary>是否已暂停（运行中且非暂停 = 计时中）。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>剩余时间字符串 mm:ss（或 h:mm:ss）。</summary>
    public string ClockText
    {
        get
        {
            var r = _remaining;
            return r.TotalHours >= 1 ? $"{(int)r.TotalHours:00}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes:00}:{r.Seconds:00}";
        }
    }

    public event Action? Tick;
    /// <summary>阶段结束（参数为刚结束的阶段）。</summary>
    public event Action<PomodoroPhase>? Completed;

    public void StartWork(int minutes)
    {
        Start(TimeSpan.FromMinutes(Math.Max(1, minutes)), PomodoroPhase.Work);
    }

    public void StartBreak(int minutes)
    {
        Start(TimeSpan.FromMinutes(Math.Max(1, minutes)), PomodoroPhase.Break);
    }

    public void Stop()
    {
        Phase = PomodoroPhase.Stopped;
        IsPaused = false;
        _timer.Stop();
        Raise(() => Tick?.Invoke());
    }

    /// <summary>暂停：冻结剩余时间，计时器停止。</summary>
    public void Pause()
    {
        if (Phase == PomodoroPhase.Stopped || IsPaused) return;
        IsPaused = true;
        _remaining = _endTime - DateTime.Now;
        if (_remaining < TimeSpan.Zero) _remaining = TimeSpan.Zero;
        _timer.Stop();
        Raise(() => Tick?.Invoke());
    }

    /// <summary>继续：从暂停点恢复计时。</summary>
    public void Resume()
    {
        if (Phase == PomodoroPhase.Stopped || !IsPaused) return;
        IsPaused = false;
        _endTime = DateTime.Now + _remaining;
        _timer.Start();
        Raise(() => Tick?.Invoke());
    }

    /// <summary>暂停/继续切换（灵动岛上点击计时器组件时调用）。</summary>
    public void TogglePause()
    {
        if (Phase == PomodoroPhase.Stopped) return;
        if (IsPaused) Resume(); else Pause();
    }

    private void Start(TimeSpan duration, PomodoroPhase phase)
    {
        Phase = phase;
        IsPaused = false;
        _remaining = duration;
        _endTime = DateTime.Now + duration;
        _timer.Start();
        Raise(() => Tick?.Invoke());
    }

    private void OnTick()
    {
        if (IsPaused) return; // 保险：暂停期间定时器已停止
        _remaining = _endTime - DateTime.Now;
        if (_remaining < TimeSpan.Zero) _remaining = TimeSpan.Zero;
        Raise(() => Tick?.Invoke());
        if (_remaining == TimeSpan.Zero)
        {
            _timer.Stop();
            var finished = Phase;
            Phase = PomodoroPhase.Stopped;
            IsPaused = false;
            Raise(() => Completed?.Invoke(finished));
        }
    }

    public void Dispose() { _timer.Stop(); _timer.Dispose(); }
}