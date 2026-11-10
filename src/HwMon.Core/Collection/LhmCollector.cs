using System.Globalization;
using LibreHardwareMonitor.Hardware;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>
/// Sensores do LibreHardwareMonitor (clocks, temperaturas, consumo, ventoinhas, SMART), traduzidos para as
/// mesmas chaves das fontes nativas para que a matriz compare fonte a fonte.
/// </summary>
public sealed partial class LhmCollector : IDisposable
{
    private const string Source = "lhm";
    private readonly List<string> _problems;
    private Computer? _computer;
    private readonly Dictionary<IHardware, string> _gpuInstance = new();
    private readonly Dictionary<IHardware, string> _diskInstance = new();
    private readonly HashSet<string> _mapped = new();

    private readonly bool _includeStorage;
    private readonly bool _storageOnDemand;

    /// <param name="includeStorage">Abrir o módulo de discos do LHM.</param>
    /// <param name="storageOnDemand">
    /// O módulo de discos custou ~190 ms por leitura no piloto e consulta o SMART a cada atualização, o que impediria
    /// os HDDs de dormir. Com esta opção, Sample() pula os discos e só <see cref="SampleSolidState"/> atualiza
    /// SSD/NVMe (de onde vem o desgaste confiável do NVMe). HDDs nunca são consultados depois da abertura.
    /// </param>
    public LhmCollector(List<string> problems, bool includeStorage = true, bool storageOnDemand = false)
    {
        _problems = problems;
        _includeStorage = includeStorage;
        _storageOnDemand = storageOnDemand;
        _visitor = new UpdateVisitor { SkipStorage = storageOnDemand };
    }

    public bool IsOpen => _computer is not null;

