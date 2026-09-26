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

    public static void Apply(Timeline timeline, bool lowPowerMode)
        => Timeline.SetDesiredFrameRate(timeline, Current(lowPowerMode));

    internal static int Resolve(int renderTier) => (renderTier >> 16) >= 2 ? HighRefresh : Standard;
}