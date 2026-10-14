using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;

namespace Snaply.Core.Tests;

/// <summary>The window title is read before Snaply hides anything, honours the setting and never breaks a capture.</summary>
public class SourceWindowTests
{
    private readonly IScreenCaptureService _capture = Substitute.For<IScreenCaptureService>();
    private readonly IClipboardService _clipboard = Substitute.For<IClipboardService>();
    private readonly IScreenshotLibrary _library = Substitute.For<IScreenshotLibrary>();
    private readonly IApplicationSettingsService _settings = Substitute.For<IApplicationSettingsService>();
    private readonly IRegionSelector _selector = Substitute.For<IRegionSelector>();
    private readonly IAppWindowController _window = Substitute.For<IAppWindowController>();
    private readonly INotificationService _notes = Substitute.For<INotificationService>();
    private readonly IActiveWindowService _active = Substitute.For<IActiveWindowService>();
    private readonly AppSettings _s = new();
    private readonly List<string> _order = new();

    public SourceWindowTests()
    {
        _capture.Support.Returns(new CaptureSupport(true));
        _capture.GetMonitors().Returns(new[] { new MonitorInfo(0, new PixelRect(0, 0, 100, 80), 1, true) });
        _capture.CaptureAsync(Arg.Any<PixelRect>(), Arg.Any<CancellationToken>())
            .Returns(c => Result<RawImage>.Ok(new RawImage(c.Arg<PixelRect>().Width, c.Arg<PixelRect>().Height, new byte[c.Arg<PixelRect>().Width * c.Arg<PixelRect>().Height * 4])));
        _settings.Current.Returns(_s);
        _library.AddAsync(Arg.Any<RawImage>(), Arg.Any<CaptureMethod>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<Screenshot>.Ok(new Screenshot { Id = 1 }));
        _clipboard.SetImageAsync(Arg.Any<RawImage>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        _window.HideAsync().Returns(_ => { _order.Add("hide"); return Task.FromResult(true); });
        _window.RestoreAsync().Returns(Task.CompletedTask);
        _active.GetForegroundWindowTitle().Returns(_ => { _order.Add("read"); return "Postman - Collections"; });
        _s.FullScreenTarget = MonitorTarget.UnderCursor;
    }

    private CaptureOrchestrator Sut() =>
        new(_capture, _clipboard, _library, _settings, _selector, _window, _notes, NullLogger<CaptureOrchestrator>.Instance, _active);

    private Task<string?> SavedTitle()
    {
        var call = _library.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IScreenshotLibrary.AddAsync));
        return Task.FromResult((string?)call.GetArguments()[2]);
    }

    [Fact]
    public async Task The_title_is_read_before_the_main_window_is_hidden_and_saved_with_the_capture()
    {
        await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.Equal(new[] { "read", "hide" }, _order.Take(2));
        Assert.Equal("Postman - Collections", await SavedTitle());
    }

    [Fact]
    public async Task Nothing_is_read_or_stored_when_the_setting_is_off()
    {
        _s.RememberSourceWindow = false;
        await Sut().CaptureAsync(CaptureMode.FullScreen);
        _active.DidNotReceive().GetForegroundWindowTitle();
        Assert.Null(await SavedTitle());
    }

    [Fact]
    public async Task A_missing_title_is_saved_as_none()
    {
        _active.GetForegroundWindowTitle().Returns((string?)null);
        await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.Null(await SavedTitle());
    }

    [Fact]
    public async Task A_failing_window_service_does_not_break_the_capture()
    {
        _active.GetForegroundWindowTitle().Returns(_ => throw new InvalidOperationException("boom"));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.True(r.IsSuccess);
        Assert.Null(await SavedTitle());
    }

    [Fact]
    public async Task Without_a_window_service_captures_still_work()
    {
        var sut = new CaptureOrchestrator(_capture, _clipboard, _library, _settings, _selector, _window, _notes, NullLogger<CaptureOrchestrator>.Instance);
        var r = await sut.CaptureAsync(CaptureMode.FullScreen);
        Assert.True(r.IsSuccess);
        Assert.Null(await SavedTitle());
    }

    [Fact]
    public void The_setting_is_on_by_default_and_cloned()
    {
        var s = new AppSettings();
        Assert.True(s.RememberSourceWindow);
        s.RememberSourceWindow = false;
        Assert.False(s.Clone().RememberSourceWindow);
    }
}
