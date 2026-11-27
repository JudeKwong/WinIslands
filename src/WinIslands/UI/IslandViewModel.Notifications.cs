using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using WinIslands.Services;

namespace WinIslands.UI;

public sealed partial class IslandViewModel : ObservableObject, IDisposable
{
    /// <summary>复制成功等轻量提示：2.4 秒后自动消失，不占用通知卡片。</summary>
    public void ShowCopyToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        CopyToastText = message.Trim();
        HasCopyToast = true;
        _copyToastTimer.Stop();
        _copyToastTimer.Start();
    }

    /// <summary>
    /// 系统事件以「灵动岛卡片」形式展示（iOS 风格）：自动过期、弹簧动画入场、不阻塞主流程。
    /// 复用上岛推送队列：同 id 覆盖；到点后由 CheckPushExpiry 自动回收。
    /// </summary>
    public void ShowEventCard(string id, string title, string? subtitle, string icon,
        string type = "info", int durationSeconds = 5, string? body = null, MessengerUnread? unread = null)
    {
        // 勿扰模式：灵动岛事件卡片不展示（白名单仍展示由上层决定，这里只做总开关）
        if (DoNotDisturb.IsActive(_settings.Current)) return;
        // 通知去抖：同类型通知 2 秒内只保留最新一条（避免快速连续触发导致队列堆积）
        if (_settings.Current.NotificationDebounce)
        {
            _lastEventTimes.TryGetValue(type ?? "info", out var lastTime);
            if (DateTime.Now - lastTime < TimeSpan.FromSeconds(2))
            {
                // 同一应用的重复通知不丢弃：落回队列合并为一条并累加 ×N。
                var repeated = _pushes.Any(p => string.Equals(p.Id, "sys:" + id, StringComparison.Ordinal));
                if (!repeated)
                {
                    AppLogger.Info($"Notification debounced: {title} (type={type})");
                    return;
                }
            }
            _lastEventTimes[type ?? "info"] = DateTime.Now;
        }
        var dur = Math.Max(2, durationSeconds);

        PushIsland(new IslandPush
        {
            Id = "sys:" + id,
            Title = title,
            Subtitle = subtitle ?? string.Empty,
            Body = body ?? string.Empty,
            Icon = icon,
            Type = type ?? "info",
            Priority = "high", // 系统事件优先于普通第三方推送展示
            DurationSeconds = dur,
            ExpiresAt = DateTime.UtcNow.AddSeconds(dur),
            MessengerUnread = unread,
        });

        // 3.1.8 可访问性：通知出现时播报标题与正文，读屏用户无需看屏即可获知内容
        try
        {
            var spoken = string.Join("，", new[] { title, subtitle, body }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
            Accessibility.Announce(spoken);
        }
        catch { }
    }

    /// <summary>
    /// 蓝牙设备连接/断开：iOS 风格事件卡片（1.2.4）。
    /// 标题 = 已连接 / 已断开；副标题 = 设备名（读到电量时附加「· 电量 xx%」，读不到只显示设备名）。
    /// 复用上岛推送队列：弹簧入场 + 尺寸自适应动画自动生效。任何异常都不会阻塞主流程。
    /// </summary>
    public void ShowDeviceEvent(string id, bool connected, string deviceName, int? batteryPercent)
    {
        try
        {
            if (DoNotDisturb.IsActive(_settings.Current)) return;
            var subtitle = string.IsNullOrWhiteSpace(deviceName) ? string.Empty : deviceName.Trim();
            if (batteryPercent.HasValue && batteryPercent.Value >= 0 && batteryPercent.Value <= 100)
                subtitle = $"{subtitle}  ·  {Localization.Get("Battery_Level")} {batteryPercent.Value}%";
            ShowEventCard(id,
                Localization.Get(connected ? "Events_BluetoothConnected" : "Events_BluetoothDisconnected"),
                subtitle, "\uE702", "bluetooth", 5);
        }
        catch (Exception ex) { AppLogger.Warn($"ShowDeviceEvent failed: {ex.Message}"); }
    }

    public void ActivatePage(IslandPage page, int? direction = null)
    {
        if (page is null) return;
        var oldPage = ActivePage;
        foreach (var p in _pages) p.IsActive = ReferenceEquals(p, page);
        // 切页顺序：先切换页面状态并启动过渡，再发出属性通知，
        // 避免 WPF 绑定在过渡首帧抢占 Visibility 造成闪烁。
        if (direction is int dir && oldPage is { } old && !ReferenceEquals(old, page))
            PageTransitionStarted?.Invoke(old, page, dir);
        OnPropertyChanged(nameof(IsMusicPage));
        OnPropertyChanged(nameof(ActivePage));
        OnPropertyChanged(nameof(ActivePageTitle));
        OnPropertyChanged(nameof(ActivePageAccentBrush));
    }

    public void NavigatePage(int delta)
    {
        if (_pages.Count < 2) return;
        var current = ActivePage;
        var index = current is null ? 0 : Math.Max(0, _pages.IndexOf(current));
        var next = (index + delta) % _pages.Count;
        if (next < 0) next += _pages.Count;
        ActivatePage(_pages[next], delta > 0 ? 1 : -1);
    }

    public void RebuildPages()
    {
        var order = _settings.Current.PageOrder ?? new List<string>();
        var known = order.Where(k => PageDefs.Any(d => string.Equals(d.Key, k, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var d in PageDefs)
            if (!known.Contains(d.Key, StringComparer.OrdinalIgnoreCase)) known.Add(d.Key);
        var hidden = _settings.Current.HiddenPages ?? new List<string>();
        var visible = known.Where(k => !hidden.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (visible.Count == 0) visible.Add("Music");

        var currentKey = ActivePage?.Key;
        _pages.Clear();
        foreach (var key in visible)
        {
            var def = PageDefs.First(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            _pages.Add(new IslandPage(def.Key, def.Title, def.Description, def.Accent));
        }
        var target = _pages.FirstOrDefault(p => string.Equals(p.Key, currentKey, StringComparison.OrdinalIgnoreCase)) ?? _pages[0];
        ActivatePage(target);
        _pageSignature = string.Join(",", _settings.Current.PageOrder ?? new List<string>())
            + "|" + string.Join(",", _settings.Current.HiddenPages ?? new List<string>());
        OnPropertyChanged(nameof(Pages));
        OnPropertyChanged(nameof(HasMultiplePages));
    }

    private void OnPageSettingsChanged(object? sender, AppSettings s)
    {
        var signature = string.Join(",", s.PageOrder ?? new List<string>())
            + "|" + string.Join(",", s.HiddenPages ?? new List<string>());
        if (signature != _pageSignature) RebuildPages();
    }

    /// <summary>微信/QQ 未读：加入图标栈并弹出一张经典布局风格通知卡（不读取聊天正文）。</summary>
    public void ShowMessengerUnread(MessengerUnread unread)
    {
        if (DoNotDisturb.IsActive(_settings.Current)) return;
        if (!_messengerUnreads.Any(x => x.Source == unread.Source && x.Pid == unread.Pid && x.Window == unread.Window))
        {
            _messengerUnreads.Add(unread);
            OnPropertyChanged(nameof(HasMessengerUnreads));
            OnPropertyChanged(nameof(MessengerUnreadCount));
        }
        var type = unread.Source == MessengerSource.WeChat ? "wechat" : "qq";
        ShowEventCard($"msg:{type}:{unread.Pid}:{unread.Window}", unread.SourceName,
            "有新消息 · 点击查看", unread.Glyph, type, 6, unread: unread);
    }

    /// <summary>QQ 系统 Toast：带标题/正文的未读记录，使用同一个通知卡片与图标栈。</summary>
    public void ShowMessengerNotification(MessengerSource source, string title, string body, string appUserModelId)
    {
        if (DoNotDisturb.IsActive(_settings.Current)) return;
        var unread = new MessengerUnread(source, 0, IntPtr.Zero, DateTime.UtcNow,
            title ?? string.Empty, body ?? string.Empty, appUserModelId ?? string.Empty);
        var existing = _messengerUnreads.FirstOrDefault(x => x.Source == source && x.Pid == 0
            && x.Title == unread.Title && x.Body == unread.Body && x.AppUserModelId == unread.AppUserModelId);
        if (existing is null)
        {
            _messengerUnreads.Add(unread);
            existing = unread;
            OnPropertyChanged(nameof(HasMessengerUnreads));
            OnPropertyChanged(nameof(MessengerUnreadCount));
        }
        var type = source == MessengerSource.WeChat ? "wechat" : "qq";
        var displayTitle = string.IsNullOrWhiteSpace(title) ? unread.SourceName : title;
        var summary = string.IsNullOrWhiteSpace(body) ? "有新消息 · 点击查看" : body;
        // 同一应用的通知合并为一条卡片并用 ×N 计数（不同标题不再拆成多条）。
        ShowEventCard($"msg:{type}:{appUserModelId}", displayTitle, summary, unread.Glyph, type, 6, unread: existing);
    }

    public void ActivateMessengerUnread(MessengerUnread unread)
    {
        _messengerMonitor?.Activate(unread);
        RemoveMessengerUnread(unread);
    }

    /// <summary>通知卡片上的「查看」：切回微信/QQ 对应窗口，并收起当前通知。</summary>
    public void ActivateActivePushMessenger()
    {
        if (ActivePush?.MessengerUnread is not MessengerUnread unread) return;
        ActivateMessengerUnread(unread);
        DismissActivePush();
    }

    public void DismissMessengerUnread(MessengerUnread unread)
    {
        _messengerMonitor?.Remove(unread);
        RemoveMessengerUnread(unread);
    }

    public void ClearMessengerUnreads()
    {
        _messengerMonitor?.Clear();
        if (_messengerUnreads.Count == 0) return;
        _messengerUnreads.Clear();
        OnPropertyChanged(nameof(HasMessengerUnreads));
        OnPropertyChanged(nameof(MessengerUnreadCount));
    }

    private void RemoveMessengerUnread(MessengerUnread unread)
    {
        if (!_messengerUnreads.Remove(unread)) return;
        OnPropertyChanged(nameof(HasMessengerUnreads));
        OnPropertyChanged(nameof(MessengerUnreadCount));
    }

    private void OnMessengerUnreadsChanged(object? sender, EventArgs e)
    {
        if (_messengerMonitor is null) return;
        var snapshot = _messengerMonitor.Items;
        for (var i = _messengerUnreads.Count - 1; i >= 0; i--)
        {
            var item = _messengerUnreads[i];
            // 只同步 SHELLHOOK 的窗口项；系统 Toast 项（Pid=0）由通知中心自己的生命周期管理。
            if (item.Pid != 0 && !snapshot.Contains(item)) _messengerUnreads.RemoveAt(i);
        }
        foreach (var item in snapshot)
            if (!_messengerUnreads.Contains(item)) _messengerUnreads.Add(item);
        OnPropertyChanged(nameof(HasMessengerUnreads));
        OnPropertyChanged(nameof(MessengerUnreadCount));
    }

    /// <summary>上岛 API 收到推送：加入/更新推送队列（同 id 覆盖、保留原过期时间），并按优先级刷新显示。</summary>
    public void PushIsland(IslandPush push)
    {
        var idx = _pushes.FindIndex(p => string.Equals(p.Id, push.Id, StringComparison.Ordinal));
        if (idx >= 0)
        {
            // 更新内容，保持原过期时间
            if (_pushes[idx].ExpiresAt is DateTime e) push.ExpiresAt = e;
            push.RepeatCount = Math.Clamp(_pushes[idx].RepeatCount + 1, 1, 99);
            _pushes[idx] = push;
        }
        else
        {
            _pushes.Add(push);
        }
        RecomputeActivePush();
        AppLogger.Info($"Island push: '{push.Title}' (id={push.Id}, priority={push.Priority ?? "normal"})");
    }

    /// <summary>上岛 API 移除/过期指定推送。</summary>
    public void RemoveIslandPush(string id)
    {
        var removed = _pushes.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) > 0;
        if (removed) RecomputeActivePush();
    }

    /// <summary>用户点击/关闭当前上岛卡片：只关闭当前条，队列中还有推送则继续显示。</summary>
    public void DismissActivePush()
    {
        if (ActivePush is null) return;
        _pushes.RemoveAll(p => ReferenceEquals(p, ActivePush));
        RecomputeActivePush();
    }

    /// <summary>按 优先级高→低、入队 早→晚 重排队列并选出当前显示项（过期项同时清除）。</summary>
    private void RecomputeActivePush()
    {
        var dropped = _pushes
            .Where(p => p.ExpiresAt is DateTime e && e <= DateTime.UtcNow)
            .ToList();
        var maxQueue = Math.Clamp(_settings.Current.NotificationQueueMax, 3, 20);
        while (_pushes.Count - dropped.Count > maxQueue)
        {
            var minIdx = 0;
            var minRank = int.MaxValue;
            for (var j = 0; j < _pushes.Count; j++)
            {
                if (dropped.Contains(_pushes[j])) continue;
                var rank = _pushes[j].PriorityRank + TypePriorityRank(_pushes[j].Type);
                if (rank < minRank) { minRank = rank; minIdx = j; }
            }
            dropped.Add(_pushes[minIdx]);
        }
        foreach (var push in dropped)
        {
            _pushes.Remove(push);
            if (push.MessengerUnread is not MessengerUnread unread) continue;
            _messengerMonitor?.Remove(unread);
            RemoveMessengerUnread(unread);
        }
        var sorted = _pushes
            .Select((p, i) => (p, i))
            .OrderByDescending(t => t.p.PriorityRank + TypePriorityRank(t.p.Type))
            .ThenBy(t => t.i)
            .Select(t => t.p)
            .ToList();
        _pushes.Clear();
        _pushes.AddRange(sorted);
        ActivePush = _pushes.FirstOrDefault();
    }
    /// <summary>通知类型优先级数值：error=10 > warning=8 > success=5 > info=3。</summary>
    private static int TypePriorityRank(string? type)
    {
        return (type ?? "").Trim().ToLowerInvariant() switch
        {
            "error" => 10,
            "warning" => 8,
            "success" => 5,
            _ => 3,
        };
    }

    private void OnKeyboardStateChanged(string key)
    {
        var label = key switch
        {
            "CapsLock" => "Caps",
            "NumLock" => "Num",
            _ => "ScrLk",
        };
        var on = key switch
        {
            "CapsLock" => _keyboard.Current.Caps,
            "NumLock" => _keyboard.Current.Num,
            _ => _keyboard.Current.Scroll,
        };
        CapsLockText = $"{label} {(on ? Localization.Get("On") : Localization.Get("Off"))}";
        _capsLockSecondsLeft = Math.Max(1, _settings.Current.KeyIndicatorSeconds);
        RebuildCompactItems();
    }

    private void UpdateCapsLockCountdown()
    {
        if (_capsLockSecondsLeft <= 0) return;
        if (--_capsLockSecondsLeft <= 0)
        {
            CapsLockText = string.Empty;
            RebuildCompactItems();
        }
    }
    /// <summary>音量/静音变化：显示临时上岛指示（几秒后自动消失）。</summary>
    public void ShowVolumeTemp(int percent, bool muted)
    {
        if (!_settings.Current.VolumeTempIndicatorEnabled) return;
        _volumeTempGen++; // 取消上一次未完成的淡出清理
        VolumeTempText = muted ? Localization.Get("VolumeTemp_Muted") : $"{percent}%";
        VolumeTempPercent = Math.Clamp(percent / 100.0, 0, 1);
        _volumeTempSecondsLeft = Math.Max(1, _settings.Current.VolumeTempIndicatorSeconds);
        _volumeTempFading = false;
        OnPropertyChanged(nameof(VolumeTempFading));
        RebuildCompactItems();
        UpdateVisibility(); // 无媒体且隐藏时也要能临时显示
    }

    /// <summary>每秒轮询系统音量（仅开启音量指示时）；变化时上岛。无变化时近乎零开销。</summary>
    private void PollVolumeTemp()
    {
        if (!_settings.Current.VolumeTempIndicatorEnabled) return;
        try
        {
            var v = SystemVolume.GetVolume();
            var muted = SystemVolume.IsMuted();
            if (v.HasValue && (Math.Abs(v.Value - _lastVolumeTempValue) > 0.001 || muted != _lastVolumeTempMuted))
            {
                _lastVolumeTempValue = v.Value;
                _lastVolumeTempMuted = muted;
                ShowVolumeTemp((int)Math.Round(v.Value * 100), muted);
            }
        }
        catch { /* 音频服务不可用时忽略 */ }
    }

    /// <summary>每秒递减音量指示剩余秒数，到 0 后先播放淡出，再移除组件（退场丝滑）。</summary>
    private void UpdateVolumeTempCountdown()
    {
        if (_volumeTempSecondsLeft <= 0) return;
        if (--_volumeTempSecondsLeft <= 0)
        {
            var gen = _volumeTempGen;
            _volumeTempFading = true;
            OnPropertyChanged(nameof(VolumeTempFading));
            // 淡出动画约 260ms，结束后再真正移除组件，避免瞬时消失
            _ = Task.Delay(320).ContinueWith(_ =>
            {
                if (gen != _volumeTempGen) return; // 期间音量又被调节过，取消本次清理
                VolumeTempText = string.Empty;
                VolumeTempPercent = 0;
                _volumeTempFading = false;
                OnPropertyChanged(nameof(VolumeTempFading));
                RebuildCompactItems();
                UpdateVisibility();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>每秒检测前台窗口是否正在复制/移动文件；变化时更新上岛文本。</summary>
    private void PollFileCopy()
    {
        if (!_settings.Current.FileCopyNotifyEnabled) return;
        var newText = FileTransferMonitor.IsCopyingOrMoving() ? Localization.Get("FileCopy_IslandText") : string.Empty;
        if (FileCopyText != newText)
        {
            FileCopyText = newText;
            RebuildCompactItems();
            UpdateVisibility(); // 上岛卡片显示/消失影响灵动岛可见性
        }
    }

    /// <summary>每秒扫描下载目录中的浏览器临时文件；变化时更新上岛文本。</summary>
    private void PollDownloadProgress()
    {
        if (!_settings.Current.DownloadProgressEnabled) return;
        var count = DownloadDetector.ActiveDownloadCount();
        var newText = count > 0 ? string.Format(Localization.Get("Download_IslandText"), count) : string.Empty;
        if (DownloadText != newText)
        {
            DownloadText = newText;
            RebuildCompactItems();
            UpdateVisibility(); // 上岛卡片显示/消失影响灵动岛可见性
        }
    }

    /// <summary>执行上岛卡片按钮动作（url/启动程序；notify 回调给推送方，#10）。</summary>
    public void ExecutePushAction(IslandPushButton button)
    {
        if (button is null) return;
        try
        {
            // #10 notify 动作：不打开链接，触发事件由 App 转发给 WebSocket 订阅端（推送方自行处理回调）
            if (string.Equals(button.Action, "notify", StringComparison.OrdinalIgnoreCase))
            {
                var pushId = FindPushIdByButton(button);
                PushActionRequested?.Invoke(button, pushId);
                AppLogger.Info($"Island push notify action: {button.Label ?? button.Value} (push={pushId})");
                return;
            }
            if (string.IsNullOrWhiteSpace(button.Value)) return;

            // #16 command 动作：执行本地命令（本地上岛 API 仅回环监听，可配 Token；请仅信任可信推送方）
            if (string.Equals(button.Action, "command", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + button.Value)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                AppLogger.Info($"Island push command: {button.Value}");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(button.Value)
            {
                UseShellExecute = true,
            });
            AppLogger.Info($"Island push action: {button.Action} -> {button.Value}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Island push action failed: {ex.Message}");
        }
    }

    /// <summary>查找包含该按钮的推送 ID（用于 notify 回调广播）。</summary>
    private string FindPushIdByButton(IslandPushButton button)
    {
        foreach (var p in _pushes)
        {
            if (p.Buttons is not null && p.Buttons.Contains(button)) return p.Id;
            if (p.Click == button) return p.Id;
        }
        return ActivePush?.Id ?? string.Empty;
    }

    /// <summary>执行整卡点击回跳动作（推送配置了 click 时），执行后关闭该条推送。</summary>
    public void ExecutePushClick()
    {
        if (ActivePush?.Click is not IslandPushButton click || string.IsNullOrWhiteSpace(click.Value)) return;
        ExecutePushAction(click);
        DismissActivePush();
    }

    /// <summary>每秒检查上岛推送是否过期（由 _widgetTimer 调用）。</summary>
    private void CheckPushExpiry()
    {
        if (!_pushes.Any(p => p.ExpiresAt is DateTime e && e <= DateTime.UtcNow)) return;
        RecomputeActivePush();
    }
}
