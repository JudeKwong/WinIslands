using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WinIslands.Services;

namespace WinIslands.Mac.Services;

/// <summary>
/// AppleScript 媒体提供者：通过 osascript 读取并控制 Music.app / Spotify。
/// macOS 没有 Windows SMTC 那样的全局媒体 API，只能按应用脚本接入（Cider 走 HTTP API，不在此列）。
/// 覆盖：曲目信息、播放状态、时长/位置/音量、播放控制与跳转；
/// 封面：Spotify 返回 artwork URL（由协调器下载缓存），Music.app 把 artwork 二进制导出到缓存目录。
/// 脚本仅在对应应用进程存在时才执行（先查进程再跑 osascript），避免无谓的进程开销与误启动。
/// </summary>
public sealed class AppleScriptMediaProvider
{
    private const char Sep = (char)31; // AppleScript 输出字段分隔符（ASCII 31）
    private const int ScriptTimeoutMs = 3000;

    private string _lastMusicArtKey = "";
    private string _lastSpotifyArtKey = "";

    /// <summary>读取当前快照：优先「正在播放」的应用，其次「暂停中」的应用；都没有返回 null。</summary>
    /// <param name="artworkDir">Music.app 封面导出目录（AppPaths.ThumbCacheDir）。</param>
    public MediaSnapshot? GetSnapshot(string artworkDir)
    {
        MediaSnapshot? paused = null;

        if (IsAppRunning("Music"))
        {
            var snap = QueryApp("Music", artworkDir);
            if (snap is not null)
            {
                if (snap.Status == PlaybackStatus.Playing) return snap;
                paused = snap;
            }
        }

        if (IsAppRunning("Spotify"))
        {
            var snap = QueryApp("Spotify", artworkDir);
            if (snap is not null)
            {
                if (snap.Status == PlaybackStatus.Playing) return snap;
                paused ??= snap;
            }
        }

        return paused;
    }

    // ── 播放控制（返回是否成功；在 Task 中调用以免阻塞 UI 线程）────────────

    public Task<bool> TogglePlayPauseAsync(string app) => RunControlAsync(app, "playpause");
    public Task<bool> PlayAsync(string app) => RunControlAsync(app, "play");
    public Task<bool> PauseAsync(string app) => RunControlAsync(app, "pause");
    public Task<bool> NextAsync(string app) => RunControlAsync(app, "next track");
    public Task<bool> PreviousAsync(string app) => RunControlAsync(app, "previous track");

