using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinIslands.UI;

/// <summary>
/// Resolves a practical animation frame-rate ceiling for the current machine.
/// High-tier GPUs get 120 FPS; integrated/legacy render tiers stay at 60 FPS.
/// Low-power mode always caps animations at 60 FPS to avoid unnecessary power use.
/// </summary>
internal static class AnimationFrameRate
{
    private const int Standard = 60;
    private const int HighRefresh = 120;
    /// <summary>低功耗模式下的动画帧率上限（2.3.0）：弹簧 / 卡拉OK等合成帧驱动统一降到 60 FPS。</summary>
    public const int StandardForLowPower = 60;
    private static readonly int HardwareFrameRate = Resolve(RenderCapability.Tier);

    /// <summary>内置帧率档位的预计算帧间隔（2.3.7）：把热路径里每帧的 Clamp + 除法换成常数。</summary>
    private const double Interval30 = 1.0 / 30.0;
    private const double Interval60 = 1.0 / 60.0;
    private const double Interval120 = 1.0 / 120.0;

    public static int Current(bool lowPowerMode) => lowPowerMode ? Standard : HardwareFrameRate;

    /// <summary>显示器级帧率目标（不含低功耗降频），供音频采集 / 外部采样组件对齐发布节奏（2.1.8）。</summary>
    public static int DisplayTarget => HardwareFrameRate;

    public static void Apply(Timeline timeline, bool lowPowerMode)
        => Timeline.SetDesiredFrameRate(timeline, Current(lowPowerMode));

    internal static int Resolve(int renderTier) => (renderTier >> 16) >= 2 ? HighRefresh : Standard;

    /// <summary>
    /// Advances a lightweight frame deadline. This prevents 144/165 Hz compositors from
    /// running expensive visualizer math above the requested 120 FPS ceiling while avoiding
    /// the halved-frame effect of a naive "skip one frame" throttle.
    /// </summary>
    public static bool ShouldProcessFrame(double nowSeconds, ref double nextFrameSeconds, int framesPerSecond)
    {
        // 2.4.3: 非有限时钟输入绝不当成"正常帧"推进截止线——NaN/+Inf 直接放行本帧且
        // 不写 nextFrameSeconds；否则 +Inf 会把截止线永久污染成 Inf，后续所有帧都被
        // 判为"未到点"而 return false，卡拉OK/低功耗节拍从此停摆且无法自愈。
        if (!double.IsFinite(nowSeconds)) return true;

        // 常见档位直接命中预计算常数，免去每帧的 Math.Clamp 与除法（2.3.7）。
        var interval = framesPerSecond switch
        {
            30 => Interval30,
            60 => Interval60,
            120 => Interval120,
            _ => 1.0 / Math.Clamp(framesPerSecond, 30, HighRefresh),
        };
        if (nowSeconds + 0.0000001 < nextFrameSeconds) return false;

        // Resynchronize after sleep/suspend instead of trying to replay a large backlog.
        nextFrameSeconds = nextFrameSeconds < nowSeconds - interval * 4
            ? nowSeconds + interval
            : nextFrameSeconds + interval;
        return true;
    }
}