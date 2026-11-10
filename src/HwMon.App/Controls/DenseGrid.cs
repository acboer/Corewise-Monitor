using System.Windows;
using System.Windows.Controls;

namespace HwMon.App.Controls;

/// <summary>
/// Grade única da página da máquina (como "grid-auto-flow: dense"): colunas de largura mínima fixa que ocupam a
/// janela inteira; cartões largos ocupam duas colunas; cartões de prioridade maior vêm primeiro; um cartão estreito
/// que vem depois preenche o vão deixado por um largo que não coube no fim da linha. A linha tem a altura do
/// cartão mais alto (no mínimo <see cref="RowMinHeight"/>) e todos se esticam até ela.
/// </summary>
public sealed class DenseGrid : Panel
{
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
        "Span", typeof(int), typeof(DenseGrid), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(int), typeof(DenseGrid), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static int GetSpan(DependencyObject d) => (int)d.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject d, int value) => d.SetValue(SpanProperty, value);
    public static int GetPriority(DependencyObject d) => (int)d.GetValue(PriorityProperty);
    public static void SetPriority(DependencyObject d, int value) => d.SetValue(PriorityProperty, value);

    public double MinColumnWidth { get; set; } = 232;
    public double Gap { get; set; } = 12;
    public double RowMinHeight { get; set; } = 0;

    private readonly List<(UIElement Child, int Row, int Col, int Span)> _placed = new();
    private readonly List<double> _rowHeights = new();
    private double _columnWidth;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * 4 + Gap * 3 : available.Width;
        var columns = Math.Max(1, (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap)));
        _columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);

        // Ordem estável por prioridade (maior primeiro), depois a ordem original.
        var children = InternalChildren.Cast<UIElement>()
            .Select((c, i) => (Child: c, Index: i))
            .Where(x => x.Child.Visibility != Visibility.Collapsed)
            .OrderByDescending(x => GetPriority(x.Child)).ThenBy(x => x.Index)
            .Select(x => x.Child).ToList();
        foreach (UIElement c in InternalChildren)
            if (c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));

        // Posicionamento denso: cada cartão vai para a primeira célula livre onde ele cabe.
        _placed.Clear();
        var occupied = new List<bool[]>();
        foreach (var child in children)
        {
            var span = Math.Clamp(GetSpan(child), 1, columns);
            for (var row = 0; ; row++)
            {
                while (occupied.Count <= row) occupied.Add(new bool[columns]);
                var cells = occupied[row];
                var col = -1;
                for (var c = 0; c + span <= columns && col < 0; c++)
                    if (Enumerable.Range(c, span).All(k => !cells[k])) col = c;
                if (col < 0) continue;
                for (var k = col; k < col + span; k++) cells[k] = true;
                _placed.Add((child, row, col, span));
                break;
            }
        }

        _rowHeights.Clear();
        _rowHeights.AddRange(Enumerable.Repeat(RowMinHeight, occupied.Count));
        foreach (var (child, row, _, span) in _placed)
        {
            child.Measure(new Size(span * _columnWidth + (span - 1) * Gap, double.PositiveInfinity));
            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
        }
        var height = _rowHeights.Sum() + Gap * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var tops = new double[_rowHeights.Count];
        for (var r = 1; r < tops.Length; r++) tops[r] = tops[r - 1] + _rowHeights[r - 1] + Gap;
        foreach (var (child, row, col, span) in _placed)
            child.Arrange(new Rect(col * (_columnWidth + Gap), tops[row], span * _columnWidth + (span - 1) * Gap, _rowHeights[row]));
        foreach (UIElement c in InternalChildren)
            if (c.Visibility == Visibility.Collapsed) c.Arrange(new Rect());
        return finalSize;
    }
}
