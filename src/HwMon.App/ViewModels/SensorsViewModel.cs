using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>Uma linha da aba Sensores: valor atual e extremos desde que o painel abriu (ou desde "Zerar").</summary>
public sealed class SensorRow(string metric, string label, string unit) : ObservableObject
{
    private string _current = "—", _min = "—", _max = "—";
    private bool _isStale;
    private double? _minValue, _maxValue;

    public string Metric { get; } = metric;
    public string Label { get; } = label;
    public string Unit { get; private set; } = unit;
    public string Current { get => _current; private set => Set(ref _current, value); }
    public string Min { get => _min; private set => Set(ref _min, value); }
    public string Max { get => _max; private set => Set(ref _max, value); }
    public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }

    public void Update(TelemetryValue? v, bool machineConnected)
    {
        IsStale = v is null || v.State != ReadingState.Ok || !machineConnected;
        if (v is null) return;
        Unit = v.Unit;
        if (v.Metric == "health")
        {
            Current = v.Note is null ? "—" : L.T(v.Note);
            return;
        }
        Current = Format.Value(v);
        if (v.Value is not { } x || v.State != ReadingState.Ok) return;
        if (_minValue is null || x < _minValue) { _minValue = x; Min = Format.Number(x, v.Unit); }
        if (_maxValue is null || x > _maxValue) { _maxValue = x; Max = Format.Number(x, v.Unit); }
    }

    public void Reset()
    {
        _minValue = _maxValue = null;
        Min = Max = "—";
    }
}

public sealed class SensorComponent(string id, string title, string? detail, string icon)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string Icon { get; } = icon;
    public ObservableCollection<SensorRow> Rows { get; } = new();
}

public sealed class SensorMachine(MachineViewModel machine)
{
    public MachineViewModel Machine { get; } = machine;
    public ObservableCollection<SensorComponent> Components { get; } = new();
    public int DescriptorVersion { get; set; } = -1;
}

/// <summary>
/// Aba "Sensores" (estilo HWMonitor): uma árvore com todas as máquinas → componentes → sensores, ao vivo, com
/// mínimo e máximo desde que o painel abriu. Exporta em texto ou imagem.
/// </summary>
public sealed class SensorsViewModel : ObservableObject
{
    private readonly ObservableCollection<MachineViewModel> _machines;
    private DateTime _since = DateTime.Now;

    public SensorsViewModel(ObservableCollection<MachineViewModel> machines)
    {
        _machines = machines;
        foreach (var m in machines) Attach(m);
        machines.CollectionChanged += (_, e) =>
        {
            foreach (MachineViewModel m in e.NewItems ?? Array.Empty<MachineViewModel>()) Attach(m);
            foreach (MachineViewModel m in e.OldItems ?? Array.Empty<MachineViewModel>())
                if (Machines.FirstOrDefault(x => x.Machine == m) is { } sm) Machines.Remove(sm);
        };
        ResetCommand = new RelayCommand(Reset);
    }

    public ObservableCollection<SensorMachine> Machines { get; } = new();
    public RelayCommand ResetCommand { get; }
    public string SinceText => L.T("Mínimo e máximo desde {0} de {1}", _since.ToString("HH:mm:ss", Format.Pt), Format.DayMonth(_since));

    private void Attach(MachineViewModel m)
    {
        var sm = new SensorMachine(m);
        Machines.Add(sm);
        m.Updated += () => Refresh(sm);
        Refresh(sm);
    }

    private static readonly HashSet<string> Hidden = ["charge_eta", "runtime"];

