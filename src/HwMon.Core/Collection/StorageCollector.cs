using System.Globalization;
using System.Management;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>
/// Inventário de armazenamento via Storage Management API (WMI): discos físicos, pools, discos virtuais
/// e volumes, com a relação volume → disco. Saúde/temperatura/desgaste vêm de MSFT_StorageReliabilityCounter.
/// </summary>
public sealed class StorageCollector
{
    private const string Source = "wmi";
    private readonly List<string> _problems;
    private readonly List<(PhysicalDiskInfo Info, ManagementObject Wmi)> _disks = new();

    public List<PhysicalDiskInfo> PhysicalDisks { get; } = new();
    public List<VolumeInfo> Volumes { get; } = new();
    public List<StoragePoolInfo> Pools { get; } = new();

    public StorageCollector(List<string> problems) => _problems = problems;

    private static readonly bool IsElevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public static string DiskKey(PhysicalDiskInfo d) => d.Number?.ToString(CultureInfo.InvariantCulture) ?? "dev" + d.DeviceId;

    private bool _checkPower = true;

    /// <param name="checkPower">
    /// Registrar o estado de energia dos discos. O serviço desliga isso: a consulta ATA pode reiniciar a contagem de
    /// ociosidade do Windows e impedir que os HDDs entrem em repouso.
    /// </param>
    public void Discover(bool checkPower = true)
    {
        _checkPower = checkPower;
        SystemCollector.Try(_problems, "MSFT_PhysicalDisk", DiscoverPhysicalDisks);
        SystemCollector.Try(_problems, "MSFT_StoragePool", DiscoverPools);
        SystemCollector.Try(_problems, "MSFT_Volume", DiscoverVolumes);
    }

    private void DiscoverPhysicalDisks()
    {
        foreach (var pd in Wmi.Query(Wmi.Storage, "SELECT * FROM MSFT_PhysicalDisk"))
        {
            var deviceId = Wmi.Str(pd, "DeviceId") ?? "";
            var info = new PhysicalDiskInfo
            {
                DeviceId = deviceId,
                Number = int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
                FriendlyName = Wmi.Str(pd, "FriendlyName"),
                Model = Wmi.Str(pd, "Model"),
                Serial = Wmi.Str(pd, "SerialNumber"),
                Firmware = Wmi.Str(pd, "FirmwareVersion"),
                MediaType = MediaTypeName(Wmi.Get<int?>(pd, "MediaType")),
                BusType = BusTypeName(Wmi.Get<int?>(pd, "BusType")),
                SizeGb = Math.Round((Wmi.Get<ulong?>(pd, "Size") ?? 0) / 1e9, 1),
                Health = HealthName(Wmi.Get<int?>(pd, "HealthStatus")),
                OperationalStatus = string.Join(", ", (Wmi.Get<ushort[]>(pd, "OperationalStatus") ?? []).Select(OperationalName)),
            };
            // NVMe costuma vir com ponto final no número de série ("…3DEB_91F3.").
            info.Serial = info.Serial?.Trim().TrimEnd('.');
            info.StableId = info.Serial ?? Wmi.Str(pd, "UniqueId") ?? "dev" + deviceId;
            if (_checkPower && info.Number is { } num) (info.PowerBefore, info.PowerMethod) = Native.GetDiskPowerState(num);
            _disks.Add((info, pd));
            PhysicalDisks.Add(info);
        }

        // Número de série repetido (alguns adaptadores USB) não serve como identificador estável.
        foreach (var dup in PhysicalDisks.GroupBy(d => d.StableId).Where(g => g.Count() > 1).SelectMany(g => g))
        {
            var unique = Wmi.Str(_disks.First(x => x.Info == dup).Wmi, "UniqueId");
            dup.StableId = unique ?? "dev" + dup.DeviceId;
        }
        PhysicalDisks.Sort((a, b) => (a.Number ?? int.MaxValue).CompareTo(b.Number ?? int.MaxValue));
    }

