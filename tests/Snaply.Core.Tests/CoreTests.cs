using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;

namespace Snaply.Core.Tests;

public class GestureTests
{
    [Theory]
    [InlineData("Alt+Shift+S", HotkeyModifiers.Alt | HotkeyModifiers.Shift, "S")]
    [InlineData("ctrl+f5", HotkeyModifiers.Ctrl, "F5")]
    [InlineData("Win+PrintScreen", HotkeyModifiers.Meta, "Printscreen")]
    public void Parses_valid_gestures(string text, HotkeyModifiers mods, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g, out var err), err);
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S")]
    [InlineData("Alt+")]
    [InlineData("Alt+S+D")]
    [InlineData("Alt+@@")]
    public void Rejects_invalid_gestures(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void ToString_is_canonical()
    {
        HotkeyGesture.TryParse("shift+alt+s", out var g, out _);
        Assert.Equal("Alt+Shift+S", g.ToString());
    }
}

public class RawImageTests
{
    [Fact]
    public void Crop_copies_expected_pixels()
    {
        var px = new byte[4 * 4 * 4];
        for (var i = 0; i < 16; i++) { px[i * 4] = (byte)i; px[i * 4 + 3] = 255; }
        var img = new RawImage(4, 4, px);
        var c = img.Crop(new PixelRect(1, 1, 2, 2));
        Assert.Equal(new byte[] { 5, 6, 9, 10 }, new[] { c.Bgra[0], c.Bgra[4], c.Bgra[8], c.Bgra[12] });
    }

    [Fact]
    public void Crop_outside_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawImage(2, 2, new byte[16]).Crop(new PixelRect(5, 5, 2, 2)));

    [Fact]
    public void Black_detection()
    {
        Assert.True(new RawImage(1, 1, new byte[] { 0, 0, 0, 255 }).IsAllBlack());
        Assert.False(new RawImage(1, 1, new byte[] { 0, 1, 0, 255 }).IsAllBlack());
    }

    [Fact]
    public void Mismatched_buffer_rejected() =>
        Assert.Throws<ArgumentException>(() => new RawImage(2, 2, new byte[3]));

    [Fact]
    public void Rect_union_and_intersect()
    {
        var u = PixelRect.Union(new[] { new PixelRect(-100, 0, 100, 50), new PixelRect(0, 0, 200, 100) });
        Assert.Equal(new PixelRect(-100, 0, 300, 100), u);
        Assert.True(new PixelRect(0, 0, 10, 10).Intersect(new PixelRect(20, 20, 5, 5)).IsEmpty);
    }
}

public class SettingsValidatorTests
{
    [Fact]
    public void Defaults_are_valid() => Assert.True(SettingsValidator.Validate(new AppSettings()).IsSuccess);

    [Fact]
    public void Duplicate_hotkeys_rejected()
    {
        var s = new AppSettings();
        s.Hotkeys[HotkeyActions.OpenApp] = s.Hotkeys[HotkeyActions.CaptureRegion];
        var r = SettingsValidator.Validate(s);
        Assert.Equal(ErrorCode.InvalidInput, r.Error!.Code);
    }

    [Fact]
    public void Hotkey_without_modifier_rejected()
    {
        var s = new AppSettings();
        s.Hotkeys[HotkeyActions.CaptureRegion] = "S";
        Assert.False(SettingsValidator.Validate(s).IsSuccess);
    }

    [Fact]
    public void Relative_directory_rejected() =>
        Assert.False(SettingsValidator.Validate(new AppSettings { SaveDirectory = "relative/path" }).IsSuccess);

