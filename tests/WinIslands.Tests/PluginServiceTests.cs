using WinIslands.Services;

namespace WinIslands.Tests;

public sealed class PluginServiceTests
{
    [Fact]
    public void ParseOutput_SingleComponent()
    {
        var result = PluginService.ParseOutput("demo.plugin", "{\"id\":\"clock\",\"text\":\"10:24\",\"icon\":\"\\uE823\",\"order\":3}", 4096);
        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("clock", result![0].Id);
        Assert.Equal("10:24", result[0].Text);
        Assert.Equal(3, result[0].Order);
    }

    [Fact]
    public void ParseOutput_WrappedComponentArray()
    {
        var result = PluginService.ParseOutput("demo.plugin", "{\"components\":[{\"id\":\"a\",\"text\":\"A\"},{\"id\":\"b\",\"text\":\"B\"}]}", 4096);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal("A", result[0].Text);
        Assert.Equal("B", result[1].Text);
    }

    [Fact]
    public void ParseOutput_InvalidJsonReturnsNull()
    {
        Assert.Null(PluginService.ParseOutput("demo.plugin", "not-json", 4096));
    }

    [Theory]
    [InlineData("run.ps1", true)]
    [InlineData("tool.exe", true)]
    [InlineData("run.cmd", true)]
    [InlineData("plugin.dll", false)]
    public void IsSupportedEntry_ValidatesExtension(string path, bool expected)
        => Assert.Equal(expected, PluginService.IsSupportedEntry(path));
}
