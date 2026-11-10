using System.Diagnostics;
using System.Globalization;
using System.Text;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>
/// Acompanha por um tempo longo o estado de energia dos discos (comando ATA, que não acorda o disco) e a atividade
/// de leitura/escrita entre uma verificação e outra (contadores do Windows, que também não acordam).
/// Não consulta SMART, não lê espaço de volumes e não abre o LibreHardwareMonitor: só observa.
/// Responde: os HDDs chegam a dormir? Quanto tempo depois do último acesso? O que os acorda?
/// </summary>
public static class PowerWatch
{
    private sealed record Disk(int Number, string Name, string Media);

    private sealed class DiskTrack
    {
        public DiskPowerState? Last;
        public DateTimeOffset? LastIo;
        public DateTimeOffset? FirstStandby;
        public int Checks, StandbyChecks;
        public string Method = "";
        public readonly List<string> Wakes = new();
    }

    public static string Run(TimeSpan duration, TimeSpan interval, string outDir, Action<string> log, CancellationToken cancel)
    {
        var problems = new List<string>();
        var disks = new List<Disk>();
        SystemCollector.Try(problems, "MSFT_PhysicalDisk", () =>
        {
            foreach (var pd in Wmi.Query(Wmi.Storage, "SELECT DeviceId, FriendlyName, MediaType FROM MSFT_PhysicalDisk"))
                if (int.TryParse(Wmi.Str(pd, "DeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    disks.Add(new Disk(n, Wmi.Str(pd, "FriendlyName") ?? $"Disco {n}", StorageCollector.MediaTypeName(Wmi.Get<int?>(pd, "MediaType")) ?? "?"));
        });
        disks.Sort((a, b) => a.Number.CompareTo(b.Number));
        foreach (var p in problems) log("Aviso: " + p);

        Directory.CreateDirectory(outDir);
        var started = DateTimeOffset.Now;
        var baseName = Path.Combine(outDir, $"{Environment.MachineName}_energia_{started:yyyyMMdd-HHmmss}");
        var csvPath = baseName + ".csv";
        using (var csv = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
            csv.WriteLine("horario;disco;modelo;estado;metodo;lido_MB;escrito_MB;ativo_pct");

        var tracks = disks.ToDictionary(d => d.Number, _ => new DiskTrack());
        var previous = ReadDiskCounters();
        var lastTime = Stopwatch.GetTimestamp();
        log($"Acompanhando {disks.Count} discos por {duration.TotalMinutes:0} min, verificando a cada {interval.TotalSeconds:0} s. Ctrl+C encerra e gera o resumo.");
        log("Durante o teste, não abra pastas dos HDDs neste PC nem pela rede.");

        var end = started + duration;
        var first = true;
        while (!cancel.IsCancellationRequested && DateTimeOffset.Now < end)
        {
            if (!first) cancel.WaitHandle.WaitOne(interval);
            if (cancel.IsCancellationRequested) break;

            var now = DateTimeOffset.Now;
            var current = ReadDiskCounters();
            var seconds = Stopwatch.GetElapsedTime(lastTime).TotalSeconds;
            lastTime = Stopwatch.GetTimestamp();
            var line = new StringBuilder(now.ToString("HH:mm", CultureInfo.InvariantCulture));
            using var csv = new StreamWriter(csvPath, true, new UTF8Encoding(false));

            foreach (var d in disks)
            {
                var (state, method) = Native.GetDiskPowerState(d.Number);
                var t = tracks[d.Number];
                t.Method = method;
                double readMb = 0, writeMb = 0, active = 0;
                if (!first && previous.TryGetValue(d.Number, out var p) && current.TryGetValue(d.Number, out var c))
                {
                    readMb = CounterSample.Calculate(p.Read, c.Read) * seconds / 1e6;
                    writeMb = CounterSample.Calculate(p.Write, c.Write) * seconds / 1e6;
                    active = Math.Clamp(100 - CounterSample.Calculate(p.Idle, c.Idle), 0, 100);
                }
                var hadIo = readMb + writeMb > 0.01;
                if (hadIo) t.LastIo = now;

                t.Checks++;
                if (state == DiskPowerState.Standby)
                {
                    t.StandbyChecks++;
                    t.FirstStandby ??= now;
                }
                if (t.Last == DiskPowerState.Standby && state == DiskPowerState.On)
                {
                    t.Wakes.Add(hadIo
                        ? $"{now:HH:mm} acordou com acesso a arquivos ({readMb:0.##} MB lidos, {writeMb:0.##} MB escritos)"
                        : $"{now:HH:mm} acordou sem leitura/escrita registrada (consulta de outro programa: SMART, energia, antivírus...)");
                }
                t.Last = state;

                csv.WriteLine(string.Join(';', now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), d.Number, d.Name,
                    state, method, readMb.ToString("0.###", CultureInfo.InvariantCulture), writeMb.ToString("0.###", CultureInfo.InvariantCulture),
                    active.ToString("0.##", CultureInfo.InvariantCulture)));
                line.Append($"  D{d.Number} {Short(state)}{(hadIo ? $" ({readMb + writeMb:0.##} MB)" : "")}");
            }
            log(line.ToString());
            previous = current;
            first = false;
        }

        var md = Summary(disks, tracks, started, DateTimeOffset.Now, interval);
        File.WriteAllText(baseName + ".md", md, new UTF8Encoding(false));
        log("");
        log(md);
        log($"Gravado: {Path.GetFullPath(csvPath)}");
        log($"         {Path.GetFullPath(baseName)}.md");
        return baseName;
    }

    private static string Short(DiskPowerState s) => s switch { DiskPowerState.Standby => "repouso", DiskPowerState.On => "girando", _ => "?" };

    private static string Summary(List<Disk> disks, Dictionary<int, DiskTrack> tracks, DateTimeOffset start, DateTimeOffset end, TimeSpan interval)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Repouso dos discos — {Environment.MachineName}");
        sb.AppendLine();
        sb.AppendLine($"{start:dd/MM/yyyy HH:mm} → {end:HH:mm} ({(end - start).TotalMinutes:0} min), verificação a cada {interval.TotalSeconds:0} s.");
        sb.AppendLine();
        sb.AppendLine("| Disco | Modelo | Tipo | Método | Em repouso | Primeiro repouso | Último acesso a arquivos | Vezes que acordou |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var d in disks)
        {
            var t = tracks[d.Number];
            var pct = t.Checks > 0 ? 100.0 * t.StandbyChecks / t.Checks : 0;
            sb.AppendLine($"| {d.Number} | {d.Name} | {d.Media} | {t.Method} | {pct:0}% das verificações | " +
                          $"{(t.FirstStandby is { } fs ? fs.ToString("HH:mm", CultureInfo.InvariantCulture) : "nunca")} | " +
                          $"{(t.LastIo is { } io ? io.ToString("HH:mm", CultureInfo.InvariantCulture) : "nenhum")} | {t.Wakes.Count} |");
        }
        sb.AppendLine();
        foreach (var d in disks.Where(d => tracks[d.Number].Wakes.Count > 0))
        {
            sb.AppendLine($"**Disco {d.Number} ({d.Name})**");
            foreach (var w in tracks[d.Number].Wakes) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }
        if (disks.Any(d => tracks[d.Number].Method != "ATA" && d.Media == "HDD"))
            sb.AppendLine("Atenção: algum HDD não aceitou o comando ATA (rode como administrador). Pelo método do Windows o repouso pode não aparecer.");
        return sb.ToString();
    }

    private sealed record DiskCounters(CounterSample Read, CounterSample Write, CounterSample Idle);

    private static Dictionary<int, DiskCounters> ReadDiskCounters()
    {
        var result = new Dictionary<int, DiskCounters>();
        try
        {
            var data = new PerformanceCounterCategory("PhysicalDisk").ReadCategory();
            var read = data["Disk Read Bytes/sec"];
            var write = data["Disk Write Bytes/sec"];
            var idle = data["% Idle Time"];
            foreach (InstanceData inst in read.Values)
            {
                var name = inst.InstanceName;
                var space = name.IndexOf(' ');
                if (!int.TryParse(space > 0 ? name[..space] : name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
                if (write[name] is { } w && idle[name] is { } i)
                    result[n] = new DiskCounters(inst.Sample, w.Sample, i.Sample);
            }
        }
        catch (Exception)
        {
            // sem contadores: o estado de energia continua sendo registrado, só sem a atividade
        }
        return result;
    }
}
