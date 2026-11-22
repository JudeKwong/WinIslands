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
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace WinIslands.UI;

public partial class SettingsWindow : Window
{
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
