using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HwMon.Core.Collection.Peripherals;

/// <summary>Uma coleção HID (um "pedaço" de um dispositivo USB): caminho, ids e tamanhos de relatório.</summary>
public sealed record HidInfo(string Path, ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage,
    int InputLength, int OutputLength, string? Product, int FeatureLength = 0);

/// <summary>Acesso direto a dispositivos HID do Windows (hid.dll + setupapi), sem drivers extras.</summary>
public static class HidDevice
{
    public static List<HidInfo> Enumerate(ushort? vendorId = null)
    {
        var list = new List<HidInfo>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x10 | 0x2); // DEVICEINTERFACE | PRESENT
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try
        {
            var iface = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size == 0) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (path is null) continue;
                    if (Describe(path) is { } info && (vendorId is null || info.VendorId == vendorId)) list.Add(info);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    /// <summary>Lê ids e capacidades sem pedir acesso de leitura/escrita (funciona até para teclado e mouse).</summary>
    private static HidInfo? Describe(string path)
    {
        using var handle = CreateFile(path, 0, 0x1 | 0x2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var attrs = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attrs)) return null;
        if (!HidD_GetPreparsedData(handle, out var pre)) return null;
        HidpCaps caps;
        try
        {
            if (HidP_GetCaps(pre, out caps) != 0x110000) return null; // HIDP_STATUS_SUCCESS
        }
        finally
        {
            HidD_FreePreparsedData(pre);
        }
        var buffer = new byte[256];
        var product = HidD_GetProductString(handle, buffer, buffer.Length) ? Encoding.Unicode.GetString(buffer).TrimEnd('\0') : null;
        return new HidInfo(path, attrs.VendorId, attrs.ProductId, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, product,
            caps.FeatureReportByteLength);
    }

    /// <summary>Lê um relatório "feature" (diagnóstico de protocolos desconhecidos). Nulo se o dispositivo recusar.</summary>
    public static byte[]? GetFeature(string path, byte reportId, int length)
    {
        using var handle = CreateFile(path, 0x80000000 | 0x40000000, 0x1 | 0x2, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || length <= 0) return null;
        var buffer = new byte[length];
        buffer[0] = reportId;
        return HidD_GetFeature(handle, buffer, buffer.Length) ? buffer : null;
    }

    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

    /// <summary>Abre para leitura e escrita assíncronas (com prazo). Nulo se o Windows negar.</summary>
    public static FileStream? Open(string path)
    {
        var handle = CreateFile(path, 0x80000000 | 0x40000000, 0x1 | 0x2, IntPtr.Zero, 3, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    // ------------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
