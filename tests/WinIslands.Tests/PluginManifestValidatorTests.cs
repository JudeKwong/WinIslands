using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class PluginManifestValidatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "WinIslandsPlugin-" + Guid.NewGuid().ToString("N"));

    public PluginManifestValidatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteEntry(string name = "run.ps1", string text = "Write-Output '{}'")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static PluginManifest Valid(string entry = "run.ps1") => new()
    {
        Id = "com.example.hello",
        Name = "Hello",
        Version = "1.0.0",
        Author = "Tester",
        Description = "Sample",
        Entry = entry,
        TimeoutSeconds = 10,
        MaxOutputKb = 64,
        Sha256 = new string('a', 64),
    };

    [Fact]
    public void Valid_Manifest_Passes()
    {
        WriteEntry();
        var result = PluginManifestValidator.Validate(Valid(), _dir);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("bad id!")]
    public void Invalid_Id_Is_Rejected(string id)
    {
        WriteEntry();
        var manifest = Valid();
        manifest.Id = id;
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code is "id-missing" or "id-invalid");
    }

    [Fact]
    public void Entry_Escape_Is_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "evil-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(outside, "x");
        try
        {
            var result = PluginManifestValidator.Validate(Valid("../" + Path.GetFileName(outside)), _dir);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Code == "entry-escape");
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void Unsupported_Entry_Extension_Is_Rejected()
    {
        WriteEntry("run.vbs");
        var result = PluginManifestValidator.Validate(Valid("run.vbs"), _dir);
        Assert.Contains(result.Errors, e => e.Code == "entry-extension");
    }

    [Fact]
    public void Missing_Entry_File_Is_Rejected()
    {
        var result = PluginManifestValidator.Validate(Valid("missing.ps1"), _dir);
        Assert.Contains(result.Errors, e => e.Code == "entry-not-found");
    }

    [Fact]
    public void Unknown_Permission_Is_Rejected()
    {
        WriteEntry();
        var manifest = Valid();
        manifest.Permissions = new List<string> { "network", "registry" };
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.Contains(result.Errors, e => e.Code == "permission-unknown");
    }

    [Fact]
    public void Known_Permissions_Are_Accepted_Case_Insensitively()
    {
        WriteEntry();
        var manifest = Valid();
        manifest.Permissions = new List<string> { "NETWORK", "Filesystem", "processes" };
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-1, 10, 64)]
    [InlineData(86401, 10, 64)]
    [InlineData(1, 0, 64)]
    [InlineData(1, 121, 64)]
    [InlineData(1, 10, 0)]
    [InlineData(1, 10, 2048)]
    public void Numeric_Ranges_Are_Enforced(int interval, int timeout, int outputKb)
    {
        WriteEntry();
        var manifest = Valid();
        manifest.IntervalSeconds = interval;
        manifest.TimeoutSeconds = timeout;
        manifest.MaxOutputKb = outputKb;
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Small_Interval_Is_A_Warning_Not_An_Error()
    {
        WriteEntry();
        var manifest = Valid();
        manifest.IntervalSeconds = 1;
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Code == "interval-too-small");
    }

    [Fact]
    public void Missing_Sha256_Is_A_Warning()
    {
        WriteEntry();
        var manifest = Valid();
        manifest.Sha256 = "";
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Code == "sha256-missing");
    }

    [Fact]
    public void Malformed_Sha256_Is_Rejected()
    {
        WriteEntry();
        var manifest = Valid();
        manifest.Sha256 = "not-a-hash";
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.Contains(result.Errors, e => e.Code == "sha256-format");
    }

    [Fact]
    public void Hash_Mismatch_Is_Rejected_When_Provided()
    {
        WriteEntry();
        var actual = PluginManifestValidator.ComputeFileSha256Safe(Path.Combine(_dir, "run.ps1"));
        var manifest = Valid();
        manifest.Sha256 = actual;
        Assert.True(PluginManifestValidator.Validate(manifest, _dir, actual).IsValid);

        var other = new string('b', 64);
        var mismatch = PluginManifestValidator.Validate(manifest, _dir, other);
        Assert.Contains(mismatch.Errors, e => e.Code == "sha256-mismatch");
    }

    [Fact]
    public void WorkingDirectory_Escape_Is_Rejected()
    {
        WriteEntry();
        var manifest = Valid();
        manifest.WorkingDirectory = "../..";
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.Contains(result.Errors, e => e.Code == "working-directory-escape");
    }

    [Fact]
    public void Missing_Optional_Metadata_Produces_Warnings()
    {
        WriteEntry();
        var manifest = Valid();
        manifest.Version = "";
        manifest.Author = "";
        manifest.Description = "";
        var result = PluginManifestValidator.Validate(manifest, _dir);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Code == "version-missing");
        Assert.Contains(result.Warnings, w => w.Code == "author-missing");
        Assert.Contains(result.Warnings, w => w.Code == "description-missing");
    }
}
