using System.Collections.ObjectModel;
using System.Windows;
using HwMon.App.Infrastructure;
using HwMon.App.Services;
using HwMon.Client;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>
/// Uma máquina pareada: mantém a conexão, os valores atuais, um histórico curto (2 min) e os cartões do painel.
/// </summary>
public sealed class MachineViewModel : ObservableObject
{
    private const int HistoryLength = 120;
    private readonly SettingsStore _settings;
    private readonly Dictionary<(string, string), Queue<double>> _history = new();
    private CancellationTokenSource? _cts;
    private int _descriptorVersion = -1;
    private DateTimeOffset _lastSnapshotTime;

    private ConnectionStatus _status = ConnectionStatus.Connecting;
    private string _statusText = "conectando...";
    private string _ageText = "";
    private MachineDescriptor? _descriptor;
    private bool _isCustomizing;

    private readonly MachineStore _store;

    public MachineViewModel(PairedMachine machine, SettingsStore settings, MachineStore store)
    {
        Machine = machine;
        _settings = settings;
        _store = store;
        SystemPage = new SystemPageViewModel(machine);
        Processes = new ProcessListViewModel(machine);
    }

    /// <summary>Cartão "Processos" (consulta o coletor só com a página aberta).</summary>
    public ProcessListViewModel Processes { get; }

    /// <summary>Página da máquina aberta ou fechada: liga/desliga a consulta de processos.</summary>
    public void SetActive(bool active)
    {
        if (active && ShowProcesses) Processes.Start();
        else Processes.Stop();
    }

    /// <summary>Mostrar o cartão de processos (desmarcável no modo Personalizar).</summary>
    public bool ShowProcesses
    {
        get => !IsHiddenPref("processes", false);
        set
        {
            SetHidden("processes", !value);
            Raise();
            if (value) Processes.Start();
            else Processes.Stop();
        }
    }

    /// <summary>Aba "Sistema" (resumo estilo CPU-Z).</summary>
    public SystemPageViewModel SystemPage { get; }

    /// <summary>Aba ativa da página da máquina: 0 Painel, 1 Sistema, 2 Benchmarks.</summary>
    private int _tab;

    private void SetTab(int tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        Raise(nameof(ShowPanel));
        Raise(nameof(ShowSystem));
        Raise(nameof(ShowBench));
        Raise(nameof(PanelVisible));
        Raise(nameof(SystemVisible));
        Raise(nameof(BenchVisible));
        if (tab == 1) SystemPage.EnsureLoaded();
        if (tab == 2) Benchmarks.EnsureLoaded();
    }

    public bool ShowPanel { get => _tab == 0; set { if (value) SetTab(0); } }
    public bool ShowSystem { get => _tab == 1; set { if (value) SetTab(1); } }
    public bool ShowBench { get => _tab == 2; set { if (value) SetTab(2); } }

    /// <summary>Aba "Benchmarks" (disco e rede).</summary>
    public BenchmarkPageViewModel Benchmarks => _benchmarks ??= new BenchmarkPageViewModel(this);
    private BenchmarkPageViewModel? _benchmarks;

    // ------------------------------------------------------------------ histórico

    private HistoryViewModel? _historyPage;

    /// <summary>Página de histórico aberta (clique num cartão); nula = abas Painel/Sistema.</summary>
    public HistoryViewModel? HistoryPage
    {
        get => _historyPage;
        private set
        {
            _historyPage?.Stop();
            if (!Set(ref _historyPage, value)) return;
            Raise(nameof(IsHistoryOpen));
            Raise(nameof(PanelVisible));
            Raise(nameof(SystemVisible));
            Raise(nameof(BenchVisible));
        }
    }

    public bool IsHistoryOpen => _historyPage is not null;
    public bool PanelVisible => _tab == 0 && _historyPage is null;
    public bool SystemVisible => _tab == 1 && _historyPage is null;
    public bool BenchVisible => _tab == 2 && _historyPage is null;

    public void OpenHistory(CardViewModel card) => HistoryPage = new HistoryViewModel(this, card);
    public void CloseHistory() => HistoryPage = null;

    private DateTimeOffset _lastStats = DateTimeOffset.MinValue;
    private bool _statsLoading;

    /// <summary>Mínimo, média e máximo das últimas 24 h embaixo do gráfico de cada cartão (a cada minuto).</summary>
    private async Task RefreshStatsAsync()
    {
        if (_statsLoading || DateTimeOffset.Now - _lastStats < TimeSpan.FromSeconds(60)) return;
        _statsLoading = true;
        _lastStats = DateTimeOffset.Now;
        try
        {
            var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var client = CollectorClient.For(Machine);
            var stats = await client.HistoryStatsAsync(to - 86400, to);
            var map = stats.Series.ToDictionary(s => (s.Component, s.Metric));
            foreach (var card in Cards)
            {
                if (card.MainMetric is not { } main || !map.TryGetValue(main, out var s)) continue;
                card.StatsText = L.T("24 h · mín {0} · méd {1} · máx {2}", Format.Number(s.Min, s.Unit), Format.Number(s.Avg, s.Unit), Format.Number(s.Max, s.Unit));
            }
        }
        catch (CollectorException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            _lastStats = DateTimeOffset.Now.AddMinutes(10); // coletor antigo, sem histórico: tenta de novo daqui a pouco
        }
        catch (Exception)
        {
            // falha passageira: tenta no próximo minuto
        }
        finally
        {
            _statsLoading = false;
        }
    }

    public PairedMachine Machine { get; }
    public string MachineId => Machine.MachineId;
    public string Name => Machine.Name;
    public string? Role => _descriptor?.Role;

