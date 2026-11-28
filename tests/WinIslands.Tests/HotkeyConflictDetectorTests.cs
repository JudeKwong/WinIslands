using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

public class HotkeyConflictDetectorTests
{
    [Fact]
    public void DuplicateGestures_AreDetected_RegardlessOfModifierOrderAndCase()
    {
        var conflicts = HotkeyConflictDetector.FindConflicts(new[]
        {
            ("播放", "Ctrl+Alt+P"),
            ("下一首", "alt+ctrl+p"),
            ("显示", "Ctrl+Shift+I"),
        });

        Assert.Single(conflicts);
        Assert.Equal("CTRL+ALT+VK50", conflicts[0]);
    }

    [Fact]
    public void DistinctGestures_DoNotConflict()
    {
        var conflicts = HotkeyConflictDetector.FindConflicts(new[]
        {
            ("播放", "Ctrl+Alt+P"),
            ("下一首", "Ctrl+Alt+Right"),
            ("显示", "Ctrl+Alt+I"),
        });

        Assert.Empty(conflicts);
    }

    [Fact]
    public void InvalidGestures_AreReported()
    {
        var invalid = HotkeyConflictDetector.FindInvalid(new[]
        {
            ("播放", "Ctrl+Alt+P"),
            ("坏的", "Ctrl+Nope"),
            ("空的", " "),
        });

        Assert.Single(invalid);
        Assert.Equal("Ctrl+Nope", invalid[0]);
    }

    [Fact]
    public void BlankBindings_AreIgnored()
    {
        Assert.Empty(HotkeyConflictDetector.FindConflicts(new[]
        {
            ("A", ""),
            ("B", "  "),
        }));
        Assert.Empty(HotkeyConflictDetector.FindInvalid(new[]
        {
            ("A", ""),
            ("B", "  "),
        }));
    }
}
