using System.IO;

namespace WinIslands.Services;

/// <summary>Well-known file/directory locations used by the app.</summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: %APPDATA%\WinIslands；macOS: ~/Library/Application Support/WinIslands ——
    /// 配置、日志、缓存和歌词都放在这里。
    /// 测试可通过 WINISLANDS_APPDATA 环境变量重定向。
    /// </summary>
    public static string AppDataDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("WINISLANDS_APPDATA");
            if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
            // macOS 遵循 Apple 文件系统惯例（Application Support），不用 ~/.config
            if (OperatingSystem.IsMacOS())
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library", "Application Support", "WinIslands");
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIslands");
        }
    }

    // Note: computed (not cached) so tests/runtime can redirect via WINISLANDS_APPDATA.
    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    public static string LogsDir => Path.Combine(AppDataDir, "logs");

    public static string ThumbCacheDir => Path.Combine(AppDataDir, "thumbcache");

    public static string LyricsDir => Path.Combine(AppDataDir, "Lyrics");

    /// <summary>用户插件目录：%APPDATA%\\WinIslands\\plugins。</summary>
    public static string PluginsDir => Path.Combine(AppDataDir, "plugins");

    /// <summary>便携插件目录：程序 exe 同级的 plugins。</summary>
    public static string PortablePluginsDir
    {
        get
        {
            var exeDir = string.IsNullOrWhiteSpace(ExePath) ? AppContext.BaseDirectory : Path.GetDirectoryName(ExePath);
            return Path.Combine(string.IsNullOrWhiteSpace(exeDir) ? AppContext.BaseDirectory : exeDir!, "plugins");
        }
    }

    /// <summary>崩溃自动恢复标记文件：异常退出时写入，下次启动检测到后提示「已恢复」。</summary>
    public static string CrashMarkerFile => Path.Combine(AppDataDir, "crash-recovery.json");

    public static string ExePath { get; } = Environment.ProcessPath ?? string.Empty;

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ThumbCacheDir);
        Directory.CreateDirectory(LyricsDir);
    }
}


