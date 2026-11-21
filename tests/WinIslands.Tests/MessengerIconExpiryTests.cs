using System.Reflection;
using System.Windows.Threading;
using WinIslands.Services;
using WinIslands.UI;

namespace WinIslands.Tests;

/// <summary>系统 Toast 到期后，通知卡片与圆形应用图标必须同时消失。</summary>
public class MessengerIconExpiryTests : IDisposable
{
    private readonly string _origAppData;
    private readonly SettingsService _settings = new();
    private readonly SmtcMediaProvider _smtc;
    private readonly CiderMediaProvider _cider;
    private readonly WindowTitleMediaProvider _title = new();
    private readonly MediaCoordinator _coordinator;

    public MessengerIconExpiryTests()
    {
        _origAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(Path.GetTempPath(), "WinIslandsMessengerIconTests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", dir);
        Directory.CreateDirectory(dir);
        _smtc = new SmtcMediaProvider(_settings, new MediaAppRegistry());
        _cider = new CiderMediaProvider(_settings);
        _coordinator = new MediaCoordinator(_settings, _smtc, _cider, _title, Dispatcher.CurrentDispatcher);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        Environment.SetEnvironmentVariable("WINISLANDS_APPDATA", null);
        Environment.SetEnvironmentVariable("APPDATA", _origAppData);
    }

    [Fact]
    public void ExpiredMessengerPush_RemovesCircularIcon()
    {
        using var vm = new IslandViewModel(_coordinator, _settings, new LyricsService(_settings, _cider));
        vm.ShowMessengerNotification(MessengerSource.QQ, "QQ", "测试消息", "QQ.App");

        Assert.NotNull(vm.ActivePush);
        Assert.Single(vm.MessengerUnreads);
        vm.ActivePush!.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);

        typeof(IslandViewModel)
            .GetMethod("CheckPushExpiry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null);

        Assert.Null(vm.ActivePush);
        Assert.Empty(vm.MessengerUnreads);
        Assert.False(vm.HasMessengerUnreads);
    }
}
