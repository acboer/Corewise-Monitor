using System.Diagnostics;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Collection.Bench;

/// <summary>
/// Teste de estabilidade: todas as threads rodam as cargas de <see cref="CpuWorkloads"/> sem parar, conferindo cada
/// resultado com o calculado no início (resultado diferente = erro de cálculo, sinal de CPU/memória instável), e
/// opcionalmente a placa de vídeo junto. Para sozinho se a CPU passar de 100 °C ou a GPU de 92 °C por 10 s.
/// O gráfico e a análise de throttling usam os sensores amostrados pelo coletor.
/// </summary>
public static class StressTest
{
    public const double CpuLimit = 100, GpuLimit = 92;
    private static readonly string[] Keys = ["float", "crypto", "sort", "compress"];

    /// <param name="temps">Temperatura atual da CPU e da GPU (para parar se esquentar demais).</param>
    /// <param name="gpuLoad">Carga da placa de vídeo (roda até o token cancelar), ou nulo.</param>
    public static Task<BenchResult> RunAsync(int minutes, Func<(double? Cpu, double? Gpu)> temps, Action<CancellationToken>? gpuLoad,
        Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
        => Task.Run(() => Run(minutes, temps, gpuLoad, progress, partial, cancel), cancel);

    private static BenchResult Run(int minutes, Func<(double? Cpu, double? Gpu)> temps, Action<CancellationToken>? gpuLoad,
        Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
    {
        minutes = Math.Clamp(minutes, 1, 240);
        var threads = Environment.ProcessorCount;
        var result = new BenchResult { Kind = "estabilidade", Detail = $"{minutes} min · {threads} threads" + (gpuLoad is null ? "" : " + placa de vídeo") };

        progress("Calculando os resultados de referência", 0);
        var expected = new Dictionary<string, ulong>();
        using (var w = new CpuWorkloads())
            foreach (var k in Keys) expected[k] = w.Run(k);

        long ops = 0;
        var errors = 0;
        string? firstError = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            using var w = new CpuWorkloads();
            var n = i;
            while (!stop.IsCancellationRequested)
            {
                var k = Keys[n++ % Keys.Length];
                if (w.Run(k) != expected[k])
                {
                    if (Interlocked.Increment(ref errors) == 1) firstError = $"{DateTime.Now:HH:mm:ss}: resultado errado em \"{k}\" na thread {i}";
                }
                Interlocked.Increment(ref ops);
            }
        }) { IsBackground = true }).ToList();
        foreach (var t in workers) t.Start();
        Thread? gpu = null;
        if (gpuLoad is not null)
        {
            gpu = new Thread(() =>
            {
                try { gpuLoad(stop.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { result.Notes.Add("A carga da placa de vídeo falhou: " + ex.Message); }
            }) { IsBackground = true };
            gpu.Start();
        }

        var clock = Stopwatch.StartNew();
        var total = TimeSpan.FromMinutes(minutes);
        int hotCpu = 0, hotGpu = 0;
        try
        {
            while (clock.Elapsed < total && !cancel.IsCancellationRequested)
            {
                Thread.Sleep(1000);
                var left = total - clock.Elapsed;
                progress($"Estressando · faltam {(int)left.TotalMinutes}:{left.Seconds:00} · {errors} erros", clock.Elapsed / total);
                partial(new BenchScore { Key = "errors", Label = "Erros de cálculo", Value = errors, Unit = "" });
                var (c, g) = temps();
                hotCpu = c >= CpuLimit ? hotCpu + 1 : 0;
                hotGpu = g >= GpuLimit ? hotGpu + 1 : 0;
                if (hotCpu >= 10) { result.StoppedReason = $"CPU passou de {CpuLimit:0} °C por 10 s"; break; }
                if (hotGpu >= 10) { result.StoppedReason = $"Placa de vídeo passou de {GpuLimit:0} °C por 10 s"; break; }
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var t in workers) t.Join();
            gpu?.Join(TimeSpan.FromSeconds(10));
        }
        if (cancel.IsCancellationRequested && result.StoppedReason is null) result.StoppedReason = "Cancelado";

        result.Seconds = Math.Round(clock.Elapsed.TotalSeconds);
        result.Errors = errors;
        result.Scores.Add(new BenchScore { Key = "duration", Label = "Duração", Value = Math.Round(clock.Elapsed.TotalMinutes, 1), Unit = "min", Detail = result.StoppedReason ?? "completo" });
        result.Scores.Add(new BenchScore { Key = "errors", Label = "Erros de cálculo", Value = errors, Unit = "", Detail = errors == 0 ? "estável" : firstError });
        result.Scores.Add(new BenchScore { Key = "ops", Label = "Operações", Value = ops, Unit = "", Detail = $"{ops / Math.Max(1, clock.Elapsed.TotalSeconds):N0} por segundo" });
        progress("Concluído", 1);
        return result;
    }

    /// <summary>Frases do resultado a partir dos sensores: temperatura máxima, clock no início × fim, consumo.</summary>
    public static void Analyze(BenchResult result)
    {
        var s = result.Samples;
        if (result.Errors == 0 && result.StoppedReason is null) result.Notes.Insert(0, "Nenhum erro de cálculo: estável neste teste.");
        else if (result.Errors > 0) result.Notes.Insert(0, $"{result.Errors} erros de cálculo: CPU ou memória instável (overclock, undervolt, EXPO/XMP ou temperatura).");
        if (s.Count < 30) return;
        string N(double v, string u) => v.ToString("N0") + " " + u;
        if (s.Max(x => x.CpuTemp) is { } ct) result.Notes.Add($"CPU chegou a {N(ct, "°C")}" + (ct >= 95 ? " (no limite do processador; Ryzen e Intel recentes trabalham assim e reduzem o clock para não passar)" : "") + ".");
        if (s.Max(x => x.GpuTemp) is { } gt && result.Detail?.Contains("placa") == true) result.Notes.Add($"Placa de vídeo chegou a {N(gt, "°C")}.");
        // Clock no 1º minuto (depois de 20 s) × último minuto.
        var first = s.Skip(20).Take(60).Select(x => x.CpuClock).OfType<double>().DefaultIfEmpty().Average();
        var last = s.TakeLast(60).Select(x => x.CpuClock).OfType<double>().DefaultIfEmpty().Average();
        if (first > 0 && last > 0 && s.Count > 140)
        {
            var drop = (first - last) / first * 100;
            result.Notes.Add(drop >= 5
                ? $"Clock caiu {drop:0} % do início ({first / 1000:0.00} GHz) para o fim ({last / 1000:0.00} GHz): throttling por temperatura ou consumo."
                : $"Clock estável: {first / 1000:0.00} GHz no início, {last / 1000:0.00} GHz no fim.");
        }
        if (s.Max(x => x.CpuPower) is { } cp) result.Notes.Add($"Consumo máximo da CPU: {N(cp, "W")}.");
    }
}
