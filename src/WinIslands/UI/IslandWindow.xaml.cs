using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using WinIslands.Services;

namespace WinIslands.UI;

/// <summary>
/// iOS 椋庢牸鐏靛姩宀涚獥鍙ｃ€?
/// 绐楀彛灏哄鍥哄畾锛?00x400銆侀€忔槑銆佺偣鍑荤┛閫忥級锛屼粎鍐呴儴鍗＄墖锛圕ard锛夊舰鍙橈細
/// 绱у噾 = 340x56 鑳跺泭锛屽睍寮€ = 400x~384 鍗＄墖鍚戜笅鐢熼暱銆?
/// 鍔ㄧ敾鍙綔鐢ㄤ簬鍗曚釜鍏冪礌锛岀敱 WPF 鍚堟垚绾跨▼ 60fps 椹卞姩锛岄伩鍏嶇獥鍙ｇ骇 Resize 鍗￠】銆?
/// 鐐瑰嚮绌块€忛€氳繃 WM_NCHITTEST 鏄惧紡澶勭悊锛氬崱鐗囧唴鍙氦浜掞紝鍗＄墖澶栫┛閫忋€?
/// </summary>
public partial class IslandWindow : Window, INotifyPropertyChanged
{
    // 灏哄鏉ヨ嚜璁剧疆锛堝彲璋冿級锛屽甫瀹夊叏閽冲埗
    /// <summary>瀛楀彿缂╂斁绯绘暟锛氭暣寮犲崱鐗?LayoutTransform 缂╂斁锛岄€昏緫灏哄 = 瑙嗚灏哄 / 缂╂斁姣斻€?/summary>
    private double FontScale => Math.Clamp(_settings.Current.FontScale, 0.8, 1.4);
    private double ManualCompactW => Math.Clamp(_settings.Current.CompactWidth / FontScale, 240 / FontScale, 520 / FontScale);
    private double ManualCompactH => Math.Clamp(_settings.Current.CompactHeight / FontScale, 48 / FontScale, 140 / FontScale);
    /// <summary>
    /// 绱у噾鎬佹渶澶ц瑙夊搴︼紙浣跨敤鏃堕櫎浠?FontScale 寰楀埌閫昏緫涓婇檺锛夛細
    /// 鏃犳帹閫佹椂淇濇寔 800 涓婇檺閬垮厤宀涜繃瀹斤紱鏈変笂宀涙帹閫佹椂鏀惧鍒版墍鍦ㄦ樉绀哄櫒宸ヤ綔鍖哄搴︼紙鐣欒竟璺濓級锛?
    /// 淇濊瘉闀块€氱煡鍑虹幇鏃跺彸渚х粍浠讹紙濯掍綋鎸夐挳/鏃堕挓绛夛級涓嶈 ClipToBounds 瑁佸垏銆佹枃瀛楀畬鏁存樉绀恒€?
    /// </summary>
    private double MaxCompactVisualWidth
    {
        get
        {
            if (!_vm.HasActivePush) return 800;
            try
            {
                var workW = ScreenHelper.DpiWorkArea(_screen).Width;
                return Math.Max(800, workW - 48); // 宸﹀彸鍚勭暀 24 杈硅窛锛岄伩鍏嶈创鍒板睆骞曡竟缂?
            }
            catch
            {
                return 800;
            }
        }
    }

    /// <summary>
    /// 瀹炴祴绱у噾鍐呭瀹藉害锛堝矝鍙鏃剁簿纭创鍚堢粍浠讹級銆?
    /// 宀涢殣钘?鏈竷灞€鏃惰繑鍥炪€屼及绠椾笌鎵嬪姩鍊煎彇杈冨ぇ鑰呫€嶏紝閬垮厤鍚姩鐬棿 Card 杩囩獎瀵艰嚧缁勪欢鎸ゅ帇銆佹樉绀轰笉瀹屾暣銆?
    /// </summary>
    private double MeasureCompactWidthNow()
    {
        var fallback = Math.Max(_vm.EstimatedCompactWidth, ManualCompactW);
        try
        {
            if (!IsLoaded || !_vm.IsVisible) return fallback;
            PillRow.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = PillRow.DesiredSize.Width;
            // 瀛楀彿缂╂斁锛氶€昏緫瀹藉害鎸夋瘮渚嬬缉灏忥紝鍗＄墖娓叉煋鏃跺啀鏀惧ぇ锛屾渶缁堣瑙夊搴︿笉鍙?
            return w >= 20 ? Math.Clamp((w + 56) / FontScale, 240 / FontScale, MaxCompactVisualWidth / FontScale) : fallback; // 鎬荤暀鐧?56锛堝乏渚?22 + 鍙充晶 24锛屽彸渚х暐澶氾級
        }
        catch
        {
            return fallback;
        }
    }

    private double _noPushCompactW;
    private bool _noPushWValid;

    /// <summary>
    /// 鎺ㄩ€佸崱鐗囧湪绱у噾鎬佹墍闇€瀹藉害锛氬崟琛屾樉绀猴紙鍥炬爣 30 + 闂磋窛 8 + 鏍囬 + 鍗曡鎽樿涓婇檺 190锛夛紝
    /// 鎽樿杩囬暱鐢?TextTrimming 鐪佺暐锛屾暣浣撳搴︾揣鍑戙€佷笉澶у箙鎾戝鐏靛姩宀涖€?
    /// </summary>
    private double PushCardCompactWidth()
    {
        var p = _vm.ActivePush;
        if (p is null) return 0;
        double need = 38; // 鍥炬爣 30 + 闂磋窛 8
        need += Math.Min(TextW(p.Title, 13, 7), 240); // 鏍囬锛圫emiBold锛夛紝涓婇檺 240 涓?XAML MaxWidth 涓€鑷?
        if (!string.IsNullOrEmpty(p.Subtitle) || !string.IsNullOrEmpty(p.Body))
        {
            var summary = !string.IsNullOrEmpty(p.Subtitle) ? p.Subtitle : p.Body;
            need += 8 + Math.Min(TextW(summary, 11.5, 6.2), 200); // 鎽樿鍗曡涓婇檺 200锛岃秴鍑虹渷鐣ワ紙涓?XAML MaxWidth 涓€鑷达級
        }
        return (Math.Min(need, 520) + 48) / FontScale; // +48锛氬乏鍙冲唴杈硅窛(12+12) + 浣欓噺
    }

