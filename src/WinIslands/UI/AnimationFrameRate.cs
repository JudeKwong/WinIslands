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
    private static readonly int HardwareFrameRate = Resolve(RenderCapability.Tier);

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
        var fps = Math.Clamp(framesPerSecond, 30, HighRefresh);
        var interval = 1.0 / fps;
        if (nowSeconds + 0.0000001 < nextFrameSeconds) return false;

        // Resynchronize after sleep/suspend instead of trying to replay a large backlog.
        nextFrameSeconds = nextFrameSeconds < nowSeconds - interval * 4
            ? nowSeconds + interval
            : nextFrameSeconds + interval;
        return true;
    }
}