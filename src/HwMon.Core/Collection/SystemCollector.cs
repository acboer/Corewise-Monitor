using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>Identidade da máquina, permissões e inventário de CPU, memória e GPU.</summary>
public static class SystemCollector
{
    public static SystemInfo CollectSystem(List<string> problems)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var info = new SystemInfo
        {
            HostName = Environment.MachineName,
            User = identity.Name,
            IsElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            IsLocalSystem = identity.IsSystem,
            UptimeHours = Math.Round(Environment.TickCount64 / 3_600_000.0, 2),
            LhmVersion = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version?.ToString(),
        };

        // MachineGuid é estável entre reinícios e trocas de rede; o nome do PC pode mudar.
        using (var crypto = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
            info.MachineId = crypto?.GetValue("MachineGuid") as string ?? info.HostName;

        info.PawnIoInstalled =
            Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO") is not null ||
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO") is not null;

        Try(problems, "Win32_OperatingSystem", () =>
        {
            var os = Wmi.Query(Wmi.Cimv2, "SELECT Caption, BuildNumber, Version FROM Win32_OperatingSystem").FirstOrDefault();
            if (os is null) return;
            info.OsCaption = Wmi.Str(os, "Caption");
            using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var display = cv?.GetValue("DisplayVersion") as string;
            var ubr = cv?.GetValue("UBR");
            info.OsBuild = $"{Wmi.Str(os, "BuildNumber")}.{ubr} ({display})";
        });

        Try(problems, "Win32_ComputerSystem", () =>
        {
            var cs = Wmi.Query(Wmi.Cimv2, "SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
            if (cs is not null) { info.Manufacturer = Wmi.Str(cs, "Manufacturer"); info.Model = Wmi.Str(cs, "Model"); }
            var bb = Wmi.Query(Wmi.Cimv2, "SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
            if (bb is not null) info.Motherboard = $"{Wmi.Str(bb, "Manufacturer")} {Wmi.Str(bb, "Product")}".Trim();
        });

        Try(problems, "DeviceGuard (HVCI)", () =>
        {
            var dg = Wmi.Query(Wmi.DeviceGuard, "SELECT SecurityServicesRunning FROM Win32_DeviceGuard").FirstOrDefault();
            if (dg is null) return;
            var running = Wmi.Get<uint[]>(dg, "SecurityServicesRunning") ?? [];
            info.HvciRunning = running.Contains(2u); // 2 = Integridade de Memória (HVCI)
        });

        return info;
    }

    public static List<CpuInfo> CollectCpus(List<string> problems)
    {
        var list = new List<CpuInfo>();
        Try(problems, "Win32_Processor", () =>
        {
            var i = 0;
            foreach (var p in Wmi.Query(Wmi.Cimv2, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
            {
                list.Add(new CpuInfo
                {
                    Index = i++,
                    Name = Wmi.Str(p, "Name") ?? "CPU",
                    Cores = Wmi.Get<int?>(p, "NumberOfCores"),
                    LogicalProcessors = Wmi.Get<int?>(p, "NumberOfLogicalProcessors"),
                    BaseClockMhz = Wmi.Get<int?>(p, "MaxClockSpeed"),
                });
            }
        });
        return list;
    }

    public static MemoryInfo CollectMemory(List<string> problems)
    {
        var info = new MemoryInfo();
        if (Native.GetMemoryStatus() is { } status) info.TotalGb = Math.Round(status.TotalPhys / 1_073_741_824.0, 2);
        Try(problems, "Win32_PhysicalMemory", () =>
        {
            foreach (var m in Wmi.Query(Wmi.Cimv2, "SELECT BankLabel, DeviceLocator, Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            {
                info.Modules.Add(new MemoryModuleInfo
                {
                    Bank = Wmi.Str(m, "BankLabel"),
                    Locator = Wmi.Str(m, "DeviceLocator"),
                    CapacityGb = Math.Round((Wmi.Get<ulong?>(m, "Capacity") ?? 0) / 1_073_741_824.0, 2),
                    SpeedMts = Wmi.Get<int?>(m, "ConfiguredClockSpeed") ?? Wmi.Get<int?>(m, "Speed"),
                    Manufacturer = Wmi.Str(m, "Manufacturer"),
                    PartNumber = Wmi.Str(m, "PartNumber"),
                    Type = (Wmi.Get<int?>(m, "SMBIOSMemoryType")) switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", null => null, var t => $"tipo {t}" },
                });
            }
        });
        return info;
    }

    public static List<GpuInfo> CollectGpus(List<string> problems)
    {
        var list = new List<GpuInfo>();
        Try(problems, "DXGI", () =>
        {
            var i = 0;
            foreach (var d in Native.EnumerateGpus())
            {
                // Mesma GPU listada de novo com outro LUID (ex.: sessão remota): vira apelido da primeira.
                // Limitação conhecida: duas placas idênticas também seriam unidas; tratar quando houver esse caso.
                var luid = Native.FormatLuid(d.LuidHigh, d.LuidLow);
                var same = list.FirstOrDefault(g => g.Name == d.Description.Trim()
                    && g.VendorId == d.VendorId.ToString("X4", CultureInfo.InvariantCulture)
                    && g.DeviceId == d.DeviceId.ToString("X4", CultureInfo.InvariantCulture)
                    && g.SubSysId == d.SubSysId.ToString("X8", CultureInfo.InvariantCulture));
                if (same is not null)
                {
                    same.AliasLuids.Add(luid);
                    continue;
                }
                list.Add(new GpuInfo
                {
                    // A Radeon integrada do Ryzen declara 2 GB dedicados: o nome também conta.
                    Integrated = d.DedicatedVideoMemory < 1_073_741_824
                                 || System.Text.RegularExpressions.Regex.IsMatch(d.Description, @"UHD|Iris|Radeon\(TM\) Graphics|Radeon Graphics|Vega \d+ Graphics", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    Index = i++,
                    Name = d.Description.Trim(),
                    VendorId = d.VendorId.ToString("X4", CultureInfo.InvariantCulture),
                    DeviceId = d.DeviceId.ToString("X4", CultureInfo.InvariantCulture),
                    SubSysId = d.SubSysId.ToString("X8", CultureInfo.InvariantCulture),
                    Luid = luid,
                    DedicatedMemoryGb = Math.Round(d.DedicatedVideoMemory / 1_073_741_824.0, 2),
                });
            }
        });
        return list;
    }

    public static IEnumerable<Reading> SampleMemory()
    {
        if (Native.GetMemoryStatus() is not { } s)
        {
            yield return Reading.Failed("ram", "0", "used", "GB", "win32", "GlobalMemoryStatusEx falhou");
            yield break;
        }
        const double gb = 1_073_741_824.0;
        yield return Reading.Ok("ram", "0", "used", (s.TotalPhys - s.AvailPhys) / gb, "GB", "win32");
        yield return Reading.Ok("ram", "0", "available", s.AvailPhys / gb, "GB", "win32");
        yield return Reading.Ok("ram", "0", "total", s.TotalPhys / gb, "GB", "win32");
        yield return Reading.Ok("ram", "0", "load", s.MemoryLoad, "%", "win32");
    }

    internal static void Try(List<string> problems, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { problems.Add($"{what}: {ex.GetType().Name}: {ex.Message}"); }
    }
}
