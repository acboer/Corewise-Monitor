using System.Runtime.InteropServices;
using System.Text;

namespace HwMon.Core.Collection;

/// <summary>Consultas de baixo nível para o resumo do sistema: topologia da CPU, firmware e link PCIe.</summary>
internal static partial class Native
{
    // ---------- Topologia da CPU (núcleos P/E e caches) ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    public sealed record CoreInfo(byte EfficiencyClass, bool Smt);
    public sealed record CacheInfo(int Level, int Type, uint SizeBytes);

    /// <summary>Núcleos físicos (classe de eficiência, SMT) e caches. Tipo de cache: 0 unificado, 1 instrução, 2 dados.</summary>
    public static (List<CoreInfo> Cores, List<CacheInfo> Caches) ProcessorTopology()
    {
        var cores = new List<CoreInfo>();
        var caches = new List<CacheInfo>();
        const int relationAll = 0xFFFF;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationAll, IntPtr.Zero, ref length);
        if (length == 0) return (cores, caches);
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationAll, buffer, ref length)) return (cores, caches);
            var offset = 0;
            while (offset < length)
            {
                var entry = buffer + offset;
                var relationship = Marshal.ReadInt32(entry);
                var size = Marshal.ReadInt32(entry, 4);
                if (size <= 0) break;
                switch (relationship)
                {
                    case 0: // RelationProcessorCore: Flags (1 = SMT), EfficiencyClass
                        cores.Add(new CoreInfo(Marshal.ReadByte(entry, 9), (Marshal.ReadByte(entry, 8) & 1) != 0));
                        break;
                    case 2: // RelationCache: Level, Associativity, LineSize, CacheSize, Type
                        caches.Add(new CacheInfo(Marshal.ReadByte(entry, 8), Marshal.ReadInt32(entry, 16), (uint)Marshal.ReadInt32(entry, 12)));
                        break;
                }
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return (cores, caches);
    }

    // ---------- Firmware ----------

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int type);

    /// <summary>"UEFI", "BIOS (legado)" ou nulo.</summary>
    public static string? FirmwareType() => GetFirmwareType(out var t) ? t switch { 1 => "BIOS (legado)", 2 => "UEFI", _ => null } : null;

    // ---------- Propriedades de dispositivo (link PCIe) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    // DEVPKEY_PciDevice_* (devpkey.h): 9 link atual, 10 largura atual, 11 link máximo, 12 largura máxima.
    private static readonly Guid PciDeviceFmtid = new("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");

    public sealed record PcieLink(int CurrentGen, int CurrentWidth, int MaxGen, int MaxWidth);

    /// <summary>
    /// Link PCIe do dispositivo ou do primeiro ancestral PCI (um disco NVMe aparece como SCSI\..., o controlador PCI
    /// é o pai). Nulo se não houver PCIe (SATA, USB) ou o Windows não informar.
    /// </summary>
    public static PcieLink? GetPcieLink(string pnpDeviceId)
    {
        if (CM_Locate_DevNodeW(out var dev, pnpDeviceId, 0) != 0) return null;
        for (var depth = 0; depth < 4; depth++)
        {
            var id = DeviceId(dev);
            if (id?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true)
            {
                var cur = ReadUInt(dev, 9);
                var curW = ReadUInt(dev, 10);
                var max = ReadUInt(dev, 11);
                var maxW = ReadUInt(dev, 12);
                return max is > 0 && maxW is > 0 ? new PcieLink((int)(cur ?? 0), (int)(curW ?? 0), (int)max.Value, (int)maxW.Value) : null;
            }
            if (CM_Get_Parent(out var parent, dev, 0) != 0) return null;
            dev = parent;
        }
        return null;
    }

    private static string? DeviceId(uint dev)
    {
        var sb = new StringBuilder(400);
        return CM_Get_Device_IDW(dev, sb, sb.Capacity, 0) == 0 ? sb.ToString() : null;
    }

    private static uint? ReadUInt(uint dev, uint pid)
    {
        var key = new DevPropKey { Fmtid = PciDeviceFmtid, Pid = pid };
        uint size = 4;
        var buffer = new byte[4];
        return CM_Get_DevNode_PropertyW(dev, ref key, out _, buffer, ref size, 0) == 0 && size >= 4 ? BitConverter.ToUInt32(buffer, 0) : null;
    }
}
