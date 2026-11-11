using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.Collector.History;

/// <summary>
/// Junta os snapshots de 1 s em blocos de 10 s, 1 min e 1 h (média, mínimo, máximo) e grava cada bloco quando ele
/// fecha. Só leituras válidas entram: leitura antiga (HDD parado, Stale) não vira linha reta falsa no gráfico.
/// Blocos abertos quando o serviço para são perdidos (no máximo 1 h na resolução de 1 h). Bloco que não pôde ser
/// gravado (disco cheio, banco travado) fica numa fila e é gravado na próxima vez, com o horário dele.
/// </summary>
public sealed class HistoryRecorder(HistoryStore store)
{
    /// <summary>Capacidades e contadores desde o boot: não mudam ou não fazem sentido como gráfico.</summary>
    private static readonly HashSet<string> Skip = ["total", "vram_total", "rx_total", "tx_total", "health", "charge_eta", "runtime"];

    private sealed class Acc
    {
        public double Sum, Min = double.MaxValue, Max = double.MinValue;
        public int N;
        public void Add(double v) { Sum += v; N++; if (v < Min) Min = v; if (v > Max) Max = v; }
        public void Reset() { Sum = 0; N = 0; Min = double.MaxValue; Max = double.MinValue; }
    }

    private readonly Dictionary<long, Acc[]> _acc = new();
    private readonly long[] _bucket = new long[HistoryStore.Tiers.Length];

    public void Add(TelemetrySnapshot snapshot)
    {
        var t = snapshot.Time.ToUnixTimeSeconds();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        for (var i = 0; i < HistoryStore.Tiers.Length; i++)
        {
            var step = HistoryStore.Tiers[i].Step;
            var bucket = t - t % step;
            if (_bucket[i] != 0 && bucket != _bucket[i])
            {
                // Falha ao gravar não pode deixar este snapshot pela metade: termina e só então avisa.
                try { Flush(i); }
                catch (Exception ex) { error ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            }
            _bucket[i] = bucket;
        }
        foreach (var v in snapshot.Values)
        {
            if (v.Value is not { } x || v.State != ReadingState.Ok || Skip.Contains(v.Metric) || double.IsNaN(x) || double.IsInfinity(x)) continue;
            var id = store.SeriesId(v.Component, v.Metric, v.Unit);
            if (!_acc.TryGetValue(id, out var accs))
                _acc[id] = accs = HistoryStore.Tiers.Select(_ => new Acc()).ToArray();
            foreach (var a in accs) a.Add(x);
        }
        error?.Throw();
    }

    private void Flush(int tier)
    {
        var rows = new List<(long, double, double, double, int)>();
        foreach (var (id, accs) in _acc)
        {
            var a = accs[tier];
            if (a.N == 0) continue;
            rows.Add((id, a.Sum / a.N, a.Min, a.Max, a.N));
            a.Reset();
        }
        if (rows.Count > 0) _pending.Enqueue((tier, _bucket[tier], rows));
        // Gravação que falhou (disco cheio, banco travado) fica na fila e é tentada de novo no próximo bloco, com o
        // horário original. INSERT OR REPLACE: repetir o mesmo bloco não duplica nada.
        while (_pending.Count > MaxPending)
        {
            _pending.Dequeue();
            Dropped++;
        }
        while (_pending.TryPeek(out var block))
        {
            store.Write(HistoryStore.Tiers[block.Tier], block.Bucket, block.Rows);
            _pending.Dequeue();
        }
    }

    /// <summary>Blocos esperando gravação: ~1 h de blocos de 10 s, mais os de 1 min e 1 h do mesmo período.</summary>
    private const int MaxPending = 500;
    private readonly Queue<(int Tier, long Bucket, List<(long, double, double, double, int)> Rows)> _pending = new();

    /// <summary>Blocos descartados porque o banco ficou indisponível por tempo demais.</summary>
    public int Dropped { get; private set; }
}
