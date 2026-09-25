using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinIslands.Services;

/// <summary>
/// 屏幕录制 / 截图提示：
/// - 低层键盘钩子监听 PrintScreen（含 Alt+PrintScreen），按下即触发「已截图」提示；
/// - 定时轮询常见录制进程（OBS、Bandicam、Fraps、Camtasia、Mirillis Action、XSplit、Streamlabs、
///   Xbox Game Bar 等），进入录制状态时提示一次。
/// 纯本机检测，不联网、不上报数据。
/// </summary>
public sealed class ScreenCaptureMonitor : IDisposable
{
    // ── WH_KEYBOARD_LL 低层键盘钩子 ──
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int VkSnapshot = 0x2C; // PrintScreen

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private IntPtr _hook;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly DispatcherTimer? _timer;
    private bool _started;

    /// <summary>是否已启动（用于设置联动时判断是否需要 Start/Stop）。</summary>
    public bool IsRunning => _started;

    /// <summary>按下了 PrintScreen（已截图）。</summary>
    public event Action? ScreenshotTaken;

    /// <summary>进入/退出录制状态（参数：是否录制中、应用显示名）。</summary>
    public event Action<bool, string>? RecordingChanged;

    /// <summary>是否提示截图（PrintScreen）。</summary>
    public bool ScreenshotEnabled { get; set; } = true;

    /// <summary>是否提示录制进程。</summary>
    public bool RecordingEnabled { get; set; } = true;

    /// <summary>全局是否正在录制（静态镜像，供勿扰/规则引擎查询）。</summary>
    public static bool GlobalIsRecording { get; private set; }

    /// <summary>当前是否检测到录制中。</summary>
    public bool IsRecording { get; private set; }

    /// <summary>检测到的录制软件显示名。</summary>
    public string RecordingApp { get; private set; } = string.Empty;

