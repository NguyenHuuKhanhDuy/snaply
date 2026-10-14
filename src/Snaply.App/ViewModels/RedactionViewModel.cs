using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Redaction;
using Snaply.Editor.Model;

namespace Snaply.App.ViewModels;

/// <summary>One suspected sensitive value. Unchecked until the user decides; checking it never changes the image by itself.</summary>
public sealed partial class RedactionSuggestion : ObservableObject
{
    public RedactionSuggestion(SensitiveFinding finding) => Finding = finding;

    public SensitiveFinding Finding { get; }
    public string TypeLabel => SensitiveText.Label(Finding.Type);
    /// <summary>Partly masked, so the list never shows a full secret.</summary>
    public string Preview => Finding.Preview;
    public string ConfidenceText => Finding.Confidence switch { SensitiveConfidence.High => "likely", SensitiveConfidence.Medium => "possible", _ => "unsure" };
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class RedactionGroup : ObservableObject
{
    public RedactionGroup(SensitiveType type, IEnumerable<RedactionSuggestion> items)
    {
        Type = type;
        foreach (var i in items) Items.Add(i);
    }

    public SensitiveType Type { get; }
    public string Label => $"{SensitiveText.Label(Type)} ({Items.Count})";
    public ObservableCollection<RedactionSuggestion> Items { get; } = new();

    /// <summary>"Select all of this type": checks everything, or unchecks everything when all are already checked.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var target = Items.Any(i => !i.IsChecked);
        foreach (var i in Items) i.IsChecked = target;
    }
}

/// <summary>
/// The review workflow for sensitive-data suggestions in the editor. A scan is always started by the user; suggestions stay in memory
/// only; nothing is covered until the user applies it.
/// </summary>
public sealed partial class RedactionViewModel : ObservableObject
{
    private readonly IOcrService _ocr;
    private readonly IApplicationSettingsService _settings;
    private readonly INotificationService _notes;
    private readonly SensitiveDataDetector _detector = new();
    private readonly ILogger<RedactionViewModel> _log;
    private EditorViewModel? _editor;
    private CancellationTokenSource? _cts;

    public RedactionViewModel(IOcrService ocr, IApplicationSettingsService settings, INotificationService notes, ILogger<RedactionViewModel> log)
    {
        _ocr = ocr; _settings = settings; _notes = notes; _log = log;
    }

    public ObservableCollection<RedactionGroup> Groups { get; } = new();

