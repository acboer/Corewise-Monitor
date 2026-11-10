using System.Globalization;
using System.Text;
using HwMon.Core.Collection;
using HwMon.Core.Model;

namespace HwMon.Core.Reporting;

public sealed record MatrixCell(string Source, MetricSummary Summary);

public sealed record MatrixRow(string Component, string Instance, string InstanceLabel, string Metric, List<MatrixCell> Cells, bool Expected)
{
    public MatrixCell? Best { get; init; }
    public bool Available => Best is not null;
}

/// <summary>
/// Monta a matriz "métrica × fonte" de uma captura e a comparação entre PCs/cenários, em Markdown.
/// </summary>
public static class MatrixBuilder
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] SourceOrder = ["perf", "win32", "wmi", "net", "lhm"];

    private static readonly Dictionary<string, string[]> Expected = new()
    {
        ["cpu"] = ["usage", "clock", "temp", "power"],
        ["ram"] = ["used", "available", "total"],
        ["gpu"] = ["usage", "clock", "temp", "vram_used", "vram_total"],
        ["disk"] = ["active", "read", "write", "temp", "health", "wear"],
        ["vol"] = ["used", "free", "total"],
        ["net"] = ["rx", "tx", "rx_total", "tx_total"],
    };

    private static readonly HashSet<string> PreferLhm = HwMon.Core.Telemetry.MetricPolicy.PreferLhm;

    public static readonly Dictionary<string, string> ComponentLabels = new()
    {
        ["cpu"] = "CPU", ["ram"] = "RAM", ["gpu"] = "GPU", ["disk"] = "Disco", ["vol"] = "Volume", ["net"] = "Rede", ["fan"] = "Ventoinha",
    };

    public static readonly Dictionary<string, string> MetricLabels = new()
    {
        ["usage"] = "Uso", ["clock"] = "Clock", ["clock_max"] = "Clock máx. (núcleo)", ["temp"] = "Temperatura",
        ["temp_hotspot"] = "Temperatura hot spot", ["power"] = "Consumo", ["used"] = "Usado", ["available"] = "Disponível",
        ["total"] = "Total", ["load"] = "Carga", ["vram_used"] = "VRAM usada", ["vram_total"] = "VRAM total",
        ["active"] = "Tempo ativo", ["read"] = "Leitura", ["write"] = "Escrita", ["health"] = "Saúde", ["wear"] = "Desgaste",
        ["power_on_hours"] = "Horas ligado", ["free"] = "Livre", ["rx"] = "Recebendo", ["tx"] = "Enviando",
        ["rx_total"] = "Recebido (acumulado)", ["tx_total"] = "Enviado (acumulado)", ["fan"] = "Ventoinha", ["rpm"] = "Rotação",
    };

    public static List<MatrixRow> BuildRows(InventoryReport r)
    {
        var rows = new List<MatrixRow>();
        var byInstance = r.Metrics.GroupBy(m => (m.Component, m.Instance)).ToDictionary(g => g.Key, g => g.ToList());

        // Instâncias esperadas a partir do inventário (para que ausências apareçam como linhas ❌).
        var instances = new List<(string Component, string Instance)>();
        instances.AddRange(r.Cpus.Select(c => ("cpu", c.Index.ToString(CultureInfo.InvariantCulture))));
        instances.Add(("ram", "0"));
        instances.AddRange(r.Gpus.Select(g => ("gpu", g.Index.ToString(CultureInfo.InvariantCulture))));
        instances.AddRange(r.PhysicalDisks.Select(d => ("disk", StorageCollector.DiskKey(d))));
        instances.AddRange(r.Volumes.Select(v => ("vol", v.Key)));
        instances.AddRange(r.NetworkAdapters.Where(a => a.Status == "Up").Select(a => ("net", a.Name)));
        foreach (var key in byInstance.Keys.Where(k => !instances.Contains(k)))
            instances.Add(key);

        foreach (var (component, instance) in instances)
        {
            var metrics = byInstance.GetValueOrDefault((component, instance)) ?? [];
            var expected = Expected.GetValueOrDefault(component) ?? [];
            if (component == "ram" && instance != "0") expected = ["temp"];
            // Não se aplica: desgaste em HDD (é métrica de SSD); temperatura/VRAM própria em GPU integrada.
            if (component == "disk" && r.PhysicalDisks.FirstOrDefault(d => StorageCollector.DiskKey(d) == instance)?.MediaType == "HDD")
                expected = expected.Where(m => m != "wear").ToArray();
            if (component == "gpu" && int.TryParse(instance, out var gi) && gi < r.Gpus.Count && (r.Gpus[gi].Integrated || r.Gpus[gi].DedicatedMemoryGb < 1))
                expected = expected.Where(m => m is not ("temp" or "vram_total")).ToArray();
            var names = expected.Concat(metrics.Select(m => m.Metric).Where(m => !expected.Contains(m)).Distinct().OrderBy(m => m));

            foreach (var metric in names)
            {
                var cells = metrics.Where(m => m.Metric == metric)
                    .OrderBy(m => Array.IndexOf(SourceOrder, m.Source) is var i and >= 0 ? i : 99)
                    .Select(m => new MatrixCell(m.Source, m)).ToList();
                var ordered = PreferLhm.Contains($"{component}.{metric}")
                    ? cells.OrderBy(c => c.Source == "lhm" ? 0 : 1).ToList()
                    : cells;
                rows.Add(new MatrixRow(component, instance, InstanceLabel(r, component, instance), metric, cells, expected.Contains(metric))
                {
                    Best = ordered.FirstOrDefault(c => c.Summary.Ok > 0),
                });
            }
        }

        // Temperatura da RAM: se nenhum módulo expôs sensor, registrar a ausência explicitamente.
        if (!rows.Any(x => x.Component == "ram" && x.Metric == "temp"))
            rows.Insert(rows.FindLastIndex(x => x.Component == "ram") + 1,
                new MatrixRow("ram", "módulos", "Módulos de memória", "temp", [], true));

        return rows;
    }

    public static string InstanceLabel(InventoryReport r, string component, string instance)
    {
        switch (component)
        {
            case "cpu" when int.TryParse(instance, out var i) && i < r.Cpus.Count:
                return r.Cpus[i].Name;
            case "gpu" when int.TryParse(instance, out var g) && g < r.Gpus.Count:
                return r.Gpus[g].Name;
            case "disk":
                var d = r.PhysicalDisks.FirstOrDefault(x => StorageCollector.DiskKey(x) == instance);
                return d is null ? instance : $"Disco {instance} — {d.FriendlyName} ({d.MediaType}, {d.BusType})";
            case "vol":
                var v = r.Volumes.FirstOrDefault(x => x.Key == instance);
                if (v is null) return instance;
                var label = $"{(v.DriveLetter is null ? "" : v.DriveLetter + ": ")}{v.Label}".Trim();
                return label.Length > 0 ? label : $"sem letra ({v.Key}…)";
            case "ram" when instance == "0":
                return "Memória total";
            default:
                return instance;
        }
    }

    // ------------------------------------------------------------------ uma captura

    public static string RenderSingle(InventoryReport r)
    {
        var sb = new StringBuilder();
        var s = r.System;
        sb.AppendLine($"# Inventário de telemetria — {s.HostName}");
        sb.AppendLine();
        sb.AppendLine($"Cenário **{r.Scenario}** · {r.StartedAt.ToString("dd/MM/yyyy HH:mm", Pt)} · {r.SampleCount} amostras a cada {Fmt(r.IntervalSeconds)} s" +
                      (r.SyntheticCpuLoad ? " · carga sintética de CPU ligada" : ""));
        sb.AppendLine();
        sb.AppendLine("| Item | Valor |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Máquina | {s.Manufacturer} {s.Model} · placa {s.Motherboard} |");
        sb.AppendLine($"| Windows | {s.OsCaption} · build {s.OsBuild} |");
        sb.AppendLine($"| ID estável (MachineGuid) | `{s.MachineId}` |");
        sb.AppendLine($"| Executado como | {s.User}{(s.IsLocalSystem ? " (LocalSystem)" : "")} · administrador: **{SimNao(s.IsElevated)}** |");
        sb.AppendLine($"| Integridade de Memória (HVCI) | {(s.HvciRunning is null ? "?" : SimNao(s.HvciRunning.Value))} |");
        sb.AppendLine($"| Driver PawnIO instalado | **{SimNao(s.PawnIoInstalled)}** |");
        sb.AppendLine($"| LibreHardwareMonitor | {s.LhmVersion} |");
        sb.AppendLine();

        var warnings = Warnings(r).ToList();
        if (warnings.Count > 0)
        {
            sb.AppendLine("## Avisos");
            sb.AppendLine();
            foreach (var w in warnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }

        sb.AppendLine("## Matriz de sensores");
        sb.AppendLine();
        sb.AppendLine("Valores = média (mín–máx) durante a captura. ✅ lido · ⚠️ erro na leitura · ❌ fonte não oferece · ➖ métrica não se aplica (ex.: desgaste em HDD) · — fonte não se aplica.");
        sb.AppendLine("Fontes: **perf** contadores do Windows · **win32** API do Windows · **wmi** Storage Management · **net** estatísticas de rede · **lhm** LibreHardwareMonitor.");
        sb.AppendLine();
        sb.AppendLine("| | Componente | Métrica | perf/win32/wmi/net | lhm | Usar |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var row in BuildRows(r))
        {
            var native = row.Cells.Where(c => c.Source != "lhm").ToList();
            var lhm = row.Cells.FirstOrDefault(c => c.Source == "lhm");
            var status = row.Available ? "✅" : !row.Expected ? "➖" : row.Cells.Any(c => c.Summary.Errors > 0) ? "⚠️" : "❌";
            sb.AppendLine($"| {status} | {ComponentLabels.GetValueOrDefault(row.Component, row.Component)} · {Esc(row.InstanceLabel)} | {MetricLabel(row.Metric)} | " +
                          $"{(native.Count == 0 ? "—" : string.Join("<br>", native.Select(CellText)))} | {(lhm is null ? "—" : CellText(lhm))} | " +
                          $"{(row.Best is not null ? row.Best.Source : row.Expected ? "**nenhuma**" : "não se aplica")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Discos físicos");
        sb.AppendLine();
        sb.AppendLine("| Nº | Modelo | Tipo | Barramento | Tamanho | Saúde | Pool | ID estável | SMART via WMI | Par no LHM | Energia (antes → WMI → LHM) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var d in r.PhysicalDisks)
        {
            sb.AppendLine($"| {d.Number} | {Esc(d.FriendlyName)} | {d.MediaType} | {d.BusType} | {Fmt(d.SizeGb)} GB | {d.Health} | {d.Pool ?? "—"} | `{d.StableId}` | " +
                          $"{(d.ReliabilityAvailable ? "sim" : "não: " + d.ReliabilityError)} | {(d.LhmIdentifier is null ? "não" : $"{d.LhmIdentifier} ({d.LhmMatch})")} | " +
                          $"{Power(d.PowerBefore)} → {Power(d.PowerAfterWmiSmart)} → {Power(d.PowerAfterLhm)}{(d.PowerMethod is null ? "" : $" ({d.PowerMethod})")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Volumes → discos");
        sb.AppendLine();
        sb.AppendLine("| Volume | Sistema de arquivos | Tamanho | Livre | Saúde | Discos |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var v in r.Volumes)
            sb.AppendLine($"| {Esc(InstanceLabel(r, "vol", v.Key))} | {v.FileSystem} | {Fmt(v.SizeGb)} GB | {Fmt(v.FreeGb)} GB | {v.Health} | {(v.DiskNumbers.Count == 0 ? "?" : string.Join(", ", v.DiskNumbers))} |");
        sb.AppendLine();

        if (r.StoragePools.Count > 0)
        {
            sb.AppendLine("## Pools (Storage Spaces)");
            sb.AppendLine();
            foreach (var p in r.StoragePools)
            {
                sb.AppendLine($"- **{Esc(p.Name)}** · {p.Health} · {Fmt(p.AllocatedGb)} de {Fmt(p.SizeGb)} GB · membros: {string.Join(", ", p.MemberDiskStableIds)}");
                foreach (var vd in p.VirtualDisks)
                    sb.AppendLine($"  - disco virtual {Esc(vd.Name)} ({vd.Resiliency}, {vd.Health}) → disco {vd.DiskNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Rede");
        sb.AppendLine();
        sb.AppendLine("| Adaptador | Descrição | Tipo | Físico | Estado | Velocidade | IPv4 | Gateway |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var a in r.NetworkAdapters)
            sb.AppendLine($"| {Esc(a.Name)} | {Esc(a.Description)} | {a.Type} | {(a.Physical is null ? "?" : SimNao(a.Physical.Value))} | {a.Status} | {Fmt(a.SpeedMbps)} Mbit/s | {string.Join(", ", a.IPv4)} | {string.Join(", ", a.Gateways)} |");
        sb.AppendLine();

        var unmapped = r.LhmSensors.Where(x => !x.Mapped).ToList();
        sb.AppendLine("## LibreHardwareMonitor");
        sb.AppendLine();
        sb.AppendLine($"{r.LhmSensors.Count} sensores expostos, {r.LhmSensors.Count - unmapped.Count} usados na matriz. Os demais estão no JSON (`lhmSensors`). Resumo por hardware:");
        sb.AppendLine();
        foreach (var g in r.LhmSensors.GroupBy(x => (x.HardwareType, x.Hardware)))
            sb.AppendLine($"- **{g.Key.HardwareType}** {Esc(g.Key.Hardware)}: " + string.Join(", ", g.GroupBy(x => x.SensorType).Select(t => $"{t.Count()} {t.Key}")));
        sb.AppendLine();

        if (r.Cost is { } c)
        {
            sb.AppendLine("## Custo da coleta");
            sb.AppendLine();
            sb.AppendLine($"Inicialização {Fmt(c.InitSeconds)} s (LHM {Fmt(c.LhmOpenSeconds)} s) · amostra média {Fmt(c.AvgTickMs)} ms (máx. {Fmt(c.MaxTickMs)} ms) · " +
                          $"CPU do processo {Fmt(c.ProcessCpuPercent)} % · memória {Fmt(c.WorkingSetMb)} MB (pico {Fmt(c.PeakWorkingSetMb)} MB).");
            if (c.AvgTickMsBySource.Count > 0)
                sb.AppendLine($"Tempo médio por amostra, por fonte: {string.Join(" · ", c.AvgTickMsBySource.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {Fmt(kv.Value)} ms"))}.");
            sb.AppendLine();
        }

        if (r.Problems.Count > 0)
        {
            sb.AppendLine("## Problemas de coleta");
            sb.AppendLine();
            foreach (var p in r.Problems) sb.AppendLine($"- {Esc(p)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static IEnumerable<string> Warnings(InventoryReport r)
    {
        var s = r.System;
        if (!s.IsElevated)
            yield return "Executado **sem administrador**: SMART via WMI e a maior parte dos sensores do LHM ficam indisponíveis. Rode de novo como administrador (o serviço final roda como LocalSystem).";
        if (!s.PawnIoInstalled)
            yield return "Driver **PawnIO não instalado**: o LHM 0.9.x depende dele para temperatura/clock da CPU, sensores da placa-mãe e SPD da RAM. Instale o PawnIO (pawnio.eu) e repita.";
        foreach (var d in r.PhysicalDisks)
        {
            if (d.PowerBefore == DiskPowerState.Standby && d.PowerAfterWmiSmart == DiskPowerState.On)
                yield return $"Disco {d.Number} ({d.FriendlyName}) estava em repouso e **acordou com a consulta SMART via WMI**.";
            else if (d.PowerAfterWmiSmart == DiskPowerState.Standby && d.PowerAfterLhm == DiskPowerState.On)
                yield return $"Disco {d.Number} ({d.FriendlyName}) estava em repouso e **acordou ao abrir o LHM**.";
            if (d.LhmMatch?.Contains("AMBÍGUO", StringComparison.Ordinal) == true)
                yield return $"Disco {d.Number}: par no LHM escolhido por nome com discos idênticos — confira as temperaturas pelo número de série.";
        }
        var virtualUp = r.NetworkAdapters.Where(a => a.Status == "Up" && a.Physical == false && a.Gateways.Count == 0 && a.IPv4.Count > 0).ToList();
        var hyperV = virtualUp.Where(a => a.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)).ToList();
        var tunnels = virtualUp.Except(hyperV).ToList();
        if (tunnels.Count > 0 && r.NetworkAdapters.Any(a => a.Status == "Up" && a.Physical == true))
            yield return $"Adaptador virtual ativo ({string.Join(", ", tunnels.Select(t => t.Name))}), provavelmente VPN: o tráfego que passa por ele também é contado no adaptador físico. Não some os dois; na separação LAN/internet ele entra como “Indeterminado”.";
        if (hyperV.Count > 0)
            yield return $"Redes virtuais do Hyper-V/WSL ({string.Join(", ", hyperV.Select(t => t.Name))}): tráfego entre o PC e máquinas virtuais. Ficam fora dos totais por padrão.";
        var hdds = r.PhysicalDisks.Where(d => d.MediaType == "HDD").ToList();
        if (hdds.Count > 0 && hdds.All(d => d.PowerBefore == DiskPowerState.On))
            yield return hdds.Any(d => d.PowerMethod == "ATA")
                ? "Os HDDs estavam girando no início (confirmado pelo comando ATA). Algo os acessou nos últimos minutos; use o modo `energia` para descobrir quando e o quê."
                : "Os HDDs aparecem ligados, mas pelo método do Windows, que não detecta o repouso de discos SATA com segurança. Rode como administrador para usar o comando ATA.";
    }

    // ------------------------------------------------------------------ várias capturas

    public static string RenderComparison(IList<InventoryReport> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Matriz de disponibilidade — comparação");
        sb.AppendLine();
        sb.AppendLine("Cada célula: instâncias com leitura / instâncias esperadas, e a fonte escolhida.");
        sb.AppendLine();

        var rowsByReport = reports.Select(BuildRows).ToList();
        var keys = rowsByReport.SelectMany(rows => rows.Where(x => x.Expected).Select(x => (x.Component, x.Metric))).Distinct().ToList();

        sb.Append("| Componente | Métrica |");
        foreach (var r in reports) sb.Append($" {Esc(r.System.HostName)} · {Esc(r.Scenario)} · {r.StartedAt.ToString("dd/MM HH:mm", Pt)}{(r.System.IsElevated ? "" : " (sem admin)")} |");
        sb.AppendLine();
        sb.Append("|---|---|");
        foreach (var _ in reports) sb.Append("---|");
        sb.AppendLine();

        foreach (var (component, metric) in keys)
        {
            sb.Append($"| {ComponentLabels.GetValueOrDefault(component, component)} | {MetricLabel(metric)} |");
            foreach (var rows in rowsByReport)
            {
                var matching = rows.Where(x => x.Expected && x.Component == component && x.Metric == metric).ToList();
                if (matching.Count == 0) { sb.Append(" — |"); continue; }
                var ok = matching.Count(x => x.Available);
                var icon = ok == matching.Count ? "✅" : ok == 0 ? "❌" : "⚠️";
                var sources = string.Join("+", matching.Where(x => x.Best is not null).Select(x => x.Best!.Source).Distinct());
                sb.Append($" {icon} {ok}/{matching.Count}{(sources.Length > 0 ? " " + sources : "")} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("## Avisos por captura");
        sb.AppendLine();
        foreach (var r in reports)
        {
            var w = Warnings(r).ToList();
            sb.AppendLine($"**{Esc(r.System.HostName)} · {Esc(r.Scenario)}** — {(w.Count == 0 ? "nenhum" : "")}");
            foreach (var x in w) sb.AppendLine($"- {x}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ formatação

    private static string CellText(MatrixCell c)
    {
        var s = c.Summary;
        var tag = c.Source == "lhm" ? "" : $"{c.Source}: ";
        if (s.Ok == 0)
            return $"{tag}{(s.Errors > 0 ? "⚠️" : "❌")} {Esc(s.Note ?? "sem valor")}";
        if (s.Unit == "código")
            return $"{tag}✅ {Esc(s.Note)}";
        var range = s.Min is { } mn && s.Max is { } mx && Math.Abs(mx - mn) > 1e-9 ? $" ({Fmt(mn)}–{Fmt(mx)})" : "";
        var partial = s.Ok < s.Samples ? $" ⚠️ {s.Ok}/{s.Samples}" : "";
        return $"{tag}✅ {Fmt(s.Avg)} {s.Unit}{range}{partial}";
    }

    private static string MetricLabel(string m) => MetricLabels.GetValueOrDefault(m, m);
    private static string Power(DiskPowerState p) => p switch { DiskPowerState.On => "ligado", DiskPowerState.Standby => "repouso", _ => "?" };
    private static string SimNao(bool b) => b ? "sim" : "não";
    private static string Esc(string? s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");

    private static string Fmt(double? v) => v switch
    {
        null => "?",
        { } d when Math.Abs(d) >= 100 => d.ToString("N0", Pt),
        { } d when Math.Abs(d) >= 10 => d.ToString("N1", Pt),
        { } d => d.ToString("0.##", Pt),
    };
}
