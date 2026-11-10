using HwMon.Core.Model;

namespace HwMon.Core.Telemetry;

public static class Protocol
{
    public const int DefaultPort = 47821;
    public const int ApiVersion = 1;
    public const string ServiceName = "Corewise";
    /// <summary>Nome do serviço até a 0.11 (o instalador remove).</summary>
    public const string LegacyServiceName = "HwMonCollector";
}

/// <summary>Tipos de componente. O id de cada componente é estável entre reinícios (número de série, GUID...).</summary>
public static class ComponentKind
{
    public const string Cpu = "cpu";
    public const string Ram = "ram";
    public const string Gpu = "gpu";
    public const string Disk = "disk";
    public const string Volume = "volume";
    public const string Network = "network";
    public const string Fan = "fan";
    /// <summary>Periférico sem fio com bateria (headset, mouse, teclado). Detalhe: "Headset · Lightspeed".</summary>
    public const string Peripheral = "peripheral";
}

/// <summary>Resposta pública de /v1/info (sem autenticação): só o necessário para parear.</summary>
public sealed class AgentInfo
{
    public string MachineId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string? Role { get; set; }
    public string AgentVersion { get; set; } = "";
    public int ApiVersion { get; set; } = Protocol.ApiVersion;
    public string CertificateFingerprint { get; set; } = "";
}

public sealed class PairRequest
{
    public string Code { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientName { get; set; } = "";
}

public sealed class PairResponse
{
    public string Token { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string HostName { get; set; } = "";
}

/// <summary>Pareamento por aceite (0.13+): resposta ao pedido; o id serve para perguntar o resultado.</summary>
public sealed class PairAskResponse
{
    public string Id { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string HostName { get; set; } = "";
}

/// <summary>pending, approved (com o token), denied ou expired.</summary>
public sealed class PairAskStatus
{
    public string State { get; set; } = "pending";
    public string? Token { get; set; }
}

/// <summary>Pedido esperando o "Permitir?" no painel desta máquina.</summary>
public sealed class PairPending
{
    public string Id { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string? Address { get; set; }
    public DateTimeOffset Time { get; set; }
}

/// <summary>Código de pareamento gerado pelo painel desta máquina (sem precisar do atalho do menu Iniciar).</summary>
public sealed class PairCodeResponse
{
    public string Code { get; set; } = "";
    public int Minutes { get; set; }
    public string HostName { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
}

/// <summary>O que existe na máquina. Muda raramente; o snapshot traz a versão para o cliente saber quando recarregar.</summary>
public sealed class MachineDescriptor
{
    public string MachineId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string? Role { get; set; }
    public string? Os { get; set; }
    public string? Model { get; set; }
    /// <summary>Placa-mãe com o fabricante abreviado ("MSI MAG X870 TOMAHAWK WIFI"). Coletor 0.7.1+.</summary>
    public string? Board { get; set; }
    public string AgentVersion { get; set; } = "";
    public int Version { get; set; }
    public List<ComponentDescriptor> Components { get; set; } = new();
}

public sealed class ComponentDescriptor
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public int Order { get; set; }
    /// <summary>Componente pai (volume → disco).</summary>
    public List<string> Parents { get; set; } = new();
}

/// <summary>Leituras atuais: uma por componente e métrica, já com a melhor fonte escolhida.</summary>
public sealed class TelemetrySnapshot
{
    public string MachineId { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public long Sequence { get; set; }
    public int DescriptorVersion { get; set; }
    public List<TelemetryValue> Values { get; set; } = new();
}

public sealed class TelemetryValue
{
    public string Component { get; set; } = "";
    public string Metric { get; set; } = "";
    public double? Value { get; set; }
    public string Unit { get; set; } = "";
    public ReadingState State { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Source { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>Regras de escolha de fonte, compartilhadas pelo serviço e pela matriz do inventário.</summary>
public static class MetricPolicy
{
    // Em geral a fonte nativa (sem driver, mais barata) vem antes. Exceções onde o LHM é mais fiel:
    // desgaste de NVMe (o WMI devolveu 0% para um disco com 8% de vida usada no piloto).
    // O clock da CPU fica com o contador nativo: é o mesmo número do Gerenciador de Tarefas.
    public static readonly HashSet<string> PreferLhm = ["gpu.clock", "cpu.temp", "gpu.temp", "disk.wear"];

    public static int SourceRank(string component, string metric, string source)
        => PreferLhm.Contains($"{component}.{metric}") ? (source == "lhm" ? 0 : 1) : (source == "lhm" ? 1 : 0);
}
