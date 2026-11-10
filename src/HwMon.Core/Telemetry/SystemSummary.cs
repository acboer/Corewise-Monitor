namespace HwMon.Core.Telemetry;

/// <summary>
/// Resumo do sistema (estilo CPU-Z) de /v1/system. Genérico de propósito (seção → grupo → linhas rótulo/valor):
/// o coletor pode ganhar campos novos sem mudar o painel. Lido uma vez ao iniciar o coletor e quando o cliente pede
/// para atualizar.
/// </summary>
public sealed class SystemSummary
{
    public string HostName { get; set; } = "";
    public DateTimeOffset CollectedAt { get; set; }
    public List<SummarySection> Sections { get; set; } = new();
    /// <summary>Fontes que falharam (ex.: TPM sem permissão). Só para diagnóstico.</summary>
    public List<string> Problems { get; set; } = new();
}

public sealed class SummarySection
{
    /// <summary>Estável: os, board, cpu, memory, gpu, storage, network, display, audio, power.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Linha curta para o topo da seção (ex.: "AMD Ryzen 9 9950X · 16 núcleos").</summary>
    public string? Headline { get; set; }
    public List<SummaryGroup> Groups { get; set; } = new();
}

public sealed class SummaryGroup
{
    /// <summary>Nulo quando a seção tem um grupo só (ex.: Processador); senão o nome do item (pente, disco, placa).</summary>
    public string? Title { get; set; }
    public List<SummaryItem> Items { get; set; } = new();
}

public sealed class SummaryItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";

    public SummaryItem() { }
    public SummaryItem(string label, string value) => (Label, Value) = (label, value);
}
