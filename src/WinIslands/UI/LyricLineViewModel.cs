using WinIslands.Services;

namespace WinIslands.UI;

/// <summary>?????????????????+ ???? + ????? + ????/??/???????</summary>
public sealed class LyricLineViewModel : ObservableObject
{
    private readonly string _text;
    private readonly TimeSpan _time;
    private bool _isCurrent;
    private bool _showTranslation = true;
    private double _highlightFraction;

    public LyricLineViewModel(
        LyricLine line,
        string? translation = null,
        IReadOnlyList<TtmlWord>? words = null,
        double? endSec = null)
    {
        _text = line.Text ?? string.Empty;
        _time = line.Time;
        StartSec = Math.Max(0.0, line.Time.TotalSeconds);
        EndSec = NormalizeEnd(endSec, StartSec);
        Translation = string.IsNullOrWhiteSpace(translation) ? null : translation;
        Words = NormalizeWords(words, _text, StartSec, EndSec);
    }

    /// <summary>TTML ???????????????????????</summary>
    public LyricLineViewModel(TtmlLine line)
    {
        _text = line.Text ?? string.Empty;
        StartSec = double.IsFinite(line.BeginSec) ? Math.Max(0.0, line.BeginSec) : 0.0;
        EndSec = NormalizeEnd(line.EndSec, StartSec);
        _time = TimeSpan.FromSeconds(StartSec);
        Translation = string.IsNullOrWhiteSpace(line.Translation) ? null : line.Translation;
        Empty = line.Empty || string.IsNullOrWhiteSpace(_text);
        IsDuet = line.IsDuet;
        IsBackground = line.IsBackground;
        Words = Empty ? Array.Empty<TtmlWord>() : NormalizeWords(line.Words, _text, StartSec, EndSec);
    }

    public string Text => _text;
    public TimeSpan Time => _time;

    /// <summary>???????TTML / LRC ????</summary>
    public double StartSec { get; }

    /// <summary>???????end &gt; start??</summary>
    public double EndSec { get; }

    /// <summary>?????????????????????</summary>
    public bool Empty { get; }

    /// <summary>?? / ?????ttm:agent="v2"??</summary>
    public bool IsDuet { get; }

    /// <summary>?????ttm:role="x-bg"??</summary>
    public bool IsBackground { get; }

    /// <summary>????????? null??</summary>
    public string? Translation { get; }

    /// <summary>
    /// ??????????TTML ???????LRC ????????
    /// <see cref="TtmlParser.FillWordGaps"/> ? CJK ?? / ?????????????????
    /// </summary>
    public IReadOnlyList<TtmlWord> Words { get; }

    /// <summary>??????????????????????????????</summary>
    public bool ShowTranslation
    {
        get => _showTranslation;
        set => Set(ref _showTranslation, value);
    }

    public bool HasTranslation => Translation is not null;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    /// <summary>????OK???????0..1??????????????????</summary>
    public double HighlightFraction
    {
        get => _highlightFraction;
        set => Set(ref _highlightFraction, value);
    }

    private static double NormalizeEnd(double? endSec, double startSec)
    {
        var end = endSec.HasValue && double.IsFinite(endSec.Value) ? endSec.Value : startSec + 4.0;
        return Math.Max(startSec + 0.05, end);
    }

    private static IReadOnlyList<TtmlWord> NormalizeWords(
        IReadOnlyList<TtmlWord>? words, string text, double startSec, double endSec)
    {
        if (words is { Count: > 0 }) return words;
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TtmlWord>();
        return TtmlParser.FillWordGaps(text, startSec, endSec);
    }
}
