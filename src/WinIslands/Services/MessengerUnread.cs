using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using System.Windows.Threading;

namespace WinIslands.Services;

public enum MessengerSource
{
    WeChat,
    QQ,
}

/// <summary>一条未读消息（来自微信/QQ 的任务栏闪烁，不读取聊天正文）。</summary>
public sealed class MessengerUnread
{
    public MessengerUnread(MessengerSource source, uint pid, IntPtr window, DateTime atUtc,
        string? title = null, string? body = null, string? appUserModelId = null)
    {
        Source = source;
        Pid = pid;
        Window = window;
        AtUtc = atUtc;
        Title = title ?? string.Empty;
        Body = body ?? string.Empty;
        AppUserModelId = appUserModelId ?? string.Empty;
        AccentBrush = new SolidColorBrush(source == MessengerSource.WeChat
            ? Color.FromRgb(0x46, 0xCB, 0x7B)
            : Color.FromRgb(0x12, 0xB7, 0xF5));
        AccentBrush.Freeze();
    }

    public MessengerSource Source { get; }
    public uint Pid { get; }
    public IntPtr Window { get; }
    public DateTime AtUtc { get; }
    public string Title { get; }
    public string Body { get; }
    public string AppUserModelId { get; }

    public string SourceName => Source == MessengerSource.WeChat ? "微信" : "QQ";
    public string Glyph => Source == MessengerSource.WeChat ? "\uE8BD" : "\U0001F427";
    public string GlyphFont => Source == MessengerSource.WeChat ? "Segoe MDL2 Assets" : "Segoe UI Emoji";
    public Brush AccentBrush { get; }
}

/// <summary>
/// 监听 Windows ShellHook 的 HSHELL_FLASH，识别微信/QQ 任务栏闪烁。
/// 参考经典布局的 wechat.rs：只根据任务栏提醒判断有无新消息，不读取联系人/聊天正文。
/// 全程 try/catch，注册失败只记日志，不影响主流程。
/// </summary>
public sealed class MessengerUnreadMonitor : IDisposable
{
    private const int HSHELL_FLASH = 0x8006;
    private const uint GA_ROOTOWNER = 3;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DeregisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly object _gate = new();
    private readonly List<MessengerUnread> _items = new();
    private readonly DispatcherTimer _cleanupTimer;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private int _shellHookMessage;
    private bool _started;
    private bool _disposed;

    public event EventHandler<MessengerUnread>? UnreadAdded;
    public event EventHandler? Changed;

    public MessengerUnreadMonitor()
    {
        _cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _cleanupTimer.Tick += (_, _) => Cleanup();
    }

    public IReadOnlyList<MessengerUnread> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    public bool Start()
    {
        if (_disposed) return false;
        if (_started) return true;

        // 上次启动若在注册阶段失败，先清掉遗留的隐藏窗口，避免每次重试累积钩子。
        ReleaseHook();
        try
        {
            var parameters = new HwndSourceParameters("WinIslandsMessengerHook")
            {
                Width = 0,
                Height = 0,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = 0,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            _hwnd = _source.Handle;
            _shellHookMessage = (int)RegisterWindowMessage("SHELLHOOK");
            if (!RegisterShellHookWindow(_hwnd))
            {
                AppLogger.Warn($"RegisterShellHookWindow failed: {Marshal.GetLastWin32Error()}");
                ReleaseHook();
                return false;
            }
            _started = true;
            _cleanupTimer.Start();
            AppLogger.Info("Messenger unread monitor started (WeChat/QQ shell hook).");
            return true;
        }
        catch (Exception ex)
        {
            ReleaseHook();
            AppLogger.Warn($"Messenger unread monitor start failed: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        var hadResources = _started || _source is not null || _hwnd != IntPtr.Zero;
        _started = false;
        _cleanupTimer.Stop();
        ReleaseHook();
        lock (_gate)
        {
            hadResources |= _items.Count > 0;
            _items.Clear();
        }
        if (hadResources) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ReleaseHook()
    {
        var source = _source;
        var hwnd = _hwnd;
        _source = null;
        _hwnd = IntPtr.Zero;
        _shellHookMessage = 0;

        try { if (hwnd != IntPtr.Zero) DeregisterShellHookWindow(hwnd); } catch { }
        try
        {
            if (source is not null)
            {
                source.RemoveHook(WndProc);
                source.Dispose();
            }
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (_started && !_disposed && _shellHookMessage != 0
                && msg == _shellHookMessage && wParam.ToInt32() == HSHELL_FLASH)
                OnFlash(lParam);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Messenger shell hook ignored: {ex.Message}");
        }
        return IntPtr.Zero;
    }

    private void OnFlash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;

        MessengerSource? source = null;
        try
        {
            var name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
            source = name switch
            {
                "weixin" or "wechat" => MessengerSource.WeChat,
                _ => null,
            };
        }
        catch { return; }

        if (source is null) return;
        var root = GetAncestor(hwnd, GA_ROOTOWNER);
        if (root == IntPtr.Zero) root = hwnd;

        MessengerUnread item;
        lock (_gate)
        {
            if (_items.Any(x => x.Source == source.Value && x.Pid == pid && x.Window == root)) return;
            item = new MessengerUnread(source.Value, pid, root, DateTime.UtcNow);
            _items.Add(item);
        }
        UnreadAdded?.Invoke(this, item);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Activate(MessengerUnread item)
    {
        try
        {
            if (item.Window != IntPtr.Zero && IsWindow(item.Window))
            {
                ShowWindowAsync(item.Window, SW_RESTORE);
                SetForegroundWindow(item.Window);
            }
            else if (!ActivateProcess(item.Source))
            {
                if (!string.IsNullOrWhiteSpace(item.AppUserModelId))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{item.AppUserModelId}") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { AppLogger.Debug($"Activate messenger failed: {ex.Message}"); }
        Remove(item);
    }

    private static bool ActivateProcess(MessengerSource source)
    {
        var names = source == MessengerSource.WeChat ? new[] { "WeChat", "Weixin" } : new[] { "QQ" };
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                var hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                ShowWindowAsync(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                return true;
            }
        }
        return false;
    }

    public void Remove(MessengerUnread item)
    {
        bool removed;
        lock (_gate) removed = _items.Remove(item);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Cleanup()
    {
        var removed = false;
        lock (_gate)
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (IsWindow(item.Window) && ProcessAlive(item.Pid)) continue;
                _items.RemoveAt(i);
                removed = true;
            }
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool ProcessAlive(uint pid)
    {
        try { return !Process.GetProcessById((int)pid).HasExited; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
