using System.Windows;
using System.Windows.Media;

namespace HwMon.App.Controls;

/// <summary>Gráfico curto (últimos 2 min): linha com preenchimento suave, sem eixos.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Quantos pontos cabem na largura (o gráfico cresce da direita para a esquerda).</summary>
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));

    // IReadOnlyList (e não double[]): o compilador de XAML não aceita propriedade do tipo array em templates.
    public IReadOnlyList<double> Values { get => (IReadOnlyList<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is not { Count: > 1 } || w <= 0 || h <= 0) return;

        var max = Maximum > 0 ? Maximum : 1;
        var step = w / Math.Max(1, Capacity - 1);
        var start = w - (values.Count - 1) * step;
        Point P(int i) => new(start + i * step, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (var i = 1; i < values.Count; i++) g.LineTo(P(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(start, h), true, true);
            for (var i = 0; i < values.Count; i++) g.LineTo(P(i), true, true);
            g.LineTo(new Point(w, h), true, true);
        }
        area.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        // Preenchimento suave (12 % no topo, design/tipografia.json → sparkline.fillOpacity).
        var fill = new LinearGradientBrush(Color.FromArgb(31, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        var pen = new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>Barra de ocupação arredondada (volumes, uso).</summary>
public sealed class FractionBar : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(FractionBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(FractionBar), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(FractionBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 6 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        var fw = Math.Clamp(Fraction, 0, 1) * w;
        if (fw > 0.5) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(fw, h), h), r, r);
    }
}
