using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace WinIslands.Services;

/// <summary>
/// 全屏自动隐藏：定时轮询前台窗口，若其矩形覆盖整个显示器工作区（全屏视频/游戏/演示/远程投屏等），
/// 判定为「全屏中」。灵动岛据此自动隐藏，退出全屏后恢复。纯本机轮询，不联网。
/// 
/// 支持两种模式（由 HideOnMaximize 控制）：
/// - true（默认）：最大化窗口也触发隐藏（窗口矩形 == 工作区 == 覆盖）
/// - false：仅真正的全屏窗口才隐藏（窗口矩形覆盖整个显示器 rcMonitor，含任务栏区域）
/// </summary>
public sealed class FullScreenMonitor : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(800) }; // 800ms 轮询：平衡响应性与 CPU 占用

    /// <summary>当前是否检测到全屏窗口。</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>是否正在轮询。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>是否在最大化窗口时也隐藏灵动岛（true=最大化也隐藏，false=仅真全屏才隐藏）。</summary>
    public bool HideOnMaximize { get; set; } = true;

    /// <summary>进入/退出全屏（参数：是否全屏中）。</summary>
    public event Action<bool>? FullScreenChanged;

    public FullScreenMonitor()
    {
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        IsRunning = true;
        Poll();         // 立即采样一次，避免启动时状态未知
        _timer.Start();
    }

    public void Stop()
    {
        IsRunning = false;
        _timer.Stop();
    }

    private void Poll()
    {
        try
        {
            var full = IsCurrentFullScreen();
            if (full != IsFullScreen)
            {
                IsFullScreen = full;
                // 全屏时降频到 1500ms（减少游戏/视频时 CPU 占用），退出全屏恢复 800ms
                _timer.Interval = full ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromMilliseconds(800);
                FullScreenChanged?.Invoke(full);
            }
        }
        catch
        {
            // 检测异常不影响主流程
        }
    }

    /// <summary>
    /// 检测当前前台窗口是否覆盖整个显示器。
    /// HideOnMaximize=true 时与工作区 (rcWork) 比较，最大化窗口也会触发隐藏。
    /// HideOnMaximize=false 时与完整显示器区域 (rcMonitor) 比较，仅真正的全屏窗口触发。
    /// 桌面窗口 (Progman / WorkerW) 始终排除，点击桌面不会隐藏灵动岛。
    /// </summary>
    private bool IsCurrentFullScreen()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (!Native.IsWindowVisible(hwnd)) return false;

        // 排除桌面窗口：点击桌面不应触发隐藏
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        var className = sb.ToString();
        if (className is "Progman" or "WorkerW") return false;

        // 获取前台窗口矩形
        if (!Native.GetWindowRect(hwnd, out var wndRect)) return false;

        // 获取前台窗口所在显示器信息
        var hMon = Native.MonitorFromWindow(hwnd, Native.MonitorDefaultToNearest);
        var mi = new Native.MonitorInfo
        {
            cbSize = (uint)Marshal.SizeOf<Native.MonitorInfo>()
        };
        if (!Native.GetMonitorInfo(hMon, ref mi)) return false;

        // HideOnMaximize=true → 与工作区比较（排除任务栏区域）
        // HideOnMaximize=false → 与完整显示器区域比较（含任务栏）
        var target = HideOnMaximize ? mi.rcWork : mi.rcMonitor;

        // 前台窗口矩形完全覆盖目标区域 → 判定为全屏/最大化
        return wndRect.Left <= target.Left
            && wndRect.Top <= target.Top
            && wndRect.Right >= target.Right
            && wndRect.Bottom >= target.Bottom;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private static class Native
    {
        public const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }
}
