using HwMon.App.Infrastructure;

namespace HwMon.App.ViewModels;

/// <summary>O que o overlay do modo jogo mostra (atualizado a cada meio segundo pelo GameModeService).</summary>
public sealed class GameOverlayViewModel : ObservableObject
{
    private string _game = "";
    private string _fps = "—", _low = "—", _frametime = "—";
    private string? _gpu, _vram, _cpu, _message;
    private IReadOnlyList<double> _frametimes = Array.Empty<double>();
    private double _graphMax = 33.4;
    private Severity _lowSeverity;
    private bool _showGraph = true;
    private double _backgroundOpacity = 0.8;

    public string Game { get => _game; set => Set(ref _game, value); }
    public string Fps { get => _fps; set => Set(ref _fps, value); }
    public string Low { get => _low; set => Set(ref _low, value); }
    public string Frametime { get => _frametime; set => Set(ref _frametime, value); }
    public string? Gpu { get => _gpu; set => Set(ref _gpu, value); }
    public string? Vram { get => _vram; set => Set(ref _vram, value); }
    public string? Cpu { get => _cpu; set => Set(ref _cpu, value); }
    /// <summary>Aviso no lugar dos números (ex.: coletor desatualizado).</summary>
    public string? Message { get => _message; set => Set(ref _message, value); }
    public IReadOnlyList<double> Frametimes { get => _frametimes; set => Set(ref _frametimes, value); }
    public double GraphMax { get => _graphMax; set => Set(ref _graphMax, value); }
    /// <summary>1 % low muito abaixo da média = engasgos.</summary>
    public Severity LowSeverity { get => _lowSeverity; set => Set(ref _lowSeverity, value); }
    public bool ShowGraph { get => _showGraph; set => Set(ref _showGraph, value); }
    public double BackgroundOpacity { get => _backgroundOpacity; set => Set(ref _backgroundOpacity, value); }
}
