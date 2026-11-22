using System.Diagnostics;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WinIslands.Services;

public sealed class SystemNotificationEventArgs : EventArgs
{
    public SystemNotificationEventArgs(MessengerSource? source, string title, string body,
        string appUserModelId, string appDisplayName)
    {
        Source = source;
        Title = title;
        Body = body;
        AppUserModelId = appUserModelId;
        AppDisplayName = appDisplayName;
    }

    /// <summary>QQ / 微信来源；其它应用为 null。</summary>
    public MessengerSource? Source { get; }
    public string Title { get; }
    public string Body { get; }
    public string AppUserModelId { get; }
    public string AppDisplayName { get; }
}

/// <summary>
/// 读取 Windows 通知中心的 Toast。所有应用的通知都会上报，由上层按通知来源过滤决定是否展示；
/// 默认不读取聊天数据库。首次轮询只建立基线，不把历史通知当作新消息；之后每 2 秒检查新增通知。
/// </summary>
public sealed class SystemNotificationMonitor : IDisposable, IBackgroundPausable
{
    private readonly DispatcherTimer _timer;
    private readonly HashSet<uint> _seen = new();
    private readonly object _gate = new();
    private UserNotificationListener? _listener;
    private bool _primed;
    private volatile bool _started;
    private bool _disposed;
    private int _starting;
    private int _polling;
    private int _generation;

    public event EventHandler<SystemNotificationEventArgs>? NotificationReceived;

    public SystemNotificationMonitor()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await PollAsync().ConfigureAwait(true);
    }

    public async Task<bool> StartAsync()
    {
        if (_disposed) return false;
        if (_started) return true;
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0) return false;
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync().AsTask().ConfigureAwait(true);
            AppLogger.Info($"System notification listener access: {access}");
            if (access != UserNotificationListenerAccessStatus.Allowed) return false;
            if (_disposed) return false;
            Interlocked.Increment(ref _generation);
            _started = true;
            _timer.Start();
            _ = PollAsync(); // 立即建立基线
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"System notification listener start failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _starting, 0);
        }
    }

    /// <summary>锁屏 / 全屏 / 省电时暂停读取通知中心；恢复后立即补一次。</summary>
    public void SetBackgroundSuspended(bool suspended)
    {
        if (!_started || _disposed) return;
        if (suspended)
        {
            _timer.Stop();
        }
        else if (!_timer.IsEnabled)
        {
            _timer.Start();
            _ = PollAsync();
        }
    }

    private async Task PollAsync()
    {
        if (!_started || _listener is null) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;

        var generation = Volatile.Read(ref _generation);
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().ConfigureAwait(true);
            if (!IsCurrent(generation)) return;

            var current = new HashSet<uint>();
            foreach (var notification in notifications)
            {
                try
                {
                    var id = notification.Id;
                    current.Add(id);
                    bool isNew;
                    lock (_gate) isNew = _seen.Add(id);
                    if (!_primed || !isNew) continue;

                    var app = notification.AppInfo;
                    var appName = app?.DisplayInfo?.DisplayName ?? string.Empty;
                    var aumid = app?.AppUserModelId ?? string.Empty;
                    var source = DetectSource(appName, aumid);

                    var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    var texts = binding?.GetTextElements()?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                        ?? new List<string>();
                    var title = texts.Count > 0
                        ? texts[0]
                        : string.IsNullOrWhiteSpace(appName) ? "通知" : appName;
                    var body = texts.Count > 1 ? texts[1] : string.Empty;
                    if (!IsCurrent(generation)) return;
                    AppLogger.Info($"System notification captured: app={appName} aumid={aumid} title={title}");
                    NotificationReceived?.Invoke(this, new SystemNotificationEventArgs(source, title, body, aumid, appName));
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"System notification item ignored: {ex.Message}");
                }
            }

            if (!IsCurrent(generation)) return;
            lock (_gate)
            {
                // 通知被清除后从 seen 中移除，避免 Id 复用后永久跳过。
                _seen.RemoveWhere(id => !current.Contains(id));
                _primed = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"System notification poll failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private bool IsCurrent(int generation)
        => _started && generation == Volatile.Read(ref _generation);

    private static MessengerSource? DetectSource(string appName, string aumid)
    {
        var text = appName + " " + aumid;
        if (text.Contains("QQ", StringComparison.OrdinalIgnoreCase)) return MessengerSource.QQ;
        if (text.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Weixin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("微信", StringComparison.Ordinal))
            return MessengerSource.WeChat;
        return null;
    }

    public void Stop()
    {
        _started = false;
        Interlocked.Increment(ref _generation);
        _timer.Stop();
        lock (_gate)
        {
            _seen.Clear();
            _primed = false;
        }
        _listener = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}