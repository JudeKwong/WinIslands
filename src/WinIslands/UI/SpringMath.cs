using System;

namespace WinIslands.UI;

/// <summary>
/// 2.8.2: iOS 弹簧收敛阈值换算专用的数学分支链（逐位等价运行时 Math.Min/Math.Max）。
/// 本机 .NET 8 运行时探针结论：Math.Min(常量, NaN)/Math.Max(常量, NaN) 会原样透传 NaN
/// 位形（含负 NaN / 自定义负载），Math.Min(-0,+0) 返回 -0，Math.Max(-0,+0) 返回 +0
/// （IEEE 754-2019 极值语义，与实参顺序无关），相等值返回首参。所有分支链与运行时
/// 逐位一致，热路径免去 Math.Min/Math.Max 的范围检查调用。
/// </summary>
internal static class SpringMath
{
    /// <summary>
    /// Math.Min(1.0, x) 分支链：x != x ? x : x &lt; 1.0 ? x : 1.0。逐位等价运行时
    /// Math.Min——NaN 先自比较透传位形；-0.0 &lt; 1.0 返回 -0（与运行时一致）；
    /// x == 1.0 返回常量 1.0（相等值时运行时返回首参）；+Inf 落常量。仅有 1 次比较。
    /// </summary>
    internal static double MinUnit(double x) => x != x ? x : x < 1.0 ? x : 1.0;

    /// <summary>
    /// Math.Max(floor, x) 分支链：x!=x ? x : floor!=floor ? floor : x&gt;floor ? x :
    /// floor&gt;x ? floor : （相等值）±0 角处理/floor。逐位等价运行时 Math.Max——NaN
    /// 首参位形透传；有序值取大者；相等时混合 ±0 返回 +0（运行时探针确认，与实参顺序
    /// 无关），双 -0 返回 -0，双 +0 返回 +0，非零相等值位形相同返回 floor。
    /// </summary>
    internal static double MaxFloor(double x, double floor)
    {
        if (x != x) return x;
        if (floor != floor) return floor;
        if (x > floor) return x;
        if (floor > x) return floor;
        if (x == 0.0) // 数值相等且为 ±0 对：IEEE 754-2019 maximum 语义
        {
            var xi = BitConverter.DoubleToInt64Bits(x);
            var fi = BitConverter.DoubleToInt64Bits(floor);
            return (xi < 0 && fi < 0) ? -0.0 : 0.0;
        }
        return floor;
    }

    /// <summary>
    /// Math.Max(0.0, Math.Min(0.25, x)) 分支链（2.8.7）：x!=x ? x : x&lt;=0.0 ? 0.0 :
    /// x&gt;=0.25 ? 0.25 : x。逐位等价运行时 Math.Max(0.0, Math.Min(0.25, x))——NaN
    /// 位形透传（运行时 Math.Min/Math.Max 对 NaN 原样透传）；±0 与负数收敛到 +0
    /// （Math.Min(0.25,-0)=-0、Math.Max(0.0,-0)=+0，IEEE 754-2019 极值语义，与实参顺序
    /// 无关）；[0,0.25) 原样返回、0.25 及更大值收敛到 0.25、+Inf 落 0.25、-Inf 落 +0。
    /// 文本过渡淡入起始透明度走此链，省两次范围检查调用。
    /// </summary>
    internal static double ClampZeroToQuarter(double x)
    {
        if (x != x) return x;
        if (x <= 0.0) return 0.0;
        if (x >= 0.25) return 0.25;
        return x;
    }
}
