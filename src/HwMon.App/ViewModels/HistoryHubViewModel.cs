using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using HwMon.App.Controls;
using HwMon.App.Infrastructure;
using HwMon.App.Views;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>Métrica das barras do uso (por dia ou por mês).</summary>
public sealed class BarChoice(string group, string key, string label, string unit) : ObservableObject
{
    private bool _isSelected;
    /// <summary>Grupo das etiquetas (uma escolha por máquina).</summary>
    public string Group { get; } = group;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Unit { get; } = unit;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Um jogo numa máquina, com os totais do período.</summary>
public sealed class GameRow(HistoryHubViewModel hub, MachineViewModel machine, GameSummary summary) : ObservableObject
{
    private bool _isSelected;
    public HistoryHubViewModel Hub { get; } = hub;
    public MachineViewModel Machine { get; } = machine;
    public GameSummary Summary { get; } = summary;
    public string Name => Summary.Name;
    public string Caption => Summary.LastPlayed > 0
        ? Machine.Name + " · " + L.T("última vez {0}", HistoryHubViewModel.When(Summary.LastPlayed))
        : Machine.Name + (Summary.Path is { } p ? " · " + p : "");
    public string Time => HistoryHubViewModel.Hours(Summary.Seconds);
    public string Sessions => Summary.Sessions == 1 ? L.T("1 sessão") : L.T("{0} sessões", Summary.Sessions);
    public string Fps => Summary.FpsAvg.ToString("N0", Format.Pt);
    public string Low => Summary.Low1Fps.ToString("N0", Format.Pt);
    public string Energy => Summary.EnergyWh is { } e ? Format.Number(e, "Wh") : "—";
    /// <summary>Filtra a lista de sessões por este jogo.</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Uma sessão na lista.</summary>
public sealed class SessionRow(MachineViewModel machine, GameSession session)
{
    public MachineViewModel Machine { get; } = machine;
    public GameSession Session { get; } = session;
    public string When => HistoryHubViewModel.When(Session.Start);
    public string Game => Session.Name;
    public string MachineName => Machine.Name;
    public string Duration => HistoryHubViewModel.Hours(Session.ActiveSeconds);
    public string Fps => Session.FpsAvg.ToString("N0", Format.Pt);
    public string Low1 => Session.Low1Fps.ToString("N0", Format.Pt);
    public string Low01 => Session.Low01Fps.ToString("N0", Format.Pt);
    public string GpuTemp => Session.Metrics.FirstOrDefault(m => m.Key == "gpu_temp") is { } t ? Format.Number(t.Max, "°C") : "—";
    public string Energy => Session.EnergyWh is { } e ? Format.Number(e, "Wh") : "—";
    public bool IsOpen => Session.Open;
    /// <summary>Faixa "Jogando agora": "no NAS · 38 min · 144 FPS médio".</summary>
    public string NowPlaying => L.T("no {0} · {1} · {2} FPS médio", MachineName, Duration, Fps);
    /// <summary>Texto do item no "Comparar com".</summary>
    public string CompareLabel => $"{When} · {Duration} · {Fps} FPS";
    public override string ToString() => CompareLabel;
}

/// <summary>Detalhe de uma sessão: números, gráfico ao longo da partida, sensores e comparação com outra sessão.</summary>
public sealed class SessionDetail : ObservableObject
{
    private HistoryResponse? _data;
    private SessionRow? _compare;
    private MetricChoice _metric;
    private bool _isLoading = true;
    private string? _error;

    public SessionDetail(SessionRow row, IEnumerable<SessionRow> sameGame)
    {
        Row = row;
        Session = row.Session;
        CompareOptions = sameGame.Where(s => s.Session.Id != row.Session.Id).ToList();
        foreach (var (key, label) in ChartMetrics) Metrics.Add(new MetricChoice("", key, label));
        Metrics[0].IsSelected = true;
        _metric = Metrics[0];
        foreach (var m in Metrics) m.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(MetricChoice.IsSelected) && ((MetricChoice)s!).IsSelected) { _metric = (MetricChoice)s!; BuildChart(); } };
        Fill();
    }

    private static readonly (string Key, string Label)[] ChartMetrics =
    [
        ("fps", "FPS"), ("gpu_usage", L.T("GPU uso")), ("gpu_temp", L.T("GPU temperatura")), ("gpu_power", L.T("GPU consumo")),
        ("cpu_usage", L.T("CPU uso")), ("cpu_temp", L.T("CPU temperatura")),
    ];

    private static readonly Dictionary<string, string> SensorLabels = new()
    {
        ["gpu_usage"] = L.T("GPU uso"), ["gpu_temp"] = L.T("GPU temperatura"), ["gpu_power"] = L.T("GPU consumo"), ["gpu_clock"] = L.T("GPU clock"),
        ["vram"] = "VRAM", ["cpu_usage"] = L.T("CPU uso"), ["cpu_temp"] = L.T("CPU temperatura"), ["cpu_power"] = L.T("CPU consumo"), ["ram"] = "RAM",
    };

