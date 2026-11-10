using System.Text.Json.Serialization;

namespace HwMon.Core.Telemetry;

/// <summary>Uma sessão de jogo gravada pelo coletor: do primeiro ao último quadro do programa.</summary>
public sealed class GameSession
{
    public long Id { get; set; }
    /// <summary>Chave do jogo: nome do executável em minúsculas (ex.: "cyberpunk2077.exe").</summary>
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Segundos Unix.</summary>
    public long Start { get; set; }
    public long End { get; set; }
    /// <summary>Segundos em que o jogo estava desenhando quadros (sem as pausas longas).</summary>
    public long ActiveSeconds { get; set; }
    /// <summary>Ainda jogando (a sessão é atualizada a cada minuto).</summary>
    public bool Open { get; set; }
    public long Frames { get; set; }
    public double FpsAvg { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double FrametimeAvgMs { get; set; }
    public double FrametimeMaxMs { get; set; }
    /// <summary>Quadros que demoraram mais que o dobro da média (engasgos perceptíveis).</summary>
    public long Stutters { get; set; }
    /// <summary>CPU + placa de vídeo, em Wh (o resto do PC não tem sensor).</summary>
    public double? EnergyWh { get; set; }
    public string? Gpu { get; set; }
    public string? Cpu { get; set; }
    /// <summary>Média e máximo dos sensores durante a sessão.</summary>
    public List<SessionMetric> Metrics { get; set; } = new();
    /// <summary>Um ponto a cada 5 s (só no detalhe da sessão).</summary>
    public List<GamePoint>? Points { get; set; }
}

public sealed class SessionMetric
{
    /// <summary>gpu_usage, gpu_temp, gpu_power, gpu_clock, vram, cpu_usage, cpu_temp, cpu_power, ram.</summary>
    public string Key { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Avg { get; set; }
    public double Max { get; set; }
}

/// <summary>Ponto de 5 s de uma sessão (nomes curtos: uma sessão de 3 h tem ~2.000 pontos).</summary>
public sealed class GamePoint
{
    [JsonPropertyName("t")] public long T { get; set; }
    /// <summary>FPS médio nos 5 s.</summary>
    [JsonPropertyName("f")] public double Fps { get; set; }
    /// <summary>1 % low (janela de 10 s).</summary>
    [JsonPropertyName("l")] public double Low1 { get; set; }
    [JsonPropertyName("gu")] public double? GpuUsage { get; set; }
    [JsonPropertyName("gt")] public double? GpuTemp { get; set; }
    [JsonPropertyName("gp")] public double? GpuPower { get; set; }
    [JsonPropertyName("cu")] public double? CpuUsage { get; set; }
    [JsonPropertyName("ct")] public double? CpuTemp { get; set; }
}

/// <summary>Totais de um jogo no período.</summary>
public sealed class GameSummary
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public int Sessions { get; set; }
    public long Seconds { get; set; }
    public long LastPlayed { get; set; }
    /// <summary>Média ponderada pelo tempo de cada sessão.</summary>
    public double FpsAvg { get; set; }
    public double Low1Fps { get; set; }
    public double? EnergyWh { get; set; }
}

public sealed class GamesResponse
{
    public long From { get; set; }
    public long To { get; set; }
    /// <summary>Gravando (sessão de eventos ligada). Falso + Error: o coletor não consegue ler os quadros.</summary>
    public bool Recording { get; set; }
    public string? Error { get; set; }
    public List<GameSummary> Games { get; set; } = new();
    /// <summary>Sessões do período, mais recentes primeiro (sem os pontos).</summary>
    public List<GameSession> Sessions { get; set; } = new();
    /// <summary>Programas que o usuário mandou ignorar (não são gravados nem listados).</summary>
    public List<GameSummary> Ignored { get; set; } = new();
}

/// <summary>Limpar o histórico de uma máquina (POST /v1/history/clear).</summary>
public sealed class HistoryClearRequest
{
    /// <summary>Sensores e uso consolidado (historico.db).</summary>
    public bool Sensors { get; set; }
    /// <summary>Sessões de jogo (jogos.db). Nomes escolhidos e programas ignorados continuam.</summary>
    public bool Games { get; set; }
}

/// <summary>Renomear ou ignorar um jogo.</summary>
public sealed class GameUpdate
{
    public string Exe { get; set; } = "";
    /// <summary>Nome novo; vazio = volta ao nome detectado.</summary>
    public string? Name { get; set; }
    public bool? Ignored { get; set; }
}

/// <summary>Uso consolidado de uma máquina, dia a dia (/v1/usage).</summary>
public sealed class UsageResponse
{
    public long From { get; set; }
    public long To { get; set; }
    /// <summary>Desde quando o histórico existe neste coletor.</summary>
    public long Since { get; set; }
    /// <summary>Resolução usada nas contas (1min até 30 dias, 1h além disso).</summary>
    public string Resolution { get; set; } = "";
    public string? Cpu { get; set; }
    public string? Gpu { get; set; }
    public List<UsageDay> Days { get; set; } = new();
    public UsagePeak? CpuTempPeak { get; set; }
    public UsagePeak? GpuTempPeak { get; set; }
}

public sealed class UsagePeak
{
    public double Value { get; set; }
    public long T { get; set; }
}

public sealed class UsageDay
{
    /// <summary>Meia-noite (fuso do coletor), em segundos Unix.</summary>
    public long Date { get; set; }
    /// <summary>Tempo com o coletor gravando (= PC ligado).</summary>
    public long OnSeconds { get; set; }
    public double? CpuUsageAvg { get; set; }
    public double? GpuUsageAvg { get; set; }
    public double? CpuTempAvg { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTempAvg { get; set; }
    public double? GpuTempMax { get; set; }
    /// <summary>CPU + placas de vídeo (Wh).</summary>
    public double? EnergyWh { get; set; }
    /// <summary>Tempo com CPU ou GPU acima de 80 % (média do minuto).</summary>
    public long CpuHeavySeconds { get; set; }
    public long GpuHeavySeconds { get; set; }
    public double NetRxMB { get; set; }
    public double NetTxMB { get; set; }
    public double DiskReadMB { get; set; }
    public double DiskWriteMB { get; set; }
    public long GameSeconds { get; set; }
}
