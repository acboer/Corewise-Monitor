using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Threading;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

public sealed record ProcessRow(string Name, string Value, double Fraction, string? Detail);

/// <summary>
/// Cartão "Processos": os 5 que mais usam CPU, RAM e disco na máquina. Só consulta o coletor (a cada 3 s) enquanto
/// a página da máquina está aberta.
/// </summary>
public sealed class ProcessListViewModel : ObservableObject
{
    private readonly PairedMachine _machine;
    private readonly DispatcherTimer _timer;
    private CollectorClient? _client;
    private string? _error;
    private string _info = L.T("aguardando a primeira medição...");
    private bool _busy;

    public ProcessListViewModel(PairedMachine machine)
    {
        _machine = machine;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<ProcessRow> ByCpu { get; } = new();
    public ObservableCollection<ProcessRow> ByRam { get; } = new();
    public ObservableCollection<ProcessRow> ByDisk { get; } = new();
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string Info { get => _info; private set => Set(ref _info, value); }

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        _client?.Dispose();
        _client = null;
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _client ??= CollectorClient.For(_machine);
            var r = await _client.ProcessesAsync();
            Error = null;
            if (r.IntervalSeconds <= 0)
            {
                Info = L.T("{0} processos · medindo...", r.ProcessCount);
                return;
            }
            // Uma casa decimal: a maioria dos processos fica abaixo de 1 %.
            Fill(ByCpu, r.Processes.OrderByDescending(p => p.CpuPercent), p => p.CpuPercent, p => p.CpuPercent.ToString("0.0", Format.Pt) + " %",
                Math.Max(5, r.Processes.Max(p => p.CpuPercent)));
            Fill(ByRam, r.Processes.OrderByDescending(p => p.RamGb), p => p.RamGb, p => Format.Number(p.RamGb, "GB"), null);
            Fill(ByDisk, r.Processes.OrderByDescending(p => p.DiskMBs), p => p.DiskMBs, p => Format.Number(p.DiskMBs, "MB/s"), null);
            Info = L.T("{0} processos · atualiza a cada 3 s · agrupados pelo nome do programa", r.ProcessCount);
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            Error = L.T("O serviço desta máquina ainda não mostra processos. Atualize o Corewise nela.");
            _timer.Stop();
        }
        catch (Exception ex)
        {
            Error = L.T("Sem dados de processos agora: {0}", ex.Message);
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _busy = false;
        }
    }

    private static void Fill(ObservableCollection<ProcessRow> target, IEnumerable<ProcessUsage> ordered, Func<ProcessUsage, double> value,
        Func<ProcessUsage, string> text, double? scaleMax)
    {
        var top = ordered.Take(5).ToList();
        var max = scaleMax ?? Math.Max(1e-6, top.Select(value).DefaultIfEmpty(0).Max());
        target.Clear();
        foreach (var p in top)
            target.Add(new ProcessRow(p.Name, text(p), Math.Clamp(value(p) / max, 0, 1), p.Count > 1 ? L.T("{0} processos", p.Count) : null));
    }
}
