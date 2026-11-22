using System.IO.Compression;
using System.Text;
using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

/// <summary>3.1.7 设置维护：恢复默认与插件安装回滚。</summary>
public sealed class SettingsMaintenanceTests : IDisposable
{
    private readonly string? _orig = Environment.GetEnvironmentVariable("WINISLANDS_APPDATA");
    private readonly string _dir;

    public SettingsMaintenanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "WinIslandsMaint-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", _dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", _orig);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ResetToDefaults_KeepsLanguage_AndResetsEverythingElse()
    {
        var s = new AppSettings
        {
            Language = "zh-CN",
            Theme = ThemeMode.Light,
            WaveVisualizerEnabled = true,
            IslandAlwaysVisible = true,
            LowPowerMode = false,
            OnboardingCompleted = true,
        };
        s.Components.TimeWhenIdle = true;
        s.Components.DateWhenIdle = true;

        s.ResetToDefaults("en-US");

        Assert.Equal("en-US", s.Language);
        Assert.Equal(ThemeMode.Auto, s.Theme);
        Assert.False(s.WaveVisualizerEnabled);
        Assert.True(s.LowPowerMode);
        Assert.True(s.OnboardingCompleted);
        Assert.Equal(8, s.Version);
        Assert.True(s.Components.TimeWhenIdle);
        Assert.False(s.Components.DateWhenIdle);
    }

    [Fact]
    public void ImportPlugin_RollsBack_WhenEntryFileMissing()
    {
        using var plugins = new PluginService();
        var zip = Path.Combine(_dir, "bad-plugin.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("plugin.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("""
            {
              "id": "bad.plugin",
              "name": "Bad",
              "version": "1.0.0",
              "entry": "run.ps1",
              "enabled": true,
              "run_at_startup": false,
              "interval_seconds": 0
            }
            """);
        }

        var ok = plugins.ImportPlugin(zip);

        Assert.False(ok);
        Assert.False(Directory.Exists(Path.Combine(AppPaths.PluginsDir, "bad.plugin")));
        Assert.Empty(Directory.Exists(AppPaths.PluginsDir)
            ? Directory.GetFileSystemEntries(AppPaths.PluginsDir)
            : Array.Empty<string>());
    }
}
