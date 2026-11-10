namespace HwMon.Core.Telemetry;

/// <summary>Pedido de teste de disco: volume (letra) e tamanho do arquivo de teste.</summary>
public sealed class DiskBenchRequest
{
    /// <summary>Letra do volume, ex.: "C:" ou "D".</summary>
    public string Drive { get; set; } = "";
    public int SizeMb { get; set; } = 1024;
    /// <summary>Segundos de cada medição (leitura e escrita de cada teste).</summary>
    public int Seconds { get; set; } = 5;
}

/// <summary>Um teste no estilo CrystalDiskMark (ex.: SEQ1M Q8T1) com leitura e escrita.</summary>
public sealed class DiskBenchLine
{
    public string Test { get; set; } = "";
    public double? ReadMBs { get; set; }
    public double? WriteMBs { get; set; }
    public double? ReadIops { get; set; }
    public double? WriteIops { get; set; }
    /// <summary>Latência média por operação (µs), mais útil nos testes Q1.</summary>
    public double? ReadLatencyUs { get; set; }
    public double? WriteLatencyUs { get; set; }
}

public sealed class DiskBenchResult
{
    public string Id { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string Drive { get; set; } = "";
    public string? VolumeLabel { get; set; }
    public string? DiskName { get; set; }
    public int SizeMb { get; set; }
    public int Seconds { get; set; }
    public List<DiskBenchLine> Lines { get; set; } = new();
}

/// <summary>Estado do teste em andamento (ou do último), para a barra de progresso.</summary>
public sealed class DiskBenchStatus
{
    public string? Id { get; set; }
    /// <summary>idle, running, done, failed, canceled.</summary>
    public string State { get; set; } = "idle";
    public string? Phase { get; set; }
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DiskBenchResult? Result { get; set; }
    /// <summary>Durante o teste: as linhas já medidas (e a atual, com o que já terminou).</summary>
    public List<DiskBenchLine>? Partial { get; set; }
    /// <summary>Velocidade da medição em andamento (MB/s), qual teste e se é escrita.</summary>
    public double? LiveMBs { get; set; }
    public string? LiveTest { get; set; }
    public bool LiveWrite { get; set; }
}

/// <summary>Pedido de teste de carga: cpu, memoria, gpu ou estabilidade.</summary>
public sealed class BenchRequest
{
    public string Kind { get; set; } = "";
    /// <summary>Estabilidade: duração em minutos.</summary>
    public int Minutes { get; set; } = 10;
    /// <summary>Estabilidade: estressar também a placa de vídeo.</summary>
    public bool Gpu { get; set; }
}

/// <summary>Um número do resultado (ex.: "1 núcleo" 1000 pts, "Leitura" 62 GB/s).</summary>
public sealed class BenchScore
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>Sensores a cada segundo durante o teste (para o gráfico e o resumo).</summary>
public sealed class BenchSample
{
    [System.Text.Json.Serialization.JsonPropertyName("t")] public long T { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("cu")] public double? CpuUsage { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("cc")] public double? CpuClock { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ct")] public double? CpuTemp { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("cp")] public double? CpuPower { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gu")] public double? GpuUsage { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gc")] public double? GpuClock { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gt")] public double? GpuTemp { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gp")] public double? GpuPower { get; set; }
}

public sealed class BenchResult
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public double Seconds { get; set; }
    /// <summary>Processador ou placa de vídeo testada, e detalhes (threads, tamanho do bloco…).</summary>
    public string? Device { get; set; }
    public string? Detail { get; set; }
    public List<BenchScore> Scores { get; set; } = new();
    /// <summary>Frases do resultado (ex.: "clock caiu 8 % no fim", "nenhum erro de cálculo").</summary>
    public List<string> Notes { get; set; } = new();
    /// <summary>Estabilidade: erros de cálculo encontrados (0 = estável).</summary>
    public int Errors { get; set; }
    /// <summary>Estabilidade: parou antes do fim (temperatura, cancelado, erro).</summary>
    public string? StoppedReason { get; set; }
    public List<BenchSample> Samples { get; set; } = new();
}

/// <summary>Teste de carga em andamento (ou o último), para a barra de progresso e os números ao vivo.</summary>
public sealed class BenchStatus
{
    public string? Id { get; set; }
    public string? Kind { get; set; }
    /// <summary>idle, running, done, failed, canceled.</summary>
    public string State { get; set; } = "idle";
    public string? Phase { get; set; }
    public double Progress { get; set; }
    public string? Error { get; set; }
    public List<BenchScore>? Partial { get; set; }
    public List<BenchSample>? Samples { get; set; }
    public BenchResult? Result { get; set; }
}

/// <summary>Resposta de /v1/frames: quem está desenhando quadros agora (jogos, navegadores, players).</summary>
public sealed class FramesResponse
{
    /// <summary>Sessão de eventos ligada (precisa do coletor como serviço).</summary>
    public bool Running { get; set; }
    /// <summary>Acabou de ligar: os números aparecem em ~2 s.</summary>
    public bool WarmingUp { get; set; }
    public string? Error { get; set; }
    public List<FrameStats> Processes { get; set; } = new();
}

public sealed class FrameStats
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Quadros por segundo (média do último segundo).</summary>
    public double Fps { get; set; }
    public double FrametimeMs { get; set; }
    /// <summary>"1 % low": FPS equivalente ao percentil 99 do frametime nos últimos 10 s.</summary>
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public double MaxFrametimeMs { get; set; }
    /// <summary>Frametimes recentes (ms), para o gráfico.</summary>
    public List<double> Frametimes { get; set; } = new();
    public double WindowSeconds { get; set; }
}

/// <summary>Resultado do teste de rede entre este PC (painel) e um coletor.</summary>
public sealed class NetBenchResult
{
    public string Id { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Address { get; set; }
    /// <summary>Do coletor para este PC.</summary>
    public double DownloadMbps { get; set; }
    /// <summary>Deste PC para o coletor.</summary>
    public double UploadMbps { get; set; }
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public int Streams { get; set; }
    public int Seconds { get; set; }
    /// <summary>Velocidade do link do adaptador usado neste PC (Mbit/s), para comparar.</summary>
    public double? LinkMbps { get; set; }
    public string? Adapter { get; set; }
}
