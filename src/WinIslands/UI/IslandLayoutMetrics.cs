using System;

namespace WinIslands.UI;

/// <summary>一次展开布局的计算结果（内部单位均为 WPF DIP）。</summary>
public readonly record struct IslandExpandedLayout(
    double VisualWidth,
    double VisualHeight,
    double WindowWidth,
    double WindowHeight,
    double FitScale,
    double PhysicalWidth,
    double PhysicalHeight,
    bool IsClamped)
{
    public bool FitsWorkArea(double workAreaWidth) => WindowWidth <= workAreaWidth + 0.5;
}

/// <summary>
/// 展开/收起几何的纯计算部分（与 WPF 解耦，便于跨 DPI / 多显示器回归测试）。
///
/// 目标：
/// 1. 展开卡片在正常显示器上保持统一参考尺寸；
/// 2. 在小屏、竖屏、低分辨率或超大任务栏的工作区上自动等比收缩，绝不超出屏幕被裁切；
/// 3. 收缩有下限，避免缩成不可用的小条；圆角始终不超过高度的一半，保证收起后仍是圆角；
/// 4. DPI 只影响物理像素，不改变 DIP 布局（PerMonitorV2 下 WPF 已做换算）。
/// </summary>
public static class IslandLayoutMetrics
{
    /// <summary>展开卡片与工作区左右至少保留的安全边距（DIP）。</summary>
    public const double WorkAreaSideMargin = 24.0;
    /// <summary>展开卡片与工作区上下至少保留的安全边距（DIP）。</summary>
    public const double WorkAreaVerticalMargin = 16.0;
    /// <summary>自动收缩的视觉下限：低于此值改用滚动，而不是继续压扁。</summary>
    public const double MinFitScale = 0.62;

    public static IslandExpandedLayout ComputeExpanded(
        double referenceWidth,
        double referenceHeight,
        double fontScale,
        double dpiScale,
        double workAreaWidth,
        double workAreaHeight,
        double topOffset = 6.0)
    {
        if (!double.IsFinite(referenceWidth) || referenceWidth <= 0) referenceWidth = 496;
        if (!double.IsFinite(referenceHeight) || referenceHeight <= 0) referenceHeight = 190;
        if (!double.IsFinite(fontScale) || fontScale <= 0) fontScale = 1;
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1;
        if (!double.IsFinite(workAreaWidth) || workAreaWidth <= 0) workAreaWidth = referenceWidth + WorkAreaSideMargin * 2;
        if (!double.IsFinite(workAreaHeight) || workAreaHeight <= 0) workAreaHeight = referenceHeight + WorkAreaVerticalMargin * 2;
        if (!double.IsFinite(topOffset) || topOffset < 0) topOffset = 0;

        var usableWidth = Math.Max(1, workAreaWidth - WorkAreaSideMargin * 2);
        var usableHeight = Math.Max(1, workAreaHeight - WorkAreaVerticalMargin * 2 - topOffset);

        var widthScale = usableWidth / referenceWidth;
        var heightScale = usableHeight / referenceHeight;
        var fit = Math.Clamp(Math.Min(1.0, Math.Min(widthScale, heightScale)), MinFitScale, 1.0);

        var visualWidth = referenceWidth * fit;
        var visualHeight = referenceHeight * fit;
        var logicalWidth = Math.Max(1, visualWidth / fontScale);
        var logicalHeight = Math.Max(1, visualHeight / fontScale);

        return new IslandExpandedLayout(
            visualWidth,
            visualHeight,
            // 宿主窗口需要完整容纳卡片；紧凑态窗口宽度由调用方另行取最大值。
            visualWidth,
            logicalHeight,
            fit,
            visualWidth * dpiScale,
            visualHeight * dpiScale,
            fit < 0.9999);
    }

    /// <summary>圆角必须为正且不超过高度一半；否则收起后会出现“方形/直边”观感。</summary>
    public static double SafeCornerRadius(double requested, double height)
    {
        if (!double.IsFinite(height) || height <= 0) return 0;
        if (!double.IsFinite(requested) || requested <= 0) return 0;
        return Math.Min(requested, height / 2.0);
    }

    /// <summary>展开顶栏（日期/时间/设置按钮）在给定宽度内是否完整可见。</summary>
    public static bool HeaderFits(double cardWidth, double dateWidth, double clockWidth, double settingsWidth,
        double horizontalPadding = 20, double safeGap = 10)
    {
        if (!double.IsFinite(cardWidth) || cardWidth <= 0) return false;
        var required = dateWidth + clockWidth + settingsWidth + horizontalPadding + safeGap;
        return cardWidth >= required;
    }
}