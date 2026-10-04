using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinIslands.Services;

/// <summary>
/// 本地插件清单。插件通过独立的本地进程运行，主程序只读取组件输出，不加载第三方代码。
/// </summary>
public sealed class PluginManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("entry")]
    public string Entry { get; set; } = "";

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; set; } = new();

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = new();

    [JsonPropertyName("config")]
    public Dictionary<string, string> Config { get; set; } = new();

    [JsonPropertyName("config_schema")]
    public JsonElement? ConfigSchema { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("run_at_startup")]
    public bool RunAtStartup { get; set; } = true;

    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; }

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 10;

    [JsonPropertyName("max_output_kb")]
    public int MaxOutputKb { get; set; } = 256;

    [JsonIgnore]
    public string DirectoryPath { get; set; } = "";

    [JsonIgnore]
    public string ManifestPath { get; set; } = "";

    [JsonIgnore]
    public string PermissionSummary => Permissions.Count == 0 ? "无额外权限" : string.Join(", ", Permissions);
}

/// <summary>
/// 插件单次运行输出的一个灵动岛组件。
/// </summary>
public sealed class PluginComponentSpec
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "\uE8D6";

    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("progress")]
    public double? Progress { get; set; }

    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("click_action")]
    public string ClickAction { get; set; } = "";

    [JsonPropertyName("click_value")]
    public string ClickValue { get; set; } = "";

    [JsonPropertyName("tooltip")]
    public string ToolTip { get; set; } = "";

    [JsonPropertyName("show_when_playing")]
    public bool ShowWhenPlaying { get; set; } = true;

    [JsonPropertyName("show_when_idle")]
    public bool ShowWhenIdle { get; set; } = true;

    [JsonPropertyName("order")]
    public int Order { get; set; }
}

public sealed class PluginOutput
{
    [JsonPropertyName("components")]
    public List<PluginComponentSpec>? Components { get; set; }
}

public sealed class PluginComponentsChangedEventArgs : EventArgs
{
    public string PluginId { get; }
    public IReadOnlyList<PluginComponentSpec> Components { get; }

    public PluginComponentsChangedEventArgs(string pluginId, IReadOnlyList<PluginComponentSpec> components)
    {
        PluginId = pluginId;
        Components = components;
    }
}

/// <summary>插件健康状态快照（供插件管理界面显示）。</summary>
public sealed class PluginHealthSnapshot
{
    public string PluginId { get; init; } = "";
    public bool IsRunning { get; init; }
    public DateTime? LastSuccessTime { get; init; }
    public long LastDurationMs { get; init; }
    public int FailureCount { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// 本地组件插件管理器。
/// 搜索顺序：%APPDATA%\WinIslands\plugins，然后 exe 同级 plugins。
/// 每个子目录中的 plugin.json 定义一个插件，插件进程的 stdout 返回组件 JSON。
/// </summary>
public sealed class PluginService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly List<PluginManifest> _plugins = new();
    private readonly List<PluginRuntime> _runtimes = new();
    private readonly CancellationTokenSource _lifetime = new();
    /// <summary>最小轮询间隔（秒）。防止插件配置过密导致进程风暴，拖垮系统。</summary>
    private const int MinPluginIntervalSeconds = 5;
    private bool _started;
    private bool _disposed;

    public event EventHandler<PluginComponentsChangedEventArgs>? ComponentsChanged;

    /// <summary>重新扫描完成后触发（插件被启用/禁用/导入/新建后，主程序可借此清理失效的岛组件）。</summary>
    public event EventHandler? PluginsReloaded;

    public IReadOnlyList<PluginManifest> Plugins
    {
        get { lock (_gate) return _plugins.ToArray(); }
    }

    public void Start()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        Reload();
    }

