using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>
/// Tráfego por adaptador. Os contadores do Windows são acumulados desde que o adaptador subiu;
/// a taxa sai da diferença entre amostras. Se o contador voltar (reinício do adaptador), a amostra é
/// marcada como erro em vez de gerar um pico falso.
/// </summary>
public sealed class NetworkCollector
{
    private const string Source = "net";
    private readonly List<string> _problems;
    private readonly Dictionary<string, (long Rx, long Tx, long Ticks)> _previous = new();

    public List<NetworkAdapterInfo> Adapters { get; } = new();

    public NetworkCollector(List<string> problems) => _problems = problems;

    public void Discover()
    {
        var physical = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pnp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SystemCollector.Try(_problems, "MSFT_NetAdapter", () =>
        {
            foreach (var a in Wmi.Query(Wmi.StandardCimv2, "SELECT InterfaceGuid, HardwareInterface, PnPDeviceID FROM MSFT_NetAdapter"))
                if (Wmi.Str(a, "InterfaceGuid") is { } guid)
                {
                    physical[guid] = Wmi.Get<bool?>(a, "HardwareInterface") ?? false;
                    if (Wmi.Str(a, "PnPDeviceID") is { } id && id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) pnp[guid] = id;
                }
        });

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // GetAllNetworkInterfaces também lista filtros (QoS, WFP) e miniportas ocultas que duplicam o tráfego.
            // MSFT_NetAdapter só traz os adaptadores visíveis (os mesmos do Get-NetAdapter).
            if (physical.Count > 0 ? !physical.ContainsKey(nic.Id) : nic.Name.Contains("-0000", StringComparison.Ordinal)) continue;
            if (nic.OperationalStatus == OperationalStatus.NotPresent) continue;
            var info = new NetworkAdapterInfo
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = nic.Description,
                Type = nic.NetworkInterfaceType.ToString(),
                Status = nic.OperationalStatus.ToString(),
                SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000.0 : 0,
                Physical = physical.TryGetValue(nic.Id, out var p) ? p : null,
                Mac = nic.GetPhysicalAddress().ToString() is { Length: > 0 } mac ? mac : null,
            };
            try
            {
                var props = nic.GetIPProperties();
                foreach (var ua in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    info.IPv4.Add($"{ua.Address}/{ua.PrefixLength}");
                foreach (var gw in props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    info.Gateways.Add(gw.Address.ToString());
            }
            catch (NetworkInformationException ex)
            {
                _problems.Add($"IP do adaptador {nic.Name}: {ex.Message}");
            }
            Adapters.Add(info);
        }
        // Wi-Fi 7 (multi-link) cria várias interfaces da mesma placa ("Wi-Fi", "Wi-Fi 4", "Wi-Fi 5"): fica uma,
        // a conectada (ou a de nome mais curto).
        var duplicates = Adapters.Where(a => pnp.ContainsKey(a.Id)).GroupBy(a => pnp[a.Id]).Where(g => g.Count() > 1)
            .SelectMany(g => g.OrderBy(a => a.Status == "Up" ? 0 : 1).ThenBy(a => a.IPv4.Count > 0 ? 0 : 1).ThenBy(a => a.Name.Length).Skip(1))
            .ToList();
        foreach (var d in duplicates) Adapters.Remove(d);

        // Adaptadores ativos e físicos primeiro.
        Adapters.Sort((a, b) => (b.Status == "Up").CompareTo(a.Status == "Up") is var c and not 0 ? c : (b.Physical == true).CompareTo(a.Physical == true));
    }

    public IEnumerable<Reading> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var byId = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in Adapters)
        {
            var key = adapter.Name;
            if (!byId.TryGetValue(adapter.Id, out var nic))
            {
                yield return Reading.Unavailable("net", key, "rx", "Mbit/s", Source, "adaptador sumiu");
                continue;
            }
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                _previous.Remove(adapter.Id); // ao voltar, recomeça a base sem gerar pico
                continue;
            }

            long rx = 0, tx = 0;
            string? error = null;
            try
            {
                var stats = nic.GetIPStatistics();
                rx = stats.BytesReceived;
                tx = stats.BytesSent;
            }
            catch (NetworkInformationException ex)
            {
                error = ex.Message;
            }
            if (error is not null)
            {
                yield return Reading.Failed("net", key, "rx", "Mbit/s", Source, error);
                continue;
            }

            yield return Reading.Ok("net", key, "rx_total", rx / 1e9, "GB", Source, "desde que o adaptador subiu");
            yield return Reading.Ok("net", key, "tx_total", tx / 1e9, "GB", Source, "desde que o adaptador subiu");

            if (_previous.TryGetValue(adapter.Id, out var prev))
            {
                var seconds = Stopwatch.GetElapsedTime(prev.Ticks, now).TotalSeconds;
                if (rx < prev.Rx || tx < prev.Tx)
                {
                    yield return Reading.Failed("net", key, "rx", "Mbit/s", Source, "contador reiniciado; amostra descartada");
                    yield return Reading.Failed("net", key, "tx", "Mbit/s", Source, "contador reiniciado; amostra descartada");
                }
                else if (seconds > 0)
                {
                    yield return Reading.Ok("net", key, "rx", (rx - prev.Rx) * 8 / 1e6 / seconds, "Mbit/s", Source);
                    yield return Reading.Ok("net", key, "tx", (tx - prev.Tx) * 8 / 1e6 / seconds, "Mbit/s", Source);
                }
            }
            _previous[adapter.Id] = (rx, tx, now);
        }
    }
}
