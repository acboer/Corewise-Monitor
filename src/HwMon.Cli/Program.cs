using System.Globalization;
using System.Text;
using System.Text.Json;
using HwMon.Client;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;

Console.OutputEncoding = Encoding.UTF8;
var pt = CultureInfo.GetCultureInfo("pt-BR");

var profile = Option(args, "--perfil");
var store = new MachineStore(profile);
var positional = Positional(args);

try
{
    return positional switch
    {
        ["parear", var host, ..] => await PairAsync(host),
        ["testar", var host, ..] => await ProbeAsync(host),
        ["maquinas"] => Machines(),
        ["remover", var name] => Remove(name),
        ["ver", .. var rest] => await WatchAsync(rest.FirstOrDefault()),
        ["snapshot", .. var rest] => await DumpAsync(rest.FirstOrDefault()),
        ["historico", var name, var component, var metric, ..] => await HistoryAsync(name, component, metric),
        ["historico", var name] => await HistoryStatsAsync(name),
        ["rede", var name] => await NetBenchAsync(name),
        _ => Help(),
    };
}
catch (Exception ex) when (ex is CollectorException or CertificateMismatchException)
{
    Console.Error.WriteLine($"Erro: {ex.Message}");
    return 1;
}

// ------------------------------------------------------------------ comandos

async Task<int> PairAsync(string host)
{
    var port = int.Parse(Option(args, "--porta") ?? Protocol.DefaultPort.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    using var client = new CollectorClient(host, port, expectedFingerprint: null, token: null);
    var info = await client.InfoAsync();
    var presented = client.PresentedFingerprint!;
    if (!string.Equals(presented, info.CertificateFingerprint, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Erro: o certificado da conexão não confere com o anunciado pelo coletor. Pareamento cancelado.");
        return 1;
    }

    Console.WriteLine($"Coletor: {info.HostName}{(info.Role is null ? "" : $" ({info.Role})")} · versão {info.AgentVersion}");
    Console.WriteLine($"Certificado: {Fingerprint.Short(presented)}  ← confira se é o mesmo mostrado no comando \"parear\" do coletor");
    var code = Option(args, "--codigo");
    if (code is null)
    {
        Console.Write("Código de 6 dígitos: ");
        code = Console.ReadLine();
    }
    code = new string((code ?? "").Where(char.IsDigit).ToArray());

    var response = await client.PairAsync(new PairRequest { Code = code, ClientId = store.ClientId, ClientName = Environment.MachineName });
    var machine = new PairedMachine
    {
        Name = Option(args, "--nome") ?? info.HostName,
        Host = host,
        Port = port,
        MachineId = response.MachineId,
        Fingerprint = presented,
        PairedAt = DateTimeOffset.Now,
        KnownAddresses = client.ConnectedAddress is { } ip ? [ip] : [],
    };
    store.Save(machine, response.Token);
    Console.WriteLine($"Pareado. Veja ao vivo com:  hwmon ver {machine.Name}");
    return 0;
}

async Task<int> ProbeAsync(string host)
{
    var port = int.Parse(Option(args, "--porta") ?? Protocol.DefaultPort.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    try
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(host);
        Console.WriteLine($"{host} → {string.Join(", ", addresses.Select(a => a.ToString()))}");
    }
    catch (System.Net.Sockets.SocketException ex)
    {
        Console.WriteLine($"Nome não encontrado: {ex.Message}");
        return 1;
    }
    using var client = new CollectorClient(host, port, expectedFingerprint: null, token: null);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var info = await client.InfoAsync();
    Console.WriteLine($"Coletor respondeu em {watch.ElapsedMilliseconds} ms: {info.HostName}{(info.Role is null ? "" : $" ({info.Role})")} · versão {info.AgentVersion} · certificado {Fingerprint.Short(info.CertificateFingerprint)}");
    return 0;
}

int Machines()
{
    var all = store.List();
    if (all.Count == 0) Console.WriteLine("Nenhum PC pareado. Use: hwmon parear <nome-ou-ip>");
    foreach (var m in all)
        Console.WriteLine($"{m.Name,-16} {m.Host}:{m.Port,-6} certificado {Fingerprint.Short(m.Fingerprint)}   pareado {m.PairedAt.ToString("dd/MM/yyyy HH:mm", pt)}");
    return 0;
}

int Remove(string name)
{
    Console.WriteLine(store.Remove(name) ? "Removido deste PC. (Para cortar o acesso no coletor: HwMon.Collector.exe revogar)" : "Não encontrado.");
    return 0;
}

async Task<int> HistoryAsync(string name, string component, string metric)
{
    var hours = double.Parse(Option(args, "--horas") ?? "1", CultureInfo.InvariantCulture);
    var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var client = CollectorClient.For(Resolve(name));
    var h = await client.HistoryAsync(component, metric, to - (long)(hours * 3600), to, int.Parse(Option(args, "--pontos") ?? "30", CultureInfo.InvariantCulture));
    string Local(long t) => DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("dd/MM HH:mm:ss", pt);
    Console.WriteLine($"{h.Component} {h.Metric} ({h.Unit}) · resolução {h.Resolution} · ponto de {h.StepSeconds} s · {h.Points.Count} pontos · gravando desde {(h.Since is { } s ? Local(s) : "?")}");
    if (h.Stats is { } st)
        Console.WriteLine($"mín {Format.Number(st.Min, h.Unit)} ({Local(st.MinT)}) · média {Format.Number(st.Avg, h.Unit)} · máx {Format.Number(st.Max, h.Unit)} ({Local(st.MaxT)}) · {st.Seconds / 60.0:0} min medidos");
    foreach (var p in h.Points) Console.WriteLine($"  {Local(p.T)}  média {Format.Number(p.Avg, h.Unit),-12} mín {Format.Number(p.Min, h.Unit),-12} máx {Format.Number(p.Max, h.Unit)}");
    return 0;
}

async Task<int> HistoryStatsAsync(string name)
{
    var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var client = CollectorClient.For(Resolve(name));
    var stats = await client.HistoryStatsAsync(to - (long)(double.Parse(Option(args, "--horas") ?? "24", CultureInfo.InvariantCulture) * 3600), to);
    foreach (var s in stats.Series.OrderBy(s => s.Component).ThenBy(s => s.Metric))
        Console.WriteLine($"{s.Component,-40} {s.Metric,-16} mín {Format.Number(s.Min, s.Unit),-12} média {Format.Number(s.Avg, s.Unit),-12} máx {Format.Number(s.Max, s.Unit)}");
    return 0;
}

async Task<int> NetBenchAsync(string name)
{
    var last = "";
    var r = await NetBenchmark.RunAsync(Resolve(name), (phase, p, now) =>
    {
        if (phase != last) { Console.WriteLine($"  {phase}..."); last = phase; }
    }, CancellationToken.None);
    Console.WriteLine($"\n{r.From} ⇄ {r.To} ({r.Address}) · {r.Streams} conexões · {r.Seconds} s cada sentido");
    Console.WriteLine($"  Recebendo:  {r.DownloadMbps:0.0} Mbit/s");
    Console.WriteLine($"  Enviando:   {r.UploadMbps:0.0} Mbit/s");
    Console.WriteLine($"  Latência:   {r.LatencyMs:0.00} ms (variação {r.JitterMs:0.00} ms)");
    if (r.LinkMbps is { } link) Console.WriteLine($"  Link local: {r.Adapter} a {link:0} Mbit/s");
    return 0;
}

PairedMachine Resolve(string? name)
    => store.Find(name) ?? throw new CollectorException(name is null
        ? "Informe qual PC (veja \"hwmon maquinas\")."
        : $"\"{name}\" não está pareado (veja \"hwmon maquinas\").");

async Task<int> DumpAsync(string? name)
{
    using var client = CollectorClient.For(Resolve(name));
    var snapshot = await client.SnapshotAsync();
    Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(CollectorClient.Json) { WriteIndented = true }));
    return 0;
}

