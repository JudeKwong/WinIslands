using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinIslands.Services;
using Color = System.Windows.Media.Color;
using CheckBox = System.Windows.Controls.CheckBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace WinIslands.UI;

/// <summary>插件列表中的一张卡片：包装 manifest + 实时健康状态，供界面绑定与定时刷新。</summary>
public sealed class PluginCardViewModel : ObservableObject
{
    private readonly PluginService _plugins;
    private readonly Action _onChanged;
    private PluginHealthSnapshot? _health;

    public PluginCardViewModel(PluginService plugins, PluginManifest manifest, Action onChanged)
    {
        _plugins = plugins;
        Manifest = manifest;
        _onChanged = onChanged;
    }

    public PluginManifest Manifest { get; }
    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public string Version => Manifest.Version;
    public string Description => Manifest.Description;
    public string PermissionSummary => Manifest.PermissionSummary;
    public bool IsUnverified => !Manifest.IsHashVerified;
    public string SecurityWarning => Manifest.SecurityWarning;
    public string DirectoryPath => Manifest.DirectoryPath;
    public string ManifestPath => Manifest.ManifestPath;

    /// <summary>是否存在可配置参数（有 config 键或非空 config_schema）。</summary>
    public bool HasConfig
    {
        get
        {
            if (Manifest.Config.Count > 0) return true;
            return Manifest.ConfigSchema is JsonElement e
                && e.ValueKind == JsonValueKind.Object
                && e.EnumerateObject().Any();
        }
    }

    public bool Enabled
    {
        get => Manifest.Enabled;
        set
        {
            if (Manifest.Enabled == value) return;

            // 未校验插件不允许启用，必须先“校验并信任”。
            if (value && !Manifest.IsHashVerified)
            {
                var en = Localization.CurrentLanguage == "en-US";
                System.Windows.MessageBox.Show(
                    en
                        ? "This plugin has not been verified, so it will not run. Click \"Verify & Trust\" first."
                        : "该插件尚未校验，当前不会运行。请先点击“校验并信任”。",
                    "WinIslands", MessageBoxButton.OK, MessageBoxImage.Information);
                OnPropertyChanged();
                return;
            }

            // 权限提示：启用带权限声明的插件前必须确认
            if (value && Manifest.Permissions.Count > 0)
            {
                var en = Localization.CurrentLanguage == "en-US";
                var message = en
                    ? $"This plugin requests the following permissions:\n\n{string.Join(", ", Manifest.Permissions)}\n\nOnly enable it if you trust the source."
                    : $"该插件申请以下权限：\n\n{string.Join(", ", Manifest.Permissions)}\n\n仅在你信任插件来源时启用。";
                if (System.Windows.MessageBox.Show(message, "WinIslands", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes)
                {
                    OnPropertyChanged();
                    return;
                }
            }

            if (_plugins.SetEnabled(Id, value)) _onChanged(); // 启用状态以文件为准，成功后重建列表
            else OnPropertyChanged();
        }
    }

    /// <summary>刷新健康状态（由 1 秒定时器调用，代价极低）。</summary>
    public void RefreshHealth() => _health = _plugins.GetHealth(Id);

    public Brush StatusBrush
    {
        get
        {
            var (r, g, b) = !Manifest.IsHashVerified ? (0xFF, 0xA7, 0x26)
                : _health?.IsRunning == true ? (0x34, 0xC7, 0x59)
                : _health?.FailureCount > 0 || !string.IsNullOrEmpty(_health?.LastError) ? (0xFF, 0x59, 0x4F)
                : (0xA8, 0xA8, 0xB8);
            var brush = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>状态文字：运行中 / 空闲 / 尚未运行 / 异常。</summary>
    public string StatusText
    {
        get
        {
            if (!Manifest.IsHashVerified) return "● " + Localization.Get("PluginMgr_Unverified");
            if (_health?.IsRunning == true) return "● " + Localization.Get("PluginMgr_HealthRunning");
            if (_health?.FailureCount > 0 || !string.IsNullOrEmpty(_health?.LastError)) return "● " + Localization.Get("PluginMgr_HealthIdle");
            return _health?.LastSuccessTime is not null
                ? "● " + Localization.Get("PluginMgr_HealthIdle")
                : "● " + Localization.Get("PluginMgr_HealthNeverRun");
        }
    }

    /// <summary>运行详情：上次成功时间 · 耗时 · 失败次数。</summary>
    public string HealthDetail
    {
        get
        {
            if (_health is null) return string.Empty;
            if (_health.LastSuccessTime is DateTime t)
            {
                var time = t.ToLocalTime().ToString("HH:mm:ss");
                return $"{Localization.Get("PluginMgr_HealthLastRun")} {time} · "
                     + $"{Localization.Get("PluginMgr_HealthDuration")} {_health.LastDurationMs} ms · "
                     + $"{Localization.Get("PluginMgr_HealthFailures")} {_health.FailureCount}";
            }
            return string.Empty;
        }
    }

    public string ErrorText =>
        string.IsNullOrEmpty(_health?.LastError) ? string.Empty
            : $"{Localization.Get("PluginMgr_HealthError")}：{_health!.LastError}";

    public void RefreshAll()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(IsUnverified));
        OnPropertyChanged(nameof(SecurityWarning));
        OnPropertyChanged(nameof(HealthDetail));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(StatusBrush));
    }
}