    public SessionRow Row { get; }
    public GameSession Session { get; private set; }
    public ObservableCollection<StatItem> Stats { get; } = new();
    public ObservableCollection<StatItem> Sensors { get; } = new();
    public ObservableCollection<MetricChoice> Metrics { get; } = new();
    public ObservableCollection<StatItem> CompareStats { get; } = new();
    public List<SessionRow> CompareOptions { get; }
    public bool CanCompare => CompareOptions.Count > 0;

    public string Title => Session.Name;
    public string Subtitle
    {
        get
        {
            var start = HistoryHubViewModel.Local(Session.Start);
            var end = HistoryHubViewModel.Local(Session.End);
            var hw = string.Join(" · ", new[] { Session.Gpu, Session.Cpu }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return $"{start.ToString(L.English ? "dddd, MM/dd/yyyy" : "dddd, dd/MM/yyyy", Format.Pt)}, {start:HH:mm}–{end:HH:mm}{(Session.Open ? " " + L.T("(jogando agora)") : "")} · {Row.MachineName}" + (hw.Length > 0 ? " · " + hw : "");
        }
    }

    public HistoryResponse? Data { get => _data; private set { if (Set(ref _data, value)) Raise(nameof(Points)); } }
    public IReadOnlyList<HistoryPoint> Points => _data?.Points ?? (IReadOnlyList<HistoryPoint>)Array.Empty<HistoryPoint>();
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public SessionRow? Compare
    {
        get => _compare;
        set
        {
            if (!Set(ref _compare, value)) return;
            _ = LoadCompareAsync();
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            using var client = CollectorClient.For(Row.Machine.Machine);
            Session = await client.GameSessionAsync(Row.Session.Id);
            Error = null;
            Fill();
            Raise(nameof(Title));
            Raise(nameof(Subtitle));
        }
        catch (Exception ex)
        {
            Error = L.T("Não foi possível ler a sessão: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
            BuildChart();
        }
    }

    private void Fill()
    {
        var s = Session;
        Stats.Clear();
        Stats.Add(new StatItem(L.T("FPS médio"), s.FpsAvg.ToString("N0", Format.Pt), L.T("tempo de quadro {0:0.0} ms", s.FrametimeAvgMs)));
        Stats.Add(new StatItem("1 % low", s.Low1Fps.ToString("N0", Format.Pt), L.T("0,1 % low {0:N0}", s.Low01Fps)));
        var perMin = s.ActiveSeconds > 0 ? s.Stutters / (s.ActiveSeconds / 60.0) : 0;
        Stats.Add(new StatItem(L.T("Engasgos"), s.Stutters.ToString("N0", Format.Pt),
            L.T("{0:0.#} por minuto · pior quadro {1:N0} ms", perMin, s.FrametimeMaxMs)));
        Stats.Add(new StatItem(L.T("Tempo jogado"), HistoryHubViewModel.Hours(s.ActiveSeconds),
            s.End - s.Start > s.ActiveSeconds + 120 ? L.T("de {0} com o jogo aberto", HistoryHubViewModel.Hours(s.End - s.Start)) : L.T("{0:N0} quadros", s.Frames)));
        Stats.Add(new StatItem(L.T("Energia"), s.EnergyWh is { } e ? Format.Number(e, "Wh") : "—", L.T("CPU + placa de vídeo")));

        Sensors.Clear();
        foreach (var key in SensorLabels.Keys)
            if (s.Metrics.FirstOrDefault(m => m.Key == key) is { } m)
                Sensors.Add(new StatItem(SensorLabels[key], L.T("{0} em média", Format.Number(m.Avg, m.Unit)), L.T("máximo {0}", Format.Number(m.Max, m.Unit))));
    }

    private void BuildChart()
    {
        var points = Session.Points;
        var metric = _metric;
        if (points is null || points.Count == 0)
        {
            Data = null;
            return;
        }
        var unit = metric.Metric switch { "fps" => "FPS", "gpu_power" => "W", "gpu_temp" or "cpu_temp" => "°C", _ => "%" };
        var list = new List<HistoryPoint>();
        foreach (var p in points)
        {
            double? v = metric.Metric switch
            {
                "fps" => p.Fps, "gpu_usage" => p.GpuUsage, "gpu_temp" => p.GpuTemp, "gpu_power" => p.GpuPower,
                "cpu_usage" => p.CpuUsage, "cpu_temp" => p.CpuTemp, _ => null,
            };
            if (v is not { } x) continue;
            // FPS: a faixa vai do 1 % low até a média (onde a faixa abre, o jogo engasgou).
            list.Add(metric.Metric == "fps"
                ? new HistoryPoint { T = p.T, Avg = x, Min = Math.Min(x, p.Low1 > 0 ? p.Low1 : x), Max = x }
                : new HistoryPoint { T = p.T, Avg = x, Min = x, Max = x });
        }
        var from = points[0].T;
        var to = Math.Max(Session.End, points[^1].T + 5);
        Data = new HistoryResponse { Unit = unit, From = from, To = Math.Max(from + 60, to), StepSeconds = 5, Points = list };
    }

    private async Task LoadCompareAsync()
    {
        CompareStats.Clear();
        if (_compare is not { } other) return;
        GameSession o;
        try
        {
            using var client = CollectorClient.For(other.Machine.Machine);
            o = await client.GameSessionAsync(other.Session.Id);
        }
        catch (Exception)
        {
            o = other.Session;
        }
        if (_compare != other) return;
        void Add(string label, double? mine, double? theirs, string unit, bool higherIsBetter)
        {
            if (mine is not { } a || theirs is not { } b) return;
            var delta = b == 0 ? 0 : (a - b) / b * 100;
            var better = Math.Abs(delta) < 1 ? L.T("igual") : (delta > 0) == higherIsBetter ? L.T("melhor") : L.T("pior");
            CompareStats.Add(new StatItem(label, Format.Number(a, unit),
                L.T("antes {0} · {1} % ({2})", Format.Number(b, unit), (delta >= 0 ? "+" : "") + delta.ToString("0.0", Format.Pt), better)));
        }
        double? M(GameSession s, string key) => s.Metrics.FirstOrDefault(m => m.Key == key)?.Avg;
        Add(L.T("FPS médio"), Session.FpsAvg, o.FpsAvg, "FPS", true);
        Add("1 % low", Session.Low1Fps, o.Low1Fps, "FPS", true);
        Add(L.T("0,1 % low"), Session.Low01Fps, o.Low01Fps, "FPS", true);
        Add(L.T("GPU temperatura média"), M(Session, "gpu_temp"), M(o, "gpu_temp"), "°C", false);
        Add(L.T("GPU consumo médio"), M(Session, "gpu_power"), M(o, "gpu_power"), "W", false);
        Add(L.T("CPU temperatura média"), M(Session, "cpu_temp"), M(o, "cpu_temp"), "°C", false);
    }
}

/// <summary>Uso consolidado de uma máquina no período: totais, picos e barras por dia ou por mês.</summary>
public sealed class UsageMachine : ObservableObject
{
    private IReadOnlyList<ChartBar> _bars = Array.Empty<ChartBar>();
    private string? _error;
    private string _subtitle = "";
    private UsageResponse? _data;
    private bool _monthly;
    private BarChoice _choice;

    public UsageMachine(MachineViewModel machine)
    {
        Machine = machine;
        foreach (var (key, label, unit) in BarMetrics) Choices.Add(new BarChoice("usage-" + machine.MachineId, key, label, unit));
        Choices[0].IsSelected = true;
        _choice = Choices[0];
        foreach (var c in Choices) c.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(BarChoice.IsSelected) && ((BarChoice)s!).IsSelected) { _choice = (BarChoice)s!; BuildBars(); } };
    }

