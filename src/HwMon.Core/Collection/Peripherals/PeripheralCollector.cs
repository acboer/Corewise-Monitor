using System.Text.RegularExpressions;
using HwMon.Core.Model;

namespace HwMon.Core.Collection.Peripherals;

/// <summary>Periférico sem fio encontrado num dongle.</summary>
public sealed class Peripheral
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    /// <summary>headset, mouse, keyboard, trackpad...</summary>
    public string? Type { get; init; }
    /// <summary>Lightspeed, Logi Bolt, Unifying.</summary>
    public required string Connection { get; init; }
    /// <summary>Canal HID++ (Logitech); nulo nos aparelhos lidos pelo Windows (Bluetooth, XInput).</summary>
    public HidPP? Channel { get; init; }
    /// <summary>Leitura da bateria pelo Windows (Bluetooth, XInput), no lugar do HID++.</summary>
    public Func<BatteryStatus?>? Read { get; init; }
    /// <summary>Só níveis aproximados (controle Xbox): sem estimativa de tempo.</summary>
    public bool Coarse { get; init; }
    public byte Index { get; init; }
    public ushort BatteryFeature { get; init; }
    public byte BatteryIndex { get; init; }
}

/// <summary>
/// Bateria de periféricos Logitech sem fio (headsets G Lightspeed, receptores Logi Bolt e Unifying) pelo HID++.
/// A busca (quais aparelhos estão em cada dongle) é feita ao iniciar e a cada 10 min; a bateria é lida a cada minuto.
/// Aparelho desligado não responde: a última leitura fica como antiga ("última leitura às…").
/// Outras marcas (ex.: kit Dell) usam protocolos fechados e não aparecem.
/// </summary>
public sealed class PeripheralCollector(List<string> problems, string? statePath = null) : IDisposable
{
    private static readonly HashSet<ushort> Receivers =
    [
        0xC52B, 0xC532, // Unifying
        0xC52F, 0xC534, 0xC531, 0xC52E, 0xC526, 0xC537, // nano (mouses e kits M/MK)
        0xC539, 0xC53A, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D, // Lightspeed
        0xC548, // Logi Bolt
    ];

    private readonly Dictionary<string, HidPP> _channels = new(StringComparer.OrdinalIgnoreCase);
    private List<Peripheral> _devices = new();

    public IReadOnlyList<Peripheral> Devices => _devices;

    private static string ConnectionName(ushort pid) => pid switch
    {
        0xC548 => "Logi Bolt",
        0xC52B or 0xC532 => "Unifying",
        0xC52F or 0xC534 or 0xC531 or 0xC52E or 0xC526 or 0xC537 => "receptor nano",
        _ => "Lightspeed",
    };

