using System.Windows;
using System.Windows.Controls;

namespace HwMon.App.Controls;

/// <summary>Colunas de largura igual; cada item vai para a coluna mais curta (cartões de alturas muito diferentes).</summary>
public sealed class Masonry : Panel
{
    public double MinColumnWidth { get; set; } = 380;
    public double Gap { get; set; } = 12;
    public int MaxColumns { get; set; } = 3;

    private readonly List<Rect> _slots = new();

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * 2 + Gap : available.Width;
        var columns = Math.Clamp((int)Math.Floor((width + Gap) / (MinColumnWidth + Gap)), 1, MaxColumns);
        var columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        _slots.Clear();
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed)
            {
                _slots.Add(Rect.Empty);
                continue;
            }
            var col = Array.IndexOf(heights, heights.Min());
            _slots.Add(new Rect(col * (columnWidth + Gap), heights[col], columnWidth, child.DesiredSize.Height));
            heights[col] += child.DesiredSize.Height + Gap;
        }
        return new Size(width, Math.Max(0, heights.Max() - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count && i < _slots.Count; i++)
            InternalChildren[i].Arrange(_slots[i].IsEmpty ? new Rect() : _slots[i]);
        return finalSize;
    }
}
