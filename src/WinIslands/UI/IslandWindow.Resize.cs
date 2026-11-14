using System;
using System.Windows;
using System.Windows.Input;
using Point = System.Windows.Point;

namespace WinIslands.UI;

/// <summary>
/// 边缘拖拽缩放（参考 BrimDeck 的 ResizeGrips 交互模型）：
/// - 未展开：左右边缘改变宽度（以岛中心为锚点左右同时伸缩），下边缘改变高度；
/// - 已展开：所有页面统一固定为参考项目的 419×140；不提供边缘拖拽缩放；
/// - 未展开时鼠标靠近边缘会切换缩放光标，按下即进入缩放，不会误触“点击展开/收起”；
/// - 手动缩放仅作用于紧凑态，会关闭对应方向的“自动调整尺寸”；
/// - 上锁只约束“移动位置”；未展开时仍可拖边缘调整紧凑尺寸。
/// </summary>
public partial class IslandWindow
{
    // 紧凑态视觉尺寸范围（与 ManualCompactW / ManualCompactH 的钳制保持一致）
    private const double MinCompactVisualW = 240;
    private const double MaxCompactVisualW = 520;
    private const double MinCompactVisualH = 48;
    private const double MaxCompactVisualH = 140;
    // 展开态视觉尺寸范围：历史保留，用于透明宿主窗口容量计算；当前展开态统一为 419×140。
    private const double MinExpandedVisualW = 320;
    private const double MaxExpandedVisualW = 620;
    private const double MinExpandedVisualH = 240;
    private const double MaxExpandedVisualH = 620;

    private bool _resizing;
    private bool _resizeExpanded;
    private IslandResizeEdges _resizeEdges;
    private Point _resizeStartPoint;
    private double _resizeStartVisualW;
    private double _resizeStartVisualH;
    private bool _inUpdateEdgeResize; // 防止拖拽预览 → 布局 → 鼠标事件 重入

    /// <summary>拖拽起始视觉尺寸必须为有限正值：Card.ActualWidth/Height 在首次布局前为 0 或 NaN，
    /// Math.Max(1, NaN) 在 .NET 仍返回 NaN，会污染 ComputeSize → Card.Width=NaN → 崩溃。</summary>
    private static double FiniteStart(double v, double fallback) => double.IsFinite(v) && v > 0 ? v : fallback;

    /// <summary>当前是否正处于边缘缩放拖拽中。</summary>
    internal bool IsEdgeResizing => _resizing;

    private void InitializeEdgeResize()
    {
        Card.LostMouseCapture += (_, _) =>
        {
            if (_resizing) EndEdgeResize();
        };
        // 3.1.2beta2 稳定性：窗口失活（被其他窗口抢占焦点 / 注入弹窗 / 锁屏）时
        // Mouse.Capture 会静默丢失，_resizing 仍为 true 会留下一个「卡住的捕获」，
        // 后续鼠标消息走陈旧几何，是 WPF 原生访问违稳的经典诱因。这里强制收尾。
        Deactivated += (_, _) =>
        {
            if (_resizing) EndEdgeResize();
        };
    }

