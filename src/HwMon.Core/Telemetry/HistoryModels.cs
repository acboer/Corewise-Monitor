namespace HwMon.Core.Telemetry;

/// <summary>Resposta de /v1/history: uma métrica num período, já reduzida a no máximo N pontos.</summary>
public sealed class HistoryResponse
{
    public string Component { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Tabela usada: "10s", "1min" ou "1h".</summary>
    public string Resolution { get; set; } = "";
    /// <summary>Largura de cada ponto, em segundos.</summary>
    public int StepSeconds { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public List<HistoryPoint> Points { get; set; } = new();
    public HistoryStats? Stats { get; set; }
    /// <summary>Início da gravação do histórico neste coletor (Unix, segundos).</summary>
    public long? Since { get; set; }
}

/// <summary>Um ponto do gráfico: média, mínimo e máximo do intervalo que começa em T (Unix, segundos).</summary>
public sealed class HistoryPoint
{
    public long T { get; set; }
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class HistoryStats
{
    public double Min { get; set; }
    public long MinT { get; set; }
    public double Max { get; set; }
    public long MaxT { get; set; }
    public double Avg { get; set; }
    /// <summary>Segundos efetivamente medidos no período (o PC pode ter ficado desligado).</summary>
    public long Seconds { get; set; }
}

/// <summary>Resposta de /v1/history/stats: mínimo, média e máximo de todas as métricas num período (cartões).</summary>
public sealed class HistoryStatsResponse
{
    public long From { get; set; }
    public long To { get; set; }
    public long? Since { get; set; }
    public List<SeriesStats> Series { get; set; } = new();
}

/// <summary>Resposta de /v1/processes: os mais pesados em CPU, RAM e disco (união dos três "top").</summary>
public sealed class ProcessesResponse
{
    public DateTimeOffset Time { get; set; }
    public int LogicalCores { get; set; }
    /// <summary>Intervalo medido (0 na primeira chamada: CPU e disco ainda sem base).</summary>
    public double IntervalSeconds { get; set; }
    public int ProcessCount { get; set; }
    public List<ProcessUsage> Processes { get; set; } = new();
}

public sealed class ProcessUsage
{
    public string Name { get; set; } = "";
    /// <summary>Quantos processos com esse nome (abas do navegador, etc.).</summary>
    public int Count { get; set; }
    /// <summary>% da CPU inteira (100 = todos os núcleos), como no Gerenciador de Tarefas.</summary>
    public double CpuPercent { get; set; }
    public double RamGb { get; set; }
    /// <summary>Leitura + escrita (inclui rede e dispositivos, como a coluna "E/S" do Windows).</summary>
    public double DiskMBs { get; set; }
}

public sealed class SeriesStats
{
    public string Component { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Min { get; set; }
    public double Avg { get; set; }
    public double Max { get; set; }
}
