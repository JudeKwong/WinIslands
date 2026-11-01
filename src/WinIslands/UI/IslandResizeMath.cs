using System;

namespace WinIslands.UI;

[Flags]
public enum IslandResizeEdges
{
    None = 0,
    Left = 1,
    Right = 2,
    Bottom = 4,
}

/// <summary>
/// 边缘拖拽缩放的纯计算部分（与 WPF 视觉树解耦，便于单元测试）。
/// 交互模型参考 BrimDeck 的 ResizeGrips：边缘内侧一小段为抓取区，
/// 左右端点同时伸缩、拖动下边缘改变高度。
/// </summary>
public static class IslandResizeMath
{
    /// <summary>卡片边缘内侧的抓取带宽度（视觉 DIP）。</summary>
    public const double GripInset = 8;

    /// <summary>指针允许略微超出卡片边缘仍算命中（贴合圆角）。</summary>
    public const double GripSlack = 3;

    /// <summary>命中测试：返回指针所在的缩放边。上锁时始终为 None。</summary>
    public static IslandResizeEdges HitTest(
        double x, double y,
        double left, double top, double width, double height,
        bool locked)
    {
        if (locked) return IslandResizeEdges.None;
        if (!(width >= 8) || !(height >= 8)) return IslandResizeEdges.None;
        if (double.IsNaN(x) || double.IsNaN(y)) return IslandResizeEdges.None;

        var right = left + width;
        var bottom = top + height;
        if (x < left - GripSlack || x > right + GripSlack) return IslandResizeEdges.None;
        if (y < top - GripSlack || y > bottom + GripSlack) return IslandResizeEdges.None;

        var edges = IslandResizeEdges.None;
        if (x <= left + GripInset) edges |= IslandResizeEdges.Left;
        else if (x >= right - GripInset) edges |= IslandResizeEdges.Right;
        if (y >= bottom - GripInset) edges |= IslandResizeEdges.Bottom;
        return edges;
    }

    /// <summary>
    /// 计算拖拽后的目标尺寸。左右边缘以中心为锚点对称伸缩（宽度变化量为位移的两倍），
    /// 下边缘直接跟随纵向位移；结果始终钳制在给定范围内。
    /// </summary>
    public static (double Width, double Height) ComputeSize(
        double startWidth, double startHeight,
        double deltaX, double deltaY,
        IslandResizeEdges edges,
        double minWidth, double maxWidth,
        double minHeight, double maxHeight)
    {
        var width = startWidth;
        if ((edges & IslandResizeEdges.Left) != 0) width = startWidth - deltaX * 2;
        else if ((edges & IslandResizeEdges.Right) != 0) width = startWidth + deltaX * 2;

        var height = startHeight;
        if ((edges & IslandResizeEdges.Bottom) != 0) height = startHeight + deltaY;

        if (!(maxWidth >= minWidth)) maxWidth = minWidth;
        if (!(maxHeight >= minHeight)) maxHeight = minHeight;

        return (Math.Clamp(width, minWidth, maxWidth), Math.Clamp(height, minHeight, maxHeight));
    }

    /// <summary>把窗口可用宽度换算成卡片可用的最大视觉宽度（两侧各留少量余量）。</summary>
    public static double MaxWidthForWindow(double windowWidth, double preferredMax, double minWidth)
    {
        if (double.IsNaN(windowWidth) || double.IsInfinity(windowWidth)) return preferredMax;
        var capacity = windowWidth - 32;
        var max = Math.Min(preferredMax, capacity);
        return max < minWidth ? minWidth : max;
    }
}
