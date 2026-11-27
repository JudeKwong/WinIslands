using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using WinIslands.Services;

namespace WinIslands.Mac.UI;

/// <summary>
/// 逐字卡拉OK歌词控件（Avalonia 移植版，带平滑过渡动画，跟随显示器刷新率）。
/// 两种模式：
///  1. 逐字模式（有 <see cref="Words"/>，来自 AMLL TTML）：每个字/词按各自独立起止时间
///     从左到右点亮（字间带交叉过渡的缓动曲线，动画连贯不顿挫），控件内部按墙钟在两次位置更新之间连续推进；
///  2. 整行均分模式（无 Words，兜底）：按 <see cref="HighlightFraction"/> 比例把字符均匀点亮。
/// 暂停/启动恢复时保持「暂停时刻」的高亮不动；播放中换句时从 0 开始，第一个字先不亮。
/// 帧回调使用 TopLevel.RequestAnimationFrame（WPF 的 CompositionTarget.Rendering 在 Avalonia 无对应事件）。
/// </summary>
public class KaraokeTextBlock : TextBlock
{
    public static readonly StyledProperty<string> KaraokeTextProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, string>(nameof(KaraokeText), string.Empty);

    /// <summary>目标高亮比例 0..1（连续值；仅在无逐字时间轴时使用）。</summary>
    public static readonly StyledProperty<double> HighlightFractionProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, double>(nameof(HighlightFraction));

    /// <summary>逐字时间轴（每字/词的绝对起止秒）。非空时启用真正的逐字卡拉OK。</summary>
    public static readonly StyledProperty<IReadOnlyList<TtmlWord>?> WordsProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, IReadOnlyList<TtmlWord>?>(nameof(Words));

    /// <summary>当前播放位置（秒，绝对时间，含歌词偏移；由 ViewModel 以约 5Hz 更新，控件内部按墙钟按帧连续推进）。</summary>
    public static readonly StyledProperty<double> PositionSecondsProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, double>(nameof(PositionSeconds));

    public static readonly StyledProperty<IBrush> HighlightBrushProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, IBrush>(nameof(HighlightBrush), Brushes.White);

    public static readonly StyledProperty<IBrush> BaseBrushProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, IBrush>(nameof(BaseBrush), Brushes.Gray);

    /// <summary>是否正在播放：播放中逐字推进；暂停/启动恢复时保持暂停时刻的高亮。</summary>
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, bool>(nameof(IsPlaying));

    /// <summary>卡拉OK推进速度倍率（1.0 = 标准，0.5~2.0；仅影响播放中的推进速度）。</summary>
    public static readonly StyledProperty<double> KaraokeSpeedProperty =
        AvaloniaProperty.Register<KaraokeTextBlock, double>(nameof(KaraokeSpeed), 1.0);

    private bool _frameLoopActive;      // 已排队下一帧回调
    private double _lastTickTime;       // 上一帧时间（秒），用于帧率无关平滑
    private readonly Stopwatch _tickClock = Stopwatch.StartNew();
    private double _currentFraction;    // 当前已点亮比例（0..1，整行均分模式平滑推进）
    private double _targetFraction;     // 目标比例（0..1，来自 HighlightFraction）
    private string _lastText = string.Empty;
    private int _lastLitChars = -1;
    // 整行均分模式：缓存 3 个 Run（同一句内只更新 Foreground，仅重绘、不触发布局，换句才重建）
    private Run? _litRun;
    private Run? _blendRun;
    private Run? _restRun;
    private Color _litColor = Colors.White;
    private Color _blendColor = Colors.Gray;
    private Color _restColor = Colors.Gray;

    // 逐字模式状态
    private IReadOnlyList<TtmlWord> _words = Array.Empty<TtmlWord>();
    private IReadOnlyList<TtmlWord>? _renderedWords;
    private readonly List<Run> _wordRuns = new();
    private double[] _wordStarts = Array.Empty<double>();
    private double[] _wordDenoms = Array.Empty<double>();
    private readonly List<Color> _wordColors = new();
    private bool _hasWords;
    private double _karaokeSpeedScale = 1.0;
    private double _posBase;            // 最近一次来自 ViewModel 的位置（秒）
    private long _posBaseTicks;         // 该位置对应的单调时钟刻度

