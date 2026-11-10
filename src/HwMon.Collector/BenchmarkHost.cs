using System.Text.Json;
using HwMon.Core.Collection;
using HwMon.Core.Telemetry;

namespace HwMon.Collector;

/// <summary>
/// Um teste de disco por vez (em segundo plano), com estado para a barra de progresso e os resultados guardados em
/// %ProgramData%\HwMon\benchmarks.json para comparar depois.
/// </summary>
public sealed class BenchmarkHost(DataStore store, ILogger<BenchmarkHost> log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly DiskBenchStatus _status = new();
    private CancellationTokenSource? _cancel;

    private string HistoryPath => Path.Combine(store.Directory, "benchmarks.json");

    public DiskBenchStatus Status()
    {
        lock (_lock)
            return new DiskBenchStatus
            {
                Id = _status.Id, State = _status.State, Phase = _status.Phase, Progress = _status.Progress, Error = _status.Error, Result = _status.Result,
                Partial = _status.State == "running" ? _status.Partial : null,
                LiveMBs = _status.State == "running" ? _status.LiveMBs : null,
                LiveTest = _status.State == "running" ? _status.LiveTest : null,
                LiveWrite = _status.LiveWrite,
            };
    }

    /// <summary>Começa um teste. Falso se já há um rodando.</summary>
    public bool Start(DiskBenchRequest request, Func<string, string?> diskNameForDrive, out string id)
    {
        lock (_lock)
        {
            id = "";
            if (_status.State == "running" || !BenchGate.TryEnter("disco")) return false;
            id = Guid.NewGuid().ToString("N")[..12];
            _status.Id = id;
            _status.State = "running";
            _status.Phase = "Começando";
            _status.Progress = 0;
            _status.Error = null;
            _status.Result = null;
            _status.Partial = null;
            _status.LiveMBs = null;
            _status.LiveTest = null;
            _cancel = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        }
        var cancel = _cancel.Token;
        _ = Task.Run(async () =>
        {
            log.LogInformation("Benchmark de disco em {Drive} ({Size} MB)", request.Drive, request.SizeMb);
            try
            {
                var result = await DiskBenchmark.RunAsync(request, (phase, p) =>
                {
                    lock (_lock) { _status.Phase = phase; _status.Progress = p; }
                }, cancel, (lines, test, write, mbs) =>
                {
                    lock (_lock) { _status.Partial = lines; _status.LiveTest = test; _status.LiveWrite = write; _status.LiveMBs = mbs; }
                });
                result.DiskName = diskNameForDrive(result.Drive);
                Save(result);
                lock (_lock) { _status.State = "done"; _status.Result = result; _status.Progress = 1; }
            }
            catch (OperationCanceledException)
            {
                lock (_lock) { _status.State = "canceled"; _status.Phase = "Cancelado"; }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Benchmark de disco falhou");
                lock (_lock) { _status.State = "failed"; _status.Error = ex.Message; }
            }
            finally
            {
                BenchGate.Exit();
            }
        });
        return true;
    }

    public void Cancel()
    {
        lock (_lock) _cancel?.Cancel();
    }

    public List<DiskBenchResult> History()
    {
        try
        {
            return File.Exists(HistoryPath) ? JsonSerializer.Deserialize<List<DiskBenchResult>>(File.ReadAllText(HistoryPath)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var all = History();
            if (all.RemoveAll(r => r.Id == id) == 0) return false;
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(all, Json));
            return true;
        }
    }

    private void Save(DiskBenchResult result)
    {
        lock (_lock)
        {
            var all = History();
            all.Insert(0, result);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(all.Take(200).ToList(), Json));
        }
    }
}
