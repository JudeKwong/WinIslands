using WinIslands.Services;

namespace WinIslands.Tests;

/// <summary>
/// TTML 逐字歌词解析测试（Apple Music 风格 TTML）。
/// 验证：基本结构解析、逐字时间轴、翻译/音译 span 排除、
/// 时间格式（mm:ss.fff / hh:mm:ss.fff / 秒后缀）、空输入与畸形 XML。
/// </summary>
public class TtmlParserTests
{
    private const string NsTt = "http://www.w3.org/ns/ttml";
    private const string NsTtm = "http://www.w3.org/ns/ttml#metadata";

    [Fact]
    public void Empty_Input_Returns_Empty_Document()
    {
        Assert.True(TtmlParser.Parse("").IsEmpty);
        Assert.True(TtmlParser.Parse("   ").IsEmpty);
        Assert.True(TtmlParser.Parse(null!).IsEmpty);
    }

    [Fact]
    public void Malformed_Xml_Returns_Empty_Document()
    {
        Assert.True(TtmlParser.Parse("not xml at all").IsEmpty);
        Assert.True(TtmlParser.Parse("<unclosed>").IsEmpty);
    }

    [Fact]
    public void Parses_Basic_Line_With_Word_Spans()
    {
        var xml = $"""
        <tt xmlns="{NsTt}" xmlns:ttm="{NsTtm}">
          <body>
            <div>
              <p begin="00:01.000" end="00:04.000">
                <span begin="00:01.000" end="00:01.500">Hello</span>
                <span begin="00:01.500" end="00:02.000">World</span>
              </p>
            </div>
          </body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.False(doc.IsEmpty);
        Assert.Single(doc.Lines);

        var line = doc.Lines[0];
        Assert.Equal("HelloWorld", line.Text);
        Assert.Equal(1.0, line.BeginSec);
        Assert.Equal(4.0, line.EndSec);
        Assert.Equal(2, line.Words.Count);
        Assert.Equal("Hello", line.Words[0].Text);
        Assert.Equal(1.0, line.Words[0].BeginSec);
        Assert.Equal(1.5, line.Words[0].EndSec);
        Assert.Equal("World", line.Words[1].Text);
        Assert.Equal(1.5, line.Words[1].BeginSec);
        Assert.Equal(2.0, line.Words[1].EndSec);
    }

    [Fact]
    public void Parses_Direct_Text_As_Single_Word()
    {
        // <p> with no child <span> — text becomes one word spanning the whole line.
        var xml = $"""
        <tt xmlns="{NsTt}">
          <body><div>
            <p begin="00:10.000" end="00:13.500">Plain text line</p>
          </div></body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.Single(doc.Lines);
        var line = doc.Lines[0];
        Assert.Equal("Plain text line", line.Text);
        Assert.Equal(10.0, line.BeginSec);
        Assert.Equal(13.5, line.EndSec);
        Assert.Single(line.Words);
        Assert.Equal("Plain text line", line.Words[0].Text);
    }

    [Fact]
    public void Skips_Translation_And_Roman_Spans()
    {
        var xml = $"""
        <tt xmlns="{NsTt}" xmlns:ttm="{NsTtm}">
          <body><div>
            <p begin="00:01.000" end="00:03.000">
              <span begin="00:01.000" end="00:02.000">hello</span>
              <span ttm:role="x-translation">你好</span>
              <span ttm:role="x-roman">ni hao</span>
            </p>
          </div></body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.Single(doc.Lines);
        var line = doc.Lines[0];
        // Only "hello" should be in words; translation/roman are excluded from timing.
        Assert.Single(line.Words);
        Assert.Equal("hello", line.Words[0].Text);
    }

    [Fact]
    public void Parses_Multiple_Lines_Sorted_By_BeginTime()
    {
        var xml = $"""
        <tt xmlns="{NsTt}">
          <body><div>
            <p begin="00:10.000" end="00:13.000">third</p>
            <p begin="00:01.000" end="00:04.000">first</p>
            <p begin="00:05.000" end="00:08.000">second</p>
          </div></body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.Equal(3, doc.Lines.Count);
        // Lines should be sorted by begin time.
        Assert.Equal("first", doc.Lines[0].Text);
        Assert.Equal(1.0, doc.Lines[0].BeginSec);
        Assert.Equal("second", doc.Lines[1].Text);
        Assert.Equal(5.0, doc.Lines[1].BeginSec);
        Assert.Equal("third", doc.Lines[2].Text);
        Assert.Equal(10.0, doc.Lines[2].BeginSec);
    }