async Task<int> WatchAsync(string? name)
{
    var machine = Resolve(name);
    var seconds = Option(args, "--segundos") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : (double?)null;
    var plain = args.Contains("--simples") || Console.IsOutputRedirected;
    using var cts = seconds is { } sec ? new CancellationTokenSource(TimeSpan.FromSeconds(sec)) : new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var connection = new MachineConnection(machine, store);
    if (!plain) { Console.Clear(); Console.CursorVisible = false; }
    var lastStatus = (ConnectionStatus?)null;
    try
    {
        await connection.RunAsync(state =>
        {
            if (plain)
            {
                // Saída para log/arquivo: mudanças de estado + um resumo curto por atualização.
                if (state.Status != lastStatus || state.Status != ConnectionStatus.Connected)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {StatusLine(state)}");
                else if (state.Snapshot is { } snap)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] #{snap.Sequence} {Summary(state)}");
                lastStatus = state.Status;
                return;
            }
            Render(machine, state);
        }, cts.Token);
    }
    finally
    {
        if (!plain) Console.CursorVisible = true;
    }
    return 0;
}

// ------------------------------------------------------------------ exibição

string StatusLine(ConnectionState s) => s.Status switch
{
    ConnectionStatus.Connected => "● conectado",
    ConnectionStatus.Connecting => "○ conectando...",
    ConnectionStatus.Disconnected => $"✕ desconectado desde {s.Since.ToString("HH:mm:ss", pt)} — {s.Message} (nova tentativa em {s.NextRetry?.TotalSeconds:0} s)",
    _ => $"✕ {s.Message}",
};

string Summary(ConnectionState s)
{
    var v = Values(s.Snapshot);
    var cpu = s.Descriptor?.Components.FirstOrDefault(c => c.Kind == ComponentKind.Cpu)?.Id;
    var ram = s.Descriptor?.Components.FirstOrDefault(c => c.Kind == ComponentKind.Ram)?.Id;
    return $"CPU {Fmt(v, cpu, "usage")} {Fmt(v, cpu, "temp")} · RAM {Fmt(v, ram, "used")} · {v.Count} leituras";
}

