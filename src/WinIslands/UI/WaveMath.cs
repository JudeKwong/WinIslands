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
}
