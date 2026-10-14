using Snaply.Core.Services;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using NSubstitute;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Platform;

namespace Snaply.App.Tests;

public class StartupTests
{
    [Theory]
    [InlineData("--background", StartupCommand.Background)]
    [InlineData("--BACKGROUND", StartupCommand.Background)]
    [InlineData("--capture-region", StartupCommand.CaptureRegion)]
    [InlineData("--open", StartupCommand.Open)]
    [InlineData("--unknown", StartupCommand.Open)]
    public void Command_line_is_parsed(string arg, StartupCommand expected) =>
        Assert.Equal(expected, StartupCommands.Parse(new[] { arg }));

    [Fact]
    public void No_arguments_means_a_normal_launch() =>
        Assert.Equal(StartupCommand.Open, StartupCommands.Parse(Array.Empty<string>()));

    [Fact]
    public void Registration_writes_a_quoted_hidden_launch_command_and_removes_it_again()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = @"Software\SnaplyTests\" + Guid.NewGuid().ToString("N");
        try
        {
            var reg = new WindowsStartupRegistration(key, () => @"C:\Program Files\My Apps\Snaply.exe");
            Assert.False(reg.IsEnabled);

            Assert.True(reg.Set(true).IsSuccess);
            Assert.True(reg.IsEnabled);
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Program Files\\My Apps\\Snaply.exe\" --background", k!.GetValue(WindowsStartupRegistration.ValueName));

            Assert.True(reg.Set(true).IsSuccess);                       // idempotent
            Assert.True(reg.Set(false).IsSuccess);
            Assert.False(reg.IsEnabled);
            Assert.True(reg.Set(false).IsSuccess);                      // removing what is not there is fine
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Registration_reports_a_missing_program_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = @"Software\SnaplyTests\" + Guid.NewGuid().ToString("N");
        try
        {
            var r = new WindowsStartupRegistration(key, () => null).Set(true);
            Assert.False(r.IsSuccess);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Other_platforms_report_not_supported()
    {
        var r = new UnsupportedStartupRegistration();
        Assert.False(r.IsSupported);
        Assert.False(r.Set(true).IsSuccess);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Startup_is_off_by_default_and_the_switch_registers_and_persists()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var settings = fx.Services.GetRequiredService<IApplicationSettingsService>();
        var startup = fx.Services.GetRequiredService<IStartupRegistration>();
        var vm = fx.Services.GetRequiredService<SettingsViewModel>();
        await vm.OnNavigatedToAsync();

        Assert.False(settings.Current.StartWithWindows);
        Assert.False(vm.StartWithWindows);
        Assert.True(vm.StartupAvailable);

        vm.StartWithWindows = true;
        await Task.Delay(300);
        startup.Received(1).Set(true);
        Assert.True(settings.Current.StartWithWindows);

        vm.StartWithWindows = false;
        await Task.Delay(300);
        startup.Received(1).Set(false);
        Assert.False(settings.Current.StartWithWindows);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_failed_registration_puts_the_switch_back_and_saves_nothing()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var settings = fx.Services.GetRequiredService<IApplicationSettingsService>();
        fx.Services.GetRequiredService<IStartupRegistration>().Set(true).Returns(Result.Fail(ErrorCode.Failed, "blocked by policy"));
        var vm = fx.Services.GetRequiredService<SettingsViewModel>();
        await vm.OnNavigatedToAsync();

        vm.StartWithWindows = true;
        await Task.Delay(300);

        Assert.False(vm.StartWithWindows);
        Assert.False(settings.Current.StartWithWindows);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Development_builds_cannot_register()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        fx.Services.GetRequiredService<IAppUpdateService>().IsSupported.Returns(false);     // not installed by the installer
        var vm = fx.Services.GetRequiredService<SettingsViewModel>();
        await vm.OnNavigatedToAsync();

        Assert.False(vm.StartupAvailable);
        Assert.Contains("installed app", vm.StartupNote);

        vm.StartWithWindows = true;                                  // even if something flips it, nothing is registered
        await Task.Delay(200);
        fx.Services.GetRequiredService<IStartupRegistration>().DidNotReceive().Set(Arg.Any<bool>());
    }
}