    /// <summary>
    /// "ACBOER · MSI MAG X870 TOMAHAWK WIFI · Windows 11 Pro 26300.9711 (26H2)": nome do PC no Windows (quando o
    /// apelido é outro), placa-mãe (coletor 0.7.1+; antes, o modelo do sistema) e Windows.
    /// </summary>
    public string Description
    {
        get
        {
            if (_descriptor is null) return $"{Machine.Host}:{Machine.Port}";
            var parts = new List<string>();
            if (!string.Equals(_descriptor.HostName, Name, StringComparison.OrdinalIgnoreCase)) parts.Add(_descriptor.HostName);
            parts.Add(_descriptor.Board ?? _descriptor.Model ?? "");
            parts.Add((_descriptor.Os ?? "").Replace("Microsoft ", ""));
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    // ------------------------------------------------------------------ renomear

    private bool _isRenaming;
    private string _editName = "";
    private string? _renameError;

    public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public string? RenameError { get => _renameError; private set => Set(ref _renameError, value); }

    public void BeginRename()
    {
        EditName = Name;
        RenameError = null;
        IsRenaming = true;
    }

    public void CancelRename() => IsRenaming = false;

    /// <summary>Grava o novo apelido (só neste PC; o coletor e o nome do Windows não mudam).</summary>
    public bool CommitRename()
    {
        var name = EditName.Trim();
        if (name.Length == 0) name = _descriptor?.HostName ?? Name; // vazio = volta ao nome do PC
        if (name == Name)
        {
            IsRenaming = false;
            return true;
        }
        if (!_store.Rename(MachineId, name))
        {
            RenameError = L.T("Já existe uma máquina chamada \"{0}\".", name);
            return false;
        }
        Machine.Name = name;
        IsRenaming = false;
        Raise(nameof(Name));
        Raise(nameof(Description));
        Updated?.Invoke();
        return true;
    }
    public bool IsNas => Role?.Contains("NAS", StringComparison.OrdinalIgnoreCase) == true;

    public ConnectionStatus Status { get => _status; private set { if (Set(ref _status, value)) { Raise(nameof(IsConnected)); Raise(nameof(CanWake)); } } }

    // ------------------------------------------------------------------ ligar pela rede (Wake-on-LAN)

    /// <summary>Botão "Ligar": a máquina não responde e o serviço já informou o MAC da placa de rede dela.</summary>
    public bool CanWake => !IsConnected && Machine.WakeMacs.Count > 0;

    private string? _wakeText;
    /// <summary>Resultado do último "Ligar" (some quando a máquina conecta).</summary>
    public string? WakeText { get => _wakeText; private set => Set(ref _wakeText, value); }

    private RelayCommand? _wakeCommand;
    public RelayCommand WakeCommand => _wakeCommand ??= new RelayCommand(() => _ = WakeAsync());

    /// <summary>Quanto esperar o PC responder depois do sinal antes de dizer que não deu certo.</summary>
    private static readonly TimeSpan WakeWait = TimeSpan.FromMinutes(3);
    private DateTimeOffset? _wakeSent;

    /// <summary>Sinal enviado e o PC ainda não respondeu: o botão vira "Ligando… 0:42" e fica desativado.</summary>
    public bool IsWaking => _wakeSent is not null;
    public bool CanPressWake => !IsWaking;
    public string WakeButtonText => _wakeSent is { } sent
        ? L.T("Ligando… {0}", (DateTimeOffset.Now - sent).ToString(@"m\:ss"))
        : WakeFailed ? L.T("Tentar de novo") : L.T("Ligar este PC");
    private bool WakeFailed { get; set; }

    private async Task WakeAsync()
    {
        if (IsWaking) return;
        try
        {
            var sent = await HwMon.Client.WakeOnLan.SendAsync(Machine.WakeMacs, Machine.KnownAddresses);
            if (sent == 0)
            {
                WakeText = L.T("Não foi possível enviar o sinal: nenhuma rede disponível neste PC.");
                return;
            }
            _wakeSent = DateTimeOffset.Now;
            WakeFailed = false;
            WakeText = L.T("Sinal enviado às {0}. Aguardando o PC ligar e o Corewise responder (costuma levar de 30 s a 2 min).", DateTime.Now.ToString("HH:mm", L.Culture));
        }
        catch (Exception ex)
        {
            WakeText = L.T("Não foi possível enviar o sinal: {0}", ex.Message);
        }
        RaiseWake();
    }

    /// <summary>A cada segundo (relógio da tela principal): conta o tempo e desiste depois de 3 min.</summary>
    private void TickWake()
    {
        if (_wakeSent is not { } sent) return;
        if (IsConnected)
        {
            WakeText = null;
            _wakeSent = null;
        }
        else if (DateTimeOffset.Now - sent > WakeWait)
        {
            _wakeSent = null;
            WakeFailed = true;
            WakeText = L.T("O PC não respondeu em 3 minutos. Confira se ele está no cabo de rede e se o Wake on LAN está ativado no BIOS e na placa de rede (Gerenciador de Dispositivos › placa de rede › Avançado › Wake on Magic Packet).");
        }
        RaiseWake();
    }

    private void RaiseWake()
    {
        Raise(nameof(IsWaking));
        Raise(nameof(CanPressWake));
        Raise(nameof(WakeButtonText));
    }
    public bool IsConnected => _status == ConnectionStatus.Connected;
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string AgeText { get => _ageText; private set => Set(ref _ageText, value); }
    public DateTimeOffset? LastUpdate { get; private set; }
    public MachineDescriptor? Descriptor { get => _descriptor; private set => Set(ref _descriptor, value); }
    public Dictionary<(string Component, string Metric), TelemetryValue> Values { get; private set; } = new();

    /// <summary>Todos os cartões na ordem base da grade (a grade ainda sobe os largos e os em alerta).</summary>
    public ObservableCollection<CardViewModel> Cards { get; } = new();
    public ObservableCollection<TileViewModel> Tiles { get; } = new();

    public bool IsCustomizing
    {
        get => _isCustomizing;
        set => Set(ref _isCustomizing, value);
    }

    /// <summary>Ícone da máquina: NAS, workstation ou genérico.</summary>
    public string MachineIcon => IsNas ? "machine-nas" : _descriptor is null ? "machine-generic" : "machine-workstation";

    /// <summary>Disparado a cada snapshot aplicado (o widget escuta).</summary>
    public event Action? Updated;

    // ------------------------------------------------------------------ conexão

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var dispatcher = Application.Current.Dispatcher;
        var connection = new MachineConnection(Machine, _store);
        _ = Task.Run(() => connection.RunAsync(state => dispatcher.InvokeAsync(() => Apply(state)), token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private void Apply(ConnectionState state)
    {
        Status = state.Status;
        if (state.Status == ConnectionStatus.Connected && (WakeText is not null || _wakeSent is not null || WakeFailed))
        {
            WakeText = null;
            _wakeSent = null;
            WakeFailed = false;
            RaiseWake();
        }
        StatusText = state.Status switch
        {
            ConnectionStatus.Connected => L.T("conectado"),
            ConnectionStatus.Connecting => L.T("conectando..."),
            ConnectionStatus.Disconnected => L.T("desconectado desde {0}", state.Since.ToString("HH:mm:ss", Format.Pt)),
            _ => state.Message ?? L.T("falha"),
        };

        // Guarda os MACs (vêm com a descrição da máquina) para poder ligá-la quando estiver desligada.
        if (state.Descriptor is { WakeMacs.Count: > 0 } wd && !wd.WakeMacs.SequenceEqual(Machine.WakeMacs))
        {
            Machine.WakeMacs = wd.WakeMacs.ToList();
            var id = MachineId;
            var macs = Machine.WakeMacs;
            _ = Task.Run(() => { try { _store.RememberWakeMacs(id, macs); } catch (Exception) { /* tenta na próxima conexão */ } });
            Raise(nameof(CanWake));
        }

        if (state.Descriptor is { } d && (d.Version != _descriptorVersion || _descriptor?.MachineId != d.MachineId))
        {
            Descriptor = d;
            _descriptorVersion = d.Version;
            Raise(nameof(Role));
            Raise(nameof(Description));
            Raise(nameof(IsNas));
            Raise(nameof(MachineIcon));
            Rebuild();
        }

        if (state.Snapshot is { } s && s.Time != _lastSnapshotTime)
        {
            _lastSnapshotTime = s.Time;
            Values = s.Values.ToDictionary(v => (v.Component, v.Metric));
            foreach (var v in s.Values)
            {
                if (v.Value is not { } x || v.State != ReadingState.Ok) continue;
                if (!_history.TryGetValue((v.Component, v.Metric), out var q)) _history[(v.Component, v.Metric)] = q = new Queue<double>(HistoryLength + 1);
                q.Enqueue(x);
                while (q.Count > HistoryLength) q.Dequeue();
            }
            // Hora de chegada aqui, não a do relógio da outra máquina: um relógio alguns segundos atrasado (o NAS) deixava
            // "atualizado há 3 s" piscando o tempo todo, com a conexão ao vivo.
            LastUpdate = DateTimeOffset.Now;
        }

        Refresh();
        Updated?.Invoke();
        if (Status == ConnectionStatus.Connected && Cards.Count > 0) _ = RefreshStatsAsync();
    }

    public void RefreshAge()
    {
        TickWake();
        AgeText = Status == ConnectionStatus.Connected && LastUpdate is { } t ? L.T("atualizado {0}", Format.Ago(t)) : StatusText;
    }

    // ------------------------------------------------------------------ valores

    public TelemetryValue? Value(string component, string metric) => Values.GetValueOrDefault((component, metric));

    public double[] History(string component, string metric)
        => _history.TryGetValue((component, metric), out var q) ? q.ToArray() : [];

    /// <summary>Leitura antiga: estado Stale no coletor ou máquina desconectada.</summary>
    public bool IsStale(TelemetryValue? v) => v is null || v.State == ReadingState.Stale || !IsConnected;

    public string Text(string component, string metric)
    {
        var v = Value(component, metric);
        if (metric == "health") return v?.Note ?? "—";
        if (component.StartsWith("volume:", StringComparison.Ordinal) && metric == "used_pct")
            return UsedFraction(component) is { } f ? Format.Number(f * 100, "%") : "—";
        return Format.Value(v);
    }

    public double? UsedFraction(string volumeId)
    {
        var used = Value(volumeId, "used")?.Value;
        var total = Value(volumeId, "total")?.Value;
        return used is { } u && total is > 0 ? Math.Clamp(u / total.Value, 0, 1) : null;
    }

    public ComponentDescriptor? Component(string id) => _descriptor?.Components.FirstOrDefault(c => c.Id == id);

    // ------------------------------------------------------------------ cartões

    private void Rebuild()
    {
        Cards.Clear();
        Tiles.Clear();
        if (_descriptor is null) return;
        var comps = _descriptor.Components.OrderBy(c => c.Order).ToList();

        var cpu = comps.Where(c => c.Kind == ComponentKind.Cpu).Select(c =>
        {
            var card = Card(c, "CPU", "usage", "kind-cpu", wide: !IsNas);
            card.Lines.Add(Line(c.Id, "clock"));
            card.Lines.Add(Line(c.Id, "temp"));
            card.Lines.Add(Line(c.Id, "power"));
            card.DetailLines.Add(Line(c.Id, "clock_max", L.T("Núcleo mais rápido")));
            return card;
        }).ToList();

        var ram = new List<CardViewModel>();
        if (comps.FirstOrDefault(c => c.Id == "ram:0") is { } mem)
        {
            var card = Card(mem, "RAM", "load", "kind-ram");
            card.Lines.Add(Line(mem.Id, "used", L.T("Usada")));
            card.Lines.Add(Line(mem.Id, "available"));
            foreach (var dimm in comps.Where(c => c.Kind == ComponentKind.Ram && c.Id != "ram:0"))
            {
                card.ExtraComponentIds.Add(dimm.Id);
                card.Lines.Add(Line(dimm.Id, "temp", L.T("Temp. ") + dimm.Name.Replace("Módulo ", "", StringComparison.Ordinal).Replace("Module ", "", StringComparison.Ordinal)));
            }
            ram.Add(card);
        }

        var mainGpu = MainGpu(comps);
        var gpu = comps.Where(c => c.Kind == ComponentKind.Gpu).OrderBy(c => c == mainGpu ? 0 : 1).Select(c =>
        {
            var integrated = IsIntegrated(c);
            var card = Card(c, "GPU", "usage", integrated ? "kind-gpu-integrated" : "kind-gpu", wide: !IsNas && c == mainGpu && !integrated);
            card.Lines.Add(Line(c.Id, "clock"));
            card.Lines.Add(Line(c.Id, "temp"));
            card.Lines.Add(Line(c.Id, "vram_used", "VRAM"));
            card.Lines.Add(Line(c.Id, "power"));
            card.Lines.Add(Line(c.Id, "fan"));
            card.DetailLines.Add(Line(c.Id, "temp_hotspot"));
            return card;
        }).ToList();

        var volumes = comps.Where(c => c.Kind == ComponentKind.Volume).ToList();
        var disks = comps.Where(c => c.Kind == ComponentKind.Disk).Select(c =>
        {
            var badge = c.Detail?.StartsWith("HDD", StringComparison.Ordinal) == true ? "HDD"
                : c.Detail?.StartsWith("SSD", StringComparison.Ordinal) == true ? "SSD" : L.T("DISCO");
            var icon = c.Detail?.Contains("USB", StringComparison.OrdinalIgnoreCase) == true ? "kind-disk-external"
                : badge == "HDD" ? "kind-hdd"
                : c.Detail?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) == true ? "kind-nvme"
                : badge == "SSD" ? "kind-ssd" : "kind-storage";
            var card = Card(c, badge, "active", icon);
            card.Lines.Add(Line(c.Id, "read"));
            card.Lines.Add(Line(c.Id, "write"));
            card.Lines.Add(Line(c.Id, "temp"));
            card.Lines.Add(Line(c.Id, "health"));
            card.Lines.Add(Line(c.Id, "wear"));
            card.DetailLines.Add(Line(c.Id, "power_on_hours"));
            foreach (var v in volumes.Where(v => v.Parents.Contains(c.Id)))
                card.Volumes.Add(new VolumeItem(v.Id, v.Name) { IsVisible = !IsHiddenPref(v.Id, DefaultHidden(v)) });
            return card;
        }).ToList();

        var net = comps.Where(c => c.Kind == ComponentKind.Network).Select(c =>
        {
            var icon = DefaultHidden(c) ? "kind-network-virtual"
                : (c.Name + " " + c.Detail).Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                  || (c.Name + " " + c.Detail).Contains("Wireless", StringComparison.OrdinalIgnoreCase) ? "kind-wifi" : "kind-network";
            var card = Card(c, L.T("REDE"), "rx", icon);
            card.Lines.Add(Line(c.Id, "tx"));
            card.Lines.Add(Line(c.Id, "rx_today"));
            card.Lines.Add(Line(c.Id, "tx_today"));
            card.DetailLines.Add(Line(c.Id, "rx_total"));
            card.DetailLines.Add(Line(c.Id, "tx_total"));
            return card;
        }).ToList();

        var peripherals = comps.Where(c => c.Kind == ComponentKind.Peripheral).Select(c =>
            Card(c, L.T("BATERIA"), "battery", PeripheralIcon(c))).ToList();

        // Energia: soma do que tem sensor de consumo (CPU e GPUs). O resto do PC não é medido.
        var energy = new List<CardViewModel>();
        var powered = comps.Where(c => c.Kind is ComponentKind.Cpu or ComponentKind.Gpu).ToList();
        if (powered.Count > 0)
        {
            var card = Track(new CardViewModel("energy", EnergyKind, "W", L.T("Energia"), L.T("placa-mãe, discos e ventoinhas não têm sensor"))
            {
                IsHidden = IsHiddenPref("energy", false),
                Icon = "metric-power",
            });
            foreach (var c in powered)
                card.Lines.Add(Line(c.Id, "power", c.Kind == ComponentKind.Cpu ? "CPU" : CleanName(c.Name).Replace("NVIDIA ", "").Replace("GeForce ", "")));
            energy.Add(card);
        }

        var fans = comps.Where(c => c.Kind == ComponentKind.Fan)
            .OrderBy(c => int.TryParse(new string(c.Name.Where(char.IsDigit).ToArray()), out var n) ? n : 99).ToList();
        var fanCards = new List<CardViewModel>();
        if (fans.Count > 0)
        {
            var card = Track(new CardViewModel("fans", ComponentKind.Fan, "FAN", L.T("Ventoinhas"), L.T("placa-mãe"))
            {
                IsHidden = IsHiddenPref("fans", false),
                Icon = "kind-fan",
            });
            foreach (var f in fans)
            {
                card.ExtraComponentIds.Add(f.Id);
                card.Lines.Add(Line(f.Id, "rpm", L.T(f.Name)));
            }
            fanCards.Add(card);
        }

        // NAS: armazenamento primeiro, como nos sistemas de NAS.
        var order = IsNas
            ? new[] { disks, net, cpu, ram, gpu, energy, peripherals, fanCards }
            : new[] { cpu, gpu, ram, energy, disks, net, peripherals, fanCards };
        // Ordem arrastada pelo usuário (se houver); cartões que não estavam na lista ficam no fim, na ordem padrão.
        var defaults = order.SelectMany(g => g).ToList();
        if (_settings.Current.CardOrder.TryGetValue(MachineId, out var saved))
            defaults = defaults.OrderBy(c => saved.IndexOf(c.ComponentId) is var i and >= 0 ? i : saved.Count + defaults.IndexOf(c)).ToList();
        foreach (var card in defaults) Cards.Add(card);

        BuildTiles();
    }

    /// <summary>Arrastou <paramref name="card"/> para o lugar de <paramref name="target"/>.</summary>
    public void MoveCard(CardViewModel card, CardViewModel target)
    {
        var from = Cards.IndexOf(card);
        var to = Cards.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Cards.Move(from, to);
        _settings.Current.CardOrder[MachineId] = Cards.Select(c => c.ComponentId).ToList();
        _settings.SaveSoon();
        Raise(nameof(HasCustomOrder));
    }

    public bool HasCustomOrder => _settings.Current.CardOrder.ContainsKey(MachineId);

    public void ResetCardOrder()
    {
        _settings.Current.CardOrder.Remove(MachineId);
        _settings.SaveSoon();
        Rebuild();
        Raise(nameof(HasCustomOrder));
    }

    /// <summary>Cartão calculado no painel (não é um componente do coletor).</summary>
    public const string EnergyKind = "energy";

    /// <summary>Ícone pelo tipo no detalhe ("Headset · Lightspeed").</summary>
    public static string PeripheralIcon(ComponentDescriptor c) => c.Detail?.Split(" · ")[0] switch
    {
        "Headset" => "kind-headset",
        "Mouse" or "Trackball" => "kind-mouse",
        "Teclado" or "Keyboard" or "Trackpad" or "Teclado numérico" or "Numpad" => "kind-keyboard",
        "Controle" or "Controller" => "kind-gamepad",
        _ => "metric-battery",
    };

    /// <summary>Bateria baixa: até 10 % crítico, até 20 % atenção (carregando não conta).</summary>
    public static Severity BatterySeverity(TelemetryValue? v)
        => v?.Value is not { } pct || v.State != ReadingState.Ok || v.Note?.StartsWith("carreg", StringComparison.Ordinal) == true || v.Note?.StartsWith("charg", StringComparison.Ordinal) == true ? Severity.Normal
            : pct <= 10 ? Severity.Critical : pct <= 20 ? Severity.Caution : Severity.Normal;

    /// <summary>"AMD Ryzen 9 9950X 16-Core Processor" → "AMD Ryzen 9 9950X"; "Intel(R) Core(TM) i5-14400" → "Intel Core i5-14400".</summary>
    public static string CleanName(string name)
    {
        var s = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\d+-Core Processor|\s+Processor|\s+CPU @.*$", "");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    private CardViewModel Card(ComponentDescriptor c, string badge, string mainMetric, string icon, bool wide = false) => Track(new CardViewModel(c.Id, c.Kind, badge,
        c.Kind is ComponentKind.Cpu or ComponentKind.Gpu ? CleanName(c.Name) : L.T(c.Name), c.Detail is null ? null : L.T(c.Detail))
    {
        MainMetric = (c.Id, mainMetric),
        IsHidden = IsHiddenPref(c.Id, DefaultHidden(c)),
        Icon = icon,
        BaseWide = wide,
    });

    /// <summary>Marcar/desmarcar no modo Personalizar grava a preferência na hora.</summary>
    private CardViewModel Track(CardViewModel card)
    {
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CardViewModel.IsHidden)) SetHidden(card.ComponentId, card.IsHidden);
        };
        foreach (var vol in card.Volumes) TrackVolume(vol);
        card.Volumes.CollectionChanged += (_, e) =>
        {
            foreach (VolumeItem vol in e.NewItems ?? Array.Empty<VolumeItem>()) TrackVolume(vol);
        };
        return card;
    }

    private void TrackVolume(VolumeItem vol) => vol.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(VolumeItem.IsVisible)) SetHidden(vol.ComponentId, !vol.IsVisible);
    };

    private static MetricLine Line(string component, string metric, string? label = null) => new(component, metric, label ?? Format.MetricLabel(metric));

    /// <summary>Ocultos por padrão: redes virtuais (Hyper-V, WSL, VPN) e volumes sem letra (recuperação, EFI).</summary>
    private static bool DefaultHidden(ComponentDescriptor c) => c.Kind switch
    {
        ComponentKind.Network => c.Detail?.Contains("virtual", StringComparison.OrdinalIgnoreCase) == true
                                 || c.Detail?.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) == true,
        ComponentKind.Volume => L.Is(c.Name, "Sem letra"),
        _ => false,
    };

    private bool IsHiddenPref(string componentId, bool defaultHidden)
    {
        var key = AppSettings.Key(MachineId, componentId);
        return _settings.Current.PanelHidden.Contains(key) || (defaultHidden && !_settings.Current.PanelShown.Contains(key));
    }

    public void SetHidden(string componentId, bool hidden)
    {
        var key = AppSettings.Key(MachineId, componentId);
        if (hidden)
        {
            _settings.Current.PanelHidden.Add(key);
            _settings.Current.PanelShown.Remove(key);
        }
        else
        {
            _settings.Current.PanelHidden.Remove(key);
            _settings.Current.PanelShown.Add(key);
        }
        _settings.SaveSoon();
    }

    // ------------------------------------------------------------------ atualização

    private void Refresh()
    {
        foreach (var card in Cards) UpdateCard(card);
        UpdateTiles();
        RefreshAge();
    }

    private void UpdateCard(CardViewModel card)
    {
        var worst = Severity.Normal;
        foreach (var line in card.Lines.Concat(card.DetailLines))
        {
            var v = Value(line.ComponentId, line.Metric);
            line.HasValue = v?.Value is not null || (line.Metric == "health" && v?.Note is not null);
            line.Value = line.Metric switch
            {
                "vram_used" when Value(line.ComponentId, "vram_total")?.Value is { } total && v?.Value is { } used
                    => L.T("{0} de {1}", Format.Number(used, "GB"), Format.Number(total, "GB")),
                "used" when line.ComponentId == "ram:0" && Value("ram:0", "total")?.Value is { } t && v?.Value is { } u
                    => L.T("{0} de {1}", Format.Number(u, "GB"), Format.Number(t, "GB")),
                // Ventoinha: RPM e a % de controle (PWM) definida pela BIOS/curva da placa
                "rpm" when Value(line.ComponentId, "duty")?.Value is { } duty && v?.Value is { } rpm
                    => $"{Format.Number(rpm, "RPM")} · {duty:0} %",
                "fan" when Value(line.ComponentId, "fan_duty")?.Value is { } gduty && v?.Value is { } grpm
                    => $"{Format.Number(grpm, "RPM")} · {gduty:0} %",
                _ => Text(line.ComponentId, line.Metric),
            };
            line.IsStale = IsStale(v);
            line.Note = v is null ? null : $"{(v.Note is null ? "" : L.T(v.Note) + " · ")}" + L.T("fonte: {0} · {1}", v.Source, v.Time.ToString("HH:mm:ss", Format.Pt));
            line.Severity = LineSeverity(card, line, v);
            if (line.Severity > worst) worst = line.Severity;
        }
        // Ventoinha parada (0 RPM) sem nada ligado: esconde a linha.
        if (card.Kind == ComponentKind.Fan)
            foreach (var line in card.Lines) line.HasValue &= Value(line.ComponentId, "rpm")?.Value > 0;

        foreach (var vol in card.Volumes)
        {
            var f = UsedFraction(vol.ComponentId);
            vol.Fraction = f ?? 0;
            vol.Text = f is null ? "—" : L.T("{0} livres de {1}", Text(vol.ComponentId, "free"), Text(vol.ComponentId, "total"));
            vol.IsStale = IsStale(Value(vol.ComponentId, "used"));
            var s = f >= 0.9 ? Severity.Critical : f >= 0.8 ? Severity.Caution : Severity.Normal;
            if (vol.IsVisible && s > worst) worst = s;
        }

        if (card.MainMetric is { } main)
        {
            var v = Value(main.ComponentId, main.Metric);
            card.MainValue = Text(main.ComponentId, main.Metric);
            card.MainStale = IsStale(v);
            card.History = History(main.ComponentId, main.Metric);
            card.HistoryMax = v?.Unit == "%" ? 100 : Math.Max(1, card.History.DefaultIfEmpty(0).Max() * 1.2);
            card.MainCaption = card.Kind switch
            {
                ComponentKind.Ram => L.T("em uso"),
                ComponentKind.Cpu or ComponentKind.Gpu => L.T("de uso"),
                ComponentKind.Disk => L.T("tempo ativo"),
                ComponentKind.Network => L.T("recebendo · enviando {0}", Text(main.ComponentId, "tx")),
                // "carregando", "na bateria" ou "última leitura às 14:02" (aparelho desligado/em repouso)
                ComponentKind.Peripheral => PeripheralCaption(main.ComponentId, v),
                _ => null,
            };
            if (card.Kind == ComponentKind.Peripheral) worst = BatterySeverity(v);
        }
        else if (card.Kind == ComponentKind.Fan)
        {
            var spinning = card.Lines.Count(l => l.HasValue);
            card.MainValue = spinning.ToString(Format.Pt);
            card.MainCaption = L.T("girando");
        }
        else if (card.Kind == EnergyKind)
        {
            // Total = soma das linhas; o gráfico soma os históricos das partes (alinhados pelo fim).
            var parts = card.Lines.Select(l => Value(l.ComponentId, "power")).Where(v => v?.Value is not null).ToList();
            var total = parts.Sum(v => v!.Value!.Value);
            card.MainValue = parts.Count > 0 ? Format.Number(total, "W") : "—";
            card.MainStale = parts.Count == 0 || parts.Any(IsStale);
            card.MainCaption = L.T("CPU + placa de vídeo, agora");
            var histories = card.Lines.Select(l => History(l.ComponentId, "power")).Where(h => h.Length > 0).ToList();
            var length = histories.Count == 0 ? 0 : histories.Min(h => h.Length);
            card.History = Enumerable.Range(0, length).Select(i => histories.Sum(h => h[h.Length - length + i])).ToArray();
            card.HistoryMax = Math.Max(10, card.History.DefaultIfEmpty(0).Max() * 1.2);
        }
        card.Severity = worst;
    }

    /// <summary>"carregando · cheia em ~40 min", "na bateria · ~9 h restantes" ou só o estado.</summary>
    private string PeripheralCaption(string componentId, TelemetryValue? battery)
    {
        var state = L.T(battery?.Note?.Split(" · ")[0] ?? "bateria");
        if (Value(componentId, "charge_eta") is { State: ReadingState.Ok, Value: { } eta }) return state + " · " + L.T("cheia em {0}", Format.Duration(eta));
        if (Value(componentId, "runtime") is { State: ReadingState.Ok, Value: { } left }) return state + " · " + L.T("{0} restantes", Format.Duration(left));
        return state;
    }

    private Severity LineSeverity(CardViewModel card, MetricLine line, TelemetryValue? v)
    {
        if (line.Metric == "health")
            return v?.Note is null || L.Is(v.Note, "Saudável") ? Severity.Normal : L.Is(v.Note, "Atenção") ? Severity.Caution : Severity.Critical;
        if (v?.Value is not { } x || line.Metric != "temp") return Severity.Normal;
        var (caution, critical) = card.Kind switch
        {
            ComponentKind.Cpu => (80.0, 90.0),
            ComponentKind.Gpu => (75.0, 85.0),
            ComponentKind.Ram => (70.0, 80.0),
            ComponentKind.Disk when card.Badge == "HDD" => (50.0, 55.0),
            ComponentKind.Disk => (60.0, 70.0),
            _ => (double.MaxValue, double.MaxValue),
        };
        return x >= critical ? Severity.Critical : x >= caution ? Severity.Caution : Severity.Normal;
    }

    // ------------------------------------------------------------------ visão geral

    private TileViewModel? _tCpu, _tGpu, _tRam, _tNet, _tStorage, _tDisks;
    private string? _cpuId, _gpuId;
    private List<string> _physicalNets = new(), _letteredVolumes = new(), _diskIds = new();

    private void BuildTiles()
    {
        var comps = _descriptor!.Components;
        _cpuId = comps.FirstOrDefault(c => c.Kind == ComponentKind.Cpu)?.Id;
        _gpuId = MainGpu(comps)?.Id;
        _physicalNets = comps.Where(c => c.Kind == ComponentKind.Network && !DefaultHidden(c)).Select(c => c.Id).ToList();
        _letteredVolumes = comps.Where(c => c.Kind == ComponentKind.Volume && !L.Is(c.Name, "Sem letra")).Select(c => c.Id).ToList();
        _diskIds = comps.Where(c => c.Kind == ComponentKind.Disk).Select(c => c.Id).ToList();

        _peripheralIds = comps.Where(c => c.Kind == ComponentKind.Peripheral).Select(c => c.Id).ToList();

        _tCpu = new TileViewModel("CPU", "kind-cpu");
        _tGpu = _gpuId is null ? null : new TileViewModel("GPU", "kind-gpu");
        _tRam = new TileViewModel(L.T("Memória"), "kind-ram");
        _tNet = new TileViewModel(L.T("Rede"), "kind-network");
        _tStorage = new TileViewModel(L.T("Espaço"), "tile-space");
        _tDisks = new TileViewModel(L.T("Discos"), "tile-disk-health");
        _tPower = new TileViewModel(L.T("Energia"), "metric-power");
        _tPeriph = _peripheralIds.Count == 0 ? null : new TileViewModel(L.T("Periféricos"), "metric-battery");

        // Clique no bloco abre o histórico do cartão correspondente.
        _tCpu.Target = _cpuId;
        if (_tGpu is not null) _tGpu.Target = _gpuId;
        _tRam.Target = comps.Any(c => c.Id == "ram:0") ? "ram:0" : null;
        _tNet.Target = _physicalNets.FirstOrDefault();
        _tStorage.Target = comps.FirstOrDefault(c => c.Id == _letteredVolumes.FirstOrDefault())?.Parents.FirstOrDefault(p => _diskIds.Contains(p)) ?? _diskIds.FirstOrDefault();
        _tDisks.Target = _diskIds.FirstOrDefault();
        _tPower.Target = comps.Any(c => c.Kind is ComponentKind.Cpu or ComponentKind.Gpu) ? "energy" : null;
        if (_tPeriph is not null) _tPeriph.Target = _peripheralIds.FirstOrDefault();

        _allTiles = new List<(string, TileViewModel?)>
        {
            ("cpu", _tCpu), ("gpu", _tGpu), ("ram", _tRam), ("net", _tNet), ("space", _tStorage), ("disks", _tDisks),
            ("power", _tPower), ("periph", _tPeriph),
        }.Where(t => t.Item2 is not null).Select(t => (t.Item1, t.Item2!)).ToList();
        LayoutTiles();
    }

    private List<(string Key, TileViewModel Tile)> _allTiles = new();
    private TileViewModel? _tPower, _tPeriph;
    private List<string> _peripheralIds = new();

    /// <summary>Padrão: workstation CPU, GPU, Memória, Rede; NAS Espaço, Discos, Rede, CPU.</summary>
    private List<string> DefaultTileKeys() => IsNas ? ["space", "disks", "net", "cpu"] : ["cpu", "gpu", "ram", "net"];

    private List<string> TileKeys()
        => _settings.Current.OverviewTiles.TryGetValue(MachineId, out var keys) ? keys : DefaultTileKeys();

    /// <summary>Blocos escolhidos, na ordem escolhida; e a lista completa para o modo Personalizar.</summary>
    private void LayoutTiles()
    {
        var keys = TileKeys();
        Tiles.Clear();
        foreach (var key in keys)
            if (_allTiles.FirstOrDefault(t => t.Key == key).Tile is { } tile) Tiles.Add(tile);
        TileOptions.Clear();
        foreach (var (key, tile) in _allTiles.OrderBy(t => keys.IndexOf(t.Key) is var i and >= 0 ? i : 100))
            TileOptions.Add(new TileOption(key, tile.Title, tile.Icon, keys.Contains(key), this));
    }

    /// <summary>Opções do modo Personalizar da visão geral (marcar e mudar a ordem).</summary>
    public ObservableCollection<TileOption> TileOptions { get; } = new();

    internal void SaveTileLayout()
    {
        _settings.Current.OverviewTiles[MachineId] = TileOptions.Where(o => o.IsSelected).Select(o => o.Key).ToList();
        _settings.SaveSoon();
        LayoutTiles();
        UpdateTiles();
    }

    internal void MoveTile(TileOption option, int delta)
    {
        var i = TileOptions.IndexOf(option);
        var j = Math.Clamp(i + delta, 0, TileOptions.Count - 1);
        if (i < 0 || i == j) return;
        TileOptions.Move(i, j);
        SaveTileLayout();
    }

    public void ResetTiles()
    {
        _settings.Current.OverviewTiles.Remove(MachineId);
        _settings.SaveSoon();
        LayoutTiles();
        UpdateTiles();
    }

    /// <summary>
    /// GPU principal = a de mais memória dedicada. Não dá para confiar só no "integrada": a Radeon integrada do
    /// Ryzen 9950X declara 2 GB dedicados e aparece como placa comum.
    /// </summary>
    public static ComponentDescriptor? MainGpu(IEnumerable<ComponentDescriptor> comps)
        => comps.Where(c => c.Kind == ComponentKind.Gpu).OrderByDescending(c => IsIntegrated(c) ? -1 : DetailGb(c)).FirstOrDefault();

    public static bool IsIntegrated(ComponentDescriptor c)
        => L.Is(c.Detail, "integrada")
           || System.Text.RegularExpressions.Regex.IsMatch(c.Name, @"UHD|Iris|Radeon\(TM\) Graphics|Radeon Graphics|Vega \d+ Graphics", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static double DetailGb(ComponentDescriptor c)
        => double.TryParse(new string((c.Detail ?? "").TakeWhile(ch => char.IsDigit(ch) || ch == ',' || ch == '.').ToArray()).Replace(',', '.'),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : 0;

    private void UpdateTiles()
    {
        if (_descriptor is null) return;
        if (_tCpu is not null && _cpuId is not null)
        {
            _tCpu.Value = Text(_cpuId, "usage");
            _tCpu.Sub = $"{Text(_cpuId, "temp")} · {Text(_cpuId, "clock")}";
            _tCpu.Fraction = Value(_cpuId, "usage")?.Value / 100;
            _tCpu.Severity = Value(_cpuId, "temp")?.Value >= 90 ? Severity.Critical : Value(_cpuId, "temp")?.Value >= 80 ? Severity.Caution : Severity.Normal;
        }
        if (_tGpu is not null && _gpuId is not null)
        {
            _tGpu.Value = Text(_gpuId, "usage");
            _tGpu.Sub = $"{Text(_gpuId, "temp")} · {Component(_gpuId)?.Name}";
            _tGpu.Fraction = Value(_gpuId, "usage")?.Value / 100;
        }
        if (_tRam is not null)
        {
            _tRam.Value = Text("ram:0", "load");
            _tRam.Sub = L.T("{0} de {1}", Text("ram:0", "used"), Text("ram:0", "total"));
            _tRam.Fraction = Value("ram:0", "load")?.Value / 100;
        }
        if (_tNet is not null)
        {
            double Sum(string metric) => _physicalNets.Sum(id => Value(id, metric)?.Value ?? 0);
            _tNet.Value = "↓ " + Format.Number(Sum("rx"), "Mbit/s");
            _tNet.Sub = $"↑ {Format.Number(Sum("tx"), "Mbit/s")} · " + L.T("hoje {0}", Format.Number(Sum("rx_today") + Sum("tx_today"), "GB"));
        }
        if (_tStorage is not null)
        {
            var total = _letteredVolumes.Sum(id => Value(id, "total")?.Value ?? 0);
            var free = _letteredVolumes.Sum(id => Value(id, "free")?.Value ?? 0);
            _tStorage.Value = total > 0 ? L.T("{0} livres", Format.Number(free, "GB")) : "—";
            _tStorage.Sub = total > 0 ? L.T("de {0} em {1} volumes", Format.Number(total, "GB"), _letteredVolumes.Count) : null;
            _tStorage.Fraction = total > 0 ? (total - free) / total : null;
            _tStorage.Severity = _tStorage.Fraction >= 0.9 ? Severity.Critical : _tStorage.Fraction >= 0.8 ? Severity.Caution : Severity.Normal;
        }
        if (_tDisks is not null)
        {
            // Pelo código do Windows (0 saudável, 1 atenção, 2 com problema), só de leituras válidas: disco sem leitura
            // (falha, indisponível, "desconhecida") não conta como saudável, aparece à parte.
            int healthy = 0, bad = 0, unknown = 0;
            foreach (var id in _diskIds)
            {
                var v = Value(id, "health");
                if (v is { State: ReadingState.Ok or ReadingState.Stale, Value: { } code })
                {
                    if (code == 0) healthy++;
                    else if (code is 1 or 2) bad++;
                    else unknown++;
                }
                else unknown++;
            }
            _tDisks.Value = bad > 0 ? L.T("{0} com problema", bad)
                : healthy > 0 ? L.T("{0} saudáveis", healthy)
                : L.T("sem leitura");
            var temps = _diskIds.Select(id => Value(id, "temp")).Where(v => v?.Value is not null).ToList();
            var sub = temps.Count > 0 ? L.T("até {0}", Format.Number(temps.Max(v => v!.Value!.Value), "°C")) : L.T("temperatura aguardando uso dos discos");
            _tDisks.Sub = unknown > 0 && (healthy > 0 || bad > 0) ? L.T("{0} sem leitura", unknown) + " · " + sub : sub;
            _tDisks.Severity = bad > 0 ? Severity.Critical : healthy == 0 && unknown > 0 ? Severity.Caution : Severity.Normal;
        }
        if (_tPower is not null)
        {
            // Só o que é medido: CPU (pacote) + GPUs. Placa-mãe, discos e ventoinhas não têm sensor de consumo.
            var cpuW = _cpuId is null ? null : Value(_cpuId, "power")?.Value;
            var gpus = _descriptor.Components.Where(c => c.Kind == ComponentKind.Gpu).Select(c => Value(c.Id, "power")?.Value).Where(w => w is not null).Sum(w => w!.Value);
            var total = (cpuW ?? 0) + gpus;
            _tPower.Value = total > 0 ? Format.Number(total, "W") : "—";
            _tPower.Sub = $"CPU {(cpuW is { } c ? Format.Number(c, "W") : "—")} · GPU {Format.Number(gpus, "W")}";
        }
        if (_tPeriph is not null)
        {
            // A menor bateria em destaque (é a que pede atenção).
            var batteries = _peripheralIds.Select(id => (Id: id, V: Value(id, "battery"))).Where(x => x.V?.Value is not null && x.V.State == ReadingState.Ok).ToList();
            if (batteries.Count == 0)
            {
                _tPeriph.Value = "—";
                _tPeriph.Sub = L.T("sem leitura agora");
                _tPeriph.Fraction = null;
            }
            else
            {
                var low = batteries.MinBy(x => x.V!.Value!.Value);
                _tPeriph.Value = Format.Number(low.V!.Value!.Value, "%");
                _tPeriph.Sub = $"{Component(low.Id)?.Name} · " + L.T(batteries.Count == 1 ? "{0} aparelho" : "{0} aparelhos", batteries.Count);
                _tPeriph.Fraction = low.V.Value / 100;
                _tPeriph.Severity = BatterySeverity(low.V);
            }
        }
    }
}

/// <summary>Bloco da visão geral no modo Personalizar: mostrar ou não, subir ou descer.</summary>
public sealed class TileOption : ObservableObject
{
    private readonly MachineViewModel _machine;
    private bool _isSelected;

    public TileOption(string key, string title, string icon, bool selected, MachineViewModel machine)
    {
        Key = key;
        Title = title;
        Icon = icon;
        _isSelected = selected;
        _machine = machine;
        UpCommand = new RelayCommand(() => _machine.MoveTile(this, -1));
        DownCommand = new RelayCommand(() => _machine.MoveTile(this, +1));
    }

    public string Key { get; }
    public string Title { get; }
    public string Icon { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _machine.SaveTileLayout();
        }
    }
}
