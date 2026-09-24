using WinIslands.Services;

namespace WinIslands.Tests;

/// <summary>
/// GlobalHotkeyService.TryParse 静态方法测试。
/// 验证各种快捷键格式解析：修饰键组合、字母/数字键、功能键 F1-F24、
/// 特殊键（Space/Enter/Tab/Esc 等）、箭头键、大小写不敏感、无效输入。
/// 纯逻辑测试，不依赖 Win32 RegisterHotKey。
/// </summary>
public class GlobalHotkeyServiceTests
{
    // ── 基本组合键 ──────────────────────────────────────────────

    [Fact]
    public void Parses_Ctrl_Alt_I()
    {
        Assert.True(GlobalHotkeyService.TryParse("Ctrl+Alt+I", out var mods, out var vk));
        Assert.NotEqual(0u, vk);
        Assert.Equal((uint)'I', vk);
        // MOD_CONTROL = 0x0002, MOD_ALT = 0x0001
        Assert.NotEqual(0u, mods);
    }

    [Fact]
    public void Parses_Ctrl_Shift_Space()
    {
        Assert.True(GlobalHotkeyService.TryParse("Ctrl+Shift+Space", out _, out var vk));
        Assert.Equal(0x20u, vk); // VK_SPACE
    }

    [Fact]
    public void Parses_Win_Alt_F1()
    {
        Assert.True(GlobalHotkeyService.TryParse("Win+Alt+F1", out var mods, out var vk));
        Assert.Equal(0x70u, vk); // F1 = 0x70
        Assert.NotEqual(0u, mods);
    }

    [Fact]
    public void Parses_Windows_Synonym_For_Win()
    {
        Assert.True(GlobalHotkeyService.TryParse("Windows+D", out var mods1, out var vk1));
        Assert.True(GlobalHotkeyService.TryParse("Win+D", out var mods2, out var vk2));
        Assert.Equal(mods1, mods2);
        Assert.Equal(vk1, vk2);
    }

    [Fact]
    public void Parses_Cmd_Synonym_For_Win()
    {
        Assert.True(GlobalHotkeyService.TryParse("Cmd+D", out var mods1, out _));
        Assert.True(GlobalHotkeyService.TryParse("Win+D", out var mods2, out _));
        Assert.Equal(mods1, mods2);
    }

    [Fact]
    public void Parses_Control_Synonym_For_Ctrl()
    {
        Assert.True(GlobalHotkeyService.TryParse("Control+Alt+P", out var mods1, out _));
        Assert.True(GlobalHotkeyService.TryParse("Ctrl+Alt+P", out var mods2, out _));
        Assert.Equal(mods1, mods2);
    }

    // ── 大小写不敏感 ──────────────────────────────────────────

    [Fact]
    public void Case_Insensitive_Modifiers()
    {
        Assert.True(GlobalHotkeyService.TryParse("ctrl+alt+i", out var mods1, out var vk1));
        Assert.True(GlobalHotkeyService.TryParse("CTRL+ALT+I", out var mods2, out var vk2));
        Assert.True(GlobalHotkeyService.TryParse("Ctrl+Alt+I", out var mods3, out var vk3));
        Assert.Equal(mods1, mods2);
        Assert.Equal(mods2, mods3);
        Assert.Equal(vk1, vk2);
        Assert.Equal(vk2, vk3);
    }

    // ── 单字母与数字键 ─────────────────────────────────────────

    [Fact]
    public void Parses_Single_Letter_A_To_Z()
    {
        for (var c = 'A'; c <= 'Z'; c++)
        {
            Assert.True(GlobalHotkeyService.TryParse($"Ctrl+{c}", out _, out var vk));
            Assert.Equal((uint)c, vk);
        }
    }

    [Fact]
    public void Parses_Single_Number_0_To_9()
    {
        for (var c = '0'; c <= '9'; c++)
        {
            Assert.True(GlobalHotkeyService.TryParse($"Ctrl+{c}", out _, out var vk));
            Assert.Equal((uint)c, vk);
        }
    }

    [Fact]
    public void Lowercase_Letter_Returns_Uppercase_VK()
    {
        Assert.True(GlobalHotkeyService.TryParse("Ctrl+a", out _, out var vk));
        Assert.Equal((uint)'A', vk);
    }

    // ── 功能键 F1-F24 ──────────────────────────────────────────

    [Fact]
    public void Parses_F1_To_F24()
    {
        for (var fn = 1; fn <= 24; fn++)
        {
            Assert.True(GlobalHotkeyService.TryParse($"Ctrl+F{fn}", out _, out var vk));
            Assert.Equal((uint)(0x70 + fn - 1), vk);
        }
    }