    /// <summary>Procura os aparelhos em todos os dongles Logitech. Devolve true se a lista mudou.</summary>
    public bool Scan()
    {
        var found = new List<Peripheral>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<HidInfo> collections;
        try
        {
            collections = HidDevice.Enumerate(0x046D);
        }
        catch (Exception ex)
        {
            problems.Add($"Periféricos: falha ao listar dispositivos HID: {ex.Message}");
            return false;
        }

        // Mesma interface USB, coleções diferentes: "...&mi_02&col01#b&55fa487&0&0000" (curta) e "...&col02#...&0001" (longa).
        static string Parent(string path) => Regex.Replace(path.ToLowerInvariant(), @"&col\d+|&\d{4}(?=#)", "");

        foreach (var info in collections.Where(c => c.UsagePage >= 0xFF00 && c.OutputLength == 20))
        {
            seenPaths.Add(info.Path);
            if (!_channels.TryGetValue(info.Path, out var channel))
            {
                var shortInfo = Receivers.Contains(info.ProductId)
                    ? collections.FirstOrDefault(c => c.OutputLength == 7 && c.UsagePage == 0xFF00 && Parent(c.Path) == Parent(info.Path))
                    : null;
                channel = HidPP.Open(info, shortInfo)!;
                if (channel is null) continue;
                _channels[info.Path] = channel;
            }
            var indexes = Receivers.Contains(info.ProductId) ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
            foreach (var index in indexes)
            {
                try
                {
                    if (Probe(channel, index) is { } p && found.All(f => f.Key != p.Key)) found.Add(p);
                }
                catch (Exception ex)
                {
                    problems.Add($"Periféricos: {info.Product} #{index}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Aparelho em repouso agora, mas já visto neste dongle (nesta execução ou numa anterior, pelo arquivo):
        // entra mesmo assim, com as funções já conhecidas. A bateria fica "antiga" até ele acordar.
        // O mesmo aparelho pode ter ficado gravado com outra chave (ex.: numa busca em que ele não informou o número de
        // série): um aparelho por posição no receptor, senão o mouse aparecia duas vezes.
        bool SameSlot(HidPP channel, byte index) => found.Any(f => f.Channel is { } c && c.Info.ProductId == channel.Info.ProductId && f.Index == index);
        foreach (var old in _devices.Where(o => o.Channel is { } c && found.All(f => f.Key != o.Key) && _channels.ContainsValue(c) && !SameSlot(c, o.Index)))
            found.Add(old);
        foreach (var known in LoadKnown().Where(k => found.All(f => f.Key != k.Key)))
        {
            var channel = _channels.Values.FirstOrDefault(c => c.Info.ProductId == known.ReceiverPid && seenPaths.Contains(c.Info.Path));
            if (channel is null || SameSlot(channel, known.Index)) continue; // dongle não está conectado, ou a posição já tem aparelho
            found.Add(new Peripheral
            {
                Key = known.Key, Name = known.Name, Type = known.Type, Connection = known.Connection, Channel = channel,
                Index = known.Index, BatteryFeature = known.BatteryFeature, BatteryIndex = known.BatteryIndex,
            });
        }

        // Dongles que sumiram (desconectados) ou coleções sem nada útil: fecha.
        var used = found.Where(f => f.Channel is not null).Select(f => f.Channel!).ToHashSet();
        foreach (var path in _channels.Keys.ToList())
        {
            if (seenPaths.Contains(path) && used.Contains(_channels[path])) continue;
            _channels[path].Dispose();
            _channels.Remove(path);
        }

        // Bluetooth e controles Xbox: o Windows já sabe quais estão conectados.
        foreach (var d in SystemBatteries.Find(problems))
            found.Add(new Peripheral { Key = d.Key, Name = d.Name, Type = d.Type, Connection = d.Connection, Read = d.Read, Coarse = d.Coarse });
        _signature = Signature();

        var changed = !found.Select(f => f.Key).Order().SequenceEqual(_devices.Select(d => d.Key).Order());
        _devices = found;
        if (changed) SaveKnown();
        return changed;
    }

    // ------------------------------------------------------------------ aparelhos já vistos (perifericos.json)

    /// <summary>Gravado para reconhecer, depois de reiniciar, aparelhos que estão em repouso na hora da busca.</summary>
    public sealed record KnownDevice(string Key, string Name, string? Type, string Connection, ushort ReceiverPid, byte Index, ushort BatteryFeature, byte BatteryIndex);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = true };

    private List<KnownDevice> LoadKnown()
    {
        if (statePath is null || !File.Exists(statePath)) return new();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<KnownDevice>>(File.ReadAllText(statePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return new();
        }
    }

    private void SaveKnown()
    {
        if (statePath is null) return;
        try
        {
            var all = LoadKnown().ToDictionary(k => k.Key);
            foreach (var d in _devices)
            {
                if (d.Channel is not { } c) continue;
                // Outra chave na mesma posição do mesmo receptor é este aparelho gravado com outro nome: sai.
                foreach (var stale in all.Values.Where(k => k.Key != d.Key && k.ReceiverPid == c.Info.ProductId && k.Index == d.Index).ToList())
                    all.Remove(stale.Key);
                all[d.Key] = new KnownDevice(d.Key, d.Name, d.Type, d.Connection, c.Info.ProductId, d.Index, d.BatteryFeature, d.BatteryIndex);
            }
            HwMon.Core.SafeFile.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(all.Values.ToList(), Json));
        }
        catch (IOException ex)
        {
            problems.Add($"Periféricos: não foi possível gravar {statePath}: {ex.Message}");
        }
    }

    /// <summary>Esquece um aparelho (ex.: vendido/despareado). Não é usado pela interface ainda.</summary>
    public void Forget(string key)
    {
        if (statePath is null) return;
        var all = LoadKnown().Where(k => k.Key != key).ToList();
        HwMon.Core.SafeFile.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(all, Json));
    }

    private static Peripheral? Probe(HidPP channel, byte index)
    {
        if (channel.Ping(index) is not { Major: >= 2 }) return null; // só HID++ 2.0
        ushort feature = 0;
        byte batteryIndex = 0;
        foreach (var f in new[] { HidPP.FeatUnifiedBattery, HidPP.FeatBatteryStatus, HidPP.FeatBatteryVoltage, HidPP.FeatAdcMeasurement })
        {
            batteryIndex = channel.FeatureIndex(index, f);
            if (batteryIndex == 0) continue;
            feature = f;
            break;
        }
        if (feature == 0) return null; // sem bateria (ex.: algo com fio)

        var nameIndex = channel.FeatureIndex(index, HidPP.FeatName);
        var name = nameIndex == 0 ? null : channel.Name(index, nameIndex);
        var type = nameIndex == 0 ? null : channel.DeviceType(index, nameIndex);
        var fwIndex = channel.FeatureIndex(index, HidPP.FeatFirmware);
        var unit = fwIndex == 0 ? null : channel.UnitId(index, fwIndex);
        name ??= channel.Info.Product ?? $"Logitech {channel.Info.ProductId:X4}";
        type ??= feature == HidPP.FeatAdcMeasurement ? "headset" : null;
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return new Peripheral
        {
            Key = unit is not null ? $"logi-{unit.ToLowerInvariant()}" : $"logi-{channel.Info.ProductId:x4}-{index}-{slug}",
            Name = name,
            Type = type,
            Connection = ConnectionName(channel.Info.ProductId),
            Channel = channel,
            Index = index,
            BatteryFeature = feature,
            BatteryIndex = batteryIndex,
        };
    }

    private string? _signature;

    /// <summary>Lista de dispositivos do Windows (HID, Bluetooth com bateria, controles): muda quando algo entra ou sai.</summary>
    private static string Signature()
    {
        try
        {
            var paths = HidDevice.Paths();
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("|", paths) + "#" + SystemBatteries.Signature();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Conferência barata (alguns ms): algo foi conectado ou desconectado desde a última busca?</summary>
    public bool DevicesChanged() => _signature is not null && Signature() != _signature;

    private readonly Dictionary<string, DateTimeOffset> _lastRead = new();
    private readonly Dictionary<string, BatteryTrend> _trends = new();

    /// <summary>Mouses e teclados: a cada minuto. Headsets: a cada chamada (10 s), para "desligado" aparecer logo.</summary>
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(60);

    public IEnumerable<Reading> Sample()
    {
        var now = DateTimeOffset.Now;
        foreach (var d in _devices)
        {
            var fast = d.BatteryFeature == HidPP.FeatAdcMeasurement || d.Type == "headset";
            if (!fast && _lastRead.TryGetValue(d.Key, out var last) && now - last < SlowInterval - TimeSpan.FromSeconds(2)) continue;
            _lastRead[d.Key] = now;
            BatteryStatus? b = null;
            try
            {
                b = d.Read is { } read ? read() : d.Channel!.Battery(d.Index, d.BatteryFeature, d.BatteryIndex);
            }
            catch (Exception ex)
            {
                problems.Add($"Periféricos: {d.Name}: {ex.GetType().Name}: {ex.Message}");
            }
            if (b?.Percent is { } pct)
            {
                var note = b.State + (b.VoltageMv is { } mv ? $" · {mv / 1000.0:0.00} V (estimado pela tensão)" : "");
                yield return Reading.Ok("periph", d.Key, "battery", pct, "%", "hidpp", d.Coarse ? note + " · nível aproximado" : note);
                if (d.Coarse)
                {
                    // O controle informa só vazio/baixo/médio/cheio: não dá para estimar tempo.
                    yield return Reading.Unavailable("periph", d.Key, "runtime", "min", "hidpp", "o controle informa só o nível aproximado");
                    yield return Reading.Unavailable("periph", d.Key, "charge_eta", "min", "hidpp", "o controle informa só o nível aproximado");
                    continue;
                }

                // Tempo estimado: para carregar (carregando) ou de bateria restante (na bateria).
                var charging = b.State == "carregando";
                if (!_trends.TryGetValue(d.Key, out var trend)) _trends[d.Key] = trend = new BatteryTrend();
                var estimate = trend.Add(now, pct, charging);
                if (charging)
                {
                    yield return estimate is { } eta
                        ? Reading.Ok("periph", d.Key, "charge_eta", eta, "min", "hidpp", "estimado pela velocidade de carga")
                        : Reading.Unavailable("periph", d.Key, "charge_eta", "min", "hidpp", "calculando (alguns minutos carregando)");
                    yield return Reading.Unavailable("periph", d.Key, "runtime", "min", "hidpp", "carregando");
                }
                else
                {
                    yield return estimate is { } left && b.State == "na bateria"
                        ? Reading.Ok("periph", d.Key, "runtime", left, "min", "hidpp", "estimado pelo ritmo de descarga")
                        : Reading.Unavailable("periph", d.Key, "runtime", "min", "hidpp", "calculando (precisa de algumas horas de uso)");
                    yield return Reading.Unavailable("periph", d.Key, "charge_eta", "min", "hidpp", b.State);
                }
            }
            else if (b is { Percent: null } && b.State is not "desligado")
            {
                yield return Reading.Unavailable("periph", d.Key, "battery", "%", "hidpp", b.State); // ex.: controle com fio
            }
            else
            {
                var error = d.Channel?.LastError;
                // Headset com dongle próprio: o dongle responde com erro quando o headset está desligado.
                var off = b?.State == "desligado" || (d.Channel is not null && d.Index == 0xFF && error is not null);
                yield return Reading.Unavailable("periph", d.Key, "battery", "%", "hidpp",
                    off ? "desligado" : $"sem resposta (desligado, em repouso ou fora de alcance){(error is { } e ? $" · erro HID++ 0x{e:X2}" : "")}");
                if (off)
                {
                    _trends.Remove(d.Key);
                    yield return Reading.Unavailable("periph", d.Key, "charge_eta", "min", "hidpp", "desligado");
                    yield return Reading.Unavailable("periph", d.Key, "runtime", "min", "hidpp", "desligado");
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var c in _channels.Values) c.Dispose();
        _channels.Clear();
    }
}
