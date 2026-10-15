using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.Tests;

public class AppLogoTests
{
    [Fact]
    public void Frame_focus_is_the_default_and_unknown_ids_fall_back_to_it()
    {
        Assert.Equal("frame", new AppSettings().AppIcon);
        Assert.Equal("frame", AppLogo.DefaultId);
        Assert.Equal(new[] { "frame", "crop-s", "handle" }, AppLogo.All.Select(l => l.Id));
        foreach (var id in new[] { null, "", "nonsense" }) Assert.Equal("frame", AppLogo.Get(id).Id);
        Assert.Equal("crop-s", new AppSettings { AppIcon = "crop-s" }.Clone().AppIcon);
    }

    [AvaloniaFact]
    public void Every_logo_has_every_size_the_app_uses()
    {
        foreach (var logo in AppLogo.All)
            foreach (var size in new[] { 16, 32, 64 })
            {
                using var bitmap = AppLogo.LoadBitmap(logo.Id, size);
                Assert.Equal(size, bitmap.PixelSize.Width);
            }
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_chosen_logo_is_applied_saved_and_restored_by_the_settings_page()
    {
        using var fx = await UiFixture.CreateAsync(populated: false, updateAvailable: false);
        var settings = fx.Services.GetRequiredService<IApplicationSettingsService>();
        var vm = fx.Services.GetRequiredService<SettingsViewModel>();
        await vm.OnNavigatedToAsync();
        Assert.Equal("frame", vm.SelectedLogo.Id);
        Assert.Equal(AppLogo.All.Count, vm.Logos.Count);

        var changed = 0;
        void Count() => changed++;
        AppLogo.Changed += Count;
        try
        {
            vm.SelectedLogo = vm.Logos.First(l => l.Id == "handle");
            await Task.Delay(300);
            Assert.Equal("handle", settings.Current.AppIcon);
            Assert.Equal("handle", AppLogo.CurrentId);
            Assert.Equal(1, changed);
            Assert.True(vm.SelectedLogo.IsSelected);
            Assert.Equal(1, vm.Logos.Count(l => l.IsSelected));

            await vm.OnNavigatedToAsync();
            Assert.Equal("handle", vm.SelectedLogo.Id);

            vm.SelectedLogo = vm.Logos.First(l => l.Id == "frame");     // leave the shared app as it was
            await Task.Delay(300);
            Assert.Equal("frame", AppLogo.CurrentId);
        }
        finally { AppLogo.Changed -= Count; }
    }
}
