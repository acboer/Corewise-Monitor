using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HwMon.Core.Telemetry;

namespace HwMon.Client;

/// <summary>
/// Velocidade da rede entre este PC e um coletor (estilo iperf), pela própria conexão HTTPS do coletor, sem abrir
/// porta extra: latência (20 pedidos), recebendo (coletor → aqui) e enviando (aqui → coletor), cada um com 4
/// conexões em paralelo. Os 2 primeiros segundos de cada sentido ficam de fora (o TCP ainda está acelerando).
/// </summary>
public static class NetBenchmark
{
    public const int Streams = 4;
    public const int Seconds = 10;
    private const double Warmup = 2.0;

    /// <param name="progress">fase, % do teste, velocidade agora (Mbit/s, quando medindo)</param>
    public static async Task<NetBenchResult> RunAsync(PairedMachine machine, Action<string, double, double?> progress, CancellationToken cancel)
    {
        using var client = CollectorClient.For(machine);

        // Latência: 3 de aquecimento (abre a conexão) e 20 medidas.
        progress(L.T("Medindo a latência"), 0, null);
        for (var i = 0; i < 3; i++) await client.PingAsync(cancel);
        var rtts = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            rtts.Add((await client.PingAsync(cancel)).TotalMilliseconds);
            progress(L.T("Medindo a latência"), 0.05 * (i + 1) / 20, null);
        }
        rtts.Sort();
        var median = rtts[rtts.Count / 2];
        var jitter = rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(b - a)).DefaultIfEmpty(0).Average();

        var down = await MeasureAsync(L.T("Recebendo dados"), 0.05, (onBytes, ct) => client.DownloadAsync(Seconds, onBytes, ct), progress, cancel);
        var up = await MeasureAsync(L.T("Enviando dados"), 0.525, (onBytes, ct) => client.UploadAsync(Seconds, onBytes, ct), progress, cancel);
        progress(L.T("Concluído"), 1, null);

        var (adapter, link) = LocalAdapter(client.ConnectedAddress);
        return new NetBenchResult
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Time = DateTimeOffset.Now,
            From = Environment.MachineName,
            To = machine.Name,
            Address = client.ConnectedAddress,
            DownloadMbps = Math.Round(down, 1),
            UploadMbps = Math.Round(up, 1),
            LatencyMs = Math.Round(median, 2),
            JitterMs = Math.Round(jitter, 2),
            Streams = Streams,
            Seconds = Seconds,
            LinkMbps = link,
            Adapter = adapter,
        };
    }

    private static async Task<double> MeasureAsync(string phase, double start, Func<Action<int>, CancellationToken, Task> transfer,
        Action<string, double, double?> progress, CancellationToken cancel)
    {
        long total = 0;
        var clock = Stopwatch.StartNew();
        var samples = new List<(double T, long Bytes)>();
        var tasks = Enumerable.Range(0, Streams).Select(_ => transfer(n => Interlocked.Add(ref total, n), cancel)).ToList();
        var all = Task.WhenAll(tasks);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(250, cancel));
            var t = clock.Elapsed.TotalSeconds;
            samples.Add((t, Interlocked.Read(ref total)));
            var recent = samples.LastOrDefault(s => s.T <= t - 1);
            double? now = recent.T > 0 ? (samples[^1].Bytes - recent.Bytes) * 8 / 1e6 / (t - recent.T) : null;
            progress(phase, start + 0.475 * Math.Min(1, t / Seconds), now);
        }
        await all;
        // Velocidade média depois do aquecimento.
        var first = samples.FirstOrDefault(s => s.T >= Warmup);
        var last = samples[^1];
        return first.T > 0 && last.T > first.T
            ? (last.Bytes - first.Bytes) * 8 / 1e6 / (last.T - first.T)
            : last.Bytes * 8 / 1e6 / Math.Max(0.001, last.T);
    }

    /// <summary>Adaptador local na mesma sub-rede do coletor e a velocidade do link (para "X de Y possíveis").</summary>
    private static (string? Name, double? Mbps) LocalAdapter(string? remote)
    {
        if (remote is null || !IPAddress.TryParse(remote, out var target) || target.AddressFamily != AddressFamily.InterNetwork) return (null, null);
        if (IPAddress.IsLoopback(target)) return ("loopback", null);
        var bytes = target.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var ua in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is not null))
            {
                var local = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (Enumerable.Range(0, 4).All(i => (local[i] & mask[i]) == (bytes[i] & mask[i])))
                    return (nic.Name, nic.Speed > 0 ? nic.Speed / 1e6 : null);
            }
        return (null, null);
    }
}