    private void Refresh(SensorMachine sm)
    {
        var m = sm.Machine;
        if (m.Descriptor is not { } d) return;
        if (d.Version != sm.DescriptorVersion)
        {
            sm.DescriptorVersion = d.Version;
            var old = sm.Components.SelectMany(c => c.Rows.Select(r => (c.Id, r))).ToDictionary(x => (x.Id, x.r.Metric), x => x.r);
            sm.Components.Clear();
            foreach (var c in d.Components.OrderBy(c => KindOrder(c.Kind)).ThenBy(c => c.Order))
            {
                var metrics = m.Values.Keys.Where(k => k.Component == c.Id && !Hidden.Contains(k.Metric)).Select(k => k.Metric).OrderBy(MetricOrder).ToList();
                if (metrics.Count == 0) continue;
                var comp = new SensorComponent(c.Id, c.Kind is ComponentKind.Cpu or ComponentKind.Gpu ? MachineViewModel.CleanName(c.Name) : L.T(c.Name), c.Detail is null ? null : L.T(c.Detail), KindIcon(c));
                foreach (var metric in metrics)
                    comp.Rows.Add(old.TryGetValue((c.Id, metric), out var keep) ? keep : new SensorRow(metric, Format.MetricLabel(metric), ""));
                sm.Components.Add(comp);
            }
        }
        foreach (var comp in sm.Components)
            foreach (var row in comp.Rows)
                row.Update(m.Value(comp.Id, row.Metric), m.IsConnected);
    }

    private static int KindOrder(string kind) => kind switch
    {
        ComponentKind.Cpu => 0, ComponentKind.Ram => 1, ComponentKind.Gpu => 2, ComponentKind.Disk => 3, ComponentKind.Volume => 4,
        ComponentKind.Network => 5, ComponentKind.Fan => 6, ComponentKind.Peripheral => 7, _ => 9,
    };

    private static int MetricOrder(string metric) => metric switch
    {
        "usage" or "load" or "active" or "battery" or "rx" or "rpm" => 0, "tx" or "duty" => 1, "read" => 2, "write" => 3,
        "temp" => 4, "temp_hotspot" => 5, "clock" => 6, "clock_max" => 7, "power" => 8, "fan" => 9, "fan_duty" => 10,
        "used" or "vram_used" => 11, "available" or "free" => 12, "total" or "vram_total" => 13, "rx_today" => 14, "tx_today" => 15,
        "health" => 16, "wear" => 17, "power_on_hours" => 18, _ => 30,
    };

    public static string KindIcon(ComponentDescriptor c) => c.Kind switch
    {
        ComponentKind.Cpu => "kind-cpu",
        ComponentKind.Ram => "kind-ram",
        ComponentKind.Gpu => MachineViewModel.IsIntegrated(c) ? "kind-gpu-integrated" : "kind-gpu",
        ComponentKind.Disk => c.Detail?.StartsWith("HDD", StringComparison.Ordinal) == true ? "kind-hdd" : "kind-ssd",
        ComponentKind.Volume => "kind-volume",
        ComponentKind.Network => "kind-network",
        ComponentKind.Fan => "kind-fan",
        ComponentKind.Peripheral => MachineViewModel.PeripheralIcon(c),
        _ => "nav-overview",
    };

    private void Reset()
    {
        _since = DateTime.Now;
        foreach (var row in Machines.SelectMany(m => m.Components).SelectMany(c => c.Rows)) row.Reset();
        Raise(nameof(SinceText));
    }

    /// <summary>Texto alinhado em colunas (para colar em fórum, anexar em chamado).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Corewise · " + L.T("sensores em {0}", Format.When(DateTimeOffset.Now)) + " · " + SinceText.ToLowerInvariant());
        foreach (var sm in Machines)
        {
            sb.AppendLine();
            sb.AppendLine($"=== {sm.Machine.Name} ({sm.Machine.StatusText}) — {sm.Machine.Description}");
            foreach (var c in sm.Components)
            {
                sb.AppendLine($"  {c.Title}{(c.Detail is null ? "" : $"  ({c.Detail})")}");
                foreach (var r in c.Rows)
                    sb.AppendLine($"    {r.Label,-28} {r.Current,16} {r.Min,16} {r.Max,16}");
            }
        }
        return sb.ToString();
    }

    public void ExportText(string path) => File.WriteAllText(path, ToText(), new UTF8Encoding(true));
}
