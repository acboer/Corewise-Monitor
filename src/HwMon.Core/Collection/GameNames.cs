using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HwMon.Core.Collection;

/// <summary>
/// Caminho e nome amigável de um jogo a partir do processo: a pasta do jogo nas lojas (Steam, Epic, Xbox, GOG,
/// EA, Ubisoft), senão o nome do produto gravado no executável, senão o próprio nome do .exe.
/// </summary>
public static class GameNames
{
    /// <summary>Programas que desenham quadros sem parar mas não são jogos (nunca viram sessão).</summary>
    public static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "explorer", "chrome", "msedge", "msedgewebview2", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc",
        "steamwebhelper", "EpicWebHelper", "EpicGamesLauncher", "EADesktop", "Battle.net", "GalaxyClient", "upc", "RiotClientUx",
        "Discord", "Spotify", "Teams", "ms-teams", "Zoom", "slack", "WhatsApp", "Telegram", "Claude", "Notion",
        "obs64", "vlc", "mpc-hc64", "mpc-be64", "mpv", "PotPlayerMini64", "Video.UI", "Microsoft.Media.Player", "Netflix",
        "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "LockApp",
        "Widgets", "SystemSettings", "WindowsTerminal", "Photos", "PhotosApp", "GameBar", "XboxGameBarWidgets",
        "NVIDIA Overlay", "NVIDIA app", "nvcontainer", "HwMon", "HwMon.Collector", "Corewise", "Corewise.Service", "Code", "devenv", "POWERPNT",
        "CefSharp.BrowserSubprocess", "Lively", "wallpaper32", "wallpaper64",
        // Acesso remoto e máquinas virtuais: desenham a tela de outro PC.
        "msrdc", "mstsc", "msrdcw", "Windows365", "vmconnect", "VirtualBoxVM", "vmware-vmx", "vmware", "AnyDesk", "TeamViewer",
        "rustdesk", "parsecd", "vncviewer", "tvnviewer", "RemoteDesktopManager", "Moonlight", "SunshineSvc", "RemotePCDesktop",
        // Launchers de anticheat que abrem antes do jogo.
        "start_protected_game", "EasyAntiCheat_launcher", "EasyAntiCheat_EOS_Setup", "BEService", "BELauncher",
    };

    /// <summary>Nomes de produto genéricos (motor do jogo, não o jogo).</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unity", "UnityPlayer", "Unreal Engine", "UnrealEngine", "UE4", "UE5", "BootstrapPackagedGame", "Godot Engine",
        "Microsoft® Windows® Operating System", "Microsoft Windows Operating System", "Launcher", "Game",
    };

    /// <summary>Pastas de bibliotecas: o nome do jogo é a pasta logo depois.</summary>
    private static readonly string[][] Libraries =
    [
        ["steamapps", "common"], ["Epic Games"], ["XboxGames"], ["GOG Galaxy", "Games"], ["GOG Games"], ["EA Games"],
        ["Ubisoft Game Launcher", "games"], ["Riot Games"], ["Rockstar Games"],
    ];

    public static string? ProcessPath(int pid)
    {
        var handle = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>O executável está dentro da pasta de uma loja de jogos (Steam, Epic, Xbox…).</summary>
    public static bool InLibrary(string? path)
    {
        if (path is null) return false;
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var lib in Libraries)
            for (var i = 0; i + lib.Length < parts.Length - 1; i++)
                if (lib.Select((p, k) => string.Equals(parts[i + k], p, StringComparison.OrdinalIgnoreCase)).All(x => x))
                    return true;
        return false;
    }

    /// <summary>(chave = nome do .exe em minúsculas, nome amigável).</summary>
    public static (string Exe, string Name) Resolve(string? path, string processName)
    {
        var exe = (path is null ? processName + ".exe" : Path.GetFileName(path)).ToLowerInvariant();
        if (path is null) return (exe, processName);

        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (var lib in Libraries)
            for (var i = 0; i + lib.Length < parts.Length - 1; i++)
                if (lib.Select((p, k) => string.Equals(parts[i + k], p, StringComparison.OrdinalIgnoreCase)).All(x => x))
                    return (exe, Clean(parts[i + lib.Length]));

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var candidate in new[] { info.ProductName, info.FileDescription })
                if (!string.IsNullOrWhiteSpace(candidate) && !Generic.Contains(candidate.Trim()) && candidate.Trim().Length > 1)
                    return (exe, Clean(candidate));
        }
        catch (Exception)
        {
            // sem acesso ao arquivo
        }
        return (exe, processName);
    }

    private static string Clean(string s) => s.Replace("™", "").Replace("®", "").Replace("©", "").Replace('_', ' ').Trim();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
