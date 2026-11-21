using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinIslands.Services;

/// <summary>一个逐字时间片：文本 + 绝对起止秒（来自 TTML &lt;span&gt;）。</summary>
public sealed record TtmlWord(string Text, double BeginSec, double EndSec, bool IsBackground = false)
{
    public double DurationSec => Math.Max(0.001, EndSec - BeginSec);
}

/// <summary>TTML 中的一行（&lt;p&gt; 元素）：行文本 + 该行内逐字时间轴。</summary>
public sealed record TtmlLine(
    string Text,
    double BeginSec,
    double EndSec,
    IReadOnlyList<TtmlWord> Words,
    string? Translation = null,
    bool Empty = false,
    bool IsDuet = false,
    bool IsBackground = false)
{
    public bool HasWords => Words.Count > 0;
}

/// <summary>解析后的 TTML 逐字歌词文档（Apple Music 风格）。</summary>
public sealed class TtmlDocument
{
    public IReadOnlyList<TtmlLine> Lines { get; init; } = Array.Empty<TtmlLine>();
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// 解析 Apple Music 风格 TTML 逐字歌词 XML。
/// 输出统一行模型：绝对起止秒、逐词时间、翻译、空行、对唱与背景和声标记。
/// 没有逐词时间时按 CJK 单字 / 拉丁词均分；长间奏自动插入空行。
/// </summary>
public static class TtmlParser
{
    private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
    private static readonly XNamespace Ttm = "http://www.w3.org/ns/ttml#metadata";

    private static readonly Regex TimeRegex = new(
        @"^(?:(?<h>\d+):)?(?<m>\d{1,2}):(?<s>\d{1,2})(?:[.,](?<f>\d{1,4}))?$",
        RegexOptions.Compiled);

    private const double EmptyGapSeconds = 5.0;

    private sealed record TimedText(string Text, double? BeginSec, double? EndSec, bool IsBackground);

