using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HwMon.Client;

namespace HwMon.App.Controls;

/// <summary>Uma barra: rótulo no eixo, valor e o texto do balão ao passar o mouse.</summary>
public sealed record ChartBar(string Label, double Value, string Tooltip);

/// <summary>Barras verticais simples (uso por dia ou por mês) com grade, rótulos e balão sob o mouse.</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(nameof(Bars), typeof(IReadOnlyList<ChartBar>), typeof(BarChart),
        new FrameworkPropertyMetadata(Array.Empty<ChartBar>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(BarChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(nameof(TooltipBackground), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartBar> Bars { get => (IReadOnlyList<ChartBar>)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    /// <summary>Unidade dos rótulos do eixo vertical (a mesma de <see cref="Format.Number"/>).</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush TooltipBackground { get => (Brush)GetValue(TooltipBackgroundProperty); set => SetValue(TooltipBackgroundProperty, value); }

    private Point? _mouse;
    private const double Left = 64, Right = 8, Top = 10, Bottom = 22;

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var bars = Bars;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = (FontFamily)GetValue(System.Windows.Documents.TextElement.FontFamilyProperty);
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Text(string s, double size, Brush brush, FontWeight? weight = null)
            => new(s, Format.Pt, FlowDirection.LeftToRight, weight is null ? typeface : new Typeface(family, FontStyles.Normal, weight.Value, FontStretches.Normal), size, brush, dpi);

        if (bars.Count == 0 || bars.All(b => b.Value <= 0))
        {
            var empty = Text(L.T("Sem dados neste período"), 13, LabelBrush);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        var plot = new Rect(Left, Top, Math.Max(10, w - Left - Right), Math.Max(10, h - Top - Bottom));
        var hi = Nice(bars.Max(b => b.Value) * 1.1);
        double Y(double v) => plot.Bottom - Math.Clamp(v, 0, hi) / hi * plot.Height;

        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        gridPen.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var v = hi * i / 4;
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            // Escala pequena (ex.: 0,5 h): com casas decimais, senão tudo vira "0 h".
            var label = Text(hi < 10 ? v.ToString("0.##", Format.Pt) + " " + Unit : Format.Number(v, Unit), 11, LabelBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }

        var slot = plot.Width / bars.Count;
        var barWidth = Math.Max(2, Math.Min(36, slot * 0.7));
        // Rótulos do eixo: só os que cabem (no máximo um a cada ~44 px).
        var every = Math.Max(1, (int)Math.Ceiling(44 / slot));
        int? hover = _mouse is { } m && plot.Contains(m) ? Math.Clamp((int)((m.X - plot.Left) / slot), 0, bars.Count - 1) : null;

        for (var i = 0; i < bars.Count; i++)
        {
            var cx = plot.Left + slot * (i + 0.5);
            var b = bars[i];
            if (b.Value > 0)
            {
                var top = Y(b.Value);
                var rect = new Rect(cx - barWidth / 2, top, barWidth, Math.Max(1, plot.Bottom - top));
                dc.PushOpacity(hover is null || hover == i ? 1 : 0.55);
                dc.DrawRoundedRectangle(Fill, null, rect, Math.Min(4, barWidth / 3), Math.Min(4, barWidth / 3));
                dc.Pop();
            }
            if (i % every == 0)
            {
                var label = Text(b.Label, 11, LabelBrush);
                dc.DrawText(label, new Point(Math.Clamp(cx - label.Width / 2, plot.Left - 20, w - label.Width), plot.Bottom + 5));
            }
        }

        if (hover is { } hi2)
        {
            var b = bars[hi2];
            var cx = plot.Left + slot * (hi2 + 0.5);
            var lines = b.Tooltip.Split('\n');
            var title = Text(lines[0], 11, LabelBrush);
            var rest = lines.Skip(1).Select((l, k) => Text(l, k == 0 ? 15 : 11, k == 0 ? TextBrush : LabelBrush, k == 0 ? FontWeights.SemiBold : null)).ToList();
            var bw = Math.Max(title.Width, rest.Select(r => r.Width).DefaultIfEmpty(0).Max()) + 20;
            var bh = title.Height + rest.Sum(r => r.Height) + 16;
            var bx = cx + 14 + bw > plot.Right ? cx - 14 - bw : cx + 14;
            var box = new Rect(Math.Max(0, bx), plot.Top + 4, bw, bh);
            dc.DrawRoundedRectangle(TooltipBackground, new Pen(GridBrush, 1), box, 8, 8);
            var y = box.Top + 7;
            dc.DrawText(title, new Point(box.Left + 10, y));
            y += title.Height;
            foreach (var r in rest)
            {
                dc.DrawText(r, new Point(box.Left + 10, y));
                y += r.Height;
            }
        }
    }

    private static double Nice(double v)
    {
        if (v <= 0) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var f = v / exp;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp;
    }
}
