using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>Linha da tabela estilo CrystalDiskMark.</summary>
public sealed record DiskBenchRow(string Test, string Read, string Write, string? ReadSub, string? WriteSub);

/// <summary>Teste anterior (para comparar). Com DeleteCommand aparece o X para remover da lista.</summary>
public sealed record BenchHistoryRow(string When, string What, string Summary)
{
    public RelayCommand? DeleteCommand { get; init; }
}

public sealed record SizeChoice(string Label, int Mb);

/// <summary>
/// Aba "Benchmarks" de uma máquina: disco (no coletor dela, estilo CrystalDiskMark) e rede (entre este PC e ela).
/// </summary>
public sealed class BenchmarkPageViewModel : ObservableObject
{
    private readonly MachineViewModel _machine;
    private CancellationTokenSource? _netCancel;
    private bool _diskRunning, _netRunning, _loaded;
    private string? _diskPhase, _diskError, _netPhase, _netError, _netNow;
    private double _diskProgress, _netProgress;
    private string? _selectedVolume;
    private SizeChoice _selectedSize;

    public BenchmarkPageViewModel(MachineViewModel machine)
    {
        _machine = machine;
        Sizes = [new(L.T("256 MB (rápido)"), 256), new(L.T("1 GB (padrão)"), 1024), new(L.T("4 GB (mais fiel em HDD)"), 4096)];
        _selectedSize = Sizes[1];
        StartDiskCommand = new RelayCommand(() => _ = StartDiskAsync());
        CancelDiskCommand = new RelayCommand(() => _ = CancelDiskAsync());
        StartNetCommand = new RelayCommand(() => _ = StartNetAsync());
        CancelNetCommand = new RelayCommand(() => _netCancel?.Cancel());
        LoadBenches = new[] { "cpu", "memoria", "gpu", "estabilidade" }.Select(k => new LoadBenchViewModel(machine, k)).ToList();
    }

    /// <summary>Processador, memória, placa de vídeo e estabilidade.</summary>
    public List<LoadBenchViewModel> LoadBenches { get; }

    private static string Outdated => L.T("O serviço desta máquina ainda não tem benchmarks. Atualize o Corewise nela.");