    /// <summary>重新扫描本地插件目录；新增、删除或修改插件后调用即可生效。</summary>
    public void Reload()
    {
        if (_disposed) return;
        var old = new List<PluginRuntime>();
        lock (_gate)
        {
            old.AddRange(_runtimes);
            _runtimes.Clear();
            _plugins.Clear();
        }
        foreach (var runtime in old) runtime.Dispose();

        var discovered = DiscoverPlugins();
        var runtimes = new List<PluginRuntime>();
        lock (_gate)
        {
            _plugins.AddRange(discovered);
            foreach (var manifest in discovered.Where(p => p.Enabled))
                runtimes.Add(new PluginRuntime(manifest));
            _runtimes.AddRange(runtimes);
        }

        foreach (var runtime in runtimes) StartRuntime(runtime);
        AppLogger.Info($"Plugin scan complete: {discovered.Count} manifest(s), {runtimes.Count} enabled");
        PluginsReloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启用或禁用插件，并立即重新扫描。</summary>
    public bool SetEnabled(string pluginId, bool enabled)
    {
        var manifest = Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.ManifestPath) || !File.Exists(manifest.ManifestPath))
            return false;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest.ManifestPath));
            if (node is null) return false;
            node["enabled"] = enabled;
            File.WriteAllText(manifest.ManifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            manifest.Enabled = enabled;
            Reload();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Plugin enable state update failed [{pluginId}]: {ex.Message}");
            return false;
        }
    }

    /// <summary>写入插件参数(config)并立即重新扫描，使环境变量与间隔等生效。</summary>
    public bool SetConfig(string pluginId, IReadOnlyDictionary<string, string> config)
    {
        var manifest = Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.ManifestPath) || !File.Exists(manifest.ManifestPath))
            return false;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest.ManifestPath));
            if (node is null) return false;
            var cfgNode = new System.Text.Json.Nodes.JsonObject();
            foreach (var pair in config) cfgNode[pair.Key] = pair.Value;
            node["config"] = cfgNode;
            File.WriteAllText(manifest.ManifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            manifest.Config = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase);
            Reload();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Plugin config update failed [{pluginId}]: {ex.Message}");
            return false;
        }
    }

    /// <summary>将插件目录打包为 zip（plugin.json 位于压缩包根目录），便于分发。</summary>
    public bool ExportPlugin(string pluginId, string destZip)
    {
        var manifest = Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.DirectoryPath) || !Directory.Exists(manifest.DirectoryPath))
            return false;
        try
        {
            var dir = Path.GetFullPath(manifest.DirectoryPath);
            if (File.Exists(destZip)) File.Delete(destZip);
            ZipFile.CreateFromDirectory(dir, destZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Plugin export failed [{pluginId}]: {ex.Message}");
            return false;
        }
    }

    /// <summary>从 zip 导入插件到用户插件目录；同名插件已存在 / 压缩包无有效 plugin.json / 路径越界时返回 false。</summary>
    public bool ImportPlugin(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return false;
        var targetRoot = AppPaths.PluginsDir;
        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var manifestEntry = archive.Entries.FirstOrDefault(e => e.FullName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry is null)
                    manifestEntry = archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("/plugin.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry is null) return false;

                PluginManifest? manifest;
                using (var stream = manifestEntry.Open())
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    manifest = JsonSerializer.Deserialize<PluginManifest>(reader.ReadToEnd(), JsonOptions);
                }
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)
                    || !Regex.IsMatch(manifest.Id, "^[A-Za-z0-9._-]{1,80}$"))
                    return false;

                if (Plugins.Any(p => string.Equals(p.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
                    return false;

                Directory.CreateDirectory(targetRoot);
                var targetDir = Path.GetFullPath(Path.Combine(targetRoot, manifest.Id));
                if (Directory.Exists(targetDir)) return false;
                Directory.CreateDirectory(targetDir);

                var prefix = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                // 兼容“导出时带基目录”的压缩包：压缩包内文件统一位于 manifestEntry 所在子文件夹下时，剥掉第一层
                var baseFolder = Path.GetDirectoryName(manifestEntry.FullName.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (!string.IsNullOrEmpty(baseFolder)
                        && relative.StartsWith(baseFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        relative = relative[(baseFolder.Length + 1)..];
                    var dest = Path.GetFullPath(Path.Combine(targetDir, relative));
                    if (!dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false; // 路径穿越防护

                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    using (var src = entry.Open())
                    using (var dst = File.Create(dest))
                        src.CopyTo(dst);
                }
            }
            Reload();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Plugin import failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>新建最小插件模板（plugin.json + run.ps1）到用户插件目录；成功返回 true，失败返回错误信息。</summary>
    public string? CreateTemplatePlugin(string pluginId, string name)
    {
        pluginId = (pluginId ?? "").Trim();
        name = string.IsNullOrWhiteSpace(name) ? pluginId : name.Trim();
        if (!Regex.IsMatch(pluginId, "^[A-Za-z0-9._-]{1,80}$"))
            return "插件 ID 只能包含字母、数字、点、横线与下划线（1-80 字符）";
        if (Plugins.Any(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase)))
            return "同名插件已存在";
        try
        {
            var targetDir = Path.GetFullPath(Path.Combine(AppPaths.PluginsDir, pluginId));
            var rootPrefix = Path.GetFullPath(AppPaths.PluginsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!targetDir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return "非法插件 ID";
            Directory.CreateDirectory(targetDir);

            var manifest = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = pluginId,
                ["name"] = name,
                ["version"] = "1.0.0",
                ["description"] = "A WinIslands plugin component.",
                ["author"] = "user",
                ["entry"] = "run.ps1",
                ["arguments"] = new System.Text.Json.Nodes.JsonArray(),
                ["permissions"] = new System.Text.Json.Nodes.JsonArray(),
                ["config"] = new System.Text.Json.Nodes.JsonObject(),
                ["config_schema"] = new System.Text.Json.Nodes.JsonObject(),
                ["enabled"] = true,
                ["run_at_startup"] = true,
                ["interval_seconds"] = 60,
                ["timeout_seconds"] = 10,
                ["max_output_kb"] = 64,
            };
            File.WriteAllText(Path.Combine(targetDir, "plugin.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            const string templatePs1 = @"[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$ErrorActionPreference = 'SilentlyContinue'

# WinIslands 插件最小模板：向 stdout 输出 JSON 组件即可。
# 组件字段：id / text / icon / progress / color / tooltip / click_action / click_value / order
#          / show_when_playing / show_when_idle
$component = @{
    id                 = 'sample'
    text               = '你好，WinIslands！'
    icon               = '✨'
    tooltip            = '这是一个插件组件，修改 run.ps1 即可自定义'
    order              = 100
    show_when_playing  = $true
    show_when_idle     = $true
}

@{ components = @($component) } | ConvertTo-Json -Compress -Depth 6
";
            File.WriteAllText(Path.Combine(targetDir, "run.ps1"), templatePs1, new System.Text.UTF8Encoding(false));

            Reload();
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Plugin template creation failed [{pluginId}]: {ex.Message}");
            return "创建失败：" + ex.Message;
        }
    }

    /// <summary>获取插件健康状态快照；插件未加载(禁用/未发现)时返回 null。</summary>
    public PluginHealthSnapshot? GetHealth(string pluginId)
    {
        lock (_gate)
        {
            var runtime = _runtimes.FirstOrDefault(r => string.Equals(r.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (runtime is null) return null;
            return new PluginHealthSnapshot
            {
                PluginId = runtime.Manifest.Id,
                IsRunning = Volatile.Read(ref runtime.Running) != 0,
                LastSuccessTime = runtime.LastSuccessTime,
                LastDurationMs = runtime.LastDurationMs,
                FailureCount = runtime.FailureCount,
                LastError = runtime.LastError,
            };
        }
    }

    /// <summary>重启单个插件：停掉旧进程与定时器，用同一清单重新拉起。禁用/未加载时返回 false。</summary>
    public bool Restart(string pluginId)
    {
        PluginManifest? manifest;
        lock (_gate)
        {
            var runtime = _runtimes.FirstOrDefault(r => string.Equals(r.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (runtime is not null)
            {
                manifest = runtime.Manifest;
                _runtimes.Remove(runtime);
                runtime.Dispose();
            }
            else
            {
                manifest = _plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            }
            if (manifest is null || !manifest.Enabled) return manifest is not null && !manifest.Enabled;
            var fresh = new PluginRuntime(manifest);
            _runtimes.Add(fresh);
            StartRuntime(fresh);
        }
        AppLogger.Info($"Plugin restarted: {pluginId}");
        return true;
    }

    private List<PluginManifest> DiscoverPlugins()
    {
        var result = new List<PluginManifest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = GetSearchDirectories().ToArray();
        AppLogger.Info($"Plugin search dirs: {string.Join(" | ", directories)}");
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var manifestPath in Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories))
            {
                var manifest = LoadManifest(manifestPath);
                if (manifest is null || !seen.Add(manifest.Id)) continue;
                result.Add(manifest);
            }
        }
        return result;
    }

    private static IEnumerable<string> GetSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[]
                 {
                     AppPaths.PluginsDir,
                     AppPaths.PortablePluginsDir,
                     Path.Combine(AppContext.BaseDirectory, "plugins"),
                 })
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(directory); }
            catch { continue; }
            if (seen.Add(fullPath)) yield return fullPath;
        }
    }
    private static PluginManifest? LoadManifest(string manifestPath)
    {
        try
        {
            var pluginDirectory = Path.GetDirectoryName(manifestPath)!;
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null) return null;
            manifest.Id = manifest.Id.Trim();
            manifest.Name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name.Trim();
            manifest.Entry = manifest.Entry.Trim();
            manifest.DirectoryPath = pluginDirectory;
            manifest.ManifestPath = manifestPath;
            manifest.IntervalSeconds = Math.Clamp(manifest.IntervalSeconds, 0, 86400);
            // 防御：任何 >0 的轮询间隔不得小于 5 秒，避免 PowerShell/WMI 进程风暴
            if (manifest.IntervalSeconds > 0 && manifest.IntervalSeconds < MinPluginIntervalSeconds)
                manifest.IntervalSeconds = MinPluginIntervalSeconds;
            manifest.TimeoutSeconds = Math.Clamp(manifest.TimeoutSeconds, 1, 120);
            manifest.MaxOutputKb = Math.Clamp(manifest.MaxOutputKb, 1, 1024);

            if (string.IsNullOrWhiteSpace(manifest.Id) ||
                !Regex.IsMatch(manifest.Id, "^[A-Za-z0-9._-]{1,80}$") ||
                string.IsNullOrWhiteSpace(manifest.Entry))
            {
                AppLogger.Warn($"Plugin manifest invalid: {manifestPath}");
                return null;
            }

            var entry = ResolveEntryPath(manifest);
            if (entry is null || !File.Exists(entry) || !IsSupportedEntry(entry))
            {
                AppLogger.Warn($"Plugin entry invalid: {manifestPath} -> {manifest.Entry}");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry)));
                if (!string.Equals(actual, manifest.Sha256.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn($"Plugin SHA-256 mismatch: {manifestPath}");
                    return null;
                }
            }

            return manifest;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Plugin manifest load failed: {manifestPath}: {ex.Message}");
            return null;
        }
    }

    internal static bool IsSupportedEntry(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" or ".bat" or ".ps1" or ".com";

    private static string? ResolveEntryPath(PluginManifest manifest)
    {
        try
        {
            var root = Path.GetFullPath(manifest.DirectoryPath);
            var entry = Path.GetFullPath(Path.Combine(root, manifest.Entry));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? entry : null;
        }
        catch { return null; }
    }

    private void StartRuntime(PluginRuntime runtime)
    {
        if (runtime.Manifest.RunAtStartup)
            _ = RunAsync(runtime, runtime.Cancellation.Token);

        if (runtime.Manifest.IntervalSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(runtime.Manifest.IntervalSeconds);
            runtime.Timer = new System.Threading.Timer(_ => _ = RunAsync(runtime, runtime.Cancellation.Token), null, interval, interval);
        }
    }

    private async Task RunAsync(PluginRuntime runtime, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref runtime.Running, 1, 0) != 0) return;
        var startedAt = DateTime.UtcNow;
        runtime.LastError = null; // 新一轮运行开始，清空上次错误（失败次数保留，便于观察稳定性）
        try
        {
            var manifest = runtime.Manifest;
            AppLogger.Debug($"Plugin starting: {manifest.Id}");
            var startInfo = BuildStartInfo(manifest);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                runtime.FailureCount++;
                runtime.LastError = "启动失败";
                AppLogger.Warn($"Plugin failed to start: {manifest.Id}");
                return;
            }
            AppLogger.Debug($"Plugin process started: {manifest.Id}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(manifest.TimeoutSeconds));
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                runtime.FailureCount++;
                runtime.LastError = "运行超时";
                AppLogger.Warn($"Plugin timed out: {manifest.Id} ({manifest.TimeoutSeconds}s)");
                return;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            AppLogger.Debug($"Plugin process exited: {manifest.Id}, code={process.ExitCode}, stdout={stdout.Length}");
            if (!string.IsNullOrWhiteSpace(stderr))
                AppLogger.Warn($"Plugin stderr [{manifest.Id}]: {Limit(stderr, 1000)}");
            if (process.ExitCode != 0)
            {
                runtime.FailureCount++;
                runtime.LastError = $"退出码 {process.ExitCode}";
                AppLogger.Warn($"Plugin exited with code {process.ExitCode}: {manifest.Id}");
            }

            var components = ParseOutput(manifest.Id, stdout, manifest.MaxOutputKb * 1024);
            AppLogger.Debug($"Plugin output parsed: {components?.Count.ToString() ?? "null"}, chars={stdout.Length}");
            if (components is not null)
            {
                AppLogger.Debug($"Plugin components updated [{manifest.Id}]: {components.Count}");
                ComponentsChanged?.Invoke(this, new PluginComponentsChangedEventArgs(manifest.Id, components));
            }

            // 无论是否输出组件，只要正常退出就记为一次成功，方便观察“最后运行时间/耗时”
            runtime.LastSuccessTime = DateTime.UtcNow;
            runtime.LastDurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            runtime.FailureCount++;
            runtime.LastError = Limit(ex.Message, 300);
            AppLogger.Warn($"Plugin run failed [{(runtime.Manifest.Id)}]: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref runtime.Running, 0);
        }
    }


    private ProcessStartInfo BuildStartInfo(PluginManifest manifest)
    {
        var entry = ResolveEntryPath(manifest)!;
        var extension = Path.GetExtension(entry).ToLowerInvariant();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = string.IsNullOrWhiteSpace(manifest.WorkingDirectory)
                ? manifest.DirectoryPath
                : Path.GetFullPath(Path.Combine(manifest.DirectoryPath, manifest.WorkingDirectory)),
        };

        if (extension == ".ps1")
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(entry);
        }
        else if (extension is ".cmd" or ".bat")
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(entry);
        }
        else
        {
            startInfo.FileName = entry;
        }


        foreach (var pair in manifest.Config)
        {
            var key = "WINISLANDS_PLUGIN_CONFIG_" + new string(pair.Key.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(key)) startInfo.Environment[key] = pair.Value ?? string.Empty;
        }
        foreach (var argument in manifest.Arguments)
            startInfo.ArgumentList.Add(ExpandArgument(argument, manifest));
        return startInfo;
    }

    private static string ExpandArgument(string value, PluginManifest manifest)
        => value
            .Replace("${pluginDir}", manifest.DirectoryPath, StringComparison.OrdinalIgnoreCase)
            .Replace("${dataDir}", AppPaths.AppDataDir, StringComparison.OrdinalIgnoreCase)
            .Replace("${appDir}", AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("${appExe}", AppPaths.ExePath, StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<PluginComponentSpec>? ParseOutput(string pluginId, string? stdout, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var text = Limit(stdout.Trim(), Math.Max(1024, maxBytes));
        try
        {
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                return Normalize(pluginId, JsonSerializer.Deserialize<List<PluginComponentSpec>>(text, JsonOptions));
            }
            if (text.StartsWith("{", StringComparison.Ordinal))
            {
                var wrapped = JsonSerializer.Deserialize<PluginOutput>(text, JsonOptions);
                if (wrapped?.Components is not null) return Normalize(pluginId, wrapped.Components);
                var single = JsonSerializer.Deserialize<PluginComponentSpec>(text, JsonOptions);
                return Normalize(pluginId, single is null ? null : new List<PluginComponentSpec> { single });
            }
        }
        catch
        {
            // 兼容“每行一个 JSON 对象”的简单输出；真正的格式错误留给下面逐行解析。
        }

        var result = new List<PluginComponentSpec>();
        foreach (var line in text.Split('\n'))
        {
            var candidate = line.Trim();
            if (candidate.Length == 0) continue;
            try
            {
                var item = JsonSerializer.Deserialize<PluginComponentSpec>(candidate, JsonOptions);
                if (item is not null) result.Add(item);
            }
            catch { }
        }
        return result.Count == 0 ? null : Normalize(pluginId, result);
    }

    private static List<PluginComponentSpec> Normalize(string pluginId, IReadOnlyList<PluginComponentSpec>? source)
    {
        var result = new List<PluginComponentSpec>();
        if (source is null) return result;
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i];
            item.Id = string.IsNullOrWhiteSpace(item.Id) ? $"component-{i + 1}" : item.Id.Trim();
            if (string.IsNullOrWhiteSpace(item.Text)) continue;
            item.Text = Limit(item.Text.Trim(), 500);
            item.Icon = string.IsNullOrWhiteSpace(item.Icon) ? "\uE8D6" : Limit(item.Icon.Trim(), 32);
            item.ToolTip = Limit(item.ToolTip ?? string.Empty, 1000);
            item.Order = Math.Clamp(item.Order, -10000, 10000);
            result.Add(item);
        }
        return result;
    }

    private static string Limit(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        lock (_gate)
        {
            foreach (var runtime in _runtimes) runtime.Dispose();
            _runtimes.Clear();
            _plugins.Clear();
        }
        _lifetime.Dispose();
    }

    private sealed class PluginRuntime : IDisposable
    {
        public PluginManifest Manifest { get; }
        public System.Threading.Timer? Timer { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public int Running;

        // 健康状态：由 RunAsync 更新，GetHealth 在 _gate 锁内读取
        public DateTime? LastSuccessTime;
        public long LastDurationMs;
        public int FailureCount;
        public string? LastError;

        public PluginRuntime(PluginManifest manifest) => Manifest = manifest;

        public void Dispose()
        {
            Cancellation.Cancel();
            Timer?.Dispose();
            Timer = null;
            Cancellation.Dispose();
        }
    }
}
