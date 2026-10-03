using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinIslands.Services;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace WinIslands.UI;

/// <summary>插件参数设置弹窗：按 config_schema 渲染字段，没有 schema 时按现有 config 推断类型。</summary>
public partial class PluginConfigWindow : Window
{
    private readonly PluginService _plugins;
    private readonly PluginManifest _manifest;
    private readonly Action _onChanged;
    private readonly List<FieldEntry> _fields = new();

    private sealed class FieldEntry
    {
        public string Key = "";
        public string Label = "";
        public string? Description;
        public string Type = "string"; // string | boolean | integer | number
        public TextBox? Box;
        public CheckBox? Check;
        public string Original = "";
    }

    public PluginConfigWindow(PluginService plugins, PluginManifest manifest, Action onChanged, ThemeService theme)
    {
        _plugins = plugins;
        _manifest = manifest;
        _onChanged = onChanged;
        InitializeComponent();
        ApplyTheme(theme.IsDark);
        TitleText.Text = manifest.Name + " · 参数";
        SubtitleText.Text = manifest.Id;
        BuildFields();
        if (_fields.Count == 0)
        {
            SaveButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "关闭";
            FieldPanel.Children.Add(new TextBlock
            {
                Text = "该插件没有可配置参数。",
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    private void BuildFields()
    {
        var schema = ParseSchema(_manifest.ConfigSchema);
        var keys = schema.Count > 0
            ? schema.Keys.ToList()
            : _manifest.Config.Keys.ToList();
        foreach (var key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var original = _manifest.Config.TryGetValue(key, out var v) ? v ?? "" : "";
            var entry = new FieldEntry { Key = key, Original = original };
            if (schema.TryGetValue(key, out var spec))
            {
                entry.Label = string.IsNullOrWhiteSpace(spec.Title) ? key : spec.Title!;
                entry.Description = spec.Description;
                entry.Type = string.IsNullOrWhiteSpace(spec.Type) ? "string" : spec.Type!;
            }
            else
            {
                entry.Label = key;
                entry.Type = InferType(original);
            }
            _fields.Add(entry);
            FieldPanel.Children.Add(CreateFieldRow(entry));
        }
    }

    private static string InferType(string value)
    {
        var t = value.Trim();
        if (t.Equals("on", StringComparison.OrdinalIgnoreCase) || t.Equals("off", StringComparison.OrdinalIgnoreCase)
            || t.Equals("true", StringComparison.OrdinalIgnoreCase) || t.Equals("false", StringComparison.OrdinalIgnoreCase))
            return "boolean";
        if (long.TryParse(t, out _) || double.TryParse(t, out _)) return "number";
        return "string";
    }

    private static Dictionary<string, (string? Type, string? Title, string? Description)> ParseSchema(JsonElement? element)
    {
        var result = new Dictionary<string, (string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (element is not JsonElement e || e.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in e.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                string? type = null, title = null, desc = null;
                foreach (var p in prop.Value.EnumerateObject())
                {
                    switch (p.Name.ToLowerInvariant())
                    {
                        case "type": type = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                        case "title": title = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                        case "description": desc = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null; break;
                    }
                }
                result[prop.Name] = (type, title, desc);
            }
        }
        catch { }
        return result;
    }

    private UIElement CreateFieldRow(FieldEntry entry)
    {
        var textColor = FindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var secondary = FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        if (entry.Type == "boolean")
        {
            var check = new CheckBox
            {
                IsChecked = !IsOffValue(entry.Original),
                Foreground = textColor,
                FontSize = 12,
                Content = entry.Label,
                VerticalAlignment = VerticalAlignment.Center,
            };
            entry.Check = check;
            row.Children.Add(check);
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = entry.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = textColor,
                Margin = new Thickness(0, 0, 0, 4),
            });
            var box = new TextBox
            {
                Text = entry.Original,
                FontSize = 12,
                Padding = new Thickness(8, 5, 8, 5),
                VerticalContentAlignment = VerticalAlignment.Center,
                Foreground = textColor,
                Background = FindResource("ControlBgBrush") as Brush,
                BorderBrush = FindResource("ControlBorderBrush") as Brush,
                BorderThickness = new Thickness(1),
            };
            entry.Box = box;
            row.Children.Add(box);
        }

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            row.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 10,
                Foreground = secondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(entry.Type == "boolean" ? 24 : 0, 3, 0, 0),
            });
        }
        return row;
    }

    private static bool IsOffValue(string value)
    {
        var t = value.Trim();
        return t.Equals("off", StringComparison.OrdinalIgnoreCase)
            || t.Equals("false", StringComparison.OrdinalIgnoreCase)
            || t.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in _fields)
        {
            if (field.Check is not null)
                config[field.Key] = field.Check.IsChecked == true ? "on" : "off";
            else
                config[field.Key] = field.Box?.Text?.Trim() ?? "";
        }
        if (_plugins.SetConfig(_manifest.Id, config))
        {
            _onChanged();
            Close();
        }
        else
        {
            MessageBox.Show(this, "参数保存失败，请检查插件目录写入权限。", "WinIslands", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyTheme(bool dark)
    {
        var bg = new SolidColorBrush(dark
            ? Color.FromArgb(0xEE, 0x1B, 0x1B, 0x26)
            : Color.FromArgb(0xEE, 0xF5, 0xF5, 0xFA));
        var border = new SolidColorBrush(dark
            ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        var text = new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF2, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x24));
        var secondary = new SolidColorBrush(dark ? Color.FromRgb(0xB8, 0xB8, 0xC8) : Color.FromRgb(0x5B, 0x5B, 0x68));
        var control = new SolidColorBrush(dark ? Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        var controlBorder = new SolidColorBrush(dark ? Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        RootBorder.Background = bg;
        RootBorder.BorderBrush = border;
        Foreground = text;
        Resources["TextPrimaryBrush"] = text;
        Resources["TextSecondaryBrush"] = secondary;
        Resources["ControlBgBrush"] = control;
        Resources["ControlBorderBrush"] = controlBorder;
        // 已有控件动态资源会跟随；手动创建的控件在 BuildFields 时取资源，需重建一次
        foreach (var entry in _fields)
        {
            if (entry.Check is not null) entry.Check.Foreground = text;
            if (entry.Box is not null)
            {
                entry.Box.Foreground = text;
                entry.Box.Background = control;
                entry.Box.BorderBrush = controlBorder;
            }
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
