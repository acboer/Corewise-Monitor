using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace HwMon.App.Controls;

/// <summary>
/// Ícone de uma cor da identidade Corewise (icons/*.svg → IconData.g.cs).
/// Desenha na cor do texto ao redor (Foreground herdado), como o currentColor do SVG. Tamanho padrão 16.
/// </summary>
public sealed class Icon : FrameworkElement
{
    private static readonly Dictionary<string, (Geometry Geometry, bool Stroke, bool Fill)[]> Cache = new();

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Espessura do traço na grade de 24 (1,5 = o desenho original).</summary>
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));
    }

    public string? Kind { get => (string?)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public static bool Exists(string? kind) => kind is not null && IconData.All.ContainsKey(kind);

    private static (Geometry, bool, bool)[]? Parts(string? kind)
    {
        if (kind is null) return null;
        if (Cache.TryGetValue(kind, out var parts)) return parts;
        if (!IconData.All.TryGetValue(kind, out var raw)) return null;
        parts = raw.Select(p =>
        {
            var g = Geometry.Parse(p.Data);
            g.Freeze();
            return (g, p.Stroke, p.Fill);
        }).ToArray();
        Cache[kind] = parts;
        return parts;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var parts = Parts(Kind);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (parts is null || size <= 0) return;

        var scale = size / 24.0;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        foreach (var (geometry, stroke, fill) in parts)
            dc.DrawGeometry(fill ? Foreground : null, stroke ? pen : null, geometry);
        dc.Pop();
        dc.Pop();
    }
}
