namespace HwMon.Core.Model;

/// <summary>
/// Estado de uma leitura. "Zero" não é um estado: um valor 0 com estado Ok é uma leitura válida.
/// </summary>
public enum ReadingState
{
    /// <summary>Valor lido com sucesso neste instante.</summary>
    Ok,
    /// <summary>A fonte não oferece este sensor nesta máquina (ou não com as permissões atuais).</summary>
    Unavailable,
    /// <summary>Último valor conhecido é antigo demais para ser exibido como atual.</summary>
    Stale,
    /// <summary>A fonte existe, mas a leitura falhou (exceção, contador reiniciado etc.).</summary>
    Error,
}

/// <summary>
/// Uma leitura de telemetria: componente/instância/métrica, valor, unidade, horário, estado e origem.
/// </summary>
public sealed record Reading(
    string Component,
    string Instance,
    string Metric,
    double? Value,
    string Unit,
    string Source,
    ReadingState State,
    DateTimeOffset Timestamp,
    string? Note = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => $"{Component}/{Instance}/{Metric}@{Source}";

    public static Reading Ok(string component, string instance, string metric, double value, string unit, string source, string? note = null)
        => double.IsFinite(value)
            ? new(component, instance, metric, value, unit, source, ReadingState.Ok, DateTimeOffset.Now, note)
            : Unavailable(component, instance, metric, unit, source, "valor não numérico (NaN)");

    public static Reading Unavailable(string component, string instance, string metric, string unit, string source, string? note = null)
        => new(component, instance, metric, null, unit, source, ReadingState.Unavailable, DateTimeOffset.Now, note);

    public static Reading Failed(string component, string instance, string metric, string unit, string source, string note)
        => new(component, instance, metric, null, unit, source, ReadingState.Error, DateTimeOffset.Now, note);
}