    [Fact]
    public void Unwritable_directory_rejected()
    {
        // A path beneath an existing *file* can never be created.
        var file = Path.GetTempFileName();
        try
        {
            var r = SettingsValidator.CheckDirectory(Path.Combine(file, "sub"));
            Assert.Equal(ErrorCode.StorageUnavailable, r.Error!.Code);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public void Delay_out_of_range_rejected(int delay) =>
        Assert.False(SettingsValidator.Validate(new AppSettings { CaptureDelaySeconds = delay }).IsSuccess);

    [Fact]
    public void Empty_ocr_languages_rejected() =>
        Assert.False(SettingsValidator.Validate(new AppSettings { OcrLanguages = new() }).IsSuccess);
}

public class HotkeyManagerTests
{
    private static (HotkeyManager, IGlobalHotkeyService, IApplicationSettingsService) Make(bool supported = true)
    {
        var svc = Substitute.For<IGlobalHotkeyService>();
        svc.IsSupported.Returns(supported);
        svc.UnsupportedReason.Returns(supported ? null : "Wayland");
        svc.Register(Arg.Any<string>(), Arg.Any<HotkeyGesture>(), Arg.Any<Action>()).Returns(new HotkeyRegistration(HotkeyStatus.Registered));
        var settings = Substitute.For<IApplicationSettingsService>();
        settings.Current.Returns(new AppSettings());
        return (new HotkeyManager(svc, settings, NullLogger<HotkeyManager>.Instance), svc, settings);
    }

    [Fact]
    public void Registers_all_defaults()
    {
        var (m, svc, _) = Make();
        m.ApplyAll();
        svc.Received(1).Register(HotkeyActions.CaptureRegion, Arg.Is<HotkeyGesture>(g => g.ToString() == "Alt+Shift+S"), Arg.Any<Action>());
        Assert.All(HotkeyActions.All, a => Assert.True(m.Status[a].IsRegistered));
    }

    [Fact]
    public void Conflict_is_recorded_without_throwing()
    {
        var (m, svc, _) = Make();
        svc.Register(HotkeyActions.CaptureRegion, Arg.Any<HotkeyGesture>(), Arg.Any<Action>()).Returns(new HotkeyRegistration(HotkeyStatus.Conflict, "taken"));
        m.ApplyAll();
        Assert.Equal(HotkeyStatus.Conflict, m.Status[HotkeyActions.CaptureRegion].Status);
        Assert.True(m.Status[HotkeyActions.CaptureFullScreen].IsRegistered);
    }

    [Fact]
    public void Service_exception_becomes_failed_status()
    {
        var (m, svc, _) = Make();
        svc.Register(HotkeyActions.OpenApp, Arg.Any<HotkeyGesture>(), Arg.Any<Action>()).Returns(_ => throw new InvalidOperationException("boom"));
        m.ApplyAll();
        Assert.Equal(HotkeyStatus.Failed, m.Status[HotkeyActions.OpenApp].Status);
    }

    [Fact]
    public void Unsupported_platform_reports_reason_and_never_registers()
    {
        var (m, svc, _) = Make(supported: false);
        m.ApplyAll();
        svc.DidNotReceiveWithAnyArgs().Register(default!, default!, default!);
        Assert.Equal("Wayland", m.Status[HotkeyActions.CaptureRegion].Message);
    }

    [Fact]
    public void Settings_change_rebinds()
    {
        var (m, svc, settings) = Make();
        m.ApplyAll();
        svc.ClearReceivedCalls();
        var s = new AppSettings();
        s.Hotkeys[HotkeyActions.CaptureRegion] = "Ctrl+Alt+R";
        settings.Current.Returns(s);
        settings.Changed += Raise.Event<EventHandler<AppSettings>>(settings, s);
        svc.Received(1).Register(HotkeyActions.CaptureRegion, Arg.Is<HotkeyGesture>(g => g.ToString() == "Ctrl+Alt+R"), Arg.Any<Action>());
    }

    [Fact]
    public void Dispose_releases_everything()
    {
        var (m, svc, _) = Make();
        m.ApplyAll();
        m.Dispose();
        svc.Received(1).UnregisterAll();
    }
}

public class CaptureOrchestratorTests
{
    private readonly IScreenCaptureService _capture = Substitute.For<IScreenCaptureService>();
    private readonly IClipboardService _clipboard = Substitute.For<IClipboardService>();
    private readonly IScreenshotLibrary _library = Substitute.For<IScreenshotLibrary>();
    private readonly IApplicationSettingsService _settings = Substitute.For<IApplicationSettingsService>();
    private readonly IRegionSelector _selector = Substitute.For<IRegionSelector>();
    private readonly IAppWindowController _window = Substitute.For<IAppWindowController>();
    private readonly INotificationService _notes = Substitute.For<INotificationService>();
    private readonly AppSettings _s = new();

    public CaptureOrchestratorTests()
    {
        _capture.Support.Returns(new CaptureSupport(true));
        _capture.GetMonitors().Returns(new[]
        {
            new MonitorInfo(0, new PixelRect(0, 0, 100, 80), 1, true),
            new MonitorInfo(1, new PixelRect(100, 0, 100, 80), 1, false),
        });
        _capture.CaptureAsync(Arg.Any<PixelRect>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<PixelRect>().IsEmpty
                ? Result<RawImage>.Fail(ErrorCode.Failed, "empty")
                : Result<RawImage>.Ok(new RawImage(c.Arg<PixelRect>().Width, c.Arg<PixelRect>().Height, new byte[c.Arg<PixelRect>().Width * c.Arg<PixelRect>().Height * 4])));
        _settings.Current.Returns(_s);
        _library.AddAsync(Arg.Any<RawImage>(), Arg.Any<CaptureMethod>(), Arg.Any<CancellationToken>())
            .Returns(Result<Screenshot>.Ok(new Screenshot { Id = 7 }));
        _clipboard.SetImageAsync(Arg.Any<RawImage>(), Arg.Any<CancellationToken>()).Returns(Result.Ok());
        _window.HideAsync().Returns(Task.CompletedTask);
        _window.RestoreAsync().Returns(Task.CompletedTask);
    }

    private CaptureOrchestrator Sut() =>
        new(_capture, _clipboard, _library, _settings, _selector, _window, _notes, NullLogger<CaptureOrchestrator>.Instance);

    [Fact]
    public async Task Full_screen_uses_monitor_under_cursor_and_saves()
    {
        _capture.GetCursorPosition().Returns(new PixelPoint(150, 10));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.True(r.IsSuccess);
        await _capture.Received().CaptureAsync(new PixelRect(100, 0, 100, 80), Arg.Any<CancellationToken>());
        Assert.Equal(7, r.Value.Saved!.Id);
        _notes.Received().Show(NotificationKind.Success, Arg.Any<string>(), Arg.Any<string?>());
        await _window.Received().RestoreAsync();
    }

    [Fact]
    public async Task Full_screen_all_monitors_captures_virtual_desktop()
    {
        _s.FullScreenTarget = MonitorTarget.AllMonitors;
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.Equal((200, 80), (r.Value.Image.Width, r.Value.Image.Height));
    }

    [Fact]
    public async Task Region_crops_selection_from_frozen_frame()
    {
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(90, 10, 40, 30), null, SelectionAction.Default));
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.Equal((40, 30), (r.Value.Image.Width, r.Value.Image.Height));
    }

