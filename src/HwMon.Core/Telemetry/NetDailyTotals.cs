using System.Text.Json;

namespace HwMon.Core.Telemetry;

/// <summary>
/// GB enviados e recebidos no dia, por adaptador. Os contadores do Windows são acumulados desde que o adaptador
/// subiu e zeram no reinício; aqui só somamos diferenças positivas, então reinícios e quedas do adaptador não
/// geram picos. O estado é gravado em disco para sobreviver ao reinício do serviço.
/// </summary>
public sealed class NetDailyTotals
{
    private readonly string? _path;
    private readonly object _lock = new();
    private State _state = new();

    public NetDailyTotals(string? path)
    {
        _path = path;
        try
        {
            if (path is not null && File.Exists(path))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
        }
        catch (Exception)
        {
            _state = new State(); // arquivo corrompido: recomeça o dia
        }
    }

    /// <summary>Registra os totais acumulados atuais (bytes) e devolve os totais do dia (bytes).</summary>
    public (double RxToday, double TxToday) Update(string adapterId, double rxBytes, double txBytes)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
            var boot = BootId();
            if (!_state.Adapters.TryGetValue(adapterId, out var e))
                _state.Adapters[adapterId] = e = new Entry { Date = today };

            if (e.Date != today)
            {
                e.Date = today;
                e.RxToday = 0;
                e.TxToday = 0;
            }

            // Soma a diferença só quando ela é deste dia: mesma inicialização do Windows e a leitura anterior é de
            // poucos minutos atrás. Com o serviço parado por horas (ou desde ontem), não dá para saber em que dia o
            // tráfego aconteceu: fica fora e a leitura atual vira a nova base. Recebido e enviado são conferidos
            // separadamente (um contador que voltou não descarta o outro).
            var now = DateTime.UtcNow;
            var continuous = Math.Abs((e.Boot - boot).TotalMinutes) < 2 && e.LastTime is { } last && now - last is { } gap
                             && gap >= TimeSpan.Zero && gap <= MaxGap;
            if (continuous && e.LastRx is { } lr && rxBytes >= lr) e.RxToday += rxBytes - lr;
            if (continuous && e.LastTx is { } lt && txBytes >= lt) e.TxToday += txBytes - lt;
            e.LastRx = rxBytes;
            e.LastTx = txBytes;
            e.LastTime = now;
            e.Boot = boot;
            return (e.RxToday, e.TxToday);
        }
    }

    /// <summary>Maior intervalo entre duas leituras que ainda conta (atualização do serviço, pausa curta).</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(5);

    public void Save()
    {
        if (_path is null) return;
        string json;
        lock (_lock) json = JsonSerializer.Serialize(_state);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Horário do boot: identifica a sessão do Windows (comparado com folga de 2 min).</summary>
    private static DateTime BootId() => DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64);

    private sealed class State
    {
        public Dictionary<string, Entry> Adapters { get; set; } = new();
    }

    private sealed class Entry
    {
        public string Date { get; set; } = "";
        public double RxToday { get; set; }
        public double TxToday { get; set; }
        public double? LastRx { get; set; }
        public double? LastTx { get; set; }
        public DateTime Boot { get; set; }
        /// <summary>Hora (UTC) da última leitura. Nulo em arquivos antigos: a primeira leitura só define a base.</summary>
        public DateTime? LastTime { get; set; }
    }
}
