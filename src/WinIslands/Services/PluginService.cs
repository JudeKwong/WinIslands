using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private bool _started;
    private bool _disposed;

    public event EventHandler<PluginComponentsChangedEventArgs>? ComponentsChanged;

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
            _ = RunAsync(runtime, _lifetime.Token);

        if (runtime.Manifest.IntervalSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(runtime.Manifest.IntervalSeconds);
            runtime.Timer = new System.Threading.Timer(_ => _ = RunAsync(runtime, _lifetime.Token), null, interval, interval);
        }
    }

    private async Task RunAsync(PluginRuntime runtime, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref runtime.Running, 1, 0) != 0) return;
        try
        {
            var manifest = runtime.Manifest;
            AppLogger.Info($"Plugin starting: {manifest.Id}");
            var startInfo = BuildStartInfo(manifest);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                AppLogger.Warn($"Plugin failed to start: {manifest.Id}");
                return;
            }
            AppLogger.Info($"Plugin process started: {manifest.Id}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
                AppLogger.Warn($"Plugin timed out: {manifest.Id} ({manifest.TimeoutSeconds}s)");
                return;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            AppLogger.Info($"Plugin process exited: {manifest.Id}, code={process.ExitCode}, stdout={stdout.Length}");
            if (!string.IsNullOrWhiteSpace(stderr))
                AppLogger.Warn($"Plugin stderr [{manifest.Id}]: {Limit(stderr, 1000)}");
            if (process.ExitCode != 0)
                AppLogger.Warn($"Plugin exited with code {process.ExitCode}: {manifest.Id}");

            var components = ParseOutput(manifest.Id, stdout, manifest.MaxOutputKb * 1024);
            AppLogger.Info($"Plugin output parsed: {components?.Count.ToString() ?? "null"}, chars={stdout.Length}");
            if (components is not null)
                AppLogger.Debug($"Plugin components updated [{manifest.Id}]: {components.Count}");
                ComponentsChanged?.Invoke(this, new PluginComponentsChangedEventArgs(manifest.Id, components));
        }
        catch (Exception ex)
        {
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
        public int Running;

        public PluginRuntime(PluginManifest manifest) => Manifest = manifest;

        public void Dispose()
        {
            Timer?.Dispose();
            Timer = null;
        }
    }
}