    static KaraokeTextBlock()
    {
        AffectsMeasure<KaraokeTextBlock>(KaraokeTextProperty);
        KaraokeTextProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, _) => x.RefreshTarget());
        HighlightFractionProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, _) => x.RefreshTarget());
        WordsProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, _) => x.RefreshTarget());
        HighlightBrushProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, _) => x.RefreshTarget());
        BaseBrushProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, _) => x.RefreshTarget());
        IsPlayingProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, _) => x.RefreshTarget());
        KaraokeSpeedProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, _) => x.RefreshTarget());
        PositionSecondsProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, e) =>
            x.OnPositionChanged(e.NewValue is double d ? d : 0.0));
    }

    public KaraokeTextBlock()
    {
        AttachedToVisualTree += (_, _) =>
        {
            if (_hasWords && IsPlaying && IsVisible && !_frameLoopActive) StartAnimation();
        };
        DetachedFromVisualTree += (_, _) => StopAnimation();
        IsVisibleProperty.Changed.AddClassHandler<KaraokeTextBlock>((x, e) =>
        {
            if (e.NewValue is not bool visible) return;
            if (!visible) { x.StopAnimation(); return; }
            if (x._hasWords && x.IsPlaying)
            {
                x._posBaseTicks = x._tickClock.ElapsedTicks; // 重新可见：立即校准墙钟基准（避免位置跳变）
                if (!x._frameLoopActive) x.StartAnimation();
            }
            else
            {
                x.RefreshTarget();
            }
        });
    }

    public string KaraokeText
    {
        get => GetValue(KaraokeTextProperty);
        set => SetValue(KaraokeTextProperty, value);
    }

    public double HighlightFraction
    {
        get => GetValue(HighlightFractionProperty);
        set => SetValue(HighlightFractionProperty, value);
    }

    public IReadOnlyList<TtmlWord>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public double PositionSeconds
    {
        get => GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }

    public IBrush HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public IBrush BaseBrush
    {
        get => GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public double KaraokeSpeed
    {
        get => GetValue(KaraokeSpeedProperty);
        set => SetValue(KaraokeSpeedProperty, value);
    }

    private void OnPositionChanged(double pos)
    {
        _posBase = pos;
        _posBaseTicks = _tickClock.ElapsedTicks;
        // 位置更新可能来自 seek/恢复：立即重绘一次；隐藏时不启动动画（避免空转耗 CPU）
        if (!_hasWords || !IsVisible) return;
        if (IsPlaying)
        {
            if (NeedsAnimation(pos))
            {
                if (!_frameLoopActive) StartAnimation();
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

    private void RefreshTarget()
    {
        _karaokeSpeedScale = Math.Clamp(KaraokeSpeed <= 0 ? 1.0 : KaraokeSpeed, 0.2, 3.0);
        var words = Words;
        _hasWords = words is { Count: > 0 };
        if (_hasWords && !ReferenceEquals(words, _words))
        {
            _words = words!;
            _renderedWords = null; // 强制重建 Run
            _wordRuns.Clear();
            _wordColors.Clear();
            _wordStarts = new double[_words.Count];
            _wordDenoms = new double[_words.Count];
            for (var i = 0; i < _words.Count; i++)
            {
                var w = _words[i];
                var duration = Math.Max(w.DurationSec, 0.001);
                var lead = i > 0 ? Math.Min(0.045, duration * 0.5) : 0.0;
                _wordStarts[i] = w.BeginSec - lead;
                _wordDenoms[i] = duration + lead;
            }
        }

        if (_hasWords)
        {
            if (IsPlaying)
            {
                if (NeedsAnimation(_posBase))
                {
                    if (!_frameLoopActive) StartAnimation();
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

        if (!_frameLoopActive) StartAnimation();
    }

    /// <summary>启动逐帧推进（通过 TopLevel.RequestAnimationFrame 一帧一帧排队）。</summary>
    private void StartAnimation()
    {
        if (_frameLoopActive || !IsVisible) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _frameLoopActive = true;
        _lastTickTime = _tickClock.Elapsed.TotalSeconds;
        RequestFrame(top);
    }

    private void RequestFrame(TopLevel top)
    {
        top.RequestAnimationFrame(_ =>
        {
            if (!_frameLoopActive) return;
            TickAnimation();
            if (_frameLoopActive) RequestFrame(top);
        });
    }

    private void StopAnimation() => _frameLoopActive = false;

    private void TickAnimation()
    {
        if (_hasWords)
        {
            if (IsPlaying)
            {
                // 两次 ViewModel 位置更新之间按墙钟连续推进 → 帧率自适应丝滑，不"一动一停"
                // 按真实时间插值（不乘速度倍率）：ViewModel 每 200ms 用真实播放位置校正一次，
                // 若在此处乘倍率会产生「先超前、再被拉回」的每 200ms 回跳，看起来卡顿。
                // 「高亮更快」改为在 RenderWords 内缩放每个字的进度（见 speedScale），效果相同但不回跳。
                var pos = _posBase + (_tickClock.ElapsedTicks - _posBaseTicks) / (double)Stopwatch.Frequency;
                RenderWords(pos);
                // 该行已全部点亮/尚未开始：静态即可，停止动画（避免多行同时空转）
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
        // 帧率无关指数平滑：rate=42 在 60fps 与 120fps 下自动适配
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
        if (_words.Count == 0) return;
        var inlines = Inlines;
        if (inlines is null) return;

        // 换句/首次时重建每个字的 Run（文本变化才触发布局）；随后仅更新颜色（只重绘不布局）
        if (!ReferenceEquals(_renderedWords, _words) || _wordRuns.Count != _words.Count)
        {
            _wordRuns.Clear();
            _wordColors.Clear();
            for (var i = 0; i < _words.Count; i++)
            {
                _wordRuns.Add(new Run { Text = _words[i].Text });
                _wordColors.Add(Colors.Transparent);
            }
            inlines.Clear();
            foreach (var r in _wordRuns) inlines.Add(r);
            _renderedWords = _words;
        }

        var hl = ToColor(HighlightBrush);
        var bs = ToColor(BaseBrush);

        // 字间交叉过渡：后续字在其开始前约 45ms 提前起笔，前一字在结束后同样微延收笔，
        // 两段缓动曲线首尾重叠 → 高亮像光带一样从左到右"流动"，不会在字边界停一下再动一下；
        // 句首第一个字不提前，保证换句时第一个字保持未点亮。
        // 卡拉OK速度倍率：作用在每个字的填充进度上（而非时间轴），因此不会与位置校正互相拉扯。
        var speedScale = _karaokeSpeedScale;
        for (var i = 0; i < _wordRuns.Count && i < _words.Count; i++)
        {
            var raw = (pos - _wordStarts[i]) / _wordDenoms[i] * speedScale;
            var frac = SmoothStep(raw); // ease-in-out：起笔/收笔有加减速，匀速的机械感消失
            var c = Lerp(bs, hl, frac);
            // 只在颜色字节值真正变化时才分配画刷（高帧率下多数帧的色差不足 1 字节），避免 GC 抖动
            if (!ColorEqual(_wordColors[i], c))
            {
                _wordColors[i] = c;
                _wordRuns[i].Foreground = new SolidColorBrush(c);
            }
        }
    }

    private void Render()
    {
        var text = KaraokeText ?? string.Empty;
        var inlines = Inlines;
        if (inlines is null) return;

        // 空文本：清空 Inlines（同时释放缓存的 Run）
        if (text.Length == 0)
        {
            if (inlines.Count > 0)
            {
                inlines.Clear();
                _litRun = _blendRun = _restRun = null;
            }
            return;
        }

        var f = Math.Clamp(_currentFraction, 0, 1);
        var hl = ToColor(HighlightBrush);
        var bs = ToColor(BaseBrush);

        // 按字符着色（而非二维渐变）：换行时高亮按阅读顺序从左到右逐行流动
        var len = text.Length;
        var litChars = Math.Min((int)Math.Floor(f * len), len);
        var blend = f * len - litChars;
        if (litChars >= len) blend = 1;

        if (_litRun is null || !string.Equals(_lastText, text, StringComparison.Ordinal))
        {
            _lastText = text;
            _lastLitChars = -1;
            _litColor = _blendColor = _restColor = Colors.Transparent;
            _litRun = new Run { Foreground = new SolidColorBrush(hl) };
            _blendRun = new Run { Foreground = new SolidColorBrush(bs) };
            _restRun = new Run { Foreground = new SolidColorBrush(bs) };
            inlines.Clear();
            inlines.Add(_litRun);
            inlines.Add(_blendRun);
            inlines.Add(_restRun);
        }

        if (_lastLitChars != litChars)
        {
            _lastLitChars = litChars;
            _litRun!.Text = litChars > 0 ? text.Substring(0, litChars) : string.Empty;
            _blendRun!.Text = litChars < len ? text[litChars].ToString() : string.Empty;
            _restRun!.Text = litChars + 1 < len ? text.Substring(litChars + 1) : string.Empty;
        }

        if (!ColorEqual(_litColor, hl)) { _litColor = hl; _litRun!.Foreground = new SolidColorBrush(hl); }
        var blendColor = litChars < len ? Lerp(bs, hl, Math.Clamp(blend, 0, 1)) : bs;
        if (!ColorEqual(_blendColor, blendColor)) { _blendColor = blendColor; _blendRun!.Foreground = new SolidColorBrush(blendColor); }
        if (!ColorEqual(_restColor, bs)) { _restColor = bs; _restRun!.Foreground = new SolidColorBrush(bs); }
    }

    /// <summary>播放位置是否落在本句某个字的起止区间内（该行是否处于正在点亮的状态）。</summary>
    private bool NeedsAnimation(double pos)
    {
        var speed = _karaokeSpeedScale;
        foreach (var w in _words)
        {
            if (pos < w.BeginSec) continue;                    // 尚未开始：静态即可
            if ((pos - w.BeginSec) / w.DurationSec * speed < 1) return true; // 仍在点亮：需要动画
        }
        return false;
    }

    /// <summary>ease-in-out 缓动（smoothstep）：起笔慢→中段快→收笔慢，配合字间交叉过渡实现丝滑连贯的逐字推进。</summary>
    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static Color Lerp(Color a, Color b, double t)
        => Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    private static Color ToColor(IBrush? brush)
        => brush is ISolidColorBrush s ? s.Color : Colors.White;

    /// <summary>比较两个颜色是否完全一致（避免为不足 1 字节的色差重复分配画刷）。</summary>
    private static bool ColorEqual(Color a, Color b)
        => a.A == b.A && a.R == b.R && a.G == b.G && a.B == b.B;
}