    /// <summary>内置录制软件进程名（不含 .exe，大小写不敏感）。</summary>
    private static readonly HashSet<string> RecordingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "obs64", "obs32", "obs", "bandicam", "bandicam64", "fraps", "camtasia",
        "camtasiastudio", "action", "xsplit.core", "xsplit", "streamlabs", "streamlabsdesktop",
        "dxtory", "duality", "loilo", "gifcam", "screenrecorder",
    };
    /// <summary>
    /// 已知录制软件需要同时满足的窗口标题关键词（进程名 → 标题必须包含的关键词之一）。
    /// 仅进程在运行不足以判定为「录制中」，还需窗口标题佐证。
    /// </summary>
    private static readonly Dictionary<string, string[]> RecordingTitleKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["obs64"] = new[] { "recording", "录制", "●" },
        ["obs32"] = new[] { "recording", "录制", "●" },
        ["obs"]   = new[] { "recording", "录制", "●" },
        ["bandicam"] = new[] { "recording", "录制" },
        ["bandicam64"] = new[] { "recording", "录制" },
        ["fraps"] = new[] { "recording", "录制" },
        ["camtasia"] = new[] { "recording", "录制" },
        ["camtasiastudio"] = new[] { "recording", "录制" },
        ["action"] = new[] { "recording", "录制", "mirillis" },
        ["xsplit.core"] = new[] { "recording", "录制" },
        ["xsplit"] = new[] { "recording", "录制" },
        ["streamlabs"] = new[] { "recording", "录制" },
        ["streamlabsdesktop"] = new[] { "recording", "录制" },
        ["dxtory"] = new[] { "recording", "录制" },
        ["duality"] = new[] { "recording", "录制" },
        ["loilo"] = new[] { "recording", "录制" },
        ["gifcam"] = new[] { "recording", "录制" },
        ["screenrecorder"] = new[] { "recording", "录制" },
    };

    /// <summary>
    /// 窗口标题中表示「正在录制」的精确模式（不依赖进程名）。
    /// 要求关键词足够特异，避免误匹配普通窗口标题。
    /// </summary>
    private static readonly string[] RecordingTitlePatterns =
    {
        "正在录制", "录制中", "● rec", "rec ●", "recording ●",
        "[recording]", "(recording)", "— recording",
        "is recording", "录屏中",
    };

    public ScreenCaptureMonitor()
    {
        _hookProc = HookCallback;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => CheckRecording();
    }

    /// <summary>启动：安装键盘钩子 + 开始轮询录制进程。</summary>
    public void Start()
    {
        _started = true;
        InstallHook();
        _timer?.Start();
        CheckRecording(); // 立即采样一次，避免启动时状态未知
    }

    public void Stop()
    {
        _started = false;
        UninstallHook();
        _timer?.Stop();
    }

    private void InstallHook()
    {
        try
        {
            if (_hook != IntPtr.Zero) return;
            using var cur = Process.GetCurrentProcess();
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(cur.MainModule?.ModuleName), 0);
            if (_hook == IntPtr.Zero) AppLogger.Warn("PrintScreen hook install failed");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"PrintScreen hook install failed: {ex.Message}");
        }
    }

    private void UninstallHook()
    {
        try
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
        catch { /* 忽略 */ }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)WmKeyDown && ScreenshotEnabled)
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (info.VkCode == VkSnapshot)
                    ScreenshotTaken?.Invoke();
            }
        }
        catch { /* 钩子回调异常不影响系统 */ }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void CheckRecording()
    {
        if (!RecordingEnabled)
        {
            if (IsRecording) SetRecording(false, string.Empty);
            return;
        }
        try
        {
            // 前台窗口标题含「录制/recording」也算（如 OBS 的「正在录制」窗口标题）
            var fg = ForegroundWindowInfo();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (RecordingProcesses.Contains(p.ProcessName))
                    {
                        SetRecording(true, DisplayName(p.ProcessName));
                        return;
                    }
                }
                catch { /* 进程已退出 */ }
            }
            if (fg.Title.Length > 0)
            {
                var t = fg.Title;
                if (t.IndexOf("recording", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("正在录制", StringComparison.Ordinal) >= 0
                    || t.IndexOf("录制中", StringComparison.Ordinal) >= 0)
                {
                    SetRecording(true, fg.Proc);
                    return;
                }
            }
            SetRecording(false, string.Empty);
        }
        catch { SetRecording(false, string.Empty); }
    }

    private void SetRecording(bool recording, string app)
    {
        var changed = recording != IsRecording || app != RecordingApp;
        IsRecording = recording;
        GlobalIsRecording = recording;
        RecordingApp = app;
        if (changed) RecordingChanged?.Invoke(recording, app);
    }

    private static string DisplayName(string proc) => proc.ToLowerInvariant() switch
    {
        "obs64" or "obs32" or "obs" => "OBS Studio",
        "bandicam" or "bandicam64" => "Bandicam",
        "fraps" => "Fraps",
        "camtasia" or "camtasiastudio" => "Camtasia",
        "action" => "Mirillis Action!",
        "xsplit.core" or "xsplit" => "XSplit",
        "streamlabs" or "streamlabsdesktop" => "Streamlabs",
        "dxtory" => "Dxtory",
        _ => proc,
    };

    private static (string Title, string Proc) ForegroundWindowInfo()
    {
        try
        {
            var hwnd = User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (string.Empty, string.Empty);
            var len = User32.GetWindowTextLength(hwnd);
            var sb = new System.Text.StringBuilder(Math.Max(1, len + 1));
            User32.GetWindowText(hwnd, sb, sb.Capacity);
            uint pid = 0;
            User32.GetWindowThreadProcessId(hwnd, out pid);
            string? name = null;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch { }
            return (sb.ToString(), name ?? string.Empty);
        }
        catch { return (string.Empty, string.Empty); }
    }

    /// <summary>获取进程主窗口标题（安全，不抛异常）。</summary>
    private static string GetProcessWindowTitle(Process p)
    {
        try
        {
            // 优先用 MainWindowTitle
            var title = p.MainWindowTitle ?? string.Empty;
            if (!string.IsNullOrEmpty(title)) return title;

            // 如果主窗口标题为空，尝试枚举该进程的所有窗口
            var pid = (uint)p.Id;
            var foundTitle = string.Empty;
            User32.EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    uint windowPid = 0;
                    User32.GetWindowThreadProcessId(hWnd, out windowPid);
                    if (windowPid != pid) return true;

                    var len = User32.GetWindowTextLength(hWnd);
                    if (len <= 0) return true;

                    var sb = new System.Text.StringBuilder(len + 1);
                    User32.GetWindowText(hWnd, sb, sb.Capacity);
                    if (sb.Length > 0)
                    {
                        foundTitle = sb.ToString();
                        return false; // 找到第一个有标题的窗口，停止枚举
                    }
                }
                catch { }
                return true; // 继续枚举
            }, IntPtr.Zero);

            return foundTitle;
        }
        catch { return string.Empty; }
    }
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private static class User32
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
