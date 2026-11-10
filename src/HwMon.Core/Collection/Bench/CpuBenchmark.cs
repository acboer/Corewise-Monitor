using System.Diagnostics;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Collection.Bench;

/// <summary>
/// Teste de CPU: cada carga de <see cref="CpuWorkloads"/> roda alguns segundos em 1 thread e depois em todas.
/// Pontuação = média geométrica de (operações por segundo ÷ referência) × 1000, onde a referência é a workstation
/// (Ryzen 9 9950X) em 1 núcleo: ela dá ~1000 em 1 núcleo e o multi mostra quantos "núcleos da workstation" a
/// máquina vale. Só serve para comparar as suas máquinas (não é a escala do Cinebench/Geekbench).
/// </summary>
public static class CpuBenchmark
{
    /// <summary>Operações por segundo da workstation em 1 thread (calibrado em 08/11/2026).</summary>
    public static readonly Dictionary<string, double> Reference = new()
    {
        ["compress"] = 324, ["crypto"] = 951, ["float"] = 2977, ["sort"] = 260,
    };

    private const double Warmup = 0.5, SingleSeconds = 3, MultiSeconds = 3;

    public static Task<BenchResult> RunAsync(Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
        => Task.Run(() => Run(progress, partial, cancel), cancel);

    private static BenchResult Run(Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var threads = Environment.ProcessorCount;
        var result = new BenchResult { Kind = "cpu", Detail = $"{threads} threads" };
        var single = new Dictionary<string, double>();
        var multi = new Dictionary<string, double>();
        var steps = CpuWorkloads.All.Length * 2;
        var step = 0;

        foreach (var w in CpuWorkloads.All)
        {
            progress($"{w.Label} · 1 núcleo", (double)step++ / steps);
            single[w.Key] = Measure(w.Key, 1, SingleSeconds, cancel);
            partial(new BenchScore { Key = "1:" + w.Key, Label = $"{w.Label} (1 núcleo)", Value = Points(w.Key, single[w.Key]), Unit = "pts" });
        }
        foreach (var w in CpuWorkloads.All)
        {
            progress($"{w.Label} · {threads} threads", (double)step++ / steps);
            multi[w.Key] = Measure(w.Key, threads, MultiSeconds, cancel);
            partial(new BenchScore { Key = "n:" + w.Key, Label = $"{w.Label} (todos)", Value = Points(w.Key, multi[w.Key]), Unit = "pts" });
        }

        var s = Geo(single);
        var m = Geo(multi);
        result.Scores.Add(new BenchScore { Key = "single", Label = "1 núcleo", Value = Math.Round(s), Unit = "pts", Detail = "workstation ≈ 1000" });
        result.Scores.Add(new BenchScore { Key = "multi", Label = "Todos os núcleos", Value = Math.Round(m), Unit = "pts", Detail = $"{threads} threads" });
        result.Scores.Add(new BenchScore { Key = "scaling", Label = "Ganho com todos", Value = Math.Round(m / Math.Max(1, s), 1), Unit = "×", Detail = "multi ÷ 1 núcleo" });
        foreach (var w in CpuWorkloads.All)
            result.Scores.Add(new BenchScore
            {
                Key = w.Key, Label = w.Label, Value = Math.Round(Points(w.Key, single[w.Key])), Unit = "pts",
                Detail = $"todos: {Points(w.Key, multi[w.Key]):N0} pts · {w.What}",
            });
        result.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        progress("Concluído", 1);
        return result;
    }

    private static double Points(string key, double opsPerSecond) => opsPerSecond / Reference[key] * 1000;

    private static double Geo(Dictionary<string, double> ops)
        => Math.Exp(ops.Select(kv => Math.Log(Math.Max(1e-9, Points(kv.Key, kv.Value)))).Average());

    /// <summary>Operações por segundo com N threads (aquecimento descartado).</summary>
    public static double Measure(string key, int threads, double seconds, CancellationToken cancel)
    {
        long ops = 0;
        var start = new ManualResetEventSlim(false);
        var measuring = 0;
        var stopAt = 0L;
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            using var w = new CpuWorkloads();
            start.Wait();
            while (!cancel.IsCancellationRequested)
            {
                w.Run(key);
                if (Volatile.Read(ref measuring) == 1) Interlocked.Increment(ref ops);
                if (Stopwatch.GetTimestamp() >= Volatile.Read(ref stopAt) && stopAt != 0) break;
            }
        }) { IsBackground = true, Priority = ThreadPriority.Normal }).ToList();
        foreach (var t in workers) t.Start();
        Volatile.Write(ref stopAt, Stopwatch.GetTimestamp() + (long)((Warmup + seconds) * Stopwatch.Frequency) + Stopwatch.Frequency * 60);
        start.Set();
        Thread.Sleep(TimeSpan.FromSeconds(Warmup));
        var t0 = Stopwatch.GetTimestamp();
        Volatile.Write(ref measuring, 1);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Volatile.Write(ref measuring, 0);
        var elapsed = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        var counted = Interlocked.Read(ref ops);
        Volatile.Write(ref stopAt, 1); // todos param na próxima operação
        foreach (var t in workers) t.Join();
        cancel.ThrowIfCancellationRequested();
        return counted / elapsed;
    }
}
