using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinIslands.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace WinIslands.UI;

/// <summary>
/// Apple Music ??????????????? + ???? + ???
/// ?????????????????????????????????????????
/// ????????????????? 60fps ?????????????????????
/// </summary>
public sealed class AppleMusicLyricLine : System.Windows.Controls.Control
{
    public static readonly DependencyProperty WordsProperty =
        DependencyProperty.Register(nameof(Words), typeof(IReadOnlyList<TtmlWord>), typeof(AppleMusicLyricLine),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((AppleMusicLyricLine)d).InvalidateLayout()));

    /// <summary>??????????????????</summary>
    public static readonly DependencyProperty PositionSecondsProperty =
        DependencyProperty.Register(nameof(PositionSeconds), typeof(double), typeof(AppleMusicLyricLine),
            new FrameworkPropertyMetadata(0.0, (d, e) => ((AppleMusicLyricLine)d).OnPositionChanged((double)e.NewValue)));

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(AppleMusicLyricLine),
            new FrameworkPropertyMetadata(false, (d, e) => ((AppleMusicLyricLine)d).OnPlayStateChanged((bool)e.NewValue)));

    /// <summary>??????????????????</summary>
    public static readonly DependencyProperty IsEmptyProperty =
        DependencyProperty.Register(nameof(IsEmpty), typeof(bool), typeof(AppleMusicLyricLine),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((AppleMusicLyricLine)d).InvalidateLayout()));

    /// <summary>?????????????</summary>
    public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.Register(nameof(HighlightBrush), typeof(Brush), typeof(AppleMusicLyricLine),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>???????1.0 = ?????????OK????</summary>
    public static readonly DependencyProperty KaraokeSpeedProperty =
        DependencyProperty.Register(nameof(KaraokeSpeed), typeof(double), typeof(AppleMusicLyricLine),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>?????Control ???? LineHeight???????????????</summary>
    public static readonly DependencyProperty LineHeightProperty =
        DependencyProperty.Register(nameof(LineHeight), typeof(double), typeof(AppleMusicLyricLine),
            new FrameworkPropertyMetadata(15.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((AppleMusicLyricLine)d).InvalidateLayout()));

    private const double WallClockLeadCap = 0.35;
    private const double StallStopSeconds = 0.8;
    private const double DefaultWidth = 184.0;

    private sealed class WordBox
    {
        public TtmlWord Word = null!;
        public FormattedText Base = null!;
        public FormattedText Highlight = null!;
        public double X;
        public double Y;
        public double Width;
    }

    private readonly List<WordBox> _boxes = new();
    private readonly DispatcherTimer _timer;
    private bool _layoutDirty = true;
    private double _layoutWidth = double.NaN;
    private double _desiredHeight;
    private double _lineHeight = 15.0;
    private double _basePosition;
    private long _baseTicks;
    private Brush? _lastBaseBrush;
    private Brush? _lastHighlightBrush;

    public AppleMusicLyricLine()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => UpdateTimerState();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _timer.Stop(); else UpdateTimerState(); };
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

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsEmpty
    {
        get => (bool)GetValue(IsEmptyProperty);
        set => SetValue(IsEmptyProperty, value);
    }

    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public double KaraokeSpeed
    {
        get => (double)GetValue(KaraokeSpeedProperty);
        set => SetValue(KaraokeSpeedProperty, value);
    }

    /// <summary>???????????</summary>
    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty ||
            e.Property == FontWeightProperty || e.Property == FontStyleProperty ||
            e.Property == ForegroundProperty || e.Property == LineHeightProperty)
        {
            InvalidateLayout();
        }
    }

    private void InvalidateLayout()
    {
        _layoutDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnPositionChanged(double value)
    {
        _basePosition = double.IsFinite(value) ? value : 0.0;
        _baseTicks = Stopwatch.GetTimestamp();
        UpdateTimerState();
        InvalidateVisual();
    }

    private void OnPlayStateChanged(bool playing)
    {
        _baseTicks = Stopwatch.GetTimestamp();
        UpdateTimerState();
        InvalidateVisual();
    }

    private void UpdateTimerState()
    {
        if (!IsLoaded || !IsVisible || !IsPlaying || IsEmpty || Words is not { Count: > 0 } words)
        {
            _timer.Stop();
            return;
        }

        var pos = _basePosition;
        var first = words[0].BeginSec;
        var last = words[^1].EndSec;
        // ?? 0.4s ??????????????????????????
        if (pos >= first - 0.4 && pos <= last + 0.05)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsPlaying || !IsVisible)
        {
            _timer.Stop();
            return;
        }

        var elapsed = (Stopwatch.GetTimestamp() - _baseTicks) / (double)Stopwatch.Frequency;
        if (elapsed > StallStopSeconds)
        {
            // ???????????????????????????
            _timer.Stop();
            InvalidateVisual();
            return;
        }

        var pos = _basePosition + Math.Min(Math.Max(0.0, elapsed), WallClockLeadCap);
        if (Words is { Count: > 0 } words)
        {
            if (pos < words[0].BeginSec - 0.05 || pos >= words[^1].EndSec) _timer.Stop();
        }
        else
        {
            _timer.Stop();
        }
        InvalidateVisual();
    }

    private double RenderPosition()
    {
        if (!_timer.IsEnabled) return _basePosition;
        var elapsed = (Stopwatch.GetTimestamp() - _baseTicks) / (double)Stopwatch.Frequency;
        return _basePosition + Math.Min(Math.Max(0.0, elapsed), WallClockLeadCap);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        var width = double.IsFinite(constraint.Width) && constraint.Width > 1 ? constraint.Width : DefaultWidth;
        EnsureLayout(width);
        return new System.Windows.Size(width, _desiredHeight);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size arrangeSize)
    {
        var width = double.IsFinite(arrangeSize.Width) && arrangeSize.Width > 1 ? arrangeSize.Width : DefaultWidth;
        if (!_layoutDirty && Math.Abs(width - _layoutWidth) > 0.5)
        {
            EnsureLayout(width);
            if (Math.Abs(_desiredHeight - arrangeSize.Height) > 0.5)
                Dispatcher.BeginInvoke(new Action(InvalidateMeasure), DispatcherPriority.Loaded);
        }
        return arrangeSize;
    }

    private void EnsureLayout(double width)
    {
        if (!_layoutDirty && Math.Abs(width - _layoutWidth) <= 0.5) return;

        _boxes.Clear();
        _layoutWidth = width;
        _layoutDirty = false;

        var emSize = double.IsFinite(FontSize) && FontSize > 0 ? FontSize : 10.0;
        var styleLineHeight = double.IsFinite(LineHeight) && LineHeight > 0 ? LineHeight : emSize * 1.5;
        _lineHeight = Math.Max(styleLineHeight, emSize * 1.2);

        var words = Words;
        if (IsEmpty || words is not { Count: > 0 })
        {
            _desiredHeight = IsEmpty ? Math.Max(8.0, _lineHeight * 0.6) : _lineHeight;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelsPerDip = dpi.PixelsPerDip > 0.05 ? dpi.PixelsPerDip : 1.0;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var measureWidth = Math.Max(1.0, width);

        var pending = new List<WordBox>(words.Count);
        var lineWidth = 0.0;
        var lineY = 0.0;
        var totalHeight = 0.0;
        string? previousText = null;

        foreach (var word in words)
        {
            if (word is null || string.IsNullOrWhiteSpace(word.Text)) continue;

            var baseText = MakeFormattedText(word.Text, typeface, emSize, pixelsPerDip, Foreground);
            var highlightText = MakeFormattedText(word.Text, typeface, emSize, pixelsPerDip, HighlightBrush);
            var wordWidth = Math.Max(0.5, baseText.WidthIncludingTrailingWhitespace);
            var space = pending.Count > 0 && previousText is not null && TtmlParser.NeedsSpace(previousText, word.Text)
                ? SpaceWidth(typeface, emSize, pixelsPerDip)
                : 0.0;

            if (pending.Count > 0 && lineWidth + space + wordWidth > measureWidth)
            {
                CenterLine(pending, measureWidth, lineWidth, lineY);
                _boxes.AddRange(pending);
                pending.Clear();
                lineY += _lineHeight;
                lineWidth = 0.0;
                space = 0.0;
            }

            lineWidth += space;
            pending.Add(new WordBox
            {
                Word = word,
                Base = baseText,
                Highlight = highlightText,
                X = lineWidth,
                Y = lineY,
                Width = wordWidth,
            });
            lineWidth += wordWidth;
            previousText = word.Text;
        }

        if (pending.Count > 0)
        {
            CenterLine(pending, measureWidth, lineWidth, lineY);
            _boxes.AddRange(pending);
            totalHeight = lineY + _lineHeight;
        }

        _desiredHeight = Math.Max(_lineHeight, totalHeight);
        _lastBaseBrush = Foreground;
        _lastHighlightBrush = HighlightBrush;
    }

    private static void CenterLine(List<WordBox> line, double availableWidth, double lineWidth, double y)
    {
        var offset = Math.Max(0.0, (availableWidth - lineWidth) / 2.0);
        foreach (var box in line)
        {
            box.X += offset;
            box.Y = y;
        }
    }

    private double SpaceWidth(Typeface typeface, double emSize, double pixelsPerDip)
    {
        var space = new FormattedText(" ", CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            typeface, emSize, Brushes.White, pixelsPerDip);
        return Math.Max(1.0, space.WidthIncludingTrailingWhitespace);
    }

    private static FormattedText MakeFormattedText(string text, Typeface typeface, double emSize,
        double pixelsPerDip, Brush? brush)
        => new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface, emSize,
            brush ?? Brushes.White, pixelsPerDip);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (IsEmpty || _boxes.Count == 0) return;
        if (!_layoutDirty && _layoutWidth < 1) EnsureLayout(DefaultWidth);

        var baseBrush = Foreground ?? Brushes.White;
        var highlightBrush = HighlightBrush ?? Brushes.White;
        if (!ReferenceEquals(baseBrush, _lastBaseBrush) || !ReferenceEquals(highlightBrush, _lastHighlightBrush))
        {
            foreach (var b in _boxes)
            {
                b.Base.SetForegroundBrush(baseBrush);
                b.Highlight.SetForegroundBrush(highlightBrush);
            }
            _lastBaseBrush = baseBrush;
            _lastHighlightBrush = highlightBrush;
        }

        var pos = RenderPosition();
        var speed = double.IsFinite(KaraokeSpeed) && KaraokeSpeed > 0 ? Math.Clamp(KaraokeSpeed, 0.2, 3.0) : 1.0;

        foreach (var box in _boxes)
        {
            drawingContext.DrawText(box.Base, new Point(box.X, box.Y));
            var fraction = FillFraction(box.Word, pos, speed);
            if (fraction <= 0.0005) continue;

            var clipWidth = box.Width * fraction;
            if (clipWidth <= 0.01) continue;
            drawingContext.PushClip(new RectangleGeometry(new Rect(box.X, box.Y, clipWidth, box.Highlight.Height + 2.0)));
            drawingContext.DrawText(box.Highlight, new Point(box.X, box.Y));
            drawingContext.Pop();
        }
    }

    /// <summary>time &lt;= start ? 0?start..end ???time &gt;= end ? 1?</summary>
    internal static double FillFraction(TtmlWord word, double position, double speed = 1.0)
    {
        if (position >= word.EndSec) return 1.0;
        if (position <= word.BeginSec) return 0.0;
        var duration = word.EndSec - word.BeginSec;
        if (!double.IsFinite(duration) || duration <= 0.0001) return position >= word.EndSec ? 1.0 : 0.0;
        var fraction = (position - word.BeginSec) / duration;
        if (speed > 0 && Math.Abs(speed - 1.0) > 0.0001) fraction *= speed;
        return Math.Clamp(fraction, 0.0, 1.0);
    }
}
