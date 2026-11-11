using System.Collections.Concurrent;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;
using HwMon.Core.Collection;
using HwMon.Core.Model;

namespace HwMon.Core.Telemetry;

public sealed class EngineOptions
{
    public string? Role { get; set; }
    public TimeSpan FastInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan LhmInterval { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan VolumeInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan SmartInterval { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>HDD só tem o SMART lido se teve leitura/escrita neste intervalo (ou seja, já está girando).</summary>
    public TimeSpan SmartRequiresActivityWithin { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Discos (id estável) cujo SMART nunca deve ser lido.</summary>
    public HashSet<string> SmartDisabledDisks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool LhmEnabled { get; set; } = true;
    /// <summary>Bateria de periféricos Logitech sem fio (HID++).</summary>
    public bool PeripheralsEnabled { get; set; } = true;
    /// <summary>Ritmo do laço de periféricos: headsets a cada volta; mouses e teclados a cada minuto.</summary>
    public TimeSpan PeripheralInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan PeripheralScanInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Pasta para estado persistente (totais diários de rede). Nulo = não persiste.</summary>
    public string? DataDirectory { get; set; }
}

/// <summary>
/// Motor do serviço: descobre os componentes, agenda cada fonte no seu intervalo (decidido no piloto) e mantém
/// a leitura mais recente de cada métrica. Produz o descritor da máquina (ids estáveis) e o snapshot atual.
/// </summary>
public sealed class TelemetryEngine : IDisposable
{
    private readonly EngineOptions _options;
    private readonly Action<string> _log;
    // Uma lista de problemas por rotina: cada coletor só é usado por uma thread, e quem esvazia a lista é a mesma thread.
    private readonly List<string> _initProblems = new(), _perfProblems = new(), _storageProblems = new(), _lhmProblems = new();
    private List<string> _netProblems = new();
    private readonly ConcurrentDictionary<string, Reading> _latest = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastIo = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSmart = new();
    private readonly object _descriptorLock = new();
    private readonly Dictionary<(string Component, string Instance), ComponentDescriptor> _components = new();
    private readonly NetDailyTotals _daily;

    private SystemInfo _system = new();
    private List<CpuInfo> _cpus = new();
    private MemoryInfo _memory = new();
    private List<GpuInfo> _gpus = new();
    private StorageCollector _storage = null!;
    private NetworkCollector _network = null!;
    private PerfCounterCollector _perf = null!;
    private LhmCollector? _lhm;
    private Collection.Peripherals.PeripheralCollector? _peripherals;
    private readonly List<string> _periphProblems = new();
    private DateTimeOffset _lastPeripheralScan;
    private int _descriptorVersion;
    private long _sequence;

    public TelemetryEngine(EngineOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
        _daily = new NetDailyTotals(options.DataDirectory is null ? null : Path.Combine(options.DataDirectory, "rede-diaria.json"));
    }

    /// <summary>Uso da engine 3D por processo (%), da última amostra dos contadores.</summary>
    public IReadOnlyDictionary<int, double> Gpu3dByProcess => _perf?.Gpu3dByProcess ?? new Dictionary<int, double>();

    public string MachineId => _system.MachineId;
    public string HostName => _system.HostName;
    public static string AgentVersion => (Assembly.GetEntryAssembly() ?? typeof(TelemetryEngine).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0";

    // ------------------------------------------------------------------ descoberta

    public void Initialize()
    {
        _system = SystemCollector.CollectSystem(_initProblems);
        _cpus = SystemCollector.CollectCpus(_initProblems);
        _memory = SystemCollector.CollectMemory(_initProblems);
        _gpus = SystemCollector.CollectGpus(_initProblems);

        _storage = new StorageCollector(_storageProblems);
        _storage.Discover(checkPower: false);
        _network = new NetworkCollector(_netProblems);
        _network.Discover();
        _perf = new PerfCounterCollector(_gpus, _perfProblems);
        _perf.Prime();
        _ = _network.Sample().ToList();

        if (_options.LhmEnabled)
        {
            _lhm = new LhmCollector(_lhmProblems, includeStorage: true, storageOnDemand: true);
            _lhm.Open();
            _lhm.MapIdentities(_gpus, _storage.PhysicalDisks);
        }

        if (_options.PeripheralsEnabled)
        {
            _peripherals = new Collection.Peripherals.PeripheralCollector(_periphProblems,
                _options.DataDirectory is null ? null : Path.Combine(_options.DataDirectory, "perifericos.json"));
            try
            {
                _peripherals.Scan();
                _lastPeripheralScan = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                _periphProblems.Add($"Periféricos: {ex.GetType().Name}: {ex.Message}");
            }
        }

        BuildDescriptor();

        // Antes da primeira leitura de SMART, deixa claro por que a temperatura do HDD ainda não aparece.
        foreach (var d in _storage.PhysicalDisks)
        {
            var key = StorageCollector.DiskKey(d);
            var note = _options.SmartDisabledDisks.Contains(d.StableId)
                ? "SMART desativado nas configurações"
                : IsRotational(d) ? "aguardando atividade do disco (não acordamos HDDs em repouso)" : "aguardando primeira leitura";
            foreach (var (m, u) in new[] { ("temp", "°C"), ("power_on_hours", "h") })
                Store(Reading.Unavailable("disk", key, m, u, "wmi", note));
        }
        Store(_storage.SampleVolumes());
        foreach (var d in _storage.PhysicalDisks) Store(_storage.SampleDisk(d, smart: false));

        FlushProblems(_initProblems, _perfProblems, _storageProblems, _lhmProblems, _netProblems, _periphProblems);
        _log($"Pronto: {_components.Count} componentes, {_storage.PhysicalDisks.Count} discos, {_gpus.Count} GPUs, " +
             $"{_peripherals?.Devices.Count ?? 0} periféricos, LHM {(_lhm?.IsOpen == true ? "ativo" : "desligado")}.");
    }

    private void BuildDescriptor()
    {
        lock (_descriptorLock)
        {
            var order = 0;
            foreach (var c in _cpus)
                Register("cpu", c.Index.ToString(CultureInfo.InvariantCulture), $"cpu:{c.Index}", ComponentKind.Cpu, c.Name,
                    c.Cores is null ? null : $"{c.Cores} núcleos · {c.LogicalProcessors} threads", order++);

            var modules = _memory.Modules;
            Register("ram", "0", "ram:0", ComponentKind.Ram, "Memória",
                $"{_memory.TotalGb:0} GB" + (modules.Count > 0 ? $" · {modules.Count} módulos {modules[0].Type} {modules[0].SpeedMts} MT/s" : ""), order++);

            var seen = new Dictionary<string, int>();
            foreach (var g in _gpus)
            {
                var baseId = $"gpu:{g.VendorId}-{g.DeviceId}-{g.SubSysId}".ToLowerInvariant();
                var n = seen[baseId] = seen.GetValueOrDefault(baseId) + 1;
                Register("gpu", g.Index.ToString(CultureInfo.InvariantCulture), n == 1 ? baseId : $"{baseId}-{n}", ComponentKind.Gpu, g.Name,
                    g.Integrated ? "integrada" : $"{g.DedicatedMemoryGb:0} GB", order++);
            }

            foreach (var d in _storage.PhysicalDisks)
                Register("disk", StorageCollector.DiskKey(d), "disk:" + d.StableId, ComponentKind.Disk, d.FriendlyName ?? d.StableId,
                    $"{d.MediaType} · {d.BusType} · {d.SizeGb:0} GB", order++);

            foreach (var v in _storage.Volumes)
            {
                var guid = v.Path.Replace(@"\\?\Volume", "").Trim('{', '}', '\\');
                var name = $"{(v.DriveLetter is null ? "" : v.DriveLetter + ": ")}{v.Label}".Trim();
                var c = Register("vol", v.Key, "volume:" + guid, ComponentKind.Volume, name.Length > 0 ? name : "Sem letra",
                    $"{v.FileSystem} · {v.SizeGb:0} GB", order++);
                c.Parents = _storage.PhysicalDisks.Where(d => d.Number is { } n && v.DiskNumbers.Contains(n)).Select(d => "disk:" + d.StableId).ToList();
            }

            foreach (var a in _network.Adapters)
                Register("net", a.Name, "network:" + a.Id.Trim('{', '}').ToLowerInvariant(), ComponentKind.Network, a.Name,
                    $"{a.Description}{(a.IPv4.Count > 0 ? " · " + string.Join(", ", a.IPv4) : "")}{(a.Physical == false ? " · virtual" : "")}", order++);

            RegisterPeripherals();
            _descriptorVersion++;
        }
    }

    private void RegisterPeripherals()
    {
        foreach (var k in _components.Keys.Where(k => k.Component == "periph").ToList()) _components.Remove(k);
        var order = 800;
        foreach (var p in _peripherals?.Devices ?? [])
        {
            var type = p.Type switch
            {
                "headset" => "Headset", "mouse" => "Mouse", "keyboard" => "Teclado", "trackpad" => "Trackpad",
                "trackball" => "Trackball", "presenter" => "Apresentador", "gamepad" => "Controle", "numpad" => "Teclado numérico",
                _ => "Periférico",
            };
            Register("periph", p.Key, "peripheral:" + p.Key, ComponentKind.Peripheral, p.Name, $"{type} · {p.Connection}", order++);
        }
    }

    private ComponentDescriptor Register(string component, string instance, string id, string kind, string name, string? detail, int order)
    {
        var c = new ComponentDescriptor { Id = id, Kind = kind, Name = name, Detail = detail, Order = order };
        _components[(component, instance)] = c;
        return c;
    }

    /// <summary>Componente que só aparece nas leituras (ventoinhas, módulos de RAM com sensor): registra na hora.</summary>
    private ComponentDescriptor? Resolve(string component, string instance)
    {
        lock (_descriptorLock)
        {
            if (_components.TryGetValue((component, instance), out var c)) return c;
            // Adaptador que saiu da lista (troca de rede) ou disco órfão do LHM: não vira componente fantasma.
            if (component is "net" or "disk" or "vol" or "periph") return null;
            var kind = component switch
            {
                "cpu" => ComponentKind.Cpu, "ram" => ComponentKind.Ram, "gpu" => ComponentKind.Gpu, "disk" => ComponentKind.Disk,
                "vol" => ComponentKind.Volume, "net" => ComponentKind.Network, "fan" => ComponentKind.Fan, _ => component,
            };
            var name = component switch
            {
                "fan" => "Ventoinha " + instance.Replace("fan-", "", StringComparison.Ordinal),
                "ram" => "Módulo " + instance,
                _ => instance,
            };
            c = Register(component, instance, $"{kind}:{instance}", kind, name, null, 1000 + _components.Count);
            _descriptorVersion++;
            return c;
        }
    }

    /// <summary>
    /// "Micro-Star International Co., Ltd. MAG X870 TOMAHAWK WIFI (MS-7E51)" → "MSI MAG X870 TOMAHAWK WIFI";
    /// "ASUSTeK COMPUTER INC. ROG STRIX B650E-F" → "ASUS ROG STRIX B650E-F".
    /// </summary>
    public static string? ShortBoard(string? board)
    {
        if (string.IsNullOrWhiteSpace(board)) return null;
        var s = board;
        (string Pattern, string Short)[] vendors =
        [
            (@"^Micro-Star International Co\.?,? Ltd\.?", "MSI"), (@"^ASUSTeK COMPUTER INC\.?", "ASUS"),
            (@"^Gigabyte Technology Co\.?,? Ltd\.?", "Gigabyte"), (@"^ASRock(Rack)?", "ASRock"), (@"^Dell Inc\.?", "Dell"),
            (@"^Hewlett-Packard|^HP\b", "HP"), (@"^LENOVO", "Lenovo"), (@"^Intel Corporation", "Intel"), (@"^BIOSTAR Group", "Biostar"),
        ];
        foreach (var (pattern, shortName) in vendors)
            s = System.Text.RegularExpressions.Regex.Replace(s, pattern, shortName, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\((MS|SKU)-?[0-9A-Z]+\)\s*$", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    public MachineDescriptor Descriptor()
    {
        lock (_descriptorLock)
        {
            return new MachineDescriptor
            {
                MachineId = _system.MachineId,
                HostName = _system.HostName,
                Role = _options.Role,
                Os = $"{_system.OsCaption} {_system.OsBuild}".Trim(),
                Model = $"{_system.Manufacturer} {_system.Model}".Trim(),
                Board = ShortBoard(_system.Motherboard),
                AgentVersion = AgentVersion,
                Version = _descriptorVersion,
                Components = _components.Values.OrderBy(c => c.Order).ToList(),
            };
        }
    }

    // ------------------------------------------------------------------ laços de coleta

    public Task RunAsync(CancellationToken cancel)
    {
        Task Loop(string name, TimeSpan interval, Action tick, Func<List<string>[]> problems) => Task.Factory.StartNew(() =>
        {
            while (!cancel.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                try { tick(); }
                catch (Exception ex) { _log($"Erro em {name}: {ex.GetType().Name}: {ex.Message}"); }
                FlushProblems(problems());
                var took = DateTime.UtcNow - started;
                if (took > interval * 2 && took > TimeSpan.FromSeconds(2))
                    _log($"Rotina \"{name}\" demorou {took.TotalSeconds:0.0} s (intervalo {interval.TotalSeconds:0} s).");
                var wait = interval - took;
                if (wait > TimeSpan.Zero) cancel.WaitHandle.WaitOne(wait);
            }
        }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var tasks = new List<Task>
        {
            Loop("coleta rápida", _options.FastInterval, FastTick, () => [_perfProblems, _netProblems]),
            Loop("coleta lenta", TimeSpan.FromSeconds(15), SlowTick, () => [_storageProblems]),
        };
        if (_lhm?.IsOpen == true) tasks.Add(Loop("LHM", _options.LhmInterval, LhmTick, () => [_lhmProblems]));
        if (_peripherals is not null) tasks.Add(Loop("periféricos", _options.PeripheralInterval, PeripheralTick, () => [_periphProblems]));
        return Task.WhenAll(tasks);
    }

    /// <summary>
    /// Bateria a cada minuto; nova busca de aparelhos a cada 5 min (ligou o headset, pareou um mouse).
    /// Sem resposta não apaga a última leitura: ela fica como antiga, com o horário.
    /// </summary>
    private void PeripheralTick()
    {
        var p = _peripherals!;
        // Busca completa a cada 5 min, ou logo que algo for conectado/desconectado (controle, fone Bluetooth, dongle).
        if (DateTimeOffset.Now - _lastPeripheralScan >= _options.PeripheralScanInterval || p.DevicesChanged())
        {
            _lastPeripheralScan = DateTimeOffset.Now;
            if (p.Scan())
            {
                lock (_descriptorLock)
                {
                    RegisterPeripherals();
                    _descriptorVersion++;
                }
                var keys = p.Devices.Select(d => d.Key).ToHashSet();
                foreach (var key in _latest.Keys.Where(k => k.StartsWith("periph/", StringComparison.Ordinal) && !keys.Any(k.Contains)))
                    _latest.TryRemove(key, out _);
            }
        }
        foreach (var r in p.Sample())
        {
            // Sem resposta (em repouso) mantém a última leitura; "desligado" (o headset avisou) vale na hora.
            if (r.Metric == "battery" && r.State != ReadingState.Ok && r.Note != "desligado"
                && _latest.TryGetValue(r.Key, out var previous) && previous.State == ReadingState.Ok) continue;
            Store(r);
        }
    }

    private void FastTick()
    {
        var now = DateTimeOffset.Now;
        var perf = _perf.Sample().ToList();
        foreach (var r in perf.Where(r => r.Component == "disk" && r.Value is { } v &&
                                          ((r.Metric is "read" or "write" && v > 0.01) || (r.Metric == "active" && v > 1))))
            if (int.TryParse(r.Instance, out var n)) _lastIo[n] = now;
        Store(perf);
        Store(SystemCollector.SampleMemory());

        var net = _network.Sample().ToList();
        Store(net);
        foreach (var adapter in _network.Adapters)
        {
            var rx = net.FirstOrDefault(r => r.Instance == adapter.Name && r.Metric == "rx_total")?.Value;
            var tx = net.FirstOrDefault(r => r.Instance == adapter.Name && r.Metric == "tx_total")?.Value;
            if (rx is null || tx is null) continue;
            var (rxToday, txToday) = _daily.Update(adapter.Id, rx.Value * 1e9, tx.Value * 1e9);
            Store(Reading.Ok("net", adapter.Name, "rx_today", rxToday / 1e9, "GB", "net", "hoje"));
            Store(Reading.Ok("net", adapter.Name, "tx_today", txToday / 1e9, "GB", "net", "hoje"));
        }
        Interlocked.Increment(ref _sequence);
    }

    private DateTimeOffset _lastVolumes, _lastHealth, _lastNetCheck, _lastSave;

    private void SlowTick()
    {
        var now = DateTimeOffset.Now;
        if (now - _lastVolumes >= _options.VolumeInterval)
        {
            Store(_storage.SampleVolumes());
            _lastVolumes = now;
        }

        var healthDue = now - _lastHealth >= _options.HealthInterval;
        foreach (var d in _storage.PhysicalDisks)
        {
            var smartDue = !_options.SmartDisabledDisks.Contains(d.StableId)
                           && (!_lastSmart.TryGetValue(d.StableId, out var last) || now - last >= _options.SmartInterval)
                           && (!IsRotational(d) || (d.Number is { } n && _lastIo.TryGetValue(n, out var io) && now - io <= _options.SmartRequiresActivityWithin));
            if (smartDue)
            {
                Store(_storage.SampleDisk(d, smart: true));
                _lastSmart[d.StableId] = now;
            }
            else if (healthDue)
                Store(_storage.SampleDisk(d, smart: false));
        }
        if (healthDue) _lastHealth = now;

        if (now - _lastNetCheck >= TimeSpan.FromSeconds(60))
        {
            _lastNetCheck = now;
            CheckAdapters();
        }
        if (now - _lastSave >= TimeSpan.FromSeconds(60))
        {
            _lastSave = now;
            try { _daily.Save(); } catch (Exception ex) { _log($"Falha ao gravar totais de rede: {ex.Message}"); }
        }
    }

    /// <summary>Adaptador ligado/desligado/trocado: refaz a lista de rede (os totais diários seguem pelo id).</summary>
    private void CheckAdapters()
    {
        var current = NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_network.Adapters.All(a => current.Contains(a.Id)) && current.Count == _lastAdapterCount) return;
        _lastAdapterCount = current.Count;
        var freshProblems = new List<string>();
        var fresh = new NetworkCollector(freshProblems);
        fresh.Discover();
        if (fresh.Adapters.Select(a => a.Id).Order().SequenceEqual(_network.Adapters.Select(a => a.Id).Order())) return;
        _ = fresh.Sample().ToList();
        _netProblems = freshProblems;
        _network = fresh;
        foreach (var key in _latest.Keys.Where(k => k.StartsWith("net/", StringComparison.Ordinal))) _latest.TryRemove(key, out _);
        lock (_descriptorLock)
        {
            foreach (var k in _components.Keys.Where(k => k.Component == "net").ToList()) _components.Remove(k);
            var order = 500;
            foreach (var a in _network.Adapters)
                Register("net", a.Name, "network:" + a.Id.Trim('{', '}').ToLowerInvariant(), ComponentKind.Network, a.Name,
                    $"{a.Description}{(a.IPv4.Count > 0 ? " · " + string.Join(", ", a.IPv4) : "")}", order++);
            _descriptorVersion++;
        }
        _log("Adaptadores de rede mudaram; lista atualizada.");
    }

    private int _lastAdapterCount = -1;

    private static bool IsRotational(PhysicalDiskInfo d) => d.MediaType is not ("SSD" or "SCM");

    private void Store(IEnumerable<Reading> readings)
    {
        foreach (var r in readings) Store(r);
    }

    private void Store(Reading r) => _latest[r.Key] = r;

    private void FlushProblems(params List<string>[] lists)
    {
        foreach (var list in lists)
        {
            if (list.Count == 0) continue;
            foreach (var p in list.ToArray().Distinct()) _log(p);
            list.Clear();
        }
    }

    private DateTimeOffset _lastSolidState;

    private void LhmTick()
    {
        Store(_lhm!.Sample());
        if (DateTimeOffset.Now - _lastSolidState >= _options.SmartInterval)
        {
            Store(_lhm.SampleSolidState()); // desgaste/temperatura de SSD e NVMe; HDDs nunca
            _lastSolidState = DateTimeOffset.Now;
        }
    }

    // ------------------------------------------------------------------ snapshot

    public TelemetrySnapshot Snapshot()
    {
        var now = DateTimeOffset.Now;
        var values = new List<TelemetryValue>();
        foreach (var group in _latest.Values.GroupBy(r => (r.Component, r.Instance, r.Metric)))
        {
            var component = Resolve(group.Key.Component, group.Key.Instance);
            if (component is null) continue;
            var fresh = group.Where(r => r.State == ReadingState.Ok && now - r.Timestamp <= StaleAfter(r))
                .OrderBy(r => MetricPolicy.SourceRank(r.Component, r.Metric, r.Source)).FirstOrDefault();
            Reading chosen;
            ReadingState state;
            string? note;
            if (fresh is not null)
            {
                (chosen, state, note) = (fresh, ReadingState.Ok, fresh.Note);
            }
            else if (group.Where(r => r.State == ReadingState.Ok).MaxBy(r => r.Timestamp) is { } old)
            {
                (chosen, state, note) = (old, ReadingState.Stale, $"última leitura às {old.Timestamp:HH:mm:ss}");
            }
            else
            {
                chosen = group.MaxBy(r => r.Timestamp)!;
                (state, note) = (chosen.State, chosen.Note);
            }

            values.Add(new TelemetryValue
            {
                Component = component.Id,
                Metric = chosen.Metric,
                Value = chosen.Value is { } v ? Math.Round(v, 3) : null,
                Unit = chosen.Unit,
                State = state,
                Time = chosen.Timestamp,
                Source = chosen.Source,
                Note = note,
            });
        }

        int version;
        lock (_descriptorLock) version = _descriptorVersion;
        return new TelemetrySnapshot
        {
            MachineId = _system.MachineId,
            Time = now,
            Sequence = Interlocked.Read(ref _sequence),
            DescriptorVersion = version,
            Values = values.OrderBy(v => v.Component).ThenBy(v => v.Metric).ToList(),
        };
    }

    public long Sequence => Interlocked.Read(ref _sequence);

    private TimeSpan StaleAfter(Reading r) => r.Source switch
    {
        "perf" or "win32" or "net" => TimeSpan.FromSeconds(5),
        "lhm" => _options.LhmInterval * 5,
        "wmi" when r.Component == "vol" => _options.VolumeInterval * 3,
        "wmi" when r.Metric == "health" => _options.HealthInterval * 3,
        "wmi" => _options.SmartInterval * 1.5,
        "hidpp" => TimeSpan.FromMinutes(3),
        _ => TimeSpan.FromMinutes(1),
    };

    public void Dispose()
    {
        try { _daily.Save(); } catch { /* melhor esforço ao desligar */ }
        _lhm?.Dispose();
        _peripherals?.Dispose();
    }
}
