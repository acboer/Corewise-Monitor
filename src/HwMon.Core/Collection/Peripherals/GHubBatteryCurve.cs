using System.Globalization;
using System.Xml.Linq;

namespace HwMon.Core.Collection.Peripherals;

/// <summary>
/// Curva de bateria do próprio G HUB para um aparelho Logitech, lida da instalação do G HUB neste PC
/// (<c>%ProgramData%\LGHUB\depots\…\&lt;nome&gt;_&lt;pid&gt;\battery_&lt;pid&gt;.xml</c>). Nada é copiado para o Corewise:
/// sem o G HUB instalado, fica a fórmula genérica.
/// <list type="bullet">
/// <item>Descarga: tensão sob carga × energia já gasta (mWh); % restante = 1 − gasto ÷ total.</item>
/// <item>Carga: tensão no carregador → % de carga. Na carga a tensão quase não muda (3,990 V ≈ 10 %, 3,998 V ≈ 100 %),
/// então a curva de descarga nunca chegava a 100 % com o headset no carregador.</item>
/// </list>
/// O G HUB ainda soma o consumo desde a última carga (com a iluminação ligada ou não), o que esta curva não sabe;
/// por isso a porcentagem pode ficar alguns pontos diferente da dele.
/// </summary>
public sealed class GHubBatteryCurve
{
    private readonly (double Volts, double Percent)[] _discharge; // tensão decrescente
    private readonly (double Volts, double Percent)[] _charging;  // tensão crescente

    private GHubBatteryCurve((double, double)[] discharge, (double, double)[] charging)
    {
        _discharge = discharge;
        _charging = charging;
    }

    public bool HasCharging => _charging.Length >= 2;

    public double? Percent(int millivolts, bool charging)
    {
        var v = millivolts / 1000.0;
        if (charging && HasCharging)
        {
            // O headset informa milivolts inteiros e o topo da tabela tem casas a mais (3,99837 V): 3998 mV já é "cheia",
            // senão a carga parava em 96 % para sempre.
            if (millivolts >= Math.Floor(_charging[^1].Volts * 1000)) return 100;
            if (v <= _charging[0].Volts) return Math.Round(_charging[0].Percent);
            return Math.Round(Interpolate(_charging, v, ascending: true));
        }
        if (_discharge.Length < 2) return null;
        if (v >= _discharge[0].Volts) return 100;
        if (v <= _discharge[^1].Volts) return 0;
        return Math.Round(Interpolate(_discharge, v, ascending: false));
    }

    private static double Interpolate((double Volts, double Percent)[] curve, double v, bool ascending)
    {
        for (var i = 1; i < curve.Length; i++)
        {
            var (v0, p0) = curve[i - 1];
            var (v1, p1) = curve[i];
            var inside = ascending ? v >= v0 && v <= v1 : v <= v0 && v >= v1;
            if (!inside) continue;
            if (Math.Abs(v1 - v0) < 1e-9) return Math.Max(p0, p1);
            return p0 + (v - v0) * (p1 - p0) / (v1 - v0);
        }
        return curve[^1].Percent;
    }

    /// <summary>Lê o XML no formato do G HUB (pontos em CDATA, uma linha por ponto). Nulo se não der para usar.</summary>
    public static GHubBatteryCurve? Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var battery = doc.Root?.Element("battery");
        if (battery is null) return null;

        var discharge = new List<(double, double)>();
        var rows = Rows(battery.Element("discharge")?.Value).Where(r => r.Length >= 3).ToList();
        if (rows.Count >= 2)
        {
            var total = rows[^1][2];
            if (total > 0)
            {
                discharge.Add((rows[0][1], 100)); // início da curva = cheia
                foreach (var r in rows) discharge.Add((r[1], 100 * (1 - r[2] / total)));
            }
        }
        // Tensão sempre decrescente: pontos repetidos ou fora de ordem (ruído da medição) ficam de fora.
        var cleanDischarge = new List<(double V, double P)>();
        foreach (var p in discharge)
            if (cleanDischarge.Count == 0 || p.Item1 < cleanDischarge[^1].V) cleanDischarge.Add((p.Item1, p.Item2));
            else if (Math.Abs(p.Item1 - cleanDischarge[^1].V) < 1e-9) cleanDischarge[^1] = (p.Item1, p.Item2);

        var charging = Rows(battery.Element("charging")?.Value).Where(r => r.Length >= 2)
            .Select(r => (V: r[0], P: Math.Min(100, r[1]))).ToList();
        // Tensão crescente; na mesma tensão fica a maior carga (o G HUB mostra a carga ao atingir aquela tensão).
        var cleanCharging = new List<(double V, double P)>();
        foreach (var p in charging.OrderBy(p => p.V).ThenBy(p => p.P))
            if (cleanCharging.Count > 0 && Math.Abs(p.V - cleanCharging[^1].V) < 1e-9) cleanCharging[^1] = p;
            else if (cleanCharging.Count == 0 || p.P >= cleanCharging[^1].P) cleanCharging.Add(p);

        if (cleanDischarge.Count < 2 && cleanCharging.Count < 2) return null;
        return new GHubBatteryCurve(cleanDischarge.Select(p => (p.V, p.P)).ToArray(), cleanCharging.Select(p => (p.V, p.P)).ToArray());
    }

    private static IEnumerable<double[]> Rows(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            var values = new double[parts.Length];
            var ok = parts.Length > 0;
            for (var i = 0; i < parts.Length && ok; i++)
                ok = double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
            if (ok) yield return values;
        }
    }

    // ------------------------------------------------------------------ busca na instalação do G HUB

    private static readonly Dictionary<ushort, (DateTime Read, GHubBatteryCurve? Curve)> Cache = new();
    private static readonly object CacheLock = new();

    /// <summary>Curva do aparelho com este product id, se o G HUB estiver instalado. Relida a cada hora (atualizações do G HUB).</summary>
    public static GHubBatteryCurve? For(ushort productId)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(productId, out var cached) && DateTime.UtcNow - cached.Read < TimeSpan.FromHours(1)) return cached.Curve;
            GHubBatteryCurve? curve = null;
            try
            {
                var depots = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LGHUB", "depots");
                if (Directory.Exists(depots))
                {
                    var pid = productId.ToString("x4", CultureInfo.InvariantCulture);
                    var file = Directory.EnumerateDirectories(depots)
                        .SelectMany(d => Directory.EnumerateDirectories(d, "*_" + pid))
                        .Select(d => Path.Combine(d, $"battery_{pid}.xml"))
                        .Where(File.Exists)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (file is not null) curve = Parse(File.ReadAllText(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // G HUB em atualização ou arquivo diferente do esperado: fica a fórmula genérica
            }
            Cache[productId] = (DateTime.UtcNow, curve);
            return curve;
        }
    }
}
