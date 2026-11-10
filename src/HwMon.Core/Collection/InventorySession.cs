using System.Diagnostics;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

public sealed record InventoryOptions(string Scenario, int Seconds, double IntervalSeconds, bool SyntheticCpuLoad = false)
{
    /// <summary>Chamado logo antes da primeira amostra (ex.: iniciar carga sintética).</summary>
    public Action? OnSamplingStart { get; init; }
    /// <summary>Chamado logo após a última amostra.</summary>
    public Action? OnSamplingEnd { get; init; }
}

/// <summary>
/// Uma captura completa: inventário, leituras lentas (SMART/volumes) no início e no fim, e amostras
/// periódicas de todas as fontes no meio. Registra o estado de energia dos discos entre as fases
/// para descobrir se alguma consulta acorda HDDs em repouso.
/// </summary>
public static class InventorySession
{
    public static InventoryReport Run(InventoryOptions options, Action<string> log, CancellationToken cancel = default)
    {
        var process = Process.GetCurrentProcess();
        var total = Stopwatch.StartNew();
        var problems = new List<string>();

        log("Identificando a máquina...");
        var report = new InventoryReport
        {
            System = SystemCollector.CollectSystem(problems),
            Scenario = options.Scenario,
            StartedAt = DateTimeOffset.Now,
            IntervalSeconds = options.IntervalSeconds,
            SyntheticCpuLoad = options.SyntheticCpuLoad,
        };
        report.Cpus.AddRange(SystemCollector.CollectCpus(problems));
        report.Memory = SystemCollector.CollectMemory(problems);
        report.Gpus.AddRange(SystemCollector.CollectGpus(problems));

        log("Inventariando discos, volumes e pools...");
        var storage = new StorageCollector(problems);
        storage.Discover(); // grava PowerBefore antes de qualquer consulta SMART
        report.PhysicalDisks.AddRange(storage.PhysicalDisks);
        report.Volumes.AddRange(storage.Volumes);
        report.StoragePools.AddRange(storage.Pools);

        log("Inventariando adaptadores de rede...");
        var network = new NetworkCollector(problems);
        network.Discover();
        report.NetworkAdapters.AddRange(network.Adapters);

        var aggregator = new MetricAggregator();
        log("Lendo SMART via WMI (início)...");
        aggregator.AddRange(storage.SampleSlow());
        storage.RecordPower((d, s) => d.PowerAfterWmiSmart = s);

        log("Abrindo LibreHardwareMonitor...");
        var lhmWatch = Stopwatch.StartNew();
        using var lhm = new LhmCollector(problems);
        lhm.Open();
        lhm.MapIdentities(report.Gpus, report.PhysicalDisks);
        var lhmOpen = lhmWatch.Elapsed.TotalSeconds;
        storage.RecordPower((d, s) => d.PowerAfterLhm = s);

        var perf = new PerfCounterCollector(report.Gpus, problems);
        perf.Prime();
        _ = network.Sample().ToList(); // primeira amostra só define a base das taxas
        var init = total.Elapsed.TotalSeconds;

        var ticks = Math.Max(1, (int)Math.Round(options.Seconds / options.IntervalSeconds));
        var interval = TimeSpan.FromSeconds(options.IntervalSeconds);
        var tickTimes = new List<double>(ticks);
        var sourceTimes = new Dictionary<string, double>();
        void Timed(string source, Func<IEnumerable<Reading>> sample)
        {
            var sw = Stopwatch.StartNew();
            aggregator.AddRange(sample()); // enumera aqui: os coletores são preguiçosos
            sourceTimes[source] = sourceTimes.GetValueOrDefault(source) + sw.Elapsed.TotalMilliseconds;
        }
        var cpuBefore = process.TotalProcessorTime;
        var loopWatch = Stopwatch.StartNew();

        options.OnSamplingStart?.Invoke();
        try
        {
            log($"Capturando {ticks} amostras a cada {options.IntervalSeconds:0.##} s (cenário: {options.Scenario})...");
            var next = Stopwatch.GetTimestamp();
            for (var i = 0; i < ticks && !cancel.IsCancellationRequested; i++)
            {
                next += (long)(interval.TotalSeconds * Stopwatch.Frequency);
                var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
                if (wait > TimeSpan.Zero) cancel.WaitHandle.WaitOne(wait);

                var tick = Stopwatch.StartNew();
                Timed("perf", () => perf.Sample());
                Timed("win32", SystemCollector.SampleMemory);
                Timed("net", () => network.Sample());
                Timed("lhm", () => lhm.Sample());
                tickTimes.Add(tick.Elapsed.TotalMilliseconds);
                report.SampleCount++;
                if ((i + 1) % 10 == 0 || i + 1 == ticks) log($"  {i + 1}/{ticks}");
            }
        }
        finally
        {
            options.OnSamplingEnd?.Invoke();
        }

        var loopSeconds = loopWatch.Elapsed.TotalSeconds;
        var cpuUsed = (process.TotalProcessorTime - cpuBefore).TotalSeconds;

        log("Lendo SMART via WMI (fim)...");
        aggregator.AddRange(storage.SampleSlow());

        report.Metrics.AddRange(aggregator.Build());
        report.LhmSensors.AddRange(lhm.SensorInventory());
        report.Problems.AddRange(problems.Distinct());
        process.Refresh();
        report.Cost = new CollectorCost
        {
            InitSeconds = Math.Round(init, 2),
            LhmOpenSeconds = Math.Round(lhmOpen, 2),
            AvgTickMs = tickTimes.Count > 0 ? Math.Round(tickTimes.Average(), 1) : 0,
            MaxTickMs = tickTimes.Count > 0 ? Math.Round(tickTimes.Max(), 1) : 0,
            AvgTickMsBySource = sourceTimes.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / Math.Max(1, tickTimes.Count), 1))
                .Concat(lhm.AvgUpdateMsByHardware()).ToDictionary(),
            ProcessCpuPercent = loopSeconds > 0 ? Math.Round(cpuUsed / loopSeconds / Environment.ProcessorCount * 100, 2) : 0,
            WorkingSetMb = Math.Round(process.WorkingSet64 / 1_048_576.0, 1),
            PeakWorkingSetMb = Math.Round(process.PeakWorkingSet64 / 1_048_576.0, 1),
        };
        report.FinishedAt = DateTimeOffset.Now;
        return report;
    }
}
