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
            Assert.Equal(Math.Sin(t * 6.0), sin, 12);
            Assert.Equal(Math.Cos(t * 6.0), cos, 12);
            // 单位圆约束：sin²+cos² ≈ 1（合并调用共享主元约简，精度不低于分开调用）
            Assert.Equal(1.0, sin * sin + cos * cos, 11);
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
            Assert.Equal(Math.Sin(i * step), offsets[i].Sin, 12);
            Assert.Equal(Math.Cos(i * step), offsets[i].Cos, 12);
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

    /// <summary>2.7.8：单元区间钳制 ClampUnit 与 Math.Clamp(x, 0, 1) 逐位一致（全部 double 输入）。</summary>
    [Fact]
    public void ClampUnit_Specials_BitIdenticalToMathClamp()
    {
        // 特殊值：NaN/±Inf/±0/极值/Epsilon/边界内外
        var specials = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0
        };
        foreach (var x in specials)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Clamp(x, 0.0, 1.0)),
                BitConverter.DoubleToInt64Bits(WaveMath.ClampUnit(x)));
    }

    /// <summary>2.7.8：[-3, 3] 100001 点密集扫描 + 指数极端值，逐位一致。</summary>
    [Fact]
    public void ClampUnit_DenseSweep_BitIdenticalToMathClamp()
    {
        for (var i = 0; i <= 100000; i++)
        {
            var x = -3.0 + 6.0 * i / 100000.0;
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Clamp(x, 0.0, 1.0)),
                BitConverter.DoubleToInt64Bits(WaveMath.ClampUnit(x)));
        }
        var extremes = new[] { 1e300, -1e300, 1e-300, -1e-300, 0.9999999999999999, 1.0000000000000002 };
        foreach (var x in extremes)
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(Math.Clamp(x, 0.0, 1.0)),
                BitConverter.DoubleToInt64Bits(WaveMath.ClampUnit(x)));
    }

    /// <summary>2.7.8：边界断言——[0,1] 内原样、外钳端点、NaN/±Inf、-0.0 保号。</summary>
    [Fact]
    public void ClampUnit_Boundaries_Exact()
    {
        Assert.Equal(0.5, WaveMath.ClampUnit(0.5));
        Assert.Equal(1.0, WaveMath.ClampUnit(1.0));
        Assert.Equal(1.0, WaveMath.ClampUnit(1.0000000000000001));
        Assert.Equal(0.0, WaveMath.ClampUnit(-0.0000000000000001));
        Assert.Equal(0.0, WaveMath.ClampUnit(-2.5));
        Assert.Equal(1.0, WaveMath.ClampUnit(2.5));
        Assert.Equal(0.0, WaveMath.ClampUnit(double.NegativeInfinity));
        Assert.Equal(1.0, WaveMath.ClampUnit(double.PositiveInfinity));
        Assert.True(double.IsNaN(WaveMath.ClampUnit(double.NaN)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(WaveMath.ClampUnit(-0.0)));
    }

    /// <summary>2.7.9：区间钳制 ClampRange 与 Math.Clamp(x, min, max) 在合法输入上逐位一致。</summary>
    [Fact]
    public void ClampRange_Specials_BitIdenticalToMathClamp()
    {
        var specials = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0
        };
        var ranges = new[] { (0.05, 1.0), (0.08, 1.0), (-1.0, 1.0), (0.3, 0.7) };
        foreach (var (min, max) in ranges)
            foreach (var x in specials)
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(Math.Clamp(x, min, max)),
                    BitConverter.DoubleToInt64Bits(WaveMath.ClampRange(x, min, max)));
    }

    /// <summary>2.7.9：[-2, 2] 100001 点密集扫描 × 实际声波纹上下界，逐位一致。</summary>
    [Fact]
    public void ClampRange_DenseSweep_BitIdenticalToMathClamp()
    {
        foreach (var (min, max) in new[] { (0.05, 1.0), (0.08, 1.0), (-1.0, 1.0) })
            for (var i = 0; i <= 100000; i++)
            {
                var x = -2.0 + 4.0 * i / 100000.0;
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(Math.Clamp(x, min, max)),
                    BitConverter.DoubleToInt64Bits(WaveMath.ClampRange(x, min, max)));
            }
    }

    /// <summary>2.7.9：边界断言——区间内原样、低于下限钳 min、高于上限钳 max、NaN/±Inf 与 Math.Clamp 一致。</summary>
    [Fact]
    public void ClampRange_Boundaries_Exact()
    {
        Assert.Equal(0.05, WaveMath.ClampRange(0.05, 0.05, 1.0));
        Assert.Equal(1.0, WaveMath.ClampRange(1.0, 0.05, 1.0));
        Assert.Equal(0.05, WaveMath.ClampRange(-2.0, 0.05, 1.0));
        Assert.Equal(1.0, WaveMath.ClampRange(2.0, 0.05, 1.0));
        Assert.Equal(0.08, WaveMath.ClampRange(0.0, 0.08, 1.0));
        Assert.Equal(0.5, WaveMath.ClampRange(0.5, 0.08, 1.0));
        Assert.Equal(1.0, WaveMath.ClampRange(double.PositiveInfinity, 0.05, 1.0));
        Assert.Equal(0.05, WaveMath.ClampRange(double.NegativeInfinity, 0.05, 1.0));
        Assert.True(double.IsNaN(WaveMath.ClampRange(double.NaN, 0.05, 1.0)));
    }
    // ── 2.8.4：ShouldWriteEased 双边界分支链 + EnvelopeSample 清符号位（逐位等价 Math.Abs 形式）────────────────
    private static bool ShouldWriteEasedRef(double current, double target, double alpha, double epsilon)
    {
        if (!double.IsFinite(current)) return true;
        if (!double.IsFinite(target) || !double.IsFinite(alpha)) return false;
        return Math.Abs((target - current) * alpha) >= epsilon;
    }

    [Fact]
    public void ShouldWriteEased_BranchChain_EquivalentToAbsForm_Grid()
    {
        var vals = new double[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
            double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon,
            0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0, 0.1, -0.1
        };
        var epsilons = new double[] { WaveMath.ScaleEpsilon, WaveMath.OffsetEpsilon, 0.5, 1e-9, 0.0, -0.0, double.NaN };
        foreach (var c in vals)
            foreach (var ta in vals)
                foreach (var al in vals)
                    foreach (var ep in epsilons)
                        Assert.Equal(ShouldWriteEasedRef(c, ta, al, ep), WaveMath.ShouldWriteEased(c, ta, al, ep));
    }

    [Fact]
    public void ShouldWriteEased_BranchChain_EquivalentToAbsForm_Random()
    {
        var rnd = new Random(28404);
        for (var i = 0; i < 200_000; i++)
        {
            var c = BitConverter.Int64BitsToDouble(rnd.NextInt64());
            var ta = BitConverter.Int64BitsToDouble(rnd.NextInt64());
            var al = BitConverter.Int64BitsToDouble(rnd.NextInt64());
            var ep = rnd.Next(2) == 0 ? WaveMath.ScaleEpsilon : WaveMath.OffsetEpsilon;
            Assert.Equal(ShouldWriteEasedRef(c, ta, al, ep), WaveMath.ShouldWriteEased(c, ta, al, ep));
        }
    }

    private static double EnvelopeSampleRef(float x)
    {
        var bits = BitConverter.SingleToUInt32Bits(x);
        if ((bits & 0x7F800000u) == 0x7F800000u) return 0.0;
        var v = Math.Abs((double)x);
        return v > 1.0 ? 1.0 : v;
    }

    [Fact]
    public void EnvelopeSample_SignBitClear_BitwiseIdenticalToMathAbsForm()
    {
        var bitSpecials = new uint[]
        {
            0x7FC00000u, 0xFFC00000u, 0x7F800001u, 0xFF800001u, // ±NaN（含负载）
            0x7F800000u, 0xFF800000u, // ±Inf
            0x00000000u, 0x80000000u, // ±0
            0x3F800000u, 0xBF800000u, // ±1
            0x7F7FFFFFu, 0xFF7FFFFFu, // ±MaxValue
            0x00000001u, 0x80000001u, // ±最小次正规
            0x3F7FFFFFu, 0xBF7FFFFFu, // 1-ulp 临界内侧
            0x3F800001u, 0xBF800001u  // 1+ulp 临界外侧
        };
        foreach (var bits in bitSpecials)
        {
            var f = BitConverter.UInt32BitsToSingle(bits);
            Assert.Equal(BitConverter.DoubleToInt64Bits(EnvelopeSampleRef(f)),
                         BitConverter.DoubleToInt64Bits(WaveMath.EnvelopeSample(f)));
        }
        var rnd = new Random(28409);
        for (var i = 0; i < 300_000; i++)
        {
            var f = BitConverter.UInt32BitsToSingle(unchecked((uint)rnd.NextInt64()));
            Assert.Equal(BitConverter.DoubleToInt64Bits(EnvelopeSampleRef(f)),
                         BitConverter.DoubleToInt64Bits(WaveMath.EnvelopeSample(f)));
        }
    }

    // ── 2.9.1：Int16 包络样本 —— 全 65536 位形与 Math.Abs(s / 32768.0) 逐位一致 ──────
    [Fact]
    public void Int16Envelope_BitIdenticalToMathAbs()
    {
        // Int16 由构造保证 |s| ≤ 32768，无需再钳制；整数 → double 除法一致，
        // 绝对值走 AbsValue（符号位清零）与 Math.Abs 逐位等价。
        for (int i = short.MinValue; i <= short.MaxValue; i++)
        {
            var s = (short)i;
            var expected = Math.Abs(s / 32768.0);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected),
                         BitConverter.DoubleToInt64Bits(WaveMath.Int16Envelope(s)));
        }
        // 边界：-32768 → 1.0，32767 → 略小于 1，0 → 0
        Assert.Equal(1.0, WaveMath.Int16Envelope(short.MinValue), 12);
        Assert.Equal(32767.0 / 32768.0, WaveMath.Int16Envelope(short.MaxValue), 12);
        Assert.Equal(0.0, WaveMath.Int16Envelope(0), 12);
    }
}
