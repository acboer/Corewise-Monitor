using System.Diagnostics;
using HwMon.Core.Collection;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.Collector.History;

/// <summary>
/// Grava sessões de jogo: a cada segundo pergunta ao <see cref="FrameMonitor"/> quem está desenhando quadros.
/// Um programa fora da lista de "não jogos" a pelo menos 15 FPS vira candidato e passa a ter todos os quadros
/// acumulados. Vira sessão (gravada e regravada a cada minuto) com 2 minutos desenhando e usando a GPU de
/// verdade: média da engine 3D do próprio processo nos últimos 2 min ≥ 10 % (≥ 3 % se o .exe estiver numa
/// biblioteca de jogos). Programas que só redesenham a interface (Dropbox, VPN, apps em Electron) ficam perto de
/// 0 % e nunca viram sessão. Dois processos com o mesmo nome de jogo ao mesmo tempo (launcher + jogo): fica o que
/// mais usa a GPU. A sessão acaba
/// quando o processo fecha ou passa 2 minutos sem quadros. Junto vão os sensores (GPU, CPU, RAM) e a energia.
/// </summary>
public sealed class GameRecorder(FrameMonitor frames, EngineHost host, DataStore data, CollectorConfig config, ILogger<GameRecorder> log) : BackgroundService
{
    private const double MinFps = 15;
    private static readonly TimeSpan Qualify = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    private const int PointSeconds = 5;
    private const double MinGpu3d = 10, MinGpu3dLibrary = 3;
    private const int GpuWindow = 120; // segundos da média de GPU para virar sessão

    private sealed class Acc
    {
        public double Sum, Max = double.MinValue;
        public int N;
        public void Add(double v) { Sum += v; N++; if (v > Max) Max = v; }
    }

    private sealed class Track
    {
        public int Pid;
        public string Exe = "", Name = "", ProcessName = "";
        public DateTimeOffset Start, LastSeen, LastSave, LastPoint;
        public long ActiveSeconds;
        public bool Qualified;
        public bool Discarded;
        public bool InLibrary;
        public readonly Queue<double> Gpu3d = new();
        public double Gpu3dAvg => Gpu3d.Count == 0 ? 0 : Gpu3d.Average();
        public string? GpuId, CpuId;
        public string? GpuName, CpuName;
        public double EnergyWh;
        public bool HasPower;
        public readonly Dictionary<string, (string Unit, Acc Acc)> Metrics = new();
        public readonly List<GamePoint> Points = new();
        public long PointFrames;
        public double PointLow1 = double.MaxValue;
        public GameSession Session = new();
    }

    private readonly Dictionary<int, Track> _tracks = new();
    private readonly object _lock = new();

    public GameStore? Store { get; private set; }

    /// <summary>Sessões em andamento (já com 2 min), para a lista "jogando agora".</summary>
    public List<GameSession> Current()
    {
        lock (_lock) return _tracks.Values.Where(t => t.Qualified).Select(t => Build(t, withPoints: false)).ToList();
    }

    /// <summary>A sessão em andamento com esse id (com os pontos), se houver.</summary>
    public GameSession? CurrentSession(long id)
    {
        lock (_lock) return _tracks.Values.FirstOrDefault(t => t.Qualified && t.Session.Id == id) is { } t ? Build(t, withPoints: true) : null;
    }

    public bool Recording => frames.Query(-1) is { Running: true };

    /// <summary>Limpa o histórico de jogos: apaga as sessões gravadas e recomeça as que estão em andamento.</summary>
    public int Clear()
    {
        if (Store is not { } store) return 0;
        lock (_lock)
        {
            foreach (var t in _tracks.Values.ToList()) End(t, save: false);
            return store.Clear();
        }
    }

