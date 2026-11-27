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
    private const int MaxArtworkBytes = 10 * 1024 * 1024;

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
                if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
                tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, path, overwrite: true);
                tmp = null;
            }
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

    public static async Task<string> DownloadAsync(string url, string key, CancellationToken ct = default)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return string.Empty;

            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return string.Empty;
            if (resp.Content.Headers.ContentLength is long declared && !IsArtworkSizeAllowed(declared)) return string.Empty;

            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + read > MaxArtworkBytes) return string.Empty;
                    await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
            return SaveBytes(ms.ToArray(), key);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Artwork download failed for {url}: {ex.Message}");
            return string.Empty;
        }
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

