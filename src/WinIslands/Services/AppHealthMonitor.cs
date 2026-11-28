using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace WinIslands.Services;

/// <summary>UI 线程健康状态。</summary>
public enum UiHealthStatus
{
    Healthy = 0,
    /// <summary>心跳明显延迟，但尚未达到“无响应”阈值。</summary>
    Degraded = 1,
    /// <summary>超过无响应阈值；此时只记录诊断，不强制结束用户进程。</summary>
    Unresponsive = 2,
}

public readonly record struct AppHealthSnapshot(
    UiHealthStatus Status,
    TimeSpan LastBeatAge,
    long DegradedCount,
    long UnresponsiveCount,
    long RecoveryCount,
    long ManagedMemoryMb,
    int ThreadCount,
    TimeSpan Uptime);

/// <summary>
/// 纯逻辑心跳看门狗：不依赖 WPF/计时器，便于单元测试。
/// 语义：Beat() 由 UI 线程周期调用；Evaluate() 由后台线程读取，判断 UI 是否卡住。
/// </summary>
public sealed class UiHeartbeatWatchdog
{
    public static readonly TimeSpan DefaultDegradedAfter = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultUnresponsiveAfter = TimeSpan.FromSeconds(12);

    private readonly TimeSpan _degradedAfter;
    private readonly TimeSpan _unresponsiveAfter;
    private long _beatCount;

    public UiHeartbeatWatchdog(TimeSpan? degradedAfter = null, TimeSpan? unresponsiveAfter = null)
    {
        _degradedAfter = degradedAfter ?? DefaultDegradedAfter;
        _unresponsiveAfter = unresponsiveAfter ?? DefaultUnresponsiveAfter;
        if (_unresponsiveAfter < _degradedAfter) _unresponsiveAfter = _degradedAfter;
    }

    public TimeSpan DegradedAfter => _degradedAfter;
    public TimeSpan UnresponsiveAfter => _unresponsiveAfter;
    public long BeatCount => Interlocked.Read(ref _beatCount);

    public void Beat(DateTime utcNow)
    {
        Interlocked.Exchange(ref _lastBeatUtcTicks, utcNow.Ticks);
        Interlocked.Increment(ref _beatCount);
    }

    private long _lastBeatUtcTicks = long.MinValue;

    public bool HasBeat => Interlocked.Read(ref _lastBeatUtcTicks) > long.MinValue;

    public TimeSpan SinceLastBeat(DateTime utcNow)
    {
        var ticks = Interlocked.Read(ref _lastBeatUtcTicks);
        if (ticks <= long.MinValue) return TimeSpan.Zero;
        var age = utcNow - new DateTime(ticks, DateTimeKind.Utc);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    public UiHealthStatus Evaluate(DateTime utcNow)
    {
        if (!HasBeat) return UiHealthStatus.Healthy;
        var age = SinceLastBeat(utcNow);
        if (age >= _unresponsiveAfter) return UiHealthStatus.Unresponsive;
        if (age >= _degradedAfter) return UiHealthStatus.Degraded;
        return UiHealthStatus.Healthy;
    }
}

/// <summary>
/// 进程内健康监测：Dispatcher 心跳 + 后台探针。
/// 设计原则：只做“观察与记录”，绝不强杀进程、绝不阻塞 UI。
/// </summary>
public sealed class AppHealthMonitor : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DumpCooldown = TimeSpan.FromMinutes(10);

    private readonly Dispatcher _dispatcher;
    private readonly UiHeartbeatWatchdog _watchdog;
    private readonly DispatcherTimer _beatTimer;
    private readonly Timer _probeTimer;
    private readonly object _gate = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private UiHealthStatus _lastStatus = UiHealthStatus.Healthy;
    private long _degradedCount;
    private long _unresponsiveCount;
    private long _recoveryCount;
    private DateTime _lastDumpUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>最近一次快照，供“诊断信息”页面读取。</summary>
    public static AppHealthSnapshot? Current { get; private set; }