    /// <summary>Depois de renomear ou ignorar: aplica à sessão em andamento (ignorado = para de gravar e descarta).</summary>
    public void Refresh(string exe)
    {
        if (Store?.Game(exe) is not { } info) return;
        lock (_lock)
            foreach (var t in _tracks.Values.Where(t => t.Exe == exe).ToList())
            {
                if (info.Ignored)
                {
                    if (t.Session.Id != 0) Store.Delete(t.Session.Id);
                    t.Qualified = false;
                    End(t);
                    continue;
                }
                t.Name = info.Display;
            }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GamesEnabled) return;
        try
        {
            Store = new GameStore(data.GamesPath);
            var orphans = Store.CloseOrphans();
            if (orphans > 0) log.LogInformation("Jogos: {N} sessões que ficaram abertas foram fechadas", orphans);
            var cleaned = Store.CleanupBeforeGpuFilter(GameNames.InLibrary);
            if (cleaned > 0) log.LogInformation("Jogos: {N} sessões de programas que não são jogos foram apagadas (filtro por GPU)", cleaned);
            var fixedTime = Store.FixActiveTime(GameNames.NotGames.Contains);
            if (fixedTime > 0) log.LogInformation("Jogos: tempo jogado corrigido em {N} sessões", fixedTime);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Histórico de jogos desligado: não foi possível abrir {Path}", data.GamesPath);
            return;
        }

