using System.Globalization;
using HwMon.Core;
using HwMon.Core.Telemetry;

namespace HwMon.Client;

/// <summary>Formatação no idioma da interface (<see cref="L"/>), compartilhada pelo painel, pelo widget e pelo hwmon.exe.</summary>
public static class Format
{
    /// <summary>Cultura do idioma da interface (o nome ficou da época em que era só português).</summary>
    public static CultureInfo Pt => L.Culture;

    /// <summary>Data e hora curtas: 08/11/2026 14:30 ou 11/08/2026 2:30 PM.</summary>
    public static string When(DateTimeOffset when) => when.LocalDateTime.ToString(L.English ? "MM/dd/yyyy h:mm tt" : "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Dia e mês: 08/11 ou 11/08.</summary>
    public static string DayMonth(System.DateTime when) => when.ToString(L.English ? "MM/dd" : "dd/MM", CultureInfo.InvariantCulture);

    public static string Value(TelemetryValue? v) => v?.Value is { } value ? Number(value, v.Unit) : "—";

    public static string Number(double value, string unit) => unit switch
    {
        "min" => Duration(value),
        "MHz" when value >= 1000 => (value / 1000).ToString("0.00", Pt) + " GHz",
        "%" or "°C" or "RPM" or "W" or "MHz" or "h" or "FPS" => value.ToString("N0", Pt) + " " + unit,
        "Wh" when value >= 1000 => (value / 1000).ToString(value >= 10000 ? "0.0" : "0.00", Pt) + " kWh",
        "Wh" => value.ToString("N0", Pt) + " Wh",
        "MB" when value >= 1_000_000 => (value / 1_000_000).ToString("0.00", Pt) + " TB",
        "MB" when value >= 1000 => (value / 1000).ToString(value >= 100_000 ? "N0" : "0.0", Pt) + " GB",
        "GB" when value >= 1000 => (value / 1000).ToString("0.00", Pt) + " TB",
        "Mbit/s" when value >= 1000 => (value / 1000).ToString("0.00", Pt) + " Gbit/s",
        _ => value.ToString(value >= 100 ? "N0" : value >= 10 ? "0.0" : "0.##", Pt) + " " + unit,
    };

    public static string MetricLabel(string metric) => L.T(MetricLabelPt(metric));

    private static string MetricLabelPt(string metric) => metric switch
    {
        "usage" => "Uso",
        "clock" => "Clock",
        "temp" => "Temperatura",
        "temp_hotspot" => "Hot spot",
        "power" => "Consumo",
        "used" => "Usado",
        "available" => "Disponível",
        "total" => "Total",
        "load" => "Uso",
        "vram_used" => "VRAM usada",
        "vram_total" => "VRAM total",
        "active" => "Tempo ativo",
        "read" => "Leitura",
        "write" => "Escrita",
        "health" => "Saúde",
        "wear" => "Desgaste",
        "power_on_hours" => "Horas ligado",
        "free" => "Livre",
        "used_pct" => "Ocupado",
        "rx" => "Recebendo",
        "tx" => "Enviando",
        "rx_today" => "Recebido hoje",
        "tx_today" => "Enviado hoje",
        "rx_total" => "Recebido (desde o boot)",
        "tx_total" => "Enviado (desde o boot)",
        "fan" => "Ventoinha",
        "rpm" => "Rotação",
        "battery" => "Bateria",
        "duty" or "fan_duty" => "Controle da ventoinha",
        "charge_eta" => "Tempo para carregar",
        "runtime" => "Bateria restante",
        _ => metric,
    };

    /// <summary>Minutos → "~40 min", "~2 h 15 min", "~3 dias".</summary>
    public static string Duration(double minutes)
    {
        var m = (int)Math.Round(minutes);
        return m switch
        {
            < 60 => $"~{Math.Max(1, m)} min",
            < 24 * 60 => m % 60 >= 5 ? $"~{m / 60} h {m % 60 / 5 * 5} min" : $"~{m / 60} h",
            _ => "~" + L.T("{0:0.#} dias", m / 1440.0),
        };
    }

    public static string Ago(DateTimeOffset when)
    {
        var s = (DateTimeOffset.Now - when).TotalSeconds;
        return s < 5 ? L.T("agora") : s < 60 ? L.T("há {0:0} s", s) : s < 3600 ? L.T("há {0:0} min", s / 60) : DayMonth(when.LocalDateTime) + when.ToString(" HH:mm", Pt);
    }
}
