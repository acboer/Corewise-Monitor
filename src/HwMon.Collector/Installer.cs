using System.Diagnostics;
using HwMon.Core.Telemetry;

namespace HwMon.Collector;

/// <summary>Instala/remove o serviço do Windows e a regra de firewall (redes privadas e de domínio).</summary>
public static class Installer
{
    private const string FirewallRule = "Corewise";
    private const string LegacyFirewallRule = "HwMon Collector";

    /// <summary>Até a 0.11: serviço "HwMonCollector" em C:\Program Files\HwMon\Collector. Remove tudo (os dados já migraram).</summary>
    private static void RemoveLegacy()
    {
        if (Run("sc.exe", $"query {Protocol.LegacyServiceName}", quiet: true) == 0)
        {
            Console.WriteLine("Removendo o serviço antigo (HwMonCollector)...");
            Run("net.exe", $"stop {Protocol.LegacyServiceName}", quiet: true);
            Run("sc.exe", $"delete {Protocol.LegacyServiceName}", quiet: true);
            Thread.Sleep(1500);
        }
        Run("netsh", $"advfirewall firewall delete rule name=\"{LegacyFirewallRule}\"", quiet: true);
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HwMon");
        try { if (Directory.Exists(old)) Directory.Delete(old, recursive: true); }
        catch (Exception) { /* arquivo preso: fica para depois, não atrapalha */ }
    }

    public static int Install(int port)
    {
        if (!DataStore.IsAdministrator())
        {
            Console.Error.WriteLine("Rode como administrador.");
            return 1;
        }
        var exe = Environment.ProcessPath!;
        if (exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("Aviso: o executável está na pasta do usuário. Recomendado: instalar pelo CorewiseSetup.");
        RemoveLegacy();

        if (ServiceExists())
        {
            Console.WriteLine("Serviço já existe; reinstalando...");
            Run("sc.exe", $"stop {Protocol.ServiceName}", quiet: true);
            Thread.Sleep(2000);
            Run("sc.exe", $"delete {Protocol.ServiceName}");
            Thread.Sleep(1000);
        }

        if (Run("sc.exe", $"create {Protocol.ServiceName} binPath= \"\\\"{exe}\\\"\" start= delayed-auto obj= LocalSystem DisplayName= \"Corewise\"") != 0)
            return 1;
        Run("sc.exe", $"description {Protocol.ServiceName} \"Coleta a telemetria de hardware deste PC e a publica para os painéis HwMon pareados na rede local.\"");
        Run("sc.exe", $"failure {Protocol.ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000");

        Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", quiet: true);
        Run("netsh", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain program=\"{exe}\"");
        WarnPublicNetwork();

        if (Run("sc.exe", $"start {Protocol.ServiceName}") != 0) return 1;
        Console.WriteLine();
        Console.WriteLine($"Serviço instalado e iniciado (porta {port}).");
        Console.WriteLine("Para ver este PC em outro computador: menu Iniciar > Corewise > Código de pareamento.");
        return 0;
    }

    public static int Uninstall()
    {
        if (!DataStore.IsAdministrator())
        {
            Console.Error.WriteLine("Rode como administrador.");
            return 1;
        }
        Run("sc.exe", $"stop {Protocol.ServiceName}", quiet: true);
        Thread.Sleep(2000);
        Run("sc.exe", $"delete {Protocol.ServiceName}");
        Run("netsh", $"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        Console.WriteLine($"Serviço removido. Configuração, certificado e clientes continuam em {DataStore.DefaultDirectory}.");
        return 0;
    }

    public static bool ServiceExists() => Run("sc.exe", $"query {Protocol.ServiceName}", quiet: true) == 0;

    public static string ServiceState()
    {
        var output = Capture("sc.exe", $"query {Protocol.ServiceName}");
        if (output is null) return "não instalado";
        if (output.Contains("RUNNING", StringComparison.Ordinal)) return "em execução";
        if (output.Contains("STOPPED", StringComparison.Ordinal)) return "parado";
        if (output.Contains("START_PENDING", StringComparison.Ordinal)) return "iniciando";
        return "estado desconhecido";
    }

    /// <summary>A regra vale só para redes privadas/domínio: em rede "Pública" o NAS fica inacessível.</summary>
    private static void WarnPublicNetwork()
    {
        try
        {
            var output = Capture("powershell.exe", "-NoProfile -Command \"Get-NetConnectionProfile | ForEach-Object { $_.InterfaceAlias + '|' + $_.NetworkCategory }\"");
            foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|');
                if (parts.Length == 2 && parts[1] == "Public")
                    Console.WriteLine($"Aviso: a rede \"{parts[0]}\" está como Pública. O firewall só libera o coletor em redes Privadas; " +
                                      "mude em Configurações → Rede e Internet → (sua conexão) → Tipo de perfil de rede → Privada.");
            }
        }
        catch (Exception)
        {
            // só um aviso
        }
    }

    private static int Run(string file, string args, bool quiet = false)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (!quiet && p.ExitCode != 0) Console.WriteLine($"{file} {args}\n  → código {p.ExitCode}: {output.Trim()}");
        return p.ExitCode;
    }

    private static string? Capture(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : null;
    }
}