        var failures = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                try
                {
                    if (host.Engine is { } engine) Tick(engine);
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (failures++ % 60 == 0) log.LogWarning(ex, "Jogos: falha ao gravar");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // parando
        }
        finally
        {
            lock (_lock)
                foreach (var t in _tracks.Values.ToList()) End(t);
            Store?.Dispose();
            Store = null;
        }
    }

    private void Tick(TelemetryEngine engine)
    {
        var response = frames.Query(null);
        if (!response.Running) return;
        var now = DateTimeOffset.UtcNow;
        var snapshot = engine.Snapshot();
        var values = snapshot.Values.Where(v => v.State == ReadingState.Ok && v.Value is not null)
            .ToDictionary(v => (v.Component, v.Metric), v => v.Value!.Value);

        var gpu = engine.Gpu3dByProcess;
        lock (_lock)
        {
            foreach (var p in response.Processes)
            {
                if (p.Fps < MinFps || GameNames.NotGames.Contains(p.Name)) continue;
                if (!_tracks.TryGetValue(p.Pid, out var t))
                {
                    var path = GameNames.ProcessPath(p.Pid);
                    var (exe, detected) = GameNames.Resolve(path, p.Name);
                    var info = Store!.Touch(exe, detected, path);
                    if (info.Ignored) continue;
                    t = new Track { Pid = p.Pid, Exe = exe, Name = info.Display, ProcessName = p.Name, Start = now, LastPoint = now, InLibrary = GameNames.InLibrary(path) };
                    PickComponents(t, engine, values);
                    _tracks[p.Pid] = t;
                    frames.BeginCapture(p.Pid);
                }
                t.LastSeen = now;
                t.ActiveSeconds++;
                t.PointLow1 = Math.Min(t.PointLow1, p.Low1Fps);
                t.Gpu3d.Enqueue(gpu.GetValueOrDefault(p.Pid));
                while (t.Gpu3d.Count > GpuWindow) t.Gpu3d.Dequeue();
                Sample(t, values);
            }

            foreach (var t in _tracks.Values.ToList())
            {
                if (t.LastSeen != now)
                {
                    if (now - t.LastSeen > Grace || !Alive(t.Pid)) End(t);
                    continue;
                }
                if (now - t.LastPoint >= TimeSpan.FromSeconds(PointSeconds)) AddPoint(t, now, values);
                if (!t.Qualified && !IsGame(t))
                {
                    // Candidato que não usa a GPU: guarda só os últimos 10 min de pontos (pode ficar horas assim).
                    if (t.Points.Count > 120) t.Points.RemoveRange(0, t.Points.Count - 120);
                }
                else if (!t.Qualified && !t.Discarded && now - t.Start >= Qualify && t.ActiveSeconds >= Qualify.TotalSeconds * 0.8)
                {
                    t.Qualified = true;
                    Save(t, open: true);
                    log.LogInformation("Jogos: sessão de {Name} ({Exe}) começou", t.Name, t.Exe);
                }
                else if (t.Qualified && Outranked(t))
                {
                    // Launcher do mesmo jogo (ex.: start_protected_game.exe do anticheat) que virou sessão antes do
                    // jogo de verdade esquentar a GPU: descarta e não grava mais.
                    if (t.Session.Id != 0) Store!.Delete(t.Session.Id);
                    t.Qualified = false;
                    t.Discarded = true;
                    log.LogInformation("Jogos: {Exe} descartado (o processo principal de {Name} é outro)", t.Exe, t.Name);
                }
                else if (t.Qualified && now - t.LastSave >= TimeSpan.FromMinutes(1)) Save(t, open: true);
            }
        }
    }

    /// <summary>Usa a GPU de verdade e é o processo principal do jogo (não o launcher do mesmo jogo).</summary>
    private bool IsGame(Track t)
    {
        if (t.Gpu3d.Count < 30) return false;
        if (t.Gpu3dAvg < (t.InLibrary ? MinGpu3dLibrary : MinGpu3d)) return false;
        return !_tracks.Values.Any(o => o != t && o.Name == t.Name && o.Gpu3dAvg > t.Gpu3dAvg);
    }

    /// <summary>Já é sessão, mas outro processo com o mesmo nome de jogo usa bem mais a GPU (é o jogo de verdade).</summary>
    private bool Outranked(Track t)
        => t.Gpu3d.Count >= GpuWindow / 2
           && _tracks.Values.Any(o => o != t && o.Name == t.Name && o.Gpu3d.Count >= 30 && o.Gpu3dAvg > Math.Max(3 * t.Gpu3dAvg, MinGpu3dLibrary));

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>CPU e a placa de vídeo do jogo (a que mais consome agora: a dedicada, não a integrada).</summary>
    private static void PickComponents(Track t, TelemetryEngine engine, Dictionary<(string, string), double> values)
    {
        var comps = engine.Descriptor().Components;
        var cpu = comps.FirstOrDefault(c => c.Kind == ComponentKind.Cpu);
        var gpu = comps.Where(c => c.Kind == ComponentKind.Gpu)
            .OrderByDescending(c => values.GetValueOrDefault((c.Id, "power")))
            .ThenByDescending(c => values.GetValueOrDefault((c.Id, "usage")))
            .FirstOrDefault();
        (t.CpuId, t.CpuName) = (cpu?.Id, cpu?.Name);
        (t.GpuId, t.GpuName) = (gpu?.Id, gpu?.Name);
    }

    private static readonly (string Key, string Who, string Metric, string Unit)[] Sensors =
    [
        ("gpu_usage", "gpu", "usage", "%"), ("gpu_temp", "gpu", "temp", "°C"), ("gpu_power", "gpu", "power", "W"),
        ("gpu_clock", "gpu", "clock", "MHz"), ("vram", "gpu", "vram_used", "GB"),
        ("cpu_usage", "cpu", "usage", "%"), ("cpu_temp", "cpu", "temp", "°C"), ("cpu_power", "cpu", "power", "W"),
        ("ram", "ram", "used", "GB"),
    ];

    private static double? Get(Track t, Dictionary<(string, string), double> values, string who, string metric)
    {
        var id = who switch { "gpu" => t.GpuId, "cpu" => t.CpuId, _ => "ram:0" };
        return id is not null && values.TryGetValue((id, metric), out var v) ? v : null;
    }

    private static void Sample(Track t, Dictionary<(string, string), double> values)
    {
        foreach (var (key, who, metric, unit) in Sensors)
        {
            if (Get(t, values, who, metric) is not { } v) continue;
            if (!t.Metrics.TryGetValue(key, out var m)) t.Metrics[key] = m = (unit, new Acc());
            m.Acc.Add(v);
        }
        var cpuW = Get(t, values, "cpu", "power");
        var gpuW = Get(t, values, "gpu", "power");
        if (cpuW is not null || gpuW is not null)
        {
            t.HasPower = true;
            t.EnergyWh += ((cpuW ?? 0) + (gpuW ?? 0)) / 3600.0; // 1 s por amostra
        }
    }

    private void AddPoint(Track t, DateTimeOffset now, Dictionary<(string, string), double> values)
    {
        var capture = frames.Capture(t.Pid);
        var seconds = (now - t.LastPoint).TotalSeconds;
        var framesNow = capture?.Frames ?? 0;
        var delta = framesNow - t.PointFrames;
        t.Points.Add(new GamePoint
        {
            T = t.LastPoint.ToUnixTimeSeconds(),
            Fps = Math.Round(delta / Math.Max(1, seconds), 1),
            Low1 = t.PointLow1 == double.MaxValue ? 0 : Math.Round(t.PointLow1, 1),
            GpuUsage = Round(Get(t, values, "gpu", "usage")),
            GpuTemp = Round(Get(t, values, "gpu", "temp")),
            GpuPower = Round(Get(t, values, "gpu", "power")),
            CpuUsage = Round(Get(t, values, "cpu", "usage")),
            CpuTemp = Round(Get(t, values, "cpu", "temp")),
        });
        t.PointFrames = framesNow;
        t.PointLow1 = double.MaxValue;
        t.LastPoint = now;
    }

    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);

    private GameSession Build(Track t, bool withPoints)
    {
        var s = t.Session;
        s.Exe = t.Exe;
        s.Name = t.Name;
        s.Start = t.Start.ToUnixTimeSeconds();
        s.End = t.LastSeen.ToUnixTimeSeconds();
        s.ActiveSeconds = t.ActiveSeconds;
        s.Gpu = t.GpuName;
        s.Cpu = t.CpuName;
        s.EnergyWh = t.HasPower ? Math.Round(t.EnergyWh, 1) : null;
        if (frames.Capture(t.Pid) is { Frames: > 0 } c)
        {
            // Tempo jogado = tempo coberto por quadros seguidos (intervalos < 1 s). Jogo esquecido aberto em segundo
            // plano, soltando um quadro de vez em quando, não conta.
            s.ActiveSeconds = Math.Min(t.ActiveSeconds, (long)Math.Round(c.SumMs / 1000));
            s.Frames = c.Frames;
            s.FrametimeAvgMs = Math.Round(c.SumMs / c.Frames, 2);
            s.FpsAvg = Math.Round(1000 / s.FrametimeAvgMs, 1);
            s.Low1Fps = Math.Round(1000 / c.Percentile(0.99), 1);
            s.Low01Fps = Math.Round(1000 / c.Percentile(0.999), 1);
            s.FrametimeMaxMs = Math.Round(c.MaxMs, 1);
            s.Stutters = c.CountAbove(Math.Max(2 * s.FrametimeAvgMs, 1000.0 / 30 + 0.1) /* nunca conta 60→30 FPS em jogo travado a 30 */);
        }
        s.Metrics = t.Metrics.Select(kv => new SessionMetric
        {
            Key = kv.Key, Unit = kv.Value.Unit,
            Avg = Math.Round(kv.Value.Acc.Sum / Math.Max(1, kv.Value.Acc.N), 1), Max = Math.Round(kv.Value.Acc.Max, 1),
        }).ToList();
        var copy = new GameSession
        {
            Id = s.Id, Exe = s.Exe, Name = s.Name, Start = s.Start, End = s.End, ActiveSeconds = s.ActiveSeconds, Open = s.Open,
            Frames = s.Frames, FpsAvg = s.FpsAvg, Low1Fps = s.Low1Fps, Low01Fps = s.Low01Fps, FrametimeAvgMs = s.FrametimeAvgMs,
            FrametimeMaxMs = s.FrametimeMaxMs, Stutters = s.Stutters, EnergyWh = s.EnergyWh, Gpu = s.Gpu, Cpu = s.Cpu,
            Metrics = s.Metrics, Points = withPoints ? t.Points.ToList() : null,
        };
        return copy;
    }

    private void Save(Track t, bool open)
    {
        t.Session.Open = open;
        var s = Build(t, withPoints: true);
        Store!.Save(s);
        t.Session.Id = s.Id;
        t.LastSave = DateTimeOffset.UtcNow;
    }

    private void End(Track t, bool save = true)
    {
        _tracks.Remove(t.Pid);
        try
        {
            if (save && t.Qualified && Store is not null)
            {
                Save(t, open: false);
                log.LogInformation("Jogos: sessão de {Name} terminou ({Minutes:0} min, {Fps:0} FPS médio)", t.Name, t.ActiveSeconds / 60.0, t.Session.FpsAvg);
            }
        }
        finally
        {
            frames.EndCapture(t.Pid);
        }
    }
}
