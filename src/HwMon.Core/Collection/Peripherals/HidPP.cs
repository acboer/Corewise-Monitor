namespace HwMon.Core.Collection.Peripherals;

/// <summary>Bateria lida de um periférico.</summary>
public sealed record BatteryStatus(double? Percent, string State, int? VoltageMv, string Feature);

/// <summary>
/// Protocolo HID++ 2.0 da Logitech (o mesmo usado pelo Options, G HUB e Solaar), pelos relatórios "longos" (0x11,
/// 20 bytes) na coleção do fabricante do dongle. Cada pedido leva um id de software próprio (0xD), então as
/// respostas para o G HUB, que também conversa com o dispositivo, são ignoradas.
///   Mensagem: [0x11, índice do dispositivo, índice da função, (função &lt;&lt; 4) | id de software, parâmetros…]
///   Índice 0xFF = o próprio dongle/dispositivo com fio (headsets G); 1–6 = aparelhos pareados num receptor.
/// </summary>
public sealed class HidPP : IDisposable
{
    public const byte LongReport = 0x11;
    private const byte SoftwareId = 0x0D;
    /// <summary>Sem fio, a resposta pode levar quase 1 s (aparelho acordando). Slot vazio responde na hora pelo canal curto.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1500);

    // Funções ("features") do HID++ 2.0 usadas aqui.
    public const ushort FeatRoot = 0x0000, FeatFirmware = 0x0003, FeatName = 0x0005,
        FeatBatteryStatus = 0x1000, FeatBatteryVoltage = 0x1001, FeatUnifiedBattery = 0x1004, FeatAdcMeasurement = 0x1F20;

    private readonly FileStream _stream;
    private readonly FileStream? _short;
    private readonly int _length;
    private readonly object _lock = new();

    public HidInfo Info { get; }

    private HidPP(HidInfo info, FileStream stream, FileStream? shortStream)
    {
        Info = info;
        _stream = stream;
        _short = shortStream;
        _length = info.OutputLength;
    }

    /// <summary>
    /// Abre a coleção HID++ longa de um dongle Logitech (página do fabricante, relatório de 20 bytes) e, se houver,
    /// a curta do mesmo dongle (7 bytes), por onde o receptor avisa "aparelho não pareado/desligado" (erro 0x8F).
    /// </summary>
    public static HidPP? Open(HidInfo info, HidInfo? shortInfo = null)
    {
        if (info.VendorId != 0x046D || info.UsagePage < 0xFF00 || info.OutputLength != 20) return null;
        var stream = HidDevice.Open(info.Path);
        if (stream is null) return null;
        var shortStream = shortInfo is null ? null : HidDevice.Open(shortInfo.Path);
        return new HidPP(info, stream, shortStream);
    }

    /// <summary>Envia um pedido e espera a resposta correspondente. Nulo: sem resposta (desligado, fora de alcance) ou erro.</summary>
    /// <summary>Código do último erro HID++ 2.0 recebido (nulo se o último pedido não teve resposta de erro).</summary>
    public byte? LastError { get; private set; }

    public byte[]? Request(byte device, byte featureIndex, byte function, params byte[] args)
    {
        lock (_lock)
        {
            LastError = null;
            var report = new byte[_length];
            report[0] = LongReport;
            report[1] = device;
            report[2] = featureIndex;
            report[3] = (byte)((function << 4) | SoftwareId);
            Array.Copy(args, 0, report, 4, Math.Min(args.Length, _length - 4));
            try
            {
                _stream.Write(report, 0, report.Length);
            }
            catch (IOException)
            {
                return null;
            }

            using var cts = new CancellationTokenSource(Timeout);
            var longTask = ReadLongAsync(device, featureIndex, report[3], cts.Token);
            var shortTask = _short is null ? Task.Delay(System.Threading.Timeout.Infinite, cts.Token) : WaitShortErrorAsync(device, featureIndex, report[3], cts.Token);
            try
            {
                var first = Task.WhenAny(longTask, shortTask).GetAwaiter().GetResult();
                cts.Cancel(); // encerra a outra leitura pendente
                return first == longTask && longTask.IsCompletedSuccessfully ? longTask.Result : null;
            }
            finally
            {
                try { Task.WaitAll([longTask, shortTask], 500); } catch (AggregateException) { /* leituras canceladas */ }
            }
        }
    }