    private void DiscoverPools()
    {
        foreach (var pool in Wmi.Query(Wmi.Storage, "SELECT * FROM MSFT_StoragePool WHERE IsPrimordial = FALSE"))
        {
            var info = new StoragePoolInfo
            {
                Name = Wmi.Str(pool, "FriendlyName") ?? "Pool",
                Health = HealthName(Wmi.Get<int?>(pool, "HealthStatus")),
                SizeGb = Math.Round((Wmi.Get<ulong?>(pool, "Size") ?? 0) / 1e9, 1),
                AllocatedGb = Math.Round((Wmi.Get<ulong?>(pool, "AllocatedSize") ?? 0) / 1e9, 1),
            };
            foreach (var member in Wmi.Related(pool, "MSFT_PhysicalDisk"))
            {
                var deviceId = Wmi.Str(member, "DeviceId");
                var disk = PhysicalDisks.FirstOrDefault(d => d.DeviceId == deviceId);
                if (disk is null) continue;
                disk.Pool = info.Name;
                info.MemberDiskStableIds.Add(disk.StableId);
            }
            foreach (var vd in Wmi.Related(pool, "MSFT_VirtualDisk"))
            {
                var vInfo = new VirtualDiskInfo
                {
                    Name = Wmi.Str(vd, "FriendlyName") ?? "",
                    Resiliency = Wmi.Str(vd, "ResiliencySettingName"),
                    Health = HealthName(Wmi.Get<int?>(vd, "HealthStatus")),
                };
                var disk = Wmi.Related(vd, "MSFT_Disk").FirstOrDefault();
                if (disk is not null) vInfo.DiskNumber = Wmi.Get<int?>(disk, "Number");
                info.VirtualDisks.Add(vInfo);
            }
            Pools.Add(info);
        }
    }