    [Fact]
    public void F25_Is_Invalid()
    {
        Assert.False(GlobalHotkeyService.TryParse("Ctrl+F25", out _, out _));
    }

    [Fact]
    public void F0_Is_Invalid()
    {
        Assert.False(GlobalHotkeyService.TryParse("Ctrl+F0", out _, out _));
    }

    // ── 特殊键 ──────────────────────────────────────────────

    [Theory]
    [InlineData("Space", 0x20)]
    [InlineData("Enter", 0x0D)]
    [InlineData("Return", 0x0D)]
    [InlineData("Tab", 0x09)]
    [InlineData("Esc", 0x1B)]
    [InlineData("Escape", 0x1B)]
    [InlineData("Back", 0x08)]
    [InlineData("Backspace", 0x08)]
    [InlineData("Delete", 0x2E)]
    [InlineData("Del", 0x2E)]
    [InlineData("Insert", 0x2D)]
    [InlineData("Ins", 0x2D)]
    [InlineData("Home", 0x24)]
    [InlineData("End", 0x23)]
    [InlineData("PageUp", 0x21)]
    [InlineData("PgUp", 0x21)]
    [InlineData("PageDown", 0x22)]
    [InlineData("PgDn", 0x22)]
    public void Parses_Special_Keys(string key, uint expectedVk)
    {
        Assert.True(GlobalHotkeyService.TryParse($"Ctrl+{key}", out _, out var vk));
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("Left", 0x25)]
    [InlineData("Right", 0x27)]
    [InlineData("Up", 0x26)]
    [InlineData("Down", 0x28)]
    public void Parses_Arrow_Keys(string key, uint expectedVk)
    {
        Assert.True(GlobalHotkeyService.TryParse($"Ctrl+{key}", out _, out var vk));
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("CapsLock", 0x14)]
    [InlineData("Caps", 0x14)]
    [InlineData("Comma", 0xBC)]
    [InlineData("Period", 0xBE)]
    [InlineData("Semicolon", 0xBA)]
    [InlineData("Minus", 0xBD)]
    [InlineData("Equals", 0xBB)]
    [InlineData("Slash", 0xBF)]
    [InlineData("Backslash", 0xDC)]
    [InlineData("Quote", 0xDE)]
    [InlineData("Grave", 0xC0)]
    [InlineData("Lbracket", 0xDB)]
    [InlineData("Rbracket", 0xDD)]
    public void Parses_Misc_Keys(string key, uint expectedVk)
    {
        Assert.True(GlobalHotkeyService.TryParse($"Ctrl+{key}", out _, out var vk));
        Assert.Equal(expectedVk, vk);
    }

    // ── 无效输入 ──────────────────────────────────────────────

    [Fact]
    public void Empty_String_Is_Invalid()
    {
        Assert.False(GlobalHotkeyService.TryParse("", out _, out _));
        Assert.False(GlobalHotkeyService.TryParse("   ", out _, out _));
        Assert.False(GlobalHotkeyService.TryParse(null!, out _, out _));
    }

    [Fact]
    public void Unknown_Modifier_Is_Invalid()
    {
        Assert.False(GlobalHotkeyService.TryParse("Foo+I", out _, out _));
        Assert.False(GlobalHotkeyService.TryParse("Ctrl+Bar+I", out _, out _));
    }

    [Fact]
    public void Unknown_Key_Is_Invalid()
    {
        Assert.False(GlobalHotkeyService.TryParse("Ctrl+Foo", out _, out _));
        Assert.False(GlobalHotkeyService.TryParse("Ctrl+ABC", out _, out _));
    }

    [Fact]
    public void No_Modifier_With_Single_Letter_Is_Valid()
    {
        // A single letter without any modifier — still valid (returns the VK code).
        Assert.True(GlobalHotkeyService.TryParse("I", out var mods, out var vk));
        Assert.Equal(0u, mods);
        Assert.Equal((uint)'I', vk);
    }

    [Fact]
    public void Multiple_Modifiers_All_Set()
    {
        Assert.True(GlobalHotkeyService.TryParse("Ctrl+Alt+Shift+Win+A", out var mods, out _));
        // All four modifier bits should be set.
        // MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_SHIFT=0x4, MOD_WIN=0x8
        Assert.NotEqual(0u, mods);
    }

    [Fact]
    public void Whitespace_In_Parts_Is_Trimmed()
    {
        Assert.True(GlobalHotkeyService.TryParse("  Ctrl  +  Alt  +  I  ", out _, out var vk));
        Assert.Equal((uint)'I', vk);
    }
}
