using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.Controls;

/// <summary>
/// Gráfico do histórico: linha da média com faixa suave de mínimo a máximo, grade com rótulos e eixo de tempo.
/// Intervalos sem dados (PC desligado) ficam em branco. Passar o mouse mostra o ponto com data, média, mín. e máx.
/// </summary>
public sealed class HistoryChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<HistoryPoint>), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Array.Empty<HistoryPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(HistoryResponse), typeof(HistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TooltipBackgroundProperty = DependencyProperty.Register(nameof(TooltipBackground), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<HistoryPoint> Points { get => (IReadOnlyList<HistoryPoint>)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>Resposta inteira (período, passo, unidade).</summary>
    public HistoryResponse? Data { get => (HistoryResponse?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush TooltipBackground { get => (Brush)GetValue(TooltipBackgroundProperty); set => SetValue(TooltipBackgroundProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private Point? _mouse;

    public HistoryChart()
    {
        // Fundo transparente "acertável" para receber o mouse em toda a área.
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _mouse = null;
        InvalidateVisual();
    }

    private const double Left = 64, Right = 8, Top = 10, Bottom = 24;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var data = Data;
        var points = Points;
        var plot = new Rect(Left, Top, Math.Max(10, w - Left - Right), Math.Max(10, h - Top - Bottom));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElementFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        FormattedText Text(string s, double size, Brush brush, FontWeight? weight = null)
            => new(s, Format.Pt, FlowDirection.LeftToRight, weight is null ? typeface : new Typeface(typeface.FontFamily, FontStyles.Normal, weight.Value, FontStretches.Normal), size, brush, dpi);

        if (data is null || points.Count == 0)
        {
            var empty = Text(L.T("Sem dados neste período"), 13, LabelBrush);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        // ---- escala vertical
        var unit = data.Unit;
        var lo = 0.0;
        var hi = points.Max(p => p.Max);
        if (unit == "%") hi = 100;
        else if (unit is "°C")
        {
            lo = Math.Max(0, Math.Floor((points.Min(p => p.Min) - 5) / 10) * 10);
            hi = Math.Ceiling((hi + 5) / 10) * 10;
        }
        else hi = Nice(hi * 1.1);
        if (hi <= lo) hi = lo + 1;

        double X(long t) => plot.Left + (t - data.From) / (double)Math.Max(1, data.To - data.From) * plot.Width;
        double Y(double v) => plot.Bottom - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * plot.Height;

        // ---- grade horizontal com rótulos
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 4], 0) };
        gridPen.Freeze();
        for (var i = 0; i <= 4; i++)
        {
            var v = lo + (hi - lo) * i / 4;
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(Format.Number(v, unit), 11, LabelBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }

        // ---- eixo do tempo
        var span = data.To - data.From;
        var timeFormat = span <= 86400 ? "HH:mm" : span <= 8 * 86400 ? "ddd HH:mm" : L.English ? "MM/dd" : "dd/MM";
        for (var i = 0; i <= 4; i++)
        {
            var t = data.From + span * i / 4;
            var label = Text(DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString(timeFormat, Format.Pt), 11, LabelBrush);
            var x = X(t) - (i == 0 ? 0 : i == 4 ? label.Width : label.Width / 2);
            dc.DrawText(label, new Point(x, plot.Bottom + 6));
        }

        // ---- faixa mín–máx e linha da média, quebradas onde faltam dados
        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.DeepSkyBlue;
        var band = new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B));
        band.Freeze();
        var linePen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        var gap = data.StepSeconds * 2.5;
        var segment = new List<HistoryPoint>();
        void DrawSegment()
        {
            if (segment.Count == 0) return;
            var half = Math.Max(1.5, data.StepSeconds / (double)Math.Max(1, span) * plot.Width / 2);
            double Cx(HistoryPoint p) => X(p.T + data.StepSeconds / 2);
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                var first = segment[0];
                g.BeginFigure(new Point(segment.Count == 1 ? Cx(first) - half : Cx(first), Y(first.Max)), true, true);
                if (segment.Count == 1) g.LineTo(new Point(Cx(first) + half, Y(first.Max)), true, true);
                foreach (var p in segment.Skip(1)) g.LineTo(new Point(Cx(p), Y(p.Max)), true, true);
                if (segment.Count == 1) g.LineTo(new Point(Cx(first) + half, Y(first.Min)), true, true);
                for (var i = segment.Count - 1; i >= 0; i--) g.LineTo(new Point(Cx(segment[i]) - (segment.Count == 1 ? half : 0), Y(segment[i].Min)), true, true);
            }
            area.Freeze();
            dc.DrawGeometry(band, null, area);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(Cx(segment[0]) - (segment.Count == 1 ? half : 0), Y(segment[0].Avg)), false, false);
                if (segment.Count == 1) g.LineTo(new Point(Cx(segment[0]) + half, Y(segment[0].Avg)), true, true);
                foreach (var p in segment.Skip(1)) g.LineTo(new Point(Cx(p), Y(p.Avg)), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, linePen, line);
            segment.Clear();
        }
        HistoryPoint? previous = null;
        foreach (var p in points)
        {
            if (previous is not null && p.T - previous.T > gap) DrawSegment();
            segment.Add(p);
            previous = p;
        }
        DrawSegment();

        // ---- ponto sob o mouse
        if (_mouse is { } m && plot.Contains(m))
        {
            var hover = points.MinBy(p => Math.Abs(X(p.T + data.StepSeconds / 2) - m.X))!;
            var x = X(hover.T + data.StepSeconds / 2);
            var cursorPen = new Pen(LabelBrush, 1);
            cursorPen.Freeze();
            dc.DrawLine(cursorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawEllipse(Stroke, new Pen(TooltipBackground, 2), new Point(x, Y(hover.Avg)), 4, 4);

            var when = DateTimeOffset.FromUnixTimeSeconds(hover.T).LocalDateTime;
            var title = Text(when.ToString(span <= 86400 ? "dd/MM HH:mm:ss" : "dd/MM/yyyy HH:mm", Format.Pt), 11, LabelBrush);
            var main = Text(Format.Number(hover.Avg, unit), 15, TextBrush, FontWeights.SemiBold);
            var range = Text(L.T("mín {0} · máx {1}", Format.Number(hover.Min, unit), Format.Number(hover.Max, unit)), 11, LabelBrush);
            var bw = Math.Max(title.Width, Math.Max(main.Width, range.Width)) + 20;
            var bh = title.Height + main.Height + range.Height + 16;
            var bx = x + 12 + bw > plot.Right ? x - 12 - bw : x + 12;
            var box = new Rect(bx, plot.Top + 4, bw, bh);
            dc.DrawRoundedRectangle(TooltipBackground, new Pen(GridBrush, 1), box, 8, 8);
            dc.DrawText(title, new Point(box.Left + 10, box.Top + 7));
            dc.DrawText(main, new Point(box.Left + 10, box.Top + 7 + title.Height));
            dc.DrawText(range, new Point(box.Left + 10, box.Top + 7 + title.Height + main.Height));
        }
    }

    /// <summary>Arredonda para cima num valor "redondo" (1, 2, 2,5, 5, 10 × 10^n).</summary>
    private static double Nice(double v)
    {
        if (v <= 0) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var f = v / exp;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp;
    }

    private FontFamily TextElementFontFamily => (FontFamily)GetValue(System.Windows.Documents.TextElement.FontFamilyProperty);
}