    [Fact]
    public async Task Cancelled_selection_saves_nothing_and_shows_no_error()
    {
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs((RegionSelection?)null);
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.True(r.IsCancelled);
        await _library.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        _notes.DidNotReceive().Show(NotificationKind.Error, Arg.Any<string>(), Arg.Any<string?>());
        await _window.Received().RestoreAsync();
    }

    [Fact]
    public async Task Tiny_selection_counts_as_cancel()
    {
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(5, 5, 1, 1), null, SelectionAction.Default));
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.True(r.IsCancelled);
    }

    [Fact]
    public async Task Permission_denied_is_surfaced_not_blank()
    {
        _capture.CaptureAsync(default, default).ReturnsForAnyArgs(Result<RawImage>.Fail(ErrorCode.PermissionDenied, "Grant Screen Recording"));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.Equal(ErrorCode.PermissionDenied, r.Error!.Code);
        _notes.Received().Show(NotificationKind.Error, Arg.Any<string>(), "Grant Screen Recording");
        await _library.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Unsupported_platform_reports_reason()
    {
        _capture.Support.Returns(new CaptureSupport(false, "No display server"));
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.Equal(ErrorCode.Unsupported, r.Error!.Code);
        Assert.Equal("No display server", r.Error.Message);
    }

    [Fact]
    public async Task Save_failure_is_reported_and_no_success_toast()
    {
        _library.AddAsync(default!, default).ReturnsForAnyArgs(Result<Screenshot>.Fail(ErrorCode.StorageUnavailable, "Folder not writable"));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.False(r.IsSuccess);
        _notes.DidNotReceive().Show(NotificationKind.Success, Arg.Any<string>(), Arg.Any<string?>());
        _notes.Received().Show(NotificationKind.Error, Arg.Any<string>(), "Folder not writable");
    }

    [Fact]
    public async Task Clipboard_only_failure_fails_the_capture()
    {
        _s.SaveToLibraryAfterCapture = false; _s.CopyToClipboardAfterCapture = true;
        _clipboard.SetImageAsync(default!, default).ReturnsForAnyArgs(Result.Fail(ErrorCode.Failed, "clipboard busy"));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.False(r.IsSuccess);
        await _library.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Clipboard_failure_after_save_is_a_warning_only()
    {
        _s.CopyToClipboardAfterCapture = true;
        _clipboard.SetImageAsync(default!, default).ReturnsForAnyArgs(Result.Fail(ErrorCode.Failed, "busy"));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.True(r.IsSuccess);
        Assert.False(r.Value.CopiedToClipboard);
        _notes.Received().Show(NotificationKind.Warning, Arg.Any<string>(), "busy");
    }

    [Fact]
    public async Task Both_outputs_disabled_forces_inline_editor_and_nothing_is_saved_without_a_choice()
    {
        _s.SaveToLibraryAfterCapture = false; _s.CopyToClipboardAfterCapture = false; _s.EditAfterSelection = false;
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs((RegionSelection?)null);
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.True(r.IsCancelled);
        await _selector.ReceivedWithAnyArgs().SelectAsync(default!, default, default!, Arg.Is<RegionSelectOptions>(o => o.EditInline), default);
        await _library.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Autosave_off_full_screen_opens_inline_editor_with_whole_monitor_preselected()
    {
        _s.SaveToLibraryAfterCapture = false; _s.CopyToClipboardAfterCapture = false;
        _capture.GetCursorPosition().Returns(new PixelPoint(150, 10));
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(100, 0, 100, 80), null, SelectionAction.Save));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.NotNull(r.Value.Saved);
        await _selector.ReceivedWithAnyArgs().SelectAsync(default!, default, default!, Arg.Is<RegionSelectOptions>(o => o.Preselect == new PixelRect(100, 0, 100, 80)), default);
    }

    [Fact]
    public async Task Inline_save_action_saves_without_copying_even_if_copy_enabled()
    {
        _s.SaveToLibraryAfterCapture = false; _s.CopyToClipboardAfterCapture = true;
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(10, 10, 20, 20), null, SelectionAction.Save));
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.NotNull(r.Value.Saved);
        Assert.False(r.Value.CopiedToClipboard);
    }

    [Fact]
    public async Task Inline_copy_action_only_copies()
    {
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(10, 10, 20, 20), null, SelectionAction.Copy));
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.Null(r.Value.Saved);
        Assert.True(r.Value.CopiedToClipboard);
    }

    [Fact]
    public async Task Edited_image_from_inline_editor_is_what_gets_saved()
    {
        var edited = new RawImage(7, 5, new byte[7 * 5 * 4]);
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(10, 10, 20, 20), edited, SelectionAction.Default));
        var r = await Sut().CaptureAsync(CaptureMode.Region);
        Assert.Same(edited, r.Value.Image);
        await _library.Received().AddAsync(edited, Arg.Any<CaptureMethod>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_after_selection_off_skips_the_overlay_toolbar()
    {
        _s.EditAfterSelection = false;
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(10, 10, 20, 20), null, SelectionAction.Default));
        await Sut().CaptureAsync(CaptureMode.Region);
        await _selector.ReceivedWithAnyArgs().SelectAsync(default!, default, default!, Arg.Is<RegionSelectOptions>(o => !o.EditInline), default);
    }

    [Fact]
    public async Task Repeat_last_reuses_region_without_overlay()
    {
        _selector.SelectAsync(default!, default, default!, default!, default).ReturnsForAnyArgs(new RegionSelection(new PixelRect(10, 10, 50, 40), null, SelectionAction.Default));
        var sut = Sut();
        await sut.CaptureAsync(CaptureMode.Region);
        _selector.ClearReceivedCalls();
        var r = await sut.RepeatLastAsync();
        Assert.True(r.IsSuccess);
        Assert.Equal((50, 40), (r.Value.Image.Width, r.Value.Image.Height));
        await _selector.DidNotReceiveWithAnyArgs().SelectAsync(default!, default, default!, default!, default);
    }

    [Fact]
    public async Task Repeat_without_history_reports_not_found()
    {
        var r = await Sut().RepeatLastAsync();
        Assert.Equal(ErrorCode.NotFound, r.Error!.Code);
    }

    [Fact]
    public async Task Unexpected_exception_is_contained()
    {
        _capture.CaptureAsync(default, default).ReturnsForAnyArgs<Task<Result<RawImage>>>(_ => throw new InvalidOperationException("boom"));
        var r = await Sut().CaptureAsync(CaptureMode.FullScreen);
        Assert.Equal(ErrorCode.Failed, r.Error!.Code);
        Assert.DoesNotContain("boom", r.Error.Message);
        await _window.Received().RestoreAsync();
    }
}