void Render(PairedMachine machine, ConnectionState state)
{
    var lines = new List<string>();
    var d = state.Descriptor;
    var v = Values(state.Snapshot);
    lines.Add($"{d?.HostName ?? machine.Name}{(d?.Role is { } role ? $" · {role}" : "")}   {StatusLine(state)}");
    lines.Add(d is null ? "" : $"{d.Model} · {d.Os} · coletor {d.AgentVersion}");
    lines.Add("");

    if (d is not null)
    {
        foreach (var c in d.Components.OrderBy(c => c.Order))
        {
            var id = c.Id;
            var line = c.Kind switch
            {
                ComponentKind.Cpu => $"CPU     {Cut(c.Name, 34),-34} {Fmt(v, id, "usage"),8} {Fmt(v, id, "clock"),10} {Fmt(v, id, "temp"),8} {Fmt(v, id, "power"),8}",
                ComponentKind.Ram when id == "ram:0" => $"RAM     {Fmt(v, id, "used")} de {Fmt(v, id, "total")} ({Fmt(v, id, "load")})",
                ComponentKind.Ram => $"        {Cut(c.Name, 34),-34} {Fmt(v, id, "temp"),8}",
                ComponentKind.Gpu => $"GPU     {Cut(c.Name, 34),-34} {Fmt(v, id, "usage"),8} {Fmt(v, id, "clock"),10} {Fmt(v, id, "temp"),8} {Fmt(v, id, "power"),8}  VRAM {Fmt(v, id, "vram_used")}",
                ComponentKind.Disk => $"Disco   {Cut(c.Name, 34),-34} {Fmt(v, id, "active"),8}  L {Fmt(v, id, "read"),10}  E {Fmt(v, id, "write"),10} {Fmt(v, id, "temp"),8}  {Note(v, id, "health")}",
                ComponentKind.Volume => $"  └ {Cut(c.Name, 24),-24} usado {Fmt(v, id, "used"),10} · livre {Fmt(v, id, "free"),10}",
                ComponentKind.Network when v.ContainsKey((id, "rx")) =>
                    $"Rede    {Cut(c.Name, 34),-34} ↓ {Fmt(v, id, "rx"),14}  ↑ {Fmt(v, id, "tx"),14}   hoje ↓ {Fmt(v, id, "rx_today")} ↑ {Fmt(v, id, "tx_today")}",
                ComponentKind.Fan when v.TryGetValue((id, "rpm"), out var rpm) && rpm.Value > 0 => $"Fan     {Cut(c.Name, 34),-34} {Fmt(v, id, "rpm")}",
                _ => null,
            };
            if (line is not null) lines.Add(line);
        }
    }
    lines.Add("");
    lines.Add("~ = leitura antiga (ex.: HDD sem uso, SMART não consultado)  — = indisponível   Ctrl+C para sair");

    var width = Math.Max(20, Console.WindowWidth - 1);
    var buffer = new StringBuilder();
    foreach (var l in lines) buffer.AppendLine(l.Length > width ? l[..width] : l.PadRight(width));
    for (var i = lines.Count; i < Console.WindowHeight - 1; i++) buffer.AppendLine(new string(' ', width));
    Console.SetCursorPosition(0, 0);
    Console.Write(buffer.ToString());
}

Dictionary<(string, string), TelemetryValue> Values(TelemetrySnapshot? s)
    => s?.Values.ToDictionary(x => (x.Component, x.Metric)) ?? new();

string Fmt(Dictionary<(string, string), TelemetryValue> v, string? component, string metric)
{
    if (component is null || !v.TryGetValue((component, metric), out var x) || x.Value is not { } value)
        return "—";
    var text = x.Unit switch
    {
        "MHz" when value >= 1000 => (value / 1000).ToString("0.00", pt) + " GHz",
        "%" or "°C" or "RPM" or "W" or "MHz" => value.ToString("0", pt) + " " + x.Unit,
        _ => value.ToString(value >= 100 ? "N0" : value >= 10 ? "0.0" : "0.##", pt) + " " + x.Unit,
    };
    return x.State == ReadingState.Stale ? text + "~" : text;
}

string Note(Dictionary<(string, string), TelemetryValue> v, string component, string metric)
    => v.TryGetValue((component, metric), out var x) ? x.Note ?? "" : "";

static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

// ------------------------------------------------------------------ utilidades

static int Help()
{
    Console.WriteLine("""
        hwmon — cliente de teste dos coletores HwMon

          hwmon parear <nome-ou-ip> [--porta 47821] [--codigo 123456] [--nome apelido]
          hwmon testar <nome-ou-ip>      verifica se o coletor responde (não precisa de código)
          hwmon maquinas
          hwmon ver [nome] [--segundos N] [--simples]
          hwmon snapshot [nome]          leitura atual em JSON
          hwmon remover <nome>

        No PC do coletor, gere o código com:  HwMon.Collector.exe parear
        """);
    return 0;
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string[] Positional(string[] args)
{
    var list = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            if (args[i] != "--simples") i++; // pula o valor da opção
            continue;
        }
        list.Add(args[i]);
    }
    return list.ToArray();
}
