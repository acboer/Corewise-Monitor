namespace HwMon.Core.Model;

public sealed class InventoryReport
{
    public string SchemaVersion { get; init; } = "0.1";
    public required SystemInfo System { get; init; }
    public required string Scenario { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; set; }
    public double IntervalSeconds { get; init; }
    public int SampleCount { get; set; }
    public bool SyntheticCpuLoad { get; init; }

    public List<CpuInfo> Cpus { get; } = new();
    public MemoryInfo? Memory { get; set; }
    public List<GpuInfo> Gpus { get; } = new();
    public List<PhysicalDiskInfo> PhysicalDisks { get; } = new();
    public List<VolumeInfo> Volumes { get; } = new();
    public List<StoragePoolInfo> StoragePools { get; } = new();
    public List<NetworkAdapterInfo> NetworkAdapters { get; } = new();

    /// <summary>Resumo de cada métrica observada durante a captura (por fonte).</summary>
    public List<MetricSummary> Metrics { get; } = new();

    /// <summary>Todos os sensores que o LibreHardwareMonitor expôs, mapeados ou não.</summary>
    public List<LhmSensorInfo> LhmSensors { get; } = new();

    /// <summary>Falhas de coleta que não impediram o relatório.</summary>
    public List<string> Problems { get; } = new();

    public CollectorCost? Cost { get; set; }
}

public sealed class SystemInfo
{
    public string MachineId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string? OsCaption { get; set; }
    public string? OsBuild { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Motherboard { get; set; }
    public string User { get; set; } = "";
    public bool IsElevated { get; set; }
    public bool IsLocalSystem { get; set; }
    public bool? HvciRunning { get; set; }
    public bool PawnIoInstalled { get; set; }
    public string? LhmVersion { get; set; }
    public double UptimeHours { get; set; }
}

public sealed class CpuInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int? Cores { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? BaseClockMhz { get; set; }
}

public sealed class MemoryInfo
{
    public double TotalGb { get; set; }
    public List<MemoryModuleInfo> Modules { get; } = new();
}

public sealed class MemoryModuleInfo
{
    public string? Locator { get; set; }
    public string? Bank { get; set; }
    public double CapacityGb { get; set; }
    public int? SpeedMts { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public string? Type { get; set; }
}

public sealed class GpuInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string SubSysId { get; set; } = "";
    public string Luid { get; set; } = "";
    public double DedicatedMemoryGb { get; set; }
    /// <summary>Integrada (memória dedicada &lt; 1 GB): não tem sensor de temperatura próprio; usa a do pacote da CPU.</summary>
    public bool Integrated { get; set; }
    /// <summary>
    /// LUIDs extras do mesmo adaptador. O DXGI lista a mesma GPU duas vezes em alguns casos
    /// (visto no NAS acessado por Área de Trabalho Remota).
    /// </summary>
    public List<string> AliasLuids { get; } = new();
    public string? LhmIdentifier { get; set; }
}

public enum DiskPowerState { Unknown, On, Standby }

public sealed class PhysicalDiskInfo
{
    public int? Number { get; set; }
    public string DeviceId { get; set; } = "";
    public string StableId { get; set; } = "";
    public string? FriendlyName { get; set; }
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? Firmware { get; set; }
    public string? MediaType { get; set; }
    public string? BusType { get; set; }
    public double SizeGb { get; set; }
    public string? Health { get; set; }
    public string? OperationalStatus { get; set; }
    public string? Pool { get; set; }

    /// <summary>Estado de energia antes de qualquer consulta SMART desta ferramenta.</summary>
    public DiskPowerState PowerBefore { get; set; }
    /// <summary>Depois da consulta SMART via WMI (MSFT_StorageReliabilityCounter).</summary>
    public DiskPowerState PowerAfterWmiSmart { get; set; }
    /// <summary>Depois de abrir o LibreHardwareMonitor (que também lê SMART).</summary>
    public DiskPowerState PowerAfterLhm { get; set; }
    /// <summary>Como o estado foi lido: "ATA" (CHECK POWER MODE, confiável), "Windows" (GetDevicePowerState) ou "nenhum".</summary>
    public string? PowerMethod { get; set; }

    public bool ReliabilityAvailable { get; set; }
    public string? ReliabilityError { get; set; }
    public string? LhmIdentifier { get; set; }
    public string? LhmMatch { get; set; }
}

public sealed class VolumeInfo
{
    public string Key { get; set; } = "";
    public string Path { get; set; } = "";
    public string? DriveLetter { get; set; }
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public double SizeGb { get; set; }
    public double FreeGb { get; set; }
    public string? Health { get; set; }
    public List<int> DiskNumbers { get; } = new();
}

public sealed class StoragePoolInfo
{
    public string Name { get; set; } = "";
    public string? Health { get; set; }
    public double SizeGb { get; set; }
    public double AllocatedGb { get; set; }
    public List<string> MemberDiskStableIds { get; } = new();
    public List<VirtualDiskInfo> VirtualDisks { get; } = new();
}

public sealed class VirtualDiskInfo
{
    public string Name { get; set; } = "";
    public string? Resiliency { get; set; }
    public int? DiskNumber { get; set; }
    public string? Health { get; set; }
}

public sealed class NetworkAdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public double SpeedMbps { get; set; }
    public bool? Physical { get; set; }
    public string? Mac { get; set; }
    public List<string> IPv4 { get; } = new();
    public List<string> Gateways { get; } = new();
}

public sealed class MetricSummary
{
    public string Component { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Source { get; set; } = "";
    public string Unit { get; set; } = "";
    public int Samples { get; set; }
    public int Ok { get; set; }
    public int Unavailable { get; set; }
    public int Errors { get; set; }
    public double? Min { get; set; }
    public double? Avg { get; set; }
    public double? Max { get; set; }
    public double? Last { get; set; }
    public string? Note { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ReadingState Overall => Ok > 0 ? ReadingState.Ok : Errors > 0 ? ReadingState.Error : ReadingState.Unavailable;
}

public sealed class LhmSensorInfo
{
    public string HardwareType { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public string Sensor { get; set; } = "";
    public string SensorType { get; set; } = "";
    public string SensorId { get; set; } = "";
    public double? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool Mapped { get; set; }
}

public sealed class CollectorCost
{
    public double InitSeconds { get; set; }
    public double LhmOpenSeconds { get; set; }
    public double AvgTickMs { get; set; }
    public double MaxTickMs { get; set; }
    public Dictionary<string, double> AvgTickMsBySource { get; set; } = new();
    public double ProcessCpuPercent { get; set; }
    public double WorkingSetMb { get; set; }
    public double PeakWorkingSetMb { get; set; }
}
