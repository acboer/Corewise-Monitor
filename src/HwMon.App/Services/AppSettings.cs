using System.IO;
using System.Text.Json;

namespace HwMon.App.Services;

/// <summary>
/// Preferências do usuário, em %AppData%\HwMon\app.json. O que aparece no painel e no widget é escolhido de forma
/// independente, por máquina (chave = MachineId do coletor).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Componentes ocultos no painel: "machineId|componentId".</summary>
    public HashSet<string> PanelHidden { get; set; } = new();
    /// <summary>Componentes que foram reexibidos de propósito (anula o oculto-por-padrão, ex.: volume sem letra).</summary>
    public HashSet<string> PanelShown { get; set; } = new();
    public WindowPlacement MainWindow { get; set; } = new();
    /// <summary>Ordem dos cartões arrastada pelo usuário: machineId → ids dos componentes (cartões novos vão no fim).</summary>
    public Dictionary<string, List<string>> CardOrder { get; set; } = new();
    /// <summary>Blocos da visão geral escolhidos e ordenados: machineId → chaves (cpu, gpu, ram, net, space, disks...).</summary>
    public Dictionary<string, List<string>> OverviewTiles { get; set; } = new();
    public WidgetSettings Widget { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public GameModeSettings GameMode { get; set; } = new();
    /// <summary>"system" (segue o Windows), "light" ou "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>"auto" (idioma do Windows), "pt" ou "en". Vale ao abrir o painel.</summary>
    public string Language { get; set; } = "auto";

    public static string Key(string machineId, string componentId) => $"{machineId}|{componentId}";
}

public sealed class WindowPlacement
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class WidgetSettings
{
    public bool Visible { get; set; }
    public bool Topmost { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public string? MachineId { get; set; }
    /// <summary>Métricas escolhidas por máquina: "componentId|metric".</summary>
    public Dictionary<string, List<string>> Metrics { get; set; } = new();
    /// <summary>"compact" ou "visual".</summary>
    public string Style { get; set; } = "compact";
    /// <summary>Tempo estimado para carregar embaixo da bateria (aparece só enquanto carrega).</summary>
    public bool ShowChargeTime { get; set; } = true;
    /// <summary>Opacidade do fundo do widget (1 = como no tema; o texto não muda).</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>Um widget para cada PC (em vez de um só, trocando pelas setas).</summary>
    public bool PerMachine { get; set; }
    /// <summary>PCs que aparecem no widget (MachineId); nulo = todos.</summary>
    public List<string>? Machines { get; set; }
    /// <summary>Posição de cada widget no modo "um por PC" (MachineId → posição).</summary>
    public Dictionary<string, WidgetPosition> Positions { get; set; } = new();
    /// <summary>Atalho que mostra ou esconde todos os widgets (vazio = sem atalho).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+F9";
}

public sealed class WidgetPosition
{
    public double Left { get; set; }
    public double Top { get; set; }
}

/// <summary>Modo jogo: overlay com FPS e dados da GPU sobre o jogo.</summary>
public sealed class GameModeSettings
{
    /// <summary>"auto" (aparece com um jogo em tela cheia), "hotkey" (só pelo atalho) ou "off".</summary>
    public string Mode { get; set; } = "auto";
    /// <summary>Esconde o widget enquanto o jogo está em primeiro plano (ele ficava por cima do jogo).</summary>
    public bool HideWidget { get; set; } = true;
    /// <summary>top-left, top-right, bottom-left, bottom-right.</summary>
    public string Corner { get; set; } = "top-left";
    public bool ShowGraph { get; set; } = true;
    public double Opacity { get; set; } = 0.8;
    /// <summary>Atalho que mostra ou esconde o overlay (vazio = sem atalho).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+F10";
}

/// <summary>O que abre quando o Windows inicia o programa (argumento --inicio).</summary>
public enum StartupMode { Widget, PanelAndWidget, Tray }

public sealed class StartupSettings
{
    public StartupMode Mode { get; set; } = StartupMode.Widget;
    /// <summary>Fechar o painel (e o widget) deixa o programa rodando na bandeja.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Já decidiu sobre iniciar com o Windows (na primeira execução o padrão é ligar).</summary>
    public bool Configured { get; set; }
    /// <summary>O aviso "continua na bandeja" já foi mostrado uma vez.</summary>
    public bool TrayHintShown { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private Timer? _debounce;

    public AppSettings Current { get; }

    public SettingsStore()
    {
        var dir = HwMon.Core.AppPaths.UserData;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "app.json");
        try
        {
            Current = File.Exists(_path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new() : new();
        }
        catch (JsonException)
        {
            Current = new AppSettings(); // arquivo corrompido: volta ao padrão
        }
    }

    /// <summary>Grava meio segundo depois da última mudança (arrastar o widget gera muitas).</summary>
    public void SaveSoon()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => SaveNow(), null, 500, Timeout.Infinite);
    }

    public void SaveNow()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception)
        {
            // arquivo em uso ou coleção mudando durante a gravação: a próxima gravação tenta de novo
        }
    }
}
