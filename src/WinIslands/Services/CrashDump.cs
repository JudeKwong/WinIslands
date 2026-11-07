using System.IO;
using System.Runtime.InteropServices;

namespace WinIslands.Services;

/// <summary>
/// 崩溃可诊断性：在托管异常处理链上写 minidump + 记录 first-chance SEH 类异常。
/// 仅用于诊断——0xc0000005/0x80131506 这类 CLR 内部错误会绕过所有托管 handler，
/// 因此配合 OS 层 WER LocalDumps（注册表）才能完整捕获。
/// </summary>
public static class CrashDump
{
    [DllImport("dbghelp.dll", SetLastError = true, EntryPoint = "MiniDumpWriteDump")]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, IntPtr hFile,
        MiniDumpType dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    [Flags]
    private enum MiniDumpType : uint
    {
        Normal = 0x00000000,
        WithDataSegs = 0x00000001,
        WithFullMemory = 0x00000002,
        WithHandleData = 0x00000004,
        WithThreadInfo = 0x00001000,
        WithUnloadedModules = 0x00000020,
    }

    private const int DumpLimit = 5;
    private static int _dumpSeq;
    private static DateTime _lastFirstChanceLogUtc = DateTime.MinValue;
    private static int _firstChanceLogCount;

    /// <summary>设为 true 时附带完整进程内存（体积大，默认关闭）。诊断疑难崩溃时可临时打开。</summary>
    public static bool CaptureFullMemoryDump;

    /// <summary>当前进程伪句柄（-1）。</summary>
    private static IntPtr CurrentProcessHandle => new(-1);

    /// <summary>在托管异常处理链触发时写一份 minidump，循环保留最近 DumpLimit 份。</summary>
    public static void TryWriteDump(string reason)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var dir = AppPaths.CrashDumpsDir;
            Directory.CreateDirectory(dir);

            var seq = Interlocked.Increment(ref _dumpSeq) - 1;
            var slot = seq % DumpLimit;
            var name = $"dump-{slot:D2}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{reason}.dmp";
            var path = Path.Combine(dir, name);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var flags = MiniDumpType.Normal
                | MiniDumpType.WithDataSegs
                | MiniDumpType.WithHandleData
                | MiniDumpType.WithThreadInfo
                | MiniDumpType.WithUnloadedModules;
            if (CaptureFullMemoryDump)
                flags |= MiniDumpType.WithFullMemory;

            var ok = MiniDumpWriteDump(CurrentProcessHandle, (uint)Environment.ProcessId,
                fs.SafeFileHandle.DangerousGetHandle(), flags, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (!ok)
            {
                try { fs.SetLength(0); } catch { }
            }

            CleanupExcessDumps(dir);
        }
        catch
        {
            // 诊断代码绝不影响主流程
        }
    }

    private static void CleanupExcessDumps(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "dump-*.dmp");
            if (files.Length <= DumpLimit) return;
            foreach (var f in files.OrderBy(File.GetLastWriteTimeUtc).Take(files.Length - DumpLimit))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// First-chance 诊断日志：只记录与本次崩溃签名（0xc0000005 / 0x80131506）相关的异常类型，
    /// 严格节流，不捕获、不改变任何行为。
    /// </summary>
    public static void RegisterFirstChanceLogging()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            try
            {
                var ex = e.Exception;
                if (ex is null) return;
                var t = ex.GetType();
                var relevant = t == typeof(AccessViolationException)
                    || t == typeof(StackOverflowException)
                    || t == typeof(System.Runtime.InteropServices.SEHException)
                    || t == typeof(OutOfMemoryException)
                    || t == typeof(System.Threading.ThreadAbortException);
                if (!relevant) return;

                var now = DateTime.UtcNow;
                if ((now - _lastFirstChanceLogUtc).TotalSeconds < 1) return;
                _lastFirstChanceLogUtc = now;
                _firstChanceLogCount++;

                var msg = ex.Message ?? string.Empty;
                if (msg.Length > 200) msg = msg[..200];
                msg = msg.Replace('\r', ' ').Replace('\n', ' ');
                AppLogger.Error($"First-chance {t.Name} #{_firstChanceLogCount}: {msg}", ex);
            }
            catch
            {
                // 诊断代码不得抛
            }
        };
    }
}
