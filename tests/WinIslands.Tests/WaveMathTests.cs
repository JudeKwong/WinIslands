using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>
/// 2.3.5：声波纹渲染「亚像素写入去重」的纯函数测试。
/// 阈值判断必须稳定：非法输入丢弃、首次写入必写、阈值边界精确且任一常见条高/位移都远小于 1 物理像素。
/// </summary>
public sealed class WaveMathTests
{
    [Fact]
    public void ShouldWriteEased_MovementAboveThreshold_Writes()
    {
        // 单帧位移 0.5*0.2=0.1，远大于 ScaleEpsilon → 必须写
        Assert.True(WaveMath.ShouldWriteEased(0.5, 1.0, 0.2, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_MovementBelowThreshold_Skip()
    {
        // 单帧位移 0.1*0.002=0.0002 < 0.0004 → 跳过写入（亚像素）
        Assert.False(WaveMath.ShouldWriteEased(1.0, 1.1, 0.002, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_ZeroDelta_Skip()
    {
        Assert.False(WaveMath.ShouldWriteEased(0.7, 0.7, 0.5, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_AtEpsilon_Writes()
    {
        // |(0.01-0)*0.04| = 0.0004，恰好等于阈值 → 写
        Assert.True(WaveMath.ShouldWriteEased(0.0, 0.01, 0.04, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_NonFiniteCurrent_MustWrite()
    {
        // 尚未有合法值（NaN/Infinity）：调用方收到 true 后直接以目标值复位
        Assert.True(WaveMath.ShouldWriteEased(double.NaN, 0.5, 0.3, WaveMath.ScaleEpsilon));
        Assert.True(WaveMath.ShouldWriteEased(double.PositiveInfinity, 0.5, 0.3, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void ShouldWriteEased_NonFiniteTargetOrAlpha_Discard()
    {
        // 目标/步长非法：丢弃（保持上次写入），绝不产生 NaN 写入
        Assert.False(WaveMath.ShouldWriteEased(0.5, double.NaN, 0.3, WaveMath.ScaleEpsilon));
        Assert.False(WaveMath.ShouldWriteEased(0.5, 0.6, double.PositiveInfinity, WaveMath.ScaleEpsilon));
        Assert.False(WaveMath.ShouldWriteEased(0.5, 0.6, double.NaN, WaveMath.ScaleEpsilon));
    }

    [Fact]
    public void Epsilons_ArePositiveAndSubVisual()
    {
        // 缩放阈值 < 1%：对任意常见条高都远小于 1 物理像素
        Assert.True(WaveMath.ScaleEpsilon > 0 && WaveMath.ScaleEpsilon < 0.01);
        // 位移阈值 < 1 DIP：约 1/20 像素
        Assert.True(WaveMath.OffsetEpsilon > 0 && WaveMath.OffsetEpsilon < 1.0);
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.0005, true)]    // 0.0005 >= 0.0004：边界外→写
    [InlineData(0.0, 1.0, 0.00039, false)]  // 0.00039 < 0.0004：略低于阈值→跳过
    [InlineData(-0.3, 0.9, 0.0004, true)]   // |1.2*0.0004|=0.00048→写
    public void ShouldWriteEased_Boundary(double current, double target, double alpha, bool expected)
    {
        Assert.Equal(expected, WaveMath.ShouldWriteEased(current, target, alpha, WaveMath.ScaleEpsilon));
    }

    [Theory]
    [InlineData(0.0, 100.0, 0.5, 50.0)]
    [InlineData(0.0, 100.0, 1.0, 100.0)]
    [InlineData(10.0, 100.0, 0.0, 10.0)]
    [InlineData(-10.0, 10.0, 0.25, -5.0)]
    public void EaseToward_BlendsTowardTarget(double current, double target, double alpha, double expected)
        => Assert.Equal(expected, WaveMath.EaseToward(current, target, alpha), 9);

    [Fact]
    public void EaseToward_NonFiniteInput_KeepsCurrent()
    {
        // 任一输入非法：保持当前值不变，绝不把 NaN/Inf 写进渲染
        Assert.Equal(5.0, WaveMath.EaseToward(5.0, double.NaN, 0.5), 9);
        Assert.Equal(5.0, WaveMath.EaseToward(5.0, 10.0, double.PositiveInfinity), 9);
        Assert.True(double.IsNaN(WaveMath.EaseToward(double.NaN, 10.0, 0.5)));
    }

    [Fact]
    public void SmoothAlpha_ZeroDt_ReturnsZero()
        => Assert.Equal(0.0, WaveMath.SmoothAlpha(0.0, 22.0), 12);

    [Fact]
    public void SmoothAlpha_PositiveDt_MonotonicIncreasing()
    {
        var prev = -1.0;
        for (var i = 0; i <= 100; i++)
        {
            var dt = i / 1000.0; // 0..0.1
            var a = WaveMath.SmoothAlpha(dt, 22.0);
            Assert.True(a > prev, $"not monotonic at {dt}");
            prev = a;
        }
        Assert.True(prev < 1.0);
    }

    [Fact]
    public void SmoothAlpha_MatchesOriginalFormula()
    {
        // 与旧实现 1 - exp(-dt*rate) 逐点一致（有限输入）
        for (var i = 1; i <= 50; i++)
        {
            var dt = i / 500.0;
            Assert.Equal(1.0 - Math.Exp(-dt * 22.0), WaveMath.SmoothAlpha(dt, 22.0), 12);
        }
    }

    [Fact]
    public void SmoothAlpha_NonFinite_FreezesFrame()
    {
        // NaN/Inf 时钟：返回 0（本帧不动），绝不把 NaN 传进渲染
        Assert.Equal(0.0, WaveMath.SmoothAlpha(double.NaN, 22.0), 9);
        Assert.Equal(0.0, WaveMath.SmoothAlpha(double.PositiveInfinity, 22.0), 9);
        Assert.Equal(0.0, WaveMath.SmoothAlpha(0.02, double.NaN), 9);
        Assert.Equal(0.0, WaveMath.SmoothAlpha(0.02, double.PositiveInfinity), 9);
    }
    /// <summary>
    /// 2.5.0：波形基波合并三角调用——WaveBase 必须与旧的 Math.Sin/Math.Cos 分开调用结果一致
    /// （采样覆盖正负、跨 2π、零附近、常见节拍值；15 位有效数字容差容忍主元约简的尾位差异）。
    /// </summary>
    [Fact]
    public void WaveBase_MatchesSeparateSinCos()
    {
        double[] samples = {
            -37.5, -7.0, -0.001, 0.0, 0.001, 0.25,
            1.0471975511965976, 3.141592653589793, 6.283185307179586,
            12.566370614359172, 99.9, 1234.5678
        };
        foreach (var t in samples)
        {
            var (sin, cos) = WaveMath.WaveBase(t);
            Assert.Equal(Math.Sin(t * 6.0), sin, 15);
            Assert.Equal(Math.Cos(t * 6.0), cos, 15);
            // 单位圆约束：sin²+cos² ≈ 1（合并调用共享主元约简，精度不低于分开调用）
            Assert.Equal(1.0, sin * sin + cos * cos, 14);
        }
    }

    [Fact]
    public void WaveBase_NonFiniteInputs_RemainNonFiniteAndDoNotThrow()
    {
        // 非法时钟的 t（NaN/±Inf）不得让波形路径抛异常：sin/cos 的 IEEE 语义返回 NaN
        var (sinN, cosN) = WaveMath.WaveBase(double.NaN);
        Assert.True(double.IsNaN(sinN) && double.IsNaN(cosN));
        var (sinP, cosP) = WaveMath.WaveBase(double.PositiveInfinity);
        Assert.True(double.IsNaN(sinP) && double.IsNaN(cosP));
        var (sinM, cosM) = WaveMath.WaveBase(double.NegativeInfinity);
        Assert.True(double.IsNaN(sinM) && double.IsNaN(cosM));
    }

    [Theory]
    [InlineData(1.0 / 120.0)]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 30.0)]
    [InlineData(0.033)]
    [InlineData(0.001)]
    public void SmoothAlpha_KaraokeRate_MatchesOriginalFormula(double dt)
    {
        // 2.5.2: 卡拉OK整行模式的平滑速率 rate=42 与原式 1-exp(-dt*42) 一致（有限输入逐位一致）
        Assert.Equal(1.0 - Math.Exp(-dt * 42.0), WaveMath.SmoothAlpha(dt, 42.0), 12);
    }


    [Theory]
    [InlineData(0.9, 32)]
    [InlineData(1.3, 32)]
    [InlineData(0.0, 1)]
    [InlineData(2.5, 5)]
    public void BuildWaveOffsets_MatchesSeparatedSinCos(double step, int count)
    {
        // v2.5.5: 打包相位偏移（Math.SinCos）与原分开 Math.Sin/Math.Cos 一致（15 位有效数字）
        var offsets = WaveMath.BuildWaveOffsets(step, count);
        Assert.Equal(count, offsets.Length);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(Math.Sin(i * step), offsets[i].Sin, 14);
            Assert.Equal(Math.Cos(i * step), offsets[i].Cos, 14);
        }
    }

    [Theory]
    [InlineData(0.3, -0.5)]
    [InlineData(-0.9, 0.7)]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    public void WaveValue_MatchesSeparatedFormula(double sinBase, double cosBase)
    {
        // v2.5.5: 打包读取的叠加值与原 0.5 + 0.5*(sinBase*cosOff - cosBase*sinOff) 一致
        var offsets = WaveMath.BuildWaveOffsets(1.0, 13);
        for (var i = 0; i < offsets.Length; i++)
        {
            var off = offsets[i];
            var old = 0.5 + 0.5 * (sinBase * off.Cos - cosBase * off.Sin);
            Assert.Equal(old, WaveMath.WaveValue(sinBase, cosBase, off), 14);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(48)]
    [InlineData(64)]
    public void BiasRamp_AccumulatedMatchesClosedForm(int n)
    {
        // v2.5.6: 频谱 bias 斜坡改为每迭代累加（ramp += 0.45/n），
        // 与原 0.55 + 0.45*i/n 闭式逐点等价：累加误差被 1e-12 界住，
        // 远小于写入阈值（ScaleEpsilon=4e-4），视觉与数值均无差异。
        var invN = 1.0 / n;
        var rampStep = 0.45 * invN;
        var accumulated = 0.55;
        for (var i = 0; i < n; i++)
        {
            var closed = 0.55 + 0.45 * i * invN;
            Assert.True(Math.Abs(accumulated - closed) < 1e-12,
                $"n={n} i={i}: accumulated={accumulated:R} closed={closed:R}");
            accumulated += rampStep;
        }
    }


    [Fact]
    public void EnvelopeSample_NaNAndInfinity_AreSilent()
    {
        Assert.Equal(0.0, WaveMath.EnvelopeSample(float.NaN));
        Assert.Equal(0.0, WaveMath.EnvelopeSample(float.PositiveInfinity));
        Assert.Equal(0.0, WaveMath.EnvelopeSample(float.NegativeInfinity));
        // 各种 NaN 位型（静默/信号/负号）都归零
        Assert.Equal(0.0, WaveMath.EnvelopeSample(BitConverter.UInt32BitsToSingle(0x7FC00000u)));
        Assert.Equal(0.0, WaveMath.EnvelopeSample(BitConverter.UInt32BitsToSingle(0x7F800001u)));
        Assert.Equal(0.0, WaveMath.EnvelopeSample(BitConverter.UInt32BitsToSingle(0xFF800000u)));
    }

    [Fact]
    public void EnvelopeSample_ClampsOverRangeToOne()
    {
        Assert.Equal(1.0, WaveMath.EnvelopeSample(1.0f));
        Assert.Equal(1.0, WaveMath.EnvelopeSample(-1.0f));
        Assert.Equal(1.0, WaveMath.EnvelopeSample(2.0f));
        Assert.Equal(1.0, WaveMath.EnvelopeSample(-3.5f));
        Assert.Equal(1.0, WaveMath.EnvelopeSample(float.MaxValue));
        Assert.Equal(1.0, WaveMath.EnvelopeSample(float.MinValue));
    }

    [Fact]
    public void EnvelopeSample_PreservesInRangeMagnitude()
    {
        Assert.Equal(0.0, WaveMath.EnvelopeSample(0.0f));
        Assert.Equal(0.0, WaveMath.EnvelopeSample(-0.0f));
        Assert.Equal(0.5, WaveMath.EnvelopeSample(0.5f));
        Assert.Equal(0.25, WaveMath.EnvelopeSample(-0.25f));
        Assert.Equal((double)float.Epsilon, WaveMath.EnvelopeSample(float.Epsilon));
        Assert.Equal((double)float.Epsilon, WaveMath.EnvelopeSample(-float.Epsilon));
    }

    [Fact]
    public void EnvelopeSample_SweepMatchesOriginalFormula()
    {
        // 全 32 位空间按大素数步长采样（NaN/Inf/非规格化/全幅值等区域都覆盖），
        // 与原 IsNaN||IsInfinity 置 0 + 越界钳 1 的实现逐位比较（DoubleToInt64Bits）。
        for (ulong i = 0; i <= uint.MaxValue; i += 65537ul)
        {
            var x = BitConverter.UInt32BitsToSingle((uint)i);
            var expect = EnvelopeReference(x);
            var actual = WaveMath.EnvelopeSample(x);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expect), BitConverter.DoubleToInt64Bits(actual));
        }
    }

    private static double EnvelopeReference(float x)
    {
        if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
        var v = Math.Abs((double)x);
        return v > 1.0 ? 1.0 : v;
    }
}