    private static readonly (string Key, string Label, string Unit)[] BarMetrics =
    [
        ("on", L.T("Tempo ligada"), "h"), ("energy", L.T("Energia"), "Wh"), ("games", L.T("Jogando"), "h"), ("cpu_temp", L.T("CPU temp. máx."), "°C"),
        ("gpu_temp", L.T("GPU temp. máx."), "°C"), ("net", L.T("Rede"), "MB"), ("disk", L.T("Disco gravado"), "MB"),
    ];

    public MachineViewModel Machine { get; }
    public string Name => Machine.Name;
    public string Icon => Machine.MachineIcon;
    public ObservableCollection<StatItem> Stats { get; } = new();
    public ObservableCollection<BarChoice> Choices { get; } = new();
    public IReadOnlyList<ChartBar> Bars { get => _bars; private set => Set(ref _bars, value); }
    public string Unit => _choice.Unit;
    public string? Error { get => _error; set => Set(ref _error, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public UsageResponse? Data => _data;
    public bool HasData => _data is { Days.Count: > 0 };

    public void Apply(UsageResponse data, bool monthly, long periodFrom)
    {
        _data = data;
        _monthly = monthly;
        Error = null;
        var days = data.Days;
        Stats.Clear();
        var on = days.Sum(d => d.OnSeconds);
        var activeDays = Math.Max(1, days.Count(d => d.OnSeconds > 0));
        var energy = days.Where(d => d.EnergyWh is not null).Sum(d => d.EnergyWh!.Value);
        var games = days.Sum(d => d.GameSeconds);
        string H(long s) => HistoryHubViewModel.Hours(s);
        Stats.Add(new StatItem(L.T("Tempo ligada"), H(on), L.T(activeDays == 1 ? "média de {0} em {1} dia" : "média de {0} em {1} dias", H(on / activeDays), activeDays)));
        Stats.Add(new StatItem(L.T("Energia (CPU + GPU)"), energy > 0 ? Format.Number(energy, "Wh") : "—",
            on > 0 && energy > 0 ? L.T("média de {0} ligada", Format.Number(energy * 3600 / on, "W")) : L.T("sem sensor de consumo")));
        Stats.Add(new StatItem(L.T("Jogando"), games > 0 ? H(games) : "—", games > 0 && on > 0 ? L.T("{0:P0} do tempo ligada", Math.Min(1, (double)games / on)) : L.T("nenhuma sessão de jogo")));
        Stats.Add(new StatItem(L.T("Carga pesada"), $"CPU {H(days.Sum(d => d.CpuHeavySeconds))}",
            L.T("GPU {0} · acima de 80 %", H(days.Sum(d => d.GpuHeavySeconds)))));
        string Peak(UsagePeak? p) => p is null ? "—" : Format.Number(p.Value, "°C");
        string PeakWhen(UsagePeak? p, IEnumerable<double?> avgs)
        {
            var avg = avgs.OfType<double>().DefaultIfEmpty(double.NaN).Average();
            return (double.IsNaN(avg) ? "" : L.T("média {0}", Format.Number(avg, "°C"))) + (p is null ? "" : " · " + L.T("pico {0}", HistoryHubViewModel.When(p.T)));
        }
        Stats.Add(new StatItem(L.T("CPU mais quente"), Peak(data.CpuTempPeak), PeakWhen(data.CpuTempPeak, days.Select(d => d.CpuTempAvg))));
        Stats.Add(new StatItem(L.T("GPU mais quente"), Peak(data.GpuTempPeak), PeakWhen(data.GpuTempPeak, days.Select(d => d.GpuTempAvg))));
        Stats.Add(new StatItem(L.T("Rede"), "↓ " + Format.Number(days.Sum(d => d.NetRxMB), "MB"), "↑ " + L.T("{0} enviados", Format.Number(days.Sum(d => d.NetTxMB), "MB"))));
        Stats.Add(new StatItem(L.T("Disco"), L.T("{0} gravados", Format.Number(days.Sum(d => d.DiskWriteMB), "MB")), L.T("{0} lidos", Format.Number(days.Sum(d => d.DiskReadMB), "MB"))));

        var since = HistoryHubViewModel.Local(data.Since);
        var parts = new List<string>();
        if (data.Gpu is { } gpu) parts.Add(gpu);
        if (data.Since > periodFrom) parts.Add(L.T("histórico desde {0}", since.ToString(L.English ? "MM/dd/yyyy" : "dd/MM/yyyy", Format.Pt)));
        if (data.Resolution == "1h") parts.Add(L.T("contas por hora (mais de 30 dias)"));
        Subtitle = string.Join(" · ", parts);
        BuildBars();
        Raise(nameof(HasData));
    }

    private void BuildBars()
    {
        Raise(nameof(Unit));
        if (_data is not { } data) { Bars = Array.Empty<ChartBar>(); return; }
        var key = _choice.Key;
        var groups = _monthly
            ? data.Days.GroupBy(d => { var l = HistoryHubViewModel.Local(d.Date); return new DateTime(l.Year, l.Month, 1); }).ToList()
            : data.Days.GroupBy(d => HistoryHubViewModel.Local(d.Date).Date).ToList();
        var bars = new List<ChartBar>();
        // Dias (ou meses) sem dados entram como zero, para o eixo do tempo ficar contínuo.
        if (groups.Count > 0)
        {
            var map = groups.ToDictionary(g => g.Key, g => g.ToList());
            var first = groups.Min(g => g.Key);
            var last = _monthly ? new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1) : DateTime.Today;
            for (var d = first; d <= last; d = _monthly ? d.AddMonths(1) : d.AddDays(1))
            {
                var items = map.GetValueOrDefault(d) ?? new List<UsageDay>();
                var on = items.Sum(x => x.OnSeconds);
                double value = key switch
                {
                    "on" => on / 3600.0,
                    "energy" => items.Sum(x => x.EnergyWh ?? 0),
                    "games" => items.Sum(x => x.GameSeconds) / 3600.0,
                    "cpu_temp" => items.Select(x => x.CpuTempMax ?? 0).DefaultIfEmpty(0).Max(),
                    "gpu_temp" => items.Select(x => x.GpuTempMax ?? 0).DefaultIfEmpty(0).Max(),
                    "net" => items.Sum(x => x.NetRxMB + x.NetTxMB),
                    _ => items.Sum(x => x.DiskWriteMB),
                };
                var label = _monthly ? d.ToString("MMM/yy", Format.Pt) : data.Days.Count <= 8 ? d.ToString("ddd dd", Format.Pt) : Format.DayMonth(d);
                var title = _monthly ? d.ToString(L.English ? "MMMM yyyy" : "MMMM 'de' yyyy", Format.Pt) : d.ToString("dddd", Format.Pt) + ", " + Format.DayMonth(d);
                var main = key switch
                {
                    "on" => HistoryHubViewModel.Hours(on),
                    "games" => HistoryHubViewModel.Hours(items.Sum(x => x.GameSeconds)),
                    "net" => Format.Number(value, "MB"),
                    _ => Format.Number(value, Unit),
                };
                var extra = key switch
                {
                    "net" => $"↓ {Format.Number(items.Sum(x => x.NetRxMB), "MB")} · ↑ {Format.Number(items.Sum(x => x.NetTxMB), "MB")}",
                    "energy" when on > 0 => L.T("média de {0} ligada", Format.Number(value * 3600 / on, "W")),
                    "cpu_temp" or "gpu_temp" => L.T("ligada {0}", HistoryHubViewModel.Hours(on)),
                    _ => on > 0 ? L.T("ligada {0}", HistoryHubViewModel.Hours(on)) : L.T("desligada"),
                };
                bars.Add(new ChartBar(label, value, $"{title}\n{main}\n{extra}"));
            }
        }
        Bars = bars;
    }
}

/// <summary>
/// Página Histórico (abaixo de Sensores): jogos de todas as máquinas (sessões com FPS e sensores) e o uso
/// consolidado de cada uma (tempo ligada, energia, temperaturas, rede, disco), por período.
/// </summary>
public sealed class HistoryHubViewModel : ObservableObject
{
    private readonly ObservableCollection<MachineViewModel> _machines;
    private readonly DispatcherTimer _timer;
    private bool _showGames = true;
    private bool _isLoading;
    private bool _active;
    private DateTime _loadedAt;
    private int _version;
    private SessionDetail? _detail;
    private GameRow? _filter;
    private readonly List<SessionRow> _allSessions = new();

