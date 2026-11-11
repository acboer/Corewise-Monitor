using System.Text.Json;
using HwMon.Core.Collection.Bench;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.Collector;

/// <summary>Um teste pesado por vez na máquina (disco, CPU, memória, GPU ou estabilidade).</summary>
public static class BenchGate
{
    private static readonly object Lock = new();
    public static string? Running { get; private set; }

    public static bool TryEnter(string kind)
    {
        lock (Lock)
        {
            if (Running is not null) return false;
            Running = kind;
            return true;
        }
    }

    public static void Exit()
    {
        lock (Lock) Running = null;
    }
}

/// <summary>
/// Testes de CPU, memória, GPU e estabilidade: roda em segundo plano, guarda os sensores a cada segundo (clock,
/// temperatura e consumo de CPU e GPU) e os resultados em %ProgramData%\HwMon\benchmarks-carga.json.
/// </summary>
public sealed class LoadBenchHost(EngineHost host, DataStore store, ILogger<LoadBenchHost> log)
{
    public static readonly string[] Kinds = ["cpu", "memoria", "gpu", "estabilidade"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();
    private readonly Dictionary<string, BenchStatus> _status = new();
    private CancellationTokenSource? _cancel;

    private string HistoryPath => Path.Combine(store.Directory, "benchmarks-carga.json");

    public BenchStatus Status(string kind)
    {
        lock (_lock)
        {
            if (!_status.TryGetValue(kind, out var s)) return new BenchStatus { Kind = kind };
            return new BenchStatus
            {
                Id = s.Id, Kind = s.Kind, State = s.State, Phase = s.Phase, Progress = s.Progress, Error = s.Error, Result = s.Result,
                Partial = s.Partial?.ToList(), Samples = s.State == "running" ? s.Samples?.TakeLast(600).ToList() : null,
            };
        }
    }

    /// <summary>Começa um teste. Nulo se começou; senão, o motivo.</summary>
    public string? Start(BenchRequest request, out string id)
    {
        id = "";
        if (!Kinds.Contains(request.Kind)) return "Tipo de teste desconhecido.";
        if (!BenchGate.TryEnter(request.Kind)) return $"Já há um teste rodando nesta máquina ({BenchGate.Running}).";
        id = Guid.NewGuid().ToString("N")[..12];
        var status = new BenchStatus { Id = id, Kind = request.Kind, State = "running", Phase = "Começando", Partial = new(), Samples = new() };
        lock (_lock)
        {
            _status[request.Kind] = status;
            _cancel = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Clamp(request.Minutes, 1, 240) + 10));
        }
        var cancel = _cancel.Token;
        var runId = id;
        _ = Task.Run(async () =>
        {
            log.LogInformation("Teste de {Kind} começou", request.Kind);
            using var sampling = new CancellationTokenSource();
            var sampler = Task.Run(() => SampleAsync(status, sampling.Token));
            try
            {
                void Progress(string phase, double p) { lock (_lock) { status.Phase = phase; status.Progress = p; } }
                void Partial(BenchScore s) { lock (_lock) { status.Partial!.RemoveAll(x => x.Key == s.Key); status.Partial.Add(s); } }
                var result = request.Kind switch
                {
                    "cpu" => await CpuBenchmark.RunAsync(Progress, Partial, cancel),
                    "memoria" => await MemoryBenchmark.RunAsync(Progress, Partial, cancel),
                    "gpu" => await GpuBenchmark.RunAsync(Progress, Partial, cancel),
                    _ => await RunStressAsync(request, status, Progress, Partial, cancel),
                };
                sampling.Cancel();
                await sampler;
                Finish(result, status, request);
                Save(result);
                lock (_lock) { status.State = "done"; status.Result = result; status.Progress = 1; status.Phase = "Concluído"; }
                log.LogInformation("Teste de {Kind} terminou: {Scores}", request.Kind, string.Join(", ", result.Scores.Take(3).Select(s => $"{s.Label} {s.Value} {s.Unit}")));
            }
            catch (OperationCanceledException)
            {
                lock (_lock) { status.State = "canceled"; status.Phase = "Cancelado"; }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Teste de {Kind} falhou", request.Kind);
                lock (_lock) { status.State = "failed"; status.Error = ex.Message; }
            }
            finally
            {
                sampling.Cancel();
                BenchGate.Exit();
            }
        });
        return null;
    }

    private Task<BenchResult> RunStressAsync(BenchRequest request, BenchStatus status, Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
    {
        GpuBenchmark? gpu = null;
        if (request.Gpu)
        {
            try { gpu = new GpuBenchmark(); }
            catch (Exception ex) { log.LogWarning("Estabilidade sem a placa de vídeo: {Message}", ex.Message); }
        }
        (double?, double?) Temps()
        {
            lock (_lock)
            {
                var last = status.Samples?.LastOrDefault();
                return (last?.CpuTemp, last?.GpuTemp);
            }
        }
        var task = StressTest.RunAsync(request.Minutes, Temps, gpu is null ? null : (Action<CancellationToken>)gpu.Stress, progress, partial, cancel);
        return task.ContinueWith(t => { gpu?.Dispose(); return t; }, TaskScheduler.Default).Unwrap();
    }

    public void Cancel()
    {
        lock (_lock) _cancel?.Cancel();
    }

    /// <summary>Sensores a cada segundo: CPU (uso, clock, °C, W) e a placa de vídeo principal.</summary>
    private async Task SampleAsync(BenchStatus status, CancellationToken cancel)
    {
        string? gpuId = null;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                if (host.Engine is { } engine)
                {
                    var comps = engine.Descriptor().Components;
                    var values = engine.Snapshot().Values.Where(v => v.State == ReadingState.Ok && v.Value is not null)
                        .ToDictionary(v => (v.Component, v.Metric), v => v.Value!.Value);
                    var cpu = comps.FirstOrDefault(c => c.Kind == ComponentKind.Cpu)?.Id;
                    // Placa principal: a de mais VRAM (a dedicada).
                    gpuId ??= comps.Where(c => c.Kind == ComponentKind.Gpu)
                        .OrderByDescending(c => values.GetValueOrDefault((c.Id, "vram_total")))
                        .ThenByDescending(c => values.GetValueOrDefault((c.Id, "power"))).FirstOrDefault()?.Id;
                    double? V(string? id, string metric) => id is not null && values.TryGetValue((id, metric), out var v) ? Math.Round(v, 1) : null;
                    var sample = new BenchSample
                    {
                        T = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        CpuUsage = V(cpu, "usage"), CpuClock = V(cpu, "clock"), CpuTemp = V(cpu, "temp"), CpuPower = V(cpu, "power"),
                        GpuUsage = V(gpuId, "usage"), GpuClock = V(gpuId, "clock"), GpuTemp = V(gpuId, "temp"), GpuPower = V(gpuId, "power"),
                    };
                    lock (_lock) status.Samples?.Add(sample);
                }
                await Task.Delay(1000, cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // fim do teste
        }
    }

    private void Finish(BenchResult result, BenchStatus status, BenchRequest request)
    {
        result.Id = status.Id ?? result.Id;
        result.Time = DateTimeOffset.Now;
        List<BenchSample> samples;
        lock (_lock) samples = status.Samples?.ToList() ?? new();
        result.Samples = samples;
        var engine = host.Engine;
        if (result.Device is null && engine is not null)
            result.Device = engine.Descriptor().Components.FirstOrDefault(c => c.Kind == ComponentKind.Cpu)?.Name;

        if (request.Kind == "estabilidade") StressTest.Analyze(result);
        else if (samples.Count > 0)
        {
            var gpu = request.Kind == "gpu";
            var temps = samples.Select(s => gpu ? s.GpuTemp : s.CpuTemp).OfType<double>().ToList();
            var clocks = samples.Select(s => gpu ? s.GpuClock : s.CpuClock).OfType<double>().Where(c => c > 0).ToList();
            var power = samples.Select(s => gpu ? s.GpuPower : s.CpuPower).OfType<double>().ToList();
            var parts = new List<string>();
            if (temps.Count > 0) parts.Add($"até {temps.Max():0} °C");
            if (clocks.Count > 0) parts.Add($"clock médio {clocks.Average() / 1000:0.00} GHz");
            if (power.Count > 0) parts.Add($"até {power.Max():0} W");
            if (parts.Count > 0) result.Notes.Add($"Durante o teste, {(gpu ? "a placa" : "a CPU")}: {string.Join(", ", parts)}.");
        }
        // Guarda no máximo ~720 pontos (estabilidade de 1 h vira um ponto a cada 5 s).
        if (result.Samples.Count > 720)
        {
            var step = (int)Math.Ceiling(result.Samples.Count / 720.0);
            result.Samples = result.Samples.Where((_, i) => i % step == 0).ToList();
        }
    }

    public List<BenchResult> History(string? kind)
    {
        try
        {
            var all = SafeFile.ReadJson<List<BenchResult>>(HistoryPath, Json) ?? new();
            return kind is null ? all : all.Where(r => r.Kind == kind).ToList();
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
            var all = History(null);
            if (all.RemoveAll(r => r.Id == id) == 0) return false;
            SafeFile.WriteAllText(HistoryPath, JsonSerializer.Serialize(all, Json));
            return true;
        }
    }

    private void Save(BenchResult result)
    {
        lock (_lock)
        {
            var all = History(null);
            all.Insert(0, result);
            // Até 50 por tipo.
            var kept = all.GroupBy(r => r.Kind).SelectMany(g => g.Take(50)).OrderByDescending(r => r.Time).ToList();
            SafeFile.WriteAllText(HistoryPath, JsonSerializer.Serialize(kept, Json));
        }
    }
}
