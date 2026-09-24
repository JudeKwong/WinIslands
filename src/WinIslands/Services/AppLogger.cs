using System.IO;
using System.Text;

namespace WinIslands.Services;

/// <summary>
/// 轻量级、零依赖的文件日志器。每条日志写入一行到
/// %APPDATA%\WinIslands\logs\app-yyyyMMdd.log（超过 5MB 自动轮转为
/// app-yyyyMMdd-1.log、app-yyyyMMdd-2.log ……）。启动时自动清理 30 天前的旧日志。
/// 永不抛出异常。
/// </summary>
public static class AppLogger
{
    private static readonly object Gate = new();
    private static string _currentDay = string.Empty;
    private static StreamWriter? _writer;
    private static int _pendingLines;              // 未落盘行数（批量 flush，减少 UI 线程同步磁盘写）
    private static DateTime _lastFlushUtc = DateTime.UtcNow;
    private const int FlushThreshold = 20;         // 每 20 行强制落盘
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2); // 或每 2 秒落盘

    /// <summary>单文件最大字节数（5MB），超过后自动轮转到下一个序列文件。</summary>
    private const long MaxFileSize = 5 * 1024 * 1024;

    /// <summary>日志保留天数，超过则启动时自动清理。</summary>
    private const int MaxLogAgeDays = 30;

    /// <summary>当前序列号：0 表示 app-{day}.log，1+ 表示 app-{day}-{n}.log。</summary>
    private static int _currentSeq;

    /// <summary>当前已写入文件的近似字节数，用于判断是否触发轮转。</summary>
    private static long _currentFileSize;

    /// <summary>是否已执行过启动清理（只做一次）。</summary>
    private static bool _startupCleanupDone;

    public static void Info(string message) => Write("INFO", message);
    public static void Debug(string message) => Write("DEBUG", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                // 启动时清理旧日志（线程安全：在锁内只执行一次）
                EnsureStartupCleanup();

                var now = DateTime.Now;
                var day = now.ToString("yyyyMMdd");

                // 跨天或首次写入：打开当天日志文件
                if (_writer is null || day != _currentDay)
                    OpenWriter(day);

                var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                _writer!.WriteLine(line);
                _pendingLines++;
                _currentFileSize += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

                // ERROR/WARN 立即落盘：崩溃或启动失败时缓冲内容会随进程一起丢失，导致无从排查
                var urgent = level is "ERROR" or "WARN";
                if (urgent || _pendingLines >= FlushThreshold || DateTime.UtcNow - _lastFlushUtc >= FlushInterval)
                {
                    _writer.Flush();
                    _pendingLines = 0;
                    _lastFlushUtc = DateTime.UtcNow;
                }

                // 文件超过 5MB：轮转到下一个序列文件
                if (_currentFileSize >= MaxFileSize)
                    RotateWriter(day);
            }
        }
        catch
        {
            // 日志记录绝不能让应用崩溃。
        }
    }

    /// <summary>打开当天的日志文件，自动找到未满 5MB 的最新序列文件追加写入。</summary>
    private static void OpenWriter(string day)
    {
        _writer?.Dispose();

        _currentDay = day;

        // 从序列 0（app-{day}.log）开始查找第一个未满阈值的文件，没有则新建
        int seq = 0;
        while (true)
        {
            var path = GetLogPath(day, seq);
            if (!File.Exists(path)) break;          // 新文件，直接使用
            var len = new FileInfo(path).Length;
            if (len < MaxFileSize) break;           // 仍有空间
            seq++;                                 // 满了，尝试下一个序列
        }

        _currentSeq = seq;
        var fullPath = GetLogPath(day, seq);
        _writer = new StreamWriter(fullPath, append: true, Encoding.UTF8)
        {
            AutoFlush = false                       // 批量落盘：避免每次写日志同步刷磁盘卡顿（动画更流畅）
        };
        _currentFileSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
        _lastFlushUtc = DateTime.MinValue;          // 新建日志首行立即落盘，保证启动记录一定存在
    }

    /// <summary>当前文件已满，关闭并切换到下一个序列文件。</summary>
    private static void RotateWriter(string day)
    {
        try { _writer?.Flush(); } catch { /* 轮转前尽量落盘 */ }
        _writer?.Dispose();

        _currentSeq++;
        var newPath = GetLogPath(day, _currentSeq);
        _writer = new StreamWriter(newPath, append: true, Encoding.UTF8)
        {
            AutoFlush = false
        };
        _currentFileSize = 0;
        _pendingLines = 0;
        _lastFlushUtc = DateTime.UtcNow;
    }

    /// <summary>获取日志文件路径：序列 0 为 app-{day}.log，1+ 为 app-{day}-{n}.log。</summary>
    private static string GetLogPath(string day, int seq) =>
        seq == 0
            ? Path.Combine(AppPaths.LogsDir, $"app-{day}.log")
            : Path.Combine(AppPaths.LogsDir, $"app-{day}-{seq}.log");

    /// <summary>启动时执行一次：清理超过 30 天的旧日志文件。</summary>
    private static void EnsureStartupCleanup()
    {
        if (_startupCleanupDone) return;
        _startupCleanupDone = true;

        try
        {
            var dir = AppPaths.LogsDir;
            if (!Directory.Exists(dir)) return;

            var cutoff = DateTime.Now.AddDays(-MaxLogAgeDays);
            foreach (var file in Directory.EnumerateFiles(dir, "app-*.log"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime < cutoff)
                        info.Delete();
                }
                catch
                {
                    // 单个文件删除失败不影响其余清理。
                }
            }
        }
        catch
        {
            // 清理失败不影响日志写入主流程。
        }
    }
}