    public void Open()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = _includeStorage,
            IsControllerEnabled = true,
            IsNetworkEnabled = false, // rede vem das APIs nativas
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
        };
        try
        {
            computer.Open();
            _computer = computer;
        }
        catch (Exception ex)
        {
            _problems.Add($"LibreHardwareMonitor não abriu: {ex.GetType().Name}: {ex.Message}");
            computer.Close();
        }
    }

    public IEnumerable<IHardware> AllHardware()
    {
        if (_computer is null) yield break;
        var stack = new Stack<IHardware>(_computer.Hardware.Reverse());
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            yield return h;
            foreach (var sub in h.SubHardware.Reverse()) stack.Push(sub);
        }
    }

    /// <summary>Casa GPUs e discos do LHM com os do inventário nativo.</summary>
    public void MapIdentities(List<GpuInfo> gpus, List<PhysicalDiskInfo> disks)
    {
        if (_computer is null) return;
        // Uma atualização completa (inclusive discos) para ler capacidade/nomes; só na abertura.
        _visitor.SkipStorage = false;
        Update();
        _visitor.SkipStorage = _storageOnDemand;

        var gpuHardware = AllHardware().Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        var extra = 0;
        foreach (var h in gpuHardware)
        {
            var match = gpus.FirstOrDefault(g => g.LhmIdentifier is null && NameEq(g.Name, h.Name))
                        ?? gpus.FirstOrDefault(g => g.LhmIdentifier is null && (NameContains(g.Name, h.Name) || NameContains(h.Name, g.Name)));
            if (match is not null)
            {
                match.LhmIdentifier = h.Identifier.ToString();
                _gpuInstance[h] = match.Index.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                _gpuInstance[h] = "lhm" + extra++;
                _problems.Add($"GPU do LHM sem par no DXGI: {h.Name} ({h.Identifier})");
            }
        }

        var storage = AllHardware().Where(h => h.HardwareType == HardwareType.Storage).ToList();
        foreach (var h in storage)
        {
            var serial = h.Properties.FirstOrDefault(kv => kv.Key.Contains("Serial", StringComparison.OrdinalIgnoreCase)).Value;
            PhysicalDiskInfo? match = null;
            string how = "";
            if (!string.IsNullOrWhiteSpace(serial))
            {
                match = disks.FirstOrDefault(d => d.LhmIdentifier is null && d.Serial is not null && Norm(d.Serial) == Norm(serial));
                how = "número de série";
            }
            if (match is null)
            {
                match = disks.Where(d => d.LhmIdentifier is null && (NameEq(d.FriendlyName, h.Name) || NameEq(d.Model, h.Name)))
                    .OrderBy(d => d.Number ?? int.MaxValue).FirstOrDefault();
                how = "nome do modelo (ordem dos discos)";
            }
            if (match is null)
            {
                match = disks.Where(d => d.LhmIdentifier is null && (NameContains(d.FriendlyName, h.Name) || NameContains(h.Name, d.FriendlyName)))
                    .OrderBy(d => d.Number ?? int.MaxValue).FirstOrDefault();
                how = "nome parcial";
            }

            if (match is not null)
            {
                var sameModel = disks.Count(d => NameEq(d.FriendlyName, match.FriendlyName));
                Pair(h, match, how + (how != "número de série" && sameModel > 1 ? $" — AMBÍGUO: {sameModel} discos iguais" : ""));
            }
        }

        // Segunda passada: gabinetes USB mostram no Windows o nome do gabinete ("Seagate Expansion Desk") e no LHM o
        // do disco interno ("ST4000DM000-…"). Casa pela capacidade quando só um disco restante tem o mesmo tamanho.
        foreach (var h in storage.Where(h => !_diskInstance.ContainsKey(h)))
        {
            var totalGb = Find(h, SensorType.Data, "Total Space")?.Value;
            var candidates = totalGb is > 0
                ? disks.Where(d => d.LhmIdentifier is null && d.SizeGb > 0 && Math.Abs(d.SizeGb - totalGb.Value) / d.SizeGb < 0.01).ToList()
                : [];
            if (candidates.Count == 1)
                Pair(h, candidates[0], "capacidade (nome diferente: gabinete/USB)");
            else
            {
                _diskInstance[h] = "lhm" + h.Identifier.ToString().Replace('/', '_');
                _problems.Add($"Disco do LHM sem par no WMI: {h.Name} ({h.Identifier})");
            }
        }

        void Pair(IHardware h, PhysicalDiskInfo disk, string how)
        {
            disk.LhmIdentifier = h.Identifier.ToString();
            disk.LhmMatch = how;
            // Gabinetes USB não informam o tipo ao Windows; o LHM enxerga o disco interno (/hdd/N, /ssd/N, /nvme/N).
            if (disk.MediaType is null or "Não especificado")
                disk.MediaType = disk.LhmIdentifier.StartsWith("/hdd", StringComparison.Ordinal) ? "HDD"
                    : disk.LhmIdentifier.StartsWith("/ssd", StringComparison.Ordinal) || disk.LhmIdentifier.StartsWith("/nvme", StringComparison.Ordinal) ? "SSD"
                    : disk.MediaType;
            _diskInstance[h] = StorageCollector.DiskKey(disk);
        }
    }

    public void Update()
    {
        if (_computer is null) return;
        try { _computer.Accept(_visitor); _updates++; }
        catch (Exception ex) { _problems.Add($"LHM Update: {ex.Message}"); }
    }

    private readonly UpdateVisitor _visitor;
    private int _updates;

    /// <summary>Atualiza e lê só SSD/NVMe (nunca HDD). Usado pelo serviço em intervalos longos.</summary>
    public List<Reading> SampleSolidState()
    {
        var readings = new List<Reading>();
        foreach (var h in AllHardware().Where(h => h.HardwareType == HardwareType.Storage))
        {
            var id = h.Identifier.ToString();
            if (!id.StartsWith("/nvme", StringComparison.Ordinal) && !id.StartsWith("/ssd", StringComparison.Ordinal)) continue;
            try
            {
                h.Update();
                Storage(h, _diskInstance.GetValueOrDefault(h, "lhm"), readings);
            }
            catch (Exception ex)
            {
                _problems.Add($"LHM disco {h.Name}: {ex.Message}");
            }
        }
        return readings;
    }

    /// <summary>Tempo médio de Update() por tipo de hardware, em ms (mostra quem pesa: SMART, SPD, GPU...).</summary>
    public Dictionary<string, double> AvgUpdateMsByHardware()
        => _visitor.Totals.ToDictionary(kv => "lhm:" + kv.Key, kv => Math.Round(kv.Value / Math.Max(1, _updates), 1));

    public IEnumerable<Reading> Sample()
    {
        if (_computer is null) return [];
        Update();
        var readings = new List<Reading>();
        var cpuIndex = 0;
        foreach (var h in AllHardware())
        {
            switch (h.HardwareType)
            {
                case HardwareType.Cpu:
                    Cpu(h, (cpuIndex++).ToString(CultureInfo.InvariantCulture), readings);
                    break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                    Gpu(h, _gpuInstance.GetValueOrDefault(h, "lhm"), readings);
                    break;
                case HardwareType.Memory:
                    Memory(h, readings);
                    break;
                case HardwareType.Storage:
                    if (!_storageOnDemand) Storage(h, _diskInstance.GetValueOrDefault(h, "lhm"), readings);
                    break;
                default:
                    Fans(h, readings);
                    break;
            }
        }
        return readings;
    }

    private void Cpu(IHardware h, string id, List<Reading> r)
    {
        Add(r, "cpu", id, "usage", "%", Find(h, SensorType.Load, "CPU Total"));

        // Núcleos "Core #N" sem sufixo; "(Effective)" e médias são variantes do mesmo dado.
        // AMD: "Core #1"; Intel híbrido: "P-Core #1" / "E-Core #1".
        var cores = h.Sensors.Where(s => s.SensorType == SensorType.Clock && CoreName().IsMatch(s.Name)).ToList();
        // "Effective" desconta o tempo dos núcleos dormindo (434 MHz em repouso no 9950X); usar o clock nominal médio.
        var average = Find(h, SensorType.Clock, "Cores (Average)");
        if (average is not null)
            Add(r, "cpu", id, "clock", "MHz", average);
        else if (cores.Count > 0)
        {
            var valid = cores.Where(c => c.Value is > 0).ToList();
            foreach (var c in cores) _mapped.Add(c.Identifier.ToString());
            r.Add(valid.Count > 0
                ? Reading.Ok("cpu", id, "clock", valid.Average(c => c.Value!.Value), "MHz", Source, $"média de {valid.Count} núcleos")
                : Reading.Unavailable("cpu", id, "clock", "MHz", Source, "núcleos sem valor (driver PawnIO/permissão)"));
        }
        if (cores.Any(c => c.Value is > 0))
            r.Add(Reading.Ok("cpu", id, "clock_max", cores.Max(c => c.Value ?? 0), "MHz", Source, "núcleo mais rápido"));

        Add(r, "cpu", id, "temp", "°C", Find(h, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tctl/Tdie", "Core Average", "Core Max"));
        Add(r, "cpu", id, "power", "W", Find(h, SensorType.Power, "CPU Package", "Package"));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^([PE]-)?Core #\d+$")]
    private static partial System.Text.RegularExpressions.Regex CoreName();

    private void Gpu(IHardware h, string id, List<Reading> r)
    {
        Add(r, "gpu", id, "usage", "%", Find(h, SensorType.Load, "GPU Core", "D3D 3D")); // Intel integrada só expõe "D3D 3D"
        Add(r, "gpu", id, "clock", "MHz", Find(h, SensorType.Clock, "GPU Core"));
        Add(r, "gpu", id, "temp", "°C", Find(h, SensorType.Temperature, "GPU Core")
                                        ?? h.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && !s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)));
        Add(r, "gpu", id, "temp_hotspot", "°C", Find(h, SensorType.Temperature, "GPU Hot Spot"));
        Add(r, "gpu", id, "vram_used", "GB", Find(h, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"), 1 / 1024.0);
        Add(r, "gpu", id, "vram_total", "GB", Find(h, SensorType.SmallData, "GPU Memory Total"), 1 / 1024.0);
        Add(r, "gpu", id, "power", "W", Find(h, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power", "GPU Core"));
        Add(r, "gpu", id, "fan", "RPM", h.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan));
        Add(r, "gpu", id, "fan_duty", "%", h.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control));
    }

    private void Memory(IHardware h, List<Reading> r)
    {
        // "Total Memory": usada/disponível ("Virtual Memory" inclui o arquivo de paginação e fica de fora).
        // DIMMs (via SPD): temperatura por módulo.
        if (!h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && Find(h, SensorType.Data, "Memory Used") is { } used)
        {
            Add(r, "ram", "0", "used", "GB", used);
            Add(r, "ram", "0", "available", "GB", Find(h, SensorType.Data, "Memory Available"));
        }
        // Só "DIMM #N"; os demais sensores de temperatura do SPD são limites fixos (55/85 °C) e a resolução (0,25).
        foreach (var t in h.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase)))
            Add(r, "ram", Slug(h.Name), "temp", "°C", t);
    }

    private void Storage(IHardware h, string id, List<Reading> r)
    {
        // NVMe também expõe "Warning/Critical Temperature" (limites fixos), que não podem ser usados como leitura.
        Add(r, "disk", id, "temp", "°C", Find(h, SensorType.Temperature, "Composite Temperature", "Temperature", "Temperature #1")
                                         ?? h.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature
                                                                          && !s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                                                                          && !s.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)));
        Add(r, "disk", id, "power_on_hours", "h", Find(h, SensorType.Factor, "Power On Hours"));
        Add(r, "disk", id, "active", "%", Find(h, SensorType.Load, "Total Activity"));
        Add(r, "disk", id, "read", "MB/s", Find(h, SensorType.Throughput, "Read Rate"), 1 / 1e6);
        Add(r, "disk", id, "write", "MB/s", Find(h, SensorType.Throughput, "Write Rate"), 1 / 1e6);
        if (Find(h, SensorType.Level, "Percentage Used") is { } pu)
            Add(r, "disk", id, "wear", "%", pu);
        else if (Find(h, SensorType.Level, "Remaining Life") is { Value: { } life } rl)
        {
            _mapped.Add(rl.Identifier.ToString());
            r.Add(Reading.Ok("disk", id, "wear", 100 - life, "%", Source, "100 − vida restante"));
        }
    }

    private void Fans(IHardware h, List<Reading> r)
    {
        foreach (var f in h.Sensors.Where(s => s.SensorType == SensorType.Fan))
        {
            Add(r, "fan", Slug(f.Name), "rpm", "RPM", f);
            // % de controle (PWM) da mesma saída: ".../fan/3" ↔ ".../control/3". O nome do perfil da BIOS não chega ao Windows.
            var index = f.Identifier.ToString().Split('/').Last();
            var control = h.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Identifier.ToString().EndsWith("/control/" + index, StringComparison.Ordinal));
            if (control?.Value is not null) Add(r, "fan", Slug(f.Name), "duty", "%", control);
        }
    }

    private void Add(List<Reading> r, string component, string instance, string metric, string unit, ISensor? sensor, double scale = 1)
    {
        if (sensor is null) return; // métrica ausente nesta fonte: a matriz trata como indisponível
        _mapped.Add(sensor.Identifier.ToString());
        // Sem driver/permissão o LHM devolve 0 (não vazio) em temperatura, consumo e clock.
        // Zero nesses tipos não é fisicamente plausível, então vira "indisponível" em vez de um zero falso.
        var implausibleZero = sensor.Value == 0 && sensor.SensorType is SensorType.Temperature or SensorType.Power or SensorType.Clock;
        if (sensor.SensorType == SensorType.Temperature && sensor.Value is { } t && (t < 1 || t > 125) && !implausibleZero)
        {
            r.Add(Reading.Failed(component, instance, metric, unit, Source, $"'{sensor.Name}' = {t:0.##} °C, fora da faixa plausível"));
            return;
        }
        r.Add(sensor.Value is { } v && !implausibleZero
            ? Reading.Ok(component, instance, metric, v * scale, unit, Source, sensor.Name)
            : Reading.Unavailable(component, instance, metric, unit, Source,
                implausibleZero ? $"'{sensor.Name}' = 0 (sem driver PawnIO ou sem administrador)" : $"sensor '{sensor.Name}' sem valor"));
    }

    private static ISensor? Find(IHardware h, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var s = h.Sensors.FirstOrDefault(x => x.SensorType == type && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s is not null) return s;
        }
        return null;
    }

    public List<LhmSensorInfo> SensorInventory()
    {
        var list = new List<LhmSensorInfo>();
        foreach (var h in AllHardware())
        foreach (var s in h.Sensors)
        {
            list.Add(new LhmSensorInfo
            {
                HardwareType = h.HardwareType.ToString(),
                Hardware = h.Name,
                HardwareId = h.Identifier.ToString(),
                Sensor = s.Name,
                SensorType = s.SensorType.ToString(),
                SensorId = s.Identifier.ToString(),
                Value = Finite(s.Value),
                Min = Finite(s.Min),
                Max = Finite(s.Max),
                Mapped = _mapped.Contains(s.Identifier.ToString()),
            });
        }
        return list;
    }

    private static double? Finite(float? v) => v is { } f && float.IsFinite(f) ? Math.Round(f, 3) : null;
    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    private static bool NameEq(string? a, string? b) => a is not null && b is not null && Norm(a) == Norm(b);
    private static bool NameContains(string? a, string? b) => a is not null && b is not null && Norm(b).Length > 0 && Norm(a).Contains(Norm(b));
    private static string Slug(string s) => string.Join('-', s.Split([' ', '#', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public void Dispose()
    {
        try { _computer?.Close(); } catch { /* fechar o driver nunca deve derrubar o relatório */ }
        _computer = null;
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public Dictionary<string, double> Totals { get; } = new();
        public bool SkipStorage { get; set; }

        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            if (SkipStorage && hardware.HardwareType == HardwareType.Storage) return;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            hardware.Update();
            var key = hardware.HardwareType.ToString();
            Totals[key] = Totals.GetValueOrDefault(key) + System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
