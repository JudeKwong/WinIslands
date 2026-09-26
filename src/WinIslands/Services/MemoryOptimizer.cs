using System.Runtime.InteropServices;

namespace WinIslands.Services;

/// <summary>
/// 低频工作集回收：只在无媒体、未展开且内存偏高时执行，避免影响动画和播放。
/// </summary>
public static class MemoryOptimizer
{
    private const long TrimThresholdBytes = 180L * 1024 * 1024;
    private const long MinIntervalMs = 5L * 60 * 1000;
    private static long _lastTrimTicks;

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>请求一次低优先级工作集回收；重复请求会被节流。</summary>
    public static void RequestTrim()
    {
        var now = Environment.TickCount64;
        if (Environment.WorkingSet < TrimThresholdBytes) return;
        if (now - Interlocked.Read(ref _lastTrimTicks) < MinIntervalMs) return;
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
}