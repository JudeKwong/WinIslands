using System;

namespace WinIslands.UI;

/// <summary>
/// 展开/收起的交叉淡入曲线（2.0.9）。
///
/// 背景：旧实现让展开内容在卡片形变的 72% 时就已完全不透明——卡片还没长好，
/// 内容就已全实地“悬”在卡片顶部并向下方留白，观感就是“文字从上面出现”。
/// iOS 的做法是内容淡入跟随形变：形状先长到位、内容再逐渐浮现，
/// 收起时内容先淡出、形状再收拢，避免底部行被收缩边缘“剪断”。
/// 本类集中这些纯函数，便于单元测试。
/// </summary>
public static class CrossFadeCurves
{
    /// <summary>展开时内容淡入的响应系数：&gt;1 表示略滞后于卡片形变（形状先导、内容跟随后浮现）。</summary>
    public const double ExpandContentFactor = 1.06;

    /// <summary>收起时内容淡出的响应系数：&lt;1 表示先于形变淡出（内容退场后再收拢，底部行不被剪断）。</summary>
    public const double CollapseContentFactor = 0.76;

    /// <summary>内容透明度弹簧的响应时长（秒）：由卡片形变响应时长换算。</summary>
    public static double FadeResponse(double shapeResponse, bool expand)
    {
        // NotFinite (NaN/+/-Inf) or negative: fall back to 0.03 floor so UI stays safe.
        if (!double.IsFinite(shapeResponse) || shapeResponse < 0)
            return 0.03;
        return Math.Max(0.03, shapeResponse * (expand ? ExpandContentFactor : CollapseContentFactor));
    }

    /// <summary>
    /// 胶囊行（紧凑内容）在当前展开内容透明度 v 下的目标不透明度。
    /// 展开：胶囊行在内容浮现的前 25% 期间让位（1→0），两手交接丝滑。
    /// 收起：内容淡出到最后 1/5 时胶囊行才浮现（0→1），正好配合卡片收拢到胶囊尺寸。
    /// </summary>
    public static double PillOpacity(double expandedOpacity, bool expand)
    {
        // 非法输入（NaN/Inf）：按 0 兜底，胶囊行保持可见的最安全落点
        var v = double.IsFinite(expandedOpacity) ? Math.Clamp(expandedOpacity, 0.0, 1.0) : 0.0;
        if (expand)
        {
            // v: 0→1，胶囊行在 v∈[0, 0.25] 内淡出
            return Math.Clamp(1 - v / 0.25, 0.0, 1.0);
        }
        // 收起：v 由 1→0，胶囊行在 v∈[0.2, 0] 内淡入
        return Math.Clamp((0.2 - v) / 0.2, 0.0, 1.0);
    }
}