using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIslands.Services;

/// <summary>
/// Persistent cache for remotely fetched lyrics (online providers / AMLL).
/// Keeps the last successful payload per (source, track) so replaying a song
/// does not hit the network again; entries expire after 30 days and the store
/// is bounded to avoid unbounded disk growth.
/// </summary>
public sealed class LyricsCacheStore
{
    public const int DefaultMaxEntries = 300;
    public const int MaxTextLength = 256 * 1024;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(30);

    private readonly string _directory;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly object _gate = new();

    public LyricsCacheStore(string? directory = null, TimeSpan? ttl = null, int maxEntries = DefaultMaxEntries)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? AppPaths.LyricsCacheDir : directory;
        _ttl = ttl ?? DefaultTtl;
        _maxEntries = Math.Max(1, maxEntries);
    }

    public string DirectoryPath => _directory;

    /// <summary>Stable, file-system-safe cache key. Source is part of the key so
    /// switching providers never mixes payloads.</summary>
    internal static string KeyFor(LyricsSourceKind source, TrackInfo track)
    {
        var material = string.Join('\u0001',
            source.ToString(),
            LyricsService.Sanitize(track.Artist),
            LyricsService.Sanitize(track.Title),
            LyricsService.Sanitize(track.Album));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    internal static bool IsFresh(DateTimeOffset savedAtUtc, DateTimeOffset nowUtc, TimeSpan ttl)
        => ttl > TimeSpan.Zero
           && savedAtUtc <= nowUtc.AddMinutes(5)
           && nowUtc - savedAtUtc <= ttl;

    public bool TryRead(LyricsSourceKind source, TrackInfo track, out string text)
    {
        text = string.Empty;
        var path = PathFor(source, track);
        try
        {
            lock (_gate)
            {
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path, Encoding.UTF8);
                var entry = JsonSerializer.Deserialize<CacheEntry>(json);
                if (entry is null || entry.Version != 1) return false;
                if (!string.Equals(entry.Source, source.ToString(), StringComparison.Ordinal)) return false;
                if (!IsFresh(entry.SavedAtUtc, DateTimeOffset.UtcNow, _ttl)) return false;
                if (string.IsNullOrWhiteSpace(entry.Text)) return false;
                text = entry.Text;
                // Refresh LRU stamp without touching SavedAtUtc (TTL anchor).
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Lyrics cache read failed: {ex.Message}");
            return false;
        }
    }

    public void Write(LyricsSourceKind source, TrackInfo track, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return;
        var path = PathFor(source, track);
        var temp = path + ".tmp";
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                var entry = new CacheEntry
                {
                    Version = 1,
                    Source = source.ToString(),
                    Title = track.Title ?? string.Empty,
                    Artist = track.Artist ?? string.Empty,
                    Album = track.Album ?? string.Empty,
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Text = text,
                };
                File.WriteAllText(temp, JsonSerializer.Serialize(entry), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
                PruneLocked();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Lyrics cache write failed: {ex.Message}");
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Delete expired entries and the oldest entries above the cap.</summary>
    public int Prune()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                return PruneLocked();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Lyrics cache prune failed: {ex.Message}");
                return 0;
            }
        }
    }

    private int PruneLocked()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*.json");
        if (files.Length == 0) return 0;
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        var survivors = new List<FileInfo>(files.Length);
        foreach (var file in files)
        {
            var expired = false;
            try
            {
                var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file.FullName, Encoding.UTF8));
                expired = entry is null || !IsFresh(entry.SavedAtUtc, now, _ttl);
            }
            catch
            {
                expired = true; // corrupt cache entries are disposable
            }
            if (expired)
            {
                try { file.Delete(); removed++; } catch { }
            }
            else
            {
                survivors.Add(file);
            }
        }
        foreach (var file in survivors.OrderBy(f => f.LastWriteTimeUtc).Take(Math.Max(0, survivors.Count - _maxEntries)))
        {
            try { file.Delete(); removed++; } catch { }
        }
        return removed;
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_directory)) return;
                foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Lyrics cache clear failed: {ex.Message}");
            }
        }
    }

    public (int Count, long Bytes) GetStats()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_directory)) return (0, 0);
                var files = new DirectoryInfo(_directory).GetFiles("*.json");
                return (files.Length, files.Sum(f => f.Length));
            }
            catch
            {
                return (0, 0);
            }
        }
    }

    private string PathFor(LyricsSourceKind source, TrackInfo track)
        => Path.Combine(_directory, KeyFor(source, track) + ".json");

    private sealed class CacheEntry
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("artist")] public string Artist { get; set; } = string.Empty;
        [JsonPropertyName("album")] public string Album { get; set; } = string.Empty;
        [JsonPropertyName("savedAtUtc")] public DateTimeOffset SavedAtUtc { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }
}