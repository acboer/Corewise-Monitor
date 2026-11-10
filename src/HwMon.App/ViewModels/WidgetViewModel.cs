using System.Collections.ObjectModel;
using HwMon.App.Infrastructure;
using HwMon.App.Services;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

public sealed class WidgetRow(string componentId, string metric, string label) : ObservableObject
{
    private string _value = "—";
    private double? _fraction;
    private bool _isStale;
    private Severity _severity;

    public string ComponentId { get; } = componentId;
    public string Metric { get; } = metric;
    public string Label { get; } = label;
    public string Value { get => _value; set => Set(ref _value, value); }
    public double? Fraction { get => _fraction; set => Set(ref _fraction, value); }
    public bool IsStale { get => _isStale; set => Set(ref _isStale, value); }
    public Severity Severity { get => _severity; set => Set(ref _severity, value); }

    private string? _detail;
    /// <summary>Linha pequena embaixo (ex.: "carregando · cheia em ~40 min").</summary>
    public string? Detail { get => _detail; set => Set(ref _detail, value); }
}

/// <summary>Bloco do widget: uma categoria (CPU, Memória, GPU, Armazenamento, Rede, Periféricos) com as suas linhas.</summary>
public sealed class WidgetGroup(string key, string title, string icon)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public ObservableCollection<WidgetRow> Rows { get; } = new();
}

/// <summary>Uma métrica que pode ir para o widget (lista do "Escolher métricas").</summary>
public sealed class MetricOption(string componentId, string metric, string group, string label, bool selected) : ObservableObject
{
    private bool _selected = selected;
    public string ComponentId { get; } = componentId;
    public string Metric { get; } = metric;
    public string Group { get; } = group;
    public string Label { get; } = label;
    public string Key => $"{ComponentId}|{Metric}";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>
/// Widget de área de trabalho. Um só, mostrando uma máquina por vez (setas trocam entre as escolhidas), ou um para
/// cada máquina (<see cref="Fixed"/>). Estilo, transparência e "sempre no topo" valem para todos; as métricas são
/// escolhidas por máquina e independentes do painel.
/// </summary>
public sealed class WidgetViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly ObservableCollection<MachineViewModel> _machines;
    private MachineViewModel? _current;
    private int _descriptorVersion = -1;

    /// <summary>Uma preferência comum a todos os widgets mudou: cada um se atualiza.</summary>
    private static event Action<string>? SharedChanged;

    public WidgetViewModel(ObservableCollection<MachineViewModel> machines, SettingsStore settings, MachineViewModel? fixedMachine = null)
    {
        _machines = machines;
        _settings = settings;
        Fixed = fixedMachine;
        Current = fixedMachine ?? Shown.FirstOrDefault(m => m.MachineId == settings.Current.Widget.MachineId) ?? Shown.FirstOrDefault();
        if (fixedMachine is null)
            machines.CollectionChanged += (_, _) => RefreshShown();
        PreviousCommand = new RelayCommand(() => Move(-1));
        NextCommand = new RelayCommand(() => Move(+1));
        TogglePinCommand = new RelayCommand(() => Topmost = !Topmost);
        SharedChanged += OnSharedChanged;
    }

    /// <summary>Widget de uma máquina só (modo "um widget por PC").</summary>
    public MachineViewModel? Fixed { get; }

    /// <summary>Máquinas escolhidas para o widget (todas, se nenhuma escolha foi feita).</summary>
    public List<MachineViewModel> Shown => _settings.Current.Widget.Machines is { } ids
        ? _machines.Where(m => ids.Contains(m.MachineId)).ToList()
        : _machines.ToList();

    /// <summary>A escolha de máquinas mudou (ou entrou/saiu uma): o widget único continua numa das escolhidas.</summary>
    public void RefreshShown()
    {
        if (Fixed is not null) return;
        var shown = Shown;
        if (_current is null || !shown.Contains(_current)) Current = shown.FirstOrDefault();
        Raise(nameof(CanSwitch));
    }

    private void OnSharedChanged(string property)
    {
        Raise(property);
        if (property == nameof(IsVisual)) Raise(nameof(IsCompact));
        if (property == nameof(BackgroundOpacity)) Raise(nameof(TransparencyPercent));
        if (property == nameof(ShowChargeTime)) OnUpdated();
    }

