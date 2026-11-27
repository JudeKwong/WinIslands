using System.Security.Cryptography;
using WinIslands.Services;
using Xunit;

namespace WinIslands.Tests;

/// <summary>
/// 3.1.8 更新包校验回归：哈希/大小任一不符都必须判定失败，
/// 保证被截断或被篡改的安装包不会进入替换流程。
/// </summary>
public sealed class UpdateVerificationTests
{
    private static (string Path, string Hash, long Size) CreateFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "winislands-update-test", Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return (path, hash, bytes.Length);
    }

    [Fact]
    public void VerifyFileHash_AcceptsMatchingHash()
    {
        var (path, hash, size) = CreateFile("update-payload");
        try
        {
            Assert.True(UpdaterService.VerifyFileHash(path, hash, size));
            Assert.True(UpdaterService.VerifyFileHash(path, hash.ToLowerInvariant()));
            Assert.True(UpdaterService.VerifyFileHash(path, "  " + hash + "  "));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyFileHash_RejectsWrongHash()
    {
        var (path, _, size) = CreateFile("update-payload");
        try
        {
            Assert.False(UpdaterService.VerifyFileHash(path, new string('a', 64), size));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyFileHash_RejectsWrongSize()
    {
        var (path, hash, size) = CreateFile("update-payload");
        try
        {
            Assert.False(UpdaterService.VerifyFileHash(path, hash, size + 1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VerifyFileHash_RejectsMissingFileOrEmptyHash()
    {
        Assert.False(UpdaterService.VerifyFileHash(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), new string('a', 64)));
        var (path, _, _) = CreateFile("x");
        try { Assert.False(UpdaterService.VerifyFileHash(path, "")); }
        finally { File.Delete(path); }
    }
}