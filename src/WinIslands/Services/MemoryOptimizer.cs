using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinIslands.Services;

/// <summary>
/// 低频工作集回收：只在无媒体、未展开且内存偏高时执行，避免影响动画和播放。
/// </summary>
public static class MemoryOptimizer
{
    private const long TrimPrivateThresholdBytes = 40L * 1024 * 1024;
    private const long TrimWorkingSetThresholdBytes = 96L * 1024 * 1024;
    private const long MinIntervalMs = 2L * 60 * 1000;
    private const long IdlePrivateThresholdBytes = 28L * 1024 * 1024; // 2.2.12：空闲态（无媒体/未展开）更激进回收
    private const long IdleWorkingSetThresholdBytes = 64L * 1024 * 1024;
    private const long IdleMinIntervalMs = 60L * 1000;
    private static long _lastTrimTicks;

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>请求一次低优先级工作集回收；重复请求会被节流。</summary>
    public static void RequestTrim() => RequestTrim(idle: false);
    /// <summary>2.2.12：idle=true 表示当前无媒体、未展开（低交互），用更低的门限与更短的间隔回收，进一步压低空闲内存。</summary>
    public static void RequestTrim(bool idle)
    {
        var now = Environment.TickCount64;
        using var process = Process.GetCurrentProcess();
        var workingSet = Environment.WorkingSet;
        var privateBytes = process.PrivateMemorySize64;
        if (!ShouldTrim(workingSet, privateBytes, Interlocked.Read(ref _lastTrimTicks), now, idle)) return;
        Interlocked.Exchange(ref _lastTrimTicks, now);

        _ = Task.Run(() =>
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
                _ = EmptyWorkingSet(GetCurrentProcess());
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Memory trim skipped: {ex.Message}");
            }
        });
    }

    internal static bool ShouldTrim(long workingSet, long privateBytes, long lastTrimTicks, long nowTicks)
        => ShouldTrim(workingSet, privateBytes, lastTrimTicks, nowTicks, idle: false);

    /// <summary>2.2.12：空闲态用更积极的门限（WS 64MB / 私有 28MB / 60s），活动态保持原门限（96/40/120s）。</summary>
    internal static bool ShouldTrim(long workingSet, long privateBytes, long lastTrimTicks, long nowTicks, bool idle)
    {
        var minInterval = idle ? IdleMinIntervalMs : MinIntervalMs;
        if (nowTicks - lastTrimTicks < minInterval) return false;
        var wsThreshold = idle ? IdleWorkingSetThresholdBytes : TrimWorkingSetThresholdBytes;
        var pbThreshold = idle ? IdlePrivateThresholdBytes : TrimPrivateThresholdBytes;
        return workingSet >= wsThreshold && privateBytes >= pbThreshold;
    }

}
