using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinIslands.Services;

/// <summary>GitHub Release 中的一个可下载资产。</summary>
public sealed class UpdateAsset
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

/// <summary>一次更新检查的结果。</summary>
public sealed class UpdateInfo
{
    public string Tag { get; init; } = "";
    public string Version { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string Notes { get; init; } = "";
    public UpdateAsset Asset { get; init; } = new();
    public bool IsPortable => Asset.Name.Contains("-win-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 自更新：访问 GitHub Releases 最新版，比较版本，校验 SHA256 后下载。
/// 仅在你手动开启（或点击“立即检查”）时联网，不上报任何数据。
/// </summary>
public sealed class UpdaterService
{
    /// <summary>GitHub 仓库标识（账号改名前/后两个名称都可用，按顺序尝试）。</summary>
    private static readonly string[] RepoCandidates = { "JudeKwong/WinIslands", "DMP-Pig/WinIslands" };

    /// <summary>最近一次成功解析到的仓库，用于拼接回退链接。</summary>
    public static string Repo { get; private set; } = RepoCandidates[0];

    /// <summary>最近一次检查发现的新版本；为空表示当前已是最新或尚未检查。</summary>
    public UpdateInfo? LastAvailable { get; private set; }

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _downloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public event Action<string, string>? NewVersionAvailable; // version, url

    /// <summary>检查最新版本；返回 true 表示存在新版本。任何网络异常都安全返回 false。</summary>
    public async Task<bool> CheckAsync()
    {
        LastAvailable = null;
        // 逐个候选仓库尝试：账号改名后旧名会 404，新名可用；两者都试可避免更新检查静默失效。
        foreach (var repo in RepoCandidates)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{repo}/releases/latest");
                request.Headers.UserAgent.ParseAdd("WinIslands-Updater/1.0");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) continue;
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : "";
                var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : "";
                var notes = root.TryGetProperty("body", out var b) ? b.GetString() : "";
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var latest = ParseVersion(tag);
                var current = ParseVersion(CurrentVersion());
                if (latest <= 0 || current <= 0) continue;
                Repo = repo; // 记录可用仓库，回退链接使用它
                if (latest <= current) return false;

                var version = NormalizeVersion(tag);
                var asset = await SelectAssetAsync(root, version);
                if (asset is null)
                {
                    AppLogger.Debug($"Update {tag} has no compatible Windows asset; falling back to release page.");
                    NewVersionAvailable?.Invoke(tag, url ?? $"https://github.com/{repo}/releases");
                    return true;
                }
                LastAvailable = new UpdateInfo
                {
                    Tag = tag,
                    Version = version,
                    PageUrl = url ?? $"https://github.com/{repo}/releases",
                    Notes = notes ?? "",
                    Asset = asset,
                };
                NewVersionAvailable?.Invoke(tag, LastAvailable.PageUrl);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Update check failed for {repo}: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// 下载并校验更新资产。校验失败会删除临时文件并抛出异常；不会执行任何文件。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Asset.Url) || string.IsNullOrWhiteSpace(info.Asset.Name))
            throw new InvalidOperationException("没有可下载的更新资产。");
        if (string.IsNullOrWhiteSpace(info.Asset.Sha256))
            throw new InvalidOperationException("该版本没有提供 SHA256 校验值，为安全起见已阻止自动安装。请前往发布页手动下载。");

        var dir = Path.Combine(Path.GetTempPath(), "WinIslands", "updates", info.Version);
        Directory.CreateDirectory(dir);
        var finalPath = Path.Combine(dir, info.Asset.Name);
        var partPath = finalPath + ".part";
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }

        using var response = await _downloadHttp.GetAsync(info.Asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? info.Asset.Size;

        using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                   81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var sha = SHA256.Create())
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                sha.TransformBlock(buffer, 0, n, null, 0);
                read += n;
                if (total > 0) progress?.Report(Math.Clamp((double)read / total, 0, 1));
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
            if (!hash.Equals(info.Asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(partPath); } catch { }
                throw new InvalidDataException($"更新包校验失败（期望 {info.Asset.Sha256}，实际 {hash}）。已删除下载文件。");
            }
            // 大小校验：GitHub 元数据存在时作为第二道校验。
            if (info.Asset.Size > 0 && read != info.Asset.Size)
            {
                try { File.Delete(partPath); } catch { }
                throw new InvalidDataException("更新包大小与发布信息不一致，已删除下载文件。");
            }
        }

        // 3.1.8 二次校验：流式哈希之后再完整读一遍落盘文件，
        // 防止网络中断导致的截断包被误判为「已校验」。
        if (!VerifyFileHash(partPath, info.Asset.Sha256, info.Asset.Size))
        {
            try { File.Delete(partPath); } catch { }
            throw new InvalidDataException("更新包二次校验失败（文件可能被截断），已删除下载文件。");
        }

        File.Move(partPath, finalPath, true);
        progress?.Report(1);
        return finalPath;
    }