    [Fact]
    public void Parses_Hour_Format_Time()
    {
        var xml = $"""
        <tt xmlns="{NsTt}">
          <body><div>
            <p begin="01:02:03.500" end="01:02:05.000">long track</p>
          </div></body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.Single(doc.Lines);
        Assert.Equal(1 * 3600 + 2 * 60 + 3.5, doc.Lines[0].BeginSec);
        Assert.Equal(1 * 3600 + 2 * 60 + 5.0, doc.Lines[0].EndSec);
    }

    [Fact]
    public void ParseClock_Seconds_Suffix()
    {
        // "12.5s" → 12.5 seconds
        Assert.Equal(12.5, TtmlParser.ParseClock("12.5s"));
        Assert.Equal(3.0, TtmlParser.ParseClock("3s"));
        Assert.Equal(0.5, TtmlParser.ParseClock("0.5s"));
    }

    [Fact]
    public void ParseClock_Comma_Decimal_Separator()
    {
        // TTML allows comma as decimal separator (e.g. 00:01,500)
        Assert.Equal(1.5, TtmlParser.ParseClock("00:01,500"));
    }

    [Fact]
    public void ParseClock_Invalid_Returns_Null()
    {
        Assert.Null(TtmlParser.ParseClock("abc"));
        Assert.Null(TtmlParser.ParseClock(""));
        Assert.Null(TtmlParser.ParseClock("not a time"));
    }

    [Fact]
    public void Span_Without_Explicit_Times_Inherits_Parent_Times()
    {
        var xml = $"""
        <tt xmlns="{NsTt}">
          <body><div>
            <p begin="00:05.000" end="00:08.000">
              <span>word1</span>
              <span>word2</span>
            </p>
          </div></body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.Single(doc.Lines);
        var line = doc.Lines[0];
        Assert.Equal(2, line.Words.Count);
        // Spans without begin/end should inherit parent p's times.
        Assert.All(line.Words, w => Assert.Equal(5.0, w.BeginSec));
    }

    [Fact]
    public void Line_Without_Begin_Attribute_Is_Skipped()
    {
        var xml = $"""
        <tt xmlns="{NsTt}">
          <body><div>
            <p end="00:05.000">no begin time</p>
            <p begin="00:01.000" end="00:03.000">has begin time</p>
          </div></body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.Single(doc.Lines);
        Assert.Equal("has begin time", doc.Lines[0].Text);
    }

    [Fact]
    public void DurationSec_Always_Positive()
    {
        // If end < begin, DurationSec should still be > 0 (clamped).
        var word = new TtmlWord("test", 5.0, 3.0);
        Assert.True(word.DurationSec > 0);
    }

    [Fact]
    public void Nested_Spans_Only_Leaf_Included()
    {
        // Parent span wrapping child spans: only leaf spans (no child spans) contribute words.
        var xml = $"""
        <tt xmlns="{NsTt}">
          <body><div>
            <p begin="00:01.000" end="00:05.000">
              <span begin="00:01.000" end="00:03.000">
                <span begin="00:01.000" end="00:02.000">A</span>
                <span begin="00:02.000" end="00:03.000">B</span>
              </span>
            </p>
          </div></body>
        </tt>
        """;

        var doc = TtmlParser.Parse(xml);

        Assert.Single(doc.Lines);
        var line = doc.Lines[0];
        // Only the leaf spans (A, B) should be collected, not the parent "AB".
        Assert.Equal(2, line.Words.Count);
        Assert.Equal("A", line.Words[0].Text);
        Assert.Equal("B", line.Words[1].Text);
    }
}