    /// <summary>卡片在窗口坐标系中的实际视觉矩形（已包含 LayoutTransform 字号缩放）。</summary>
    private Rect CardVisualBounds()
    {
        try
        {
            var transform = Card.TransformToAncestor(this);
            var topLeft = transform.Transform(new Point(0, 0));
            var bottomRight = transform.Transform(
                new Point(Math.Max(1, Card.ActualWidth), Math.Max(1, Card.ActualHeight)));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private IslandResizeEdges HitTestResizeEdges(Point local)
    {
        var bounds = CardVisualBounds();
        if (bounds.IsEmpty) return IslandResizeEdges.None;
        return IslandResizeMath.HitTest(
            local.X, local.Y,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            locked: false); // 上锁仅限制移动位置，边缘缩放始终可用
    }

    /// <summary>悬停时切换缩放光标；离开边缘恢复默认光标。</summary>
    private void UpdateEdgeResizeCursor(Point local)
    {
        if (_vm.IsExpanded) return; // 展开尺寸固定为参考项目的 419×140，不再提供拖拽调整。
        var edges = HitTestResizeEdges(local);
        Cursor = edges switch
        {
            IslandResizeEdges.Left or IslandResizeEdges.Right => System.Windows.Input.Cursors.SizeWE,
            IslandResizeEdges.Bottom => System.Windows.Input.Cursors.SizeNS,
            IslandResizeEdges.Left | IslandResizeEdges.Bottom => System.Windows.Input.Cursors.SizeNWSE,
            IslandResizeEdges.Right | IslandResizeEdges.Bottom => System.Windows.Input.Cursors.SizeNESW,
            _ => null,
        };
    }

    /// <summary>按下时若命中边缘则进入缩放；返回 true 表示事件已被缩放接管。</summary>
    private bool TryBeginEdgeResize(Point local)
    {
        if (_vm.IsExpanded) return false; // 展开尺寸固定为参考项目的 419×140，不再提供拖拽调整。
        var edges = HitTestResizeEdges(local);
        if (edges == IslandResizeEdges.None) return false;

        _resizing = true;
        _resizeExpanded = _vm.IsExpanded;
        _resizeEdges = edges;
        _resizeStartPoint = local;
        // 首次布局前 ActualWidth/Height 可能为 0 或 NaN：Math.Max(1, NaN) 仍为 NaN，
        // 会把 NaN 灌入 ComputeSize 与 Card.Width，导致后续布局崩溃。用有限正值兜底。
        _resizeStartVisualW = FiniteStart(Card.ActualWidth, MinCompactVisualW) * FontScale;
        _resizeStartVisualH = FiniteStart(Card.ActualHeight, MinCompactVisualH) * FontScale;

        _collapseTimer.Stop();

        // 展开态可能被拖得比当前透明窗口更高：先把窗口向下扩足（卡片顶对齐，视觉不位移），
        // 否则形变超出窗口会被裁切。窗口其余区域仍由 WM_NCHITTEST 穿透。
        if (_resizeExpanded && Height < MaxExpandedVisualH + 40)
        {
            Height = MaxExpandedVisualH + 40;
        }

        // 终止正在进行的展开/收起弹簧，避免它继续写回 Card 尺寸
        StopCardSprings();
        var currentW = FiniteStart(Card.ActualWidth, MinCompactVisualW);
        var currentH = FiniteStart(Card.ActualHeight, MinCompactVisualH);
        Card.BeginAnimation(FrameworkElement.WidthProperty, null);
        Card.BeginAnimation(FrameworkElement.HeightProperty, null);
        Card.Width = Math.Max(1, currentW);
        Card.Height = Math.Max(1, currentH);

        // 让内容层与当前展开状态对齐（拖拽可能打断展开/收起动画）
        if (_resizeExpanded)
        {
            PillRow.Visibility = Visibility.Collapsed;
            PillRow.Opacity = 0;
            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedContent.Opacity = 1;
            ExpandedContent.Width = Math.Max(120, Card.Width - 20 - ExpandedContentExtraHorizontalMargin);
        }
        else
        {
            PillRow.Visibility = Visibility.Visible;
            PillRow.Opacity = 1;
            ExpandedContent.Visibility = Visibility.Collapsed;
        }

        Mouse.Capture(Card);
        return true;
    }

    /// <summary>拖拽过程中实时预览尺寸（不写设置，松手后才持久化）。</summary>
    private void UpdateEdgeResize(Point local)
    {
        if (!_resizing) return;
        if (_inUpdateEdgeResize) return; // 布局回写触发鼠标事件重入时直接跳过

        var minW = _resizeExpanded ? MinExpandedVisualW : MinCompactVisualW;
        var maxW = _resizeExpanded ? MaxExpandedVisualW : MaxCompactVisualW;
        var minH = _resizeExpanded ? MinExpandedVisualH : MinCompactVisualH;
        var maxH = _resizeExpanded ? MaxExpandedVisualH : MaxCompactVisualH;

        // 同时受当前窗口容量限制，避免形变超出透明窗口被裁切
        maxW = IslandResizeMath.MaxWidthForWindow(Width, maxW, minW);

        var (width, height) = IslandResizeMath.ComputeSize(
            _resizeStartVisualW, _resizeStartVisualH,
            local.X - _resizeStartPoint.X, local.Y - _resizeStartPoint.Y,
            _resizeEdges, minW, maxW, minH, maxH);

        _inUpdateEdgeResize = true;
        try
        {
            ApplyEdgeResizePreview(width, height);
        }
        finally
        {
            _inUpdateEdgeResize = false;
        }
    }

    private void ApplyEdgeResizePreview(double visualWidth, double visualHeight)
    {
        if (!double.IsFinite(visualWidth) || !double.IsFinite(visualHeight)) return;
        var logicalWidth = Math.Max(80, visualWidth / FontScale);
        var logicalHeight = Math.Max(40, visualHeight / FontScale);

        Card.Width = logicalWidth;
        Card.Height = logicalHeight;

        if (_resizeExpanded)
        {
            ExpandedContent.Width = Math.Max(120, logicalWidth - 20 - ExpandedContentExtraHorizontalMargin);
        }
        else
        {
            UpdateCompactContentWidth(logicalWidth);
        }

        FinalizeCardShape();
    }

    /// <summary>松手：把尺寸写入设置并关闭对应方向的自动调整。</summary>
    private void EndEdgeResize()
    {
        if (!_resizing) return;

        _resizing = false;
        if (Card.IsMouseCaptured) Card.ReleaseMouseCapture();

        var edges = _resizeEdges;
        _resizeEdges = IslandResizeEdges.None;

        var visualWidth = Math.Max(1, Card.ActualWidth) * FontScale;
        var visualHeight = Math.Max(1, Card.ActualHeight) * FontScale;
        var changedWidth = (edges & (IslandResizeEdges.Left | IslandResizeEdges.Right)) != 0;
        var changedHeight = (edges & IslandResizeEdges.Bottom) != 0;

        if (!changedWidth && !changedHeight)
        {
            ApplySize();
            Cursor = null;
            return;
        }

        if (_resizeExpanded)
        {
            _settings.Update(s =>
            {
                if (changedWidth)
                {
                    s.ExpandedWidth = Math.Round(visualWidth);
                    s.ExpandedWidthAuto = false;
                }
                if (changedHeight)
                {
                    s.MaxExpandedHeight = Math.Round(visualHeight);
                    s.MaxExpandedHeightAuto = false;
                }
            });
            _expandedHeightDirty = true;
            _expandedTargetHeight = Math.Max(MinExpandedVisualH, visualHeight / FontScale);
            ExpandedContent.Width = Math.Max(120, Card.Width - 20 - ExpandedContentExtraHorizontalMargin);
        }
        else
        {
            _settings.Update(s =>
            {
                if (changedWidth)
                {
                    s.CompactWidth = Math.Round(visualWidth);
                    s.CompactWidthAuto = false;
                }
                if (changedHeight)
                {
                    s.CompactHeight = Math.Round(visualHeight);
                    s.CompactHeightAuto = false;
                }
            });
            ApplySize();
        }

        Cursor = null;
    }
}
