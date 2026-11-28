using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinIslands.Services;

/// <summary>一条清单校验问题（Code 便于脚本/测试断言，Message 面向开发者）。</summary>
public sealed record PluginValidationIssue(string Code, string Message);

/// <summary>插件清单校验结果。</summary>
public sealed class PluginValidationResult
{
    public List<PluginValidationIssue> Errors { get; } = new();
    public List<PluginValidationIssue> Warnings { get; } = new();

    public bool IsValid => Errors.Count == 0;

    public void Error(string code, string message) => Errors.Add(new PluginValidationIssue(code, message));
    public void Warn(string code, string message) => Warnings.Add(new PluginValidationIssue(code, message));

    public string ToDisplayString()
    {
        if (IsValid && Warnings.Count == 0) return "校验通过";
        var lines = new List<string>();
        lines.AddRange(Errors.Select(e => "错误 [" + e.Code + "] " + e.Message));
        lines.AddRange(Warnings.Select(w => "警告 [" + w.Code + "] " + w.Message));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 插件清单的静态校验规则：与运行时加载保持一致，供插件管理器、开发者工具和测试共用。
/// 只做纯逻辑校验，不执行插件代码。
/// </summary>
public static class PluginManifestValidator
{
    public const int MinIntervalSeconds = 5;
    public const int MaxIntervalSeconds = 86400;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 120;
    public const int MinOutputKb = 1;
    public const int MaxOutputKb = 1024;

    public static readonly IReadOnlySet<string> KnownPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "network", "filesystem", "processes",
    };

    public static readonly IReadOnlySet<string> SupportedEntryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".cmd", ".bat", ".ps1", ".com",
    };

    private static readonly Regex IdPattern = new("^[A-Za-z0-9._-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// 校验清单。pluginDirectory 为 plugin.json 所在目录；entrySha256 传入时用于比对 sha256 字段。
    /// </summary>
    public static PluginValidationResult Validate(PluginManifest? manifest, string pluginDirectory, string? entrySha256 = null)
    {
        var result = new PluginValidationResult();
        if (manifest is null)
        {
            result.Error("manifest-missing", "无法读取 plugin.json 或 JSON 格式无效。");
            return result;
        }

        var id = (manifest.Id ?? string.Empty).Trim();
        if (id.Length == 0)
            result.Error("id-missing", "缺少 id。");
        else if (id is "." or ".." || !IdPattern.IsMatch(id))
            result.Error("id-invalid", "id 只允许字母、数字、点、横线和下划线，长度 1-80。");

        var entry = (manifest.Entry ?? string.Empty).Trim();
        if (entry.Length == 0)
        {
            result.Error("entry-missing", "缺少 entry（入口文件）。");
        }
        else
        {
            var extension = Path.GetExtension(entry);
            if (!SupportedEntryExtensions.Contains(extension))
                result.Error("entry-extension", $"不支持的入口类型 '{extension}'，仅支持 .exe/.cmd/.bat/.ps1/.com。");

            var root = SafeFullPath(pluginDirectory);
            var fullEntry = root is null ? null : SafeFullPath(Path.Combine(root, entry));
            if (root is null || fullEntry is null)
            {
                result.Error("entry-path", "entry 路径无效。");
            }
            else if (!IsInside(root, fullEntry))
            {
                result.Error("entry-escape", "entry 不能跳出插件目录。");
            }
            else if (!File.Exists(fullEntry))
            {
                result.Error("entry-not-found", $"入口文件不存在：{entry}");
            }
            else if (entrySha256 is not null && !string.Equals(entrySha256, ComputeFileSha256Safe(fullEntry), StringComparison.OrdinalIgnoreCase))
            {
                result.Error("sha256-mismatch", "sha256 与入口文件实际哈希不一致。");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.WorkingDirectory))
        {
            var root = SafeFullPath(pluginDirectory);
            var workDir = root is null ? null : SafeFullPath(Path.Combine(root, manifest.WorkingDirectory));
            if (root is null || workDir is null)
                result.Error("working-directory-path", "working_directory 路径无效。");
            else if (!IsInside(root, workDir))
                result.Error("working-directory-escape", "working_directory 不能跳出插件目录。");
            else if (!Directory.Exists(workDir))
                result.Warn("working-directory-missing", "working_directory 目录不存在，将回退到插件目录。");
        }

        foreach (var raw in manifest.Permissions ?? new List<string>())
        {
            var permission = (raw ?? string.Empty).Trim();
            if (permission.Length == 0) continue;
            if (!KnownPermissions.Contains(permission))
                result.Error("permission-unknown", $"未知权限 '{permission}'，仅支持 network / filesystem / processes。");
        }

        if (manifest.IntervalSeconds < 0)
            result.Error("interval-negative", "interval_seconds 不能为负数。");
        else if (manifest.IntervalSeconds > MaxIntervalSeconds)
            result.Error("interval-too-large", $"interval_seconds 不能超过 {MaxIntervalSeconds}。");
        else if (manifest.IntervalSeconds > 0 && manifest.IntervalSeconds < MinIntervalSeconds)
            result.Warn("interval-too-small", $"interval_seconds 小于 {MinIntervalSeconds} 秒，运行时将被提升到 {MinIntervalSeconds} 秒。");

        if (manifest.TimeoutSeconds < MinTimeoutSeconds || manifest.TimeoutSeconds > MaxTimeoutSeconds)
            result.Error("timeout-range", $"timeout_seconds 必须在 {MinTimeoutSeconds}-{MaxTimeoutSeconds} 之间。");

        if (manifest.MaxOutputKb < MinOutputKb || manifest.MaxOutputKb > MaxOutputKb)
            result.Error("output-range", $"max_output_kb 必须在 {MinOutputKb}-{MaxOutputKb} 之间。");

        if (manifest.Arguments is { Count: > 64 })
            result.Error("arguments-too-many", "arguments 最多 64 项。");
        if (manifest.Arguments?.Any(a => (a ?? string.Empty).Length > 512) == true)
            result.Error("argument-too-long", "单项参数不能超过 512 个字符。");

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var hash = manifest.Sha256.Replace("-", string.Empty).Trim();
            if (!Sha256Pattern.IsMatch(hash))
                result.Error("sha256-format", "sha256 必须是 64 位十六进制字符串。");
        }
        else
        {
            result.Warn("sha256-missing", "未提供 sha256；安装后需要在插件管理器中「校验并信任」才能运行。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
            result.Warn("version-missing", "建议填写 version（语义化版本，例如 1.0.0）。");
        if (string.IsNullOrWhiteSpace(manifest.Author))
            result.Warn("author-missing", "建议填写 author，方便问题追踪。");
        if (string.IsNullOrWhiteSpace(manifest.Description))
            result.Warn("description-missing", "建议填写 description，插件列表会显示这段说明。");

        return result;
    }

    public static string ComputeFileSha256Safe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