    public AppHealthMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _watchdog = new UiHeartbeatWatchdog();
        _beatTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _beatTimer.Tick += (_, _) => _watchdog.Beat(DateTime.UtcNow);
        _probeTimer = new Timer(Probe, null, ProbeInterval, ProbeInterval);
    }

    public void Start()
    {
        _watchdog.Beat(DateTime.UtcNow);
        _beatTimer.Start();
        AppLogger.Info("Health monitor started (UI heartbeat + hang probe).");
    }

    private void Probe(object? _)
    {
        if (_disposed) return;
        try
        {
            var now = DateTime.UtcNow;
            var status = _watchdog.Evaluate(now);
            var age = _watchdog.SinceLastBeat(now);

            if (status == UiHealthStatus.Healthy && _lastStatus != UiHealthStatus.Healthy)
            {
                Interlocked.Increment(ref _recoveryCount);
                AppLogger.Info($"UI thread recovered after {age.TotalMilliseconds:0} ms.");
            }
            else if (status == UiHealthStatus.Degraded && _lastStatus == UiHealthStatus.Healthy)
            {
                Interlocked.Increment(ref _degradedCount);
                AppLogger.Warn($"UI thread heartbeat delayed: {age.TotalMilliseconds:0} ms " +
                               $"(managed={GC.GetTotalMemory(false) / 1024 / 1024} MB, threads={Process.GetCurrentProcess().Threads.Count}).");
            }
            else if (status == UiHealthStatus.Unresponsive && _lastStatus != UiHealthStatus.Unresponsive)
            {
                Interlocked.Increment(ref _unresponsiveCount);
                AppLogger.Warn($"UI thread unresponsive for {age.TotalSeconds:0.0}s; capturing diagnostic snapshot.");
                if (now - _lastDumpUtc >= DumpCooldown)
                {
                    _lastDumpUtc = now;
                    // minidump 在后台线程写入，不触碰 UI 线程；失败也不影响主流程。
                    ThreadPool.QueueUserWorkItem(_ => CrashDump.TryWriteDump("uihang"));
                }
            }

            _lastStatus = status;
            Current = BuildSnapshot(status, age);
        }
        catch
        {
            // 健康监测自身绝不抛异常。
        }
    }

    private AppHealthSnapshot BuildSnapshot(UiHealthStatus status, TimeSpan age)
    {
        long memoryMb = 0;
        var threads = 0;
        try
        {
            memoryMb = GC.GetTotalMemory(false) / 1024 / 1024;
            threads = Process.GetCurrentProcess().Threads.Count;
        }
        catch { }

        return new AppHealthSnapshot(
            status,
            age,
            Interlocked.Read(ref _degradedCount),
            Interlocked.Read(ref _unresponsiveCount),
            Interlocked.Read(ref _recoveryCount),
            memoryMb,
            threads,
            DateTime.UtcNow - _startedUtc);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _beatTimer.Stop(); } catch { }
        try { _probeTimer.Dispose(); } catch { }
    }
}

/// <summary>
/// 重启风暴保护：崩溃自动重启最多允许 <c>maxRestarts</c> 次 / <c>window</c> 时间窗。
/// 防止“启动即崩溃 → 自动重启 → 再崩溃”的死循环。
/// </summary>
public sealed class RestartStormGuard
{
    private readonly int _maxRestarts;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _restarts = new();
    private readonly object _gate = new();

    public RestartStormGuard(int maxRestarts = 3, TimeSpan? window = null)
    {
        _maxRestarts = Math.Max(1, maxRestarts);
        _window = window ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>尝试登记一次重启；返回 false 表示已进入风暴保护，不应再自动重启。</summary>
    public bool TryRecord(DateTime utcNow)
    {
        lock (_gate)
        {
            while (_restarts.Count > 0 && utcNow - _restarts.Peek() > _window)
                _restarts.Dequeue();
            if (_restarts.Count >= _maxRestarts) return false;
            _restarts.Enqueue(utcNow);
            return true;
        }
    }

    public int CountInWindow(DateTime utcNow)
    {
        lock (_gate)
        {
            while (_restarts.Count > 0 && utcNow - _restarts.Peek() > _window)
                _restarts.Dequeue();
            return _restarts.Count;
        }
    }
}