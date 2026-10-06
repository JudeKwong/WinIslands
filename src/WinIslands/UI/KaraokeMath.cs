using System;

namespace WinIslands.UI;

/// <summary>
/// 逐字卡拉OK渲染的纯数值工具（2.3.6）：颜色通道混合。
/// 过渡字每帧按填充进度从底色向高亮色插值；提取为纯函数便于单元测试，
/// 并对非法进度（NaN/Infinity）与越界进度做钳制，杜绝黑色字节（旧代码 NaN 强转 byte 得 0）。
/// </summary>
internal static class KaraokeMath
{
    /// <summary>
    /// 混合单个颜色通道：frac=0 返回 from，frac=1 返回 to，中间线性插值（与旧实现一致地截断取整）。
    /// 非法进度按 from（未点亮）兜底；越界进度钳制到端点，绝不产生越界字节值。
    /// </summary>
    internal static byte BlendChannel(byte from, byte to, double frac)
    {
        if (!double.IsFinite(frac)) return from;
        if (frac <= 0.0) return from;
        if (frac >= 1.0) return to;
        return (byte)(from + (to - from) * frac);
    }
}
