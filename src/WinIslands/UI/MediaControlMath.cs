using WinIslands.Services;

namespace WinIslands.UI;

/// <summary>媒体控制的纯计算规则：相对跳转与音量步进，便于单元测试。</summary>
public static class MediaControlMath
{
    public const double SeekStepSeconds = 15;
    public const double VolumeStep = 0.05;

    /// <summary>相对跳转目标：结果始终落在 0..duration 之间；duration 无效时返回 0。</summary>
    public static double SeekTarget(double positionSeconds, double durationSeconds, double deltaSeconds)
    {
        if (double.IsNaN(durationSeconds) || durationSeconds <= 0) return 0;
        if (double.IsNaN(positionSeconds)) positionSeconds = 0;
        if (double.IsNaN(deltaSeconds)) deltaSeconds = 0;
        return Math.Clamp(positionSeconds + deltaSeconds, 0, durationSeconds);
    }

    /// <summary>Repeat modes cycle None -> Track -> List -> None.</summary>
    public static MediaRepeatMode NextRepeatMode(MediaRepeatMode current) => current switch
    {
        MediaRepeatMode.None => MediaRepeatMode.Track,
        MediaRepeatMode.Track => MediaRepeatMode.List,
        _ => MediaRepeatMode.None,
    };

    /// <summary>音量步进目标：始终落在 0..1 之间。</summary>
    public static double VolumeTarget(double volume, double delta)
    {
        if (double.IsNaN(volume)) volume = 0;
        if (double.IsNaN(delta)) delta = 0;
        return Math.Clamp(volume + delta, 0, 1);
    }
}