    /// <summary>Janela fechada: para de ouvir a máquina e as preferências.</summary>
    public void Detach()
    {
        SharedChanged -= OnSharedChanged;
        if (_current is not null) _current.Updated -= OnUpdated;
    }

    /// <summary>Todas as linhas (para atualizar os valores); a tela mostra <see cref="Groups"/>.</summary>
    public List<WidgetRow> Rows { get; } = new();
    public ObservableCollection<WidgetGroup> Groups { get; } = new();

    // ------------------------------------------------------------------ aparência

    /// <summary>"compact" (linhas, como antes) ou "visual" (ícone por categoria, valores maiores).</summary>
    public bool IsVisual
    {
        get => _settings.Current.Widget.Style == "visual";
        set
        {
            _settings.Current.Widget.Style = value ? "visual" : "compact";
            _settings.SaveSoon();
            SharedChanged?.Invoke(nameof(IsVisual));
        }
    }

    public bool IsCompact { get => !IsVisual; set => IsVisual = !value; }

    /// <summary>Mostra "cheia em ~40 min" embaixo da bateria (só enquanto carrega).</summary>
    public bool ShowChargeTime
    {
        get => _settings.Current.Widget.ShowChargeTime;
        set
        {
            _settings.Current.Widget.ShowChargeTime = value;
            _settings.SaveSoon();
            SharedChanged?.Invoke(nameof(ShowChargeTime));
        }
    }

    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand TogglePinCommand { get; }
    public bool CanSwitch => Fixed is null && Shown.Count > 1;

