using System;

namespace WinIslands.UI;

/// <summary>
/// 收敛/容差比较的纯函数（2.7.7）。
/// 动画热路径（紧凑尺寸收敛判定等）原先用 Math.Abs(a - b) &lt; eps 判断两个值是否足够接近，
/// 公开 Abs 是符号掩码 + 比较对；这里改为对「单次求得的差值」做双边界分支链
/// （d &lt; eps &amp;&amp; d &gt; -eps），在全部 double 输入上与 Math.Abs 形式布尔一致
/// （NaN/±Inf/±0/边界值均一致），热路径省掉绝对值调用。
/// </summary>
public static class ToleranceMath
{
    /// <summary>两个值是否在 eps 内接近（等价于 |a - b| &lt; eps，用差值 + 双边界比较判定）。</summary>
    public static bool NearWithin(double a, double b, double eps)
    {
        var d = a - b;
        return d < eps && d > -eps;
    }
}
