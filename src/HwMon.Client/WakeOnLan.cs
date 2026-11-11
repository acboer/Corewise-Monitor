using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HwMon.Client;

/// <summary>
/// Liga um PC pela rede (Wake-on-LAN): o "pacote mágico" é 6 bytes 0xFF seguidos de 16 cópias do MAC da placa, em UDP
/// para o broadcast da rede local. A placa do PC desligado (com WoL ativado no BIOS e no Windows) reconhece e liga.
/// </summary>
public static class WakeOnLan
{
    /// <summary>MAC em qualquer formato comum ("AA-BB-CC-DD-EE-FF", "aa:bb:…", "AABBCCDDEEFF") → 6 bytes; nulo se inválido.</summary>
    public static byte[]? ParseMac(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || mac.Any(c => !Uri.IsHexDigit(c) && c is not (':' or '-' or '.' or ' '))) return null;
        return Convert.FromHexString(hex);
    }

    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("MAC precisa ter 6 bytes.", nameof(mac));
        var packet = new byte[6 + 16 * 6];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    /// <summary>
    /// Envia para cada MAC, pelas portas 9 e 7, no broadcast geral e no de cada rede IPv4 deste PC (alguns roteadores
    /// e placas só aceitam um deles), e para o último IP conhecido. Devolve quantos pacotes saíram.
    /// </summary>
    public static async Task<int> SendAsync(IEnumerable<string> macs, IEnumerable<string>? knownAddresses = null)
    {
        var packets = macs.Select(ParseMac).OfType<byte[]>().Select(MagicPacket).ToList();
        if (packets.Count == 0) return 0;
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
            foreach (var ua in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is not null))
            {
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                targets.Add(new IPAddress(Enumerable.Range(0, 4).Select(i => (byte)(ip[i] | ~mask[i])).ToArray()));
            }
        foreach (var known in knownAddresses ?? [])
            if (IPAddress.TryParse(known, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) targets.Add(ip);

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var sent = 0;
        for (var round = 0; round < 3; round++) // UDP pode perder pacote: três vezes, com intervalo curto
        {
            foreach (var packet in packets)
                foreach (var target in targets)
                    foreach (var port in new[] { 9, 7 })
                    {
                        try
                        {
                            await udp.SendAsync(packet, new IPEndPoint(target, port));
                            sent++;
                        }
                        catch (SocketException)
                        {
                            // rede sem rota para esse destino: segue com os outros
                        }
                    }
            await Task.Delay(150);
        }
        return sent;
    }
}
