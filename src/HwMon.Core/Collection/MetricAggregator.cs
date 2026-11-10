using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>Resume as leituras de uma captura em mínimo/média/máximo por métrica e fonte.</summary>
public sealed class MetricAggregator
{
    private readonly Dictionary<string, MetricSummary> _summaries = new();
    private readonly Dictionary<string, double> _sums = new();

    public void AddRange(IEnumerable<Reading> readings)
    {
        foreach (var r in readings) Add(r);
    }

    public void Add(Reading r)
    {
        if (!_summaries.TryGetValue(r.Key, out var s))
        {
            s = new MetricSummary { Component = r.Component, Instance = r.Instance, Metric = r.Metric, Source = r.Source, Unit = r.Unit };
            _summaries[r.Key] = s;
        }
        s.Samples++;
        switch (r.State)
        {
            case ReadingState.Ok when r.Value is { } v:
                s.Ok++;
                s.Min = s.Min is null ? v : Math.Min(s.Min.Value, v);
                s.Max = s.Max is null ? v : Math.Max(s.Max.Value, v);
                s.Last = v;
                _sums[r.Key] = _sums.GetValueOrDefault(r.Key) + v;
                s.Avg = _sums[r.Key] / s.Ok;
                if (r.Note is not null) s.Note = r.Note;
                break;
            case ReadingState.Error:
                s.Errors++;
                s.Note = r.Note;
                break;
            default:
                s.Unavailable++;
                s.Note ??= r.Note;
                break;
        }
    }

    private static readonly string[] ComponentOrder = ["cpu", "ram", "gpu", "disk", "vol", "net", "fan"];

    public List<MetricSummary> Build()
    {
        foreach (var s in _summaries.Values)
        {
            s.Min = Round(s.Min);
            s.Max = Round(s.Max);
            s.Avg = Round(s.Avg);
            s.Last = Round(s.Last);
        }
        return _summaries.Values
            .OrderBy(s => Array.IndexOf(ComponentOrder, s.Component) is var i and >= 0 ? i : 99)
            .ThenBy(s => s.Instance, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Metric)
            .ThenBy(s => s.Source)
            .ToList();
    }

    private static double? Round(double? v) => v is { } d ? Math.Round(d, 3) : null;
}