    /// <summary>Pergunta antes de remover um resultado da lista (não dá para desfazer).</summary>
    public static bool ConfirmDelete(string when)
        => MessageBox.Show(Application.Current.MainWindow!, L.T("Remover o resultado de {0} da lista?\n\nNão dá para desfazer.", when),
            L.T("Remover resultado"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    // ------------------------------------------------------------------ disco

    public List<SizeChoice> Sizes { get; }
    public SizeChoice SelectedSize { get => _selectedSize; set => Set(ref _selectedSize, value); }
    public ObservableCollection<string> Volumes { get; } = new();
    public string? SelectedVolume { get => _selectedVolume; set => Set(ref _selectedVolume, value); }
    public ObservableCollection<DiskBenchRow> DiskRows { get; } = new();
    public ObservableCollection<BenchHistoryRow> DiskHistory { get; } = new();
    public string? DiskResultTitle { get => _diskResultTitle; private set => Set(ref _diskResultTitle, value); }
    private string? _diskResultTitle;

    public bool DiskRunning { get => _diskRunning; private set { if (Set(ref _diskRunning, value)) Raise(nameof(DiskIdle)); } }
    public bool DiskIdle => !_diskRunning;
    public string? DiskPhase { get => _diskPhase; private set => Set(ref _diskPhase, value); }
    public double DiskProgress { get => _diskProgress; private set => Set(ref _diskProgress, value); }
    public string? DiskError { get => _diskError; private set => Set(ref _diskError, value); }
    public RelayCommand StartDiskCommand { get; }
    public RelayCommand CancelDiskCommand { get; }

    // ------------------------------------------------------------------ rede

    /// <summary>A máquina é este próprio PC: não há rede entre os dois para medir.</summary>
    public bool IsLocal => string.Equals(_machine.Descriptor?.HostName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    public bool CanTestNet => !IsLocal;
    public string NetTitle => L.T("Entre este PC ({0}) e {1}", Environment.MachineName, _machine.Name);
    public bool NetRunning { get => _netRunning; private set { if (Set(ref _netRunning, value)) Raise(nameof(NetIdle)); } }
    public bool NetIdle => !_netRunning;
    public string? NetPhase { get => _netPhase; private set => Set(ref _netPhase, value); }
    public double NetProgress { get => _netProgress; private set => Set(ref _netProgress, value); }
    public string? NetNow { get => _netNow; private set => Set(ref _netNow, value); }
    public string? NetError { get => _netError; private set => Set(ref _netError, value); }
    public ObservableCollection<StatItem> NetResult { get; } = new();
    public string? NetVerdict { get => _netVerdict; private set => Set(ref _netVerdict, value); }
    private string? _netVerdict;
    public ObservableCollection<BenchHistoryRow> NetHistory { get; } = new();
    public RelayCommand StartNetCommand { get; }
    public RelayCommand CancelNetCommand { get; }

    /// <summary>Primeira vez que a aba abre: volumes, histórico e teste em andamento (se o painel reabriu no meio).</summary>
    public void EnsureLoaded()
    {
        Volumes.Clear();
        foreach (var v in _machine.Descriptor?.Components.Where(c => c.Kind == ComponentKind.Volume && c.Name.Length >= 2 && c.Name[1] == ':')
                     .Select(c => c.Name).OrderBy(n => n) ?? Enumerable.Empty<string>())
            Volumes.Add(v);
        SelectedVolume ??= Volumes.FirstOrDefault();
        Raise(nameof(IsLocal));
        Raise(nameof(CanTestNet));
        Raise(nameof(NetTitle));
        LoadNetHistory();
        foreach (var b in LoadBenches) b.EnsureLoaded();
        if (_loaded) return;
        _loaded = true;
        _ = LoadDiskHistoryAsync();
        _ = ResumeIfRunningAsync();
    }

    private async Task LoadDiskHistoryAsync()
    {
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            FillDiskHistory(await client.DiskBenchHistoryAsync());
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            DiskError = Outdated;
        }
        catch (Exception)
        {
            // sem histórico agora; tenta de novo ao testar
        }
    }

    private void FillDiskHistory(List<DiskBenchResult> results)
    {
        DiskHistory.Clear();
        foreach (var r in results.Take(15))
        {
            string Line(string test) => r.Lines.FirstOrDefault(l => l.Test == test) is { } l ? string.Format(Format.Pt, "{0:0.#} / {1:0.#} MB/s", l.ReadMBs, l.WriteMBs) : "—";
            var when = Format.When(r.Time);
            var id = r.Id;
            DiskHistory.Add(new BenchHistoryRow(when, $"{r.Drive} {r.VolumeLabel} · {r.DiskName} · {r.SizeMb} MB".Trim(),
                $"SEQ1M Q8T1 {Line("SEQ1M Q8T1")} · RND4K Q1T1 {Line("RND4K Q1T1")}")
            {
                DeleteCommand = string.IsNullOrEmpty(id) ? null : new RelayCommand(() => _ = DeleteDiskAsync(id, when)),
            });
        }
    }

    private async Task DeleteDiskAsync(string id, string when)
    {
        if (!ConfirmDelete(when)) return;
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            await client.DeleteBenchResultAsync(disk: true, id);
            FillDiskHistory(await client.DiskBenchHistoryAsync());
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            DiskError = L.T("Atualize o Corewise nesta máquina para remover resultados.");
        }
        catch (Exception ex)
        {
            DiskError = ex.Message;
        }
    }

    private async Task ResumeIfRunningAsync()
    {
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            var status = await client.DiskBenchStatusAsync();
            if (status.State == "running") await FollowDiskAsync(client);
        }
        catch (Exception)
        {
            // coletor antigo ou fora do ar
        }
    }