    private async Task<byte[]?> ReadLongAsync(byte device, byte featureIndex, byte funcSw, CancellationToken cancel)
    {
        var buffer = new byte[Math.Max(_length, Info.InputLength)];
        try
        {
            while (true)
            {
                var read = await _stream.ReadAsync(buffer, cancel);
                if (read < 4 || buffer[0] != LongReport || buffer[1] != device) continue;
                // Erro HID++ 2.0: [0x11, dev, 0xFF, função, funçãoSw, código]
                if (buffer[2] == 0xFF && buffer[3] == featureIndex && buffer[4] == funcSw)
                {
                    LastError = buffer[5];
                    return null;
                }
                if (buffer[2] == featureIndex && buffer[3] == funcSw) return buffer[4..read];
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return null; // prazo esgotado ou dongle removido
        }
    }

    /// <summary>Erro HID++ 1.0 do receptor: [0x10, dev, 0x8F, função, funçãoSw, código, 0] (ex.: 0x09 = não pareado).</summary>
    private async Task WaitShortErrorAsync(byte device, byte featureIndex, byte funcSw, CancellationToken cancel)
    {
        var buffer = new byte[7];
        try
        {
            while (true)
            {
                var read = await _short!.ReadAsync(buffer, cancel);
                if (read >= 6 && buffer[0] == 0x10 && buffer[1] == device && buffer[2] == 0x8F && buffer[3] == featureIndex && buffer[4] == funcSw) return;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, CancellationToken.None).WaitAsync(cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Responde ao "ping" do HID++ 2.0? (dispositivo ligado e ao alcance)</summary>
    public (int Major, int Minor)? Ping(byte device)
    {
        var r = Request(device, 0x00, 1, 0, 0, 0xAA);
        return r is { Length: >= 3 } && r[2] == 0xAA ? (r[0], r[1]) : null;
    }

    /// <summary>Índice de uma função no dispositivo (0 = não tem).</summary>
    public byte FeatureIndex(byte device, ushort feature)
    {
        var r = Request(device, 0x00, 0, (byte)(feature >> 8), (byte)feature);
        return r is { Length: >= 1 } ? r[0] : (byte)0;
    }

    public string? Name(byte device, byte nameIndex)
    {
        var count = Request(device, nameIndex, 0);
        if (count is not { Length: >= 1 } || count[0] == 0) return null;
        var chars = new List<byte>();
        while (chars.Count < count[0])
        {
            var part = Request(device, nameIndex, 1, (byte)chars.Count);
            if (part is null) break;
            var take = part.TakeWhile(b => b != 0).Take(count[0] - chars.Count).ToArray();
            if (take.Length == 0) break;
            chars.AddRange(take);
        }
        return chars.Count > 0 ? System.Text.Encoding.UTF8.GetString(chars.ToArray()).Trim() : null;
    }

    /// <summary>Tipo do aparelho (função DEVICE_NAME, chamada 2).</summary>
    public string? DeviceType(byte device, byte nameIndex)
    {
        var r = Request(device, nameIndex, 2);
        return r is { Length: >= 1 } ? r[0] switch
        {
            0 => "keyboard", 1 => "remote", 2 => "numpad", 3 => "mouse", 4 => "trackpad", 5 => "trackball", 6 => "presenter",
            8 => "headset", 9 => "webcam", 12 => "gamepad", 14 => "speaker", 15 => "microphone", _ => null,
        } : null;
    }

    /// <summary>Número de série curto do aparelho (DEVICE_FW_VERSION, "unit id"), estável entre conexões.</summary>
    public string? UnitId(byte device, byte firmwareIndex)
    {
        var r = Request(device, firmwareIndex, 0);
        // Headsets G respondem FFFFFFFF (sem número): não serve como id.
        return r is { Length: >= 5 } && (r[1] | r[2] | r[3] | r[4]) != 0 && (r[1] & r[2] & r[3] & r[4]) != 0xFF ? Convert.ToHexString(r, 1, 4) : null;
    }

    // ------------------------------------------------------------------ bateria

    /// <summary>Lê a bateria pela melhor função disponível (o índice já foi descoberto).</summary>
    public BatteryStatus? Battery(byte device, ushort feature, byte index)
    {
        var r = Request(device, index, feature == FeatUnifiedBattery ? (byte)1 : (byte)0);
        if (r is null) return null;
        switch (feature)
        {
            case FeatUnifiedBattery when r.Length >= 3:
            {
                // [0] carga %, [1] nível (1 crítico, 2 baixo, 4 bom, 8 cheio), [2] carregamento
                var state = r[2] switch { 1 or 2 => "carregando", 3 => "carregada", 4 => "erro de carga", _ => "na bateria" };
                double? pct = r[0] > 0 ? r[0] : r[1] switch { 8 => 100, 4 => 60, 2 => 20, 1 => 5, _ => null };
                return new BatteryStatus(pct, state, null, "unified");
            }
            case FeatBatteryStatus when r.Length >= 3:
            {
                var state = r[2] switch { 1 or 4 => "carregando", 2 => "quase cheia", 3 => "carregada", 0 => "na bateria", _ => "erro de bateria" };
                return new BatteryStatus(r[0] is > 0 and <= 100 ? r[0] : null, state, null, "status");
            }
            case FeatBatteryVoltage or FeatAdcMeasurement when r.Length >= 3:
            {
                var mv = (r[0] << 8) | r[1];
                var flags = r[2];
                if (feature == FeatAdcMeasurement && (flags & 0x01) == 0) return new BatteryStatus(null, "desligado", null, "adc");
                var charging = feature == FeatAdcMeasurement ? (flags & 0x02) != 0 : (flags & 0x80) != 0;
                var pct = feature == FeatAdcMeasurement ? FromHeadsetVoltage(mv) : FromVoltage(mv);
                return new BatteryStatus(pct, charging ? "carregando" : "na bateria", mv, feature == FeatAdcMeasurement ? "adc" : "voltage");
            }
        }
        return null;
    }

    /// <summary>
    /// Headsets G (G733, G533, G933…): polinômio do projeto HeadsetControl, calibrado contra o G HUB
    /// (3,99 V → 94 %, o G HUB mostrava 96 %; a curva genérica de lítio dava 80 %).
    /// </summary>
    public static double FromHeadsetVoltage(int mv)
    {
        if (mv <= 3525) return Math.Clamp(Math.Round(0.03 * mv - 101), 0, 100);
        if (mv > 4030) return 100;
        double v = mv;
        var pct = 0.0000000037268473047 * Math.Pow(v, 4) - 0.00005605626214573775 * Math.Pow(v, 3)
                  + 0.3156051902814949 * v * v - 788.0937250298629 * v + 736315.3077118985;
        return Math.Clamp(Math.Round(pct), 0, 100);
    }

    /// <summary>Curva de descarga típica de bateria de lítio (Solaar), para mouses/teclados que só informam a tensão.</summary>
    public static double FromVoltage(int mv)
    {
        (int Mv, double Pct)[] curve =
        [
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40),
            (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        ];
        if (mv >= curve[0].Mv) return 100;
        for (var i = 1; i < curve.Length; i++)
        {
            if (mv < curve[i].Mv) continue;
            // interpolação linear entre os dois pontos da curva
            var (hiMv, hiPct) = curve[i - 1];
            var (loMv, loPct) = curve[i];
            return Math.Round(loPct + (mv - loMv) * (hiPct - loPct) / (hiMv - loMv));
        }
        return 0;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _short?.Dispose();
    }
}