public class OcrWorkflowTests
{
    private readonly IOcrService _ocr = Substitute.For<IOcrService>();
    private readonly IScreenshotRepository _repo = Substitute.For<IScreenshotRepository>();

    [Fact]
    public async Task Success_stores_text()
    {
        var file = Path.GetTempFileName();
        try
        {
            _repo.GetAsync(1).Returns(new Screenshot { Id = 1, FilePath = file });
            _ocr.RecognizeAsync(file, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(Result<OcrResult>.Ok(new OcrResult("hello", Array.Empty<OcrBlock>(), "eng", "fake")));
            var r = await new OcrWorkflow(_ocr, _repo, NullLogger<OcrWorkflow>.Instance).RunAsync(1, new[] { "eng" });
            Assert.True(r.IsSuccess);
            await _repo.Received(1).SetOcrAsync(1, "hello", "eng", Arg.Any<CancellationToken>());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Failure_leaves_previous_text_untouched()
    {
        var file = Path.GetTempFileName();
        try
        {
            _repo.GetAsync(1).Returns(new Screenshot { Id = 1, FilePath = file, OcrText = "old" });
            _ocr.RecognizeAsync(default!, default!, default).ReturnsForAnyArgs(Result<OcrResult>.Fail(ErrorCode.LanguageUnavailable, "no vie"));
            var r = await new OcrWorkflow(_ocr, _repo, NullLogger<OcrWorkflow>.Instance).RunAsync(1, new[] { "vie" });
            Assert.Equal(ErrorCode.LanguageUnavailable, r.Error!.Code);
            await _repo.DidNotReceiveWithAnyArgs().SetOcrAsync(default, default, default);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Missing_file_reported()
    {
        _repo.GetAsync(1).Returns(new Screenshot { Id = 1, FilePath = "C:/does/not/exist.png" });
        var r = await new OcrWorkflow(_ocr, _repo, NullLogger<OcrWorkflow>.Instance).RunAsync(1, new[] { "eng" });
        Assert.Equal(ErrorCode.NotFound, r.Error!.Code);
        await _ocr.DidNotReceiveWithAnyArgs().RecognizeAsync(default!, default!, default);
    }

    [Fact]
    public async Task Cancellation_stores_nothing()
    {
        var file = Path.GetTempFileName();
        try
        {
            _repo.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new Screenshot { Id = 1, FilePath = file });
            using var cts = new CancellationTokenSource();
            _ocr.RecognizeAsync(default!, default!, default).ReturnsForAnyArgs(_ =>
            {
                cts.Cancel();
                return Result<OcrResult>.Ok(new OcrResult("x", Array.Empty<OcrBlock>(), "eng", "fake"));
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new OcrWorkflow(_ocr, _repo, NullLogger<OcrWorkflow>.Instance).RunAsync(1, new[] { "eng" }, cts.Token));
            await _repo.DidNotReceiveWithAnyArgs().SetOcrAsync(default, default, default);
        }
        finally { File.Delete(file); }
    }
}

public class FrameMappingTests
{
    [Fact]
    public void Identity_scale_offsets_by_virtual_origin()
    {
        var frame = new RawImage(200, 80, new byte[200 * 80 * 4]);
        var r = CaptureOrchestrator.ToFramePixels(frame, new PixelRect(-100, 0, 200, 80), new PixelRect(-90, 10, 40, 30));
        Assert.Equal(new PixelRect(10, 10, 40, 30), r);
    }

    [Fact]
    public void Retina_frame_twice_the_logical_size_doubles_the_crop()
    {
        var frame = new RawImage(400, 160, new byte[400 * 160 * 4]);
        var r = CaptureOrchestrator.ToFramePixels(frame, new PixelRect(0, 0, 200, 80), new PixelRect(10, 5, 40, 30));
        Assert.Equal(new PixelRect(20, 10, 80, 60), r);
    }
}