    public static TtmlDocument Parse(string ttmlXml)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ttmlXml)) return new TtmlDocument();

            XDocument doc;
            // 必须保留空白文本节点：相邻 span 分组的判定依赖「中间是否真的有文本节点」。
            try { doc = XDocument.Parse(ttmlXml, LoadOptions.PreserveWhitespace); }
            catch { return new TtmlDocument(); }

            var body = doc.Root?.Element(Tt + "body") ?? doc.Root?.Element("body");
            if (body is null) return new TtmlDocument();

            var parsed = new List<TtmlLine>();
            foreach (var p in body.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                var lines = ParseParagraph(p);
                if (lines.Count > 0) parsed.AddRange(lines);
            }

            parsed.Sort((a, b) =>
            {
                var byTime = a.BeginSec.CompareTo(b.BeginSec);
                return byTime != 0 ? byTime : a.IsBackground.CompareTo(b.IsBackground);
            });

            return new TtmlDocument { Lines = InsertEmptyLines(parsed) };
        }
        catch
        {
            return new TtmlDocument();
        }
    }

    private static List<TtmlLine> ParseParagraph(XElement p)
    {
        var pBegin = ParseTimeAttr(p, "begin");
        var pEnd = ParseTimeAttr(p, "end");
        var agent = (string?)p.Attribute(Ttm + "agent") ?? string.Empty;
        var role = (string?)p.Attribute(Ttm + "role") ?? string.Empty;
        var isBackgroundParagraph = IsBackgroundRole(role);

        var main = new List<TimedText>();
        var background = new List<TimedText>();
        var translations = new List<string>();

        CollectNode(p, pBegin, pEnd, isBackgroundParagraph, isTranslationContainer: false, main, background, translations);

        var explicitTimes = main.Concat(background)
            .Where(x => x.BeginSec.HasValue).Select(x => x.BeginSec!.Value)
            .Concat(main.Concat(background).Where(x => x.EndSec.HasValue).Select(x => x.EndSec!.Value))
            .ToArray();

        // ????? begin???????? begin ??????????????????
        if (pBegin is null && !p.Descendants().Any(e => e.Attribute("begin") is not null))
            return new List<TtmlLine>();

        var inferredStart = explicitTimes.Length > 0 ? explicitTimes.Min() : 0.0;
        var inferredEnd = explicitTimes.Length > 0 ? explicitTimes.Max() : inferredStart + 4.0;
        var start = FiniteOr(pBegin, inferredStart);
        var end = FiniteOr(pEnd, Math.Max(start + 0.5, inferredEnd));
        if (end <= start) end = start + 4.0;

        var result = new List<TtmlLine>(2);
        var translation = translations.Count == 0 ? null : JoinTexts(translations);

        if (!isBackgroundParagraph)
        {
            if (main.Count > 0)
            {
                var words = NormalizeWords(main, start, end);
                if (words.Count > 0)
                {
                    var text = JoinWordTexts(words.Select(w => w.Text));
                    result.Add(new TtmlLine(
                        text, start, end, words, translation,
                        Empty: false,
                        IsDuet: string.Equals(agent, "v2", StringComparison.OrdinalIgnoreCase),
                        IsBackground: false));
                }
            }
            else
            {
                result.Add(new TtmlLine(
                    string.Empty, start, end, Array.Empty<TtmlWord>(), translation,
                    Empty: true,
                    IsDuet: string.Equals(agent, "v2", StringComparison.OrdinalIgnoreCase),
                    IsBackground: false));
            }
        }

        if (background.Count > 0)
        {
            var bgWords = NormalizeWords(background, start, end);
            if (bgWords.Count > 0)
            {
                var bgStart = Math.Max(0, bgWords.Min(w => w.BeginSec));
                var bgEnd = Math.Max(bgStart + 0.5, bgWords.Max(w => w.EndSec));
                result.Add(new TtmlLine(
                    JoinWordTexts(bgWords.Select(w => w.Text)),
                    bgStart,
                    bgEnd,
                    bgWords,
                    Translation: null,
                    Empty: false,
                    IsDuet: string.Equals(agent, "v2", StringComparison.OrdinalIgnoreCase),
                    IsBackground: true));
            }
        }

        return result;
    }

    private static void CollectNode(
        XElement parent,
        double? inheritedBegin,
        double? inheritedEnd,
        bool inheritedBackground,
        bool isTranslationContainer,
        List<TimedText> main,
        List<TimedText> background,
        List<string> translations)
    {
        var nodes = parent.Nodes().ToList();
        List<TimedText>? wordGroup = null;
        var groupBackground = inheritedBackground;

        // 把「前后紧邻、中间没有文本节点」的连续 span 合并成一个词片（Apple TTML 音节分组）。
        void FlushWordGroup()
        {
            if (wordGroup is not { Count: > 0 })
            {
                wordGroup = null;
                return;
            }

            var text = JoinWordTexts(wordGroup.Select(w => w.Text));
            if (text.Length > 0)
            {
                var begin = wordGroup.Where(w => IsUsable(w.BeginSec)).Select(w => w.BeginSec).FirstOrDefault() ?? inheritedBegin;
                var end = wordGroup.Where(w => IsUsable(w.EndSec)).Select(w => w.EndSec).LastOrDefault() ?? inheritedEnd;
                (groupBackground ? background : main).Add(new TimedText(text, begin, end, groupBackground));
            }
            wordGroup = null;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is XText textNode)
            {
                // 文本节点（包括空白）会打断相邻 span 分组
                FlushWordGroup();

                if (isTranslationContainer)
                {
                    var translated = NormalizeDirectText(textNode.Value);
                    if (translated.Length > 0) translations.Add(translated);
                    continue;
                }

                var direct = NormalizeDirectText(textNode.Value);
                if (direct.Length == 0) continue;
                (inheritedBackground ? background : main)
                    .Add(new TimedText(direct, inheritedBegin, inheritedEnd, inheritedBackground));
                continue;
            }

            if (node is not XElement span || span.Name.LocalName != "span")
            {
                FlushWordGroup();
                continue;
            }

            var role = (string?)span.Attribute(Ttm + "role") ?? string.Empty;
            // 翻译/音译 span 不参与正文，也不参与正文词片分组
            var translationContainer = isTranslationContainer || IsTranslationRole(role);
            var backgroundContainer = inheritedBackground || IsBackgroundRole(role);
            var begin = ParseTimeAttr(span, "begin") ?? inheritedBegin;
            var end = ParseTimeAttr(span, "end") ?? inheritedEnd;

            if (translationContainer)
            {
                FlushWordGroup();
                var translated = NormalizeDirectText(span.Value);
                if (translated.Length > 0) translations.Add(translated);
                continue;
            }

            if (span.Elements().Any(e => e.Name.LocalName == "span"))
            {
                FlushWordGroup(); // 嵌套容器交给递归，自身不参与同层分组
                CollectNode(span, begin, end, backgroundContainer, false, main, background, translations);
                continue;
            }

            var wordText = NormalizeDirectText(span.Value);
            if (wordText.Length == 0) continue;

            var nextIsElement = i + 1 < nodes.Count && IsSpanNode(nodes[i + 1]);
            var previousIsElement = i > 0 && IsSpanNode(nodes[i - 1]);
            var hasElementSibling = nextIsElement || previousIsElement;

            if (hasElementSibling)
            {
                if (wordGroup is null || groupBackground != backgroundContainer)
                {
                    FlushWordGroup();
                    wordGroup = new List<TimedText>();
                    groupBackground = backgroundContainer;
                }
                wordGroup.Add(new TimedText(wordText, begin, end, backgroundContainer));
                if (!nextIsElement) FlushWordGroup(); // 组尾：输出合并后的词片
            }
            else
            {
                FlushWordGroup();
                (backgroundContainer ? background : main)
                    .Add(new TimedText(wordText, begin, end, backgroundContainer));
            }
        }

        FlushWordGroup();
    }

    private static bool IsSpanNode(XNode node)
        => node is XElement el && el.Name.LocalName == "span";
    private static IReadOnlyList<TtmlWord> NormalizeWords(List<TimedText> candidates, double lineStart, double lineEnd)
    {
        var valid = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .Select(c => new TimedText(c.Text.Trim(), c.BeginSec, c.EndSec, c.IsBackground))
            .ToList();
        if (valid.Count == 0) return Array.Empty<TtmlWord>();

        var text = JoinTexts(valid.Select(c => c.Text));
        if (text.Length == 0) return Array.Empty<TtmlWord>();

        var timed = valid.Where(c => IsUsable(c.BeginSec) && IsUsable(c.EndSec)).ToList();
        var allHaveTimes = timed.Count == valid.Count;
        var allSameWindow = allHaveTimes && valid.All(c =>
            Math.Abs(c.BeginSec!.Value - valid[0].BeginSec!.Value) < 0.0005 &&
            Math.Abs(c.EndSec!.Value - valid[0].EndSec!.Value) < 0.0005);

        // 整行只有一个时间窗（或所有子 span 都没写时间）：按 CJK/拉丁词均分。
        if (valid.Count == 1 && allHaveTimes)
        {
            // 单词语保留自己的 span 时间窗；只有缺失时间时才回退到整行时间
            var onlyBegin = Math.Max(lineStart, valid[0].BeginSec!.Value);
            var onlyEnd = Math.Max(onlyBegin + 0.001, valid[0].EndSec!.Value);
            return new[] { new TtmlWord(valid[0].Text, onlyBegin, onlyEnd, valid[0].IsBackground) };
        }
        if (timed.Count == 0 || allSameWindow)
            return FillWordGaps(text, lineStart, lineEnd);

        var source = allHaveTimes ? valid.OrderBy(c => c.BeginSec!.Value).ToList() : valid;
        var words = new List<TtmlWord>(source.Count);
        var previousEnd = Math.Max(0, lineStart);
        foreach (var candidate in source)
        {
            var begin = candidate.BeginSec ?? previousEnd;
            var end = candidate.EndSec ?? lineEnd;
            if (!double.IsFinite(begin)) begin = previousEnd;
            if (!double.IsFinite(end)) end = lineEnd;
            begin = Math.Max(begin, previousEnd);
            end = Math.Max(begin + 0.001, end);
            words.Add(new TtmlWord(candidate.Text, begin, end, candidate.IsBackground));
            previousEnd = end;
        }

        return words;
    }

    /// <summary>把整行文本按 CJK 单字 / 拉丁词均分到行时间。空文本返回空。</summary>
    internal static IReadOnlyList<TtmlWord> FillWordGaps(string text, double start, double end)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TtmlWord>();
        if (!double.IsFinite(start)) start = 0;
        if (!double.IsFinite(end) || end <= start) end = start + 4.0;

        var tokens = Tokenize(text);
        if (tokens.Count == 0) return Array.Empty<TtmlWord>();

        var step = Math.Max(0.001, end - start) / tokens.Count;
        var words = new TtmlWord[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            words[i] = new TtmlWord(
                tokens[i],
                start + step * i,
                i == tokens.Count - 1 ? end : start + step * (i + 1));
        }
        return words;
    }

    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
        }

        foreach (var rune in text.EnumerateRunes())
        {
            var ch = rune.ToString();
            if (Rune.IsWhiteSpace(rune))
            {
                Flush();
                continue;
            }

            if (IsCjkRune(rune))
            {
                Flush();
                tokens.Add(ch);
            }
            else
            {
                current.Append(ch);
            }
        }

        Flush();
        return tokens;
    }

    /// <summary>CJK 与 CJK 之间不加空格；拉丁词、CJK 与拉丁交界处加一个空格。</summary>
    internal static string JoinWordTexts(IEnumerable<string> texts)
    {
        var parts = texts.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (parts.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        string? previous = null;
        foreach (var part in parts)
        {
            if (previous is not null && NeedsSpace(previous, part)) sb.Append(' ');
            sb.Append(part);
            previous = part;
        }
        return sb.ToString();
    }

    internal static string JoinTexts(IEnumerable<string> texts)
    {
        var parts = texts.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (parts.Count == 0) return string.Empty;
        if (parts.Count == 1) return parts[0];

        var sb = new StringBuilder();
        string? previous = null;
        foreach (var part in parts)
        {
            if (previous is not null)
            {
                var a = previous[^1];
                var b = part[0];
                if (!(IsCjkLike(a) && IsCjkLike(b))) sb.Append(' ');
            }
            sb.Append(part);
            previous = part;
        }
        return sb.ToString();
    }

    internal static bool NeedsSpace(string previous, string current)
    {
        if (previous.Length == 0 || current.Length == 0) return false;
        var a = previous[^1];
        var b = current[0];
        if (char.IsWhiteSpace(a) || char.IsWhiteSpace(b)) return false;
        return !(IsCjkLike(a) && IsCjkLike(b));
    }

    private static bool IsCjkLike(char ch)
        => ch is >= '\u3040' and <= '\u30ff'
            or >= '\u3400' and <= '\u4dbf'
            or >= '\u4e00' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff'
            or >= '\uac00' and <= '\ud7af'
            or >= '\u3000' and <= '\u303f'
            or >= '\uff00' and <= '\uffef';

    private static bool IsCjkRune(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3040 and <= 0x30ff
            or >= 0x3400 and <= 0x4dbf
            or >= 0x4e00 and <= 0x9fff
            or >= 0xf900 and <= 0xfaff
            or >= 0xac00 and <= 0xd7af;
    }

    private static IReadOnlyList<TtmlLine> InsertEmptyLines(List<TtmlLine> lines)
    {
        if (lines.Count == 0) return lines;
        var output = new List<TtmlLine>(lines.Count + 4);
        var previousEnd = double.NegativeInfinity;
        foreach (var line in lines)
        {
            if (double.IsFinite(previousEnd) && line.BeginSec - previousEnd >= EmptyGapSeconds)
            {
                output.Add(new TtmlLine(
                    string.Empty,
                    previousEnd,
                    Math.Max(previousEnd + 0.001, line.BeginSec - 0.01),
                    Array.Empty<TtmlWord>(),
                    Translation: null,
                    Empty: true,
                    IsDuet: false,
                    IsBackground: false));
            }
            output.Add(line);
            previousEnd = Math.Max(previousEnd, line.EndSec);
        }
        return output;
    }

    private static string NormalizeDirectText(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();

    private static bool IsTranslationRole(string role)
        => role is "x-translation" or "x-roman";

    private static bool IsBackgroundRole(string role)
        => role is "x-bg" or "x-background";

    private static bool IsUsable(double? value)
        => value.HasValue && double.IsFinite(value.Value);

    private static double FiniteOr(double? value, double fallback)
        => value.HasValue && double.IsFinite(value.Value) ? value.Value : fallback;

    private static double? ParseTimeAttr(XElement el, string name)
    {
        var raw = (string?)el.Attribute(name);
        return string.IsNullOrWhiteSpace(raw) ? null : ParseClock(raw);
    }

    /// <summary>解析 TTML 时钟格式：mm:ss.fff / hh:mm:ss.fff / 12.5s / 纯秒数。</summary>
    internal static double? ParseClock(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();

        if (raw.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            return secs;

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            return numeric;

        var m = TimeRegex.Match(raw);
        if (!m.Success) return null;

        var hours = m.Groups["h"].Success ? double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = double.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var frac = m.Groups["f"].Success ? m.Groups["f"].Value : string.Empty;
        var ms = frac.Length == 0 ? 0 : double.Parse("0." + frac, CultureInfo.InvariantCulture);
        return hours * 3600 + minutes * 60 + seconds + ms;
    }
}