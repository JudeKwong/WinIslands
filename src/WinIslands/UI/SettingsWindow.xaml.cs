using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private readonly DispatcherTimer _glassRecapture;
    private IntPtr _glassHost;
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
                    ThemeMode.Light => false,
                    ThemeMode.Dark => true,
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

        // 即时生效：轮询检测 Working 变化并立即应用（无保存按钮）
        _lastAppliedJson = SettingsService.Serialize(_vm.Working);
        _autoApply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _service.Save(); };
        // 拖动结束后重新采样窗后桌面，让玻璃始终折射真实内容；拖动中不采样以省开销。
        _glassRecapture = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _glassRecapture.Tick += (_, _) => { _glassRecapture.Stop(); RecaptureGlass(); };
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
            try { _glassRecapture.Stop(); } catch { }
            try { LocationChanged -= OnGlassLocationChanged; } catch { }
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
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => ThemeHelper.IsSystemDark(),
        };
        // 注意：不用 ApplyAcrylic —— 在 AllowsTransparency 窗口上会渲染出黑色大块
        WindowEffects.ApplyDarkMode(hwnd, dark);
        ApplyGlassPalette(dark);

        // The HWND exists but is still hidden here, so CopyFromScreen sees the real
        // desktop/windows behind it. The extra margin keeps the frosted sample valid
        // while the user nudges the window around.
        GlassBackdrop.CaptureForWindow(hwnd, null, 360);
        _glassHost = hwnd;
        LocationChanged -= OnGlassLocationChanged;
        LocationChanged += OnGlassLocationChanged;
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

    private void OnGlassLocationChanged(object? sender, EventArgs e)
    {
        _glassRecapture.Stop();
        _glassRecapture.Start();
    }

    private void RecaptureGlass()
    {
        if (_glassHost == IntPtr.Zero) return;
        try { GlassBackdrop.RecaptureBehindWindow(_glassHost, 360); } catch { }
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
        BtnCheckUpdate.Content = Localization.Get("Update_CheckNow");
        TxtUpdateNote.Text = Localization.Get("Update_Note");

    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updater is null) return;
        BtnCheckUpdate.IsEnabled = false;
        try
        {
            var found = await _updater.CheckAsync();
            MessageBox.Show(this, found ? Localization.Get("Update_Found") : Localization.Get("Update_None"),
                Localization.Get("Update_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
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

    // ── 组件顺序：横向拖拽排序（鼠标捕获 + 落点计算，不经过 OLE DragDrop）──
    // 说明：WPF 的 DragDrop.DoDragDrop 会进入 OLE 嵌套消息循环；在部分注入环境/CLR 组合下
    // 会触发 0xc0000005/0x80131506（本次崩溃即发生在 Chip_PreviewMouseMove → DoDragDrop）。
    // 改为纯 WPF 鼠标捕获实现，拖拽排序不再依赖 OLE。
    private Point _dragStart;
    private OrderItem? _dragItem;
    private FrameworkElement? _dragChip;
    private bool _dragActive;

    private void Chip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (sender as FrameworkElement)?.DataContext as OrderItem;
        _dragChip = sender as FrameworkElement;
        _dragActive = _dragItem is not null;
    }

    private void Chip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragActive || _dragItem is null || _dragChip is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndChipDrag(); return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
        if (_dragChip.IsMouseCaptured) return;
        if (!_dragChip.CaptureMouse()) { EndChipDrag(); return; }
        _dragChip.Opacity = 0.6;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeWE;
    }

    private void Chip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive || _dragItem is null) return;
        var chip = sender as FrameworkElement ?? _dragChip;
        var strip = FindAncestor<System.Windows.Controls.ItemsControl>(chip);
        if (strip is not null)
        {
            var x = e.GetPosition(strip).X;
            var target = _vm.OrderItems.Count - 1;
            for (var i = 0; i < _vm.OrderItems.Count; i++)
            {
                if (strip.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                {
                    var mid = fe.TransformToAncestor(strip).Transform(new Point(fe.ActualWidth / 2, 0)).X;
                    if (x < mid) { target = i; break; }
                    target = i;
                }
            }
            _vm.MoveOrderItemTo(_dragItem, target);
        }
        EndChipDrag();
        e.Handled = true;
    }

    private void Chip_LostMouseCapture(object sender, MouseEventArgs e) => EndChipDrag();

    private void EndChipDrag()
    {
        _dragActive = false;
        var chip = _dragChip;
        _dragChip = null;
        _dragItem = null;
        if (chip is not null)
        {
            try { if (chip.IsMouseCaptured) chip.ReleaseMouseCapture(); } catch { /* ignore */ }
            chip.Opacity = 1;
        }
        Mouse.OverrideCursor = null;
    }

    // ── 页面管理：竖向拖拽排序（岛仔 page_order 交互）──
    private Point _pageDragStart;
    private PageManageRow? _pageDragRow;
    private FrameworkElement? _pageDragHandle;
    private bool _pageDragActive;

    private void PageRow_DragHandleDown(object sender, MouseButtonEventArgs e)
    {
        _pageDragStart = e.GetPosition(null);
        _pageDragRow = (sender as FrameworkElement)?.DataContext as PageManageRow;
        _pageDragHandle = sender as FrameworkElement;
        _pageDragActive = _pageDragRow is not null;
    }

    private void PageRow_DragHandleMove(object sender, MouseEventArgs e)
    {
        if (!_pageDragActive || _pageDragRow is null || _pageDragHandle is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPageRowDrag(); return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _pageDragStart.X) < 4 && Math.Abs(pos.Y - _pageDragStart.Y) < 4) return;
        if (_pageDragHandle.IsMouseCaptured) return;
        if (!_pageDragHandle.CaptureMouse()) { EndPageRowDrag(); return; }
        _pageDragHandle.Opacity = 0.55;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeNS;
    }

    private void PageRow_DragHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pageDragActive || _pageDragRow is null) return;
        var list = PageManageList;
        var y = e.GetPosition(list).Y;
        var target = _vm.PageManageRows.Count - 1;
        for (var i = 0; i < _vm.PageManageRows.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
            {
                var mid = fe.TransformToAncestor(list).Transform(new Point(0, fe.ActualHeight / 2)).Y;
                if (y < mid) { target = i; break; }
                target = i;
            }
        }
        _vm.MovePageRowTo(_pageDragRow, target);
        EndPageRowDrag();
        e.Handled = true;
    }

    private void PageRow_LostMouseCapture(object sender, MouseEventArgs e) => EndPageRowDrag();

    private void EndPageRowDrag()
    {
        _pageDragActive = false;
        var handle = _pageDragHandle;
        _pageDragHandle = null;
        _pageDragRow = null;
        if (handle is not null)
        {
            try { if (handle.IsMouseCaptured) handle.ReleaseMouseCapture(); } catch { }
            handle.Opacity = 1;
        }
        Mouse.OverrideCursor = null;
    }

    // ── 组件管理：竖向拖拽排序（岛仔页面管理同款）──
    private Point _componentDragStart;
    private ComponentRow? _componentDragRow;
    private FrameworkElement? _componentDragHandle;
    private bool _componentDragActive;

    private void ComponentRow_DragHandleDown(object sender, MouseButtonEventArgs e)
    {
        _componentDragStart = e.GetPosition(null);
        _componentDragRow = (sender as FrameworkElement)?.DataContext as ComponentRow;
        _componentDragHandle = sender as FrameworkElement;
        _componentDragActive = _componentDragRow is not null;
    }

    private void ComponentRow_DragHandleMove(object sender, MouseEventArgs e)
    {
        if (!_componentDragActive || _componentDragRow is null || _componentDragHandle is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndComponentRowDrag(); return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _componentDragStart.X) < 4 && Math.Abs(pos.Y - _componentDragStart.Y) < 4) return;
        if (_componentDragHandle.IsMouseCaptured) return;
        if (!_componentDragHandle.CaptureMouse()) { EndComponentRowDrag(); return; }
        _componentDragHandle.Opacity = 0.55;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeNS;
    }

    private void ComponentRow_DragHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (!_componentDragActive || _componentDragRow is null) return;
        var list = ComponentManageList;
        var y = e.GetPosition(list).Y;
        var target = _vm.ComponentManageRows.Count - 1;
        for (var i = 0; i < _vm.ComponentManageRows.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
            {
                var mid = fe.TransformToAncestor(list).Transform(new Point(0, fe.ActualHeight / 2)).Y;
                if (y < mid) { target = i; break; }
                target = i;
            }
        }
        _vm.MoveComponentRowTo(_componentDragRow, target);
        EndComponentRowDrag();
        e.Handled = true;
    }

    private void ComponentRow_LostMouseCapture(object sender, MouseEventArgs e) => EndComponentRowDrag();

    private void EndComponentRowDrag()
    {
        _componentDragActive = false;
        var handle = _componentDragHandle;
        _componentDragHandle = null;
        _componentDragRow = null;
        if (handle is not null)
        {
            try { if (handle.IsMouseCaptured) handle.ReleaseMouseCapture(); } catch { }
            handle.Opacity = 1;
        }
        Mouse.OverrideCursor = null;
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

    /// <summary>刷新设置页中的插件列表（名称、版本、状态与描述）。</summary>
    private void RefreshPluginList()
    {
        if (PluginList is null) return;
        if (_plugins is null)
        {
            PluginList.ItemsSource = null;
            LblPluginCount.Text = string.Empty;
            TxtPluginEmpty.Text = Localization.Get("Plugins_Unavailable");
            TxtPluginEmpty.Visibility = Visibility.Visible;
            return;
        }

        var rows = _plugins.Plugins
            .OrderBy(p => string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PluginRow
            {
                Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
                VersionText = string.IsNullOrWhiteSpace(p.Version) ? string.Empty : "v" + p.Version,
                Description = string.IsNullOrWhiteSpace(p.Description)
                    ? Localization.Get("Plugins_NoDescription")
                    : p.Description,
                MetaText = BuildPluginMeta(p),
                StatusText = Localization.Get(p.Enabled ? "Plugins_Enabled" : "Plugins_Disabled"),
                Enabled = p.Enabled,
            })
            .ToList();

        PluginList.ItemsSource = rows;
        LblPluginCount.Text = string.Format(Localization.Get("Plugins_Count"), rows.Count);
        TxtPluginEmpty.Text = Localization.Get("Plugins_Empty");
        TxtPluginEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildPluginMeta(PluginManifest plugin)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(plugin.Author))
            parts.Add(string.Format(Localization.Get("Plugins_Author"), plugin.Author));
        var permissions = plugin.Permissions.Count == 0
            ? Localization.Get("Plugins_NoPermissions")
            : string.Join(", ", plugin.Permissions);
        parts.Add(string.Format(Localization.Get("Plugins_Permissions"), permissions));
        if (plugin.IntervalSeconds > 0)
            parts.Add(string.Format(Localization.Get("Plugins_Interval"), plugin.IntervalSeconds));
        return string.Join("  ·  ", parts);
    }

    private void OpenPluginManager_Click(object sender, RoutedEventArgs e)
    {
        _openPluginManager?.Invoke();
        RefreshPluginList();
    }

    private void OpenPluginFolder_Click(object sender, RoutedEventArgs e)
    {
        _openPluginFolder?.Invoke();
        RefreshPluginList();
    }

    private void OpenPluginGuide_Click(object sender, RoutedEventArgs e) => _openPluginGuide?.Invoke();

    private void ReloadPlugins_Click(object sender, RoutedEventArgs e)
    {
        try { _plugins?.Reload(); } catch { }
        RefreshPluginList();
    }

    private sealed class PluginRow
    {
        public string Name { get; init; } = string.Empty;
        public string VersionText { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string MetaText { get; init; } = string.Empty;
        public string StatusText { get; init; } = string.Empty;
        public bool Enabled { get; init; }
    }

}

    /// <summary>唤起来源应用窗口所需的少量 Win32 互操作。</summary>
internal static class NativeUser32
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);
}
