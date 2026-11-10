using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using HwMon.Core.Telemetry;

namespace HwMon.Collector;

public sealed class CollectorConfig
{
    public int Port { get; set; } = Protocol.DefaultPort;
    /// <summary>Papel mostrado no painel, ex.: "Workstation" ou "NAS".</summary>
    public string? Role { get; set; }
    public bool LhmEnabled { get; set; } = true;
    /// <summary>Ids estáveis (número de série) dos discos cujo SMART nunca deve ser lido.</summary>
    public List<string> SmartDisabledDisks { get; set; } = new();
    public int SmartIntervalMinutes { get; set; } = 10;
    /// <summary>Grava o histórico em historico.db (10 s por 48 h, 1 min por 30 dias, 1 h por 2 anos).</summary>
    public bool HistoryEnabled { get; set; } = true;
    /// <summary>Lê a bateria de periféricos Logitech sem fio (headset, mouse, teclado) pelos dongles USB.</summary>
    public bool PeripheralsEnabled { get; set; } = true;
    /// <summary>Grava as sessões de jogo (FPS e sensores) em jogos.db.</summary>
    public bool GamesEnabled { get; set; } = true;
}

/// <summary>
/// Pasta de dados do coletor (%ProgramData%\HwMon): configuração, certificado, clientes pareados, totais de rede.
/// Quando criada por um administrador, o acesso fica restrito a SYSTEM e Administradores.
/// </summary>
public sealed class DataStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Directory { get; }
    public string ConfigPath => Path.Combine(Directory, "config.json");
    public string CertificatePath => Path.Combine(Directory, "certificado.pfx");
    public string ClientsPath => Path.Combine(Directory, "clientes.json");
    public string PairingPath => Path.Combine(Directory, "pareamento.json");
    public string HistoryPath => Path.Combine(Directory, "historico.db");
    public string GamesPath => Path.Combine(Directory, "jogos.db");

    private DataStore(string directory) => Directory = directory;

    public static string DefaultDirectory => HwMon.Core.AppPaths.MachineData;

    public static DataStore Open(string? directory)
    {
        // Até a 0.11 a pasta era %ProgramData%\HwMon: leva tudo (certificado, clientes, histórico) para a nova.
        if (directory is null && IsAdministrator()) HwMon.Core.AppPaths.MigrateMachineData();
        var dir = directory ?? DefaultDirectory;
        var created = !System.IO.Directory.Exists(dir);
        System.IO.Directory.CreateDirectory(dir);
        if (directory is null && IsAdministrator()) Restrict(dir);
        else if (created && directory is null)
            Console.Error.WriteLine("Aviso: pasta de dados criada sem administrador; o acesso não foi restrito.");
        return new DataStore(dir);
    }

    public CollectorConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            var fresh = new CollectorConfig();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(fresh, Json));
            return fresh;
        }
        return JsonSerializer.Deserialize<CollectorConfig>(File.ReadAllText(ConfigPath), Json) ?? new CollectorConfig();
    }

    public void SaveConfig(CollectorConfig config) => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Json));

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Só SYSTEM e Administradores: a pasta guarda a chave privada do certificado e os tokens.</summary>
    private static void Restrict(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Aviso: não foi possível restringir o acesso a {dir}: {ex.Message}");
        }
    }
}
