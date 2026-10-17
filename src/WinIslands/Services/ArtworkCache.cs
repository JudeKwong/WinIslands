using System.IO;

using System.Net.Http;

using System.Security.Cryptography;

using System.Text;

namespace WinIslands.Services;

/// <summary>
/// Downloads / extracts album art to a local cache so the UI can bind a file path
/// instead of keeping streams alive, and so Cider artwork is fetched only once.
/// </summary>
public static class ArtworkCache
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly object IoGate = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PathCache = new();
    private const int MaxArtworkBytes = 10 * 1024 * 1024;

    // 2.9.9 稳定性：失败冷却 + 并发去重，杜绝“同一 URL 每秒重试下载”造成的
    // 日志刷屏、网络抖动与线程池膨胀。失败后同一 URL 在冷却期内不再发起请求。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> FailedUntilUtc = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Inflight = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastFailureLogUtc = new();
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinIslands/0.1");
        return client;
    }

    public static string CacheKey(params string[] parts)
    {
        var joined = string.Join("\u0001", parts.Where(p => !string.IsNullOrEmpty(p)));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes)[..24];
    }

    /// <summary>Returns a previously resolved cache path for a key without touching the disk.
    /// Only positive results are cached (misses still scan the directory once).</summary>
    public static bool TryGetCachedPath(string key, out string path)
        => PathCache.TryGetValue(key, out path!);

    /// <summary>Save raw image bytes to the cache and return the file path.</summary>

    public static string SaveBytes(byte[] data, string key)
    {
        string? tmp = null;
        try
        {
            if (!IsArtworkSizeAllowed(data.Length)) return string.Empty;
            var ext = SniffExtension(data);
            var path = Path.Combine(AppPaths.ThumbCacheDir, $"{key}{ext}");
            lock (IoGate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    PathCache.TryAdd(key, path);
                    return path;
                }
                tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, path, overwrite: true);
                tmp = null;
            }
            PathCache.TryAdd(key, path);
            return path;
        }
        catch (Exception ex)
        {
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { }
            }
            AppLogger.Warn($"ArtworkCache.SaveBytes failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Download artwork with a retry-storm guard: failed URLs are cooled down, only one
    /// in-flight request per URL is allowed, and repeated failures stop spamming the log.
    /// Returns a local cache path or empty string on failure/skip.
    /// </summary>
    public static async Task<string> DownloadAsync(string url, string key, CancellationToken ct = default)
    {
        var urlKey = url.Trim();
        try
        {
            var uri = new Uri(urlKey);
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return string.Empty;
        }
        catch
        {
            return string.Empty;
        }

        // 1) Cooldown: skip URLs that failed recently instead of retrying every poll tick.
        var now = DateTime.UtcNow;
        if (FailedUntilUtc.TryGetValue(urlKey, out var until) && now < until)
            return string.Empty;

        // 2) In-flight dedupe: never start two downloads of the same URL concurrently.
        if (!Inflight.TryAdd(urlKey, 0))
            return string.Empty;

        try
        {
            using var resp = await Http.GetAsync(new Uri(urlKey), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                NoteFailure(urlKey, quiet: true);
                return string.Empty;
            }
            if (resp.Content.Headers.ContentLength is long declared && !IsArtworkSizeAllowed(declared))
            {
                NoteFailure(urlKey, quiet: true);
                return string.Empty;
            }

            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + read > MaxArtworkBytes)
                    {
                        NoteFailure(urlKey, quiet: true);
                        return string.Empty;
                    }
                    await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
            var path = SaveBytes(ms.ToArray(), key);
            if (string.IsNullOrEmpty(path))
                NoteFailure(urlKey, quiet: true);
            else
                ClearFailure(urlKey); // 成功后解除冷却，允许下次换图立即下载
            return path;
        }
        catch (Exception ex)
        {
            NoteFailure(urlKey, quiet: false);
            // 只有“冷却期外的首次失败”才打 WARN；冷却期内的同 URL 重试静默跳过。
            if (LastFailureLogUtc.TryGetValue(urlKey, out var lastLog) && now - lastLog < FailureCooldown)
                AppLogger.Debug($"Artwork download retry skipped for {urlKey}: {ex.Message}");
            else
            {
                LastFailureLogUtc[urlKey] = now;
                AppLogger.Warn($"Artwork download failed for {urlKey}: {ex.Message}");
            }
            return string.Empty;
        }
        finally
        {
            Inflight.TryRemove(urlKey, out _);
        }
    }

    private static void NoteFailure(string urlKey, bool quiet)
    {
        FailedUntilUtc[urlKey] = DateTime.UtcNow + FailureCooldown;
        if (!quiet)
            LastFailureLogUtc.TryRemove(urlKey, out _); // 强制下一次失败重新记 WARN
    }

    private static void ClearFailure(string urlKey)
    {
        FailedUntilUtc.TryRemove(urlKey, out _);
        LastFailureLogUtc.TryRemove(urlKey, out _);
    }

    public static async Task<string> SaveStreamAsync(Func<Stream, Task> writeTo, string key)
    {
        try
        {
            using var ms = new MemoryStream();
            await writeTo(ms).ConfigureAwait(false);
            return SaveBytes(ms.ToArray(), key);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Artwork stream save failed: {ex.Message}");
            return string.Empty;
        }
    }

    internal static bool IsArtworkSizeAllowed(long bytes) => bytes > 0 && bytes <= MaxArtworkBytes;

    private static string SniffExtension(byte[] data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return ".jpg";
        if (data.Length > 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ".png";
        if (data.Length > 3 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F') return ".gif";
        if (data.Length > 2 && data[0] == 'B' && data[1] == 'M') return ".bmp";
        return ".jpg";
    }
}
