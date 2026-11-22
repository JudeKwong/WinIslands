using System.Text.Json;
using WinIslands.Services;

namespace WinIslands.Tests;

/// <summary>默认组件精简、功能清理迁移与通知来源配置。</summary>
public class LeanDefaultsTests : IDisposable
{
    private readonly string? _origAppData = Environment.GetEnvironmentVariable("WINISLANDS_APPDATA");
    private readonly string _dir;

    public LeanDefaultsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "WinIslandsLean-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", _dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", _origAppData);
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void NewSettings_UseLeanDefaults()
    {
        var s = new AppSettings();

        Assert.Equal(7, s.Version);
        Assert.True(s.Components.TimeWhenIdle);
        Assert.False(s.Components.DateWhenIdle);
        Assert.False(s.WaveVisualizerEnabled);
        Assert.False(s.WaveSyncEnabled);
        Assert.False(s.AmllTtmlEnabled);
        Assert.True(s.LowPowerMode);
        Assert.Equal("Daozai", s.ThemePreset);
        Assert.True(s.NotificationTakeoverEnabled);
        Assert.Equal(NotificationAppFilterMode.All, s.NotificationAppFilterMode);
        Assert.Empty(s.NotificationAppFilters);
    }

    [Fact]
    public void FeatureCleanupV6_RemovesDeletedWidgets_AndKeepsUserChoices()
    {
        var s = new AppSettings
        {
            Version = 5,
            PageOrder = new List<string> { "Music", "Schedule", "Todo" },
            HiddenPages = new List<string> { "Schedule" },
            WidgetOrder = "Time,Clipboard,Todo,Timer,Schedule,Holiday,Meeting,ScreenCap,Recording,Mic,Cam",
            UsageMergeItems = new List<string> { "Mic", "Meeting", "Cam", "Recording" },
            ComponentIcons = new Dictionary<string, string>
            {
                ["Time"] = "T",
                ["Todo"] = "X",
                ["Clipboard"] = "C",
                ["Meeting"] = "M",
            },
            CopyToastEnabled = false,
            NotificationAppFilterMode = NotificationAppFilterMode.Blacklist,
            NotificationAppFilters = new List<string> { "QQ" },
        };

        Assert.True(AppSettings.ApplyFeatureCleanupV6(s));
        Assert.Equal(6, s.Version);
        Assert.Equal(new[] { "Music" }, s.PageOrder);
        Assert.Empty(s.HiddenPages);
        Assert.Equal("Time,Mic,Cam", s.WidgetOrder);
        Assert.Equal(new[] { "Mic", "Cam" }, s.UsageMergeItems);
        Assert.Equal("T", s.ComponentIcons["Time"]);
        Assert.False(s.ComponentIcons.ContainsKey("Todo"));
        Assert.False(s.ComponentIcons.ContainsKey("Clipboard"));
        Assert.False(s.ComponentIcons.ContainsKey("Meeting"));
        Assert.False(s.CopyToastEnabled);
        Assert.Equal(NotificationAppFilterMode.Blacklist, s.NotificationAppFilterMode);
        Assert.Contains("QQ", s.NotificationAppFilters);
    }

    [Fact]
    public void FeatureCleanupV6_IsNoOp_OnCurrentVersion()
    {
        var s = new AppSettings { Version = 6, WidgetOrder = "Time,Weather" };
        Assert.False(AppSettings.ApplyFeatureCleanupV6(s));
        Assert.Equal("Time,Weather", s.WidgetOrder);
    }

    [Fact]
    public void LegacyAppearance_MigratesToDaozaiBlack()
    {
        var s = new AppSettings
        {
            Version = 2,
            ThemePreset = "Default",
            Opacity = 0.92,
            OffsetY = 8,
            CompactHeight = 72,
            ExpandedWidth = 400,
            CornerRadius = 28,
        };

        Assert.True(AppSettings.ApplyDaozaiDefaultsV3(s));
        Assert.Equal(3, s.Version);
        Assert.Equal("Daozai", s.ThemePreset);
        Assert.Equal(1.0, s.Opacity);
        Assert.Equal(6, s.OffsetY);
        Assert.Equal(48, s.CompactHeight);
        Assert.Equal(420, s.ExpandedWidth);
        Assert.Equal(33, s.CornerRadius);
    }

    [Fact]
    public void LiquidGlassConfig_MigratesToBlackV4()
    {
        var s = new AppSettings { Version = 3, ThemePreset = "DaozaiLiquid" };

        Assert.True(AppSettings.ApplyBlackDaozaiV4(s));
        Assert.Equal(4, s.Version);
        Assert.Equal("Daozai", s.ThemePreset);
    }

    [Fact]
    public void LiquidGlassPreset_MigratesToBlackV7()
    {
        var s = new AppSettings { Version = 6, ThemePreset = "DaozaiLiquid" };

        Assert.True(AppSettings.ApplyBlackIslandV7(s));
        Assert.Equal(7, s.Version);
        Assert.Equal("Daozai", s.ThemePreset);
    }

    [Fact]
    public void NotificationAccess_MigratesToV5()
    {
        var s = new AppSettings { Version = 4, NotificationTakeoverEnabled = false };
        Assert.True(AppSettings.ApplyNotificationAccessV5(s));
        Assert.Equal(5, s.Version);
        Assert.True(s.NotificationTakeoverEnabled);
    }

    [Fact]
    public void SettingsService_LoadsLegacyConfig_AndPersistsV7()
    {
        var json = JsonSerializer.Serialize(new AppSettings
        {
            Version = 1,
            Components = new ComponentFlags { DateWhenIdle = true, TimeWhenIdle = true },
            WaveVisualizerEnabled = true,
            WaveSyncEnabled = true,
            AmllTtmlEnabled = true,
            LowPowerMode = false,
            WidgetOrder = "Time,Clipboard,Todo,Weather",
            UsageMergeItems = new List<string> { "Mic", "Meeting" },
        });
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);

        var service = new SettingsService();

        Assert.Equal(7, service.Current.Version);
        Assert.Equal("Daozai", service.Current.ThemePreset);
        Assert.False(service.Current.Components.DateWhenIdle);
        Assert.False(service.Current.WaveVisualizerEnabled);
        Assert.True(service.Current.LowPowerMode);
        Assert.Equal("Time,Weather", service.Current.WidgetOrder);

        // 迁移后已落盘：再读取一次仍是 v7，不会重复迁移
        var again = new SettingsService();
        Assert.Equal(7, again.Current.Version);
        Assert.False(again.Current.Components.DateWhenIdle);
    }
}