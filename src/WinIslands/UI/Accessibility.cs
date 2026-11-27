using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WinIslands.UI;

/// <summary>
/// 轻量可访问性支持（3.1.8）：
/// 1) 为缺少 AutomationProperties.Name 的交互控件，从 ToolTip / 显示文本推导可读名称，屏幕阅读器可正确朗读；
/// 2) 通知与轻提示通过 UI Automation Notification 事件播报，读屏用户能听到新消息；
/// 3) 暴露高对比度状态与变化事件，供窗口关闭透明/模糊特效。
/// 所有逻辑都做了空值保护，不影响无 UI 的单元测试环境。
/// </summary>
public static class Accessibility
{
    private static bool _registered;
    private static WeakReference<Window>? _lastWindow;
    private static int _windowCount;

    /// <summary>是否处于 Windows 高对比度模式。</summary>
    public static bool IsHighContrast
    {
        get
        {
            try { return SystemParameters.HighContrast; }
            catch { return false; }
        }
    }

    /// <summary>高对比度开关变化（只在高对比度相关属性变化时触发）。</summary>
    public static event EventHandler? HighContrastChanged;

    /// <summary>注册全局窗口钩子：每个窗口加载后自动补齐可访问性名称。重复调用无副作用。</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded),
                handledEventsToo: true);
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        }
        catch { /* 设计器或测试环境缺少 WPF 基础设施时忽略 */ }
    }

    private static void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            try { HighContrastChanged?.Invoke(null, EventArgs.Empty); } catch { }
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        _lastWindow = new WeakReference<Window>(window);
        _windowCount++;
        ApplyAutomationNames(window);

        // 控件模板与数据模板可能稍后才生成，再补一次即可，避免递归监听造成开销。
        window.Dispatcher.BeginInvoke(new Action(() => ApplyAutomationNames(window)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>递归为交互控件补齐 AutomationProperties.Name。</summary>
    public static void ApplyAutomationNames(DependencyObject root)
    {
        if (root is null) return;
        var count = 0;
        Walk(root, ref count);
    }

    private static void Walk(DependencyObject node, ref int visited)
    {
        if (visited++ > 4000) return; // 安全上限，避免异常可视化树导致卡顿
        try
        {
            if (node is UIElement element && IsInteractive(node))
            {
                var name = AutomationProperties.GetName(node);
                if (string.IsNullOrWhiteSpace(name))
                {
                    var derived = DeriveName(node);
                    if (!string.IsNullOrWhiteSpace(derived))
                    {
                        AutomationProperties.SetName(node, derived.Trim());
                    }
                }
                // 默认把悬停提示升级为帮助文本，读屏时能听到补充说明。
                if (element is FrameworkElement fe && string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(node)))
                {
                    var tip = fe.ToolTip as string;
                    if (!string.IsNullOrWhiteSpace(tip))
                    {
                        AutomationProperties.SetHelpText(node, tip.Trim());
                    }
                }
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i), ref visited);
            }
        }
        catch { /* 虚拟化容器可能在遍历途中被回收 */ }
    }

    private static bool IsInteractive(DependencyObject node)
        => node is ButtonBase || node is TextBoxBase || node is Slider || node is ComboBox
           || node is ListBoxItem || node is TreeViewItem || node is TabItem;

    private static string? DeriveName(DependencyObject node)
    {
        if (node is FrameworkElement fe)
        {
            if (fe.Tag is string tag && tag.StartsWith("a11y:", StringComparison.Ordinal))
            {
                return tag.Substring(5);
            }

            if (fe.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip))
            {
                return tip;
            }

            switch (fe)
            {
                case Button button:
                    if (button.Content is string text && !string.IsNullOrWhiteSpace(text)) return text;
                    break;
                case TextBoxBase box:
                    if (!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(box))) return AutomationProperties.GetHelpText(box);
                    break;
                case Slider slider:
                    if (!string.IsNullOrWhiteSpace(slider.Name)) return slider.Name;
                    break;
            }
        }
        return null;
    }

    /// <summary>通过 UI Automation 播报一段文字（通知、复制成功等）。无窗口或失败时静默忽略。</summary>
    public static void Announce(string? text, bool assertive = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Window? window = null;
            if (_lastWindow is not null) _lastWindow.TryGetTarget(out window);
            if (window is null) return;

            var peer = UIElementAutomationPeer.FromElement(window) ?? new WindowAutomationPeer(window);
            peer.RaiseNotificationEvent(
                assertive ? AutomationNotificationKind.ActionCompleted : AutomationNotificationKind.ItemAdded,
                assertive ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                text.Trim(),
                "WinIslandsNotification");
        }
        catch { /* 读屏服务未运行时忽略 */ }
    }

    /// <summary>已加载过的窗口数量，仅用于诊断日志。</summary>
    public static int LoadedWindowCount => _windowCount;
}