    public Task<bool> SeekAsync(string app, double seconds)
    {
        var s = Math.Max(0, seconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return RunControlAsync(app, $"set player position to {s}");
    }

    /// <param name="volume01">0..1 音量。</param>
    public Task<bool> SetVolumeAsync(string app, double volume01)
    {
        var v = (int)Math.Round(Math.Clamp(volume01, 0, 1) * 100);
        return RunControlAsync(app, $"set sound volume to {v}");
    }

    // ── 查询 ─────────────────────────────────────────────────────────

    private static bool IsAppRunning(string app)
    {
        try { return Process.GetProcessesByName(app).Length > 0; }
        catch { return false; }
    }

    private MediaSnapshot? QueryApp(string app, string artworkDir)
    {
        var output = RunOsascript(QueryScript(app));
        if (string.IsNullOrWhiteSpace(output)) return null;
        var parts = output.Trim().Split(Sep);
        if (parts.Length < 7) return null;

        var state = parts[0].Trim().ToLowerInvariant();
        var status = state switch
        {
            "playing" => PlaybackStatus.Playing,
            "paused" => PlaybackStatus.Paused,
            "stopped" => PlaybackStatus.Stopped,
            _ => PlaybackStatus.Closed,
        };
        if (status is PlaybackStatus.Closed or PlaybackStatus.Stopped) return null;

        var title = parts[1].Trim();
        var artist = parts[2].Trim();
        var album = parts[3].Trim();
        double durationSec = ParseDouble(parts[4]);   // Spotify 返回毫秒
        double positionSec = ParseDouble(parts[5]);
        double volume01 = ParseDouble(parts[6]) / 100.0;
        var artworkUrl = parts.Length > 7 ? parts[7].Trim() : "";

        var isSpotify = app == "Spotify";
        if (isSpotify) durationSec /= 1000.0; // Spotify duration 为毫秒

        string artworkPath = "";
        if (!isSpotify)
        {
            // Music.app：把当前曲目封面导出到缓存（同一曲目只导出一次）
            var key = ArtKey(title, artist, album);
            artworkUrl = "";
            if (_lastMusicArtKey != key)
            {
                _lastMusicArtKey = key;
                artworkPath = ExportMusicArtwork(key, artworkDir);
            }
            else
            {
                artworkPath = FindArtworkFile(key, artworkDir);
            }
        }
        else if (!string.IsNullOrEmpty(artworkUrl))
        {
            _lastSpotifyArtKey = ArtKey(title, artist, album);
        }

        return new MediaSnapshot
        {
            Track = new TrackInfo(
                Title: string.IsNullOrEmpty(title) ? "未知曲目" : title,
                Artist: artist,
                Album: album,
                AlbumArtist: "",
                SourceAppName: app,
                SourceAppId: isSpotify ? "com.spotify.client" : "com.apple.Music",
                ArtworkPath: artworkPath,
                ArtworkUrl: artworkUrl,
                Duration: TimeSpan.FromSeconds(Math.Max(0, durationSec))),
            Source = MediaSourceKind.WindowTitle, // 复用枚举的「窗口级来源」位（Mac 无 SMTC）
            Status = status,
            PositionSeconds = Math.Max(0, positionSec),
            DurationSeconds = Math.Max(0, durationSec),
            Volume = volume01 is >= 0 and <= 1 ? volume01 : null,
            HasVolumeControl = true,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    private static string QueryScript(string app) => app switch
    {
        // Spotify：duration 为毫秒；带 artwork url
        "Spotify" => $@"
if application ""Spotify"" is running then
	set sep to (ASCII character 31)
	tell application ""Spotify""
		set st to (player state as string)
		if st is ""playing"" or st is ""paused"" then
			set tr to current track
			return st & sep & (name of tr) & sep & (artist of tr) & sep & (album of tr) & sep & ((duration of tr) as string) & sep & ((player position) as string) & sep & ((sound volume) as string) & sep & (artwork url of tr)
		end if
	end tell
end if
return """"",

        // Music.app：duration/position 为秒
        _ => $@"
if application ""Music"" is running then
	set sep to (ASCII character 31)
	tell application ""Music""
		set st to (player state as string)
		if st is ""playing"" or st is ""paused"" then
			set tr to current track
			return st & sep & (name of tr) & sep & (artist of tr) & sep & (album of tr) & sep & ((duration of tr) as string) & sep & ((player position) as string) & sep & ((sound volume) as string)
		end if
	end tell
end if
return """"",
    };

    /// <summary>把 Music.app 当前曲目封面的原始数据导出到缓存文件（失败返回空串）。</summary>
    private static string ExportMusicArtwork(string key, string artworkDir)
    {
        try
        {
            Directory.CreateDirectory(artworkDir);
            var path = Path.Combine(artworkDir, key + ".img");
            var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var script = $@"
if application ""Music"" is running then
	tell application ""Music""
		try
			set tr to current track
			set d to (data of artwork 1 of tr)
			set fref to (open for access (POSIX file ""{escaped}"") with write permission)
			set eof fref to 0
			write d to fref
			close access fref
			return ""OK""
		on error
			return """"
		end try
	end tell
end if
return """"";
            var result = RunOsascript(script);
            if (!string.IsNullOrEmpty(result) && result.Contains("OK") && File.Exists(path) && new FileInfo(path).Length > 0)
                return path;
            try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
        }
        catch { }
        return "";
    }

    private static string FindArtworkFile(string key, string artworkDir)
    {
        try
        {
            var path = Path.Combine(artworkDir, key + ".img");
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        }
        catch { }
        return "";
    }

    private static string ArtKey(string title, string artist, string album)
    {
        var raw = $"{title}\u0001{artist}\u0001{album}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "macart-" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static double ParseDouble(string s)
        => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static async Task<bool> RunControlAsync(string app, string command)
    {
        var script = app == "Spotify"
            ? $@"
if application ""Spotify"" is running then
	tell application ""Spotify"" to {command}
	return ""OK""
end if
return """""
            : $@"
if application ""Music"" is running then
	tell application ""Music"" to {command}
	return ""OK""
end if
return """"";
        var result = await Task.Run(() => RunOsascript(script)).ConfigureAwait(false);
        return result is not null && result.Contains("OK");
    }

    /// <summary>执行 osascript 并返回 stdout（失败/超时返回 null）。</summary>
    private static string? RunOsascript(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(ScriptTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var output = stdout.Result;
            _ = stderr.Result;
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
