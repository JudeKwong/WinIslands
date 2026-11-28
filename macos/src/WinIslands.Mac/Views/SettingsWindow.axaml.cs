using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using WinIslands.Services;

namespace WinIslands.Mac.Views;

/// <summary>
/// macOS 风格设置窗口：左侧列表导航 + 右侧内容；全部改动即时生效并落盘，无“确定/完成”按钮。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly IslandApiServer? _api;
    private bool _loading;
    private bool _reallyClosing;

    private ListBox? _nav;
    private StackPanel? _pageGeneral, _pageMedia, _pageApi, _pageLyrics, _pageAbout;
    private ComboBox? _langBox, _themeBox, _posBox, _animBox, _doubleClickBox, _lyricsSourceBox;
    private Slider? _animDurSlider;
    private TextBlock? _animDurLabel;
    private CheckBox? _hideNoMediaBox, _alwaysVisibleBox, _hoverExpandBox;
    private TextBox? _hoverExpandDelayBox, _hoverCollapseDelayBox;
    private CheckBox? _ciderEnabledBox;
    private TextBox? _ciderPortBox, _ciderTokenBox;
    private CheckBox? _apiEnabledBox;
    private TextBox? _apiPortBox, _apiTokenBox, _apiDurationBox;
    private TextBlock? _apiStatusText;
    private CheckBox? _amllBox, _onlineLyricsBox;
    private TextBox? _lyricsFolderBox;
    private TextBlock? _aboutVersion;

    public SettingsWindow(SettingsService settings, IslandApiServer? api)
    {
        _settings = settings;
        _api = api;
        InitializeComponent();

        _nav = this.FindControl<ListBox>("NavList");
        _pageGeneral = this.FindControl<StackPanel>("PageGeneral");
        _pageMedia = this.FindControl<StackPanel>("PageMedia");
        _pageApi = this.FindControl<StackPanel>("PageApi");
        _pageLyrics = this.FindControl<StackPanel>("PageLyrics");
        _pageAbout = this.FindControl<StackPanel>("PageAbout");
        _langBox = this.FindControl<ComboBox>("LangBox");
        _themeBox = this.FindControl<ComboBox>("ThemeBox");
        _posBox = this.FindControl<ComboBox>("PosBox");
        _animBox = this.FindControl<ComboBox>("AnimBox");
        _doubleClickBox = this.FindControl<ComboBox>("DoubleClickBox");
        _animDurSlider = this.FindControl<Slider>("AnimDurSlider");
        _animDurLabel = this.FindControl<TextBlock>("AnimDurLabel");
        _hideNoMediaBox = this.FindControl<CheckBox>("HideNoMediaBox");
        _alwaysVisibleBox = this.FindControl<CheckBox>("AlwaysVisibleBox");
        _hoverExpandBox = this.FindControl<CheckBox>("HoverExpandBox");
        _hoverExpandDelayBox = this.FindControl<TextBox>("HoverExpandDelayBox");
        _hoverCollapseDelayBox = this.FindControl<TextBox>("HoverCollapseDelayBox");
        _ciderEnabledBox = this.FindControl<CheckBox>("CiderEnabledBox");
        _ciderPortBox = this.FindControl<TextBox>("CiderPortBox");
        _ciderTokenBox = this.FindControl<TextBox>("CiderTokenBox");
        _apiEnabledBox = this.FindControl<CheckBox>("ApiEnabledBox");
        _apiPortBox = this.FindControl<TextBox>("ApiPortBox");
        _apiTokenBox = this.FindControl<TextBox>("ApiTokenBox");
        _apiDurationBox = this.FindControl<TextBox>("ApiDurationBox");
        _apiStatusText = this.FindControl<TextBlock>("ApiStatusText");
        _lyricsSourceBox = this.FindControl<ComboBox>("LyricsSourceBox");
        _amllBox = this.FindControl<CheckBox>("AmllBox");
        _onlineLyricsBox = this.FindControl<CheckBox>("OnlineLyricsBox");
        _lyricsFolderBox = this.FindControl<TextBox>("LyricsFolderBox");
        _aboutVersion = this.FindControl<TextBlock>("AboutVersion");

        if (_nav is not null) _nav.SelectionChanged += (_, _) => SwitchPage(_nav.SelectedIndex);
        WireEvents();
        LoadFromSettings();

        _aboutVersion!.Text = $"WinIslands {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "2.0.2"}（macOS）";
        Closing += (_, e) =>
        {
            if (_reallyClosing) return;
            e.Cancel = true; // 关闭即隐藏，复用同一窗口
            Hide();
        };
    }

    /// <summary>真正退出前调用（绕过“关闭即隐藏”逻辑）。</summary>
    public void CloseForQuit()
    {
        _reallyClosing = true;
        Close();
    }

    // 与 Avalonia.NameGenerator 生成代码等价（生成器需 Roslyn 4.14+，本机 SDK 为 4.11）
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void SwitchPage(int index)
    {
        if (_pageGeneral is null) return;
        _pageGeneral.IsVisible = index == 0;
        _pageMedia!.IsVisible = index == 1;
        _pageApi!.IsVisible = index == 2;
        _pageLyrics!.IsVisible = index == 3;
        _pageAbout!.IsVisible = index == 4;
    }

    private void WireEvents()
    {
        if (_langBox is not null)
            _langBox.SelectionChanged += (_, _) => Apply(() => _settings.Current.Language = _langBox.SelectedIndex == 1 ? "en-US" : "zh-CN");
        if (_themeBox is not null)
            _themeBox.SelectionChanged += (_, _) => Apply(() =>
            {
                _settings.Current.Theme = _themeBox.SelectedIndex switch
                {
                    1 => ThemeMode.Light,
                    2 => ThemeMode.Dark,
                    _ => ThemeMode.Auto,
                };
                ApplyThemeVariant(_settings.Current.Theme);
            });
        if (_posBox is not null)
            _posBox.SelectionChanged += (_, _) => Apply(() => _settings.Current.Position = _posBox.SelectedIndex == 1 ? IslandPosition.Right : IslandPosition.Center);
        if (_animBox is not null)
            _animBox.SelectionChanged += (_, _) => Apply(() => _settings.Current.AnimationStyle = _animBox.SelectedIndex switch
            {
                1 => "Soft",
                2 => "Elastic",
                3 => "Fade",
                _ => "Spring",
            });
        if (_animDurSlider is not null)
            _animDurSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                if (_loading) return;
                var v = (int)_animDurSlider.Value;
                if (_animDurLabel is not null) _animDurLabel.Text = $"动画时长：{v}ms";
                Apply(() => _settings.Current.IslandAnimationDuration = v);
            };
        if (_doubleClickBox is not null)
            _doubleClickBox.SelectionChanged += (_, _) => Apply(() => _settings.Current.DoubleClickAction = _doubleClickBox.SelectedIndex switch
            {
                1 => "OpenSettings",
                2 => "None",
                _ => "PlayPause",
            });
        if (_hideNoMediaBox is not null)
            _hideNoMediaBox.IsCheckedChanged += (_, _) => Apply(() => _settings.Current.HideWhenNoMedia = _hideNoMediaBox.IsChecked == true);
        if (_alwaysVisibleBox is not null)
            _alwaysVisibleBox.IsCheckedChanged += (_, _) => Apply(() => _settings.Current.IslandAlwaysVisible = _alwaysVisibleBox.IsChecked == true);
        if (_hoverExpandBox is not null)
            _hoverExpandBox.IsCheckedChanged += (_, _) => Apply(() => _settings.Current.HoverExpandEnabled = _hoverExpandBox.IsChecked == true);
        BindIntBox(_hoverExpandDelayBox, v => _settings.Current.HoverExpandDelayMs = Math.Clamp(v, 0, 1500));
        BindIntBox(_hoverCollapseDelayBox, v => _settings.Current.HoverCollapseDelayMs = Math.Clamp(v, 0, 2000));

        if (_ciderEnabledBox is not null)
            _ciderEnabledBox.IsCheckedChanged += (_, _) => Apply(() => _settings.Current.CiderEnabled = _ciderEnabledBox.IsChecked == true);
        BindIntBox(_ciderPortBox, v => _settings.Current.CiderPort = v);
        BindTextBox(_ciderTokenBox, v => _settings.Current.CiderToken = v);

        if (_apiEnabledBox is not null)
            _apiEnabledBox.IsCheckedChanged += (_, _) => Apply(() => _settings.Current.IslandApiEnabled = _apiEnabledBox.IsChecked == true);
        BindIntBox(_apiPortBox, v =>
        {
            if (v is > 0 and < 65536) _settings.Current.IslandApiPort = v;
        });
        BindTextBox(_apiTokenBox, v => _settings.Current.IslandApiToken = v);
        BindIntBox(_apiDurationBox, v =>
        {
            if (v > 0) _settings.Current.IslandApiDefaultDuration = v;
        });
        if (_apiPortBox is not null)
            _apiPortBox.LostFocus += (_, _) => RestartApiIfNeeded();

        if (_lyricsSourceBox is not null)
            _lyricsSourceBox.SelectionChanged += (_, _) => Apply(() => _settings.Current.LyricsPreferredSource = _lyricsSourceBox.SelectedIndex switch
            {
                1 => "Local",
                2 => "Amll",
                3 => "Cider",
                4 => "Online",
                _ => "Auto",
            });
        if (_amllBox is not null)
            _amllBox.IsCheckedChanged += (_, _) => Apply(() => _settings.Current.AmllTtmlEnabled = _amllBox.IsChecked == true);
        if (_onlineLyricsBox is not null)
            _onlineLyricsBox.IsCheckedChanged += (_, _) => Apply(() => _settings.Current.OnlineLyricsEnabled = _onlineLyricsBox.IsChecked == true);
        BindTextBox(_lyricsFolderBox, v => _settings.Current.LyricsFolder = v);
    }

    private void BindTextBox(TextBox? box, Action<string> apply)
    {
        if (box is null) return;
        void Commit() => Apply(() => apply(box.Text ?? ""));
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
        };
    }

    private void BindIntBox(TextBox? box, Action<int> apply)
    {
        if (box is null) return;
        void Commit()
        {
            if (int.TryParse((box.Text ?? "").Trim(), out var v)) Apply(() => apply(v));
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
        };
    }

    /// <summary>应用一次改动并持久化（_loading 时跳过，避免初始化回写）。</summary>
    private void Apply(Action mutate)
    {
        if (_loading) return;
        _settings.Update(_ => mutate());
        RefreshApiStatus();
    }

    private void RestartApiIfNeeded()
    {
        if (_api is null) return;
        try
        {
            _api.Stop();
            if (_settings.Current.IslandApiEnabled) _api.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Island API restart failed: {ex.Message}");
        }
        RefreshApiStatus();
    }

    private void RefreshApiStatus()
    {
        if (_apiStatusText is null) return;
        _apiStatusText.Text = _api is null
            ? ""
            : _api.IsRunning
                ? $"运行中：http://127.0.0.1:{_settings.Current.IslandApiPort}"
                : "已停止（启用后立即生效）";
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = _settings.Current;
        if (_langBox is not null) _langBox.SelectedIndex = s.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (_themeBox is not null) _themeBox.SelectedIndex = s.Theme switch { ThemeMode.Light => 1, ThemeMode.Dark => 2, _ => 0 };
        if (_posBox is not null) _posBox.SelectedIndex = s.Position == IslandPosition.Right ? 1 : 0;
        if (_animBox is not null) _animBox.SelectedIndex = (s.AnimationStyle ?? "Spring") switch
        {
            "Soft" => 1,
            "Elastic" => 2,
            "Fade" => 3,
            _ => 0,
        };
        if (_animDurSlider is not null) _animDurSlider.Value = Math.Clamp(s.IslandAnimationDuration, 300, 1400);
        if (_animDurLabel is not null) _animDurLabel.Text = $"动画时长：{Math.Clamp(s.IslandAnimationDuration, 300, 1400)}ms";
        if (_doubleClickBox is not null) _doubleClickBox.SelectedIndex = (s.DoubleClickAction ?? "PlayPause") switch
        {
            "OpenSettings" => 1,
            "None" => 2,
            _ => 0,
        };
        if (_hideNoMediaBox is not null) _hideNoMediaBox.IsChecked = s.HideWhenNoMedia;
        if (_alwaysVisibleBox is not null) _alwaysVisibleBox.IsChecked = s.IslandAlwaysVisible;
        if (_hoverExpandBox is not null) _hoverExpandBox.IsChecked = s.HoverExpandEnabled;
        if (_hoverExpandDelayBox is not null) _hoverExpandDelayBox.Text = Math.Clamp(s.HoverExpandDelayMs, 0, 1500).ToString();
        if (_hoverCollapseDelayBox is not null) _hoverCollapseDelayBox.Text = Math.Clamp(s.HoverCollapseDelayMs, 0, 2000).ToString();
        if (_ciderEnabledBox is not null) _ciderEnabledBox.IsChecked = s.CiderEnabled;
        if (_ciderPortBox is not null) _ciderPortBox.Text = s.CiderPort.ToString();
        if (_ciderTokenBox is not null) _ciderTokenBox.Text = s.CiderToken;
        if (_apiEnabledBox is not null) _apiEnabledBox.IsChecked = s.IslandApiEnabled;
        if (_apiPortBox is not null) _apiPortBox.Text = s.IslandApiPort.ToString();
        if (_apiTokenBox is not null) _apiTokenBox.Text = s.IslandApiToken;
        if (_apiDurationBox is not null) _apiDurationBox.Text = s.IslandApiDefaultDuration.ToString();
        if (_lyricsSourceBox is not null) _lyricsSourceBox.SelectedIndex = (s.LyricsPreferredSource ?? "Auto").ToUpperInvariant() switch
        {
            "LOCAL" => 1,
            "AMLL" => 2,
            "CIDER" => 3,
            "ONLINE" => 4,
            _ => 0,
        };
        if (_amllBox is not null) _amllBox.IsChecked = s.AmllTtmlEnabled;
        if (_onlineLyricsBox is not null) _onlineLyricsBox.IsChecked = s.OnlineLyricsEnabled;
        if (_lyricsFolderBox is not null) _lyricsFolderBox.Text = s.LyricsFolder;
        _loading = false;
        RefreshApiStatus();
    }

    /// <summary>主题即时生效（岛内固定深色；应用窗口随主题）。</summary>
    public static void ApplyThemeVariant(ThemeMode mode)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