    /// <summary>浼扮畻澶氳鏂囨湰鐨勬渶瀹藉崟琛屽搴︼細涓枃/鍏ㄨ鎸?cjkPx锛孉SCII 鎸?asciiPx锛堟崲琛岀鎸夎鍒嗙鍙栨渶澶у€硷級銆?/summary>
    private static double TextW(string? s, double cjkPx, double asciiPx)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        double max = 0;
        foreach (var line in s.Split('\n'))
        {
            double w = 0;
            foreach (var ch in line) w += ch > 0x2E7F ? cjkPx : asciiPx;
            if (w > max) max = w;
        }
        return max;
    }


    /// <summary>
    /// 绱у噾瀹藉害锛氭湁鎺ㄩ€佹椂鍙栥€屽疄娴嬶紙鍚帹閫佸崱鐗囧疄闄呭竷灞€瀹斤級銆嶄笌銆屼及绠楋紙鏃犳帹閫佸熀鍑?+ 鎺ㄩ€佸锛夈€嶇殑杈冨ぇ鑰呫€?
    /// 瀹炴祴淇濊瘉浠讳綍鏂囨湰閮芥斁寰椾笅锛堜笉渚濊禆浼扮畻绮惧害锛夛紝浼扮畻鍏滃簳甯冨眬鏃跺簭锛堥甯ф湭甯冨眬鏃跺疄娴嬪彲鑳藉亸灏忥級銆?
    /// </summary>
    private double CompactWidth
    {
        get
        {
            if (!_settings.Current.CompactWidthAuto) return ManualCompactW;
            var autoW = MeasureCompactWidthNow();
            if (_vm.HasActivePush)
            {
                var estBase = _noPushWValid ? _noPushCompactW : Math.Max(autoW, ManualCompactW);
                var estimated = estBase + PushCardCompactWidth();
                return Math.Clamp(Math.Max(autoW, estimated), 240 / FontScale, MaxCompactVisualWidth / FontScale);
            }
            _noPushCompactW = autoW;
            _noPushWValid = true;
            return autoW;
        }
    }
    private double _noPushCompactH;   // 鏃犱笂宀涙帹閫佹椂鐨勭揣鍑戦珮搴︼紙缂撳瓨锛?
    private bool _noPushHValid;

    /// <summary>
    /// 绱у噾楂樺害锛氭湁涓婂矝鎺ㄩ€佹椂浠ユ帹閫佸唴瀹归珮搴︿负鍑嗭紙鏃犳帹閫佸熀鍑嗛珮搴?涓庛€屾帹閫佸崱鐗囬珮搴?+ 涓婁笅鍐呰竟璺?6+6)銆嶅彇杈冨ぇ鑰咃級锛?
    /// 淇濊瘉鍓爣棰?姝ｆ枃/杩涘害/鎸夐挳瀹屾暣鏄剧ず锛屼笉鍐嶈 ClipToBounds 涓婁笅瑁佸垏銆佹枃瀛椾笂绉汇€?
    /// </summary>
    private double CompactHeight
    {
        get
        {
            if (!_settings.Current.CompactHeightAuto) return ManualCompactH; // 鎵嬪姩妯″紡锛氶珮搴︽亽瀹?
            if (_vm.HasActivePush)
            {
                var pushVisualH = _vm.PushCompactHeight;                          // 鎺ㄩ€佸崱鐗囧唴瀹归珮搴︼紙瑙嗚 DIP锛?
                var baseVisualH = _noPushHValid ? _noPushCompactH * FontScale
                                                : Math.Clamp(_vm.EstimatedCompactHeight, 48, 224);
                var visual = Math.Clamp(Math.Max(baseVisualH, pushVisualH + 12), 48, 236); // +12 = ContentGrid 涓婁笅 Margin 6+6
                return visual / FontScale;
            }
            _noPushCompactH = _vm.EstimatedCompactHeight / FontScale;
            _noPushHValid = true;
            return _noPushCompactH;
        }
    }
    private double ExpandedWidth => _settings.Current.ExpandedWidthAuto
        ? _vm.EstimatedExpandedWidth / FontScale
        : Math.Clamp(_settings.Current.ExpandedWidth / FontScale, CompactWidth, 800 / FontScale);
    private double MaxExpandedHeight => _settings.Current.MaxExpandedHeightAuto
        ? _vm.EstimatedExpandedHeight / FontScale
        : Math.Clamp(_settings.Current.MaxExpandedHeight / FontScale, 240 / FontScale, 620 / FontScale);

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTTRANSPARENT = -1;

    private readonly IslandViewModel _vm;
    private readonly ThemeService _theme;
    private readonly SettingsService _settings;
    private readonly System.Windows.Forms.Screen _screen;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _compactRestoreTimer;
    private readonly DispatcherTimer _memoryTrimTimer;
    private readonly EventHandler _onThemeChanged;      // 鍏峰悕澶勭悊鍣細绐楀彛鍏抽棴鏃跺彲閫€璁紝闃叉硠婕?
    private readonly EventHandler<AppSettings> _onSettingsChanged;
    private NotifyCollectionChangedEventHandler? _historyChangedHandler;
    private bool _waveRendering;                  // 娉㈢汗娓叉煋涓紙宸叉寕鎺ュ悎鎴愬抚浜嬩欢锛?
    private DispatcherTimer? _waveTimer;                  // 浣庡姛鑰楁ā寮忥細娉㈢汗瀹氭椂鍣紙~120fps锛?
    private double _lastWaveTime;                 // 涓婁竴甯ф椂闂达紙绉掞級锛岀敤浜庡抚鐜囨棤鍏冲钩婊?
    private readonly System.Diagnostics.Stopwatch _waveClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly List<ScaleTransform> _waveBarsExpanded = new();
    private readonly List<ScaleTransform> _waveBarsCompact = new();
    // 澶囬€夋尝绾规牱寮忥紙棰戣氨/鐜舰/绮掑瓙锛?
    private readonly List<ScaleTransform> _waveSpectrumExpanded = new();
    private readonly List<ScaleTransform> _waveSpectrumCompact = new();
    private ScaleTransform? _waveRingScaleExpanded;
    private ScaleTransform? _waveRingScaleCompact;
    private readonly List<TranslateTransform> _waveParticleTransformsExpanded = new();
    private readonly List<TranslateTransform> _waveParticleTransformsCompact = new();
    private Storyboard? _currentStoryboard;
    private Storyboard? _glassAnimSb;               // 鐜荤拑鍒嗗眰涓嶉€忔槑搴﹀姩鐢伙紙鍙殢鏃堕噸寮€/鍋滄锛?
    /// <summary>灞曞紑鎬佺幓鐠冨彔鍔犵洰鏍囦笉閫忔槑搴︼細浠庡熀纭€ 88% 鍙犲姞鍒?鈮?7%锛堥殢鐢ㄦ埛 Opacity 缂╂斁锛夈€?/summary>
    private double GlassTargetOpacity
    {
        get
        {
            var op = Math.Clamp(_theme.Opacity, 0.3, 1.0);
            var denom = 1.0 - 0.88 * op;
            if (denom < 0.05) return 1.0;
            return Math.Clamp(0.09 * op / denom, 0, 1);
        }
    }

    /// <summary>灞曞紑鍐呭浜ら敊杩囨浮鍖哄潡锛堣嚜涓婅€屼笅锛夛細涓婂矝鎺ㄩ€?/ Hero / 灏侀潰鏍囬 / 杩涘害 / 鎺у埗 / 姝岃瘝蹇嵎 / 姝岃瘝 / 蹇嵎鎿嶄綔銆?
    /// 1.2.1锛氬睍寮€鏃朵緷娆℃贰鍏ヤ笂绉汇€佹敹璧锋椂鍙嶅悜娣″嚭涓嬬Щ锛屼豢 iOS 鐏靛姩宀涢敊宄拌繘鍑恒€?/summary>
    private (FrameworkElement El, TranslateTransform Tr)[] _cascadeBlocks = Array.Empty<(FrameworkElement, TranslateTransform)>();

    /// <summary>涓哄睍寮€鍐呭鍚勫尯鍧楁寕鎺ヤ綅绉诲彉鎹紙渚涗氦閿欒繃娓″姩鐢讳娇鐢級銆?/summary>
    private static (FrameworkElement, TranslateTransform)[] BuildCascadeBlocks(params FrameworkElement?[] els)
    {
        var list = new List<(FrameworkElement, TranslateTransform)>(els.Length);
        foreach (var el in els)
        {
            if (el is null) continue;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            if (el.RenderTransform is not TranslateTransform tr)
            {
                tr = new TranslateTransform();
                el.RenderTransform = tr;
            }
            list.Add((el, tr));
        }
        return list.ToArray();
    }

    /// <summary>ReduceMotion / 鍏滃簳锛氱洿鎺ヨ缃墍鏈変氦閿欏尯鍧楃殑閫忔槑搴︿笌浣嶇Щ锛岃烦杩囧姩鐢汇€?/summary>
    private void ApplyCascadeState(double opacity, double y)
    {
        foreach (var (el, tr) in _cascadeBlocks)
        {
            el.Opacity = opacity;
            tr.Y = y;
        }
    }
    private Storyboard? _positionStoryboard;   // 浣嶇疆鍔ㄧ敾鐙崰锛氳繛缁噸瀹氫綅鍏堝仠鏃у姩鐢?
    private HwndSource? _hwndSource;
    private CoverFullScreenWindow? _coverFullWindow;   // #2 灏侀潰娌夋蹈锛氬叏灞忓皝闈㈤瑙堢獥鍙?

    // 鈹€鈹€ #8 鍔ㄦ€佷富棰橈細灏侀潰鍙栬壊鑳屾櫙缂撴參鍛煎惛锛?0fps 鍚堟垚甯ч┍鍔紝浠呭湪灞曞紑+鍙栬壊寮€鍚椂杩愯锛夆攢鈹€
    private System.Windows.Media.Color? _tintCoverColor;   // 宸查噰鏍风殑灏侀潰涓昏壊锛堝彉鍖栨椂閲嶅缓 brush锛?
    private LinearGradientBrush? _tintBrush;               // 灏侀潰鍙栬壊娓愬彉锛堢紦瀛橈紝閬垮厤姣忓抚閲嶅缓 GC锛?
    private GradientStop? _tintStop0;
    private GradientStop? _tintStop1;
    private DateTime _tintPhaseUtc;
    // 灏侀潰鍙栬壊缂撳瓨锛氶伩鍏嶅睍寮€/鏀惰捣鏃堕噸澶嶉噰鏍峰悓涓€灏侀潰
    private ImageSource? _lastSampledArtwork;
    private System.Windows.Media.Color? _lastSampledColor;                        // 鍛煎惛鐩镐綅璧风偣
    private bool _tintRenderingSubscribed;
    private static readonly CubicEase CachedCubicEaseOut = CreateCubicEase(EasingMode.EaseOut);
    private static readonly CubicEase CachedCubicEaseIn = CreateCubicEase(EasingMode.EaseIn);
    private static readonly SpringEase CachedSpringEase = FreezeEase(new SpringEase { Damping = 11, Stiffness = 220, Mass = 1 });
    private static readonly SoftSpringEase CachedSoftEase = FreezeEase(new SoftSpringEase { Damping = 15, Stiffness = 220, Mass = 1 });
    private static readonly SoftSpringEase CachedSoftEaseSmooth = FreezeEase(new SoftSpringEase { Damping = 18, Stiffness = 250, Mass = 1 });
    private static readonly ElasticEase CachedElasticEase = FreezeEase(new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut });

    private static T FreezeEase<T>(T ease) where T : Freezable
    {
        ease.Freeze();
        return ease;
    }

    private static CubicEase CreateCubicEase(EasingMode mode)
    {
        var ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }
    // 缂撳瓨涓婂矝鎺ㄩ€佺敾鍒凤紙閬垮厤姣忔灞炴€ц闂兘 new SolidColorBrush锛?
    private Brush? _cachedPushBg, _cachedPushBorder, _cachedPushFg, _cachedPushSecondary;
    private bool _pushDarkCache;
    // 缂撳瓨姝岃瘝鐢诲埛锛堥伩鍏嶆瘡娆¤闂兘 new SolidColorBrush锛?
    private Brush? _cachedExpLyricBase, _cachedExpLyricHL, _cachedCmpLyricBase, _cachedCmpLyricHL;
    private string? _cachedLyricBaseHex, _cachedLyricHLHex;
    private bool _lyricBrushDark;


    public System.Windows.Forms.Screen Screen { get; }

    public IslandWindow(IslandViewModel vm, ThemeService theme, SettingsService settings, System.Windows.Forms.Screen screen)
    {
        _vm = vm;
        _theme = theme;
        _settings = settings;
        _screen = screen;
        Screen = screen;

        DataContext = vm;
        InitializeComponent();

        // 灞曞紑鍐呭浜ら敊杩囨浮鍖哄潡锛堝姛鑳?2锛夛細涓哄悇鍖哄潡鎸傝浇浣嶇Щ鍙樻崲锛屼緵灞曞紑/鏀惰捣閿欏嘲鍔ㄧ敾浣跨敤
        _cascadeBlocks = BuildCascadeBlocks(ExpandedPushCard, HeroCard, ArtTitleGrid, ProgressGrid,
            ControlsGrid, LyricQuickOpsPanel, LyricsScroll, QuickActionsPanel);

        // 鏀惰捣寤惰繜锛堥紶鏍囩Щ鍑哄睍寮€鎬?700ms 鍚庢敹璧凤級
        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            _vm.IsExpanded = false;
        };

        // 鏀惰捣鍔ㄧ敾鍙兘琚揩閫熷垏鎹㈡墦鏂鑷?Card 灏哄娈嬬暀锛氬姩鐢荤粨鏉熷悗鍏滃簳鎭㈠绮剧‘绱у噾灏哄
        _compactRestoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _compactRestoreTimer.Tick += (_, _) =>
        {
            _compactRestoreTimer.Stop();
            _memoryTrimTimer.Stop();
            if (IsLoaded && !_vm.IsExpanded)
            {
                Card.BeginAnimation(FrameworkElement.WidthProperty, null);
                Card.BeginAnimation(FrameworkElement.HeightProperty, null);
                Card.Width = CompactWidth;
                Card.Height = CompactHeight;
                ContentGrid.RowDefinitions[1].Height = GridLength.Auto;
            }
        };

        _memoryTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _memoryTrimTimer.Tick += (_, _) =>
        {
            if (!_vm.IsPlaying && !_vm.IsExpanded) MemoryOptimizer.RequestTrim();
        };
        _memoryTrimTimer.Start();


        // 澹伴煶娉㈢汗锛氭寕鎺ュ悎鎴愬抚浜嬩欢锛屾寜鏄剧ず鍣ㄥ埛鏂扮巼椹卞姩锛岀┖闂叉椂鎽橀櫎涓嶅崰 CPU
        if (WaveBar1 is not null)
        {
            _waveBarsExpanded.AddRange(new[] { WaveBar1, WaveBar2, WaveBar3, WaveBar4, WaveBar5, WaveBar6, WaveBar7 });
            _waveBarsCompact.AddRange(new[] { WaveBarC1, WaveBarC2, WaveBarC3, WaveBarC4, WaveBarC5, WaveBarC6, WaveBarC7 });
        }
        InitWaveVisualStyles();

        // 鎮仠涓嶅睍寮€锛涚Щ鍑烘椂鑻ュ凡灞曞紑鍒欏欢杩熸敹璧?
        Card.MouseLeave += (_, _) =>
        {
            if (_vm.IsExpanded) _collapseTimer.Start();
        };

        // 鍙屽嚮妫€娴嬶細鍗曞嚮寤惰繜 280ms 鍚庡垏鎹㈠睍寮€/鏀惰捣锛涚獥鍙ｅ唴绗簩娆″崟鍑诲垯鎵ц蹇嵎鍔ㄤ綔
        _clickDebounce.Tick += (_, _) =>
        {
            _clickDebounce.Stop();
            if (!_pendingClick) return;
            _pendingClick = false;
            if (_toggleDoneOnDown)
            {
                // #7 鐐瑰嚮鎶㈠厛锛歁ouseDown 宸茬珛鍗冲垏鎹紝杩欓噷鍙槸绛夊緟鍙屽嚮绐楀彛锛屼笉鍐嶉噸澶嶅垏鎹?
                _toggleDoneOnDown = false;
                return;
            }
            _collapseTimer.Stop();
            _vm.IsExpanded = !_vm.IsExpanded;
        };

        // 鐐瑰嚮灞曞紑/鏀惰捣锛涜В閿佺姸鎬佷笅鏀寔榧犳爣鎷栧姩
        Card.PreviewMouseLeftButtonDown += OnCardMouseLeftButtonDown;
        Card.PreviewMouseMove += OnCardMouseMove;
        Card.PreviewMouseLeftButtonUp += OnCardMouseLeftButtonUp;
        Card.PreviewMouseUp += OnCardMiddleMouseUp;   // 涓敭蹇嵎鎿嶄綔

        // 杩涘害鏉℃嫋鎷?seek
        ProgressSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _vm.BeginSeek()));
        ProgressSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, _) => await _vm.EndSeekAsync(ProgressSlider.Value)));

        _onThemeChanged = (_, _) => ApplyTheme();
        _onSettingsChanged = (_, _) =>
        {
            ApplyExpandedSectionVisibility();
            ApplyAppearance();
            RefreshWave();
            ApplyCoverTint();
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MarqueeEnabled)));
        };
        _vm.PropertyChanged += OnVmPropertyChanged;
        _theme.ThemeChanged += _onThemeChanged;
        _settings.Changed += _onSettingsChanged;
        _historyChangedHandler = (_, _) => RefreshNotificationHistoryProps();
        _vm.NotificationHistory.CollectionChanged += _historyChangedHandler;
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshNotificationHistoryProps();

        Loaded += OnLoaded;
        // 瑙︽懜灞忎氦浜掞細婊戝姩鍒囨瓕銆佺偣鍑诲睍寮€/鏀惰捣
        TouchDown += OnTouchDown;
        TouchUp += OnTouchUp;
        ManipulationStarted += OnManipulationStarted;
        ManipulationCompleted += OnManipulationCompleted;
        ManipulationDelta += OnManipulationDelta;
        // 瑙︽懜婊氬姩锛氬睍寮€鍚庡彲鐢ㄦ墜鎸囨粴鍔ㄦ瓕璇?
        PreviewTouchDown += (_, _) => { /* 纭繚瑙︽懜浜嬩欢涓嶈瀛愬厓绱犲悶鎺?*/ };
        DpiChanged += (_, _) =>
        {
            Reposition();
            ApplySize(); // DPI 鍙樺寲鏃堕噸鏂拌绠楀崱鐗囧昂瀵革紙澶氭樉绀哄櫒涓嶅悓缂╂斁姣斿満鏅級
            ApplyAppearance(); // 鍒锋柊澶栬纭繚瀛椾綋娓叉煋姝ｇ‘
        };
        Closed += OnWindowClosed; // 鍏抽棴鏃堕€€璁㈠閮ㄤ簨浠舵簮锛岄伩鍏?RecreateWindows 閲嶅缓鍚庝簨浠舵硠婕?
    }

    /// <summary>绐楀彛鍏抽棴锛氶€€璁㈠閮ㄤ簨浠跺苟鍋滄鏈獥鍙ｅ畾鏃跺櫒 / 娓叉煋寰幆锛岄槻姝㈠唴瀛樹笌 CPU 娉勬紡銆?/summary>
    // 鈹€鈹€ 瑙︽懜灞忎氦浜?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
    private DateTime _touchStartTime;
    private Point _touchStartPoint;
    private bool _touchManipulating;

    private void OnTouchDown(object sender, TouchEventArgs e)
    {
        try
        {
            var tp = e.GetTouchPoint(this);
            _touchStartTime = DateTime.Now;
            _touchStartPoint = tp.Position;
            _touchManipulating = false;
            // 鍚敤鎿嶄綔锛氬厑璁告粦鍔ㄦ墜鍔?
            if (_vm.IsExpanded)
            {
                // 灞曞紑鐘舵€佷笅鍏佽瑙︽懜婊氬姩
                e.Handled = false;
            }
        }
        catch { }
    }

    private void OnTouchUp(object sender, TouchEventArgs e)
    {
        try
        {
            if (_touchManipulating) return; // 婊戝姩鎵嬪娍宸插鐞嗭紝涓嶅啀瑙﹀彂鐐瑰嚮
            var tp = e.GetTouchPoint(this);
            var delta = tp.Position - _touchStartPoint;
            var duration = DateTime.Now - _touchStartTime;
            // 鐭椂闂淬€佸皬浣嶇Щ = 鐐瑰嚮锛堝睍寮€/鏀惰捣锛?
            if (duration < TimeSpan.FromMilliseconds(300) && Math.Abs(delta.X) < 20 && Math.Abs(delta.Y) < 20)
            {
                _vm.IsExpanded = !_vm.IsExpanded;
            }
        }
        catch { }
    }

    private void OnManipulationStarted(object sender, ManipulationStartedEventArgs e)
    {
        _touchManipulating = true;
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        // 灞曞紑鐘舵€佷笅鍏佽瑙︽懜鍨傜洿婊氬姩姝岃瘝
        if (_vm.IsExpanded)
        {
            e.Handled = false; // 璁╁唴閮?ScrollViewer 澶勭悊
        }
    }

    private void OnManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
    {
        try
        {
            // 姘村钩婊戝姩鎵嬪娍锛氬乏婊?= 涓嬩竴棣栵紝鍙虫粦 = 涓婁竴棣?
            var totalX = e.TotalManipulation.Translation.X;
            var totalY = e.TotalManipulation.Translation.Y;
            if (Math.Abs(totalX) > Math.Abs(totalY) && Math.Abs(totalX) > 50)
            {
                if (totalX < 0)
                    _vm.NextCommand.Execute(null);
                else
                    _vm.PreviousCommand.Execute(null);
                e.Handled = true;
            }
        }
        catch { }
        finally
        {
            _touchManipulating = false;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _glassAnimSb?.Stop();
        try
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _theme.ThemeChanged -= _onThemeChanged;
            _settings.Changed -= _onSettingsChanged;
            if (_historyChangedHandler is not null)
                _vm.NotificationHistory.CollectionChanged -= _historyChangedHandler;
            Localization.LanguageChanged -= OnLanguageChanged;
            _collapseTimer.Stop();
            _compactRestoreTimer.Stop();
            _memoryTrimTimer.Stop();
            StopLyricsScroll();
            CancelPendingClick();
            StopWaveRender();
            SubscribeTintRendering(false); // 鏄惧紡閫€璁㈠皝闈㈠彇鑹插悎鎴愬抚锛岄槻绐楀彛閿€姣佸悗浜嬩欢娉勬紡
            // 鍏抽棴鍙兘瀛樺湪鐨勫叏灞忓皝闈㈤瑙堢獥鍙ｏ紝閬垮厤瀛ゅ効绐楀彛
            if (_coverFullWindow is { } cfw)
            {
                try { cfw.Close(); } catch { /* ignore */ }
                _coverFullWindow = null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Island window cleanup failed", ex);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Theme passthrough for XAML bindings.
    public Brush TextPrimary => _theme.TextPrimary;
    public Brush TextSecondary => _theme.TextSecondary;
    public Brush AccentBrush => _theme.AccentBrush;
    public Brush AccentBorderBrush => _theme.AccentBorderBrush;
    public Brush CardBackground => _theme.CardBackground;
    public Brush CardBorder => _theme.CardBorder;
    public Brush ButtonHoverBrush => _theme.ButtonHoverBrush;
    public Brush SliderTrackBrush => _theme.SliderTrackBrush;
    public Brush SliderThumbBrush => _theme.SliderThumbBrush;

    // Lyric style passthrough (1.2.3): adjustable lyric font size, current-line size,
    // line spacing, karaoke speed and highlight/base colors.
    public double LyricBaseFontSize => Math.Max(9, _settings.Current.LyricFontSize);
    public double LyricCurrentFontSize => Math.Max(12, Math.Max(LyricBaseFontSize + 3, _settings.Current.LyricCurrentFontSize));
    public double LyricLineHeight
    {
        get
        {
            var s = Math.Clamp(_settings.Current.LyricLineSpacing, 0.5, 2.5);
            return Math.Max(16, LyricBaseFontSize * 1.55 * s);
        }
    }
    public Thickness LyricLineMargin
    {
        get
        {
            var s = Math.Clamp(_settings.Current.LyricLineSpacing, 0.5, 2.5);
            return new Thickness(0, 2.5 * s, 0, 2.5 * s);
        }
    }
    public double LyricKaraokeSpeed => Math.Clamp(_settings.Current.KaraokeSpeed, 0.2, 3.0);

    private static System.Windows.Media.Color BrushColor(Brush b) => (b as SolidColorBrush)?.Color ?? System.Windows.Media.Colors.White;
    private static Brush FreezeBrush(System.Windows.Media.Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static System.Windows.Media.Color ParseHexColor(string? hex, System.Windows.Media.Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex.Trim()); }
            catch { /* invalid hex -> fallback */ }
        }
        return fallback;
    }
    public Brush ExpandedLyricBaseBrush
    {
        get
        {
            var dark = _theme.IsDark;
            var hex = _settings.Current.LyricBaseColor ?? "";
            if (_cachedExpLyricBase is null || _lyricBrushDark != dark || _cachedLyricBaseHex != hex)
            {
                _lyricBrushDark = dark;
                _cachedLyricBaseHex = hex;
                _cachedExpLyricBase = FreezeBrush(ParseHexColor(hex, BrushColor(_theme.TextSecondary)));
            }
            return _cachedExpLyricBase;
        }
    }
    public Brush ExpandedLyricHighlightBrush
    {
        get
        {
            var dark = _theme.IsDark;
            var hex = _settings.Current.LyricHighlightColor ?? "";
            if (_cachedExpLyricHL is null || _lyricBrushDark != dark || _cachedLyricHLHex != hex)
            {
                _cachedLyricHLHex = hex;
                _cachedExpLyricHL = FreezeBrush(ParseHexColor(hex, BrushColor(_theme.TextPrimary)));
            }
            return _cachedExpLyricHL;
        }
    }
    public Brush CompactLyricBaseBrush
    {
        get
        {
            var dark = _theme.IsDark;
            if (_cachedCmpLyricBase is null || _lyricBrushDark != dark)
            {
                var bc = BrushColor(_theme.TextSecondary);
                _cachedCmpLyricBase = FreezeBrush(System.Windows.Media.Color.FromArgb(96, bc.R, bc.G, bc.B));
            }
            return _cachedCmpLyricBase;
        }
    }
    public Brush CompactLyricHighlightBrush
    {
        get
        {
            var dark = _theme.IsDark;
            if (_cachedCmpLyricHL is null || _lyricBrushDark != dark)
                _cachedCmpLyricHL = FreezeBrush(BrushColor(_theme.TextPrimary));
            return _cachedCmpLyricHL;
        }
    }

    public bool NotificationHistoryVisible => _settings.Current.NotificationHistoryEnabled && _vm.NotificationHistory.Count > 0;
    public string NotificationHistoryTitle => Localization.Get("Notifications_History");
    public string NotificationHistoryClearText => Localization.Get("Notifications_HistoryClear");


    // 鈹€鈹€ 涓婂矝鎺ㄩ€佸崱鐗囦富棰橈紙#17锛氱涓夋柟鍙寚瀹?dark / light锛宎uto 璺熼殢搴旂敤鏄庢殫锛夆攢鈹€
    /// <summary>鎺ㄩ€佸崱鐗囨槸鍚︽寜娣辫壊娓叉煋锛坅uto 璺熼殢搴旂敤涓婚锛夈€?/summary>
    private bool PushDark() => _vm.ActivePushTheme?.Trim().ToLowerInvariant() switch
    {
        "dark" => true,
        "light" => false,
        _ => _theme.IsDark,
    };

    public Brush PushCardBackground
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushBg is null || _pushDarkCache != dark)
            {
                _pushDarkCache = dark;
                _cachedPushBg = FreezeBrush(dark ? System.Windows.Media.Color.FromArgb(0xE6, 0x1B, 0x1B, 0x26) : System.Windows.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            }
            return _cachedPushBg;
        }
    }
    public Brush PushCardBorder
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushBorder is null || _pushDarkCache != dark)
            {
                _cachedPushBorder = FreezeBrush(dark ? System.Windows.Media.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : System.Windows.Media.Color.FromArgb(0x40, 0x00, 0x00, 0x00));
            }
            return _cachedPushBorder;
        }
    }
    public Brush PushCardForeground
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushFg is null || _pushDarkCache != dark)
            {
                _cachedPushFg = FreezeBrush(dark ? System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF7) : System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x2A));
            }
            return _cachedPushFg;
        }
    }
    public Brush PushCardSecondary
    {
        get
        {
            var dark = PushDark();
            if (_cachedPushSecondary is null || _pushDarkCache != dark)
            {
                _cachedPushSecondary = FreezeBrush(dark ? System.Windows.Media.Color.FromArgb(0xCC, 0xC8, 0xC8, 0xD4) : System.Windows.Media.Color.FromArgb(0xCC, 0x55, 0x55, 0x60));
            }
            return _cachedPushSecondary;
        }
    }

    /// <summary>涓婂矝鎺ㄩ€佷富棰樺彉鍖栨椂鍒锋柊鎺ㄩ€佸崱鐗囩敾鍒枫€?/summary>
    private void RaisePushThemeProps()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardBackground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardBorder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardForeground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushCardSecondary)));
    }

    // 鈹€鈹€ 灞曞紑鍗＄墖鍒嗗尯鍧楀紑鍏筹紙鏉ヨ嚜璁剧疆锛岀粦瀹氬埌灞曞紑鍐呭锛夆攢鈹€
    // 姝屾洸鐩稿叧鍖哄煙浠呭湪鈥滄湁濯掍綋鎾斁鈥濇椂鏄剧ず锛涘彧鏈変笂宀涙帹閫佹椂灞曞紑鎬佷互涓婂矝鍐呭涓轰富锛岄伩鍏嶇┖姝屾洸鍖?
    public bool ExpandedShowArtTitle => _vm.HasMedia && _settings.Current.ExpandedShowArtTitle
        && _settings.Current.ExpandedCardStyle != "Hero"; // Hero 澶у崱鐗囨ā鏉夸笅闅愯棌缁忓吀灏忓皝闈㈠尯
    /// <summary>濯掍綋澶у崱鐗囨ā鏉匡紙Hero锛夛細澶у皝闈㈣儗鏅?+ 姝屽悕/姝屾墜/涓撹緫鍙犲姞銆?/summary>
    public bool ExpandedHeroCard => _vm.HasMedia && _settings.Current.ExpandedCardStyle == "Hero";

    public bool ExpandedShowProgress => _vm.HasMedia && _settings.Current.ExpandedShowProgress;
    public bool ExpandedShowControls => _vm.HasMedia && _settings.Current.ExpandedShowControls;
    public bool ExpandedShowLyrics => _vm.HasMedia && _settings.Current.ExpandedShowLyrics;

    /// <summary>澶氬獟浣撴潵婧愰€夋嫨鍣ㄥ彲瑙佹€э紙#3锛氭湁濯掍綋涓斿涓細璇濆苟瀛樻椂鏄剧ず锛夈€?/summary>
    public bool MediaSessionPickerVisible => _vm.HasMedia && _vm.HasMultipleSessions;
    /// <summary>姝岃瘝鏉ユ簮涓€閿垏鎹㈡寜閽彲瑙佹€э紙璁剧疆涓紑鍚€屾瓕璇嶆潵婧愬垏鎹€嶅悗鏄剧ず锛屼究浜庡揩閫熸崲婧愶級銆?/summary>
    public bool LyricSourcePickVisible => _settings.Current.LyricsSourcePick;

    // 鈹€鈹€ 鍗曡妯″紡锛氱揣鍑戞€佹墍鏈夌粍浠朵竴琛屾樉绀?鈹€鈹€
    public bool SingleLineMode => _settings.Current.SingleLineMode;
    /// <summary>璺戦┈鐏紑鍏筹紙姝屽悕/姝岃瘝瓒呭鏃舵í鍚戞粴鍔級銆?/summary>
    public bool MarqueeEnabled => _settings.Current.MarqueeTextEnabled;
    // 澹伴煶娉㈢汗锛氭挱鏀句腑 + 寮€鍚尝绾硅缃?+ 宀涘彲瑙佹墠鏄剧ず锛堢┖闂叉椂鍋滄璁℃椂鍣級
    public bool HasWave => _vm.IsVisible && _vm.HasMedia && _vm.IsPlaying && _settings.Current.WaveVisualizerEnabled;

    // 涓婂矝鎺ㄩ€佸唴瀹癸細鍗曡妯″紡涓嬪彧鏄剧ず鍥炬爣+鏍囬锛堥殣钘忔鏂?杩涘害/鎸夐挳锛?
    public bool PushShowBody => _vm.ActivePushHasBody && !SingleLineMode;
    public bool PushShowProgress => _vm.ActivePushHasProgress && !SingleLineMode;
    public bool PushShowButtons => _vm.ActivePushHasButtons && !SingleLineMode;
    public bool PushShowInput => _vm.HasPushInput && !SingleLineMode;

    // 鈹€鈹€ 鐐瑰嚮灞曞紑 / 瑙ｉ攣鎷栧姩 / 鍙抽敭鑿滃崟 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private Point _downPoint;
    private bool _mouseDownOnCard;
    private bool _draggedCard;
    private readonly DispatcherTimer _clickDebounce = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private bool _pendingClick;
    private bool _toggleDoneOnDown;   // #7 鐐瑰嚮鎶㈠厛锛歁ouseDown 宸插垏鎹紝鍙屽嚮绐楀彛鍒版湡鍚庝笉鍐嶉噸澶嶅垏鎹?
    private bool _isExpandedBeforeToggle; // #7 淇锛氭嫋鍔ㄥ紑濮嬫椂杩樺師鎸変笅鏃跺凡鍒囨崲鐨勫睍寮€鐘舵€?
    private Point _lastClickUp;

    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownOnCard = true;
        _draggedCard = false;
        _downPoint = e.GetPosition(this);

        // #7 鐐瑰嚮鎶㈠厛锛圓鏂规锛夛細鎸変笅绔嬪嵆鍒囨崲灞曞紑/鏀惰捣锛屼笉绛?280ms 鍙屽嚮绐楀彛锛屾墜鎰熻窡鎵嬨€?
        // 浜や簰鍏冪礌锛堟寜閽?婊戝潡锛屾寜閽嚜宸卞鐞嗙偣鍑伙級銆佷笂宀涙帹閫佹暣鍗″洖璺炽€佸皝闈㈡矇娴稿ぇ鍥惧悇鑷鐞嗭紝涓嶅湪姝ゅ垏鎹€?
        if (!IsInteractiveElement(e.OriginalSource) && !IsWithinPushCard(e.OriginalSource) && !IsCoverElement(e.OriginalSource))
        {
            _toggleDoneOnDown = true;
            _isExpandedBeforeToggle = _vm.IsExpanded;
            _collapseTimer.Stop();
            _vm.IsExpanded = !_vm.IsExpanded;
        }
    }

    private void OnCardMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_mouseDownOnCard || e.LeftButton != MouseButtonState.Pressed) return;
        if (_fileDragArmed) return; // 鏂囦欢涓浆绔欑粍浠讹細鎷栧姩鐢辩粍浠惰嚜宸辩殑鎷栧嚭閫昏緫澶勭悊
        if (_settings.Current.IsLocked) return; // 涓婇攣涓嶅彲鎷栧姩

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _downPoint.X) > 4 || Math.Abs(pos.Y - _downPoint.Y) > 4)
        {
            // #7 淇锛氳В閿佹嫋鍔ㄦ椂鎸変笅宸茬珛鍗冲垏鎹㈠睍寮€锛岃繖閲岃繕鍘燂紝閬垮厤銆屾兂鎷栧姩鍗村睍寮€銆?
            if (_toggleDoneOnDown)
            {
                _toggleDoneOnDown = false;
                _vm.IsExpanded = _isExpandedBeforeToggle;
            }
            _mouseDownOnCard = false;
            _draggedCard = true;
            CancelPendingClick();
            _collapseTimer.Stop();
            try { DragMove(); } catch { /* ignore */ }
            e.Handled = true;
        }
    }

    private void OnCardMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedCard)
        {
            _draggedCard = false;
            CancelPendingClick();
            SnapAndPersistPosition();
            return;
        }
        if (!_mouseDownOnCard) return;
        _mouseDownOnCard = false;

        // 鐐瑰嚮鎸夐挳/婊戝潡涓嶈Е鍙戝睍寮€鍒囨崲锛堟寜閽嚜宸卞鐞嗙偣鍑伙級
        if (IsInteractiveElement(e.OriginalSource)) return;

        // #2 灏侀潰娌夋蹈锛氱偣鍑诲睍寮€鎬佺殑澶у皝闈?澶у崱锛圔igArt/HeroCard锛夋墦寮€鍏ㄥ睆灏侀潰棰勮
        if (IsCoverElement(e.OriginalSource))
        {
            CancelPendingClick();
            OpenCoverFullScreen();
            e.Handled = true;
            return;
        }

        // 涓婂矝鎺ㄩ€佹暣鍗＄偣鍑诲洖璺筹細鐐瑰湪鎺ㄩ€佸崱鐗囦笂涓旈厤缃簡 click 鏃讹紝鎵ц鍥炶烦鑰屼笉灞曞紑
        if (_vm.ActivePushHasClick && IsWithinPushCard(e.OriginalSource))
        {
            CancelPendingClick();
            _vm.ExecutePushClick();
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(this);
        // 鍙屽嚮锛氫笌涓婁竴娆″崟鍑昏窛绂荤浉杩戜笖鍦ㄧ獥鍙ｆ湡鍐?鈫?鎵ц蹇嵎鍔ㄤ綔
        if (_pendingClick && _clickDebounce.IsEnabled &&
            Math.Abs(pos.X - _lastClickUp.X) < 24 && Math.Abs(pos.Y - _lastClickUp.Y) < 24)
        {
            CancelPendingClick();
            ExecuteDoubleClickAction();
            e.Handled = true;
            return;
        }

        // 鍗曞嚮锛氭寕璧凤紝绛夊緟鍙屽嚮绐楀彛瓒呮椂鍚庡啀鍒囨崲灞曞紑/鏀惰捣
        _pendingClick = true;
        _lastClickUp = pos;
        _clickDebounce.Stop();
        _clickDebounce.Start();
        e.Handled = true;
    }

    /// <summary>涓敭鍗曞嚮锛氭墽琛岃缃?閫氱敤涓厤缃殑涓敭蹇嵎鍔ㄤ綔锛堥粯璁ゆ挱鏀?鏆傚仠锛夈€?/summary>
    private void OnCardMiddleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        CancelPendingClick();
        ExecuteQuickAction(_settings.Current.MiddleClickAction);
    }

    private void CancelPendingClick()
    {
        _pendingClick = false;
        _clickDebounce.Stop();
        _toggleDoneOnDown = false;
    }

    /// <summary>鍙屽嚮蹇嵎鍔ㄤ綔锛堝湪璁剧疆-閫氱敤涓厤缃級锛氭挱鏀?鏆傚仠銆佸睍寮€/鏀惰捣銆佹樉绀烘闈€侀殣钘?鏄剧ず銆佸垏姝屻€佹墦寮€璁剧疆鎴栨棤鍔ㄤ綔銆?/summary>
    private void ExecuteDoubleClickAction() => ExecuteQuickAction(_settings.Current.DoubleClickAction);

    /// <summary>涓敭蹇嵎鍔ㄤ綔锛堝湪璁剧疆-閫氱敤涓厤缃紝涓庡弻鍑诲姩浣滃悓鍊煎煙锛夈€?/summary>
    private void ExecuteMiddleClickAction() => ExecuteQuickAction(_settings.Current.MiddleClickAction);

    /// <summary>鎸夊姩浣滃悕鎵ц蹇嵎鎿嶄綔锛涙湭鐭ュ姩浣滃洖閫€涓烘挱鏀?鏆傚仠銆?/summary>
    private void ExecuteQuickAction(string action)
    {
        switch (action)
        {
            case "OpenSettings":
                _vm.OpenSettingsCommand.Execute(null);
                break;
            case "ToggleExpand":
                _collapseTimer.Stop();
                _vm.IsExpanded = !_vm.IsExpanded;
                break;
            case "ShowDesktop":
                Services.SystemShell.ShowDesktop();
                break;
            case "ToggleVisible":
                _vm.ToggleUserVisible();
                break;
            case "NextTrack":
                if (_vm.CanNext) _vm.NextCommand.Execute(null);
                break;
            case "PrevTrack":
                if (_vm.CanPrevious) _vm.PreviousCommand.Execute(null);
                break;
            case "None":
                break;
            default: // PlayPause
                if (_vm.CanPlayPause)
                    _vm.PlayPauseCommand.Execute(null);
                break;
        }
    }

    /// <summary>鍒ゆ柇鐐瑰嚮婧愭槸鍚︿綅浜庡皝闈㈡矇娴稿厓绱狅紙灞曞紑澶у皝闈?BigArt / 濯掍綋澶у崱 HeroCard锛変笂銆?/summary>
    private static bool IsCoverElement(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is Border b && (b.Name == "BigArt" || b.Name == "HeroCard"))
                return true;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>#2 灏侀潰娌夋蹈锛氭墦寮€鍏ㄥ睆灏侀潰棰勮锛堝悓灞忔渶澶у寲锛岀偣鍑?Esc/鍙抽敭鍏抽棴锛夈€?/summary>
    private void OpenCoverFullScreen()
    {
        try
        {
            if (!_vm.HasMedia || _vm.Artwork is null) return;
            if (_coverFullWindow is { IsVisible: true })
            {
                _coverFullWindow.Close();
                return;
            }
            _coverFullWindow = new CoverFullScreenWindow(_vm.Artwork, _screen);
            _coverFullWindow.Closed += (_, _) => _coverFullWindow = null;
            _coverFullWindow.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Cover fullscreen failed: {ex.Message}");
        }
    }

    /// <summary>鍒ゆ柇鐐瑰嚮婧愭槸鍚︿綅浜庝笂宀涙帹閫佸崱鐗囧唴閮ㄣ€?/summary>
    private bool IsWithinPushCard(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (ReferenceEquals(d, CompactPushCard) || ReferenceEquals(d, ExpandedPushCard))
                return true;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static bool IsInteractiveElement(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or Slider
                or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton
                or System.Windows.Controls.TextBox)   // 涓婂矝杈撳叆妗嗭細鐐瑰嚮杈撳叆涓嶈Е鍙戝睍寮€/鏀惰捣
                return true;
            // 鏂囦欢涓浆绔欑粍浠讹細鏁翠釜缁勪欢瑙嗕负浜や簰鍏冪礌锛堢偣鍑讳笉灞曞紑銆佹嫋鍔ㄤ氦缁欐嫋鍑洪€昏緫锛?
            if (d is FrameworkElement { Tag: string tag } && tag == "FileTransfer") return true;
            // Run/Inline 绛?ContentElement 涓嶆槸 Visual锛孷isualTreeHelper.GetParent 浼氭姏寮傚父锛?
            // 闇€娌块€昏緫鏍戝悜涓婏紙姝岃瘝 Run 鈫?TextBlock锛夛紝鍒拌揪 UIElement 鍚庣户缁部瑙嗚鏍戙€?
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        MenuLock.Header = _settings.Current.IsLocked
            ? Localization.Get("Island_Unlock")
            : Localization.Get("Island_Lock");
        MenuOnlineLyrics.Header = Localization.Get("Island_OnlineLyrics");
        MenuOnlineLyrics.IsChecked = _settings.Current.OnlineLyricsEnabled;
    }

    /// <summary>鐐瑰嚮鐣寗閽熺粍浠讹細鏆傚仠/缁х画銆?/summary>
    private void TimerItem_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleTimerPause();
        e.Handled = true;
    }

    /// <summary>鐐瑰嚮杈撳叆娉曠粍浠讹細鍒囨崲涓?鑻辫緭鍏ユ硶銆?/summary>
    private void InputMethodItem_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleInputMethod();
        e.Handled = true;
    }

    /// <summary>鐐瑰嚮蹇嵎寮€鍏筹紙Button.Tag: wifi / bluetooth / night / mute锛夈€?/summary>
    private void QuickToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string which)
            _vm.ToggleQuickSwitch(which);
        e.Handled = true;
    }

    /// <summary>姝岃瘝缈昏瘧寮€鍏筹細鏄剧ず / 闅愯棌缈昏瘧琛屻€?/summary>
    private void LyricTranslate_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleLyricTranslation();
        e.Handled = true;
    }

    /// <summary>姝岃瘝鏉ユ簮涓€閿垏鎹紙澶氭瓕璇嶆簮锛夛細鐐瑰嚮寰幆 Auto 鈫?鏈湴 鈫?AMLL 鈫?Cider 鈫?鍦ㄧ嚎锛屽苟绔嬪嵆閲嶈浇姝岃瘝銆?/summary>
    private void LyricSourceSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.CycleLyricsSource();
        e.Handled = true;
    }

    /// <summary>澶嶅埗褰撳墠姝岃瘝鍙ュ埌鍓创鏉裤€?/summary>
    private void CopyCurrentLyric_Click(object sender, RoutedEventArgs e)
    {
        _vm.CopyCurrentLyric();
        e.Handled = true;
    }

    // #4 姝岃瘝鏃堕棿寰皟锛氭湰鏇叉瓕璇嶆彁鍓?/ 寤跺悗 0.5 绉掞紙绔嬪嵆鐢熸晥骞朵繚瀛橈級
    private void LyricOffsetDown_Click(object sender, RoutedEventArgs e)
    {
        _vm.AdjustLyricTime(-0.5);
        e.Handled = true;
    }

    private void LyricOffsetUp_Click(object sender, RoutedEventArgs e)
    {
        _vm.AdjustLyricTime(0.5);
        e.Handled = true;
    }


    /// <summary>澶氭挱鏀惧櫒鍒囨崲锛?3锛夛細鐐瑰嚮寰幆鍒囨崲鍒颁笅涓€涓彲鐢ㄥ獟浣撴潵婧愩€?/summary>
    private void MediaSessionCycle_Click(object sender, RoutedEventArgs e)
    {
        _vm.CycleMediaSession();
        e.Handled = true;
    }

    /// <summary>蹇嵎鎿嶄綔鎸夐挳鐐瑰嚮锛氭寜 Tag锛堟搷浣滈敭锛夋墽琛屽搴旂郴缁熷姩浣溿€?/summary>
    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key) _vm.ExecuteQuickAction(key);
    }

    /// <summary>涓婂矝鎺ㄩ€佹寜閽偣鍑伙細鎵ц鍔ㄤ綔锛堟墦寮€ URL / 鍚姩绋嬪簭锛夊悗鍏抽棴褰撳墠鎺ㄩ€併€?/summary>
    private void PushButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IslandPushButton button)
        {
            _vm.ExecutePushAction(button);
        }
        e.Handled = true;
    }

    /// <summary>涓婂矝杈撳叆妗嗘彁浜わ細鎶婄敤鎴疯緭鍏ユ寜鎺ㄩ€佹柟閰嶇疆鐨勫姩浣滄墽琛岋紙榛樿 notify 鍥炰紶锛夈€?/summary>
    private void PushInputSubmit_Click(object sender, RoutedEventArgs e)
    {
        // 鎻愪氦杈撳叆鎵ц鎺ㄩ€佸姩浣滐紙榛樿 notify 鍥炰紶锛夛紝闅忓悗鍏抽棴褰撳墠鎺ㄩ€佸崱鐗?
        _vm.SubmitPushInput();
        _vm.DismissActivePush();
        e.Handled = true;
    }

    private void MenuOnlineLyrics_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.OnlineLyricsEnabled = !s.OnlineLyricsEnabled);
        _ = _vm.RefreshLyricsAsync();
        Card_ContextMenuOpening(sender, null!);
    }

    private void MenuCenterAlign_Click(object sender, RoutedEventArgs e)
    {
        // 涓婁笅涓嶅彉锛屽乏鍙冲眳涓紱灞呬腑鍚庣殑浣嶇疆鎸佷箙鍖栵紙鎷栧姩杩囧啀灞呬腑瀵归綈鍚屾牱鐢熸晥锛?
        var work = ScreenHelper.DpiWorkArea(_screen);
        var cardPos = Card.TransformToAncestor(this).Transform(new Point(0, 0));
        var cardCenterInWindow = cardPos.X + Card.ActualWidth / 2;
        var left = work.Left + work.Width / 2 - cardCenterInWindow;
        _settings.Update(s =>
        {
            s.IslandManualLeft = left;
            s.IslandManualTop ??= Top;
        });
        AnimatePosition(left, Top);
    }

    private void MenuLock_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.IsLocked = !s.IsLocked);
        Card_ContextMenuOpening(sender, null!);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 鐐瑰嚮绌块€忥細鍗＄墖澶栬繑鍥?HTTRANSPARENT
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        ApplyTheme();
        ApplySize();
        ApplyCardAlignment();
        Reposition();
        if (_vm.IsVisible) ShowIsland(instant: true);
        else Hide();
    }

    /// <summary>鍒锋柊鐜荤拑鍒嗗眰搴曡壊涓哄綋鍓嶄富棰樺簳鑹诧紙鍐荤粨缂撳瓨锛岄伩鍏嶆瘡甯ч噸寤猴級銆?/summary>
    private void ApplyGlassLayer()
    {
        if (GlassLayer is null) return;
        var b = new SolidColorBrush(_theme.TintColor);
        b.Freeze();
        GlassLayer.Background = b;
    }

    /// <summary>鏅鸿兘閫忔槑搴﹀垎灞傦細灞曞紑鏃剁幓鐠冨眰骞虫粦鍗囦笉閫忔槑搴︼紙鍗＄墖鏇村疄锛夛紝鏀惰捣鍥炶惤锛堟洿閫氶€忥級锛?
    /// 灏侀潰鍙栬壊鐢熸晥鏃剁幓鐠冨綊闆讹紝閬垮厤鍙岄噸鍙犲姞銆傚姩鐢绘椂闀胯窡闅忓綋鍓嶅姩鏁堢毊鑲わ紝杩炶疮涓嶇敓纭€?/summary>
    private void AnimateGlass(bool expanded)
    {
        if (GlassLayer is null) return;
        var tintActive = expanded && _settings.Current.CoverTintBackground && _vm.Artwork != null;
        var target = tintActive ? 0 : (expanded ? GlassTargetOpacity : 0);
        _glassAnimSb?.Stop();
        _glassAnimSb = null;
        if (_settings.Current.ReduceMotion)
        {
            GlassLayer.Opacity = target;
            return;
        }
        var (styleEase, styleMs) = GetSizeAnimationStyle(expanded);
        var dur = (int)Math.Clamp(styleMs * 0.72, 200, 900);
        var sb = new Storyboard();
        AddAnim(sb, GlassLayer, UIElement.OpacityProperty, target, dur, styleEase);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps锛堣窡闅忔樉绀哄櫒鍒锋柊鐜囷級
        _glassAnimSb = sb;
        sb.Begin();
    }

    /// <summary>涓婚鍒囨崲骞虫粦杩囨浮锛?.2.1锛夛細鏄庢殫/涓婚鑹插彉鍖栨椂锛屽崱鐗囪儗鏅笌杈规鍋?EaseOut 棰滆壊鎻掑€硷紝
    /// 閬垮厤娣辨祬鑹插垏鎹㈤棯鍙樸€傚皝闈㈠彇鑹茬敓鏁堟椂鑳屾櫙鐢卞彇鑹叉笎鍙樻帴绠★紙宸叉湁鍛煎惛鍔ㄧ敾锛夛紝璺宠繃鑳屾櫙鍙姩鐢昏竟妗嗭紱
    /// ReduceMotion / 鏈姞杞芥椂鐩存帴鍒囨柊涓婚銆傛椂闀胯窡闅忓綋鍓嶅姩鏁堢毊鑲わ紝涓庡叾浠栧姩鐢昏妭濂忎竴鑷淬€?/summary>
    private void AnimateThemeColors(System.Windows.Media.Color? prevBg, System.Windows.Media.Color? prevBd)
    {
        try
        {
            var tintActive = _settings.Current.CoverTintBackground && _vm.IsExpanded && _vm.Artwork != null;
            if (!IsLoaded || _settings.Current.ReduceMotion)
            {
                if (!tintActive) Card.Background = _theme.CardBackground;
                Card.BorderBrush = _theme.CardBorder;
                return;
            }
            var (ease, ms) = GetSizeAnimationStyle(_vm.IsExpanded);
            var dur = TimeSpan.FromMilliseconds(Math.Clamp(ms * 0.34, 180, 460));
            if (!tintActive && prevBg is System.Windows.Media.Color pb && _theme.CardBackground is SolidColorBrush nb)
                AnimateSolidBrush(Card, Border.BackgroundProperty, pb, nb.Color, dur, ease);
            if (prevBd is System.Windows.Media.Color pbd && _theme.CardBorder is SolidColorBrush nbd)
                AnimateSolidBrush(Card, Border.BorderBrushProperty, pbd, nbd.Color, dur, ease);
        }
        catch
        {
            // 鎻掑€煎姩鐢诲紓甯告椂鐩存帴搴旂敤鏂颁富棰橈紝缁濅笉褰卞搷涓绘祦绋?
            if (!(_settings.Current.CoverTintBackground && _vm.IsExpanded && _vm.Artwork != null))
                Card.Background = _theme.CardBackground;
            Card.BorderBrush = _theme.CardBorder;
        }
    }

    /// <summary>鎶婃棫棰滆壊瀹夎鍒颁复鏃?brush 涓婂苟鎾斁鍒版柊棰滆壊鐨勬彃鍊煎姩鐢伙紙HoldEnd 淇濊壊锛屽璞＄敱鍔ㄧ敾鎸佹湁锛夈€?/summary>
    private void AnimateSolidBrush(DependencyObject target, DependencyProperty prop, System.Windows.Media.Color from, System.Windows.Media.Color to, TimeSpan dur, IEasingFunction ease)
    {
        var brush = new SolidColorBrush(from);
        target.SetValue(prop, brush);
        var anim = new ColorAnimation(to, dur) { EasingFunction = ease };
        AnimationFrameRate.Apply(anim, _settings.Current.LowPowerMode);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void ApplyTheme()
    {
        // 涓婚鍒囨崲骞虫粦杩囨浮锛?.2.1锛夛細鍏堣褰曞綋鍓嶅崱鐗囪儗鏅?杈规棰滆壊锛屼緵鎻掑€煎姩鐢讳娇鐢?
        var prevBg = (Card.Background as SolidColorBrush)?.Color;
        var prevBd = (Card.BorderBrush as SolidColorBrush)?.Color;
        ApplyMenuTheme();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextPrimary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextSecondary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackground)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonHoverBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SliderTrackBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SliderThumbBrush)));
        RaisePushThemeProps();
        ApplyAppearance();
        RefreshWave();
        ApplyCoverTint(forceRebuild: true); // 涓婚鍙樺寲鏃跺己鍒堕噸寤哄彇鑹叉笎鍙橈紙鍩鸿壊闅忔柊涓婚锛?
        AnimateThemeColors(prevBg, prevBd); // 鑳屾櫙/杈规棰滆壊鎻掑€艰繃娓★紝娣辨祬鑹插垏鎹笉闂彉
        ApplyGlassLayer();
        AnimateGlass(_vm.IsExpanded); // 涓婚/鏄庢殫鍒囨崲鍚庣幓鐠冨簳鑹蹭笌涓嶉€忔槑搴﹀悓姝ュ埛鏂?
    }

    /// <summary>灞曞紑鍗＄墖鍒嗗尯鍧楃殑鍙鎬ч殢璁剧疆鍗虫椂鍒锋柊銆?/summary>
    private void ApplyExpandedSectionVisibility()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowArtTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedHeroCard)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowControls)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedShowLyrics)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SingleLineMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowBody)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowButtons)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PushShowInput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaSessionPickerVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricSourcePickVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricBaseFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricCurrentFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricLineHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricLineMargin)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LyricKaraokeSpeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLyricBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedLyricHighlightBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLyricBaseBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLyricHighlightBrush)));
        RefreshNotificationHistoryProps();
    }

    /// <summary>鎸夎缃皟鏁寸獥鍙ｄ笌鍗＄墖灏哄锛堢揣鍑?灞曞紑锛夈€備粎褰撶獥鍙ｅ昂瀵哥湡姝ｅ彉鍖栨椂鎵嶉噸瀹氫綅锛?
    /// 閬垮厤涓婇攣/鍏跺畠璁剧疆鍙樻洿鎶婄敤鎴锋嫋鍔ㄥ悗鐨勪綅缃脊鍥為粯璁ゃ€?/summary>
    /// <summary>
    /// 纭繚閫忔槑绐楀彛灏哄瓒冲瀹圭撼褰撳墠鍗＄墖锛堝惈绱у噾鎬佽嚜鍔ㄥ搴︼級锛?
    /// 鍚﹀垯鍗＄墖瓒呭嚭绐楀彛杈圭晫浼氳瑁佸壀銆傜揣鍑戝崱鐗囪瑙夊搴?= CompactWidth 脳 FontScale銆?
    /// </summary>
    private void EnsureWindowSizeFits()
    {
        if (!IsLoaded) return;
        var settingExpanded = Math.Clamp(_settings.Current.ExpandedWidth, 300, 620);
        var settingMaxH = Math.Clamp(_settings.Current.MaxExpandedHeight, 240, 620);
        var w = Math.Max(settingExpanded, Math.Max(CompactWidth * FontScale + 8, 640)) + 24;
        var h = Math.Max(settingMaxH, 220) + 24;
        if (Math.Abs(Width - w) > 0.5 || Math.Abs(Height - h) > 0.5)
        {
            Width = w;
            Height = h;
            Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    public void ApplySize()
    {
        // 绐楀彛鍥哄畾涓鸿兘瀹圭撼鏈€澶ф帹閫佸崱鐗?灞曞紑鍐呭鐨勫ぇ灏忥細鎺ㄩ€佹椂鍙渶鍔ㄧ敾 Card 褰㈠彉锛岄伩鍏嶇獥鍙ｇ骇 Resize 鍗￠】
        EnsureWindowSizeFits();
        if (!_vm.IsExpanded)
        {
            Card.Width = CompactWidth;
            Card.Height = CompactHeight;
        }
        // 鑷姩璋冭妭灏哄鏃讹細鑳跺泭琛屽乏渚ч澶栫暀鐧斤紙宸︿晶妯悜璺濈鏇村ぇ锛夛紝鎵嬪姩妯″紡淇濇寔瀵圭О
        PillRow.Margin = _settings.Current.CompactWidthAuto
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(0);
        // 60fps 浼樺寲锛氱揣鍑戣鍥哄畾涓虹揣鍑戝唴瀹瑰搴︼紝灞曞紑/鏀惰捣鍔ㄧ敾鏈熼棿涓嶉殢 Card 瀹藉害鍙樺寲閫愬抚閲嶆帓
        UpdateCompactContentWidth();
    }

    /// <summary>搴旂敤澶栬鍙傛暟锛氬渾瑙?/ 瀛椾綋 / 瀛楀彿缂╂斁銆傚瓧鍙风缉鏀句綔鐢ㄤ簬鏁村紶鍗＄墖锛圠ayoutTransform锛夛紝
    /// 閫昏緫灏哄鍚屾闄や互缂╂斁姣旓紝鏈€缁堣瑙夊昂瀵镐笌璁剧疆涓€鑷淬€佷笉婧㈠嚭涓嶈鍓€?/summary>
    /// <summary>Rebuilds the LyricLineText style from user settings (font sizes, spacing, colors).
    /// WPF cannot bind DoubleAnimation.To, so the current-line grow/shrink storyboard is built in code.</summary>
    private void UpdateLyricLineStyle()
    {
        try
        {
            if (LyricsList is null) return;
            var baseSize = LyricBaseFontSize;
            var currentSize = LyricCurrentFontSize;
            var lineHeight = LyricLineHeight;
            var margin = LyricLineMargin;
            var baseBrush = ExpandedLyricBaseBrush;
            var highlightBrush = ExpandedLyricHighlightBrush;

            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.FontSizeProperty, baseSize));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, baseBrush));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, margin));
            style.Setters.Add(new Setter(TextBlock.LineHeightProperty, lineHeight));
            style.Setters.Add(new Setter(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight));
            style.Setters.Add(new Setter(TextBlock.OpacityProperty, 0.28));

            var inSb = new Storyboard();
            var grow = new DoubleAnimation { To = currentSize, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 } };
            Storyboard.SetTargetProperty(grow, new PropertyPath(TextBlock.FontSizeProperty));
            inSb.Children.Add(grow);
            var fadeIn = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 } };
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath(TextBlock.OpacityProperty));
            inSb.Children.Add(fadeIn);

            var outSb = new Storyboard();
            var shrink = new DoubleAnimation { To = baseSize, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new SoftSpringEase { Damping = 16, Stiffness = 160, Mass = 1 } };
            Storyboard.SetTargetProperty(shrink, new PropertyPath(TextBlock.FontSizeProperty));
            outSb.Children.Add(shrink);
            var fadeOut = new DoubleAnimation { To = 0.28, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = new SoftSpringEase { Damping = 16, Stiffness = 160, Mass = 1 } };
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(TextBlock.OpacityProperty));
            outSb.Children.Add(fadeOut);

            var trigger = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding(nameof(LyricLineViewModel.IsCurrent)) { Mode = BindingMode.OneWay },
                Value = true,
            };
            AnimationFrameRate.Apply(inSb, _settings.Current.LowPowerMode);
            AnimationFrameRate.Apply(outSb, _settings.Current.LowPowerMode);
            trigger.EnterActions.Add(new BeginStoryboard { Storyboard = inSb });
            trigger.ExitActions.Add(new BeginStoryboard { Storyboard = outSb });
            trigger.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, highlightBrush));
            style.Triggers.Add(trigger);

            Resources["LyricLineText"] = style;
        }
        catch (Exception ex)
        {
            AppLogger.Error("UpdateLyricLineStyle failed", ex);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshNotificationHistoryProps();

    private void RefreshNotificationHistoryProps()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationHistoryVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationHistoryTitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotificationHistoryClearText)));
    }

    private void NotificationHistory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EventHistoryItem item)
            _vm.ReplayNotification(item);
    }

    private void ClearNotificationHistory_Click(object sender, RoutedEventArgs e)
        => _vm.ClearNotificationHistory();

    /// <summary>按真实布局边距更新紧凑内容宽度，避免右侧文字和媒体按钮被裁切。</summary>
    private void UpdateCompactContentWidth()
    {
        var horizontalChrome = ContentGrid.Margin.Left + ContentGrid.Margin.Right
            + PillRow.Margin.Left + PillRow.Margin.Right;
        PillRow.Width = Math.Max(80, CompactWidth - horizontalChrome);
    }

    private void ApplyAppearance()
    {
        try { System.Windows.Documents.TextElement.SetFontFamily(Card, new System.Windows.Media.FontFamily(_settings.Current.FontFamily)); } catch { /* 闈炴硶瀛椾綋鍚嶅拷鐣?*/ }
        var rounded = new CornerRadius(Math.Clamp(_settings.Current.CornerRadius, 16, 40));
        Card.CornerRadius = rounded;
        // 鐜荤拑鍒嗗眰涓庡崱鐗囧悓姝ュ渾瑙掞紝閬垮厤灞曞紑鏃剁煩褰㈠洓瑙掗湶鍑猴紙娣辨祬鑹叉柟妗嗙殑鏍瑰洜锛?
        if (GlassLayer is not null) GlassLayer.CornerRadius = rounded;
        // 瀛椾綋缂╂斁 = 1 鏃舵竻绌?LayoutTransform锛堣蛋鏅€氬竷灞€璺緞锛屽姩鐢绘湡闂村竷灞€鏇磋交銆佹洿蹇級锛?
        // 鍙湁鐢ㄦ埛璁剧疆缂╂斁鏃舵墠浣跨敤 ScaleTransform锛岄伩鍏嶆棤璋撶殑鍙樻崲寮€閿€銆?
        Card.LayoutTransform = Math.Abs(FontScale - 1.0) < 0.001 ? null : new ScaleTransform(FontScale, FontScale);
        UpdateLyricLineStyle();
        ApplySize();
    }

    /// <summary>鎺ㄩ€佸埌杈?鏇存柊/杩囨湡鏃讹細Card 灏哄鐢ㄥ脊绨у姩鐢诲钩婊戣繃娓″埌鏂板ぇ灏忥紙涓濇粦涓嶇敓纭級銆?/summary>
    private void AnimateCompactSize()
    {
        if (!IsLoaded) return;
        if (_vm.IsExpanded) { ApplySize(); return; }
        EnsureWindowSizeFits(); // 鍏堟墿瀹界獥鍙ｏ紝閬垮厤鍗＄墖鍔ㄧ敾鏈熼棿瓒呭嚭绐楀彛琚鍓?
        UpdateCompactContentWidth();
        var targetWidth = CompactWidth;
        var targetHeight = CompactHeight;
        if (_currentStoryboard is null
            && Math.Abs(Card.ActualWidth - targetWidth) < 0.5
            && Math.Abs(Card.ActualHeight - targetHeight) < 0.5)
        {
            Card.Width = targetWidth;
            Card.Height = targetHeight;
            return;
        }

        var (styleEase, styleMs) = GetSizeAnimationStyle(expand: false);
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0;
        var dur = (int)Math.Clamp(360 * (styleMs / 680.0), 220, 460) * lm;
        // 鍋滄鍓嶄竴涓姩鐢伙紙AnimateCard 鎴?AnimateCompactSize锛夛紝閬垮厤涓や釜 Storyboard 鍚屾椂鍐?Card 灏哄
        _currentStoryboard?.Stop();
        var sb = new Storyboard();
        AddAnim(sb, Card, FrameworkElement.WidthProperty, CompactWidth, (int)dur, styleEase);
        AddAnim(sb, Card, FrameworkElement.HeightProperty, CompactHeight, (int)dur, styleEase);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps锛堣窡闅忔樉绀哄櫒鍒锋柊鐜囷級
        _currentStoryboard = sb; // 鏇存柊寮曠敤锛氶槻姝?AnimateCard 瀹屾垚鍥炶皟瑕嗙洊鏂板昂瀵?
        sb.Begin();
    }

    /// <summary>绗笁鏂瑰簲鐢ㄤ笂宀涳細鎺ㄩ€佸崱鐗囨贰鍏?+ 杞诲井缂╂斁鐨勪笣婊戝姩鐢汇€?/summary>
    private void PlayPushCardAnimation()
    {
        if (!IsLoaded || CompactPushCard is null || !_vm.HasActivePush) return;
        CompactPushCard.Opacity = 0;
        CompactPushScale.ScaleX = CompactPushScale.ScaleY = 0.94;
        var sb = new Storyboard();
        var (styleEase, styleMs) = GetSizeAnimationStyle(expand: true);
        var smooth = CachedCubicEaseOut;
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0;
        var scaleDur = (int)(Math.Min(340, styleMs) * lm);
        AddAnim(sb, CompactPushCard, UIElement.OpacityProperty, 1, (int)(220 * lm), smooth);
        AddAnim(sb, CompactPushScale, ScaleTransform.ScaleXProperty, 1, scaleDur, styleEase);
        AddAnim(sb, CompactPushScale, ScaleTransform.ScaleYProperty, 1, scaleDur, styleEase);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps锛堣窡闅忔樉绀哄櫒鍒锋柊鐜囷級
        sb.Begin();
    }
    /// <summary>鍙抽敭鑿滃崟涓婚鑹诧紙鍦嗚娑叉€佺幓鐠冿級銆?/summary>
    private void ApplyMenuTheme()
    {
        void Add(string key, Brush b) { b.Freeze(); Resources[key] = b; }
        SolidColorBrush bg, border, text, hover;
        if (_theme.IsDark)
        {
            bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0x1B, 0x1B, 0x26));
            border = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
            text = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF7));
            hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0xF5, 0xF5, 0xFA));
            border = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            text = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x24));
            hover = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        }
        Add("MenuBgBrush", bg);
        Add("MenuBorderBrush", border);
        Add("MenuTextBrush", text);
        Add("MenuHoverBrush", hover);

        // 鐩存帴璁剧疆鑿滃崟鑳屾櫙/鍓嶆櫙锛屼繚璇佸嵆浣胯祫婧愭煡鎵惧け璐ヤ篃涓嶄細鍑虹幇鐧藉簳
        if (IslandMenu is not null)
        {
            IslandMenu.Background = bg;
            IslandMenu.Foreground = text;
            IslandMenu.BorderBrush = border;
        }
    }
    private void ApplyCardAlignment()
    {
        Card.HorizontalAlignment = _settings.Current.Position == IslandPosition.Right
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Center;
        Card.Margin = _settings.Current.Position == IslandPosition.Right
            ? new Thickness(0, 0, 4, 0)
            : new Thickness(0);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IslandViewModel.IsVisible):
                if (_vm.IsVisible) ShowIsland(instant: false);
                else HideIsland();
                ApplySize(); // 宀涙樉绀?闅愯棌鏃堕噸鏂版祴閲忚嚜鍔ㄥ昂瀵?
                RefreshWave(); // 鑻ュ惎鍔ㄦ椂宸叉湁濯掍綋鍦ㄦ挱鏀撅紝纭繚娉㈢汗瀹氭椂鍣ㄥ湪宀涙樉绀哄悗鍚姩
                break;
            case nameof(IslandViewModel.IsExpanded):
                AnimateSize();
                ApplyCoverTint(); // 灞曞紑/鏀惰捣鏃跺悓姝ュ皝闈㈠彇鑹插懠鍚革紙#8 鍔ㄦ€佷富棰橈級
                if (_vm.IsExpanded && _vm.LyricIndex >= 0)
                    Dispatcher.BeginInvoke(() => ScrollLyricsTo(_vm.LyricIndex), DispatcherPriority.Loaded);
                if (!_vm.IsExpanded) _compactRestoreTimer.Start(); // 鏀惰捣鍚庡厹搴曟仮澶嶇簿纭昂瀵革紝閬垮厤澶氭鍒囨崲鍚庝笂涓嬮棿璺濆紓甯?
                break;
            case nameof(IslandViewModel.LyricIndex):
                if (_vm.LyricIndex >= 0) QueueLyricsScroll(_vm.LyricIndex);
                break;
            case nameof(IslandViewModel.CompactItems):
                // 缁勪欢鍒楄〃鍙樺寲锛堝闊抽噺鎸囩ず鍑虹幇/娑堝け銆佷复鏃剁姸鎬佽兌鍥婂鍒狅級鏃跺钩婊戣皟鏁村昂瀵?
                if (!_vm.IsExpanded && _vm.IsVisible) AnimateCompactSize();
                break;
            case nameof(IslandViewModel.CurrentLyricText):
                // 褰撳墠姝岃瘝琛屽彉鍖栨椂锛岃嫢澶勪簬绱у噾鎬佸垯骞虫粦璋冩暣瀹藉害锛岄伩鍏嶉暱姝岃瘝琚鍒?閬尅
                if (!_vm.IsExpanded && _vm.IsVisible) AnimateCompactSize();
                break;
            case nameof(IslandViewModel.HasActivePush):
                ApplySize();           // 纭繚绐楀彛瓒冲澶э紙棣栨锛?
                AnimateCompactSize();  // 灏哄鍙樺寲锛氬脊绨у姩鐢伙紝涓濇粦
                PlayPushCardAnimation(); // 涓婂矝鍗＄墖锛氭贰鍏?+ 缂╂斁鍔ㄧ敾
                ApplyExpandedSectionVisibility();
                RaisePushThemeProps();
                break;
            case nameof(IslandViewModel.HasMedia):
                ApplyExpandedSectionVisibility();
                RefreshWave();
                break;
            case nameof(IslandViewModel.HasMultipleSessions):
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MediaSessionPickerVisible)));
                break;
            case nameof(IslandViewModel.IsPlaying):
                RefreshWave();
                break;
            case nameof(IslandViewModel.Artwork):
                ApplyCoverTint();
                PlayCoverTransition(); // 鍒囨瓕锛氬皝闈氦鍙夋贰鍏?+ 杞诲井缂╂斁
                break;
        }
    }

    /// <summary>鍒囨瓕鏃跺皝闈㈣繃娓★細绱у噾灏侀潰 / 灞曞紑澶у皝闈?/ Hero 鑳屾櫙缁熶竴鍋氥€屾贰鍏?+ 杞诲井缂╂斁銆嶏紝
    /// 涓?CoverTint 鑳屾櫙鍛煎惛浜掕ˉ锛屾崲鏇茶鎺ヤ笣婊戜笉鐢熺‖銆?/summary>
    private void PlayCoverTransition()
    {
        if (!IsLoaded) return;
        if (_settings.Current.ReduceMotion) return; // 鍑忓皯鍔ㄦ€佹晥鏋滐細璺宠繃杩囨浮
        var smooth = CachedCubicEaseOut;
        var (_, styleMs) = GetSizeAnimationStyle(expand: true);
        var dur = (int)Math.Clamp(styleMs * 0.42, 180, 420);
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0;

        // 灞曞紑鎬佸ぇ灏侀潰锛氭贰鍏?+ 浠?1.06 缂╂斁鍥?1
        if (BigArt is not null)
        {
            BigArt.BeginAnimation(UIElement.OpacityProperty, null);
            BigArt.Opacity = 0.35;
            var sbA = new Storyboard();
            AddAnim(sbA, BigArt, UIElement.OpacityProperty, 1, (int)(dur * lm), smooth);
            AnimationFrameRate.Apply(sbA, _settings.Current.LowPowerMode);
            sbA.Begin();
        }
        if (BigArtScale is not null)
        {
            BigArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BigArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BigArtScale.ScaleX = BigArtScale.ScaleY = 1.06;
            var sbS = new Storyboard();
            AddAnim(sbS, BigArtScale, ScaleTransform.ScaleXProperty, 1, (int)(dur * lm), smooth);
            AddAnim(sbS, BigArtScale, ScaleTransform.ScaleYProperty, 1, (int)(dur * lm), smooth);
            AnimationFrameRate.Apply(sbS, _settings.Current.LowPowerMode);
            sbS.Begin();
        }
        // 灞曞紑 Hero 澶у皝闈㈣儗鏅細娣″叆
        if (HeroCard is not null)
        {
            HeroCard.BeginAnimation(UIElement.OpacityProperty, null);
            HeroCard.Opacity = 0.35;
            var sbH = new Storyboard();
            AddAnim(sbH, HeroCard, UIElement.OpacityProperty, 1, (int)(dur * lm), smooth);
            AnimationFrameRate.Apply(sbH, _settings.Current.LowPowerMode);
            sbH.Begin();
        }
        // 绱у噾琛屾瓕鏇插皝闈紙鏁版嵁妯℃澘鍐咃紝鐢?Tag 瀹氫綅鍚庢贰鍏ワ級
        foreach (var b in FindVisualChildren<System.Windows.Controls.Border>(PillRow))
        {
            if (!ReferenceEquals(b.Tag, "SongCover")) continue;
            b.BeginAnimation(UIElement.OpacityProperty, null);
            b.Opacity = 0.35;
            var sbC = new Storyboard();
            AddAnim(sbC, b, UIElement.OpacityProperty, 1, (int)(dur * lm), smooth);
            AnimationFrameRate.Apply(sbC, _settings.Current.LowPowerMode);
            sbC.Begin();
        }
    }

    /// <summary>浠庡彲瑙嗘爲涓婃敹闆嗘寚瀹氱被鍨嬪瓙鍏冪礌锛堟祬灞傞亶鍘嗭紝浠呯敤浜庡垏姝屾椂鐨勫皝闈㈠畾浣嶏級銆?/summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    // 鈹€鈹€ 澹伴煶娉㈢汗 / 灏侀潰鍙栬壊 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private void RefreshWave()
    {
        var on = HasWave;
        ApplyWaveStyleVisibility();
        var lowPower = _settings.Current.LowPowerMode;
        // 涓夋€侊細鍏抽棴 / 鏅€氾紙CompositionTarget.Rendering 璺熼殢鏄剧ず鍣級/ 浣庡姛鑰楀畾鏃跺櫒锛垀120fps锛?
        var wantTimer = on && lowPower;
        var wantComposition = on && !lowPower;
        var isTimer = _waveTimer is not null;
        var isComposition = _waveRendering && !isTimer;
        if (wantTimer != isTimer || wantComposition != isComposition)
        {
            StopWaveRender();
            if (wantTimer)
            {
                _waveRendering = true;
                _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };     // CompositionTarget.Rendering锛堣窡闅忔樉绀哄櫒鍒锋柊鐜囷級
                _waveTimer.Tick += (_, _) => OnWaveFrame(null, EventArgs.Empty);
                _waveTimer.Start();
            }
            else if (wantComposition)
            {
                _waveRendering = true;
                CompositionTarget.Rendering += OnWaveFrame;
            }
        }
        if (!on)
        {
            foreach (var sc in _waveBarsExpanded) sc.ScaleY = 0.16;
            foreach (var sc in _waveBarsCompact) sc.ScaleY = 0.16;
            foreach (var sc in _waveSpectrumExpanded) sc.ScaleY = 0.05;
            foreach (var sc in _waveSpectrumCompact) sc.ScaleY = 0.05;
            if (_waveRingScaleExpanded is not null) { _waveRingScaleExpanded.ScaleX = _waveRingScaleExpanded.ScaleY = 1.0; }
            if (_waveRingScaleCompact is not null) { _waveRingScaleCompact.ScaleX = _waveRingScaleCompact.ScaleY = 1.0; }
            foreach (var tr in _waveParticleTransformsExpanded) tr.Y = 0;
            foreach (var tr in _waveParticleTransformsCompact) tr.Y = 0;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasWave)));
    }
    /// <summary>鍋滄娉㈢汗娓叉煋锛堟憳闄ゅ悎鎴愬抚浜嬩欢 + 鍋滄闄嶅抚瀹氭椂鍣級銆?/summary>
    private void StopWaveRender()
    {
        _waveRendering = false;
        if (_waveTimer is not null)
        {
            _waveTimer.Stop();
            _waveTimer = null;
        }
        CompositionTarget.Rendering -= OnWaveFrame;
    }
    /// <summary>鍚堟垚甯у洖璋冿細鎸夊抚闂撮殧鎸囨暟骞虫粦锛岄殢鐪熷疄闊抽鐢靛钩璧蜂紡锛屽姩鐢昏繛璐笉鍗￠】銆?/summary>
    private void OnWaveFrame(object? sender, EventArgs e)
    {
        try
        {
            if (!IsLoaded || !HasWave)
            {
                RefreshWave();
                return;
            }
            var now = _waveClock.Elapsed.TotalSeconds;
            var dt = Math.Min(0.05, Math.Max(0.001, now - _lastWaveTime));
            _lastWaveTime = now;

            var level = Math.Clamp(_vm.WaveLevel, 0, 1);
            var height = Math.Clamp(_settings.Current.WaveHeight, 0.25, 2.0);
            var alpha = 1.0 - Math.Exp(-dt * 22.0); // 甯х巼鏃犲叧鐨勬寚鏁板钩婊?
            // 1.2.1 鎬ц兘浼樺寲锛氬彧鏇存柊褰撳墠鍙鐨勬尝绾归泦鍚堬紙灞曞紑=澶ф尝绾广€佺揣鍑?灏忔尝绾癸級锛?
            // 闅愯棌闈㈡澘姣忓抚鐨?ScaleTransform 鏇存柊鍏ㄩ儴鐪佹帀锛岄檷浣庡獟浣撴挱鏀炬椂鐨?CPU 鍗犵敤
            var expanded = _vm.IsExpanded;
            switch (_settings.Current.WaveStyle)
            {
                case "Spectrum":
                    UpdateWaveSet(expanded ? _waveSpectrumExpanded : _waveSpectrumCompact, level, now, alpha, height, bias: 1);
                    break;
                case "Ring":
                    UpdateRingVisual(expanded ? _waveRingScaleExpanded : _waveRingScaleCompact, level, now, alpha);
                    break;
                case "Particles":
                    UpdateParticlesVisual(expanded ? _waveParticleTransformsExpanded : _waveParticleTransformsCompact, level, now, alpha, expanded ? 8.0 : 5.0);
                    break;
                default:
                    UpdateWaveSet(expanded ? _waveBarsExpanded : _waveBarsCompact, level, now, alpha, height);
                    break;
            }
        }
        catch
        {
            // 娓叉煋寮傚父缁濅笉褰卞搷涓绘祦绋?
        }
    }

    private void UpdateWaveSet(IReadOnlyList<ScaleTransform> bars, double level, double t, double alpha, double height, double bias = 0)
    {
        var n = bars.Count;
        for (var i = 0; i < n; i++)
        {
            var sc = bars[i];
            double target;
            if (_vm.IsPlaying)
            {
                var phase = t * 6.0 - i * 0.9;
                var wave = 0.5 + 0.5 * Math.Sin(phase);
                if (bias > 0)
                    target = Math.Clamp((0.05 + (0.14 + 0.66 * level) * wave * (0.55 + 0.45 * (double)i / n)) * height, 0.05, 1.0);
                else
                    target = Math.Clamp((0.10 + (0.12 + 0.72 * level) * wave) * height, 0.08, 1.0);
            }
            else
            {
                target = bias > 0 ? 0.05 : 0.08;
            }
            sc.ScaleY += (target - sc.ScaleY) * alpha;
        }
    }
    /// <summary>鎸夊綋鍓嶆尝绾规牱寮忓垏鎹㈠彲瑙侀潰鏉匡紙鏌辩姸/棰戣氨/鐜舰/绮掑瓙锛夈€?/summary>
    private void ApplyWaveStyleVisibility()
    {
        var style = _settings.Current.WaveStyle ?? "Bars";
        var bars = style == "Bars" ? Visibility.Visible : Visibility.Collapsed;
        var spec = style == "Spectrum" ? Visibility.Visible : Visibility.Collapsed;
        var ring = style == "Ring" ? Visibility.Visible : Visibility.Collapsed;
        var part = style == "Particles" ? Visibility.Visible : Visibility.Collapsed;
        if (WaveBarsPanelCompact is not null) WaveBarsPanelCompact.Visibility = bars;
        if (WaveBarsPanelExpanded is not null) WaveBarsPanelExpanded.Visibility = bars;
        if (WaveSpectrumHostCompact is not null) WaveSpectrumHostCompact.Visibility = spec;
        if (WaveSpectrumHostExpanded is not null) WaveSpectrumHostExpanded.Visibility = spec;
        if (WaveRingHostCompact is not null) WaveRingHostCompact.Visibility = ring;
        if (WaveRingHostExpanded is not null) WaveRingHostExpanded.Visibility = ring;
        if (WaveParticlesHostCompact is not null) WaveParticlesHostCompact.Visibility = part;
        if (WaveParticlesHostExpanded is not null) WaveParticlesHostExpanded.Visibility = part;
    }

    /// <summary>鏋勫缓棰戣氨/鐜舰/绮掑瓙涓夌澶囬€夋尝绾癸紙鍚姩鏃朵竴娆℃€у垱寤猴紝棰滆壊闅忎富棰樼粦瀹氾級銆?/summary>
    private void InitWaveVisualStyles()
    {
        try
        {
            BuildSpectrumBars(WaveSpectrumHostCompact, _waveSpectrumCompact, 12, 1.8, 1.2);
            BuildSpectrumBars(WaveSpectrumHostExpanded, _waveSpectrumExpanded, 16, 2.2, 1.2);
            _waveRingScaleCompact = BuildRing(WaveRingHostCompact, 12);
            _waveRingScaleExpanded = BuildRing(WaveRingHostExpanded, 16);
            BuildParticles(WaveParticlesHostCompact, _waveParticleTransformsCompact, 8);
            BuildParticles(WaveParticlesHostExpanded, _waveParticleTransformsExpanded, 10);
            ApplyWaveStyleVisibility();
        }
        catch
        {
            // 澶囬€夋牱寮忔瀯寤哄け璐ユ椂浠呬繚鐣欓粯璁ゆ煴鐘讹紝涓嶅奖鍝嶄富娴佺▼
        }
    }

    /// <summary>棰戣氨鏉★細绐勬潯涓嬪榻愶紝鍙抽珮宸︿綆棰戞鍒嗗竷锛岄殢鑺傚璧蜂紡銆?/summary>
    private void BuildSpectrumBars(Grid? host, List<ScaleTransform> list, int count, double barW, double gap)
    {
        if (host is null) return;
        var left = (host.Width - (count * barW + (count - 1) * gap)) / 2.0;
        for (var i = 0; i < count; i++)
        {
            var sc = new ScaleTransform(1, 0.05);
            var bar = new Border
            {
                Width = barW,
                Height = host.Height,
                CornerRadius = new CornerRadius(Math.Max(0.3, barW / 2)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(left, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1.0),
                RenderTransform = sc,
            };
            BindingOperations.SetBinding(bar, Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
            host.Children.Add(bar);
            list.Add(sc);
            left += barW + gap;
        }
    }

    /// <summary>鐜舰娉㈢汗锛氬渾鐐逛腑蹇冿紝闅忚妭濂忕缉鏀俱€?/summary>
    private ScaleTransform? BuildRing(Grid? host, double diameter)
    {
        if (host is null) return null;
        var sc = new ScaleTransform(1, 1);
        var ring = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = sc,
        };
        BindingOperations.SetBinding(ring, Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
        host.Children.Add(ring);
        return sc;
    }

    /// <summary>绮掑瓙娉㈢汗锛氭暎甯冨皬鍦嗙偣锛岄殢鑺傚涓婁笅鑴夊啿銆?/summary>
    private void BuildParticles(Grid? host, List<TranslateTransform> list, int count)
    {
        if (host is null) return;
        var spacing = host.Width / count;
        for (var i = 0; i < count; i++)
        {
            var tr = new TranslateTransform(0, 0);
            var p = new System.Windows.Shapes.Ellipse
            {
                Width = 2.5,
                Height = 2.5,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(spacing * (i + 0.5) - 1.25, 0, 0, 0),
                RenderTransform = tr,
            };
            BindingOperations.SetBinding(p, System.Windows.Shapes.Ellipse.FillProperty, new System.Windows.Data.Binding(nameof(TextPrimary)) { Source = this });
            host.Children.Add(p);
            list.Add(tr);
        }
    }

    private void UpdateRingVisual(ScaleTransform? ring, double level, double t, double alpha)
    {
        if (ring is null) return;
        double target = 1.0;
        if (_vm.IsPlaying)
        {
            var wave = 0.5 + 0.5 * Math.Sin(t * 6.0);
            target = 1.0 + 0.24 * level * wave;
        }
        ring.ScaleX += (target - ring.ScaleX) * alpha;
        ring.ScaleY = ring.ScaleX;
    }

    private void UpdateParticlesVisual(IReadOnlyList<TranslateTransform> parts, double level, double t, double alpha, double maxY)
    {
        var n = parts.Count;
        for (var i = 0; i < n; i++)
        {
            var tr = parts[i];
            double target = 0;
            if (_vm.IsPlaying)
            {
                var wave = 0.5 + 0.5 * Math.Sin(t * 6.0 - i * 1.3);
                target = -wave * level * maxY;
            }
            tr.Y += (target - tr.Y) * alpha;
        }
    }

    /// <summary>灞曞紑鑳屾櫙闅忎笓杈戝皝闈㈠彇鑹诧細1x1 閲囨牱涓昏壊 + 涓婚搴曡壊绾挎€ф笎鍙橈紱灞曞紑鍚庝互 60fps 缂撴參鍛煎惛銆?
    /// 娓愬彉 brush / GradientStop 缂撳瓨澶嶇敤锛屾覆鏌撳抚鍙洿鏂伴 stop 鐨?Alpha锛岄伩鍏嶆瘡甯ч噸寤哄璞″鑷?GC 鎶栧姩銆?/summary>
    private void ApplyCoverTint(bool forceRebuild = false)
    {
        try
        {
            var src = _vm.Artwork;
            if (src is null || !_settings.Current.CoverTintBackground || !_vm.IsExpanded)
            {
                ClearCoverTint();
                SubscribeTintRendering(false);
                return;
            }
            // 缂撳瓨灏侀潰鍙栬壊缁撴灉锛氬悓灏侀潰涓嶉噸澶嶉噰鏍凤紙閬垮厤灞曞紑/鏀惰捣鏃堕噸鏂?RenderTargetBitmap锛?
            if (!ReferenceEquals(src, _lastSampledArtwork))
            {
                _lastSampledColor = SampleCoverColor(src);
                _lastSampledArtwork = src;
            }
            var color = _lastSampledColor;
            if (color is null)
            {
                ClearCoverTint();
                SubscribeTintRendering(false);
                return;
            }

            // 灏侀潰涓昏壊鍙樺寲锛堟崲鏇诧級鏃堕噸寤烘笎鍙橈紱鍚屾洸鍙鐢ㄥ苟鏇存柊 Alpha锛堝懠鍚革級
            if (_tintBrush is null || _tintCoverColor != color || forceRebuild) // 涓婚鍒囨崲鏃跺己鍒堕噸寤猴紙鍩鸿壊闅忔柊涓婚锛?
            {
                _tintCoverColor = color;
                var baseColor = (_theme.CardBackground as SolidColorBrush)?.Color
                    ?? System.Windows.Media.Color.FromArgb(0xF0, 0x14, 0x14, 0x1E);
                _tintBrush = new LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(1, 1),
                };
                _tintStop0 = new GradientStop(System.Windows.Media.Color.FromArgb(0xE6, color.Value.R, color.Value.G, color.Value.B), 0);
                _tintStop1 = new GradientStop(baseColor, 1);
                _tintBrush.GradientStops.Add(_tintStop0);
                _tintBrush.GradientStops.Add(_tintStop1);
            }
            Card.Background = _tintBrush;
            // 灏侀潰鍙栬壊鐢熸晥鏃剁幓鐠冨眰褰掗浂锛岄伩鍏嶅彔鍔?
            _glassAnimSb?.Stop();
            _glassAnimSb = null;
            if (GlassLayer is not null) GlassLayer.Opacity = 0;
            _tintPhaseUtc = DateTime.UtcNow;
            SubscribeTintRendering(true);
        }
        catch
        {
            ClearCoverTint();
            SubscribeTintRendering(false);
        }
    }

    /// <summary>璁㈤槄 / 鍙栨秷鍚堟垚甯ч┍鍔紙绌洪棽鏃朵笉鍗?CPU锛夈€?/summary>
    private void SubscribeTintRendering(bool subscribe)
    {
        if (subscribe == _tintRenderingSubscribed) return;
        if (subscribe) CompositionTarget.Rendering += OnTintFrame;
        else CompositionTarget.Rendering -= OnTintFrame;
        _tintRenderingSubscribed = subscribe;
    }

    /// <summary>姣忓抚锛氬彇鑹插眰 Alpha 鍦?0.85~0.97 涔嬮棿缂撴參鍛煎惛锛堢害 18s 涓€涓懆鏈燂級锛屼笣婊戜笉璺冲彉銆?/summary>
    private void OnTintFrame(object? sender, EventArgs e)
    {
        if (!_vm.IsExpanded || !_settings.Current.CoverTintBackground || _vm.Artwork is null || _tintStop0 is null)
        {
            SubscribeTintRendering(false);
            return;
        }
        var c = _tintCoverColor;
        if (c is null) { SubscribeTintRendering(false); return; }
        var t = (DateTime.UtcNow - _tintPhaseUtc).TotalSeconds;
        var alpha = 0.85 + 0.06 * (0.5 + 0.5 * Math.Sin(t * 0.35)); // 0.85..0.97 鎱㈠懆鏈?
        var a = (byte)Math.Round(alpha * 255);
        if (_tintStop0.Color.A != a)
            _tintStop0.Color = System.Windows.Media.Color.FromArgb(a, c.Value.R, c.Value.G, c.Value.B);
    }

    /// <summary>鎭㈠ Card 鑳屾櫙涓虹粦瀹氱殑涓婚鑹诧紙绉婚櫎灏侀潰鍙栬壊锛夈€?/summary>
    private void ClearCoverTint()
    {
        try
        {
            Card.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(CardBackground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
            });
        }
        catch
        {
            Card.Background = _theme.CardBackground;
        }
        // 鏃犲皝闈㈠彇鑹叉椂閲嶆柊鍚敤鐜荤拑鍒嗗眰锛堝睍寮€鎬佹洿瀹炪€佺揣鍑戞€侀€氶€忥級
        if (GlassLayer is not null && (_vm.IsExpanded || !_settings.Current.CoverTintBackground))
            AnimateGlass(_vm.IsExpanded);
    }

    /// <summary>鎶婂皝闈㈡覆鏌撳埌 1x1 浣嶅浘閲囨牱涓昏壊锛圧GBA锛夈€?/summary>
    private static System.Windows.Media.Color? SampleCoverColor(ImageSource src)
    {
        try
        {
            var rtb = new RenderTargetBitmap(1, 1, 96, 96, PixelFormats.Pbgra32);
            var img = new System.Windows.Controls.Image { Source = src, Stretch = Stretch.UniformToFill };
            rtb.Render(img);
            var px = new byte[4];
            rtb.CopyPixels(px, 4, 0);
            if (px[3] < 40) return null; // 閫忔槑/鏈姞杞藉畬鎴愶紝鏀惧純鍙栬壊
            return System.Windows.Media.Color.FromArgb(255, px[2], px[1], px[0]);
        }
        catch
        {
            return null;
        }
    }

    // 鈹€鈹€ 鐐瑰嚮绌块€?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            var x = (short)(lParam.ToInt64() & 0xFFFF);
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var local = PointFromScreen(new Point(x, y));
            handled = true;
            return IsPointOverCard(local) ? (IntPtr)HTCLIENT : (IntPtr)HTTRANSPARENT;
        }

        return IntPtr.Zero;
    }

    private bool IsPointOverCard(Point local)
    {
        var pos = Card.TransformToAncestor(this).Transform(new Point(0, 0));
        return local.X >= pos.X && local.Y >= pos.Y &&
               local.X <= pos.X + Card.ActualWidth &&
               local.Y <= pos.Y + Card.ActualHeight;
    }

    // 鈹€鈹€ 鏄剧ず / 闅愯棌 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private void ShowIsland(bool instant)
    {
        if (!IsLoaded) return;
        if (!IsVisible)
        {
            // 鍏堟仮澶嶅瑙備笌灏哄锛岄伩鍏嶉殣钘忔湡闂磋鍘嬬缉瀵艰嚧閲嶆柊鏄剧ず鏃朵袱渚ц瑁佸垏
            ApplyAppearance();
            ApplySize();
            Reposition();
            Show();
            // 鏄剧ず鍚庝互 Loaded 浼樺厛绾у啀瀹氫綅涓€娆★紝纭繚绐楀彛鍒?Show 鏃跺昂瀵稿凡鐢熸晥
            Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (instant)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        BeginOpacity(1, 280);
    }

    private void HideIsland()
    {
        if (!IsVisible) return;
        var sb = new Storyboard();
        // 闈炵嚎鎬ф贰鍑猴細鍏堝揩鍚庢參锛圗aseIn锛夛紝娑堝け杩囩▼涓嶅寑閫熴€佷笉鐢熺‖
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 },
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        sb.Completed += (_, _) => { if (!_vm.IsVisible) Hide(); };
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps锛堣窡闅忔樉绀哄櫒鍒锋柊鐜囷級
        sb.Begin();
    }

    private void BeginOpacity(double to, int ms)
    {
        var sb = new Storyboard();
        var fade = new DoubleAnimation(Opacity, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new SoftSpringEase { Damping = 14, Stiffness = 180, Mass = 1 },
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps锛堣窡闅忔樉绀哄櫒鍒锋柊鐜囷級
        sb.Begin();
    }

    // 鈹€鈹€ iOS 椋庢牸褰㈠彉鍔ㄧ敾 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private void AnimateSize()
    {
        if (!IsLoaded) return;
        if (_vm.IsExpanded) Expand();
        else Collapse();
    }

    private void Expand()
    {
        // 鑳跺泭琛屼繚鎸佸彲瑙侊紙鍔ㄧ敾娣″嚭锛夛紝灞曞紑鍐呭瑕嗙洊鍏ㄥ崱鐗囷紙鍔ㄧ敾娣″叆锛夛紝涓よ€呴噸鍙犱氦鍙夎繃娓★紝
        // 閬垮厤 Card 娣辫壊鑳屾櫙閫忚繃鍐呭闂撮殭浜х敓"榛戞帀"鐜拌薄
        AnimateGlass(true); // 鏅鸿兘閫忔槑搴︼細灞曞紑鎬佹洿瀹?
        ContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ContentGrid.RowDefinitions[1].Height = GridLength.Auto;
        ContentGrid.VerticalAlignment = VerticalAlignment.Center;
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);

        // 鍏堟祴閲忓睍寮€鍐呭鑷劧楂樺害锛圫crollViewer 鍐呭鎬婚珮锛夛紝寰楀埌鍗＄墖鐩爣楂樺害
        ExpandedContent.Opacity = 0;
        ExpandedContent.Visibility = Visibility.Visible;
        // 60fps 浼樺寲锛氬睍寮€鍐呭鍥哄畾鐩爣瀹藉害锛屽睍寮€鍔ㄧ敾鏈熼棿涓嶉殢鍗＄墖瀹藉害閫愬抚閲嶆帓锛堝唴瀹瑰彧甯冨眬涓€娆★級
        ExpandedContent.Width = Math.Max(120, ExpandedWidth - 20);
        ExpandedContent.Measure(new System.Windows.Size(ExpandedContent.Width, double.PositiveInfinity));
        var contentH = ExpandedContent.DesiredSize.Height;
        var targetHeight = Math.Clamp(contentH + 24, 200, MaxExpandedHeight);

        // 閲嶆柊鏄剧ず鑳跺泭琛岋細鍔ㄧ敾鏈熼棿娣″嚭锛屼笌灞曞紑鍐呭浜ゅ弶杩囨浮
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        PillRow.Visibility = Visibility.Visible;
        PillRow.Opacity = 1;

        AnimateCard(ExpandedWidth, targetHeight, expand: true);
    }

    private void Collapse()
    {
        // 鍏堟仮澶嶈兌鍥婅锛堢揣鍑戣鍗犳弧骞跺瀭鐩村眳涓級锛屽啀缂╁洖绱у噾灏哄
        AnimateGlass(false); // 鏅鸿兘閫忔槑搴︼細绱у噾鎬佹洿閫氶€?
        ContentGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ContentGrid.RowDefinitions[1].Height = GridLength.Auto; // 灞曞紑琛屾仮澶嶈嚜閫傚簲
        // 淇濇寔鍨傜洿灞呬腑锛氭敹鍥炲悗缁勪欢涓婁笅瀵圭О锛堟鍓嶈涓?Top 浼氬鑷磋创椤躲€佷笅鏂圭暀鐧斤紝灞曞紑鏀跺洖鍚庤窛绂讳笉鍚岋級
        ContentGrid.VerticalAlignment = VerticalAlignment.Center;

        // 娓呴櫎灞曞紑鍔ㄧ敾鐨勬畫鐣欙紙HoldEnd 浼氭妸 PillRow.Opacity 閿佸湪 0锛岀洿鎺ヨ鏈湴鍊兼棤鏁堬級
        PillRow.BeginAnimation(UIElement.OpacityProperty, null);
        ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);
        PillRow.Visibility = Visibility.Visible;
        PillRow.Opacity = 1;
        // 灞曞紑鍐呭淇濇寔鍙浠ユ挱鏀俱€岃嚜涓嬭€屼笂銆嶇殑浜ら敊娣″嚭鍔ㄧ敾锛屽姩鐢荤粨鏉熷悗鐢?AnimateCard 鍥炶皟闅愯棌
        ExpandedContent.Visibility = Visibility.Visible;
        ExpandedContent.Opacity = 1;
        // 60fps 浼樺寲锛氭敹璧峰姩鐢绘湡闂村睍寮€鍐呭鍥哄畾瀹藉害锛屼笉闅忓崱鐗囧搴﹂€愬抚閲嶆帓
        ExpandedContent.Width = Math.Max(120, CompactWidth - 20);

        AnimateCard(CompactWidth, CompactHeight, expand: false,
            onCompleted: () => { Card.Width = CompactWidth; Card.Height = CompactHeight; });
    }

    /// <summary>
    /// 鍔ㄧ敾锛氬崱鐗囧昂瀵哥敤 iOS 闃诲凹寮圭哀锛堝厛蹇悗鎱€佽交寰繃鍐插洖寮癸級锛?
    /// 灞曞紑鍐呭鎸夊尯鍧楄嚜涓婅€屼笅浜ら敊娣″叆涓婄Щ銆佹敹璧锋椂鍙嶅悜浜ら敊娣″嚭涓嬬Щ锛?.2.1锛夛紝鏁翠綋鑺傚闈炵嚎鎬с€佷笉鐢熺‖銆?
    /// </summary>
    private void AnimateCard(double width, double height, bool expand, Action? onCompleted = null)
    {
        _currentStoryboard?.Stop();
        _currentStoryboard = null;

        // 鍑忓皯鍔ㄦ€佹晥鏋滐細鍏抽棴寮圭哀/浜ら敊鍔ㄧ敾锛岀洿鎺ョ灛鏃跺垏鎹紙鏃犻殰纰?/ 鐪佺數锛?
        if (_settings.Current.ReduceMotion)
        {
            Card.Width = width;
            Card.Height = height;
            PillRow.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
            PillRow.Opacity = expand ? 0 : 1;
            ExpandedContent.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            ExpandedContent.Opacity = expand ? 1 : 0;
            ExpandedScale.ScaleX = ExpandedScale.ScaleY = expand ? 1 : 0.98;
            ExpandedTranslate.Y = expand ? 0 : 10;
            ApplyCascadeState(expand ? 1 : 0, expand ? 0 : 10);
            onCompleted?.Invoke();
            return;
        }

        var sb = new Storyboard();
        // 鍔ㄦ晥鐨偆锛?3锛夛細Spring= iOS 寮圭哀锛堥粯璁わ級/ Soft=鏌斿拰 / Elastic=寮规€?/ Fade=绠€娲佹笎闅?
        var (styleEase, styleSizeMs) = GetSizeAnimationStyle(expand);
        var smooth = CachedCubicEaseOut;
        var lm = _settings.Current.LowPowerMode ? 0.6 : 1.0; // 浣庡姛鑰楁ā寮忥紙37锛夛細鍔ㄧ敾鏃堕棿缂╃煭锛屾洿蹇繘鍏ョ┖闂?

        // 鍗＄墖灏哄锛氬姩鏁堢毊鑲ゆ洸绾匡紙灞曞紑/鏀惰捣鏃堕暱鐢辩毊鑲ゅ喅瀹氾級
        AddAnim(sb, Card, FrameworkElement.WidthProperty, width, (int)(styleSizeMs * lm), styleEase);
        AddAnim(sb, Card, FrameworkElement.HeightProperty, height, (int)(styleSizeMs * lm), styleEase);

        // 灞曞紑鍐呭浜ら敊杩囨浮锛?.2.1 鍔熻兘 2锛夛細
        //  灞曞紑 鈥斺€?鍖哄潡鑷笂鑰屼笅渚濇娣″叆 + 杞诲井涓婄Щ锛堟瘡鍖哄潡寤惰繜 70ms锛岄敊宄板嚭鐜帮級
        //  鏀惰捣 鈥斺€?鍖哄潡鑷笅鑰屼笂鍙嶅悜渚濇娣″嚭 + 杞诲井涓嬬Щ锛屽鍣ㄦ渶鍚庢暣浣撴贰鍑?
        var blocks = _cascadeBlocks;
        if (expand)
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                var (el, tr) = blocks[i];
                el.Opacity = 0;   // 閲嶇疆鍏ュ満璧风偣锛屼繚璇佹瘡娆″睍寮€閮戒粠绌虹櫧寮€濮嬮敊宄板嚭鐜?
                tr.Y = 12;
                var delay = TimeSpan.FromMilliseconds((90 + i * 70) * lm);
                AddAnim(sb, el, UIElement.OpacityProperty, 1, (int)(340 * lm), smooth, delay);
                AddAnim(sb, tr, TranslateTransform.YProperty, 0, (int)(420 * lm), smooth, delay);
            }
            // 瀹瑰櫒娣″叆锛岃鐩栨暣涓氦閿欒繃绋嬶紙鍐呭鍑虹幇鏃舵暣浣撴洿鏌斿拰锛?
            AddAnim(sb, ExpandedContent, UIElement.OpacityProperty, 1, (int)(460 * lm), smooth, TimeSpan.FromMilliseconds(90 * lm));
        }
        else
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                var (el, tr) = blocks[i];
                // 鏀惰捣锛氬厛鏀跺熬閮ㄥ尯鍧楋紝鍐嶆敹椤堕儴鍖哄潡锛堜笌灞曞紑椤哄簭鐩稿弽锛?
                var delay = TimeSpan.FromMilliseconds((blocks.Length - 1 - i) * 55 * lm);
                AddAnim(sb, el, UIElement.OpacityProperty, 0, (int)(180 * lm), smooth, delay);
                AddAnim(sb, tr, TranslateTransform.YProperty, 14, (int)(220 * lm), smooth, delay);
            }
            // 瀹瑰櫒鍦ㄥ尯鍧楀熀鏈贰鍑哄悗鍐嶆暣浣撴贰鍑猴紝閬垮厤鍐呭娈嬬暀
            AddAnim(sb, ExpandedContent, UIElement.OpacityProperty, 0, (int)(240 * lm), smooth,
                TimeSpan.FromMilliseconds((blocks.Length * 55 + 150) * lm));
            // 鑳跺泭琛岋細宸茬敱 Collapse 鎭㈠涓哄畬鍏ㄤ笉閫忔槑锛屼綔涓烘贰鍑鸿繃绋嬩腑鐨勫簳灞傛壙鎺ュ唴瀹?
            PillRow.Opacity = 1;
        }

        // 鑳跺泭琛岋細灞曞紑鍚庢贰鍑猴紙鐢卞ぇ鍥惧尯鎺ョ锛夛紱鏀惰捣鏃剁珛鍗虫仮澶嶅畬鍏ㄤ笉閫忔槑锛?
        // 閬垮厤缂╁洖鐬棿鑳跺泭鍐呭杩樺湪娣″叆鑰屽嚭鐜?绌哄唴瀹?
        if (expand)
            AddAnim(sb, PillRow, UIElement.OpacityProperty, 0, (int)(300 * lm), smooth, TimeSpan.FromMilliseconds(80));

        sb.Completed += (_, _) =>
        {
            // 闃叉棫鍔ㄧ敾瀹屾垚鍥炶皟瑕嗙洊鏂板姩鐢荤姸鎬侊紙蹇€熻繛缁睍寮€/鏀惰捣鏃跺昂瀵搁敊涔憋級
            if (!ReferenceEquals(_currentStoryboard, sb)) return;
            try
            {
                _currentStoryboard = null;
                // 鍏抽敭锛氭竻闄ゅ姩鐢诲 Card 灏哄鐨?HoldEnd 閿佸畾锛屽惁鍒欎箣鍚庤缃湰鍦板昂瀵革紙鍚嚜鍔ㄩ噸绠楋級涓嶇敓鏁堬紝
                // 澶氭灞曞紑/鏀惰捣鍚庣粍浠朵笂涓嬮棿璺濅細娈嬬暀寮傚父
                Card.BeginAnimation(FrameworkElement.WidthProperty, null);
                Card.BeginAnimation(FrameworkElement.HeightProperty, null);
                // 蹇呴』鍐欏洖鏈€缁堝昂瀵革細娓呴櫎鍔ㄧ敾鍚庤嫢鍙緷璧栨湰鍦板€硷紝Card 浼氬洖閫€鍒扮揣鍑戞椂璁剧疆鐨?
                // 鏈湴灏哄锛圵idth/Height锛夛紝灞曞紑鎬佺灛闂寸缉鍥炵揣鍑戝ぇ灏忓鑷村唴瀹硅瑁佸壀鑰岄粦灞?
                Card.Width = width;
                Card.Height = height;
                // 鍔ㄧ敾缁撴潫鍚庢暣鐞嗗彲瑙佹€э細灞曞紑鎬佹姌鍙犺兌鍥婅骞跺浐瀹氬睍寮€鍐呭涓嶉€忔槑锛屾敹璧锋€佹仮澶嶈兌鍥婅
                if (_vm.IsExpanded)
                {
                    PillRow.Visibility = Visibility.Collapsed;
                    PillRow.Opacity = 0;
                    ExpandedContent.Opacity = 1;
                    ExpandedContent.Width = double.NaN; // 鎭㈠鑷€傚簲甯冨眬
                }
                else
                {
                    PillRow.Visibility = Visibility.Visible;
                    PillRow.Opacity = 1;
                    ExpandedContent.Visibility = Visibility.Collapsed;
                    ExpandedContent.Opacity = 0;
                    ExpandedContent.Width = double.NaN; // 鎭㈠鑷€傚簲甯冨眬
                }
                onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Card animation completed failed", ex);
            }
        };
        _currentStoryboard = sb;
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode); // 120fps锛堣窡闅忔樉绀哄櫒鍒锋柊鐜囷級
        sb.Begin();
    }

    /// <summary>
    /// 鍔ㄦ晥鐨偆锛?3锛夛細杩斿洖 (灏哄缂撳姩, 鍩哄噯鏃堕暱姣) 鍏冪粍銆?
    /// Spring = iOS 闃诲凹寮圭哀锛堥粯璁わ紝杞诲井杩囧啿鍥炲脊锛夛紱Soft = 鏌斿拰寮圭哀锛堝洖寮规洿灏戞洿杞級锛?
    /// Elastic = 寮规€у洖寮癸紙鏄庢樉寮硅烦锛夛紱Fade = 绠€娲佹笎闅愶紙鏃犲洖寮癸紝鏈€鍏嬪埗锛夈€?
    /// </summary>
    private (IEasingFunction Easing, int SizeMs) GetSizeAnimationStyle(bool expand)
    {
        // 1.2.0锛氬姩鐢绘椂闀垮彲鐢辩敤鎴峰井璋冿紙300~1400ms锛岄粯璁?700ms锛夈€?
        // 鍚勯鏍间繚鐣欑浉瀵瑰樊寮傦細Spring 鍏ㄦ椂闀?/ Soft 鐣ユ參 / Elastic 鐣ュ揩 / Fade 鏈€鐭紱
        // 鏀惰捣鏃堕暱绾︿负灞曞紑鐨?0.86 鍊嶏紝璁╁洖鏀舵洿蹇竴鐐规洿鍒╄惤銆?
        var baseMs = Math.Clamp(_settings.Current.IslandAnimationDuration, 300, 1400);
        static int Ms(double v) => (int)Math.Round(v);
        switch (_settings.Current.AnimationStyle)
        {
            case "Soft":
                return (CachedSoftEase, expand ? Ms(baseMs * 1.08) : Ms(baseMs * 0.94));
            case "Elastic":
                return (CachedElasticEase, expand ? Ms(baseMs * 0.97) : Ms(baseMs * 0.84));
            case "Smooth":
                return (CachedSoftEaseSmooth, expand ? Ms(baseMs * 1.02) : Ms(baseMs * 0.88));
            case "Fade":
                return (CachedCubicEaseOut, expand ? Ms(baseMs * 0.74) : Ms(baseMs * 0.64));
            default: // Spring
                return (CachedSpringEase, expand ? baseMs : Ms(baseMs * 0.86)); // 1.2.1锛氶樆灏肩暐闄嶃€佸垰搴︾暐鍗?-> 鍥炲脊鏇存湁寮规€?
        }
    }
    private void AddAnim(Storyboard sb, DependencyObject target, DependencyProperty prop, double to, int ms, IEasingFunction easing, TimeSpan? beginTime = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = easing,
            BeginTime = beginTime ?? TimeSpan.Zero,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
        AnimationFrameRate.Apply(anim, _settings.Current.LowPowerMode); // 120 FPS 鐩爣甯х巼
        sb.Children.Add(anim);
    }

    // 鈹€鈹€ 瀹氫綅 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    public void Reposition()
    {
        if (!IsLoaded) return;
        var s = _settings.Current;
        if (s.IslandManualLeft is double ml && s.IslandManualTop is double mt)
        {
            // 鎵嬪姩瀹氫綅锛氬彧鍦ㄧ獥鍙ｆ瘮宸ヤ綔鍖哄皬鏃跺仛瓒婄晫淇濇姢锛岄伩鍏嶈创杈?灞呬腑鍚庤嚜鍔ㄥ脊鍥?
            var work = ScreenHelper.DpiWorkArea(_screen);
            var w = Math.Max(1.0, ActualWidth);
            var h = Math.Max(1.0, ActualHeight);
            if (w < work.Width) ml = Math.Clamp(ml, work.Left, work.Right - w);
            if (h < work.Height) mt = Math.Clamp(mt, work.Top, work.Bottom - h);
            Left = ml;
            Top = mt;
            ApplyCardAlignment();
            return;
        }
        var pos = ScreenHelper.ComputePosition(_screen, s.Position,
            ActualWidth, ActualHeight, s.OffsetX, s.OffsetY);
        Left = pos.X;
        Top = pos.Y;
        ApplyCardAlignment();
    }

    // 鈹€鈹€ 鎷栨枃浠朵笂宀?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
    private bool _dragHintOn;

    private void Card_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            ShowDragHint(true);
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Card_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ShowDragHint(false);
    }

    private void Card_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ShowDragHint(false);
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        // 鏂囦欢涓浆绔欙細鎷栧叆鐨勬枃浠惰繘鍏ョ粍浠讹紝鍙啀鎷栧嚭鍒板叾浠栧簲鐢?/ 璧勬簮绠＄悊鍣紙浠呭瓨璺緞寮曠敤锛?
        _vm.AddFilesToTransfer(files);
        e.Handled = true;
    }

    /// <summary>鎷栧叆鏂囦欢鏃剁敤寮鸿皟鑹查珮浜崱鐗囪竟妗嗭紙杞诲井娣″叆娣″嚭锛夈€?/summary>
    private void ShowDragHint(bool on)
    {
        if (_dragHintOn == on) return;
        _dragHintOn = on;
        try
        {
            // 涓婚鐢荤瑪宸?Freeze锛屾棤娉曠洿鎺?BeginAnimation锛涜繖閲屾柊寤烘湭鍐荤粨鐢荤瑪鍋氶鑹叉笎鍙?
            var accent = (_theme.AccentBorderBrush as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromArgb(160, 108, 92, 231);
            var card = (_theme.CardBorder as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromArgb(60, 255, 255, 255);
            var brush = new SolidColorBrush(on ? card : accent);
            var anim = new ColorAnimation(on ? accent : card, TimeSpan.FromMilliseconds(200));
            AnimationFrameRate.Apply(anim, _settings.Current.LowPowerMode);
            if (!on)
            {
                // 杩樺師涓婚缁戝畾蹇呴』绛夐鑹插姩鐢绘挱瀹屽啀鎵ц锛歋etBinding 浼氱珛鍒绘浛鎹?BorderBrush 鐨勬湰鍦板€硷紝
                // 鑻ュ湪姝ゅ鐩存帴璋冪敤浼氭妸鍒氳捣姝ョ殑 200ms 杩囨浮鍔ㄧ敾鎴柇锛岃竟妗嗛鑹蹭細鐬棿璺冲彉銆?
                anim.Completed += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_dragHintOn) // 鏈熼棿鍙堣鎷栧叆锛坥n=true锛夊垯涓嶆仮澶嶏紝浜ょ敱涓嬩竴娆″姩鐢诲鐞?
                        Card.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(CardBorder))
                        {
                            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1),
                        });
                }));
            }
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            Card.BorderBrush = brush;
        }
        catch { /* 鍔ㄧ敾澶辫触蹇界暐 */ }
    }

    // 鈹€鈹€ 鏂囦欢涓浆绔欙細鎶婁腑杞枃浠舵嫋鍑哄埌鍏朵粬搴旂敤 / 璧勬簮绠＄悊鍣?鈹€鈹€
    private bool _fileDragArmed;
    private Point _fileDownPoint;

    private void FileTransferItem_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _fileDragArmed = true;
        _fileDownPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private void FileTransferItem_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_fileDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _fileDownPoint.X) <= 4 && Math.Abs(pos.Y - _fileDownPoint.Y) <= 4) return;
        _fileDragArmed = false;
        var paths = _vm.FileTransferItems.Select(f => f.Path).ToArray();
        if (paths.Length == 0) return;
        try
        {
            // 鐢ㄧ湡瀹炶矾寰勫彂璧风郴缁熸嫋鏀撅紙澶嶅埗璇箟锛夛紝婧愭枃浠朵笉浼氳绉诲姩鎴栧垹闄?
            var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, paths);
            System.Windows.DragDrop.DoDragDrop(sender is DependencyObject d ? d : Card, data,
                System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
        }
        catch { /* 鐢ㄦ埛鍙栨秷鎷栨斁绛?*/ }
        e.Handled = true;
    }

    private void FileTransferItem_Up(object sender, MouseButtonEventArgs e)
    {
        _fileDragArmed = false;
        e.Handled = true;
    }

    /// <summary>鐐瑰嚮鏂囦欢涓浆缁勪欢涓婄殑銆屆椼€嶏細娓呯┖涓浆绔欍€?/summary>
    private void FileTransferClear_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearFileTransfer();
        e.Handled = true;
    }

    // 鈹€鈹€ 鎷栧姩瀹氫綅锛氬惛闄?+ 鎸佷箙鍖?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    /// <summary>鎷栧姩鏉炬墜鍚庯細鑷姩鍚搁檮灞忓箷杈圭紭/灞呬腑锛屽苟鎶婁綅缃啓鍏ヨ缃紙涓婇攣/閲嶅惎鍚庝繚鎸侊級銆?/summary>
    private void SnapAndPersistPosition()
    {
        if (!IsLoaded) return;
        var s = _settings.Current;
        var work = ScreenHelper.DpiWorkArea(_screen);
        var w = Math.Max(1.0, ActualWidth);
        var h = Math.Max(1.0, ActualHeight);

        var left = Left;
        var top = Top;

        if (s.EdgeSnapEnabled)
        {
            const double snap = 56; // 鍚搁檮闃堝€硷紙DIP锛?
            left = SnapTo(left, new[] { work.Left, work.Left + (work.Width - w) / 2, work.Right - w }, snap);
            top = SnapTo(top, new[] { work.Top, work.Bottom - h }, snap);
        }

        // 瓒婄晫淇濇姢锛氱獥鍙ｆ瘮宸ヤ綔鍖哄皬鏃舵墠澶圭揣锛岄伩鍏嶅鏄剧ず鍣ㄨ礋鍧愭爣澶辨晥
        if (w < work.Width) left = Math.Clamp(left, work.Left, work.Right - w);
        if (h < work.Height) top = Math.Clamp(top, work.Top, work.Bottom - h);

        _settings.Update(s2 =>
        {
            s2.IslandManualLeft = left;
            s2.IslandManualTop = top;
        });
        AnimatePosition(left, top);
    }

    private static double SnapTo(double value, double[] targets, double threshold)
    {
        foreach (var t in targets)
        {
            if (Math.Abs(value - t) <= threshold) return t;
        }
        return value;
    }

    /// <summary>绐楀彛绉诲姩鐢ㄩ潪绾挎€х紦鍔ㄥ姩鐢伙紙涓嶇灛绉伙紝涓濇粦杩囨浮锛夈€?/summary>
    private void AnimatePosition(double left, double top)
    {
        if (!IsLoaded) return;
        if (Math.Abs(Left - left) < 0.5 && Math.Abs(Top - top) < 0.5) return;
        if (_settings.Current.ReduceMotion)
        {
            Left = left;
            Top = top;
            return;
        }
        _positionStoryboard?.Stop(); // 杩炵画閲嶅畾浣嶅厛鍋滄棫鍔ㄧ敾锛岄伩鍏嶅苟鍙戞姈鍔?
        var easing = CachedCubicEaseOut;
        var sb = new Storyboard();
        AddAnim(sb, this, Window.LeftProperty, left, 320, easing);
        AddAnim(sb, this, Window.TopProperty, top, 320, easing);
        sb.Completed += (_, _) => { if (ReferenceEquals(_positionStoryboard, sb)) _positionStoryboard = null; };
        AnimationFrameRate.Apply(sb, _settings.Current.LowPowerMode);
        _positionStoryboard = sb;
        sb.Begin();
    }

    // 鈹€鈹€ 姝岃瘝鑷姩婊氬姩 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    private bool _lyricsScrollQueued;
    private bool _lyricsScrollRendering;
    private double _lyricsScrollTarget;
    private double _lyricsScrollFrom;      // 鏈婊氬姩璧风偣鍋忕Щ锛堟椂闂村熀鍑嗙紦鍔ㄧ敤锛?
    private DateTime _lyricsScrollStartUtc; // 鏈婊氬姩璧峰澧欓挓
    private const double LyricsScrollMs = 420; // 鍗曟婊氬姩鏃堕暱锛堟绉掞級锛?0fps / 120Hz 涓嬪潎涓€鑷?

    private void QueueLyricsScroll(int index)
    {
        if (!IsLoaded || !IsVisible || !_vm.IsExpanded) return;
        if (_lyricsScrollQueued) return;
        _lyricsScrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _lyricsScrollQueued = false;
            // 鎵ц鏃跺彇鏈€鏂扮储寮曪細蹇€熷垏鍙ユ椂鎺掗槦涓殑鏃х储寮曚細琚渶鏂板彞瑕嗙洊锛屾粴鍔ㄥ缁堣窡闅忓綋鍓嶅彞
            var current = _vm.LyricIndex >= 0 ? _vm.LyricIndex : index;
            ScrollLyricsTo(current);
        }, DispatcherPriority.Loaded);
    }

    private void ScrollLyricsTo(int index)
    {
        if (LyricsList.Items.Count == 0) return;
        if (!_vm.IsExpanded || !IsVisible || !IsLoaded) { StopLyricsScroll(); return; } // 浠呭湪灞曞紑涓斿彲瑙佹椂婊氬姩锛岄伩鍏嶇┖杞?
        index = Math.Clamp(index, 0, LyricsList.Items.Count - 1);
        var container = LyricsList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        if (container is null) return;

        var viewer = LyricsScroll;
        var relY = container.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        // 瑙嗗彛鐩稿鍧愭爣 + 褰撳墠鍋忕Щ = 鍐呭鍧愭爣锛涘啀鍑忓幓鍗婁釜瑙嗗彛/鍔犱笂鍗婁釜琛岄珮浣垮綋鍓嶅彞灞呬腑
        var target = viewer.VerticalOffset + relY - viewer.ViewportHeight / 2 + container.ActualHeight / 2;
        target = Math.Max(0, target);

        // 鐩爣涓庡綋鍓嶅崄鍒嗘帴杩戯細鐩存帴钀戒綅锛屼笉鍐嶅惎鍔ㄧ敾锛堥伩鍏嶉珮棰戝垏鍙ユ椂鎶栧姩锛?
        if (Math.Abs(target - viewer.VerticalOffset) < 0.5)
        {
            StopLyricsScroll();
            return;
        }
        _lyricsScrollTarget = target;
        _lyricsScrollFrom = viewer.VerticalOffset;
        _lyricsScrollStartUtc = DateTime.UtcNow;
        StartLyricsScroll();
    }

    /// <summary>
    /// 骞虫粦婊氬姩锛氭椂闂村熀鍑嗕笁娆＄紦鍑猴紙涓庡抚鐜囨棤鍏筹紝60fps / 120Hz 鏄剧ず鍣ㄨ〃鐜颁竴鑷淬€佷笣婊戣繛璐級銆?
    /// 蹇€熻繛缁垏鍙ユ椂浠ユ渶杩戜竴娆＄洰鏍囬噸鏂拌捣绠楋紝涓嶄細鈥滀竴鍔ㄤ竴鍋溾€濄€?
    /// </summary>
    /// <summary>歌词滚动改为合成帧事件驱动，与显示器刷新率同步。</summary>
    private void StartLyricsScroll()
    {
        if (_lyricsScrollRendering) return;
        _lyricsScrollRendering = true;
        System.Windows.Media.CompositionTarget.Rendering += OnLyricsScrollRendering;
    }

    private void StopLyricsScroll()
    {
        if (!_lyricsScrollRendering) return;
        _lyricsScrollRendering = false;
        System.Windows.Media.CompositionTarget.Rendering -= OnLyricsScrollRendering;
    }

    private void OnLyricsScrollRendering(object? sender, EventArgs e) => SmoothScrollStep();
    private void SmoothScrollStep()
    {
        if (!_vm.IsExpanded || !IsVisible || !IsLoaded || LyricsList.Items.Count == 0)
        {
            StopLyricsScroll();
            return;
        }
        var viewer = LyricsScroll;
        var elapsed = (DateTime.UtcNow - _lyricsScrollStartUtc).TotalMilliseconds;
        var t = Math.Clamp(elapsed / LyricsScrollMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3); // 涓夋缂撳嚭锛氬厛蹇悗鎱€佹敹灏炬煍鍜?
        var offset = _lyricsScrollFrom + (_lyricsScrollTarget - _lyricsScrollFrom) * eased;
        viewer.ScrollToVerticalOffset(offset);
        if (t >= 1)
        {
            viewer.ScrollToVerticalOffset(_lyricsScrollTarget); // 绮剧‘钀戒綅锛屾秷闄ょ疮璁¤宸?
            StopLyricsScroll();
        }
    }
}


