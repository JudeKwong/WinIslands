using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinIslands.Services;

/// <summary>
/// 插件进程的 Windows Job Object 沙箱：
/// - 主程序退出/崩溃时自动结束插件进程（KILL_ON_JOB_CLOSE）；
/// - 限制单个插件的内存上限；
/// - 未声明 processes 权限时，禁止插件创建子进程（ActiveProcessLimit = 1）。
/// 说明：网络与文件系统的操作系统级硬隔离需要 AppContainer/防火墙，当前版本不做静默提权。
/// </summary>
internal sealed class PluginSandbox : IDisposable
{
    private IntPtr _job;

    public bool IsAttached => _job != IntPtr.Zero;

    public static PluginSandbox? Attach(Process process, bool allowChildProcesses)
    {
        var sandbox = new PluginSandbox();
        try
        {
            sandbox._job = CreateJobObject(IntPtr.Zero, null);
            if (sandbox._job == IntPtr.Zero) return null;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                                 | JobObjectLimitProcessMemory
                                 | JobObjectLimitJobMemory
                                 | (allowChildProcesses ? 0u : JobObjectLimitActiveProcess),
                    ActiveProcessLimit = allowChildProcesses ? 0u : 1u,
                },
                ProcessMemoryLimit = (UIntPtr)MemoryLimitBytes,
                JobMemoryLimit = (UIntPtr)MemoryLimitBytes,
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!SetInformationJobObject(sandbox._job, JobObjectExtendedLimitInformation, pointer, (uint)length))
                {
                    sandbox.Dispose();
                    return null;
                }
            }
            finally { Marshal.FreeHGlobal(pointer); }

            if (!AssignProcessToJobObject(sandbox._job, process.Handle))
            {
                // 进程可能已处于不允许嵌套的 Job 中；失败时退回“无沙箱但可运行”，并记录警告。
                AppLogger.Debug("Plugin sandbox attach failed (process may already belong to a job).");
                sandbox.Dispose();
                return null;
            }
            return sandbox;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Plugin sandbox error: {ex.Message}");
            sandbox.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_job == IntPtr.Zero) return;
        try { CloseHandle(_job); } catch { }
        _job = IntPtr.Zero;
    }

    private const long MemoryLimitBytes = 1024L * 1024 * 1024; // 1 GB
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
