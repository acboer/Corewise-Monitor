using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

internal static partial class Native
{
    // ---------- Memória ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    public static MemoryStatusEx? GetMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status : null;
    }

    // ---------- Estado de energia do disco (não acorda o disco: handle sem acesso de leitura) ----------

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDevicePowerState(SafeFileHandle device, [MarshalAs(UnmanagedType.Bool)] out bool on);

    /// <summary>
    /// Estado de energia sem acordar o disco. Primeiro tenta o comando ATA CHECK POWER MODE (0xE5), o mesmo do
    /// "smartctl -n standby": responde mesmo com o disco parado e não o faz girar. Precisa de administrador e de
    /// disco SATA/ATA (USB e NVMe costumam recusar). Se falhar, cai para GetDevicePowerState, que nem sempre
    /// enxerga o repouso de discos SATA (no piloto, retornou "ligado" para tudo).
    /// </summary>
    public static (DiskPowerState State, string Method) GetDiskPowerState(int diskNumber)
    {
        var ata = AtaCheckPowerMode(diskNumber);
        if (ata != DiskPowerState.Unknown) return (ata, "ATA");

        const uint shareReadWrite = 0x1 | 0x2;
        const uint openExisting = 3;
        using var handle = CreateFile($@"\\.\PhysicalDrive{diskNumber}", 0, shareReadWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return (DiskPowerState.Unknown, "nenhum");
        return GetDevicePowerState(handle, out var on)
            ? (on ? DiskPowerState.On : DiskPowerState.Standby, "Windows")
            : (DiskPowerState.Unknown, "nenhum");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AtaPassThroughEx
    {
        public ushort Length;
        public ushort AtaFlags;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte ReservedAsUchar;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public uint ReservedAsUlong;
        public nuint DataBufferOffset;
        public ulong PreviousTaskFile; // 8 registradores, 1 byte cada
        public ulong CurrentTaskFile;  // [0] features [1] sector count [2..4] LBA [5] device [6] command/status [7] reservado
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref AtaPassThroughEx input, uint inputSize,
        ref AtaPassThroughEx output, uint outputSize, out uint returned, IntPtr overlapped);

    private static DiskPowerState AtaCheckPowerMode(int diskNumber)
    {
        const uint genericReadWrite = 0x80000000 | 0x40000000;
        const uint shareReadWrite = 0x1 | 0x2;
        const uint openExisting = 3;
        const uint ioctlAtaPassThrough = 0x0004D02C;
        const ushort ataFlagsDrdyRequired = 0x01;
        const byte checkPowerMode = 0xE5;

        using var handle = CreateFile($@"\\.\PhysicalDrive{diskNumber}", genericReadWrite, shareReadWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return DiskPowerState.Unknown;

        var size = (uint)Marshal.SizeOf<AtaPassThroughEx>();
        var request = new AtaPassThroughEx
        {
            Length = (ushort)size,
            AtaFlags = ataFlagsDrdyRequired,
            TimeOutValue = 10,
            CurrentTaskFile = (ulong)checkPowerMode << 48,
        };
        var response = request;
        if (!DeviceIoControl(handle, ioctlAtaPassThrough, ref request, size, ref response, size, out _, IntPtr.Zero))
            return DiskPowerState.Unknown;

        var status = (byte)(response.CurrentTaskFile >> 48);
        if ((status & 0x01) != 0) return DiskPowerState.Unknown; // bit ERR
        var mode = (byte)(response.CurrentTaskFile >> 8);       // sector count = modo de energia
        return mode switch
        {
            0x00 or 0x01 => DiskPowerState.Standby,             // standby (girando parado)
            0x80 or 0x81 or 0x82 or 0x83 or 0xFF or 0x40 or 0x41 => DiskPowerState.On, // idle/ativo
            _ => DiskPowerState.Unknown,
        };
    }

    // ---------- DXGI: lista de GPUs com LUID (para casar com os contadores "GPU Engine") ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject (apenas para manter a ordem da vtable)
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIFactory
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    public static List<DxgiAdapterDesc1> EnumerateGpus()
    {
        const uint softwareAdapter = 0x2;
        var list = new List<DxgiAdapterDesc1>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return list;
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) >= 0; i++)
            {
                try
                {
                    if (adapter.GetDesc1(out var desc) >= 0 && (desc.Flags & softwareAdapter) == 0)
                        list.Add(desc);
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
        return list;
    }

    public static string FormatLuid(int high, uint low) => $"0x{high:X8}_0x{low:X8}";
}
