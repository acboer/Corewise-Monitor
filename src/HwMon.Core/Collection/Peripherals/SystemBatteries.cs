using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HwMon.Core.Collection.Peripherals;

/// <summary>
/// Baterias que o próprio Windows conhece, de qualquer marca:
///  • Bluetooth: fones, controles, mouses e teclados que informam a carga (a mesma porcentagem de Configurações ›
///    Bluetooth e dispositivos), lida da propriedade do aparelho no Gerenciador de Dispositivos.
///  • Controles Xbox pelo XInput (adaptador sem fio, cabo ou Bluetooth): o controle informa só 4 níveis.
/// </summary>
public static class SystemBatteries
{
    public sealed record Device(string Key, string Name, string? Type, string Connection, Func<BatteryStatus?> Read, bool Coarse);

    public static List<Device> Find(List<string> problems)
    {
        var list = new List<Device>();
        try
        {
            list.AddRange(Bluetooth());
        }
        catch (Exception ex)
        {
            problems.Add($"Periféricos: falha ao ler as baterias Bluetooth: {ex.GetType().Name}: {ex.Message}");
        }
        // Controle Xbox por Bluetooth já aparece acima, com a porcentagem exata: o XInput só entra sem ele.
        if (!list.Any(d => d.Type == "gamepad"))
        {
            try
            {
                list.AddRange(XInput());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Windows sem XInput 1.4 (versões N sem o pacote de mídia): sem controles Xbox.
            }
        }
        return list;
    }

    /// <summary>Muda quando um aparelho entra, sai ou passa a informar bateria (para a busca rápida).</summary>
    public static string Signature()
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var (instance, _, _) in BluetoothBatteries()) sb.Append(instance).Append('|');
        }
        catch (Exception)
        {
            // sem acesso: só o XInput conta
        }
        try
        {
            for (uint i = 0; i < 4; i++) sb.Append(XInputGetState(i, out _) == 0 ? '1' : '0');
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Bluetooth

    private static IEnumerable<Device> Bluetooth()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, name, _) in BluetoothBatteries())
        {
            // O mesmo aparelho pode ter vários nós (ex.: fone com perfil de chamada e de áudio): um por nome.
            if (!seen.Add(name)) continue;
            var key = "bt-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance.ToUpperInvariant())))[..10].ToLowerInvariant();
            yield return new Device(key, name, GuessType(name), "Bluetooth", () => ReadBluetooth(instance), Coarse: false);
        }
    }

    private static BatteryStatus? ReadBluetooth(string instance)
    {
        foreach (var (id, _, level) in BluetoothBatteries())
            if (string.Equals(id, instance, StringComparison.OrdinalIgnoreCase))
                return new BatteryStatus(level, "na bateria", null, "bluetooth");
        return new BatteryStatus(null, "desligado", null, "bluetooth"); // saiu da lista: desconectado
    }

    private static string? GuessType(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("controle") || n.Contains("gamepad") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("dualshock")) return "gamepad";
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere")) return "mouse";
        if (n.Contains("keyboard") || n.Contains("teclado") || n.Contains("mx keys")) return "keyboard";
        if (n.Contains("trackpad")) return "trackpad";
        if (n.Contains("head") || n.Contains("buds") || n.Contains("fone") || n.Contains("airpods") || n.Contains("earbuds") || n.Contains("wh-") || n.Contains("wf-")) return "headset";
        return null;
    }

    private static readonly DevPropKey BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DevPropKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly DevPropKey DescriptionKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    private static readonly DevPropKey InstanceIdKey = new(new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);

    /// <summary>Aparelhos presentes com a propriedade de bateria do Bluetooth (0–100 %).</summary>
    private static List<(string Instance, string Name, double Level)> BluetoothBatteries()
    {
        var list = new List<(string, string, double)>();
        var set = SetupDiGetClassDevs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x4 | 0x2); // ALLCLASSES | PRESENT
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try
        {
            var data = new SpDevInfoData { Size = Marshal.SizeOf<SpDevInfoData>() };
            var buffer = new byte[1024];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var key = BatteryKey;
                if (!SetupDiGetDeviceProperty(set, ref data, ref key, out var type, buffer, (uint)buffer.Length, out var size, 0)
                    || type != 0x3 || size < 1) continue; // DEVPROP_TYPE_BYTE
                var level = buffer[0];
                if (level > 100) continue;
                var instance = String(set, ref data, InstanceIdKey, buffer);
                var name = String(set, ref data, FriendlyNameKey, buffer) ?? String(set, ref data, DescriptionKey, buffer);
                if (instance is null || string.IsNullOrWhiteSpace(name)) continue;
                list.Add((instance, name.Trim(), level));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    private static string? String(IntPtr set, ref SpDevInfoData data, DevPropKey key, byte[] buffer)
    {
        if (!SetupDiGetDeviceProperty(set, ref data, ref key, out var type, buffer, (uint)buffer.Length, out var size, 0) || type != 0x12) return null;
        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length)).TrimEnd('\0');
    }

    // ------------------------------------------------------------------ XInput

    private static IEnumerable<Device> XInput()
    {
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out _) != 0) continue; // nada conectado nesse número
            var index = i;
            yield return new Device($"xinput-{i}", i == 0 ? "Controle Xbox" : $"Controle Xbox {i + 1}", "gamepad", "XInput", () => ReadXInput(index), Coarse: true);
        }
    }

    private static BatteryStatus? ReadXInput(uint index)
    {
        if (XInputGetState(index, out _) != 0) return new BatteryStatus(null, "desligado", null, "xinput");
        if (XInputGetBatteryInformation(index, 0, out var info) != 0) return null;
        return info.Type switch
        {
            0 => new BatteryStatus(null, "desligado", null, "xinput"),
            1 => new BatteryStatus(null, "com fio", null, "xinput"),
            // Só 4 níveis: vazio, baixo, médio, cheio.
            _ => new BatteryStatus(info.Level switch { 0 => 5, 1 => 30, 2 => 65, _ => 100 }, "na bateria", null, "xinput"),
        };
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBattery
    {
        public byte Type;
        public byte Level;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);
    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDevicePropertyW")]
    private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SpDevInfoData data, ref DevPropKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint index, out XInputState state);
    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint index, byte deviceType, out XInputBattery battery);
}
