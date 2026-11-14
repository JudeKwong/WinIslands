using System.Text.Json;
using WinIslands.Services;

namespace WinIslands.Tests;

/// <summary>默认组件精简 + 轻量化迁移（配置 v1 → v2）。</summary>
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

    /// <summary>3.1.2 及以前的默认组件组合（用于验证迁移命中）。</summary>
    private static ComponentFlags LegacyComponents()
    {
        var c = new ComponentFlags(); // 新默认 = 旧默认 + 下列 5 项差异
        c.DateWhenIdle = true;
        c.TodoWhenIdle = true;
        c.ScheduleWhenIdle = true;
        c.HolidayWhenIdle = true;
        c.ClipboardWhenPlaying = true;
        return c;
    }

    [Fact]
    public void NewSettings_UseLeanDefaults()
    {
        var s = new AppSettings();

        Assert.Equal(5, s.Version);
        Assert.True(s.Components.TimeWhenIdle);
        Assert.False(s.Components.DateWhenIdle);
        Assert.False(s.Components.TodoWhenIdle);
        Assert.False(s.Components.ScheduleWhenIdle);
        Assert.False(s.Components.HolidayWhenIdle);
        Assert.False(s.Components.ClipboardWhenPlaying);
        Assert.False(s.WaveVisualizerEnabled);
        Assert.False(s.WaveSyncEnabled);
        Assert.False(s.AmllTtmlEnabled);
        Assert.True(s.LowPowerMode);
        Assert.Equal("Daozai", s.ThemePreset);
        Assert.True(s.NotificationTakeoverEnabled);
    }

    [Fact]
    public void LegacyDefaults_MigrateToLean()
    {
        var s = new AppSettings
        {
            Version = 1,
            Components = LegacyComponents(),
            WaveVisualizerEnabled = true,
            WaveSyncEnabled = true,
            AmllTtmlEnabled = true,
            LowPowerMode = false,
        };

        Assert.True(AppSettings.ApplyLeanDefaultsV2(s));
        Assert.Equal(2, s.Version);
        Assert.True(s.Components.TimeWhenIdle);
        Assert.False(s.Components.DateWhenIdle);
        Assert.False(s.Components.TodoWhenIdle);
        Assert.False(s.Components.ScheduleWhenIdle);
        Assert.False(s.Components.HolidayWhenIdle);
        Assert.False(s.Components.ClipboardWhenPlaying);
        Assert.False(s.WaveVisualizerEnabled);
        Assert.False(s.WaveSyncEnabled);
        Assert.False(s.AmllTtmlEnabled);
        Assert.True(s.LowPowerMode);
    }

    [Fact]
    public void CustomizedComponents_ArePreserved()
    {
        var components = LegacyComponents();
        components.WeatherWhenIdle = true; // 用户自定义过组件 → 不精简
        var s = new AppSettings { Version = 1, Components = components };

        Assert.True(AppSettings.ApplyLeanDefaultsV2(s));
        Assert.True(s.Components.WeatherWhenIdle);
        Assert.True(s.Components.DateWhenIdle);
        Assert.True(s.Components.TodoWhenIdle);
        Assert.True(s.Components.ClipboardWhenPlaying);
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
    public void NotificationAccess_MigratesToV5()
    {
        var s = new AppSettings { Version = 4, NotificationTakeoverEnabled = false };
        Assert.True(AppSettings.ApplyNotificationAccessV5(s));
        Assert.Equal(5, s.Version);
        Assert.True(s.NotificationTakeoverEnabled);
    }

    [Fact]
    public void CurrentVersion_IsNoOp()
    {
        var s = new AppSettings { Version = 2 };
        Assert.False(AppSettings.ApplyLeanDefaultsV2(s));
    }

    [Fact]
    public void SettingsService_LoadsAndPersistsMigratedConfig()
    {
        var json = JsonSerializer.Serialize(new AppSettings
        {
            Version = 1,
            Components = LegacyComponents(),
            WaveVisualizerEnabled = true,
            WaveSyncEnabled = true,
            AmllTtmlEnabled = true,
            LowPowerMode = false,
        });
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);

        var service = new SettingsService();

        Assert.Equal(5, service.Current.Version);
        Assert.Equal("Daozai", service.Current.ThemePreset);
        Assert.False(service.Current.Components.DateWhenIdle);
        Assert.False(service.Current.WaveVisualizerEnabled);
        Assert.True(service.Current.LowPowerMode);

        // 迁移后已落盘：再读取一次仍是 v2，不会重复迁移
        var again = new SettingsService();
        Assert.Equal(5, again.Current.Version);
        Assert.False(again.Current.Components.DateWhenIdle);
    }
}