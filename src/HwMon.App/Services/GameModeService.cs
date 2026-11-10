using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HwMon.App.ViewModels;
using HwMon.App.Views;
using HwMon.Client;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.App.Services;

/// <summary>
/// Modo jogo: a cada meio segundo olha o programa em primeiro plano. Se for um jogo em tela cheia (ou se o usuário
/// apertou Ctrl+Shift+F10), mostra o overlay com FPS, 1 % low, frametime e GPU/CPU, e esconde o widget enquanto
/// o jogo estiver na frente. O FPS vem do coletor deste PC (/v1/frames), que lê os eventos do Windows como SISTEMA.
/// </summary>
public sealed class GameModeService : IDisposable
{
    private readonly App _app;
    private readonly MainViewModel _vm;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _timer;
    private readonly GameOverlayViewModel _overlay = new();
    private GameOverlayWindow? _window;
    private CollectorClient? _client;
    private MachineViewModel? _clientMachine;
    private bool _forced;      // ligado pelo atalho
    private bool _active;
    private bool _busy;
    private int _activePid;
    private int _suppressedPid; // escondido pelo atalho: não reaparece sozinho para este processo

    /// <summary>Programas em tela cheia que não são jogos (vídeo, navegador): não ativam o modo automático.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "vlc", "mpc-hc64", "mpc-be64", "mpv", "PotPlayerMini64",
        "Netflix", "Video.UI", "Microsoft.Media.Player", "explorer", "ApplicationFrameHost", "devenv", "Code", "POWERPNT",
        "Teams", "ms-teams", "Zoom", "obs64", "HwMon", "Corewise", "Photos", "PhotosApp", "steamwebhelper",
    };

    public GameModeService(App app, MainViewModel vm, SettingsStore settings)
    {
        _app = app;
        _vm = vm;
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public GameOverlayViewModel Overlay => _overlay;
    private GameModeSettings Options => _settings.Current.GameMode;


    // ------------------------------------------------------------------ atalho global

    /// <summary>Atalho do modo jogo (configurável): aparecendo, esconde (e não volta sozinho para este jogo); escondido, mostra para o programa da frente.</summary>
    public void Toggle()
    {
        if (_active)
        {
            var pid = _activePid;
            Deactivate();
            _suppressedPid = pid;
        }
        else
        {
            _forced = true;
            _suppressedPid = 0;
        }
    }

    // ------------------------------------------------------------------ ciclo

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var (pid, fullscreen, name) = Foreground();
            if (_suppressedPid != 0 && pid != _suppressedPid && pid != 0) _suppressedPid = 0; // trocou de programa
            var mode = Options.Mode;
            // Automático: só jogo em tela cheia; nunca o cliente da Área de Trabalho Remota (ou outro acesso remoto)
            // e nunca quando este próprio painel está sendo visto por acesso remoto.
            var wanted = mode != "off" && pid != 0 && pid != Environment.ProcessId
                         && (_forced || (mode == "auto" && fullscreen && pid != _suppressedPid && !NotGames.Contains(name)
                                         && !HwMon.Core.Collection.GameNames.NotGames.Contains(name) && !SystemParameters.IsRemoteSession));

            if (!wanted)
            {
                if (_active) Deactivate();
                return;
            }

            // Modo automático: só conta como jogo se estiver desenhando quadros de verdade.
            var frames = await FramesAsync(pid);
            var stats = frames?.Processes.FirstOrDefault(p => p.Pid == pid);
            if (!_forced && frames is { Running: true, WarmingUp: false } && (stats is null || stats.Fps < 15))
            {
                if (_active) Deactivate();
                return;
            }

            if (!_active || _activePid != pid) Activate(pid, name);
            Update(name, frames, stats);
            _window?.KeepOnTop();
        }
        catch (Exception)
        {
            // nunca derruba o painel por causa do overlay
        }
        finally
        {
            _busy = false;
        }
    }

    private void Activate(int pid, string name)
    {
        _active = true;
        _activePid = pid;
        _overlay.ShowGraph = Options.ShowGraph;
        _overlay.BackgroundOpacity = Options.Opacity;
        _window ??= new GameOverlayWindow(_overlay);
        _window.PlaceAt(Options.Corner, ForegroundMonitor());
        _window.Show();
        if (Options.HideWidget) _app.SetWidgetSuspended(true);
    }

    private void Deactivate()
    {
        _active = false;
        _activePid = 0;
        _forced = false;
        _window?.Hide();
        _app.SetWidgetSuspended(false);
    }

    private MachineViewModel? LocalMachine
        => _vm.Machines.FirstOrDefault(m => string.Equals(m.Descriptor?.HostName, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

    private async Task<FramesResponse?> FramesAsync(int pid)
    {
        var local = LocalMachine;
        if (local is null) return null;
        if (_clientMachine != local)
        {
            _client?.Dispose();
            _client = CollectorClient.For(local.Machine);
            _clientMachine = local;
        }
        try
        {
            return await _client!.FramesAsync(pid);
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return new FramesResponse { Error = L.T("Atualize o Corewise neste PC para ver o FPS.") };
        }
        catch (Exception)
        {
            _client?.Dispose();
            _client = null;
            _clientMachine = null;
            return null;
        }
    }

    private void Update(string name, FramesResponse? frames, FrameStats? stats)
    {
        _overlay.Game = name;
        var local = LocalMachine;
        _overlay.Message = local is null ? L.T("Pareie este PC no painel para ver FPS e sensores.")
            : frames?.Error is { } err ? L.T(err)
            : frames is { WarmingUp: true } ? L.T("medindo...")
            : stats is null ? L.T("sem quadros deste programa (não usa DirectX/DXGI?)")
            : null;
        if (stats is not null)
        {
            _overlay.Fps = stats.Fps.ToString("0", Format.Pt);
            _overlay.Low = stats.Low1Fps.ToString("0", Format.Pt);
            _overlay.Frametime = stats.FrametimeMs.ToString("0.0", Format.Pt) + " ms";
            _overlay.LowSeverity = stats.Low1Fps < stats.Fps * 0.5 ? Severity.Critical : stats.Low1Fps < stats.Fps * 0.7 ? Severity.Caution : Severity.Normal;
            var recent = stats.Frametimes.TakeLast(240).ToList();
            _overlay.Frametimes = recent;
            _overlay.GraphMax = Math.Max(stats.FrametimeMs * 2.5, recent.DefaultIfEmpty(0).Max() * 1.1);
        }
        else
        {
            _overlay.Fps = _overlay.Low = _overlay.Frametime = "—";
            _overlay.Frametimes = Array.Empty<double>();
        }

        if (local?.Descriptor is { } d)
        {
            var gpu = MachineViewModel.MainGpu(d.Components);
            var cpu = d.Components.FirstOrDefault(c => c.Kind == ComponentKind.Cpu);
            string V(string? id, string metric) => id is null ? "—" : local.Text(id, metric);
            _overlay.Gpu = gpu is null ? null : $"GPU {V(gpu.Id, "usage")} · {V(gpu.Id, "temp")} · {V(gpu.Id, "power")} · {V(gpu.Id, "clock")}";
            _overlay.Vram = gpu is null ? null
                : local.Value(gpu.Id, "vram_used")?.Value is { } used && local.Value(gpu.Id, "vram_total")?.Value is { } total
                    ? "VRAM " + L.T("{0} de {1}", Format.Number(used, "GB"), Format.Number(total, "GB")) : null;
            _overlay.Cpu = cpu is null ? null : $"CPU {V(cpu.Id, "usage")} · {V(cpu.Id, "temp")}   RAM {local.Text("ram:0", "used")}";
        }
    }

    // ------------------------------------------------------------------ janela em primeiro plano

    private static (int Pid, bool Fullscreen, string Name) Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (0, false, "");
        GetWindowThreadProcessId(hwnd, out var pid);
        var cls = new System.Text.StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return (0, false, "");
        var name = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch (ArgumentException) { }
        GetWindowRect(hwnd, out var r);
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var fullscreen = GetMonitorInfo(monitor, ref info)
                         && r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
        return ((int)pid, fullscreen, name);
    }

    /// <summary>Retângulo (pixels) do monitor do programa em primeiro plano.</summary>
    private static Rect ForegroundMonitor()
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info)
            ? new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top)
            : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    public void Dispose()
    {
        _timer.Stop();
        _window?.Close();
        _client?.Dispose();
    }

    // ------------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