    private async Task StartDiskAsync()
    {
        if (SelectedVolume is not { } volume) return;
        var drive = volume[..2];
        var answer = MessageBox.Show(Application.Current.MainWindow!,
            L.T("Testar o disco {0} em {1}?", volume, _machine.Name) + "\n\n" +
            L.T("• Grava um arquivo de {0} MB na raiz de {1} (apagado no fim) e lê/escreve nele por cerca de 1 minuto.", SelectedSize.Mb, drive) + "\n" +
            L.T("• Para a medida ser justa, evite usar esse disco durante o teste.") + "\n" +
            L.T("• Em SSD o desgaste é mínimo (alguns GB escritos). Em HDD parado, ele vai girar."),
            L.T("Teste de disco"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        DiskError = null;
        DiskRows.Clear();
        DiskResultTitle = null;
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            await client.StartDiskBenchAsync(new DiskBenchRequest { Drive = drive, SizeMb = SelectedSize.Mb, Seconds = 5 });
            await FollowDiskAsync(client);
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            DiskError = Outdated;
        }
        catch (Exception ex)
        {
            DiskError = ex.Message;
        }
    }

    /// <summary>Acompanha o teste até terminar (o teste roda no coletor; o painel só consulta).</summary>
    private async Task FollowDiskAsync(CollectorClient client)
    {
        DiskRunning = true;
        try
        {
            while (true)
            {
                var s = await client.DiskBenchStatusAsync();
                DiskPhase = s.Phase is null ? null : L.T(s.Phase);
                DiskProgress = s.Progress;
                if (s.State == "running" && s.Partial is { } partial) ShowPartial(partial, s.LiveTest, s.LiveWrite, s.LiveMBs);
                if (s.State != "running")
                {
                    if (s.State == "done" && s.Result is { } r) ShowDiskResult(r);
                    else if (s.State == "failed") DiskError = s.Error is null ? null : L.T(s.Error);
                    else if (s.State == "canceled") DiskError = L.T("Teste cancelado.");
                    break;
                }
                await Task.Delay(500);
            }
            FillDiskHistory(await client.DiskBenchHistoryAsync());
        }
        finally
        {
            DiskRunning = false;
        }
    }

    private void ShowDiskResult(DiskBenchResult r)
    {
        DiskResultTitle = $"{r.Drive} {r.VolumeLabel} · {r.DiskName} · " + L.T("arquivo de {0} MB", r.SizeMb) + " · " + Format.When(r.Time);
        DiskRows.Clear();
        foreach (var l in r.Lines)
        {
            var random = l.Test.StartsWith("RND", StringComparison.Ordinal);
            string? Sub(double? iops, double? lat) => random
                ? $"{(iops ?? 0).ToString("0", Format.Pt)} IOPS · {((lat ?? 0) / 1000).ToString("0.00", Format.Pt)} ms"
                : l.Test.EndsWith("Q1T1", StringComparison.Ordinal) ? L.T("{0:0.0} ms por bloco", (lat ?? 0) / 1000) : null;
            DiskRows.Add(new DiskBenchRow(l.Test, string.Format(Format.Pt, "{0:0.0}", l.ReadMBs), string.Format(Format.Pt, "{0:0.0}", l.WriteMBs), Sub(l.ReadIops, l.ReadLatencyUs), Sub(l.WriteIops, l.WriteLatencyUs)));
        }
    }

    /// <summary>Durante o teste: o que já foi medido, e a medição em andamento com a velocidade até agora.</summary>
    private void ShowPartial(List<DiskBenchLine> lines, string? liveTest, bool liveWrite, double? liveMBs)
    {
        DiskResultTitle = L.T("Medindo… (os números aparecem conforme cada teste termina)");
        DiskRows.Clear();
        foreach (var t in Core.Collection.DiskBenchmark.Tests)
        {
            var l = lines.FirstOrDefault(x => x.Test == t.Name);
            (string Value, string? Sub) Cell(double? done, bool write)
            {
                if (done is { } v) return (v.ToString("0.0", Format.Pt), null);
                if (liveTest == t.Name && liveWrite == write && liveMBs is { } now) return (now.ToString("0.0", Format.Pt), L.T("medindo…"));
                return ("—", null);
            }
            var read = Cell(l?.ReadMBs, false);
            var write = Cell(l?.WriteMBs, true);
            DiskRows.Add(new DiskBenchRow(t.Name, read.Value, write.Value, read.Sub, write.Sub));
        }
    }

    private async Task CancelDiskAsync()
    {
        try
        {
            using var client = CollectorClient.For(_machine.Machine);
            await client.CancelDiskBenchAsync();
        }
        catch (Exception)
        {
            // o acompanhamento mostra o estado final
        }
    }

    // ------------------------------------------------------------------ rede

    private static string NetHistoryPath => Path.Combine(AppPaths.UserData, "benchmarks-rede.json");

    private static List<NetBenchResult> ReadNetHistory()
    {
        try
        {
            return File.Exists(NetHistoryPath) ? JsonSerializer.Deserialize<List<NetBenchResult>>(File.ReadAllText(NetHistoryPath)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static void WriteNetHistory(List<NetBenchResult> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NetHistoryPath)!);
        SafeFile.WriteAllText(NetHistoryPath, JsonSerializer.Serialize(all.Take(200).ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadNetHistory()
    {
        NetHistory.Clear();
        foreach (var r in ReadNetHistory().Where(r => r.To == _machine.Name).Take(15))
        {
            var when = Format.When(r.Time);
            var id = r.Id;
            var time = r.Time;
            NetHistory.Add(new BenchHistoryRow(when, $"{r.From} ⇄ {r.To} ({r.Address})",
                $"↓ {r.DownloadMbps.ToString("0", Format.Pt)} Mbit/s · ↑ {r.UploadMbps.ToString("0", Format.Pt)} Mbit/s · {r.LatencyMs.ToString("0.0", Format.Pt)} ms")
            {
                DeleteCommand = new RelayCommand(() =>
                {
                    if (!ConfirmDelete(when)) return;
                    var all = ReadNetHistory();
                    all.RemoveAll(x => string.IsNullOrEmpty(id) ? x.Time == time && x.To == _machine.Name : x.Id == id);
                    WriteNetHistory(all);
                    LoadNetHistory();
                }),
            });
        }
    }

    private async Task StartNetAsync()
    {
        NetError = null;
        NetResult.Clear();
        NetVerdict = null;
        NetRunning = true;
        _netCancel = new CancellationTokenSource();
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            var r = await NetBenchmark.RunAsync(_machine.Machine, (phase, p, now) => dispatcher.InvokeAsync(() =>
            {
                NetPhase = phase;
                NetProgress = p;
                NetNow = now is { } v ? Format.Number(v, "Mbit/s") : null;
            }), _netCancel.Token);
            NetResult.Add(new StatItem(L.T("Recebendo"), Format.Number(r.DownloadMbps, "Mbit/s"), L.T("{0} → este PC", _machine.Name)));
            NetResult.Add(new StatItem(L.T("Enviando"), Format.Number(r.UploadMbps, "Mbit/s"), L.T("este PC → {0}", _machine.Name)));
            NetResult.Add(new StatItem(L.T("Latência"), $"{r.LatencyMs.ToString("0.0", Format.Pt)} ms", L.T("variação {0:0.0} ms", r.JitterMs)));
            if (r.LinkMbps is { } link)
            {
                var best = Math.Max(r.DownloadMbps, r.UploadMbps);
                var pct = best / link * 100;
                NetResult.Add(new StatItem(L.T("Link deste PC"), Format.Number(link, "Mbit/s"), r.Adapter));
                var linkText = Format.Number(link, "Mbit/s");
                NetVerdict = pct >= 85
                    ? L.T("Rede no limite do cabo/placa: {0:0} % de {1} (o TCP/HTTPS usa uns 5–7 %).", pct, linkText)
                    : pct >= 50
                        ? L.T("Abaixo do possível: {0:0} % de {1}. Pode ser o disco/CPU do outro lado, Wi-Fi no caminho ou o switch.", pct, linkText)
                        : L.T("Bem abaixo do link ({0:0} % de {1}): confira cabos, Wi-Fi, a VPN ou se o outro PC está ocupado.", pct, linkText);
            }
            var all = ReadNetHistory();
            all.Insert(0, r);
            WriteNetHistory(all);
            LoadNetHistory();
        }
        catch (OperationCanceledException)
        {
            NetError = L.T("Teste cancelado.");
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            NetError = Outdated;
        }
        catch (Exception ex)
        {
            NetError = ex.Message;
        }
        finally
        {
            NetRunning = false;
            NetPhase = null;
            NetNow = null;
        }
    }
}
