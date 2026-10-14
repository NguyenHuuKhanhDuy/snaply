using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Snaply.App.Controls;

/// <summary>Stroke icon drawn from a 24x24 geometry. Follows the inherited foreground so it matches surrounding text.</summary>
public class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 18);
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.75);
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeThicknessProperty, StrokeProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    /// <summary>Explicit stroke; when null the inherited text foreground is used.</summary>
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextElement.ForegroundProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Data is null) return;
        var brush = Stroke ?? TextElement.GetForeground(this);
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var scale = Size / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, Data);
    }
}
