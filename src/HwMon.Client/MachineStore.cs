using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HwMon.Client;

public sealed class PairedMachine
{
    /// <summary>Apelido local (padrão: nome do PC).</summary>
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string MachineId { get; set; } = "";
    /// <summary>SHA-256 do certificado do coletor, fixado no pareamento.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Token protegido com DPAPI do usuário do Windows (só este usuário neste PC consegue ler).</summary>
    public string ProtectedToken { get; set; } = "";
    public DateTimeOffset PairedAt { get; set; }
    /// <summary>
    /// IPs que já funcionaram. Usados junto com o nome: com VPN ativa, o DNS da VPN não conhece "NAS" e a conexão
    /// segue pelo IP; se o IP mudar (DHCP), o nome resolve o novo.
    /// </summary>
    public List<string> KnownAddresses { get; set; } = new();
}

/// <summary>PCs pareados deste usuário, em %AppData%\HwMon\maquinas.json.</summary>
public sealed class MachineStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public MachineStore(string? directory = null)
    {
        var dir = directory ?? HwMon.Core.AppPaths.UserData;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "maquinas.json");
        ClientIdPath = Path.Combine(dir, "cliente-id.txt");
    }

    private string ClientIdPath { get; }

    /// <summary>Identificador deste cliente (fixo), enviado no pareamento.</summary>
    public string ClientId
    {
        get
        {
            if (File.Exists(ClientIdPath)) return File.ReadAllText(ClientIdPath).Trim();
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(ClientIdPath, id);
            return id;
        }
    }

    public List<PairedMachine> List()
        => File.Exists(_path) ? JsonSerializer.Deserialize<List<PairedMachine>>(File.ReadAllText(_path)) ?? new() : new();

    public PairedMachine? Find(string? nameOrHost)
    {
        var all = List();
        if (string.IsNullOrWhiteSpace(nameOrHost)) return all.Count == 1 ? all[0] : null;
        return all.FirstOrDefault(m => m.Name.Equals(nameOrHost, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(m => m.Host.Equals(nameOrHost, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(PairedMachine machine, string token)
    {
        machine.ProtectedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        var all = List();
        all.RemoveAll(m => m.MachineId == machine.MachineId || m.Name.Equals(machine.Name, StringComparison.OrdinalIgnoreCase));
        all.Add(machine);
        Write(all);
    }

    /// <summary>Troca o apelido local de uma máquina. Falso se o nome já é de outra máquina pareada.</summary>
    public bool Rename(string machineId, string newName)
    {
        lock (WriteLock)
        {
            var all = List();
            if (all.Any(m => m.MachineId != machineId && m.Name.Equals(newName, StringComparison.OrdinalIgnoreCase))) return false;
            var target = all.FirstOrDefault(m => m.MachineId == machineId);
            if (target is null) return false;
            target.Name = newName;
            Write(all);
            return true;
        }
    }

    public bool Remove(string nameOrHost)
    {
        var all = List();
        var removed = all.RemoveAll(m => m.Name.Equals(nameOrHost, StringComparison.OrdinalIgnoreCase) || m.Host.Equals(nameOrHost, StringComparison.OrdinalIgnoreCase));
        Write(all);
        return removed > 0;
    }

    private static readonly object WriteLock = new();

    /// <summary>Memoriza o IP que acabou de funcionar (mais recente primeiro, até 3).</summary>
    public void RememberAddress(string machineId, string address)
    {
        lock (WriteLock)
        {
            var all = List();
            var m = all.FirstOrDefault(x => x.MachineId == machineId);
            if (m is null || m.KnownAddresses.FirstOrDefault() == address) return;
            m.KnownAddresses.Remove(address);
            m.KnownAddresses.Insert(0, address);
            if (m.KnownAddresses.Count > 3) m.KnownAddresses.RemoveRange(3, m.KnownAddresses.Count - 3);
            Write(all);
        }
    }

    public static string Token(PairedMachine machine)
        => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(machine.ProtectedToken), null, DataProtectionScope.CurrentUser));

    private void Write(List<PairedMachine> all) => File.WriteAllText(_path, JsonSerializer.Serialize(all, Json));
}
