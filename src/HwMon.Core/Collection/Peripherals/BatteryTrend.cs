namespace HwMon.Core.Collection.Peripherals;

/// <summary>
/// Estima o tempo para a bateria encher (carregando) ou acabar (descarregando) pela tendência das leituras:
/// reta de mínimos quadrados sobre a % no tempo. Os aparelhos não informam esse tempo; é uma estimativa.
///   Carregando: últimos 30 min, pelo menos 8 min de dados e 2 pontos de % de variação.
///   Descarregando: últimas 6 h, pelo menos 30 min de dados e 2 pontos de variação (mouse gasta ~1 %/h).
/// Trocar de estado (pôs/tirou da base) ou ficar 30 min sem leitura recomeça a conta.
/// </summary>
public sealed class BatteryTrend
{
    private readonly List<(DateTimeOffset Time, double Pct)> _samples = new();
    private bool _charging;

    /// <summary>Acrescenta a leitura e devolve a estimativa em minutos (nula enquanto não há dados suficientes).</summary>
    public double? Add(DateTimeOffset time, double pct, bool charging)
    {
        if (_charging != charging || (_samples.Count > 0 && time - _samples[^1].Time > TimeSpan.FromMinutes(30)))
        {
            _samples.Clear();
            _charging = charging;
        }
        _samples.Add((time, pct));
        var window = charging ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(6);
        _samples.RemoveAll(s => time - s.Time > window);

        var span = _samples[^1].Time - _samples[0].Time;
        var minSpan = charging ? TimeSpan.FromMinutes(8) : TimeSpan.FromMinutes(30);
        if (_samples.Count < 3 || span < minSpan) return null;
        if (_samples.Max(s => s.Pct) - _samples.Min(s => s.Pct) < 2) return null;

        // Inclinação (pontos de % por minuto) por mínimos quadrados.
        var t0 = _samples[0].Time;
        var xs = _samples.Select(s => (s.Time - t0).TotalMinutes).ToArray();
        var ys = _samples.Select(s => s.Pct).ToArray();
        var mx = xs.Average();
        var my = ys.Average();
        var den = xs.Sum(x => (x - mx) * (x - mx));
        if (den <= 0) return null;
        var slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / den;
        // Valor atual suavizado (a leitura pela tensão oscila um pouco).
        var current = my + slope * (xs[^1] - mx);

        double minutes;
        if (charging)
        {
            if (slope <= 0.01) return null;
            minutes = (100 - Math.Min(100, current)) / slope;
        }
        else
        {
            if (slope >= -0.001) return null;
            minutes = Math.Max(0, current) / -slope;
        }
        return minutes is >= 0 and <= 14 * 24 * 60 ? Math.Round(minutes) : null;
    }
}
