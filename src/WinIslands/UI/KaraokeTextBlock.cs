using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WinIslands.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace WinIslands.UI;

/// <summary>
/// 逐字卡拉OK歌词控件（带平滑过渡动画，120fps）。
/// 两种模式：
///  1. 逐字模式（有 <see cref="Words"/>，来自 AMLL TTML）：每个字/词按各自独立起止时间
///     从左到右点亮（字间带交叉过渡的缓动曲线，动画连贯不顿挫），控件内部按墙钟在两次位置更新之间连续推进；
///  2. 整行均分模式（无 Words，兜底）：按 <see cref="HighlightFraction"/> 比例把字符均匀点亮。
/// 暂停/启动恢复时保持「暂停时刻」的高亮不动；播放中换句时从 0 开始，第一个字先不亮。
/// </summary>
public class KaraokeTextBlock : TextBlock
{
    public static readonly DependencyProperty KaraokeTextProperty =
        DependencyProperty.Register(nameof(KaraokeText), typeof(string), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnRenderPropsChanged));

    /// <summary>目标高亮比例 0..1（连续值；仅在无逐字时间轴时使用）。</summary>
    public static readonly DependencyProperty HighlightFractionProperty =
        DependencyProperty.Register(nameof(HighlightFraction), typeof(double), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(0.0, OnRenderPropsChanged));

    /// <summary>逐字时间轴（每字/词的绝对起止秒）。非空时启用真正的逐字卡拉OK。</summary>
    public static readonly DependencyProperty WordsProperty =
        DependencyProperty.Register(nameof(Words), typeof(IReadOnlyList<TtmlWord>), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(null, OnRenderPropsChanged));

    /// <summary>当前播放位置（秒，绝对时间，含歌词偏移；由 ViewModel 以约 5Hz 更新，控件内部按墙钟按显示器刷新率连续推进）。</summary>
    public static readonly DependencyProperty PositionSecondsProperty =
        DependencyProperty.Register(nameof(PositionSeconds), typeof(double), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(0.0, (d, e) => ((KaraokeTextBlock)d).OnPositionChanged((double)e.NewValue)));

    public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.Register(nameof(HighlightBrush), typeof(Brush), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(Brushes.White, OnRenderPropsChanged));

    public static readonly DependencyProperty BaseBrushProperty =
        DependencyProperty.Register(nameof(BaseBrush), typeof(Brush), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(Brushes.Gray, OnRenderPropsChanged));

    /// <summary>是否正在播放：播放中逐字推进；暂停/启动恢复时保持暂停时刻的高亮。</summary>
    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(false, OnRenderPropsChanged));
    /// <summary>卡拉OK推进速度倍率（1.0 = 标准，0.5~2.0；仅影响播放中的推进速度）。</summary>
    public static readonly DependencyProperty KaraokeSpeedProperty =
        DependencyProperty.Register(nameof(KaraokeSpeed), typeof(double), typeof(KaraokeTextBlock),
            new FrameworkPropertyMetadata(1.0, OnRenderPropsChanged));

    /// <summary>换句时淡入过渡（2.1.2）：歌词切句不再是硬切，用 170ms 淡入平滑浮现（仅紧凑胶囊使用；不碰 RenderTransform，避免与跑马灯冲突）。</summary>
    public static readonly DependencyProperty EntranceFadeEnabledProperty =
        DependencyProperty.Register(nameof(EntranceFadeEnabled), typeof(bool), typeof(KaraokeTextBlock),
            new PropertyMetadata(false));
    public bool EntranceFadeEnabled
    {
        get => (bool)GetValue(EntranceFadeEnabledProperty);
        set => SetValue(EntranceFadeEnabledProperty, value);
    }

    private bool _renderingSubscribed;     // CompositionTarget.Rendering 已挂接
    private double _lastTickTime;          // 上一帧时间（秒），用于帧率无关平滑
    private readonly Stopwatch _tickClock = Stopwatch.StartNew();
    private double _currentFraction;   // 当前已点亮比例（0..1，整行均分模式平滑推进）
    private double _targetFraction;    // 目标比例（0..1，来自 HighlightFraction）
    private string _lastText = string.Empty;
    private int _lastLitChars = -1;
    // 整行均分模式：缓存 3 个 Run（同一句内只更新 Foreground，仅重绘、不触发布局，换句才重建）
    private Run? _litRun;
    private Run? _blendRun;
    private Run? _restRun;
    private SolidColorBrush? _litBrush;
    private SolidColorBrush? _blendBrush;
    private SolidColorBrush? _restBrush;

    // 逐字模式状态
    private IReadOnlyList<TtmlWord> _words = Array.Empty<TtmlWord>();
    private IReadOnlyList<TtmlWord> _renderedWords = Array.Empty<TtmlWord>();
    private readonly List<Run> _wordRuns = new();
    private double[] _wordStarts = Array.Empty<double>();
    private double[] _wordDenoms = Array.Empty<double>();
    // 2.1.3：逐字渲染性能优化——已点亮/未点亮的字共享冻结画刷，只对「正在过渡」的字逐帧算色。
    // 每字用阶段标记（0=未点亮共享底刷、1=过渡中独立刷、2=已点亮共享高亮刷），
    // 仅在阶段切换或颜色字节变化时才写 Run.Foreground，避免每帧对整行做 Color.FromArgb + SmoothStep。
    private byte[] _wordPhase = Array.Empty<byte>();
    private SolidColorBrush? _sharedHighlightBrush;
    private SolidColorBrush? _sharedBaseBrush;
    private System.Windows.Media.Color _sharedHlColor;
    private System.Windows.Media.Color _sharedBaseColor;
    private bool _hasWords;
    private double _karaokeSpeedScale = 1.0;
    private double _nextKaraokeFrameTime;
    private double _posBase;            // 最近一次来自 ViewModel 的位置（秒）
    private long _posBaseTicks;          // 该位置对应的单调时钟刻度
    /// <summary>墙钟插值仅用于补充两次 ViewModel 位置更新（约 200ms）之间的间隙；超过该上限视为 ViewModel 已停更（切歌/暂停边缘/恢复首帧），不再外推，避免歌词漂移到句尾再跳回。</summary>
    private const double MaxWallClockLeadSeconds = 0.5;
    /// <summary>播放器位置报告与墙钟外推基本一致的上限（秒）：不超过该值直接采用新位置。</summary>
    private const double PositionSyncThresholdSeconds = 0.30;
    /// <summary>偏差超过该值视为真实 seek/切歌/暂停恢复：直接硬同步，保证准确。</summary>
    private const double PositionHardSyncThresholdSeconds = 0.80;
    /// <summary>中等偏差（SMTC/本地 API 上报量化、滞后）每次上报的收敛比例：0.5 = 每 200ms 收敛一半残余偏差，约两次平滑到位，肉眼无回跳。</summary>
    private const double PositionCorrectionGain = 0.5;

    public KaraokeTextBlock()
    {
        // 120fps：使用 CompositionTarget.Rendering（跟随显示器刷新率），不再用 DispatcherTimer
        Unloaded += (_, _) => StopAnimation();
        Loaded += (_, _) =>
        {
            if (_hasWords && IsPlaying && IsVisible && !_renderingSubscribed) StartAnimation();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { StopAnimation(); return; }
            if (_hasWords && IsPlaying)
            {
                _posBaseTicks = _tickClock.ElapsedTicks; // 重新可见：立即校准墙钟基准（避免位置跳变）
                if (!_renderingSubscribed) StartAnimation();
            }
            else RefreshTarget();
        };
    }

    public string KaraokeText
    {
        get => (string)GetValue(KaraokeTextProperty);
        set => SetValue(KaraokeTextProperty, value);
    }

    public double HighlightFraction
    {
        get => (double)GetValue(HighlightFractionProperty);
        set => SetValue(HighlightFractionProperty, value);
    }

    public IReadOnlyList<TtmlWord>? Words
    {
        get => (IReadOnlyList<TtmlWord>?)GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public double PositionSeconds
    {
        get => (double)GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }

    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public Brush BaseBrush
    {
        get => (Brush)GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>卡拉OK推进速度倍率（1.0 = 标准）。</summary>
    public double KaraokeSpeed
    {
        get => (double)GetValue(KaraokeSpeedProperty);
        set => SetValue(KaraokeSpeedProperty, value);
    }

    private bool _entranceInitialized;      // 首次赋文本不淡入（避免启动闪烁）
    // 2.1.3：换句淡入改用柔和阻尼弹簧（先快后缓、轻微 Q 弹），比固定 Cubic 更接近 iOS 文字揭示的质感
    private static readonly SoftSpringEase EntranceEase = CreateEntranceEase();

    private static SoftSpringEase CreateEntranceEase()
    {
        var e = new SoftSpringEase { Damping = 15, Stiffness = 210, Mass = 1 };
        e.Freeze();
        return e;
    }

    /// <summary>换句淡入：新句从 0 快速淡入到 1，Old→New 之间没有硬切跳变。</summary>
    private void OnKaraokeTextChanged(string? oldText, string? newText)
    {
        if (!EntranceFadeEnabled)
        {
            Opacity = 1;
            _entranceInitialized = true;
            return;
        }
        if (string.Equals(oldText ?? string.Empty, newText ?? string.Empty, StringComparison.Ordinal))
            return;
        if (!_entranceInitialized)
        {
            Opacity = 1;
            _entranceInitialized = true;   // 首次绑定：保持可见，不淡入
            return;
        }
        if (!IsLoaded || !IsVisible)
        {
            Opacity = 1;                   // 隐藏/未加载时不淡入，避免残留 0 透明度
            return;
        }
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = EntranceEase,
        };
        anim.Completed += (_, _) =>
        {
            Opacity = 1;
            BeginAnimation(OpacityProperty, null);   // 摘除动画，恢复静态 1
        };
        BeginAnimation(OpacityProperty, anim);
    }
    private void OnPositionChanged(double pos)
    {
        // 2.0.6 位置平滑校正：播放中且正在逐帧渲染时，播放器上报的进度常有量化/滞后
        // （如整秒取整、SMTC 缓存），直接硬切会每 200ms 肉眼可见地回跳；
        // 按偏差大小分级处理：基本一致→直接采用；中等偏差→按增益平滑收敛；大偏差
        // （seek/切歌/暂停恢复）→硬同步，保证点击进度条后立即准确。
        if (IsPlaying && _hasWords && _renderingSubscribed && IsVisible)
        {
            var elapsed = (double)(_tickClock.ElapsedTicks - _posBaseTicks) / Stopwatch.Frequency;
            var extrapolated = _posBase + Math.Min(Math.Max(elapsed, 0), MaxWallClockLeadSeconds);
            var delta = pos - extrapolated;
            if (Math.Abs(delta) > PositionHardSyncThresholdSeconds)
            {
                _posBase = pos;                                              // 大偏差：硬同步（seek/切歌/暂停恢复）
            }
            else if (Math.Abs(delta) > PositionSyncThresholdSeconds)
            {
                _posBase = extrapolated + delta * PositionCorrectionGain;    // 中等偏差：平滑收敛不跳变
            }
            else
            {
                _posBase = pos;                                              // 基本一致：直接采用
            }
            _posBaseTicks = _tickClock.ElapsedTicks;
        }
        else
        {
            _posBase = pos;
            _posBaseTicks = _tickClock.ElapsedTicks;
        }
        // 位置更新可能来自 seek/恢复：立即重绘一次；隐藏时不启动定时器（避免空转耗 CPU）
        if (!_hasWords || !IsVisible) return;
        if (IsPlaying)
        {
            // 仅当播放位置落在这句的逐字时间轴范围内才需要动画；
            // 完全未开始/已结束的行渲染一次后停止（展开列表每行都是本控件，避免动画风暴）
            if (NeedsAnimation(pos))
            {
                if (!_renderingSubscribed) StartAnimation();
            }
            else
            {
                StopAnimation();
                RenderWords(pos);
            }
        }
        else
        {
            RenderWords(_posBase);
            StopAnimation();
        }
    }

    private static void OnRenderPropsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var k = (KaraokeTextBlock)d;
        if (e.Property == KaraokeTextProperty)
            k.OnKaraokeTextChanged((string?)e.OldValue, (string?)e.NewValue);
        k.RefreshTarget();
    }

    private void RefreshTarget()
    {
        _karaokeSpeedScale = Math.Clamp(KaraokeSpeed <= 0 ? 1.0 : KaraokeSpeed, 0.2, 3.0);
        var words = (IReadOnlyList<TtmlWord>?)GetValue(WordsProperty);
        _hasWords = words is { Count: > 0 };
        if (_hasWords && !ReferenceEquals(words, _words))
        {
            _words = words!;
            _renderedWords = Array.Empty<TtmlWord>(); // 强制重建 Run
            _wordRuns.Clear();
            _wordStarts = new double[_words.Count];
            _wordDenoms = new double[_words.Count];
            BuildWordTimeline(_words, _wordStarts, _wordDenoms);
        }

        if (_hasWords)
        {
            // 逐字模式：播放中由 CompositionTarget.Rendering 按墙钟连续推进；暂停/启动恢复直接按最近位置渲染
            if (IsPlaying)
            {
                if (NeedsAnimation(_posBase))
                {
                    if (!_renderingSubscribed) StartAnimation();
                }
                else
                {
                    StopAnimation();
                    RenderWords(_posBase);
                }
                return;
            }

            StopAnimation();
            RenderWords(_posBase);
            return;
        }

        var text = KaraokeText ?? string.Empty;
        _targetFraction = Math.Clamp(HighlightFraction, 0, 1);

        // 换行时从 0 开始：新句第一个字保持未点亮，随进度从左到右平滑点亮。
        if (!string.Equals(text, _lastText, StringComparison.Ordinal))
        {
            _lastText = text;
            if (IsPlaying)
            {
                _currentFraction = 0; // 播放中换句：从 0 开始，第一个字先不亮
                StartAnimation();
            }
            else
            {
                _currentFraction = _targetFraction; // 暂停/启动恢复换行：直接显示目标高亮
                StopAnimation();
            }
            Render();
            return;
        }

        if (Math.Abs(_currentFraction - _targetFraction) < 0.002)
        {
            _currentFraction = _targetFraction;
            StopAnimation();
            Render();
            return;
        }

        if (!_renderingSubscribed) StartAnimation();
    }

    /// <summary>挂接 CompositionTarget.Rendering（跟随显示器刷新率，120Hz 显示器上 120fps）。</summary>
    private void StartAnimation()
    {
        if (_renderingSubscribed || !IsVisible) return;
        _renderingSubscribed = true;
        _lastTickTime = _tickClock.Elapsed.TotalSeconds;
        _nextKaraokeFrameTime = _lastTickTime;
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    /// <summary>摘除 CompositionTarget.Rendering。</summary>
    private void StopAnimation()
    {
        if (!_renderingSubscribed) return;
        _renderingSubscribed = false;
        CompositionTarget.Rendering -= OnRenderingFrame;
    }

    private void OnRenderingFrame(object? sender, EventArgs e) => TickAnimation();

    private void TickAnimation()
    {
        if (_hasWords)
        {
            if (IsPlaying)
            {
                // 两次 ViewModel 位置更新之间按墙钟连续推进 → 帧率自适应丝滑，不“一动一停”
                // 按真实时间插值（不乘速度倍率）：ViewModel 每 200ms 用真实播放位置校正一次，
                // 若在此处乘倍率会产生「先超前、再被拉回」的每 200ms 回跳，看起来卡顿。
                // 「高亮更快」改为在 RenderWords 内缩放每个字的进度（见 speedScale），效果相同但不回跳。
                var now = _tickClock.Elapsed.TotalSeconds;
                var fps = AnimationFrameRate.Current(lowPowerMode: false);
                if (!AnimationFrameRate.ShouldProcessFrame(now, ref _nextKaraokeFrameTime, fps)) return;
                var elapsed = (double)(_tickClock.ElapsedTicks - _posBaseTicks) / Stopwatch.Frequency;
                var pos = ClampWallClockLead(_posBase, elapsed);
                RenderWords(pos);
                // 该行已全部点亮/尚未开始：静态即可，停止动画（避免列表里多行同时空转）
                if (!NeedsAnimation(pos)) StopAnimation();
            }
            else
            {
                StopAnimation();
                RenderWords(_posBase);
            }
            return;
        }

        // 整行均分模式：缓动逼近（差距大时走得快、接近时变慢）
        var nowTick = _tickClock.Elapsed.TotalSeconds;
        var dtTick = Math.Min(0.05, Math.Max(0.001, nowTick - _lastTickTime));
        _lastTickTime = nowTick;
        // 帧率无关指数平滑：rate=42 在 60fps 下等效于旧的 0.5 系数，120fps 下自动适配
        var lerpAlpha = 1.0 - Math.Exp(-dtTick * 42.0);
        _currentFraction += (_targetFraction - _currentFraction) * lerpAlpha;
        if (Math.Abs(_currentFraction - _targetFraction) < 0.002)
        {
            _currentFraction = _targetFraction;
            StopAnimation();
        }
        Render();
    }

    private void RenderWords(double pos)
    {
        var text = KaraokeText ?? string.Empty;
        if (_words.Count == 0) return;

        // 换句/首次时重建每个字的 Run（文本变化才触发布局）；随后仅更新颜色（只重绘不布局）
        if (!ReferenceEquals(_renderedWords, _words) || _wordRuns.Count != _words.Count)
        {
            _wordRuns.Clear();
            for (var i = 0; i < _words.Count; i++)
            {
                _wordRuns.Add(new Run { Text = _words[i].Text });
            }
            Inlines.Clear();
            foreach (var r in _wordRuns) Inlines.Add(r);
            _renderedWords = _words;
            if (_wordPhase.Length != _wordRuns.Count) _wordPhase = new byte[_wordRuns.Count];
            else Array.Clear(_wordPhase, 0, _wordPhase.Length); // 重建后阶段归零（全部未点亮），避免旧阶段误导共享刷切换
        }

        var hl = ToColor(HighlightBrush) ?? System.Windows.Media.Colors.White;
        var bs = ToColor(BaseBrush) ?? System.Windows.Media.Colors.Gray;
        // 2.1.3：已点亮/未点亮分享冻结画刷；颜色变化（如主题切换）时重建并复位阶段，
        // 让已点亮/未点亮的字重新指向新画刷，避免残留旧主题色。
        if (EnsureSharedBrushes(hl, bs))
        {
            for (var i = 0; i < _wordPhase.Length; i++)
                if (_wordPhase[i] != 1) _wordPhase[i] = 0;
        }

        // 字间交叉过渡：后续字在其开始前约 45ms 提前起笔，前一字在结束后同样微延收笔，
        // 两段缓动曲线首尾重叠 → 高亮像光带一样从左到右“流动”，不会在字边界停一下再动一下；
        // 句首第一个字不提前，保证换句时第一个字保持未点亮。
        // 卡拉OK速度倍率：作用在每个字的填充进度上（而非时间轴），因此不会与位置校正互相拉扯。
        var speedScale = _karaokeSpeedScale;
        var deltaA = hl.A - bs.A;
        var deltaR = hl.R - bs.R;
        var deltaG = hl.G - bs.G;
        var deltaB = hl.B - bs.B;
        var count = Math.Min(_wordRuns.Count, _words.Count);
        for (var i = 0; i < count; i++)
        {
            // 2.1.3：先按词阶段分支——已点亮/未点亮的字直接切共享冻结刷（且仅在阶段切换时才写），
            // 不再每帧对全行计算 SmoothStep + Color.FromArgb；只有正在过渡的 1~2 个字才逐帧混色。
            var start = _wordStarts[i];
            var end = start + _wordDenoms[i] / speedScale;
            var run = _wordRuns[i];
            if (pos >= end)
            {
                if (_wordPhase[i] != 2) { run.Foreground = _sharedHighlightBrush; _wordPhase[i] = 2; }
                continue;
            }
            if (pos < start)
            {
                if (_wordPhase[i] != 0) { run.Foreground = _sharedBaseBrush; _wordPhase[i] = 0; }
                continue;
            }
            // 正在过渡：ease-in-out（起笔/收笔有加减速）+字间 lead 重叠
            var raw = (pos - start) / _wordDenoms[i] * speedScale;
            var frac = SmoothStep(raw);
            var c = System.Windows.Media.Color.FromArgb(
                (byte)(bs.A + deltaA * frac),
                (byte)(bs.R + deltaR * frac),
                (byte)(bs.G + deltaG * frac),
                (byte)(bs.B + deltaB * frac));
            if (_wordPhase[i] != 1)
            {
                // 进入过渡：从共享刷切换为独立刷（不可以改共享冻结刷）
                run.Foreground = new SolidColorBrush(c);
                _wordPhase[i] = 1;
            }
            else if (run.Foreground is SolidColorBrush brush)
            {
                if (!ColorEqual(brush.Color, c)) brush.Color = c;
            }
            else
            {
                run.Foreground = new SolidColorBrush(c);
            }
        }
    }

    /// <summary>2.1.3：确保共享冻结画刷与当前高亮/底色一致（颜色变化时重建，执行中几乎不发生）。</summary>
    private bool EnsureSharedBrushes(System.Windows.Media.Color hl, System.Windows.Media.Color bs)
    {
        var changed = false;
        if (_sharedHighlightBrush is null || !ColorEqual(_sharedHlColor, hl))
        {
            _sharedHlColor = hl;
            _sharedHighlightBrush = new SolidColorBrush(hl);
            _sharedHighlightBrush.Freeze();
            changed = true;
        }
        if (_sharedBaseBrush is null || !ColorEqual(_sharedBaseColor, bs))
        {
            _sharedBaseColor = bs;
            _sharedBaseBrush = new SolidColorBrush(bs);
            _sharedBaseBrush.Freeze();
            changed = true;
        }
        return changed;
    }

    private void Render()
    {
        var text = KaraokeText ?? string.Empty;

        // 空文本：清空 Inlines（同时释放缓存的 Run）
        if (text.Length == 0)
        {
            if (Inlines.Count > 0)
            {
                Inlines.Clear();
                _litRun = _blendRun = _restRun = null;
            }
            return;
        }

        var f = Math.Clamp(_currentFraction, 0, 1);
        var hl = ToColor(HighlightBrush) ?? System.Windows.Media.Colors.White;
        var bs = ToColor(BaseBrush) ?? System.Windows.Media.Colors.Gray;

        // 按字符着色（而非二维渐变）：换行时高亮按阅读顺序从左到右逐行流动
        var len = text.Length;
        var litChars = Math.Min((int)Math.Floor(f * len), len);
        var blend = f * len - litChars;
        if (litChars >= len) blend = 1;

        if (_litRun is null || !string.Equals(_lastText, text, StringComparison.Ordinal))
        {
            _lastText = text;
            _lastLitChars = -1;
            _litBrush = new SolidColorBrush(hl);
            _blendBrush = new SolidColorBrush(bs);
            _restBrush = new SolidColorBrush(bs);
            _litRun = new Run { Foreground = _litBrush };
            _blendRun = new Run { Foreground = _blendBrush };
            _restRun = new Run { Foreground = _restBrush };
            Inlines.Clear();
            Inlines.Add(_litRun);
            Inlines.Add(_blendRun);
            Inlines.Add(_restRun);
        }

        if (_lastLitChars != litChars)
        {
            _lastLitChars = litChars;
            _litRun.Text = litChars > 0 ? text.Substring(0, litChars) : string.Empty;
            _blendRun!.Text = litChars < len ? text[litChars].ToString() : string.Empty;
            _restRun!.Text = litChars + 1 < len ? text.Substring(litChars + 1) : string.Empty;
        }

        if (!ColorEqual(_litBrush!.Color, hl)) _litBrush.Color = hl;
        var blendColor = litChars < len ? Lerp(bs, hl, Math.Clamp(blend, 0, 1)) : bs;
        if (!ColorEqual(_blendBrush!.Color, blendColor)) _blendBrush.Color = blendColor;
        if (!ColorEqual(_restBrush!.Color, bs)) _restBrush.Color = bs;
    }

    /// <summary>播放位置是否落在本句某个字的起止区间内（该行是否处于正在点亮的状态）。</summary>
    private bool NeedsAnimation(double pos)
        => NeedsAnimationFor(pos, _wordStarts, _wordDenoms, _karaokeSpeedScale);

    private static System.Windows.Media.SolidColorBrush Frozen(System.Windows.Media.SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>ease-in-out 缓动（smoothstep）：起笔慢→中段快→收笔慢，配合字间交叉过渡实现丝滑连贯的逐字推进。</summary>
    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static System.Windows.Media.Color Lerp(System.Windows.Media.Color a, System.Windows.Media.Color b, double t)
        => System.Windows.Media.Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    private static System.Windows.Media.Color? ToColor(Brush? brush)
        => (brush as SolidColorBrush)?.Color;

    /// <summary>比较两个颜色是否完全一致（避免为不足 1 字节的色差重复分配画刷）。</summary>
    private static bool ColorEqual(System.Windows.Media.Color a, System.Windows.Media.Color b)
        => a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;

    // ── 时间轴纯函数（与渲染共用同一套 lead 校正数组，供单元测试直接验证）──

    /// <summary>按字间交叉过渡 lead 建立逐字时间轴（与 RenderWords 完全同源，含“句首第一字不提前”规则）。</summary>
    internal static void BuildWordTimeline(IReadOnlyList<TtmlWord> words, double[] starts, double[] denoms)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var duration = Math.Max(w.DurationSec, 0.001);
            var lead = i > 0 ? Math.Min(0.045, duration * 0.5) : 0.0;
            starts[i] = w.BeginSec - lead;
            denoms[i] = duration + lead;
        }
    }

    /// <summary>与 <see cref="RenderWords"/> 完全一致的点亮区间判定：某位置下该行是否仍处于逐字点亮中（含字间 lead 预亮区间）。</summary>
    internal static bool NeedsAnimationFor(double pos, double[] starts, double[] denoms, double speed)
    {
        for (var i = 0; i < starts.Length && i < denoms.Length; i++)
        {
            if (pos < starts[i]) continue;                                    // 尚未开始（含 lead 前）：静态即可
            if ((pos - starts[i]) / Math.Max(denoms[i], 0.001) * speed < 1) return true; // 仍在点亮：需要动画
        }
        return false;
    }

    /// <summary>墙钟外推限幅：只补足两次位置更新之间的间隙，绝不让歌词进度无限超前（防“漂到句尾再跳回”）。</summary>
    internal static double ClampWallClockLead(double posBase, double elapsedSeconds)
        => posBase + Math.Min(Math.Max(elapsedSeconds, 0), MaxWallClockLeadSeconds);
}