    /// <summary>Raised whenever the suggestions or their checked state change, so the canvas can redraw.</summary>
    public event Action? Changed;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanScan), nameof(ShowIdleHint))] private bool _isScanning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _summary;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowIdleHint), nameof(ShowNothingFound))] private bool _hasScanned;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChecked), nameof(CheckedText))] private int _checkedCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSuggestions), nameof(ShowNothingFound), nameof(ShowIdleHint))] private int _suggestionCount;

    public bool OcrAvailable => _ocr.IsAvailable;
    public string? OcrHint => _ocr.UnavailableReason;
    public bool CanScan => !IsScanning && _editor?.Document is not null;
    public bool HasSuggestions => SuggestionCount > 0;
    public bool HasChecked => CheckedCount > 0;
    public bool ShowNothingFound => HasScanned && !IsScanning && SuggestionCount == 0 && Summary is null;
    public bool ShowIdleHint => !HasScanned && !IsScanning && SuggestionCount == 0;
    public string CheckedText => CheckedCount == 1 ? "Cover 1 selected" : $"Cover {CheckedCount} selected";

    public IEnumerable<RedactionSuggestion> All => Groups.SelectMany(g => g.Items);

    public void Attach(EditorViewModel editor) => _editor = editor;

    /// <summary>Forget everything: used when another image is loaded or the editor is left.</summary>
    public void Reset()
    {
        Cancel();
        Clear();
        HasScanned = false; Summary = null; Status = null;
        OnPropertyChanged(nameof(CanScan));
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts = null;
        IsScanning = false;
    }

    private void Clear()
    {
        foreach (var s in All) s.PropertyChanged -= OnSuggestionChanged;
        Groups.Clear();
        SuggestionCount = 0; CheckedCount = 0;
        Changed?.Invoke();
    }

    [RelayCommand]
    private void CancelScan() { Cancel(); Status = "Scan cancelled."; }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_editor?.Screenshot is not { } shot || _editor.Document is not { } doc) return;
        Reset();

        if (!_ocr.IsAvailable) { Status = _ocr.UnavailableReason; return; }
        var installed = _ocr.GetInstalledLanguages();
        var languages = _settings.Current.OcrLanguages.Where(l => installed.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
        if (languages.Count == 0) languages.Add(installed[0]);

        var cts = _cts = new CancellationTokenSource();
        IsScanning = true;
        Status = "Reading the image…";
        try
        {
            // Recognise the original file (not the annotated render) and keep the result in memory only: nothing is stored or logged.
            var result = await _ocr.RecognizeAsync(shot.FilePath, languages, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _cts)) return;
            if (!result.IsSuccess)
            {
                Status = result.Error!.Code == ErrorCode.Cancelled ? "Scan cancelled." : result.Error.Message;
                return;
            }

            var disabled = _settings.Current.DisabledRedactionTypes
                .Select(n => Enum.TryParse<SensitiveType>(n, out var t) ? (SensitiveType?)t : null)
                .Where(t => t is not null).Select(t => t!.Value).ToList();
            var findings = await Task.Run(() => _detector.Detect(result.Value.Words, doc.Width, doc.Height, disabled)).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(cts, _cts)) return;

            Populate(findings);
            HasScanned = true;
            Status = findings.Count == 0
                ? "Nothing sensitive was found. Text recognition can miss things, so look over the image yourself."
                : $"Found {findings.Count} possible item{(findings.Count == 1 ? "" : "s")}. Nothing is covered until you choose.";
        }
        catch (OperationCanceledException) { Status = "Scan cancelled."; }
        finally
        {
            if (ReferenceEquals(cts, _cts)) { IsScanning = false; _cts = null; }
            cts.Dispose();
            OnPropertyChanged(nameof(CanScan));
        }
    }

    private void Populate(IReadOnlyList<SensitiveFinding> findings)
    {
        Clear();
        foreach (var g in findings.GroupBy(f => f.Type).OrderBy(g => g.Key))
        {
            var items = g.OrderBy(f => f.Regions[0].Y).ThenBy(f => f.Regions[0].X).Select(f => new RedactionSuggestion(f)).ToList();
            foreach (var i in items) i.PropertyChanged += OnSuggestionChanged;
            Groups.Add(new RedactionGroup(g.Key, items));
        }
        SuggestionCount = findings.Count;
        Changed?.Invoke();
    }

    private void OnSuggestionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RedactionSuggestion.IsChecked)) return;
        CheckedCount = All.Count(s => s.IsChecked);
        Changed?.Invoke();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var s in All) s.IsChecked = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var s in All) s.IsChecked = false;
    }

    [RelayCommand]
    private void Dismiss() { Reset(); }

    /// <summary>Toggles the suggestion under a document point (clicking a highlighted box on the image).</summary>
    public bool ToggleAt(DocPoint p)
    {
        var hit = All.FirstOrDefault(s => s.Finding.Regions.Any(r => p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom));
        if (hit is null) return false;
        hit.IsChecked = !hit.IsChecked;
        return true;
    }

    [RelayCommand]
    private void ApplySelected() => Apply(All.Where(s => s.IsChecked).ToList());

    [RelayCommand]
    private void ApplyLikely() => Apply(All.Where(s => s.Finding.Confidence == SensitiveConfidence.High).ToList());

    private void Apply(IReadOnlyList<RedactionSuggestion> chosen)
    {
        if (chosen.Count == 0 || _editor?.History is not { } history) return;

        var boxes = chosen.SelectMany(s => s.Finding.Regions)
            .Select(r => (Annotation)new RedactAnnotation { Rect = new DocRect(r.X, r.Y, r.Width, r.Height) })
            .ToList();
        history.Execute(new AddAnnotationsCommand(boxes, $"Redact {boxes.Count} region{(boxes.Count == 1 ? "" : "s")}"));

        // The applied suggestions are done; the rest stay available.
        var remaining = All.Except(chosen).Select(s => s.Finding).ToList();
        Populate(remaining);
        Summary = $"Covered {boxes.Count} region{(boxes.Count == 1 ? "" : "s")}. Detection is not exhaustive: look over the image before you share it.";
        Status = null;
        _notes.Show(NotificationKind.Success, $"Covered {boxes.Count} region{(boxes.Count == 1 ? "" : "s")}", "Check the image before sharing; detection can miss things.");
        _log.LogInformation("Applied {Count} redaction regions", boxes.Count); // counts only, never the text
        OnPropertyChanged(nameof(ShowNothingFound));
    }
}
