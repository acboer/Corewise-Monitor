using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Threading;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>Métrica que pode ser vista no gráfico do histórico.</summary>
public sealed class MetricChoice(string componentId, string metric, string label) : ObservableObject
{
    private bool _isSelected;
    public string ComponentId { get; } = componentId;
    public string Metric { get; } = metric;
    public string Label { get; } = label;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Período do gráfico.</summary>
public sealed class RangeChoice(string label, TimeSpan span) : ObservableObject
{
    private bool _isSelected;
    public string Label { get; } = label;
    public TimeSpan Span { get; } = span;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Linha "rótulo · valor" de resumo (estatísticas, tráfego).</summary>
public sealed record StatItem(string Label, string Value, string? Sub = null);

/// <summary>
/// Página de histórico de um cartão: escolhe a métrica e o período, mostra o gráfico (média com faixa de mínimo e
/// máximo), as estatísticas do período e, para rede, o tráfego por dia, mês e ano. Atualiza sozinha.
/// </summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly MachineViewModel _machine;
    private readonly DispatcherTimer _timer;
    private HistoryResponse? _data;
    private bool _isLoading;
    private string? _error;
    private int _loadVersion;

    public HistoryViewModel(MachineViewModel machine, CardViewModel card)
    {
        _machine = machine;
        Card = card;

        // Métricas do cartão: a principal primeiro, depois linhas e detalhes; volumes do disco no fim.
        var seen = new HashSet<(string, string)>();
        void Add(string component, string metric, string label)
        {
            if (Excluded.Contains(metric) || !seen.Add((component, metric))) return;
            Metrics.Add(new MetricChoice(component, metric, label));
        }
        if (card.MainMetric is { } main) Add(main.ComponentId, main.Metric, Format.MetricLabel(main.Metric));
        foreach (var l in card.Lines.Concat(card.DetailLines)) Add(l.ComponentId, l.Metric, l.Label);
        foreach (var v in card.Volumes) Add(v.ComponentId, "used", L.T("Espaço usado {0}", L.T(v.Name)));
        if (Metrics.Count > 0) Metrics[0].IsSelected = true;

        Ranges.Add(new RangeChoice("5 min", TimeSpan.FromMinutes(5)));
        Ranges.Add(new RangeChoice("1 h", TimeSpan.FromHours(1)));
        Ranges.Add(new RangeChoice("24 h", TimeSpan.FromHours(24)) { IsSelected = true });
        Ranges.Add(new RangeChoice(L.T("7 dias"), TimeSpan.FromDays(7)));
        Ranges.Add(new RangeChoice(L.T("30 dias"), TimeSpan.FromDays(30)));
        Ranges.Add(new RangeChoice(L.T("1 ano"), TimeSpan.FromDays(365)));

        foreach (var m in Metrics) m.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(MetricChoice.IsSelected) && ((MetricChoice)s!).IsSelected) { _metric = (MetricChoice)s!; _ = LoadAsync(); } };
        foreach (var r in Ranges) r.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(RangeChoice.IsSelected) && ((RangeChoice)s!).IsSelected) { _range = (RangeChoice)s!; _ = LoadAsync(); } };

        CloseCommand = new RelayCommand(() => _machine.CloseHistory());
        ExportCommand = new RelayCommand(Export);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) =>
        {
            // Curto: a cada 10 s. Longo: a cada minuto (a resolução é de 1 min ou mais).
            if (SelectedRange.Span <= TimeSpan.FromHours(1) || DateTime.Now.Second < 10) _ = LoadAsync(quiet: true);
        };
        _timer.Start();
        _ = LoadAsync();
        if (IsNetwork) _ = LoadTrafficAsync();
    }

    private static readonly HashSet<string> Excluded = ["health", "rx_total", "tx_total", "total", "vram_total"];

    public CardViewModel Card { get; }
    public ObservableCollection<MetricChoice> Metrics { get; } = new();
    public ObservableCollection<RangeChoice> Ranges { get; } = new();
    public ObservableCollection<StatItem> Stats { get; } = new();
    public ObservableCollection<StatItem> Traffic { get; } = new();
    public RelayCommand CloseCommand { get; }
    public RelayCommand ExportCommand { get; }

    // Guardados no clique: na troca, a etiqueta anterior ainda está marcada por um instante.
    private MetricChoice? _metric;
    private RangeChoice? _range;
    public MetricChoice? SelectedMetric => _metric ??= Metrics.FirstOrDefault(m => m.IsSelected);
    public RangeChoice SelectedRange => _range ??= Ranges.First(r => r.IsSelected);
    public bool IsNetwork => Card.Kind == ComponentKind.Network;
    /// <summary>Algum detalhe do cartão com valor (ex.: núcleo mais rápido, hot spot, horas ligado).</summary>
    public bool HasDetails => Card.DetailLines.Any(l => l.HasValue);
    public string MachineName => _machine.Name;

    public HistoryResponse? Data { get => _data; private set { if (Set(ref _data, value)) { Raise(nameof(Points)); Raise(nameof(HasData)); } } }
    public IReadOnlyList<HistoryPoint> Points => _data?.Points ?? (IReadOnlyList<HistoryPoint>)Array.Empty<HistoryPoint>();
    public bool HasData => _data is { Points.Count: > 0 };
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public string SinceText => _data?.Since is { } s
        ? L.T("Histórico gravado desde {0} ({1})", Format.When(DateTimeOffset.FromUnixTimeSeconds(s)), Duration(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s)) : "";

    private static DateTime Local(long t) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;

    private static string Duration(long seconds) => seconds switch
    {
        < 3600 => $"{seconds / 60} min",
        < 86400 => $"{seconds / 3600} h {seconds % 3600 / 60} min",
        _ => L.T(seconds / 86400 == 1 ? "{0} dia e {1} h" : "{0} dias e {1} h", seconds / 86400, seconds % 86400 / 3600),
    };

    public void Stop() => _timer.Stop();

    private async Task LoadAsync(bool quiet = false)
    {
        if (SelectedMetric is not { } metric) return;
        var version = ++_loadVersion;
        var range = SelectedRange;
        if (!quiet) IsLoading = true;
        try
        {
            var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var from = to - (long)range.Span.TotalSeconds;
            using var client = CollectorClient.For(_machine.Machine);
            var data = await client.HistoryAsync(metric.ComponentId, metric.Metric, from, to, 500);
            if (version != _loadVersion) return; // trocou de métrica/período enquanto carregava
            Data = data;
            Error = null;
            FillStats(metric, data, range);
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            if (version != _loadVersion) return;
            Data = null;
            Stats.Clear();
            Error = ex.Message.Contains("Sem histórico", StringComparison.Ordinal) || ex.Message.Contains("No history", StringComparison.Ordinal)
                ? L.T("Ainda não há histórico desta métrica (ela começa a ser gravada quando tem uma leitura válida; HDDs parados não têm temperatura).")
                : L.T("O serviço desta máquina não grava histórico. Atualize o Corewise nela.");
        }
        catch (Exception ex)
        {
            if (version == _loadVersion && !quiet) Error = L.T("Não foi possível ler o histórico: {0}", ex.Message);
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
            Raise(nameof(SinceText));
        }
    }

    private void FillStats(MetricChoice metric, HistoryResponse data, RangeChoice range)
    {
        Stats.Clear();
        var current = _machine.Value(metric.ComponentId, metric.Metric);
        Stats.Add(new StatItem(L.T("Atual"), Format.Value(current)));
        if (data.Stats is { } s)
        {
            string When(long t) => range.Span <= TimeSpan.FromDays(1) ? Local(t).ToString("HH:mm", Format.Pt) : Local(t).ToString("dd/MM HH:mm", Format.Pt);
            Stats.Add(new StatItem(L.T("Mínimo"), Format.Number(s.Min, data.Unit), When(s.MinT)));
            Stats.Add(new StatItem(L.T("Média"), Format.Number(s.Avg, data.Unit)));
            Stats.Add(new StatItem(L.T("Máximo"), Format.Number(s.Max, data.Unit), When(s.MaxT)));
            var covered = Math.Min(1, s.Seconds / Math.Max(1, range.Span.TotalSeconds));
            Stats.Add(new StatItem(L.T("Tempo medido"), Duration(s.Seconds), covered < 0.98 ? L.T("{0:P0} do período", covered) : L.T("período todo")));
        }
    }

    /// <summary>
    /// Tráfego por dia: "Recebido hoje" zera à meia-noite, então o máximo de cada dia é o total daquele dia.
    /// Soma recebido + enviado para hoje, ontem, 7 dias, este mês e este ano.
    /// </summary>
    private async Task LoadTrafficAsync()
    {
        try
        {
            var today = new DateTimeOffset(DateTime.Today);
            var yearStart = new DateTimeOffset(new DateTime(DateTime.Today.Year, 1, 1));
            var from = Math.Min(yearStart.ToUnixTimeSeconds(), today.AddDays(-6).ToUnixTimeSeconds());
            var to = today.AddDays(1).ToUnixTimeSeconds();
            var days = (int)((to - from) / 86400);
            using var client = CollectorClient.For(_machine.Machine);
            var perDay = new Dictionary<DateTime, double>();
            foreach (var metric in new[] { "rx_today", "tx_today" })
            {
                var h = await client.HistoryAsync(Card.ComponentId, metric, from, to, days);
                foreach (var p in h.Points)
                {
                    var day = Local(p.T).Date;
                    perDay[day] = perDay.GetValueOrDefault(day) + p.Max;
                }
            }
            double Sum(Func<DateTime, bool> when) => perDay.Where(kv => when(kv.Key)).Sum(kv => kv.Value);
            var d = DateTime.Today;
            Traffic.Clear();
            Traffic.Add(new StatItem(L.T("Hoje"), Format.Number(Sum(x => x == d), "GB")));
            Traffic.Add(new StatItem(L.T("Ontem"), Format.Number(Sum(x => x == d.AddDays(-1)), "GB")));
            Traffic.Add(new StatItem(L.T("7 dias"), Format.Number(Sum(x => x > d.AddDays(-7)), "GB")));
            Traffic.Add(new StatItem(L.T("Este mês"), Format.Number(Sum(x => x.Year == d.Year && x.Month == d.Month), "GB")));
            Traffic.Add(new StatItem(L.T("Este ano"), Format.Number(Sum(x => x.Year == d.Year), "GB"), L.T("desde o início da gravação")));
        }
        catch (Exception)
        {
            // sem histórico ainda (ou coletor antigo): o bloco de tráfego fica escondido
        }
    }

    /// <summary>CSV com ponto e vírgula e vírgula decimal (abre direto no Excel em português).</summary>
    private void Export()
    {
        if (_data is not { Points.Count: > 0 } data || SelectedMetric is not { } metric) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_machine.Name} - {Card.Title} - {metric.Label} - {SelectedRange.Label}.csv".Replace('/', '-').Replace(':', '-'),
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dialog.ShowDialog() != true) return;
        var sb = new StringBuilder();
        var sep = L.English ? "," : ";";
        sb.AppendLine(string.Join(sep, L.T("Data e hora"), L.T("Média ({0})", data.Unit), L.T("Mínimo ({0})", data.Unit), L.T("Máximo ({0})", data.Unit)));
        foreach (var p in data.Points)
            sb.AppendLine(string.Join(sep, Local(p.T).ToString("yyyy-MM-dd HH:mm:ss", Format.Pt),
                p.Avg.ToString("0.###", Format.Pt), p.Min.ToString("0.###", Format.Pt), p.Max.ToString("0.###", Format.Pt)));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
    }
}
