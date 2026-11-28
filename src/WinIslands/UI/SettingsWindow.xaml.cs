using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Point = System.Windows.Point;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
using WinIslands.Diagnostics;
using WinIslands.Services;
using ThemeMode = WinIslands.Services.ThemeMode;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace WinIslands.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly SettingsService _service;
    private readonly CiderMediaProvider? _cider;
    private readonly DispatcherTimer _autoApply;
    private readonly DispatcherTimer _saveDebounce;
    private readonly UpdaterService? _updater;
    private readonly IslandViewModel? _island;
    private readonly PluginService? _plugins;
    private readonly Action? _openPluginManager;
    private readonly Action? _openPluginFolder;
    private readonly Action? _openPluginGuide;
    private string _lastAppliedJson;
    private bool _sizeSlidersInitialized; // 初始化期间不触发"手动调整关闭自动"
    private bool _audioOutputLoading;     // 音频输出下拉初始化期间不触发切换

    /// <summary>具名语言切换处理器：Closed 时退订，避免静态事件反复持有已关闭的设置窗口。</summary>
    private readonly EventHandler _onLanguageChanged;
    private readonly EventHandler<AppSettings> _onSettingsChanged;
    private EventHandler? _pluginsReloaded;
    private SelectionChangedEventHandler? _onTabChanged;

    public SettingsWindow(SettingsViewModel vm, SettingsService service, CiderMediaProvider? cider,
        UpdaterService? updater,
        IslandViewModel? island,
        PluginService? plugins,
        Action? openPluginManager,
        Action? openPluginFolder,
        Action? openPluginGuide)
    {
        _vm = vm;
        _service = service;
        _cider = cider;
        _updater = updater;
        _island = island;
        _plugins = plugins;
        _openPluginManager = openPluginManager;
        _openPluginFolder = openPluginFolder;
        _openPluginGuide = openPluginGuide;
        DataContext = vm;
        InitializeComponent();
        _onSettingsChanged = (_, settings) =>
        {
            try
            {
                var dark = settings.Theme switch
                {
                    WinIslands.Services.ThemeMode.Light => false,
                    WinIslands.Services.ThemeMode.Dark => true,
                    _ => ThemeHelper.IsSystemDark(),
                };
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) WindowEffects.ApplyDarkMode(hwnd, dark);
                ApplyGlassPalette(dark);
            }
            catch { /* 主题刷新失败不影响设置操作 */ }
        };
        _service.Changed += _onSettingsChanged;

        // 初始化完成后显式设置初始选中页（不在 XAML 里用 IsSelected，避免解析期提前触发事件）
        NavList.SelectedIndex = 0;
        MainTabs.SelectedIndex = 0;

        ApplyLocalization();
        TxtMailPass.Password = _vm.Working.MailPassword; // PasswordBox 不支持绑定，回填初值
        InitAudioOutput();
        InitCardStyle();
        InitUpdateChannel();

        // 插件：设置页内展示已加载插件，并连接插件管理器/插件目录/开发指南。
        BtnOpenPluginManager.IsEnabled = _plugins is not null && _openPluginManager is not null;
        BtnOpenPluginFolder.IsEnabled = _openPluginFolder is not null;
        BtnOpenPluginGuide.IsEnabled = _openPluginGuide is not null;
        BtnReloadPlugins.IsEnabled = _plugins is not null;
        if (_plugins is not null)
        {
            _pluginsReloaded = (_, _) => RefreshPluginList();
            _plugins.PluginsReloaded += _pluginsReloaded;
        }

        UpdatePluginDependencyHint();

        // 即时生效：轮询检测 Working 变化并立即应用（无保存按钮）
        _lastAppliedJson = SettingsService.Serialize(_vm.Working);
        _autoApply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _service.Save(); };
        _autoApply.Tick += (_, _) => AutoApply();
        _autoApply.Start();

        // 语言切换后立即刷新界面文案（具名处理器，关闭时退订，避免静态事件持有窗口导致泄漏）
        _onLanguageChanged = (_, _) => ApplyLocalization();
        Localization.LanguageChanged += _onLanguageChanged;

        // 关闭时兜底保存最后一次改动，并释放定时器/事件订阅（设置窗每次打开都会新建）
        Closed += (_, _) =>
        {
            try { _vm.Save(); } catch { }
            try { if (_plugins is not null && _pluginsReloaded is not null) _plugins.PluginsReloaded -= _pluginsReloaded; } catch { }
            try { _vm.Dispose(); } catch { }
            try { _autoApply.Stop(); } catch { }
            try { _saveDebounce.Stop(); } catch { }
            try { Localization.LanguageChanged -= _onLanguageChanged; } catch { }
            try { _service.Changed -= _onSettingsChanged; } catch { }
            try { if (MainTabs != null && _onTabChanged is not null) MainTabs.SelectionChanged -= _onTabChanged; } catch { }
        };

        // 初始化完成后才允许"手动调整关闭自动"
        Loaded += (_, _) =>
        {
            _sizeSlidersInitialized = true;
            FixInheritedBlackText();
            // Tab 切换后可能延迟创建可视元素，重新执行黑字兜底修复
            if (MainTabs != null)
            {
                _onTabChanged ??= (_, _) =>
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FixInheritedBlackText));
                MainTabs.SelectionChanged -= _onTabChanged;
                MainTabs.SelectionChanged += _onTabChanged;
            }
        };
    }
        /// <summary>兜底修复：深色模式下模板/继承链断裂处 TextBlock 会回退为默认黑色。
        /// 遍历可视树，将“未显式设置前景色且当前继承结果为纯黑”的文本统一改为主题主文字色（动态资源，跟随深浅主题）。</summary>
        private void FixInheritedBlackText()
        {
            try
            {
                WalkTextBlocks(this);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"FixInheritedBlackText failed: {ex.Message}");
            }
        }

        private void WalkTextBlocks(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
            pending.Push(root);

            while (pending.Count > 0)
            {
                var parent = pending.Pop();
                if (!visited.Add(parent)) continue;

                if (parent is System.Windows.Controls.TextBlock tb
                    && tb.ReadLocalValue(System.Windows.Controls.TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue)
                {
                    var eff = tb.Foreground as SolidColorBrush;
                    if (eff != null && eff.Color == Colors.Black)
                        tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
                }

                int count;
                try { count = VisualTreeHelper.GetChildrenCount(parent); }
                catch (InvalidOperationException) { continue; }

                for (int i = 0; i < count; i++)
                {
                    try { pending.Push(VisualTreeHelper.GetChild(parent, i)); }
                    catch (InvalidOperationException) { }
                }
            }
        }

    /// <summary>填充音频输出设备下拉框（默认选中系统当前默认设备）。</summary>
    private void InitAudioOutput()
    {
        try
        {
            if (CboAudioOutput is null) return;
            var devices = SystemVolume.GetDevices();
            CboAudioOutput.ItemsSource = devices;
            _audioOutputLoading = true;
            CboAudioOutput.SelectedItem = devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
            _audioOutputLoading = false;
        }
        catch (Exception ex)
        {
            _audioOutputLoading = false;
            AppLogger.Warn($"初始化音频输出列表失败: {ex.Message}");
        }
    }

    /// <summary>切换系统默认输出设备（IPolicyConfig 为未公开接口，已打开的播放器需重启后生效）。</summary>
    private void AudioOutput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audioOutputLoading || CboAudioOutput.SelectedItem is not SystemVolume.AudioDeviceInfo dev) return;
        try
        {
            if (SystemVolume.SetDefaultDevice(dev.Id) && TxtAudioOutputNote is not null)
                TxtAudioOutputNote.Text = Localization.Get("Media_AudioOutputApplied");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"切换音频输出失败: {ex.Message}");
        }
    }

    /// <summary>滚轮滚动当前页签的 ScrollViewer（避免被 ComboBox/Slider 拦截）。</summary>
    private void Root_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        var sv = FindAncestor<System.Windows.Controls.ScrollViewer>(d);
        if (sv is null) return;

        // 纵向优先；横向 ScrollViewer（如组件顺序条）转横向滚动
        if (sv.ComputedVerticalScrollBarVisibility == System.Windows.Visibility.Visible)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
        else if (sv.ComputedHorizontalScrollBarVisibility == System.Windows.Visibility.Visible)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>组件顺序条：滚轮转横向滚动（内容未超宽时无效果，不影响外层纵向滚动）。</summary>
    private void OrderScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ScrollViewer sv
            && sv.ComputedHorizontalScrollBarVisibility == System.Windows.Visibility.Visible)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : class
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d)
                ?? System.Windows.LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
    private void AutoApply()
    {
        try
        {
            var j = SettingsService.Serialize(_vm.Working);
            if (j != _lastAppliedJson)
            {
                _lastAppliedJson = j;
                _vm.Save(persist: false);
                _saveDebounce.Stop();
                _saveDebounce.Start();
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>应用毛玻璃：统一模糊装饰 + 圆角 + 明暗主题调色板。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = _service.Current.Theme switch
        {
            WinIslands.Services.ThemeMode.Light => false,
            WinIslands.Services.ThemeMode.Dark => true,
            _ => ThemeHelper.IsSystemDark(),
        };
        // 注意：不用 ApplyAcrylic —— 在 AllowsTransparency 窗口上会渲染出黑色大块
        WindowEffects.ApplyDarkMode(hwnd, dark);
        ApplyGlassPalette(dark);

    }

    private void ApplyGlassPalette(bool dark)
    {
        var accent = ThemeHelper.ParseColor(_service.Current.AccentColor, Color.FromRgb(0x6C, 0x5C, 0xE7));
        void Add(string key, Brush b) { b.Freeze(); Resources[key] = b; }

        if (dark)
        {
            Add("GlassBgBrush", new SolidColorBrush(Color.FromArgb(0xDC, 0x17, 0x17, 0x20)));
            Add("CardBgBrush", new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
            Add("GlassBorderBrush", new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
            Add("GlassOuterRimBrush", new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
            Add("TextPrimaryBrush", new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7)));
            Add("TextSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xB8, 0xE0, 0xE0, 0xEA)));
            Add("HoverBrush", new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)));
            Add("ControlBgBrush", new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
            Add("ControlBorderBrush", new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)));
            Add("TrackBrush", new SolidColorBrush(Color.FromArgb(0x5E, 0xFF, 0xFF, 0xFF)));
            Add("ScrollTrackBrush", new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)));
            Add("ScrollThumbBrush", new SolidColorBrush(Color.FromArgb(0x9A, 0xFF, 0xFF, 0xFF)));
            Add("SidebarBrush", new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
            Add("NavSelectedBrush", new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)));
        }
        else
        {
            Add("GlassBgBrush", new SolidColorBrush(Color.FromArgb(0xD6, 0xF7, 0xF8, 0xFC)));
            Add("CardBgBrush", new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)));
            Add("GlassBorderBrush", new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00)));
            Add("GlassOuterRimBrush", new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00)));
            Add("TextPrimaryBrush", new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x24)));
            Add("TextSecondaryBrush", new SolidColorBrush(Color.FromArgb(0xB0, 0x48, 0x48, 0x52)));
            Add("HoverBrush", new SolidColorBrush(Color.FromArgb(0x2E, 0x00, 0x00, 0x00)));
            Add("ControlBgBrush", new SolidColorBrush(Color.FromArgb(0x92, 0xFF, 0xFF, 0xFF)));
            Add("ControlBorderBrush", new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)));
            Add("TrackBrush", new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00)));
            Add("ScrollTrackBrush", new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00)));
            Add("ScrollThumbBrush", new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)));
            Add("SidebarBrush", new SolidColorBrush(Color.FromArgb(0x26, 0xF2, 0xF2, 0xF7)));
            Add("NavSelectedBrush", new SolidColorBrush(Color.FromArgb(0x4A, 0x1D, 0x1D, 0x24)));
        }

        Add("AccentBrush", new SolidColorBrush(accent));
        Add("AccentSoftBrush", new SolidColorBrush(Color.FromArgb(0x3D, accent.R, accent.G, accent.B)));

        if (FrostedEffect is not null)
        {
            var glassTint = new SolidColorBrush(dark
                ? Color.FromRgb(0x14, 0x14, 0x1B)
                : Color.FromRgb(0xF2, 0xF4, 0xFA));
            glassTint.Freeze();
            FrostedEffect.TintBrush = glassTint;
            FrostedEffect.IsDark = dark;
            FrostedEffect.Intensity = 1.0;
        }

        // 根治文字颜色：Window.Foreground 的 DynamicResource 在属性继承链上可能不刷新，
        // 导致深色模式下继承了默认黑色前景的文字看不清。资源构建完成后直接写入确定的前景色
        // （深色主题=白 #F2F2F7，浅色主题=深 #1D1D24），所有依赖继承的文本立即生效。
        Foreground = (Brush)Resources["TextPrimaryBrush"];
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* ignore */ }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>左侧导航对应的本地化键（与 TabItem 顺序一致，用于页标题）。</summary>
    private static readonly string[] NavKeys =
    {
        "Settings_General", "Settings_QuickActions", "Settings_Appearance", "Settings_Components", "Settings_Plugins", "Settings_Pages", "Settings_Media",
        "Settings_MediaInfo", "Settings_Lyrics", "Settings_Cider", "Settings_Island",
        "Settings_Update", "Settings_About", "Settings_Events", "Settings_Notifications",
    };

    /// <summary>左侧导航选中 → 同步切换右侧页面。</summary>
    private void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 防御：XAML 解析期间 MainTabs 可能尚未初始化完成
        if (MainTabs is null || MainTabs.Items.Count == 0) return;
        if (NavList.SelectedIndex >= 0 && NavList.SelectedIndex < MainTabs.Items.Count)
            MainTabs.SelectedIndex = NavList.SelectedIndex;
    }

    /// <summary>右侧页面切换 → 同步左侧导航高亮，并更新页标题。</summary>
    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 防御：XAML 解析期间 NavList 可能尚未初始化完成
        if (NavList is null) return;
        var idx = MainTabs.SelectedIndex;
        if (NavList.SelectedIndex != idx && idx >= 0)
            NavList.SelectedIndex = idx;
        UpdatePageTitle();
    }

    /// <summary>把页标题更新为当前分类名。</summary>
    private void UpdatePageTitle()
    {
        var idx = MainTabs.SelectedIndex;
        if (idx < 0 || idx >= NavKeys.Length) return;
        PageTitle.Text = Localization.Get(NavKeys[idx]);
        PageSubtitle.Text = string.Empty;
    }

    /// <summary>设置搜索：按页签名或关键词定位到对应设置页。</summary>
    private static readonly string[][] SearchKeywords =
    {
        new[] { "通用", "语言", "开机", "自启", "隐藏", "双击", "中键", "快捷键", "低电量", "充电" },
        new[] { "快捷操作", "启动器", "launcher", "搜索" },
        new[] { "外观", "主题", "颜色", "透明度", "尺寸", "位置", "预设", "自动隐藏" },
        new[] { "组件", "时间", "日期", "天气", "cpu", "内存", "gpu", "网络", "电量", "键盘" },
        new[] { "插件", "plugin", "扩展" },
        new[] { "页面", "排序", "隐藏页面" },
        new[] { "媒体", "播放器", "来源", "smtc", "cider", "专辑" },
        new[] { "媒体信息", "封面", "进度条", "控制按钮" },
        new[] { "歌词", "lyrics", "滚动", "字体", "karaoke", "ttml" },
        new[] { "cider", "令牌", "token" },
        new[] { "上岛", "api", "推送", "http" },
        new[] { "更新", "update", "版本" },
        new[] { "关于", "about", "致谢" },
        new[] { "事件", "日历", "节假日" },
        new[] { "通知", "免打扰", "规则", "dnd", "勿扰" },
    };

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtSettingsSearchHint is null) return;
        var query = (TxtSettingsSearch?.Text ?? string.Empty).Trim();
        TxtSettingsSearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (query.Length == 0 || MainTabs is null) return;

        var index = FindSettingsTab(query);
        if (index < 0)
        {
            PageSubtitle.Text = Localization.CurrentLanguage == "en-US"
                ? "No setting matches the search."
                : "没有找到匹配的设置。";
            return;
        }

        MainTabs.SelectedIndex = index;
        NavList.SelectedIndex = index;
        PageSubtitle.Text = Localization.CurrentLanguage == "en-US"
            ? $"Located: {PageTitle.Text}"
            : $"已定位到：{PageTitle.Text}";
    }

    private void SettingsSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (TxtSettingsSearch is not null) TxtSettingsSearch.Text = string.Empty;
        e.Handled = true;
    }

    private static int FindSettingsTab(string query)
    {
        for (var i = 0; i < NavKeys.Length; i++)
            if (Localization.Get(NavKeys[i]).Contains(query, StringComparison.OrdinalIgnoreCase))
                return i;
        for (var i = 0; i < SearchKeywords.Length && i < NavKeys.Length; i++)
            if (SearchKeywords[i].Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)))
                return i;
        return -1;
    }

    /// <summary>恢复出厂默认值（保留界面语言与已完成的引导标记）。</summary>
    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var en = Localization.CurrentLanguage == "en-US";
        var message = en
            ? "Restore all settings to their factory defaults?\n\nThe interface language is kept."
            : "确定要把所有设置恢复为默认值吗？\n\n界面语言会保留。";
        if (MessageBox.Show(this, message, "WinIslands", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        try
        {
            var language = _vm.Working.Language;
            _vm.Working.ResetToDefaults(language);
            DataContext = null;
            DataContext = _vm;
            if (TxtMailPass is not null) TxtMailPass.Password = _vm.Working.MailPassword;
            InitAudioOutput();
            InitCardStyle();
            ApplyLocalization();
            _vm.Save();
            RefreshPluginList();
            PageSubtitle.Text = en ? "Settings restored to defaults." : "已恢复默认设置。";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Reset settings failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "WinIslands", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>刷新勿扰状态提示与快捷按钮文案。</summary>
    private void UpdateDndStatus()
    {
        if (TxtDndStatus is null || _vm?.Working is null) return;
        var en = Localization.CurrentLanguage == "en-US";
        var active = DoNotDisturb.IsActive(_vm.Working);
        var mode = _vm.Working.DoNotDisturbManual
            ? (en ? "Manual" : "手动")
            : _vm.Working.DoNotDisturbEnabled
                ? (en ? "Scheduled" : "定时")
                : (en ? "Off" : "关闭");
        TxtDndStatus.Text = (en ? "Current: " : "当前：") + mode + (active ? (en ? " (active)" : "（生效中）") : string.Empty);
        if (BtnDndNow is not null)
            BtnDndNow.Content = active
                ? (en ? "Turn off manual DND" : "关闭手动勿扰")
                : (en ? "Turn on DND now" : "立即开启勿扰");
    }

    private void DndOption_Changed(object sender, RoutedEventArgs e) => UpdateDndStatus();

    private void DndNow_Click(object sender, RoutedEventArgs e)
    {
        _vm.Working.DoNotDisturbManual = !_vm.Working.DoNotDisturbManual;
        UpdateDndStatus();
    }

    /// <summary>一键创建内置示例插件（plugin.json + run.ps1）。</summary>
    private void CreateSamplePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (_plugins is null)
            return;
        try
        {
            var id = "sample-widget";
            var suffix = 1;
            while (_plugins.Plugins.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                id = "sample-widget-" + (++suffix);

            var error = _plugins.CreateTemplatePlugin(id, Localization.CurrentLanguage == "en-US"
                ? "Sample widget"
                : "示例组件");
            if (error is not null)
            {
                MessageBox.Show(this, error, "WinIslands", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    this,
                    Localization.CurrentLanguage == "en-US"
                        ? "The sample plugin was created and enabled. See the island components list."
                        : "示例插件已创建并启用，可在「组件」页看到它。",
                    "WinIslands", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Create sample plugin failed: {ex.Message}");
        }
        RefreshPluginList();
        UpdatePluginDependencyHint();
    }

    /// <summary>插件依赖提示：检测 PowerShell 7 是否可用。</summary>
    private void UpdatePluginDependencyHint()
    {
        if (TxtPluginDependencyHint is null) return;
        var hasPwsh = false;
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            hasPwsh = path.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(dir =>
            {
                try { return File.Exists(Path.Combine(dir.Trim(), "pwsh.exe")); }
                catch { return false; }
            });
        }
        catch { /* ignore */ }

        TxtPluginDependencyHint.Text = Localization.CurrentLanguage == "en-US"
            ? (hasPwsh
                ? "Dependency check: PowerShell 7 detected."
                : "Dependency check: PowerShell 7 not found; .ps1 plugins fall back to Windows PowerShell 5.1.")
            : (hasPwsh
                ? "依赖检查：已检测到 PowerShell 7。"
                : "依赖检查：未检测到 PowerShell 7，.ps1 插件将回退到 Windows PowerShell 5.1。");
    }

    /// <summary>手动拖动尺寸滑杆时，若该尺寸处于"自动调整"状态则自动关闭自动调整（自动/手动二选一）。</summary>
    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_sizeSlidersInitialized || sender is not Slider s || s.Tag is not string key) return;
        switch (key)
        {
            case "CompactWidth": if (_vm.Working.CompactWidthAuto) _vm.Working.CompactWidthAuto = false; break;
            case "CompactHeight": if (_vm.Working.CompactHeightAuto) _vm.Working.CompactHeightAuto = false; break;
            case "ExpandedWidth": if (_vm.Working.ExpandedWidthAuto) _vm.Working.ExpandedWidthAuto = false; break;
            case "ExpandedHeight": if (_vm.Working.MaxExpandedHeightAuto) _vm.Working.MaxExpandedHeightAuto = false; break;
        }
    }
    /// <summary>Refresh all hardcoded labels from the localization tables.</summary>
    private void ApplyLocalization()
    {
        Title = Localization.Get("Settings_Title");
        TabGeneral.Header = Localization.Get("Settings_General");
        TabQuick.Header = Localization.Get("Settings_QuickActions");
        ChkQuickActions.Content = Localization.Get("QuickActions_Enable");
        TxtQuickHint.Text = Localization.Get("QuickActions_Hint");
        TabAppearance.Header = Localization.Get("Settings_Appearance");
        TabMedia.Header = Localization.Get("Settings_Media");
        TabMediaInfo.Header = Localization.Get("Settings_MediaInfo");
        ChkShowMediaInfo.Content = Localization.Get("MediaInfo_Show");
        LblExpandedSections.Text = Localization.Get("MediaInfo_ExpandedSections");
        ChkExpandedArtTitle.Content = Localization.Get("MediaInfo_ArtTitle");
        ChkExpandedProgress.Content = Localization.Get("MediaInfo_Progress");
        ChkExpandedControls.Content = Localization.Get("MediaInfo_Controls");
        ChkExpandedLyrics.Content = Localization.Get("MediaInfo_Lyrics");
        TxtMediaInfoNote.Text = Localization.Get("MediaInfo_Note");
        LblCardStyle.Text = Localization.Get("MediaInfo_CardStyle");
        if (CboCardStyle is not null)
        {
            var prev = CboCardStyle.SelectedValue as string;
            InitCardStyle();
            CboCardStyle.SelectedValue = prev ?? _vm.Working.ExpandedCardStyle;
        }

        TabComponents.Header = Localization.Get("Settings_Components");
        TxtCompNote.Text = Localization.Get("Comp_Note");
        TabPlugins.Header = Localization.Get("Settings_Plugins");
        LblPluginsIntro.Text = Localization.Get("Plugins_Intro");
        BtnOpenPluginManager.Content = Localization.Get("Plugins_OpenManager");
        BtnOpenPluginFolder.Content = Localization.Get("Plugins_OpenFolder");
        BtnOpenPluginGuide.Content = Localization.Get("Plugins_OpenGuide");
        BtnReloadPlugins.Content = Localization.Get("Plugins_Reload");
        TxtPluginsHint.Text = Localization.Get("Plugins_Hint");
        RefreshPluginList();
        LblCompOrder.Text = Localization.Get("Comp_OrderHint");
        TabLyrics.Header = Localization.Get("Settings_Lyrics");
        TabCider.Header = Localization.Get("Settings_Cider");
        TabAbout.Header = Localization.Get("Settings_About");
        TabEvents.Header = Localization.Get("Settings_Events");
        TabIsland.Header = Localization.Get("Settings_Island");
        TabRules.Header = Localization.Get("Settings_Notifications");
        LblNotificationsIntro.Text = Localization.Get("Notifications_Intro");
        LblNotificationMode.Text = Localization.Get("Notifications_Mode");
        LblNotificationApps.Text = Localization.Get("Notifications_Apps");
        TxtNotificationAppsHint.Text = Localization.Get("Notifications_AppsHint");
        ChkNotificationTakeover.Content = Localization.Get("Notifications_Takeover");
        LblNotificationTimeout.Text = Localization.Get("Notifications_Timeout");
        LblNotificationQueue.Text = Localization.Get("Notifications_Queue");
        ChkNotificationFold.Content = Localization.Get("Notifications_Fold");
        ChkNotificationDebounce.Content = Localization.Get("Notifications_Debounce");
        ChkCopyToast.Content = Localization.Get("Clipboard_Toast");
        NavGeneral.Text = Localization.Get("Settings_General");
        NavQuick.Text = Localization.Get("Settings_QuickActions");
        NavAppearance.Text = Localization.Get("Settings_Appearance");
        NavComponents.Text = Localization.Get("Settings_Components");
        NavPlugins.Text = Localization.Get("Settings_Plugins");
        NavPages.Text = Localization.Get("Settings_Pages");
        TabPages.Header = Localization.Get("Settings_Pages");
        NavMedia.Text = Localization.Get("Settings_Media");
        NavMediaInfo.Text = Localization.Get("Settings_MediaInfo");
        NavLyrics.Text = Localization.Get("Settings_Lyrics");
        NavCider.Text = Localization.Get("Settings_Cider");
        NavIsland.Text = Localization.Get("Settings_Island");

        NavUpdate.Text = Localization.Get("Settings_Update");
        NavAbout.Text = Localization.Get("Settings_About");
        NavEvents.Text = Localization.Get("Settings_Events");
        NavRules.Text = Localization.Get("Settings_Notifications");
        UpdatePageTitle();
        ChkIslandApi.Content = Localization.Get("Island_Enabled");
        LblIslandPort.Text = Localization.Get("Island_Port");
        LblIslandToken.Text = Localization.Get("Island_Token");
        LblIslandDuration.Text = Localization.Get("Island_DefaultDuration");
        TxtIslandNote.Text = Localization.Get("Island_Note");

        LblLanguage.Text = Localization.Get("General_Language");
        LblTheme.Text = Localization.Get("Appearance_Theme");
        ChkThemeScheduled.Content = Localization.Get("Appearance_ThemeSchedule");
        LblThemeDarkStart.Text = Localization.Get("Appearance_ThemeScheduleDarkStart");
        LblThemeDarkEnd.Text = Localization.Get("Appearance_ThemeScheduleDarkEnd");
        TxtThemeScheduleNote.Text = Localization.Get("Appearance_ThemeScheduleNote");
        LblAccent.Text = Localization.Get("Appearance_Accent");
        LblPosition.Text = Localization.Get("Appearance_Position");
        LblMonitor.Text = Localization.Get("Appearance_Monitor");
        LblMonitorIndex.Text = Localization.Get("Appearance_MonitorIndexLabel");
        LblOffsetX.Text = Localization.Get("Appearance_OffsetX");
        LblOffsetY.Text = Localization.Get("Appearance_OffsetY");
        LblOpacity.Text = Localization.Get("Appearance_Opacity");
        LblIslandSize.Text = Localization.Get("Appearance_IslandSize");
        LblCompactWidth.Text = Localization.Get("Appearance_CompactWidth");
        LblCompactHeight.Text = Localization.Get("Appearance_CompactHeight");
        LblExpandedWidth.Text = Localization.Get("Appearance_ExpandedWidth");
        LblExpandedHeight.Text = Localization.Get("Appearance_ExpandedHeight");
        ChkCompactWidthAuto.Content = Localization.Get("Appearance_Auto");
        ChkCompactHeightAuto.Content = Localization.Get("Appearance_Auto");
        ChkExpandedWidthAuto.Content = Localization.Get("Appearance_Auto");
        ChkExpandedHeightAuto.Content = Localization.Get("Appearance_Auto");
        LblWidgets.Text = Localization.Get("Appearance_Widgets");
        ChkShowWidgets.Content = Localization.Get("Appearance_ShowWidgets");
        ChkWidgetTime.Content = Localization.Get("Appearance_WidgetTime");
        ChkWidgetWeather.Content = Localization.Get("Appearance_WidgetWeather");
        LblWeatherCity.Text = Localization.Get("Appearance_WeatherCity");
        TxtWidgetNote.Text = Localization.Get("Appearance_WidgetNote");
        LblCompact.Text = Localization.Get("Appearance_Compact");
        LblLyricsFolder.Text = Localization.Get("Lyrics_Folder");
        LblCiderPort.Text = Localization.Get("Cider_Port");
        LblCiderToken.Text = Localization.Get("Cider_Token");

        ChkStartWithWindows.Content = Localization.Get("General_StartWithWindows");
        ChkStartHidden.Content = Localization.Get("General_StartHidden");
        ChkHideWhenNoMedia.Content = Localization.Get("General_HideWhenNoMedia");
        ChkFullScreenHide.Content = Localization.Get("General_FullScreenHide");
        ChkLockScreenHide.Content = Localization.Get("General_LockScreenHide");
        ChkShowWhenPaused.Content = Localization.Get("General_ShowWhenPaused");
        ChkAlwaysVisible.Content = Localization.Get("General_AlwaysVisible");
        LblDoubleClick.Text = Localization.Get("General_DoubleClick");
        LblMiddleClick.Text = Localization.Get("General_MiddleClick");
        ChkHoverExpand.Content = Localization.Get("General_HoverExpand");
        LblHoverExpandDelay.Text = Localization.Get("General_HoverExpandDelay");
        LblHoverCollapseDelay.Text = Localization.Get("General_HoverCollapseDelay");
        ChkWheelPage.Content = Localization.Get("General_WheelPage");
        ChkWheelInvert.Content = Localization.Get("General_WheelInvert");
        TxtInteractionHint.Text = Localization.Get("General_InteractionHint");
        CbiDcPlayPause.Content = Localization.Get("DoubleClick_PlayPause");
        CbiDcOpenSettings.Content = Localization.Get("DoubleClick_OpenSettings");
        CbiDcNone.Content = Localization.Get("DoubleClick_None");
        CbiMcPlayPause.Content = Localization.Get("DoubleClick_PlayPause");
        CbiMcOpenSettings.Content = Localization.Get("DoubleClick_OpenSettings");
        CbiMcNone.Content = Localization.Get("DoubleClick_None");
        ChkReduceMotion.Content = Localization.Get("General_ReduceMotion");
        ChkLowPower.Content = Localization.Get("General_LowPower");
        ChkShowLunar.Content = Localization.Get("General_ShowLunar");
        ChkGlobalHotkeys.Content = Localization.Get("General_GlobalHotkeys");
        TxtHotkeysHint.Text = Localization.Get("General_HotkeysHint");
        LblHotkeyToggle.Text = Localization.Get("General_HotkeyToggle");
        LblHotkeyPlayPause.Text = Localization.Get("General_HotkeyPlayPause");
        LblHotkeyNext.Text = Localization.Get("General_HotkeyNext");
        LblHotkeyPrev.Text = Localization.Get("General_HotkeyPrev");
        LblHotkeyExpand.Text = Localization.Get("General_HotkeyExpand");
        ChkQuickLauncher.Content = Localization.Get("General_QuickLauncher");
        LblHotkeyLauncher.Text = Localization.Get("General_HotkeyLauncher");
        LblLowBattery.Text = Localization.Get("General_LowBattery");
        TxtLowBatteryHint.Text = Localization.Get("General_LowBatteryHint");
        ChkLowBatteryPersist.Content = Localization.Get("General_LowBatteryPersist");
        LblCharged.Text = Localization.Get("General_Charged");
        ChkChargedNotify.Content = Localization.Get("General_ChargedCheck");
        LblChargedThreshold.Text = Localization.Get("General_ChargedThreshold");
        TxtChargedHint.Text = Localization.Get("General_ChargedHint");
        ChkUseSystemVolume.Content = Localization.Get("Media_UseSystemVolume");
        LblAudioOutput.Text = Localization.Get("Media_AudioOutput");
        TxtAudioOutputNote.Text = Localization.Get("Media_AudioOutputNote");
        ChkLyricsSourcePick.Content = Localization.Get("Lyrics_SourcePick");
        LblLyricsPreferredSource.Text = Localization.Get("Lyrics_PreferredSource");
        ChkAmllTtml.Content = Localization.Get("Lyrics_AmllToggle");
        ChkOnlineLyrics.Content = Localization.Get("Lyrics_Online");
        ChkStandaloneLyrics.Content = Localization.Get("Lyrics_StandaloneWindow");
        LblLyricsOpacity.Text = Localization.Get("Lyrics_Opacity");
        ChkLyricsLocked.Content = Localization.Get("Lyrics_Locked");
        LblLyricsStyleTitle.Text = Localization.Get("Lyrics_StyleTitle");
        LblLyricsBaseSize.Text = Localization.Get("Lyrics_BaseSize");
        LblLyricsCurrentSize.Text = Localization.Get("Lyrics_CurrentSize");
        LblLyricsLineSpacing.Text = Localization.Get("Lyrics_LineSpacing");
        LblLyricsKaraokeSpeed.Text = Localization.Get("Lyrics_KaraokeSpeed");
        LblLyricsHighlightColor.Text = Localization.Get("Lyrics_HighlightColor");
        LblLyricsBaseColor.Text = Localization.Get("Lyrics_BaseColor");
        TxtLyricsColorNote.Text = Localization.Get("Lyrics_ColorNote");
        ChkBilingual.Content = Localization.Get("MediaInfo_Bilingual");
        ChkCiderEnabled.Content = Localization.Get("Cider_Enabled");
        ChkBluetoothNotify.Content = Localization.Get("Notifications_Bluetooth");
        ChkCallNotify.Content = Localization.Get("Call_Enabled");
        LblCallApps.Text = Localization.Get("Call_AppsLabel");
        ChkNetworkNotify.Content = Localization.Get("Notifications_Network");
        ChkDiskAlert.Content = Localization.Get("Notifications_Disk");
        LblDiskThreshold.Text = Localization.Get("Disk_Threshold");
        TxtDiskThresholdHint.Text = Localization.Get("Disk_ThresholdHint");
        ChkCompactArt.Content = Localization.Get("Appearance_CompactArt");
        ChkCompactTitle.Content = Localization.Get("Appearance_CompactTitle");
        ChkCompactProgress.Content = Localization.Get("Appearance_CompactProgress");
        ChkSingleLine.Content = Localization.Get("Appearance_SingleLine");
        ChkMiniPlayer.Content = Localization.Get("Appearance_MiniPlayer");

        TxtLyricsNote.Text = Localization.Get("Lyrics_CopyrightNote");
        TxtCiderHint.Text = Localization.Get("Cider_HowTo");
        TxtMediaInfo.Text = Localization.Get("Media_SourcePriority");
        LblMediaApps.Text = Localization.Get("Media_Apps");
        TxtMediaNote.Text = Localization.Get("Media_Note");
        TxtAbout.Text = BuildAboutText();


        BtnExport.Content = Localization.Get("Export");
        BtnImport.Content = Localization.Get("Import");
        BtnBrowse.Content = Localization.Get("Browse");
        BtnOpenConfig.Content = Localization.Get("OpenConfigFolder");
        BtnDiagnostics.Content = Localization.Get("Diagnostics");


        TabUpdate.Header = Localization.Get("Settings_Update");
        LblKeyCaps.Text = Localization.Get("General_KeyCaps");
        LblThemePreset.Text = Localization.Get("Appearance_ThemePreset");
        LblThemeTint.Text = Localization.Get("Appearance_ThemeTint");
        TxtThemeTintNote.Text = Localization.Get("Appearance_ThemeTintNote");
        LblAnimationStyle.Text = Localization.Get("Appearance_AnimStyle");
        LblAnimDuration.Text = Localization.Get("Appearance_AnimDuration");
        TxtAnimDurationHint.Text = Localization.Get("Appearance_AnimDurationHint");
        LblFontFamily.Text = Localization.Get("Appearance_FontFamily");
        LblFontScale.Text = Localization.Get("Appearance_FontScale");
        LblCornerRadius.Text = Localization.Get("Appearance_CornerRadius");
        ChkCoverTint.Content = Localization.Get("Appearance_CoverTint");
        ChkMarquee.Content = Localization.Get("Appearance_Marquee");
        ChkWallpaperTheme.Content = Localization.Get("Appearance_WallpaperTheme");
        ChkWave.Content = Localization.Get("Wave_Enabled");
        ChkWaveSync.Content = Localization.Get("Wave_Sync");
        ChkNetCurve.Content = Localization.Get("Appearance_NetCurve");
        LblWaveStyle.Text = Localization.Get("Wave_Style");
        LblWaveSensitivity.Text = Localization.Get("Wave_Sensitivity");
        LblWaveHeight.Text = Localization.Get("Wave_Height");
        TxtWaveNote.Text = Localization.Get("Wave_Note");
        LblDndTitle.Text = Localization.Get("Dnd_Title");
        ChkDndManual.Content = Localization.Get("Dnd_Manual");
        ChkDndSchedule.Content = Localization.Get("Dnd_Schedule");
        LblDndStart.Text = Localization.Get("Dnd_Start");
        LblDndEnd.Text = Localization.Get("Dnd_End");
        TxtDndNote.Text = Localization.Get("Dnd_Note");
        LblDndAllowlist.Text = Localization.Get("Dnd_Allowlist");
        UpdateDndStatus();
        LblVolTempTitle.Text = Localization.Get("VolumeTemp_Title");
        ChkVolTemp.Content = Localization.Get("VolumeTemp_Enabled");
        LblVolTempSeconds.Text = Localization.Get("VolumeTemp_Seconds");

        LblUsageMergeTitle.Text = Localization.Get("UsageMerge_Title");
        ChkUsageMerge.Content = Localization.Get("UsageMerge_Enabled");
        LblUsageMergeItems.Text = Localization.Get("UsageMerge_Items");
        ChkUsageMergeMic.Content = Localization.Get("Comp_Mic");
        ChkUsageMergeCam.Content = Localization.Get("Comp_Cam");


        TxtUsageMergeNote.Text = Localization.Get("UsageMerge_Note");
        LblCalendarTitle.Text = Localization.Get("Calendar_Title");
        ChkCalendarEnabled.Content = Localization.Get("Calendar_Enabled");
        LblCalendarPath.Text = Localization.Get("Calendar_Path");
        BtnCalendarPick.Content = Localization.Get("Calendar_Browse");
        LblCalendarAdvance.Text = Localization.Get("Calendar_Advance");
        TxtCalendarNote.Text = Localization.Get("Calendar_Note");
        LblRssTitle.Text = Localization.Get("Rss_Title");
        ChkRssEnabled.Content = Localization.Get("Rss_Enabled");
        LblRssUrls.Text = Localization.Get("Rss_Urls");
        LblRssInterval.Text = Localization.Get("Rss_Interval");
        TxtRssNote.Text = Localization.Get("Rss_Note");
        LblMailTitle.Text = Localization.Get("Mail_Title");
        ChkMailEnabled.Content = Localization.Get("Mail_Enabled");
        LblMailServer.Text = Localization.Get("Mail_Server");
        ChkMailSsl.Content = Localization.Get("Mail_Ssl");
        LblMailUser.Text = Localization.Get("Mail_User");
        LblMailPass.Text = Localization.Get("Mail_Pass");
        LblMailInterval.Text = Localization.Get("Mail_Interval");
        TxtMailNote.Text = Localization.Get("Mail_Note");
        ChkAutoUpdateCheck.Content = Localization.Get("Update_Enabled");
        LblUpdateChannel.Text = Localization.Get("Update_Channel");
        CmbUpdateChannelStable.Content = Localization.Get("Update_ChannelStable");
        CmbUpdateChannelBeta.Content = Localization.Get("Update_ChannelBeta");
        BtnCheckUpdate.Content = Localization.Get("Update_CheckNow");
        BtnRollback.Content = Localization.Get("Update_Rollback");
        BtnRollback.IsEnabled = _updater?.GetRollbackPoint() is not null;
        TxtUpdateNote.Text = Localization.Get("Update_Note");

    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updater is null) return;
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = string.Empty;
        try
        {
            var found = await _updater.CheckAsync(_vm.Working.UpdateChannel);
            if (found && _updater.LastAvailable is { } info)
            {
                BtnDownloadUpdate.Content = Localization.Get("Update_DownloadInstall");
                BtnDownloadUpdate.Visibility = Visibility.Visible;
                TxtUpdateStatus.Text = string.Format(Localization.Get("Update_AvailableVersion"), info.Version);
                MessageBox.Show(this,
                    string.Format(Localization.Get("Update_AvailableMessage"), info.Version),
                    Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                BtnDownloadUpdate.Visibility = Visibility.Collapsed;
                MessageBox.Show(this, Localization.Get("Update_None"),
                    Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Check update failed: {ex}");
            MessageBox.Show(this, ex.Message, Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updater?.LastAvailable is not { } info) return;
        if (MessageBox.Show(this, string.Format(Localization.Get("Update_ConfirmInstall"), info.Version),
                Localization.Get("Update_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BtnCheckUpdate.IsEnabled = false;
        BtnDownloadUpdate.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        TxtUpdateStatus.Text = Localization.Get("Update_Downloading");
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                TxtUpdateStatus.Text = string.Format(Localization.Get("Update_DownloadingPercent"),
                    (int)Math.Round(p * 100));
            });
            var file = await _updater.DownloadAsync(info, progress);
            UpdateProgress.Value = 1;
            TxtUpdateStatus.Text = Localization.Get("Update_Verified");

            // 便携版更新前留一个回滚点；安装包升级由安装器自身管理版本。
            if (!info.Asset.Name.Contains("-Setup-", StringComparison.OrdinalIgnoreCase))
                _updater.CreateRollbackPoint();

            var needsElevation = !UpdaterService.CurrentInstallIsWritable();
            if (!_updater.LaunchInstaller(file, needsElevation))
            {
                MessageBox.Show(this, Localization.Get("Update_LaunchFailed"),
                    Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 给更新脚本一点时间挂起等待，再正常退出；脚本会在进程退出后替换并重启。
            await Task.Delay(400);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Download update failed", ex);
            MessageBox.Show(this, ex.Message, Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtUpdateStatus.Text = ex.Message;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            BtnDownloadUpdate.IsEnabled = true;
        }
    }
    // ── 组件顺序：横向拖拽排序（鼠标捕获 + 落点计算，不经过 OLE DragDrop）──
    // 说明：WPF 的 DragDrop.DoDragDrop 会进入 OLE 嵌套消息循环；在部分注入环境/CLR 组合下
    // 会触发 0xc0000005/0x80131506（本次崩溃即发生在 Chip_PreviewMouseMove → DoDragDrop）。

    private void InitUpdateChannel()
    {
        CmbUpdateChannel.SelectionChanged += (_, _) =>
        {
            var channel = CmbUpdateChannel.SelectedIndex == 1 ? "Beta" : "Stable";
            _vm.Working.UpdateChannel = channel;
        };
        CmbUpdateChannel.SelectedIndex = string.Equals(_vm.Working.UpdateChannel, "Beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_updater?.GetRollbackPoint() is not { } point)
        {
            MessageBox.Show(this, Localization.Get("Update_RollbackNone"), Localization.Get("Update_Title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, string.Format(Localization.Get("Update_RollbackConfirm"), point.Version),
                Localization.Get("Update_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var needsElevation = !UpdaterService.CurrentInstallIsWritable();
        if (!_updater.LaunchRollback(needsElevation))
        {
            MessageBox.Show(this, Localization.Get("Update_RollbackFailed"), Localization.Get("Update_Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _updater.ClearRollbackPoint();
        MessageBox.Show(this, string.Format(Localization.Get("Update_RollbackStarted"), point.Version),
            Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        System.Windows.Application.Current.Shutdown();
    }

    private void MediaUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaAppRow row) _vm.MoveMediaApp(row, -1);
    }

    private void MediaDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaAppRow row) _vm.MoveMediaApp(row, 1);
    }
    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color })
        {
            _vm.Working.AccentColor = color;
            AccentBox.Text = color;
        }
    }

    private void BrowseLyrics_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Localization.Get("Lyrics_Folder"),
            SelectedPath = _vm.Working.LyricsFolder,
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _vm.Working.LyricsFolder = dlg.SelectedPath;
    }

    private async void ClearLyricsCache_Click(object sender, RoutedEventArgs e)
    {
        if (_island is null) return;
        BtnClearLyricsCache.IsEnabled = false;
        TxtLyricsCacheStatus.Text = "正在清理…";
        try
        {
            await _island.ClearLyricsDiskCacheAsync();
            TxtLyricsCacheStatus.Text = "已清空，正在重新获取当前歌词";
        }
        catch (Exception ex)
        {
            TxtLyricsCacheStatus.Text = "清理失败";
            AppLogger.Warn($"Clear lyrics cache failed: {ex.Message}");
        }
        finally
        {
            BtnClearLyricsCache.IsEnabled = true;
        }
    }
    /// <summary>邮箱密码由 PasswordBox 输入（PasswordBox 不支持绑定），实时写入 Working，随自动保存立即生效。</summary>
    private void MailPass_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Working.MailPassword = TxtMailPass.Password;
    }

    /// <summary>填充展开卡片模板下拉框（经典/媒体大卡片）。</summary>
    private void InitCardStyle()
    {
        if (CboCardStyle is null) return;
        CboCardStyle.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("Classic", Localization.Get("MediaInfo_CardClassic")),
            new KeyValuePair<string, string>("Hero", Localization.Get("MediaInfo_CardHero")),
        };
        CboCardStyle.DisplayMemberPath = "Value";
        CboCardStyle.SelectedValuePath = "Key";
        CboCardStyle.SelectedValue = _vm.Working.ExpandedCardStyle;
    }

    /// <summary>模板切换即时生效（应用层 Settings.Changed 会刷新展开区块可见性）。</summary>
    private void CardStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboCardStyle.SelectedValue is string style && style != _vm.Working.ExpandedCardStyle)
            _vm.Working.ExpandedCardStyle = style;
    }

    private void CalendarPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "iCalendar 文件 (*.ics)|*.ics|所有文件 (*.*)|*.*", DefaultExt = ".ics" };
        if (dlg.ShowDialog() == true)
            _vm.Working.CalendarIcsPath = dlg.FileName;
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureDirectories();
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{AppPaths.AppDataDir}\"",
            UseShellExecute = true,
        });
    }

    private void MediaCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var theme = new ThemeService();
            theme.Apply(_service.Current);
            var win = new MediaSourceCheckWindow(_service, _cider, theme) { Owner = this };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Open media source check failed", ex);
            MessageBox.Show(this, "无法打开媒体源检查：" + ex.Message, "媒体源检查",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            text = await DiagnosticsCommand.RunAsync(_service, _cider);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Diagnostics failed: {ex}");
            text = "ERROR: " + ex.Message;
        }
        var win = new Window
        {
            Title = Localization.Get("Diagnostics"),
            Width = 640,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Content = new System.Windows.Controls.TextBox
            {
                Text = text,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new Thickness(8),
            },
        };
        win.ShowDialog();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = "winisland-settings.json",
            Filter = "JSON|*.json",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, _service.Export());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Localization.Get("Settings"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON|*.json", DefaultExt = ".json" };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var json = File.ReadAllText(dlg.FileName);
                if (_service.TryImport(json))
                {
                    MessageBox.Show(this, Localization.Get("SettingsSaved"), Localization.Get("Settings"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                }
                else
                {
                    MessageBox.Show(this, Localization.Get("ImportFailed"), Localization.Get("Settings"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Localization.Get("Settings"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>快捷操作设置行：上移/下移（Tag 为方向）。</summary>
    private void QuickActionMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ButtonBase btn
            && btn.Tag is string dir && btn.CommandParameter is string key)
            _vm.MoveQuickAction(key, dir == "1" ? 1 : -1);
    }

    /// <summary>关于页：在 WinIslands 名称后追加当前版本号（如 WinIslands 1.1.7）。</summary>
    private static string BuildAboutText()
    {
        var about = Localization.Get("About_Text");
        try
        {
            var ver = typeof(SettingsWindow).Assembly.GetName().Version;
            if (ver is null) return about;
            const string name = "WinIslands";
            var idx = about.IndexOf(name, StringComparison.Ordinal);
            if (idx < 0) return about;
            return about.Insert(idx + name.Length, string.Concat(" ", ver.ToString(3)));
        }
        catch
        {
            return about;
        }
    }

}

    /// <summary>唤起来源应用窗口所需的少量 Win32 互操作。</summary>
internal static class NativeUser32
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
}