    public HistoryHubViewModel(ObservableCollection<MachineViewModel> machines)
    {
        _machines = machines;
        Ranges.Add(new RangeChoice(L.T("7 dias"), TimeSpan.FromDays(7)));
        Ranges.Add(new RangeChoice(L.T("30 dias"), TimeSpan.FromDays(30)) { IsSelected = true });
        Ranges.Add(new RangeChoice(L.T("12 meses"), TimeSpan.FromDays(365)));
        Ranges.Add(new RangeChoice(L.T("Tudo"), TimeSpan.FromDays(730)));
        foreach (var r in Ranges) r.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(RangeChoice.IsSelected) && ((RangeChoice)s!).IsSelected) { _range = (RangeChoice)s!; _ = LoadAsync(); } };

        OpenSessionCommand = new RelayCommand(p => { if (p is SessionRow row) OpenSession(row); });
        CloseSessionCommand = new RelayCommand(() => Detail = null);
        SelectGameCommand = new RelayCommand(p => { if (p is GameRow g) Filter = Filter == g ? null : g; });
        ClearFilterCommand = new RelayCommand(() => Filter = null);
        RenameCommand = new RelayCommand(p => { if (p is GameRow g) _ = RenameAsync(g.Machine, g.Summary.Exe, g.Name); });
        RenameDetailCommand = new RelayCommand(() => { if (Detail is { } d) _ = RenameAsync(d.Row.Machine, d.Session.Exe, d.Session.Name); });
        IgnoreCommand = new RelayCommand(p => { if (p is GameRow g) _ = IgnoreAsync(g); });
        RestoreCommand = new RelayCommand(p => { if (p is GameRow g) _ = RestoreAsync(g); });
        DeleteSessionCommand = new RelayCommand(() => _ = DeleteSessionAsync());
        ExportCommand = new RelayCommand(Export);
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        ClearHistoryCommand = new RelayCommand(() => _ = ClearHistoryAsync());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => { if (_active && Detail is null) _ = LoadAsync(quiet: true); };
    }

    public ObservableCollection<RangeChoice> Ranges { get; } = new();
    public ObservableCollection<StatItem> GameStats { get; } = new();
    public ObservableCollection<GameRow> Games { get; } = new();
    public ObservableCollection<GameRow> Ignored { get; } = new();
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    public ObservableCollection<SessionRow> Playing { get; } = new();
    public ObservableCollection<string> Notices { get; } = new();
    public ObservableCollection<UsageMachine> Usage { get; } = new();

    public RelayCommand OpenSessionCommand { get; }
    public RelayCommand CloseSessionCommand { get; }
    public RelayCommand SelectGameCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand RenameDetailCommand { get; }
    public RelayCommand IgnoreCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand DeleteSessionCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    public bool ShowGames { get => _showGames; set { if (Set(ref _showGames, value)) { Raise(nameof(ShowUsage)); Detail = null; } } }
    public bool ShowUsage { get => !_showGames; set => ShowGames = !value; }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool HasGames => Games.Count > 0;
    public bool NoGames => !IsLoading && Games.Count == 0 && Detail is null;
    public bool HasIgnored => Ignored.Count > 0;
    private RangeChoice? _range;
    public RangeChoice SelectedRange => _range ??= Ranges.First(r => r.IsSelected);

    public SessionDetail? Detail
    {
        get => _detail;
        private set
        {
            if (!Set(ref _detail, value)) return;
            Raise(nameof(ShowList));
            Raise(nameof(NoGames));
        }
    }

    public bool ShowList => _detail is null;

    public GameRow? Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            foreach (var g in Games) g.IsSelected = g == value;
            Raise(nameof(SessionsTitle));
            FillSessions();
        }
    }

    public string SessionsTitle => _filter is { } f ? L.T("Sessões de {0} ({1})", f.Name, f.Machine.Name) : L.T("Sessões");

    // ------------------------------------------------------------------ ciclo

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            _timer.Start();
            if (DateTime.UtcNow - _loadedAt > TimeSpan.FromSeconds(30)) _ = LoadAsync(quiet: _loadedAt != default);
        }
        else _timer.Stop();
    }

    private (long From, long To, long GamesFrom, bool Monthly) Period()
    {
        var today = DateTime.Today;
        var range = SelectedRange;
        var all = range.Span.TotalDays > 400;
        var start = range.Span.TotalDays switch
        {
            7 => today.AddDays(-6),
            30 => today.AddDays(-29),
            365 => new DateTime(today.Year, today.Month, 1).AddMonths(-11),
            _ => new DateTime(today.Year, today.Month, 1).AddMonths(-23),
        };
        var from = new DateTimeOffset(start).ToUnixTimeSeconds();
        var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
        return (from, to, all ? 0 : from, range.Span > TimeSpan.FromDays(31));
    }

    private async Task LoadAsync(bool quiet = false)
    {
        var version = ++_version;
        if (!quiet) IsLoading = true;
        Raise(nameof(NoGames));
        var (from, to, gamesFrom, monthly) = Period();
        var machines = _machines.ToList();
        var results = await Task.WhenAll(machines.Select(async m =>
        {
            GamesResponse? games = null;
            UsageResponse? usage = null;
            string? gamesError = null, usageError = null;
            try
            {
                using var client = CollectorClient.For(m.Machine);
                try { games = await client.GamesAsync(gamesFrom, to); }
                catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound) { gamesError = Outdated(ex); }
                catch (Exception ex) { gamesError = ex.Message; }
                try { usage = await client.UsageAsync(from, to); }
                catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound) { usageError = Outdated(ex); }
                catch (Exception ex) { usageError = ex.Message; }
            }
            catch (Exception ex)
            {
                gamesError = usageError = ex.Message;
            }
            return (Machine: m, Games: games, Usage: usage, GamesError: gamesError, UsageError: usageError);
        }));
        if (version != _version) return;

        // ---- jogos
        var filter = _filter is { } f ? (f.Machine, f.Summary.Exe) : default;
        Games.Clear();
        Ignored.Clear();
        _allSessions.Clear();
        Playing.Clear();
        Notices.Clear();
        foreach (var r in results)
        {
            if (r.GamesError is { } err)
            {
                Notices.Add($"{r.Machine.Name}: {err}");
                continue;
            }
            if (r.Games is not { } g) continue;
            if (!g.Recording && g.Error is { } e) Notices.Add($"{r.Machine.Name}: " + L.T("não está gravando os jogos.") + " " + L.T(e));
            foreach (var s in g.Games) Games.Add(new GameRow(this, r.Machine, s));
            foreach (var s in g.Ignored) Ignored.Add(new GameRow(this, r.Machine, s));
            foreach (var s in g.Sessions)
            {
                var row = new SessionRow(r.Machine, s);
                _allSessions.Add(row);
                if (s.Open) Playing.Add(row);
            }
        }
        _filter = Games.FirstOrDefault(x => x.Machine == filter.Machine && x.Summary.Exe == filter.Exe);
        if (_filter is not null) _filter.IsSelected = true;
        Raise(nameof(Filter));
        Raise(nameof(SessionsTitle));
        _allSessions.Sort((a, b) => b.Session.Start.CompareTo(a.Session.Start));
        FillSessions();
        FillGameStats();

        // ---- uso
        foreach (var r in results)
        {
            var u = Usage.FirstOrDefault(x => x.Machine == r.Machine);
            if (u is null) Usage.Add(u = new UsageMachine(r.Machine));
            if (r.Usage is { } data) u.Apply(data, monthly, from);
            else u.Error = r.UsageError;
        }
        foreach (var gone in Usage.Where(u => !machines.Contains(u.Machine)).ToList()) Usage.Remove(gone);

        _loadedAt = DateTime.UtcNow;
        IsLoading = false;
        Raise(nameof(HasGames));
        Raise(nameof(NoGames));
        Raise(nameof(HasIgnored));
    }

    private static string Outdated(CollectorException ex) => ex.Message.Contains("desligado", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)
        ? L.T(ex.Message)
        : L.T("o serviço desta máquina é antigo. Atualize o Corewise nela para gravar jogos e o uso.");

    private void FillSessions()
    {
        Sessions.Clear();
        foreach (var s in _allSessions.Where(s => _filter is null || (s.Machine == _filter.Machine && s.Session.Exe == _filter.Summary.Exe)).Take(300))
            Sessions.Add(s);
    }

    private void FillGameStats()
    {
        GameStats.Clear();
        var total = Games.Sum(g => g.Summary.Seconds);
        var sessions = Games.Sum(g => g.Summary.Sessions);
        GameStats.Add(new StatItem(L.T("Tempo jogado"), total > 0 ? Hours(total) : "—", SelectedRange.Span.TotalDays > 400 ? L.T("desde o início da gravação") : L.T("últimos {0}", SelectedRange.Label)));
        GameStats.Add(new StatItem(L.T("Sessões"), sessions.ToString("N0", Format.Pt), sessions > 0 ? L.T("média de {0}", Hours(total / sessions)) : null));
        GameStats.Add(new StatItem(L.T("Jogos"), Games.Select(g => g.Summary.Exe).Distinct().Count().ToString("N0", Format.Pt), null));
        var top = Games.MaxBy(g => g.Summary.Seconds);
        GameStats.Add(new StatItem(L.T("Mais jogado"), top?.Name ?? "—", top is null ? null : L.T("{0} · {1} FPS médio", top.Time, top.Fps)));
    }

    private void OpenSession(SessionRow row)
    {
        var same = _allSessions.Where(s => s.Machine == row.Machine && s.Session.Exe == row.Session.Exe);
        var detail = new SessionDetail(row, same);
        Detail = detail;
        _ = detail.LoadAsync();
    }

    // ------------------------------------------------------------------ ações

    private async Task RenameAsync(MachineViewModel machine, string exe, string current)
    {
        var name = TextPromptWindow.Ask(Application.Current.MainWindow, L.T("Renomear jogo"),
            L.T("Nome que aparece no histórico (vale para todas as sessões deste programa). Deixe vazio para voltar ao nome detectado."), current);
        if (name is null) return;
        try
        {
            using var client = CollectorClient.For(machine.Machine);
            await client.UpdateGameAsync(new GameUpdate { Exe = exe, Name = name });
            var detail = Detail;
            await LoadAsync(quiet: true);
            if (detail is not null) await detail.LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Não foi possível renomear: {0}", ex.Message), "Corewise", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task IgnoreAsync(GameRow game)
    {
        if (MessageBox.Show(L.T("Ignorar \"{0}\"?\n\nEle deixa de ser gravado e some das listas (as sessões antigas ficam guardadas). Dá para voltar em \"Programas ignorados\", no fim da página.", game.Name),
                "Corewise", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await UpdateAsync(game.Machine, new GameUpdate { Exe = game.Summary.Exe, Ignored = true });
    }

    private Task RestoreAsync(GameRow game) => UpdateAsync(game.Machine, new GameUpdate { Exe = game.Summary.Exe, Ignored = false });

    private async Task UpdateAsync(MachineViewModel machine, GameUpdate update)
    {
        try
        {
            using var client = CollectorClient.For(machine.Machine);
            await client.UpdateGameAsync(update);
            if (_filter?.Summary.Exe == update.Exe) Filter = null;
            await LoadAsync(quiet: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Não foi possível salvar: {0}", ex.Message), "Corewise", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DeleteSessionAsync()
    {
        if (Detail is not { } d) return;
        if (MessageBox.Show(L.T("Excluir a sessão de {0} de {1}? Não dá para desfazer.", d.Session.Name, When(d.Session.Start)), "Corewise",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            using var client = CollectorClient.For(d.Row.Machine.Machine);
            await client.DeleteGameSessionAsync(d.Session.Id);
            Detail = null;
            await LoadAsync(quiet: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Não foi possível excluir: {0}", ex.Message), "Corewise", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Separador do CSV: ponto e vírgula em português (vírgula é o decimal), vírgula em inglês.</summary>
    private static string Sep => L.English ? "," : ";";
    private static string Csv(string header) => header.Replace(";", Sep);
    private static string Q(string? text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";

    /// <summary>Apaga o histórico (uso e/ou jogos) de uma ou de todas as máquinas, depois de confirmar.</summary>
    private async Task ClearHistoryAsync()
    {
        var choice = ClearHistoryWindow.Ask(Application.Current.MainWindow, _machines.Select(m => m.Name).ToList());
        if (choice is null) return;
        var targets = choice.Machine is null ? _machines.ToList() : _machines.Where(m => m.Name == choice.Machine).ToList();
        var errors = new List<string>();
        IsLoading = true;
        foreach (var m in targets)
        {
            try
            {
                using var client = CollectorClient.For(m.Machine);
                await client.ClearHistoryAsync(choice.Sensors, choice.Games);
            }
            catch (Exception ex)
            {
                errors.Add($"{m.Name}: {ex.Message}");
            }
        }
        Detail = null;
        Filter = null;
        await LoadAsync();
        if (errors.Count > 0)
            MessageBox.Show(L.T("Não foi possível limpar:") + "\n\n" + string.Join("\n", errors), "Corewise", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>CSV (ponto e vírgula e vírgula decimal em português; vírgula e ponto em inglês): sessões na aba Jogos, dias na aba Uso, pontos no detalhe.</summary>
    private void Export()
    {
        string N(double? v, string format = "0.#") => v is { } x ? x.ToString(format, Format.Pt) : "";
        var sb = new StringBuilder();
        string name;
        if (Detail is { Session.Points: { Count: > 0 } points } d)
        {
            name = $"{d.Session.Name} - {Local(d.Session.Start):yyyy-MM-dd HHmm}";
            sb.AppendLine(Csv(L.T("Data e hora;FPS;1% low;GPU uso (%);GPU temp (°C);GPU consumo (W);CPU uso (%);CPU temp (°C)")));
            foreach (var p in points)
                sb.AppendLine(string.Join(Sep, Local(p.T).ToString("yyyy-MM-dd HH:mm:ss", Format.Pt), N(p.Fps), N(p.Low1), N(p.GpuUsage), N(p.GpuTemp), N(p.GpuPower), N(p.CpuUsage), N(p.CpuTemp)));
        }
        else if (ShowGames)
        {
            name = $"{L.T("Jogos")} - {SelectedRange.Label}";
            sb.AppendLine(Csv(L.T("Início;Fim;Máquina;Jogo;Executável;Tempo jogado (min);FPS médio;1% low;0,1% low;Tempo de quadro (ms);Engasgos;GPU temp máx (°C);Energia (Wh)")));
            foreach (var r in Sessions)
            {
                var s = r.Session;
                sb.AppendLine(string.Join(Sep, Local(s.Start).ToString("yyyy-MM-dd HH:mm", Format.Pt), Local(s.End).ToString("yyyy-MM-dd HH:mm", Format.Pt),
                    Q(r.MachineName), Q(s.Name), Q(s.Exe), N(s.ActiveSeconds / 60.0, "0"), N(s.FpsAvg), N(s.Low1Fps), N(s.Low01Fps), N(s.FrametimeAvgMs, "0.00"), s.Stutters,
                    N(s.Metrics.FirstOrDefault(m => m.Key == "gpu_temp")?.Max), N(s.EnergyWh)));
            }
        }
        else
        {
            name = $"{L.T("Uso")} - {SelectedRange.Label}";
            sb.AppendLine(Csv(L.T("Máquina;Dia;Ligada (h);Jogando (h);Energia CPU+GPU (Wh);CPU uso médio (%);CPU temp média (°C);CPU temp máx (°C);GPU uso médio (%);GPU temp média (°C);GPU temp máx (°C);CPU >80% (h);GPU >80% (h);Rede recebida (MB);Rede enviada (MB);Disco lido (MB);Disco gravado (MB)")));
            foreach (var u in Usage.Where(u => u.Data is not null))
                foreach (var day in u.Data!.Days)
                    sb.AppendLine(string.Join(Sep, Q(u.Name), Local(day.Date).ToString("yyyy-MM-dd", Format.Pt), N(day.OnSeconds / 3600.0, "0.00"), N(day.GameSeconds / 3600.0, "0.00"),
                        N(day.EnergyWh, "0"), N(day.CpuUsageAvg), N(day.CpuTempAvg), N(day.CpuTempMax), N(day.GpuUsageAvg), N(day.GpuTempAvg), N(day.GpuTempMax),
                        N(day.CpuHeavySeconds / 3600.0, "0.00"), N(day.GpuHeavySeconds / 3600.0, "0.00"), N(day.NetRxMB, "0"), N(day.NetTxMB, "0"), N(day.DiskReadMB, "0"), N(day.DiskWriteMB, "0")));
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = (name + ".csv").Replace('/', '-').Replace(':', '-'), Filter = "CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    // ------------------------------------------------------------------ formatação

    public static DateTime Local(long t) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;

    /// <summary>"hoje 21:30", "ontem 21:30", "sáb 01/11 21:30" ou "01/11/2025".</summary>
    public static string When(long t)
    {
        var l = Local(t);
        var days = (DateTime.Today - l.Date).TotalDays;
        return days switch
        {
            0 => L.T("hoje {0}", l.ToString("HH:mm", Format.Pt)),
            1 => L.T("ontem {0}", l.ToString("HH:mm", Format.Pt)),
            < 180 => l.ToString("ddd ", Format.Pt) + Format.DayMonth(l) + l.ToString(" HH:mm", Format.Pt),
            _ => l.ToString(L.English ? "MM/dd/yyyy" : "dd/MM/yyyy", Format.Pt),
        };
    }

    /// <summary>Segundos → "45 s", "38 min", "2 h 05 min", "124 h".</summary>
    public static string Hours(long seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min",
        < 100 * 3600 => $"{seconds / 3600} h {seconds % 3600 / 60:00} min",
        _ => $"{seconds / 3600:N0} h",
    };
}
