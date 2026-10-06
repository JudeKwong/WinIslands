using System;

namespace WinIslands.UI;

/// <summary>
/// 声波纹渲染的纯数值工具（2.3.5）：帧率无关缓动写入去重。
/// 音乐声波纹每帧以指数平滑逼近目标；当单帧位移低于亚像素阈值时跳过属性写入，
/// 减少合成线程上的依赖属性变更与脏标记，动画观感不变但帧节奏更稳、更省 CPU。
/// </summary>
internal static class WaveMath
{
    /// <summary>缩放类（ScaleY/ScaleX）写入阈值：0.04%——对任意常见条高都远小于 1 物理像素。</summary>
    internal const double ScaleEpsilon = 0.0004;

    /// <summary>位移类（TranslateTransform.Y）写入阈值：0.05 DIP（约 1/20 像素），远小于 1 物理像素。</summary>
    internal const double OffsetEpsilon = 0.05;

    /// <summary>
    /// 判断是否应把缓动一步写入属性（纯函数，可测）：
    /// current 非法（NaN）必写（调用方会直接赋目标值复位）；
    /// target/alpha 非法丢弃（返回 false，让上次写入保持不变）；
    /// 单帧位移 |(target-current)*alpha| 达到阈值才写。
    /// </summary>
    internal static bool ShouldWriteEased(double current, double target, double alpha, double epsilon)
    {
        if (!double.IsFinite(current)) return true;
        if (!double.IsFinite(target) || !double.IsFinite(alpha)) return false;
        return Math.Abs((target - current) * alpha) >= epsilon;
    }

    /// <summary>指数平滑的下一帧值（纯函数，2.3.9）：current + (target-current)*alpha；任一输入非法时保持当前值不变。</summary>
    internal static double EaseToward(double current, double target, double alpha)
    {
        if (!double.IsFinite(current) || !double.IsFinite(target) || !double.IsFinite(alpha)) return current;
        return current + (target - current) * alpha;
    }

    /// <summary>
    /// Wave frame exponential smoothing factor (2.4.8): 1 - exp(-dt*rate).
    /// Non-finite dt/rate (NaN/Inf) return 0 so a bad clock freezes this frame
    /// instead of pushing NaN into bar/ring/particle transforms; finite inputs
    /// keep the byte-identical formula.
    /// </summary>
    internal static double SmoothAlpha(double dt, double rate)
    {
        if (!double.IsFinite(dt) || !double.IsFinite(rate)) return 0.0;
        return 1.0 - Math.Exp(-dt * rate);
    }
    /// <summary>
    /// 波形基波（2.5.0）：一次 Math.SinCos 同时求 sin/cos——同一自变量 t*6.0 只做主元换算一次，
    /// JIT 展开为单条 FSINCOS 指令对。条/频谱与粒子两条波形热路径共用此纯函数，
    /// 浮点结果与原分开调用的 Math.Sin / Math.Cos 一致（15 位有效数字内）。
    /// </summary>
    internal static (double Sin, double Cos) WaveBase(double t) => Math.SinCos(t * 6.0);
    /// <summary>
    /// 波形相位偏移表（2.5.5）：一次 Math.SinCos 生成打包数组（sin+cos 同元素），
    /// 相比分开的 Sin/Cos 双数组：静态初始化三角调用减半、每帧每元素只读一次数组（一次边界检查）。
    /// 每项与原 Math.Sin(i*step) / Math.Cos(i*step) 一致（15 位有效数字内）。
    /// </summary>
    internal static (double Sin, double Cos)[] BuildWaveOffsets(double step, int count)
    {
        var offsets = new (double Sin, double Cos)[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = Math.SinCos(i * step);
        }
        return offsets;
    }

    /// <summary>
    /// 波形叠加值（2.5.5）：与原 0.5 + 0.5*(sinBase*cosOff - cosBase*sinOff) 完全等价，
    /// 但偏移来自打包数组的单次读取（少一次数组索引/边界检查）。
    /// </summary>
    internal static double WaveValue(double sinBase, double cosBase, (double Sin, double Cos) offset)
        => 0.5 + 0.5 * (sinBase * offset.Cos - cosBase * offset.Sin);

    /// <summary>
    /// 32 位浮点 PCM 样本 → 0..1 包络幅值（2.5.9）：IEEE 754 单精度指数位全 1 即 NaN/±Infinity，
    /// 一次位掩码判定替代 IsNaN+IsInfinity 两次调用；有限样本取绝对值后钳制到 1，
    /// 与原「IsNaN||IsInfinity 置 0、越界钳 1」逐位等价（见 WaveMathTests 全空间扫描）。
    /// </summary>
    internal static double EnvelopeSample(float x)
    {
        var bits = BitConverter.SingleToUInt32Bits(x);
        if ((bits & 0x7F800000u) == 0x7F800000u) return 0.0; // 指数位全 1：NaN 或 ±Infinity
        var v = Math.Abs((double)x);
        return v > 1.0 ? 1.0 : v;
    }
    /// <summary>
    /// 单元区间钳制（2.7.8）：x &lt; 0 ? 0 : x &gt; 1 ? 1 : x 双比较分支链，
    /// 与 Math.Clamp(x, 0, 1) 在全部 double 输入上逐位一致——NaN 透传、±Inf 钳到端点、±0 保持；
    /// 声波纹热路径（模拟节拍每帧、WASAPI 10ms 包络窗）各省一次范围检查调用。
    /// </summary>
    internal static double ClampUnit(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;
}