public partial class PluginManagerWindow : Window
{
    private readonly PluginService _plugins;
    private readonly ThemeService _theme;
    private readonly DispatcherTimer _healthTimer;

    public PluginManagerWindow(PluginService plugins, ThemeService theme)
    {
        _plugins = plugins;
        _theme = theme;
        InitializeComponent();
        ApplyTheme();
        RefreshList();
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _healthTimer.Tick += (_, _) => RefreshHealth();
        _healthTimer.Start();
        _statusClear.Tick += (_, _) => { _statusClear.Stop(); MgrStatus.Text = string.Empty; };
    }

    private System.Collections.Generic.List<PluginCardViewModel> _cards = new();

    /// <summary>重建卡片列表（插件增删/启用状态变化后调用）。</summary>
    private void RefreshList()
    {
        _cards = _plugins.Plugins
            .Select(m => new PluginCardViewModel(_plugins, m, RefreshList))
            .ToList();
        PluginList.ItemsSource = _cards;
        RefreshHealth();
    }

    /// <summary>原地刷新所有卡片的健康信息，不重建列表（保持滚动位置）。</summary>
    private void RefreshHealth()
    {
        foreach (var card in _cards) card.RefreshHealth();
        foreach (var card in _cards) card.RefreshAll();
    }

    private void ApplyTheme()
    {
        var dark = _theme.IsDark;
        RootBorder.Background = new SolidColorBrush(dark
            ? Color.FromArgb(0xEE, 0x1B, 0x1B, 0x26)
            : Color.FromArgb(0xEE, 0xF5, 0xF5, 0xFA));
        RootBorder.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        var text = new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF2, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x24));
        var secondary = new SolidColorBrush(dark ? Color.FromRgb(0xB8, 0xB8, 0xC8) : Color.FromRgb(0x5B, 0x5B, 0x68));
        var control = new SolidColorBrush(dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        var border = new SolidColorBrush(dark ? Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        Resources["TextPrimaryBrush"] = text;
        Resources["TextSecondaryBrush"] = secondary;
        Resources["ControlBgBrush"] = control;
        Resources["ControlBorderBrush"] = border;
        Foreground = text;
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginCardViewModel card) return;
        if (_plugins.Restart(card.Id))
        {
            ShowStatus(Localization.Get("PluginMgr_Restarted"));
            card.RefreshAll();
        }
    }

    private readonly DispatcherTimer _statusClear = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>在窗口底部显示一条短暂状态消息（用于导入/导出/新建/重启等操作结果）。</summary>
    private void ShowStatus(string message)
    {
        MgrStatus.Text = message;
        _statusClear.Stop();
        _statusClear.Start();
    }

    private void Config_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginCardViewModel card) return;
        var dialog = new PluginConfigWindow(_plugins, card.Manifest, RefreshList, _theme) { Owner = this };
        try { dialog.ShowDialog(); }
        catch (Exception ex) { AppLogger.Warn($"Open plugin config failed: {ex.Message}"); }
    }

    private void OpenPluginFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && Directory.Exists(path))
            OpenFolder(path);
    }

    /// <summary>把插件打包成 zip 供分享/导入（A4）。</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginCardViewModel card) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出插件",
            FileName = card.Id + ".winislands-plugin.zip",
            DefaultExt = ".zip",
            Filter = "WinIslands 插件包 (*.zip)|*.zip",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_plugins.ExportPlugin(card.Id, dialog.FileName))
            ShowStatus($"{Localization.Get("PluginMgr_Exported")} — {dialog.FileName}");
        else
            ShowStatus(Localization.Get("PluginMgr_ExportFailed"));
    }

    /// <summary>新建插件向导（C3）。</summary>
    private void NewPlugin_Click(object sender, RoutedEventArgs e) => OpenNewPluginWizard();

    /// <summary>列表空白处右键 → 新建插件。</summary>
    private void PluginList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = FindResource("ControlBgBrush") as Brush ?? Brushes.White,
            BorderBrush = FindResource("ControlBorderBrush") as Brush ?? Brushes.Gray,
            Foreground = FindResource("TextPrimaryBrush") as Brush ?? Brushes.Black,
            PlacementTarget = sender as UIElement,
        };
        var item = new System.Windows.Controls.MenuItem { Header = Localization.Get("PluginMgr_NewPlugin") };
        item.Click += (_, _) => OpenNewPluginWizard();
        menu.Items.Add(item);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenNewPluginWizard()
    {
        var dialog = new PluginNewWindow(_plugins, () =>
        {
            RefreshList();
            ShowStatus(Localization.Get("PluginMgr_NewCreated"));
        }, _theme.IsDark)
        { Owner = this };
        try { dialog.ShowDialog(); }
        catch (Exception ex) { AppLogger.Warn($"Open new-plugin wizard failed: {ex.Message}"); }
    }

    /// <summary>用户确认后计算入口 SHA-256 并信任插件。</summary>
    private void Trust_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginCardViewModel card) return;
        if (card.Manifest.IsHashVerified) return;

        var en = Localization.CurrentLanguage == "en-US";
        var message = en
            ? $"Trust plugin \"{card.Name}\"?\n\nWinIslands will compute the SHA-256 of its entry file and write it to plugin.json. Only continue if you trust the source of this plugin."
            : $"确认信任插件“{card.Name}”？\n\nWinIslands 会计算其入口文件的 SHA-256 并写回 plugin.json。请仅在确认插件来源可信时继续。";
        if (System.Windows.MessageBox.Show(message, "WinIslands", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
            return;

        var ok = _plugins.VerifyAndTrust(card.Id);
        ShowStatus(Localization.Get(ok ? "PluginMgr_Trusted" : "PluginMgr_TrustFailed"));
        RefreshList();
    }

    /// <summary>读取 zip 内的 plugin.json，用于导入前展示名称与权限。</summary>
    private static PluginManifest? TryReadZipManifest(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(x => x.FullName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries.FirstOrDefault(x => x.FullName.EndsWith("/plugin.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<PluginManifest>(reader.ReadToEnd());
        }
        catch { return null; }
    }

    /// <summary>从本地 zip 导入插件（A4）。</summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入插件",
            Filter = "WinIslands 插件包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        TryImportZip(dialog.FileName);
    }

    /// <summary>从 https 网址下载插件包并导入（大小与协议受限）。</summary>
    private async void ImportFromUrl_Click(object sender, RoutedEventArgs e)
    {
        var input = PromptForUrl();
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            ShowStatus("仅支持 https 网址");
            return;
        }

        const long maxBytes = 20L * 1024 * 1024;
        var temp = Path.Combine(Path.GetTempPath(), "winislands-plugin-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            ShowStatus("正在下载插件包…");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WinIslands-PluginImport/1.0");
            using var response = await http.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > maxBytes)
            {
                ShowStatus("插件包超过 20 MB，已取消");
                return;
            }
            await using (var file = File.Create(temp))
            {
                await response.Content.CopyToAsync(file).ConfigureAwait(true);
            }
            var length = new FileInfo(temp).Length;
            if (length == 0 || length > maxBytes)
            {
                ShowStatus(Localization.Get("PluginMgr_ImportFailed"));
                return;
            }
            ShowStatus("下载完成，正在校验插件包…");
            TryImportZip(temp);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Plugin URL import failed: {ex.Message}");
            ShowStatus(Localization.Get("PluginMgr_ImportFailed"));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private void TryImportZip(string path)
    {
        var summary = TryReadZipManifest(path);
        if (summary is null)
        {
            ShowStatus(Localization.Get("PluginMgr_ImportFailed"));
            return;
        }

        var permissions = summary.Permissions.Count == 0
            ? Localization.Get("Plugins_NoPermissions")
            : string.Join(", ", summary.Permissions);
        var confirm = string.Format(
            Localization.Get("PluginMgr_ImportConfirm"),
            string.IsNullOrWhiteSpace(summary.Name) ? summary.Id : summary.Name,
            permissions);
        if (System.Windows.MessageBox.Show(confirm, "WinIslands", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;

        var ok = _plugins.ImportPlugin(path);
        ShowStatus(Localization.Get(ok ? "PluginMgr_Imported" : "PluginMgr_ImportFailed"));
        if (ok) RefreshList();
    }

    private string? PromptForUrl()
    {
        var box = new TextBox
        {
            Text = "https://",
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(6, 4, 6, 4),
        };
        var okButton = new Button { Content = "下载并导入", Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
        var cancelButton = new Button { Content = "取消", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "插件包网址（.zip，需 https）", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "从网址导入插件",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Content = panel,
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text : null;
    }

    private void OpenUserFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.PluginsDir);
        OpenFolder(AppPaths.PluginsDir);
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _plugins.Reload();
        RefreshList();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true, ArgumentList = { path } });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Open plugin folder failed: {ex.Message}");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
