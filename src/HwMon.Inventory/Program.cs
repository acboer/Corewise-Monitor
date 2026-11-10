using System.Diagnostics;
using System.Globalization;
using System.Text;
using HwMon.Core.Collection;
using HwMon.Core.Reporting;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

try
{
    return args switch
    {
        ["--estresse", var seconds] => Stress(int.Parse(seconds, CultureInfo.InvariantCulture)),
        ["matriz", .. var rest] => Compare(rest),
        ["energia", .. var rest] => Power(rest),
        ["ajuda" or "--help" or "-h" or "/?"] => Help(),
        ["coletar", .. var rest] => Collect(rest),
        _ => Collect(args),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Erro: {ex.Message}");
    Console.Error.WriteLine();
    Help();
    return 2;
}

static int Collect(string[] args)
{
    var scenario = Option(args, "--cenario") ?? "repouso";
    var seconds = int.Parse(Option(args, "--segundos") ?? "20", CultureInfo.InvariantCulture);
    var interval = double.Parse(Option(args, "--intervalo") ?? "1", CultureInfo.InvariantCulture);
    var outDir = Option(args, "--saida") ?? "inventario";
    var cpuLoad = args.Contains("--carga-cpu");
    if (seconds < 1 || interval <= 0) throw new ArgumentException("--segundos e --intervalo precisam ser positivos.");

    Process? stress = null;
    var options = new InventoryOptions(scenario, seconds, interval, cpuLoad)
    {
        OnSamplingStart = cpuLoad ? () => stress = StartStress(seconds + 5) : null,
        OnSamplingEnd = () => { try { stress?.Kill(); } catch (InvalidOperationException) { } },
    };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("Interrompendo; o relatório será gravado com as amostras já feitas."); };

    var report = InventorySession.Run(options, Console.WriteLine, cts.Token);

    Directory.CreateDirectory(outDir);
    var safeScenario = string.Concat(scenario.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c));
    var baseName = Path.Combine(outDir, $"{report.System.HostName}_{safeScenario}_{report.StartedAt:yyyyMMdd-HHmmss}");
    File.WriteAllText(baseName + ".json", ReportJson.Serialize(report), new UTF8Encoding(false));
    File.WriteAllText(baseName + ".md", MatrixBuilder.RenderSingle(report), new UTF8Encoding(false));

    Console.WriteLine();
    var rows = MatrixBuilder.BuildRows(report).Where(r => r.Expected).ToList();
    Console.WriteLine($"Métricas esperadas com leitura: {rows.Count(r => r.Available)}/{rows.Count}");
    foreach (var missing in rows.Where(r => !r.Available))
        Console.WriteLine($"  sem leitura: {MatrixBuilder.ComponentLabels.GetValueOrDefault(missing.Component)} · {missing.InstanceLabel} · {MatrixBuilder.MetricLabels.GetValueOrDefault(missing.Metric, missing.Metric)}");
    foreach (var w in MatrixBuilder.Warnings(report))
        Console.WriteLine($"Aviso: {w.Replace("**", "")}");
    Console.WriteLine();
    Console.WriteLine($"Gravado: {Path.GetFullPath(baseName)}.json");
    Console.WriteLine($"         {Path.GetFullPath(baseName)}.md");
    return 0;
}

static int Compare(string[] args)
{
    var outFile = Option(args, "--saida") ?? Path.Combine("inventario", "matriz.md");
    var files = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--saida") { i++; continue; }
        if (Directory.Exists(args[i])) files.AddRange(Directory.GetFiles(args[i], "*.json").Order());
        else files.Add(args[i]);
    }
    if (files.Count == 0) throw new ArgumentException("informe arquivos .json ou uma pasta com eles.");

    var reports = files.Select(ReportJson.Load).OrderBy(r => r.System.HostName).ThenBy(r => r.StartedAt).ToList();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
    File.WriteAllText(outFile, MatrixBuilder.RenderComparison(reports), new UTF8Encoding(false));
    Console.WriteLine($"Matriz de {reports.Count} capturas gravada em {Path.GetFullPath(outFile)}");
    return 0;
}

static int Power(string[] args)
{
    var minutes = double.Parse(Option(args, "--minutos") ?? "60", CultureInfo.InvariantCulture);
    var interval = double.Parse(Option(args, "--intervalo") ?? "60", CultureInfo.InvariantCulture);
    var outDir = Option(args, "--saida") ?? "inventario";
    if (minutes <= 0 || interval < 5) throw new ArgumentException("--minutos precisa ser positivo e --intervalo pelo menos 5 s.");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    PowerWatch.Run(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(interval), outDir, Console.WriteLine, cts.Token);
    return 0;
}

static Process StartStress(int seconds)
{
    var self = Environment.ProcessPath ?? throw new InvalidOperationException("caminho do executável desconhecido");
    var p = Process.Start(new ProcessStartInfo(self, $"--estresse {seconds}") { UseShellExecute = false, CreateNoWindow = true })!;
    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
    return p;
}

static int Stress(int seconds)
{
    // Carga sintética em todos os núcleos, separada do processo que mede (não distorce o custo da coleta).
    var end = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
    Parallel.For(0, Environment.ProcessorCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, _ =>
    {
        double x = 1;
        while (Stopwatch.GetTimestamp() < end)
            for (var i = 0; i < 1_000_000; i++) x = Math.Sqrt(x + i);
        GC.KeepAlive(x);
    });
    return 0;
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    if (i < 0) return null;
    if (i + 1 >= args.Length) throw new ArgumentException($"{name} precisa de um valor.");
    return args[i + 1];
}

static int Help()
{
    Console.WriteLine("""
        HwMon.Inventory — inventário e validação da telemetria (primeiro ticket)

        Coletar (rode como administrador):
          HwMon.Inventory [coletar] [--cenario repouso|carga|copia] [--segundos 20] [--intervalo 1]
                                    [--carga-cpu] [--saida inventario]

            --cenario    nome livre do cenário; vai no nome dos arquivos
            --carga-cpu  gera carga em todos os núcleos durante a captura
            Saída: <PC>_<cenario>_<data>.json (dados completos) e .md (matriz legível)

        Acompanhar o repouso dos discos (só observa; não lê SMART):
          HwMon.Inventory energia [--minutos 60] [--intervalo 60] [--saida inventario]

        Comparar capturas (vários PCs/cenários):
          HwMon.Inventory matriz <pasta ou arquivos .json...> [--saida inventario\matriz.md]
        """);
    return 0;
}
