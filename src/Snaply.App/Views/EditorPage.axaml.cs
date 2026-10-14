using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Snaply.App.Controls;
using Snaply.App.ViewModels;
using Snaply.Editor.Model;

namespace Snaply.App.Views;

public partial class EditorPage : UserControl
{
    private TextBox? _textBox;
    private bool _committing;

    public EditorPage()
    {
        InitializeComponent();
        var canvas = this.FindControl<EditorCanvas>("EditorSurface")!;
        canvas.TextRequested += OnTextRequested;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => Focus();
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Classes.Set("compact", Bounds.Width < 880); };
    }

    private EditorViewModel? Vm => DataContext as EditorViewModel;

    private static readonly Dictionary<Key, EditorTool> ToolKeys = Snaply.Editor.Model.AnnotationToolset.Tools
        .ToDictionary(t => Enum.Parse<Key>(t.Shortcut.ToString()), t => Enum.Parse<EditorTool>(t.Kind.ToString()));

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null || _textBox is not null) return; // the in-place text editor handles its own keys
        if (e.Source is TextBox) return;                  // typing in OCR text or other inputs

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift: vm.RedoCommand.Execute(null); break;
                case Key.Z: vm.UndoCommand.Execute(null); break;
                case Key.Y: vm.RedoCommand.Execute(null); break;
                case Key.S when shift: vm.SaveAsNewCommand.Execute(null); break;
                case Key.S: vm.SaveCommand.Execute(null); break;
                case Key.E: vm.ExportCommand.Execute(null); break;
                case Key.C: vm.CopyCommand.Execute(null); break;
                case Key.OemPlus or Key.Add: vm.ZoomInCommand.Execute(null); break;
                case Key.OemMinus or Key.Subtract: vm.ZoomOutCommand.Execute(null); break;
                case Key.D0: vm.FitCommand.Execute(null); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Delete or Key.Back: vm.DeleteSelectedCommand.Execute(null); break;
            case Key.Escape:
                if (vm.SelectedId is not null) vm.Select(null);
                else if (vm.Tool != EditorTool.Select) vm.Tool = EditorTool.Select;
                else vm.GoBackCommand.Execute(null);
                break;
            case Key.OemPlus or Key.Add: vm.ZoomInCommand.Execute(null); break;
            case Key.OemMinus or Key.Subtract: vm.ZoomOutCommand.Execute(null); break;
            case Key.D0 or Key.NumPad0: vm.FitCommand.Execute(null); break;
            case Key.D1 or Key.NumPad1: vm.ActualSizeCommand.Execute(null); break;
            case Key.Left or Key.Right or Key.Up or Key.Down: Nudge(vm, e.Key, shift ? 10 : 1); break;
            default:
                if (ToolKeys.TryGetValue(e.Key, out var tool)) { vm.Tool = tool; break; }
                return;
        }
        e.Handled = true;
    }

    private static void Nudge(EditorViewModel vm, Key key, float amount)
    {
        if (vm.SelectedId is not { } id || vm.FindAnnotation(id) is not { } ann) return;
        var (dx, dy) = key switch { Key.Left => (-amount, 0f), Key.Right => (amount, 0f), Key.Up => (0f, -amount), _ => (0f, amount) };
        vm.CommitReplace(ann.Translated(dx, dy));
    }

    // ---- in-place text editing ----
    private void OnTextRequested(TextRequest req)
    {
        var vm = Vm;
        if (vm is null) return;
        CommitText(); // finish any editor that is already open

        var layer = this.FindControl<Canvas>("OverlayLayer")!;
        var canvas = this.FindControl<EditorCanvas>("EditorSurface")!;
        var fontSize = Math.Max(10, (req.Existing?.FontSize ?? vm.TextSize) * vm.Zoom);
        var color = req.Existing is { } ex ? Color.FromUInt32(ex.Color) : Color.FromUInt32(vm.SelectedColor.Argb);

        var box = new TextBox
        {
            Text = req.Existing?.Text ?? "",
            AcceptsReturn = true,
            MinWidth = 120,
            FontSize = fontSize,
            FontFamily = new FontFamily("Segoe UI, Inter, sans-serif"),
            Foreground = new SolidColorBrush(color),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderBrush = (IBrush?)Application.Current?.FindResource(ActualThemeVariant, "AccentBrush"),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(2),
            MinHeight = 0,
            Watermark = "Type text…",
            Tag = req,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseText(commit: false); e.Handled = true; }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { CloseText(commit: true); e.Handled = true; }
        };
        box.LostFocus += (_, _) => CommitText();

        Canvas.SetLeft(box, req.ViewPosition.X);
        Canvas.SetTop(box, req.ViewPosition.Y);
        layer.Children.Add(box);
        _textBox = box;
        box.Focus();
        if (req.Existing is not null) box.SelectAll();
    }

    private void CommitText() => CloseText(commit: true);

    private void CloseText(bool commit)
    {
        if (_textBox is not { } box || _committing) return;
        _committing = true;
        try
        {
            var req = (TextRequest)box.Tag!;
            _textBox = null;
            this.FindControl<Canvas>("OverlayLayer")!.Children.Remove(box);
            if (commit && Vm is { } vm)
            {
                if (req.Existing is { } existing) vm.UpdateText(existing, box.Text ?? "");
                else vm.AddText(req.DocPosition, box.Text ?? "");
            }
            Focus();
        }
        finally { _committing = false; }
    }
}