    public MachineViewModel? Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            if (_current is not null) _current.Updated -= OnUpdated;
            _current = value;
            _descriptorVersion = -1;
            if (_current is not null)
            {
                _current.Updated += OnUpdated;
                if (Fixed is null)
                {
                    _settings.Current.Widget.MachineId = _current.MachineId;
                    _settings.SaveSoon();
                }
            }
            Raise();
            OnUpdated();
        }
    }

    public bool Topmost
    {
        get => _settings.Current.Widget.Topmost;
        set
        {
            _settings.Current.Widget.Topmost = value;
            _settings.SaveSoon();
            SharedChanged?.Invoke(nameof(Topmost));
        }
    }

    /// <summary>Opacidade do fundo do widget (0,15–1). O texto continua sólido para ficar legível.</summary>
    public double BackgroundOpacity
    {
        get => Math.Clamp(_settings.Current.Widget.Opacity, 0.15, 1);
        set
        {
            _settings.Current.Widget.Opacity = Math.Clamp(Math.Round(value, 2), 0.15, 1);
            _settings.SaveSoon();
            SharedChanged?.Invoke(nameof(BackgroundOpacity));
        }
    }

    /// <summary>Transparência em % (0 = fundo sólido), para o controle deslizante.</summary>
    public double TransparencyPercent
    {
        get => Math.Round((1 - BackgroundOpacity) * 100);
        set => BackgroundOpacity = 1 - value / 100;
    }

    public RelayCommand SetTransparencyCommand => new(p =>
    {
        if (p is string s && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct)) TransparencyPercent = pct;
    });

    private void Move(int delta)
    {
        var shown = Shown;
        if (Fixed is not null || shown.Count == 0) return;
        var i = _current is null ? 0 : Math.Max(0, shown.IndexOf(_current));
        Current = shown[((i + delta) % shown.Count + shown.Count) % shown.Count];
    }

    private void OnUpdated()
    {
        var m = _current;
        if (m?.Descriptor is null)
        {
            Rows.Clear();
            Groups.Clear();
            return;
        }
        if (m.Descriptor.Version != _descriptorVersion)
        {
            _descriptorVersion = m.Descriptor.Version;
            BuildGroups(m);
        }
        foreach (var row in Rows)
        {
            if (row.Metric == "battery")
            {
                // "cheia em ~40 min" só enquanto carrega (e se a opção estiver ligada)
                var eta = m.Value(row.ComponentId, "charge_eta");
                var charging = m.Value(row.ComponentId, "battery")?.Note?.StartsWith("carregando", StringComparison.Ordinal) == true;
                row.Detail = !ShowChargeTime || !charging ? null
                    : eta?.Value is { } minutes && eta.State == HwMon.Core.Model.ReadingState.Ok ? L.T("carregando · cheia em {0}", Format.Duration(minutes))
                    : L.T("carregando · calculando o tempo");
            }
            var v = m.Value(row.ComponentId, row.Metric);
            row.Value = m.Text(row.ComponentId, row.Metric);
            row.IsStale = row.Metric == "used_pct" ? m.IsStale(m.Value(row.ComponentId, "used")) : m.IsStale(v);
            row.Fraction = row.Metric switch
            {
                "used_pct" => m.UsedFraction(row.ComponentId),
                "usage" or "load" or "active" or "temp" or "wear" or "battery" => v?.Value / 100,
                _ => null,
            };
            row.Severity = row.Metric switch
            {
                "temp" when v?.Value >= 85 => Severity.Critical,
                "temp" when v?.Value >= 75 => Severity.Caution,
                "used_pct" when row.Fraction >= 0.9 => Severity.Critical,
                "used_pct" when row.Fraction >= 0.8 => Severity.Caution,
                "battery" => MachineViewModel.BatterySeverity(v),
                "health" when v?.Note is not null && !L.Is(v.Note, "Saudável") => Severity.Critical,
                _ => Severity.Normal,
            };
        }
    }

    /// <summary>Ordem e aparência dos blocos do widget.</summary>
    private static (int Order, string Key, string Title, string Icon) Category(string kind) => kind switch
    {
        ComponentKind.Cpu => (0, "cpu", "CPU", "kind-cpu"),
        ComponentKind.Ram => (1, "ram", L.T("Memória"), "kind-ram"),
        ComponentKind.Gpu => (2, "gpu", "GPU", "kind-gpu"),
        ComponentKind.Disk or ComponentKind.Volume => (3, "storage", L.T("Armazenamento"), "kind-storage"),
        ComponentKind.Network => (4, "net", L.T("Rede"), "kind-network"),
        ComponentKind.Peripheral => (5, "periph", L.T("Periféricos"), "metric-battery"),
        ComponentKind.Fan => (6, "fan", L.T("Ventoinhas"), "kind-fan"),
        _ => (9, "other", L.T("Outros"), "nav-overview"),
    };

    /// <summary>Linhas escolhidas, agrupadas por categoria na ordem fixa; dentro do bloco, a ordem escolhida.</summary>
    private void BuildGroups(MachineViewModel m)
    {
        Rows.Clear();
        Groups.Clear();
        var picked = SelectedKeys(m)
            .Select(k => k.Split('|'))
            .Where(p => p.Length == 2 && m.Component(p[0]) is not null)
            .Select((p, i) => (Component: m.Component(p[0])!, Metric: p[1], Index: i))
            .ToList();
        foreach (var cat in picked.GroupBy(x => Category(x.Component.Kind)).OrderBy(g => g.Key.Order))
        {
            var group = new WidgetGroup(cat.Key.Key, cat.Key.Title, cat.Key.Icon);
            // Mais de um componente no bloco (vários discos, periféricos): o nome entra no rótulo.
            var several = cat.Select(x => x.Component.Id).Distinct().Count() > 1;
            foreach (var x in cat.OrderBy(x => x.Index))
            {
                var row = new WidgetRow(x.Component.Id, x.Metric, GroupedLabel(m, x.Component, x.Metric, several));
                group.Rows.Add(row);
                Rows.Add(row);
            }
            Groups.Add(group);
        }
    }

    private static string GroupedLabel(MachineViewModel m, ComponentDescriptor c, string metric, bool several)
    {
        var what = metric switch
        {
            "load" when c.Id == "ram:0" => L.T("Uso"),
            "used" when c.Id == "ram:0" => L.T("Usada"),
            "rx" => "↓ " + L.T("Recebendo"),
            "tx" => "↑ " + L.T("Enviando"),
            "used_pct" => L.T("Ocupado"),
            _ => Format.MetricLabel(metric),
        };
        if (!several) return c.Kind == ComponentKind.Volume ? $"{Short(c.Name)}" : what;
        var name = c.Kind == ComponentKind.Peripheral
            ? System.Text.RegularExpressions.Regex.Replace(c.Name, @"\b(Logitech|Gaming|Headset|Wireless|Mouse|Keyboard|Touch)\b", "").Trim()
            : c.Kind == ComponentKind.Ram && c.Id != "ram:0" ? c.Name.Replace("Módulo ", "", StringComparison.Ordinal).Replace("Module ", "", StringComparison.Ordinal) : L.T(c.Name);
        if (string.IsNullOrWhiteSpace(name)) name = c.Name;
        return metric is "battery" or "used_pct" ? Short(name) : $"{Short(name)} · {what.ToLowerInvariant()}";
    }

    // ------------------------------------------------------------------ escolha de métricas

    public List<string> SelectedKeys(MachineViewModel m)
        => _settings.Current.Widget.Metrics.TryGetValue(m.MachineId, out var list) ? list : DefaultKeys(m);

    public void SaveKeys(MachineViewModel m, IEnumerable<string> keys)
    {
        _settings.Current.Widget.Metrics[m.MachineId] = keys.ToList();
        _settings.SaveSoon();
        _descriptorVersion = -1;
        OnUpdated();
    }

    public void ResetKeys(MachineViewModel m)
    {
        _settings.Current.Widget.Metrics.Remove(m.MachineId);
        _settings.SaveSoon();
        _descriptorVersion = -1;
        OnUpdated();
    }

    /// <summary>Padrão: CPU, RAM, GPU dedicada e rede; num NAS, espaço dos volumes e saúde/temperatura dos discos.</summary>
    private static List<string> DefaultKeys(MachineViewModel m)
    {
        var comps = m.Descriptor!.Components;
        var keys = new List<string>();
        void Add(string? id, params string[] metrics)
        {
            if (id is null) return;
            foreach (var metric in metrics) keys.Add($"{id}|{metric}");
        }
        var cpu = comps.FirstOrDefault(c => c.Kind == ComponentKind.Cpu)?.Id;
        var main = MachineViewModel.MainGpu(comps);
        var gpu = main is not null && !MachineViewModel.IsIntegrated(main) ? main.Id : null;
        var net = comps.FirstOrDefault(c => c.Kind == ComponentKind.Network && c.Detail?.Contains("virtual", StringComparison.OrdinalIgnoreCase) != true)?.Id;
        Add(cpu, "usage", "temp");
        Add("ram:0", "load");
        Add(gpu, "usage", "temp");
        Add(net, "rx", "tx");
        if (m.IsNas)
        {
            foreach (var v in comps.Where(c => c.Kind == ComponentKind.Volume && !L.Is(c.Name, "Sem letra"))) Add(v.Id, "used_pct");
            foreach (var d in comps.Where(c => c.Kind == ComponentKind.Disk)) Add(d.Id, "temp");
        }
        foreach (var p in comps.Where(c => c.Kind == ComponentKind.Peripheral)) Add(p.Id, "battery");
        return keys;
    }

    // Tempos estimados de bateria aparecem embaixo da própria bateria, não como linha.
    private static readonly HashSet<string> Excluded = ["rx_total", "tx_total", "total", "clock_max", "charge_eta", "runtime"];

    public List<MetricOption> Options(MachineViewModel m)
    {
        var selected = SelectedKeys(m).ToHashSet();
        var options = new List<MetricOption>();
        foreach (var c in m.Descriptor?.Components.OrderBy(c => c.Order) ?? Enumerable.Empty<ComponentDescriptor>())
        {
            var metrics = m.Values.Keys.Where(k => k.Component == c.Id).Select(k => k.Metric)
                .Where(x => !Excluded.Contains(x)).Distinct().OrderBy(x => x).ToList();
            if (c.Kind == ComponentKind.Volume) metrics.Insert(0, "used_pct");
            foreach (var metric in metrics)
                options.Add(new MetricOption(c.Id, metric, c.Name, Format.MetricLabel(metric), selected.Contains($"{c.Id}|{metric}")));
        }
        return options;
    }

    private static string Short(string name)
    {
        var s = name.Replace("(R)", "").Replace("(TM)", "").Replace("NVIDIA ", "").Replace("GeForce ", "").Trim();
        return s.Length <= 16 ? s : s[..15] + "…";
    }
}
