using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

public sealed record DurationChoice(string Label, int Minutes);

/// <summary>
/// Cartão de um teste de carga (CPU, memória, GPU ou estabilidade) na aba Benchmarks: o teste roda no coletor da
/// máquina; o painel começa, acompanha (números parciais e sensores) e mostra o resultado e os anteriores.
/// </summary>
public sealed class LoadBenchViewModel : ObservableObject
{
    private readonly MachineViewModel _machine;
    private bool _running, _loaded;
    private string? _phase, _error, _resultTitle;
    private double _progress;
    private DurationChoice _duration;
    private bool _includeGpu;
    private List<BenchSample> _samples = new();
    private HistoryResponse? _chart;
    private MetricChoice _chartMetric;
    private List<BenchResult> _history = new();

    public LoadBenchViewModel(MachineViewModel machine, string kind)
    {
        _machine = machine;
        Kind = kind;
        (Title, Subtitle, Icon) = kind switch
        {
            "cpu" => (L.T("Processador"), L.T("Compressão, criptografia, contas vetoriais e ordenação em 1 núcleo e em todos (~30 s). Pontos: um Ryzen 9 9950X vale ~1000 em 1 núcleo; serve para comparar as suas máquinas."), "kind-cpu"),
            "memoria" => (L.T("Memória"), L.T("Leitura, escrita e cópia num bloco de 1 GB e latência, no estilo do AIDA64 (~15 s). GB = 10⁹ bytes."), "kind-ram"),
            "gpu" => (L.T("Placa de vídeo"), L.T("Cálculo FP32 (TFLOPS) e banda da memória da placa, por DirectX 11 compute (~15 s). Mede a placa, não um jogo."), "kind-gpu"),
            _ => (L.T("Estabilidade"), L.T("Estresse de todos os núcleos (e da placa de vídeo, se marcar), conferindo cada conta. Mostra erros de cálculo, temperatura e se o clock cai (throttling). Para sozinho acima de 100 °C na CPU ou 92 °C na GPU."), "status-caution"),
        };
        Durations = [new("5 min", 5), new("10 min", 10), new("30 min", 30), new("60 min", 60)];
        _duration = Durations[1];
        foreach (var (key, label) in new[] { ("ct", L.T("CPU temperatura")), ("cc", L.T("CPU clock")), ("cp", L.T("CPU consumo")), ("gt", L.T("GPU temperatura")), ("gp", L.T("GPU consumo")) })
            ChartMetrics.Add(new MetricChoice("", key, label));
        ChartMetrics[0].IsSelected = true;
        _chartMetric = ChartMetrics[0];
        foreach (var m in ChartMetrics)
            m.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(MetricChoice.IsSelected) && ((MetricChoice)s!).IsSelected) { _chartMetric = (MetricChoice)s!; BuildChart(); } };
        StartCommand = new RelayCommand(() => _ = StartAsync());
        CancelCommand = new RelayCommand(() => _ = CancelAsync());
    }

    public string Kind { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Icon { get; }
    public bool IsStress => Kind == "estabilidade";
    public List<DurationChoice> Durations { get; }
    public DurationChoice Duration { get => _duration; set => Set(ref _duration, value); }
    public bool IncludeGpu { get => _includeGpu; set => Set(ref _includeGpu, value); }

    public bool Running { get => _running; private set { if (Set(ref _running, value)) Raise(nameof(Idle)); } }
    public bool Idle => !_running;
    public string? Phase { get => _phase; private set => Set(ref _phase, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string? ResultTitle { get => _resultTitle; private set => Set(ref _resultTitle, value); }
    public ObservableCollection<StatItem> Scores { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public ObservableCollection<BenchHistoryRow> History { get; } = new();
    public ObservableCollection<MetricChoice> ChartMetrics { get; } = new();
    public HistoryResponse? Chart { get => _chart; private set { if (Set(ref _chart, value)) { Raise(nameof(ChartPoints)); Raise(nameof(HasChart)); } } }
    public IReadOnlyList<HistoryPoint> ChartPoints => _chart?.Points ?? (IReadOnlyList<HistoryPoint>)Array.Empty<HistoryPoint>();
    public bool HasChart => _samples.Count > 5;
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ------------------------------------------------------------------ ciclo

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            _history = await client.BenchHistoryAsync(Kind);
            FillHistory();
            if (_history.FirstOrDefault() is { } last) ShowResult(last);
            var status = await client.BenchStatusAsync(Kind);
            if (status.State == "running") await FollowAsync(client);
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            Error = L.T("O serviço desta máquina ainda não tem este teste. Atualize o Corewise nela.");
        }
        catch (Exception)
        {
            // fora do ar agora; tenta de novo ao começar um teste
        }
    }

    private async Task StartAsync()
    {
        var what = IsStress
            ? L.T(IncludeGpu ? "Rodar o teste de estabilidade em {0} por {1}, com a placa de vídeo?" : "Rodar o teste de estabilidade em {0} por {1}?", _machine.Name, Duration.Label) + "\n\n" +
              L.T("• Todos os núcleos ficam em 100 % o tempo todo: ventoinhas no máximo e PC lento para outras tarefas.") + "\n" +
              L.T("• O teste para sozinho se a CPU passar de 100 °C ou a placa de 92 °C por 10 s, e pode ser cancelado a qualquer momento.")
            : L.T("Rodar o teste de {0} em {1}?", Title.ToLowerInvariant(), _machine.Name) + "\n\n" +
              L.T("• Leva menos de 1 minuto e deixa o PC ocupado nesse tempo.") + "\n" + L.T("• Para a medida ser justa, feche jogos e renders antes.");
        if (MessageBox.Show(Application.Current.MainWindow!, what, Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Error = null;
        Scores.Clear();
        Notes.Clear();
        _samples = new();
        Chart = null;
        ResultTitle = L.T("Medindo…");
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            await client.StartBenchAsync(new BenchRequest { Kind = Kind, Minutes = Duration.Minutes, Gpu = IncludeGpu });
            await FollowAsync(client);
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            Error = L.T("O serviço desta máquina ainda não tem este teste. Atualize o Corewise nela.");
            ResultTitle = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            ResultTitle = null;
        }
    }

    private async Task FollowAsync(CollectorClient client)
    {
        Running = true;
        try
        {
            while (true)
            {
                var s = await client.BenchStatusAsync(Kind);
                Phase = s.Phase is null ? null : L.T(s.Phase);
                Progress = s.Progress;
                if (s.State == "running")
                {
                    if (s.Partial is { } partial)
                    {
                        Scores.Clear();
                        foreach (var p in partial) Scores.Add(new StatItem(Label(p), Value(p), L.T("parcial")));
                    }
                    if (s.Samples is { Count: > 0 } samples)
                    {
                        _samples = samples;
                        BuildChart();
                    }
                }
                else
                {
                    if (s.State == "done" && s.Result is { } r)
                    {
                        _history = await client.BenchHistoryAsync(Kind);
                        FillHistory();
                        ShowResult(r);
                    }
                    else if (s.State == "failed") { Error = s.Error is null ? null : L.T(s.Error); ResultTitle = null; }
                    else if (s.State == "canceled") { Error = L.T("Teste cancelado."); ResultTitle = null; }
                    break;
                }
                await Task.Delay(IsStress ? 2000 : 1000);
            }
        }
        finally
        {
            Running = false;
            Phase = null;
        }
    }

    private async Task CancelAsync()
    {
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            await client.CancelBenchAsync();
        }
        catch (Exception)
        {
            // o acompanhamento mostra o estado final
        }
    }

    // ------------------------------------------------------------------ resultado

    /// <summary>O nome vem do serviço (no idioma dele): traduz se for português.</summary>
    private static string Label(BenchScore s) => L.T(s.Label);

    private async Task DeleteAsync(string id, string when)
    {
        if (!BenchmarkPageViewModel.ConfirmDelete(when)) return;
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            await client.DeleteBenchResultAsync(disk: false, id);
            _history = await client.BenchHistoryAsync(Kind);
            FillHistory();
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            Error = L.T("Atualize o Corewise nesta máquina para remover resultados.");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private static string Value(BenchScore s) => s.Unit switch
    {
        "pts" => s.Value.ToString("N0", Format.Pt) + " pts",
        "×" => s.Value.ToString("0.0", Format.Pt) + "×",
        "" => s.Value.ToString("N0", Format.Pt),
        "ns" => s.Value.ToString("0.0", Format.Pt) + " ns",
        "min" => s.Value.ToString("0.#", Format.Pt) + " min",
        "TFLOPS" => s.Value.ToString("0.00", Format.Pt) + " TFLOPS",
        _ => s.Value.ToString(s.Value >= 100 ? "N0" : "0.0", Format.Pt) + " " + s.Unit,
    };

    private void ShowResult(BenchResult r)
    {
        // O teste anterior do mesmo tipo, para mostrar a diferença.
        var previous = _history.FirstOrDefault(h => h.Id != r.Id && h.Time < r.Time);
        ResultTitle = $"{Format.When(r.Time)} · {string.Join(" · ", new[] { r.Device, r.Detail }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => L.T(x!)))}";
        Scores.Clear();
        foreach (var s in r.Scores)
        {
            var sub = s.Detail;
            if (previous?.Scores.FirstOrDefault(p => p.Key == s.Key) is { Value: > 0 } p && s.Key is not ("errors" or "duration" or "ops"))
            {
                var delta = (s.Value - p.Value) / p.Value * 100;
                var better = s.Key == "latency" ? delta < 0 : delta > 0;
                var change = (delta >= 0 ? "+" : "") + delta.ToString("0.0", Format.Pt) + " %";
                sub = Math.Abs(delta) < 3 ? L.T("anterior {0} ({1})", Value(p), change) : L.T(better ? "anterior {0} ({1}, melhor)" : "anterior {0} ({1}, pior)", Value(p), change);
            }
            Scores.Add(new StatItem(Label(s), Value(s), sub is null ? null : L.T(sub)));
        }
        Notes.Clear();
        if (r.StoppedReason is { } stop && stop != "Cancelado") Notes.Add(L.T("Parou antes do fim: {0}.", L.T(stop)));
        foreach (var n in r.Notes) Notes.Add(L.T(n));
        _samples = r.Samples;
        BuildChart();
    }

    private void FillHistory()
    {
        History.Clear();
        foreach (var r in _history.Take(10))
        {
            string S(string key) => r.Scores.FirstOrDefault(s => s.Key == key) is { } s ? Value(s) : "—";
            var summary = Kind switch
            {
                "cpu" => L.T("1 núcleo {0} · todos {1}", S("single"), S("multi")),
                "memoria" => L.T("leitura {0} · escrita {1} · cópia {2} · {3}", S("read"), S("write"), S("copy"), S("latency")),
                "gpu" => $"{S("tflops")} · VRAM {S("vram")}",
                _ => L.T("{0} · {1} erros", S("duration"), r.Errors) + (r.StoppedReason is { } x ? " · " + L.T(x) : ""),
            };
            if (Kind == "estabilidade")
            {
                var hottest = r.Samples.Select(x => x.CpuTemp).OfType<double>().DefaultIfEmpty(0).Max();
                if (hottest > 0) summary += " · " + L.T("até {0:0} °C", hottest);
            }
            var when = Format.When(r.Time);
            var id = r.Id;
            History.Add(new BenchHistoryRow(when, r.Device ?? "", summary)
            {
                DeleteCommand = string.IsNullOrEmpty(id) ? null : new RelayCommand(() => _ = DeleteAsync(id, when)),
            });
        }
    }

    private void BuildChart()
    {
        Raise(nameof(HasChart));
        if (_samples.Count < 2) { Chart = null; return; }
        var key = _chartMetric.Metric;
        var unit = key switch { "cc" => "MHz", "cp" or "gp" => "W", _ => "°C" };
        var points = new List<HistoryPoint>();
        foreach (var s in _samples)
        {
            double? v = key switch { "ct" => s.CpuTemp, "cc" => s.CpuClock, "cp" => s.CpuPower, "gt" => s.GpuTemp, _ => s.GpuPower };
            if (v is { } x) points.Add(new HistoryPoint { T = s.T, Avg = x, Min = x, Max = x });
        }
        var step = _samples.Count > 1 ? Math.Max(1, (int)((_samples[^1].T - _samples[0].T) / Math.Max(1, _samples.Count - 1))) : 1;
        Chart = new HistoryResponse { Unit = unit, From = _samples[0].T, To = Math.Max(_samples[0].T + 30, _samples[^1].T + step), StepSeconds = step, Points = points };
    }
}