    private void DiscoverVolumes()
    {
        var partitions = Wmi.Query(Wmi.Storage, "SELECT DiskNumber, AccessPaths FROM MSFT_Partition")
            .Select(p => (Disk: Wmi.Get<int?>(p, "DiskNumber"), Paths: Wmi.Get<string[]>(p, "AccessPaths") ?? []))
            .ToList();

        foreach (var v in Wmi.Query(Wmi.Storage, "SELECT * FROM MSFT_Volume"))
        {
            var size = Wmi.Get<ulong?>(v, "Size") ?? 0;
            var driveType = Wmi.Get<int?>(v, "DriveType");
            if (size == 0 || driveType is 5) continue; // sem mídia / CD-ROM

            var letterRaw = Wmi.Get<object>(v, "DriveLetter")?.ToString();
            var letter = string.IsNullOrWhiteSpace(letterRaw) || letterRaw == "\0" ? null : letterRaw;
            var path = Wmi.Str(v, "Path") ?? "";
            var info = new VolumeInfo
            {
                Path = path,
                DriveLetter = letter,
                Label = Wmi.Str(v, "FileSystemLabel"),
                FileSystem = Wmi.Str(v, "FileSystem"),
                SizeGb = Math.Round(size / 1e9, 2),
                FreeGb = Math.Round((Wmi.Get<ulong?>(v, "SizeRemaining") ?? 0) / 1e9, 2),
                Health = HealthName(Wmi.Get<int?>(v, "HealthStatus")),
            };
            var guid = path.Replace(@"\\?\Volume", "").Trim('{', '}', '\\');
            info.Key = letter ?? info.Label ?? (guid.Length > 8 ? guid[..8] : guid);
            if (Volumes.Any(x => x.Key == info.Key)) info.Key += "-" + (guid.Length > 4 ? guid[..4] : guid);
            foreach (var p in partitions.Where(p => p.Disk is not null && p.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                info.DiskNumbers.Add(p.Disk!.Value);
            Volumes.Add(info);
        }
        Volumes.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Atualiza o estado de energia gravado na fase indicada (para detectar consultas que acordam discos).</summary>
    public void RecordPower(Action<PhysicalDiskInfo, DiskPowerState> setter)
    {
        foreach (var d in PhysicalDisks)
            setter(d, d.Number is { } n ? Native.GetDiskPowerState(n).State : DiskPowerState.Unknown);
    }

    /// <summary>Leituras lentas: saúde, SMART (temperatura, desgaste, horas) e espaço dos volumes.</summary>
    public IEnumerable<Reading> SampleSlow()
        => PhysicalDisks.SelectMany(d => SampleDisk(d, smart: true)).Concat(SampleVolumes()).ToList();

    /// <summary>
    /// Saúde (guardada pelo Windows, não fala com o disco) e, se <paramref name="smart"/>, o SMART via WMI,
    /// que envia comando ao disco e pode acordá-lo.
    /// </summary>
    public List<Reading> SampleDisk(PhysicalDiskInfo disk, bool smart)
    {
        var readings = new List<Reading>();
        foreach (var (info, pd) in _disks.Where(x => x.Info == disk))
        {
            var key = DiskKey(info);
            try
            {
                pd.Get(); // relê a instância para pegar a saúde atual
                var health = Wmi.Get<int?>(pd, "HealthStatus");
                readings.Add(health is { } h
                    ? Reading.Ok("disk", key, "health", h, "código", Source, HealthName(h))
                    : Reading.Unavailable("disk", key, "health", "código", Source));
            }
            catch (Exception ex)
            {
                readings.Add(Reading.Failed("disk", key, "health", "código", Source, ex.Message));
            }

            if (!smart) continue;
            try
            {
                var rel = Wmi.Related(pd, "MSFT_StorageReliabilityCounter").FirstOrDefault();
                if (rel is null)
                {
                    info.ReliabilityAvailable = false;
                    info.ReliabilityError ??= IsElevated
                        ? "o driver do disco não expõe SMART pelo Windows"
                        : "sem retorno — exige administrador";
                    foreach (var (m, u) in new[] { ("temp", "°C"), ("wear", "%"), ("power_on_hours", "h") })
                        readings.Add(Reading.Unavailable("disk", key, m, u, Source, info.ReliabilityError));
                    continue;
                }
                info.ReliabilityAvailable = true;
                var temp = Wmi.Get<int?>(rel, "Temperature");
                readings.Add(temp is > 0
                    ? Reading.Ok("disk", key, "temp", temp.Value, "°C", Source)
                    : Reading.Unavailable("disk", key, "temp", "°C", Source, "não reportada (0 ou vazio)"));
                var wear = Wmi.Get<int?>(rel, "Wear");
                readings.Add(wear is { } w && (w > 0 || info.MediaType == "SSD")
                    ? Reading.Ok("disk", key, "wear", w, "%", Source, "% de vida usada")
                    : Reading.Unavailable("disk", key, "wear", "%", Source, "não reportado"));
                var hours = Wmi.Get<long?>(rel, "PowerOnHours");
                readings.Add(hours is > 0
                    ? Reading.Ok("disk", key, "power_on_hours", hours.Value, "h", Source)
                    : Reading.Unavailable("disk", key, "power_on_hours", "h", Source, "não reportado"));
            }
            catch (Exception ex)
            {
                info.ReliabilityAvailable = false;
                info.ReliabilityError = ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied } || ex is UnauthorizedAccessException
                    ? "acesso negado (precisa de administrador)"
                    : $"{ex.GetType().Name}: {ex.Message}";
                foreach (var (m, u) in new[] { ("temp", "°C"), ("wear", "%"), ("power_on_hours", "h") })
                    readings.Add(Reading.Failed("disk", key, m, u, Source, info.ReliabilityError));
            }
        }
        return readings;
    }

    /// <summary>Espaço dos volumes (o Windows mantém esses números em memória).</summary>
    public List<Reading> SampleVolumes()
    {
        var readings = new List<Reading>();
        try
        {
            foreach (var v in Wmi.Query(Wmi.Storage, "SELECT Path, Size, SizeRemaining FROM MSFT_Volume"))
            {
                var vol = Volumes.FirstOrDefault(x => x.Path == Wmi.Str(v, "Path"));
                if (vol is null) continue;
                var size = (Wmi.Get<ulong?>(v, "Size") ?? 0) / 1e9;
                var free = (Wmi.Get<ulong?>(v, "SizeRemaining") ?? 0) / 1e9;
                readings.Add(Reading.Ok("vol", vol.Key, "total", size, "GB", Source));
                readings.Add(Reading.Ok("vol", vol.Key, "free", free, "GB", Source));
                readings.Add(Reading.Ok("vol", vol.Key, "used", size - free, "GB", Source));
            }
        }
        catch (Exception ex)
        {
            _problems.Add($"Espaço dos volumes: {ex.Message}");
        }
        return readings;
    }

    public static string? MediaTypeName(int? v) => v switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", 0 => "Não especificado", null => null, _ => $"tipo {v}" };

    public static string? BusTypeName(int? v) => v switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "Arquivo virtual", 16 => "Storage Spaces", 17 => "NVMe",
        null => null, _ => $"barramento {v}",
    };

    public static string? HealthName(int? v) => v switch { 0 => "Saudável", 1 => "Atenção", 2 => "Com problema", 5 => "Desconhecida", null => null, _ => $"código {v}" };

    private static string OperationalName(ushort v) => v switch
    {
        2 => "OK", 3 => "Degradado", 5 => "Falha prevista", 6 => "Erro", 10 => "Parado", 0xD010 => "Online",
        0xD011 => "Não pronto", 0xD012 => "Sem mídia", 0xD013 => "Offline", 0xD014 => "Falhou", 0xD016 => "Comunicação perdida",
        _ => $"0x{v:X}",
    };
}