    /// <summary>
    /// 启动独立更新脚本：等待本进程退出 → 替换 exe → 重新启动。返回 false 表示用户取消了 UAC 或无法启动。
    /// </summary>
    public bool LaunchInstaller(string downloadedFile, bool requireElevation)
    {
        var target = AppPaths.ExePath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target) || !File.Exists(downloadedFile))
            return false;

        if (downloadedFile.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
        {
            // 安装包走 Inno 静默升级；/CLOSEAPPLICATIONS 会请求应用关闭，不强制结束。
            Process.Start(new ProcessStartInfo(downloadedFile)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS",
            });
            return true;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "WinIslands", $"update-{Guid.NewGuid():N}.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(false));

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) powershell = "powershell.exe";

        var logPath = Path.Combine(AppPaths.AppDataDir, "update.log");
        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden " +
                        $"-File \"{scriptPath}\" -WaitPid {Environment.ProcessId} " +
                        $"-Source \"{downloadedFile}\" -Target \"{target}\" -LogFile \"{logPath}\"";
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = true,
            Arguments = arguments,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (requireElevation) psi.Verb = "runas";

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to start update helper", ex);
            try { File.Delete(scriptPath); } catch { }
            return false;
        }
    }

    /// <summary>检查程序所在目录是否可直接写入（Program Files 需要提权）。</summary>
    public static bool CurrentInstallIsWritable()
    {
        try
        {
            var exe = AppPaths.ExePath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrWhiteSpace(dir)) return false;
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const string UpdaterScript = """
param(
    [Parameter(Mandatory=$true)][int]$WaitPid,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Target,
    [Parameter(Mandatory=$true)][string]$LogFile
)
$ErrorActionPreference = 'SilentlyContinue'
function Write-Log([string]$message) {
    try { Add-Content -LiteralPath $LogFile -Value ("[{0}] {1}" -f (Get-Date -Format s), $message) } catch { }
}
Write-Log "helper started; waiting for pid $WaitPid"
try { Wait-Process -Id $WaitPid -Timeout 120 -ErrorAction Stop } catch { Write-Log "wait ended: $($_.Exception.Message)" }
Start-Sleep -Milliseconds 500

$copied = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        Copy-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop
        $copied = $true
        break
    } catch {
        if ($i % 5 -eq 0) { Write-Log "copy attempt $i failed: $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 500
    }
}

if ($copied) {
    Write-Log "update copied; restarting $Target"
    Start-Process -FilePath $Target -ArgumentList '--updated'
} else {
    Write-Log "update failed; keeping downloaded file at $Source"
}
Remove-Item -LiteralPath $Source -Force
Remove-Item -LiteralPath $PSCommandPath -Force
""";

    private static async Task<UpdateAsset?> SelectAssetAsync(JsonElement releaseRoot, string version)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64",
        };
        var portable = $"WinIslands-{version}-win-{arch}.exe";
        var setup = $"WinIslands-Setup-{version}.exe";

        var list = assets.EnumerateArray().ToList();
        var chosen = list.FirstOrDefault(a => string.Equals(GetString(a, "name"), portable, StringComparison.OrdinalIgnoreCase));
        if (chosen.ValueKind == JsonValueKind.Undefined)
            chosen = list.FirstOrDefault(a => string.Equals(GetString(a, "name"), setup, StringComparison.OrdinalIgnoreCase));
        if (chosen.ValueKind == JsonValueKind.Undefined) return null;

        var name = GetString(chosen, "name") ?? "";
        var url = GetString(chosen, "browser_download_url") ?? "";
        var size = chosen.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
        var sha = "";
        if (chosen.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
        {
            var digest = d.GetString() ?? "";
            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..];
        }
        // 旧版本 GitHub 没有 digest 字段时，从我们发布的 SHA256SUMS.txt 里异步读取，避免阻塞 UI 线程。
        if (sha.Length == 0)
        {
            var sums = list.FirstOrDefault(a => string.Equals(GetString(a, "name"), "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (sums.ValueKind != JsonValueKind.Undefined)
                sha = await ReadHashFromSumsAsync(GetString(sums, "browser_download_url") ?? "", name);
        }

        return new UpdateAsset { Name = name, Url = url, Size = size, Sha256 = sha };
    }

    private static async Task<string> ReadHashFromSumsAsync(string sumsUrl, string assetName)
    {
        if (string.IsNullOrWhiteSpace(sumsUrl)) return "";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var text = await client.GetStringAsync(sumsUrl);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[^1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase))
                    return parts[0];
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Failed to read SHA256SUMS: {ex.Message}");
        }
        return "";
    }
    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>把 v1.2.3 或 1.2.3-beta1 规范化为 1.2.3。</summary>
    internal static string NormalizeVersion(string tag)
    {
        var t = (tag ?? string.Empty).Trim().TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        return dash > 0 ? t[..dash] : t;
    }

    /// <summary>把 1.2.3 或 1.2.3-beta1 解析为数值（忽略预发布后缀）。</summary>
    internal static long ParseVersion(string tag)
    {
        var t = NormalizeVersion(tag);
        var parts = t.Split('.');
        long v = 0;
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n) || n < 0) return 0;
            v = v * 1000 + n;
        }
        return v;
    }

    public static string CurrentVersion()
        => typeof(UpdaterService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>校验本地文件的 SHA-256（可选同时校验大小）。供更新下载与单元测试复用。</summary>
    internal static bool VerifyFileHash(string path, string expectedSha256, long expectedSize = 0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (expectedSize > 0 && info.Length != expectedSize) return false;
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return actual.Equals(expectedSha256.Replace("-", "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
