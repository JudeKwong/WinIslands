using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace WinIslands.UI;

/// <summary>展开后的一个功能页面（岛仔「页面管理」同款：顺序、显隐、强调色）。</summary>
public sealed class IslandPage : ObservableObject
{
    private bool _isActive;

    public IslandPage(string key, string title, string description, string accentHex)
    {
        Key = key;
        Title = title;
        Description = description;
        AccentHex = accentHex;
        AccentBrush = MakeBrush(accentHex);
    }

    public string Key { get; }
    public string Title { get; }
    public string Description { get; }
    public string AccentHex { get; }
    public Brush AccentBrush { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    private static Brush MakeBrush(string hex)
    {
        var color = Color.FromRgb(0x79, 0x55, 0xB8);
        try
        {
            var h = (hex ?? string.Empty).TrimStart('#');
            if (h.Length == 6)
                color = Color.FromRgb(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
        }
        catch { }
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>设置页「页面管理」里的一行。</summary>
public sealed class PageManageRow : ObservableObject
{
    private readonly Action<PageManageRow, bool> _onVisibilityChanged;
    private bool _visible;

    public PageManageRow(string key, string title, string description, bool visible,
        Action<PageManageRow, bool> onVisibilityChanged)
    {
        Key = key;
        Title = title;
        Description = description;
        _visible = visible;
        _onVisibilityChanged = onVisibilityChanged;
    }

    public string Key { get; }
    public string Title { get; }
    public string Description { get; }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (!Set(ref _visible, value)) return;
            _onVisibilityChanged(this, value);
        }
    }

    /// <summary>用于「至少保留一个页面」的回退，不再触发回调。</summary>
    internal void SetVisibleSilently(bool value)
    {
        if (_visible == value) return;
        _visible = value;
        OnPropertyChanged(nameof(Visible));
    }
}