using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Interfaces;

namespace Snaply.App.Tests;

public class DefaultToolTests
{
    [Theory]
    [InlineData("Rectangle", InlineTool.Rectangle)]
    [InlineData("arrow", InlineTool.Arrow)]
    [InlineData("Pixelate", InlineTool.Pixelate)]
    [InlineData("Select", InlineTool.None)]
    [InlineData("Crop", InlineTool.None)]      // editor-only
    [InlineData("Nonsense", InlineTool.None)]
    [InlineData(null, InlineTool.None)]
    public void Settings_key_maps_to_overlay_tool(string? key, InlineTool expected) =>
        Assert.Equal(expected, InlineEditSession.ToolFor(key));

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Choosing_a_default_tool_is_persisted_and_restored()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var settings = fx.Services.GetRequiredService<IApplicationSettingsService>();
        var vm = fx.Services.GetRequiredService<SettingsViewModel>();
        await vm.OnNavigatedToAsync();
        Assert.Equal("Select", settings.Current.DefaultCaptureTool);
        Assert.DoesNotContain(SettingsViewModel.DefaultToolOptions, o => o.Key == "Crop");

        vm.DefaultTool = SettingsViewModel.DefaultToolOptions.First(o => o.Key == "Arrow");
        await Task.Delay(300);
        Assert.Equal("Arrow", settings.Current.DefaultCaptureTool);

        await vm.OnNavigatedToAsync();
        Assert.Equal("Arrow", vm.DefaultTool.Key);
    }
}

public class RedactionSettingsUiTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Turning_a_rule_off_is_persisted_and_restored()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var settings = fx.Services.GetRequiredService<IApplicationSettingsService>();
        var vm = fx.Services.GetRequiredService<SettingsViewModel>();
        await vm.OnNavigatedToAsync();

        Assert.Equal(Enum.GetValues<Snaply.Core.Redaction.SensitiveType>().Length, vm.RedactionRules.Count);
        Assert.False(vm.RedactionRules.First(r => r.Type == Snaply.Core.Redaction.SensitiveType.Uuid).IsEnabled); // off by default
        Assert.True(vm.RedactionRules.First(r => r.Type == Snaply.Core.Redaction.SensitiveType.Phone).IsEnabled);

        vm.RedactionRules.First(r => r.Type == Snaply.Core.Redaction.SensitiveType.Phone).IsEnabled = false;
        await Task.Delay(300);
        Assert.Contains("Phone", settings.Current.DisabledRedactionTypes);

        await vm.OnNavigatedToAsync();
        Assert.False(vm.RedactionRules.First(r => r.Type == Snaply.Core.Redaction.SensitiveType.Phone).IsEnabled);
    }
}
