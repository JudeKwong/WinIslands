using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace WinIslands.UI;

/// <summary>
/// 展开歌词「当前行」的 iOS 式强调（2.1.2）。
///
/// 旧实现用 FontSize 动画把当前行放大：FontSize 影响测量/布局，放大的行会
/// 挤压相邻行、宽度变宽触发换行，导致歌词列表回流跳动（「歌词放大缩小跳动」）。
///
/// 本类改为只缩放渲染（RenderTransform），行高/布局完全不变：
///   - 当前行从小到大平滑放大（带轻微 Q 弹，跟随 iOS），过去行平滑缩回；
///   - 页面不回流、不换行，垂直滚动位置稳定，文字过渡丝滑。
/// 每个元素持有自己的 ScaleTransform 与 IOSSpring（ConditionalWeakTable 随元素自动回收）。
///
/// 2.3.0：驱动引擎从「固定时长 DoubleAnimation + SoftSpringEase」换成与展开/收起
/// 同一套的真实 IOSSpring 物理引擎（SpringTicker 合成帧驱动）：
///   - 物理收敛、不抢时长：动画一直跑到自然静止，结尾没有硬切/跳变；
///   - 切行打断时以当前位移 + 当前速度作为新一段的初始条件，位置与速度连续、不跳变；
///   - 进入（轻 Q 弹）与退出（果断无回弹）使用两组 UIKit 风格参数，由纯函数换算。
///
/// 2.3.2：改为「归一化进度弹簧」——弹簧只驱动 0→1 的进度，缩放与不透明度由同一个
/// 线性映射推导（进入/退出共享公式），切行打断时位置与速度天然连续，且透明度过渡
/// 与缩放完全同步（不再有独立固定时长的淡入淡出 Storyboard，文字不"额外弹出"）。
/// </summary>
public static class LyricEmphasis
{
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.RegisterAttached(
        "IsCurrent", typeof(bool), typeof(LyricEmphasis),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsCurrentChanged));

    public static void SetIsCurrent(DependencyObject o, bool v) => o.SetValue(IsCurrentProperty, v);
    public static bool GetIsCurrent(DependencyObject o) => (bool)o.GetValue(IsCurrentProperty);

    /// <summary>当前行的目标缩放倍率（如 1.23 = 13px 视觉放大到 16px）。由样式按用户字号计算写入。</summary>
    public static readonly DependencyProperty TargetScaleProperty = DependencyProperty.RegisterAttached(
        "TargetScale", typeof(double), typeof(LyricEmphasis), new PropertyMetadata(1.18));

    public static void SetTargetScale(DependencyObject o, double v) => o.SetValue(TargetScaleProperty, v);
    public static double GetTargetScale(DependencyObject o) => (double)o.GetValue(TargetScaleProperty);

    /// <summary>弹簧响应时长（毫秒）。由纯函数换算成物理响应秒（进入/退出两组参数）。</summary>
    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.RegisterAttached(
        "DurationMs", typeof(double), typeof(LyricEmphasis), new PropertyMetadata(240.0));

    public static void SetDurationMs(DependencyObject o, double v) => o.SetValue(DurationMsProperty, v);
    public static double GetDurationMs(DependencyObject o) => (double)o.GetValue(DurationMsProperty);

    private sealed class ElementState
    {
        public ScaleTransform? Scale;
        public IOSSpring? Spring;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, ElementState> States = new();

    /// <summary>低功耗兼容字段（2.4.0 起帧率上限由 SpringTicker.CapAt60Fps 统一接管；保留给 IslandWindow 赋值，无副作用）。</summary>
    public static bool LowPowerModeOverride;

    /// <summary>进入（当前行涨起）：轻 Q 弹，iOS 展开手感。</summary>
    public const double EnterZeta = 0.78;
    /// <summary>退出（过去行回落）：更高阻尼、无回弹，回落果断。</summary>
    public const double ExitZeta = 0.90;
    /// <summary>退出相对进入的响应时长比例：回落比涨起略快，节奏自然。</summary>
    private const double ExitResponseScale = 0.72;
    /// <summary>渲染写入去重阈值（2.3.1）：缩放变化小于此值时不写 RenderTransform，弹簧收敛尾部不产生无效属性写入。</summary>
    internal const double ScaleWriteEpsilon = 0.0005;
    /// <summary>非当前歌词行的基准不透明度（与 XAML/代码 style 中的 Opacity 0.28 保持一致）。</summary>
    internal const double OpacityBase = 0.28;
    /// <summary>当前歌词行的目标不透明度。</summary>
    internal const double OpacityCurrent = 1.0;

    /// <summary>由 UI 设置（DurationMs）换算「进入」弹簧参数（纯函数，便于单元测试）。</summary>
    internal static (double Zeta, double ResponseSeconds) ComputeEnterParams(double durationMs)
    {
        var response = ClampDuration(durationMs) / 1000.0;
        return (EnterZeta, response);
    }

    /// <summary>由 UI 设置换算「退出」弹簧参数：阻尼更高、收敛更快（纯函数，便于单元测试）。</summary>
    internal static (double Zeta, double ResponseSeconds) ComputeExitParams(double durationMs)
    {
        var response = ClampDuration(durationMs) * ExitResponseScale / 1000.0;
        return (ExitZeta, response);
    }

    internal static double ClampDuration(double ms)
        => WaveMath.ClampRange(double.IsFinite(ms) ? ms : 240.0, 60.0, 900.0); // 2.9.3: 分支链，逐位等价 Math.Clamp(·,60,900)

    /// <summary>渲染写入去重判定（纯函数，可测）：NaN（尚未写入）必写；非法值丢弃；与上次写入的差达到阈值才写。</summary>
    internal static bool ShouldWriteScale(double value, double lastWritten)
    {
        if (double.IsNaN(lastWritten)) return true;          // 首次写入
        if (!double.IsFinite(value)) return false;           // 引擎已兜底，这里直接丢弃非法值
        return ToleranceMath.AbsAtLeast(value - lastWritten, ScaleWriteEpsilon); // 2.8.4: 双边界分支链，布尔逐位等价 Math.Abs(d) >= eps
    }

    /// <summary>归一化进度 → (缩放, 不透明度) 的统一线性映射（纯函数，可测）：进入/退出共用，打断时数值连续。</summary>
    internal static (double Scale, double Opacity) MapProgress(double progress, double targetScale)
    {
        var p = WaveMath.ClampUnit(double.IsFinite(progress) ? progress : 0.0); // 2.8.4: 分支链，逐位等价 Math.Clamp(x,0,1)
        var ts = WaveMath.ClampRange(double.IsFinite(targetScale) ? targetScale : 1.18, 1.0, 1.5); // 2.8.4: 分支链，逐位等价 Math.Clamp(x,1.0,1.5)
        var scale = 1.0 + (ts - 1.0) * p;
        var opacity = OpacityBase + (OpacityCurrent - OpacityBase) * p;
        return (scale, opacity);
    }

    /// <summary>按 基础字号/当前行字号 计算渲染缩放倍率（纯函数，便于单元测试）。</summary>
    internal static double ComputeTargetScale(double baseSize, double currentSize)
    {
        if (!double.IsFinite(baseSize) || !double.IsFinite(currentSize) || baseSize <= 0)
            return 1.18;                                   // 非法输入兜底
        return WaveMath.ClampRange(currentSize / baseSize, 1.0, 1.35); // 2.9.0: 分支链，逐位等价 Math.Clamp(x,1.0,1.35)
    }

    private static void OnIsCurrentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        try
        {
            var state = GetState(fe);
            var scale = state.Scale!;
            var isEnter = (bool)e.NewValue;
            var targetScale = ReadTargetScale(fe);
            // 2.3.2：弹簧驱动归一化进度（enter→1.0，exit→0.0），缩放与不透明度由同一映射推导。
            var targetProgress = isEnter ? 1.0 : 0.0;
            var (zeta, response) = isEnter
                ? ComputeEnterParams(ReadDuration(fe))
                : ComputeExitParams(ReadDuration(fe));

            // iOS 打断语义：以旧弹簧的当前进度 + 当前速度作为新一段的初始条件，
            // 位置与速度都不跳变；旧弹簧从合成帧队列摘除（保留当前值作为起点）。
            var fromProgress = isEnter ? 0.0 : 1.0;
            var velocity = 0.0;
            if (state.Spring is { IsActive: true } old)
            {
                fromProgress = old.Value;
                velocity = old.Velocity;
                old.Stop();
            }

            var lastScale = double.NaN; // 2.3.1：每段弹簧独立的上次写入值（NaN = 尚未写入）
            state.Spring = IOSSpring.Create(
                zeta, response,
                from: fromProgress, to: targetProgress,
                onUpdate: p =>
                {
                    // 统一线性映射：进入/退出共用公式，打断时值连续（2.3.2）
                    var s = 1.0 + (targetScale - 1.0) * p;
                    var o = OpacityBase + (OpacityCurrent - OpacityBase) * p;
                    // 写入去重：收敛尾部每帧变化极小，跳过无效属性写入，
                    // 减少合成线程上的依赖属性变更与脏标记；首次与完成写入总是落盘。
                    if (!ShouldWriteScale(s, lastScale)) return;
                    lastScale = s;
                    scale.ScaleX = s;
                    scale.ScaleY = s;
                    fe.Opacity = o;
                },
                onCompleted: null,
                initialVelocity: velocity);
        }
        catch (Exception ex)
        {
            Services.AppLogger.Error("LyricEmphasis failed", ex);
        }
    }

    private static ElementState GetState(FrameworkElement fe)
    {
        if (!States.TryGetValue(fe, out var state))
        {
            state = new ElementState();
            States.Add(fe, state);
        }
        if (state.Scale is null)
        {
            if (fe.RenderTransform is ScaleTransform existing)
            {
                state.Scale = existing;
            }
            else
            {
                var created = new ScaleTransform(1, 1);
                fe.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                fe.RenderTransform = created;
                state.Scale = created;
            }
        }
        return state;
    }

    private static double ReadTargetScale(FrameworkElement fe)
        => WaveMath.ClampRange((double)fe.GetValue(TargetScaleProperty), 1.0, 1.5); // 2.9.0: 分支链，逐位等价 Math.Clamp(x,1.0,1.5)

    private static double ReadDuration(FrameworkElement fe)
        => (double)fe.GetValue(DurationMsProperty);
}
