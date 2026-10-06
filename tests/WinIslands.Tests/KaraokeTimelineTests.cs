using WinIslands.Services;
using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 逐字卡拉OK时间轴回归测试：NeedsAnimation 的判定必须与 RenderWords 的渲染时间轴
/// （含字间 lead 预亮）完全一致，否则会在字/句边界“动一下停一下”或提前停动画；
/// 墙钟外推必须限幅，防止 ViewModel 停更时歌词漂到句尾再跳回。
/// </summary>
public class KaraokeTimelineTests
{
    private static readonly TtmlWord[] Words =
    {
        new("作", 0.0, 0.5),
        new("词", 0.5, 1.0),
        new("林", 1.0, 1.4),
        new("夕", 1.4, 1.8),
    };

    [Fact]
    public void BuildWordTimeline_FirstWordNoLead_SubsequentWordsHaveLead()
    {
        var starts = new double[4];
        var denoms = new double[4];
        KaraokeTextBlock.BuildWordTimeline(Words, starts, denoms);

        // 句首第一个字不提前（保证换句时第一个字保持未点亮）
        Assert.Equal(0.0, starts[0], 6);
        Assert.Equal(0.5, denoms[0], 6);
        // 后续字在其开始前最多提前 45ms 起笔
        Assert.Equal(0.5 - 0.045, starts[1], 6);
        Assert.Equal(0.5 + 0.045, denoms[1], 6);
        Assert.Equal(1.0 - 0.045, starts[2], 6);
        Assert.Equal(0.4 + 0.045, denoms[2], 6);
        // lead 不为负、时长不为 0
        for (var i = 0; i < 4; i++)
        {
            Assert.True(starts[i] >= 0);
            Assert.True(denoms[i] > 0);
            Assert.True(starts[i] < Words[i].EndSec);
        }
    }

    [Fact]
    public void NeedsAnimation_MatchesRenderTimeline_AtWordBoundaries()
    {
        var starts = new double[4];
        var denoms = new double[4];
        KaraokeTextBlock.BuildWordTimeline(Words, starts, denoms);

        // 第一字开始前：不点亮（静态即可）
        Assert.False(KaraokeTextBlock.NeedsAnimationFor(-0.01, starts, denoms, 1.0));
        // 第一字刚开始：需要动画（raw = 0）
        Assert.True(KaraokeTextBlock.NeedsAnimationFor(0.0, starts, denoms, 1.0));
        // 第二个字 lead 预亮窗口（word2.begin - 0.03）：渲染已经起笔，必须判定为仍需动画
        //（旧实现用 w.BeginSec 判定，会在该窗口停动画 → 字边界“停一下再动一下”）
        Assert.True(KaraokeTextBlock.NeedsAnimationFor(0.47, starts, denoms, 1.0));
        // 词已全部点亮（第二个字 raw >= 1）
        Assert.False(KaraokeTextBlock.NeedsAnimationFor(10.0, starts, denoms, 1.0));
    }

    [Fact]
    public void NeedsAnimation_AlwaysConsistentWithRenderFormula_AcrossWholeTimeline()
    {
        var starts = new double[4];
        var denoms = new double[4];
        KaraokeTextBlock.BuildWordTimeline(Words, starts, denoms);
        const double speed = 1.0;
        for (var pos = -0.1; pos <= 2.2; pos += 0.001)
        {
            var expected = false; // 渲染公式：存在任意一个字 raw ∈ [0,1) 即需要动画
            for (var i = 0; i < starts.Length; i++)
            {
                var raw = (pos - starts[i]) / denoms[i] * speed;
                if (pos >= starts[i] && raw < 1) { expected = true; break; }
            }
            Assert.Equal(expected, KaraokeTextBlock.NeedsAnimationFor(pos, starts, denoms, speed));
        }
    }

    [Theory]
    [InlineData(100.0, 50.0, 100.5)]   // 外推超过上限：限幅到 0.5s
    [InlineData(100.0, 10.0, 100.5)]   // 长时间停更：绝不无限前移
    [InlineData(100.0, 0.2, 100.2)]    // 正常间隙：原样推进
    [InlineData(100.0, -5.0, 100.0)]   // 时钟回退：不下探
    public void ClampWallClockLead_CapsExtrapolation(double posBase, double elapsed, double expected)
    {
        Assert.Equal(expected, KaraokeTextBlock.ClampWallClockLead(posBase, elapsed), 6);
    }
    [Theory]
    [InlineData(0.0, 0.5, 0.5)]
    [InlineData(0.2, 0.5, 0.5)]
    [InlineData(0.35, 0.5, 0.5)]
    [InlineData(0.5, 0.5, 0.25)]
    [InlineData(0.64, 0.5, 0.0166666667)]
    [InlineData(0.65, 0.5, 0.0)]
    [InlineData(10.0, 0.5, 0.0)]
    public void StallAwareLead_TightensWhenUpdatesStop(double since, double maxLead, double expected)
    {
        Assert.Equal(expected, KaraokeTextBlock.StallAwareLead(since, maxLead), 4);
    }

    [Fact]
    public void StallAwareLead_MonotonicInStallWindow()
    {
        double prev = double.MaxValue;
        for (var s = 0.35; s <= 0.651; s += 0.001)
        {
            var v = KaraokeTextBlock.StallAwareLead(s, 0.5);
            Assert.True(v <= prev + 1e-9, $"lead grew at since={s}");
            prev = v;
        }
        Assert.Equal(0.0, prev, 6);
    }

    [Fact]
    public void ClampWallClockLead_RespectsProvidedCap()
    {
        Assert.Equal(100.2, KaraokeTextBlock.ClampWallClockLead(100.0, 0.2, 0.3), 6);
        Assert.Equal(100.3, KaraokeTextBlock.ClampWallClockLead(100.0, 0.5, 0.3), 6);
        Assert.Equal(100.0, KaraokeTextBlock.ClampWallClockLead(100.0, -1.0, 0.3), 6);
        Assert.Equal(100.05, KaraokeTextBlock.ClampWallClockLead(100.0, 0.05, 0.15), 6);
    }
}
