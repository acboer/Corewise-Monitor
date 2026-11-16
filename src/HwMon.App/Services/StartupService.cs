using System.IO;
using Microsoft.Win32;

namespace HwMon.App.Services;

/// <summary>
/// "Iniciar com o Windows": entrada em HKCU\...\Run (por usuário, sem administrador) que abre o programa com
/// --inicio. O registro é a fonte da verdade; as configurações só guardam o que abrir nessa hora.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nodekeeper";
    /// <summary>Nomes anteriores da entrada: "Corewise" (0.12–0.13) e "HardwareWatch" (até a 0.11).</summary>
    private static readonly string[] LegacyValueNames = ["Corewise", "HardwareWatch"];
    public const string StartupArg = "--inicio";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Nodekeeper.exe");

    /// <summary>Cópia compilada dentro de bin\ (desenvolvimento): não liga sozinha na primeira execução.</summary>
    public static bool IsDevBuild => ExePath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    private static string Command => $"\"{ExePath}\" {StartupArg}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Na primeira execução de uma cópia publicada liga o início automático (padrão do produto). Depois, se o programa
    /// mudou de pasta, corrige o caminho gravado.
    /// </summary>
    public static void EnsureDefaults(SettingsStore settings)
    {
        try
        {
            var s = settings.Current.Startup;
            // Entrada com um nome anterior (apontando para Corewise.exe ou HwMon.exe): troca pela nova, mantendo a escolha.
            var hadLegacy = false;
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                foreach (var legacy in LegacyValueNames)
                    if (run?.GetValue(legacy) is string)
                    {
                        run.DeleteValue(legacy, throwOnMissingValue: false);
                        hadLegacy = true;
                    }
            if (hadLegacy)
            {
                if (!IsDevBuild) SetEnabled(true);
                s.Configured = true;
                settings.SaveSoon();
                return;
            }
            if (!s.Configured && !IsDevBuild)
            {
                SetEnabled(true);
                s.Configured = true;
                settings.SaveSoon();
                return;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (!IsDevBuild && key?.GetValue(ValueName) is string current && !current.Equals(Command, StringComparison.OrdinalIgnoreCase))
                SetEnabled(true);
        }
        catch (Exception)
        {
            // registro indisponível (política): o usuário ainda pode ligar pela página Configurações
        }
    }
}
