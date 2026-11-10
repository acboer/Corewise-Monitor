using Microsoft.Data.Sqlite;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.Collector.History;

/// <summary>
/// Histórico em SQLite (%ProgramData%\HwMon\historico.db), em três resoluções com média, mínimo e máximo:
/// 10 s por 48 h, 1 min por 30 dias e 1 h por 2 anos. Valores guardados como inteiros (× 1000): ocupam metade
/// do espaço de um REAL e a precisão sobra para °C, %, MB/s, GB.
/// Uma conexão grava (thread do gravador); cada consulta abre a sua (WAL permite ler enquanto grava).
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public sealed record Tier(string Table, string Name, int Step, TimeSpan Keep);

    public static readonly Tier[] Tiers =
    [
        new("s10", "10s", 10, TimeSpan.FromDays(2)),
        new("m1", "1min", 60, TimeSpan.FromDays(30)),
        new("h1", "1h", 3600, TimeSpan.FromDays(730)),
    ];

    private const double Scale = 1000;
    private readonly string _connectionString;
    private readonly SqliteConnection _writer;
    private readonly Dictionary<(string Component, string Metric), (long Id, string Unit)> _series = new();
    private readonly object _seriesLock = new();

    public long Since { get; private set; }
    public string Path { get; }

    public HistoryStore(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        _writer = new SqliteConnection(_connectionString);
        _writer.Open();
        Exec(_writer, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec(_writer, "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        Exec(_writer, "CREATE TABLE IF NOT EXISTS series (id INTEGER PRIMARY KEY, component TEXT NOT NULL, metric TEXT NOT NULL, unit TEXT NOT NULL, UNIQUE(component, metric));");
        foreach (var tier in Tiers)
            Exec(_writer, $"CREATE TABLE IF NOT EXISTS {tier.Table} (series INTEGER NOT NULL, t INTEGER NOT NULL, avg INTEGER NOT NULL, min INTEGER NOT NULL, max INTEGER NOT NULL, n INTEGER NOT NULL, PRIMARY KEY(series, t)) WITHOUT ROWID;");

        using (var cmd = _writer.CreateCommand())
        {
            cmd.CommandText = "SELECT id, component, metric, unit FROM series";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _series[(r.GetString(1), r.GetString(2))] = (r.GetInt64(0), r.GetString(3));
        }
        using (var cmd = _writer.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'since'";
            if (cmd.ExecuteScalar() is string s && long.TryParse(s, out var since)) Since = since;
            else
            {
                Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('since', $v)";
                cmd.Parameters.AddWithValue("$v", Since.ToString(System.Globalization.CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Pack(double v) => (long)Math.Round(v * Scale);
    private static double Unpack(long v) => v / Scale;

    // ------------------------------------------------------------------ gravação (só a thread do gravador)

    public long SeriesId(string component, string metric, string unit)
    {
        lock (_seriesLock)
        {
            if (_series.TryGetValue((component, metric), out var s))
            {
                if (s.Unit != unit)
                {
                    using var upd = _writer.CreateCommand();
                    upd.CommandText = "UPDATE series SET unit = $u WHERE id = $id";
                    upd.Parameters.AddWithValue("$u", unit);
                    upd.Parameters.AddWithValue("$id", s.Id);
                    upd.ExecuteNonQuery();
                    _series[(component, metric)] = (s.Id, unit);
                }
                return s.Id;
            }
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = "INSERT INTO series (component, metric, unit) VALUES ($c, $m, $u) RETURNING id";
            cmd.Parameters.AddWithValue("$c", component);
            cmd.Parameters.AddWithValue("$m", metric);
            cmd.Parameters.AddWithValue("$u", unit);
            var id = (long)cmd.ExecuteScalar()!;
            _series[(component, metric)] = (id, unit);
            return id;
        }
    }

    public void Write(Tier tier, long bucket, IEnumerable<(long Series, double Avg, double Min, double Max, int N)> rows)
    {
        using var tx = _writer.BeginTransaction();
        using var cmd = _writer.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT OR REPLACE INTO {tier.Table} (series, t, avg, min, max, n) VALUES ($s, $t, $a, $mi, $ma, $n)";
        var ps = cmd.Parameters;
        var pS = ps.Add("$s", SqliteType.Integer);
        var pT = ps.Add("$t", SqliteType.Integer);
        var pA = ps.Add("$a", SqliteType.Integer);
        var pMi = ps.Add("$mi", SqliteType.Integer);
        var pMa = ps.Add("$ma", SqliteType.Integer);
        var pN = ps.Add("$n", SqliteType.Integer);
        cmd.Prepare();
        foreach (var r in rows)
        {
            pS.Value = r.Series;
            pT.Value = bucket;
            pA.Value = Pack(r.Avg);
            pMi.Value = Pack(r.Min);
            pMa.Value = Pack(r.Max);
            pN.Value = r.N;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Apaga o que passou do prazo de cada resolução.</summary>
    public int Cleanup(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var tier in Tiers)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = $"DELETE FROM {tier.Table} WHERE t < $limit";
            cmd.Parameters.AddWithValue("$limit", (now - tier.Keep).ToUnixTimeSeconds());
            removed += cmd.ExecuteNonQuery();
        }
        return removed;
    }

    /// <summary>Apaga todas as medições (as séries continuam) e recomeça o "gravando desde" agora.</summary>
    public void Clear()
    {
        foreach (var tier in Tiers) Exec(_writer, $"DELETE FROM {tier.Table};");
        Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using (var cmd = _writer.CreateCommand())
        {
            cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('since', $v)";
            cmd.Parameters.AddWithValue("$v", Since.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
        try { Exec(_writer, "VACUUM;"); }
        catch (SqliteException) { /* leitor aberto agora: o espaço volta na próxima vez */ }
    }

    // ------------------------------------------------------------------ consultas (qualquer thread)

    private SqliteConnection OpenReader()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    /// <summary>Resolução mais fina que ainda cobre o início do período (e não traz linhas demais).</summary>
    public static Tier PickTier(long from, long to, long now)
    {
        var span = to - from;
        if (from >= now - (long)Tiers[0].Keep.TotalSeconds && span <= 26 * 3600) return Tiers[0];
        if (from >= now - (long)Tiers[1].Keep.TotalSeconds && span <= 32L * 86400) return Tiers[1];
        return Tiers[2];
    }

    public HistoryResponse? Query(string component, string metric, long from, long to, int points)
    {
        (long Id, string Unit) series;
        lock (_seriesLock)
            if (!_series.TryGetValue((component, metric), out series)) return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tier = PickTier(from, to, now);
        points = Math.Clamp(points, 10, 2000);
        var step = Math.Max(tier.Step, (int)Math.Ceiling((to - from) / (double)points / tier.Step) * tier.Step);

        var response = new HistoryResponse
        {
            Component = component, Metric = metric, Unit = series.Unit, Resolution = tier.Name,
            StepSeconds = step, From = from, To = to, Since = Since,
        };

        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT t, avg, min, max, n FROM {tier.Table} WHERE series = $s AND t >= $from AND t < $to ORDER BY t";
        cmd.Parameters.AddWithValue("$s", series.Id);
        cmd.Parameters.AddWithValue("$from", from - from % tier.Step);
        cmd.Parameters.AddWithValue("$to", to);
        using var r = cmd.ExecuteReader();

        // Pontos em horários redondos no fuso local (ex.: um ponto por dia começa à meia-noite daqui).
        var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(from)).TotalSeconds;
        long Bucket(long t) => t - (((t + offset) % step) + step) % step;

        HistoryPoint? point = null;
        double pointSum = 0, totalSum = 0;
        long pointN = 0, totalN = 0, rows = 0;
        var stats = new HistoryStats { Min = double.MaxValue, Max = double.MinValue };
        void Close()
        {
            if (point is null) return;
            point.Avg = pointSum / Math.Max(1, pointN);
            response.Points.Add(point);
        }
        while (r.Read())
        {
            var t = r.GetInt64(0);
            var avg = Unpack(r.GetInt64(1));
            var min = Unpack(r.GetInt64(2));
            var max = Unpack(r.GetInt64(3));
            var n = r.GetInt64(4);
            var bucket = Bucket(t);
            if (point is null || point.T != bucket)
            {
                Close();
                point = new HistoryPoint { T = bucket, Min = min, Max = max };
                pointSum = 0;
                pointN = 0;
            }
            point.Min = Math.Min(point.Min, min);
            point.Max = Math.Max(point.Max, max);
            pointSum += avg * n;
            pointN += n;
            totalSum += avg * n;
            totalN += n;
            rows++;
            if (min < stats.Min) (stats.Min, stats.MinT) = (min, t);
            if (max > stats.Max) (stats.Max, stats.MaxT) = (max, t);
        }
        Close();
        if (rows > 0)
        {
            stats.Avg = totalSum / Math.Max(1, totalN);
            stats.Seconds = rows * tier.Step;
            response.Stats = stats;
        }
        return response;
    }

    /// <summary>Mínimo, média e máximo de todas as métricas no período (para os cartões).</summary>
    public HistoryStatsResponse Stats(long from, long to)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Até 3 h: 10 s. Mais que isso: 1 min (24 h = 1440 linhas por métrica), ou 1 h se passar de 30 dias.
        var tier = to - from <= 3 * 3600 ? Tiers[0] : from >= now - (long)Tiers[1].Keep.TotalSeconds ? Tiers[1] : Tiers[2];
        List<(string Component, string Metric, long Id, string Unit)> all;
        lock (_seriesLock) all = _series.Select(kv => (kv.Key.Component, kv.Key.Metric, kv.Value.Id, kv.Value.Unit)).ToList();

        var response = new HistoryStatsResponse { From = from, To = to, Since = Since };
        using var c = OpenReader();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT MIN(min), MAX(max), SUM(avg * n), SUM(n) FROM {tier.Table} WHERE series = $s AND t >= $from AND t < $to";
        var pS = cmd.Parameters.Add("$s", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);
        cmd.Prepare();
        foreach (var s in all)
        {
            pS.Value = s.Id;
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0) || r.GetInt64(3) == 0) continue;
            response.Series.Add(new SeriesStats
            {
                Component = s.Component, Metric = s.Metric, Unit = s.Unit,
                Min = Unpack(r.GetInt64(0)), Max = Unpack(r.GetInt64(1)),
                Avg = r.GetDouble(2) / r.GetInt64(3) / Scale,
            });
        }
        return response;
    }

    // ------------------------------------------------------------------ uso consolidado (dia a dia)

    /// <summary>
    /// Dia a dia no período: tempo ligado (amostras de 1 s do uso da CPU), média/pico de uso e temperatura de CPU e
    /// GPU, energia (CPU + GPUs), tempo com carga ≥ 80 %, tráfego de rede e volume lido/gravado em disco.
    /// Até 30 dias usa a resolução de 1 min; além disso, a de 1 h (a carga pesada fica aproximada por hora).
    /// </summary>
    public UsageResponse Usage(long from, long to, IReadOnlyList<ComponentDescriptor> components, Func<long, long, Func<long, long>, Dictionary<long, long>>? gameSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tier = from >= now - (long)Tiers[1].Keep.TotalSeconds ? Tiers[1] : Tiers[2];
        var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(to)).TotalSeconds;
        long DayOf(long t) => (t + offset) / 86400 * 86400 - offset;

        List<(string Component, string Metric, long Id, string Unit)> all;
        lock (_seriesLock) all = _series.Select(kv => (kv.Key.Component, kv.Key.Metric, kv.Value.Id, kv.Value.Unit)).ToList();
        long? Find(string? component, string metric) => component is null ? null : all.FirstOrDefault(s => s.Component == component && s.Metric == metric) is { Id: > 0 } s ? s.Id : null;
        IEnumerable<long> Prefix(string prefix, string metric) => all.Where(s => s.Component.StartsWith(prefix, StringComparison.Ordinal) && s.Metric == metric).Select(s => s.Id);

        using var c = OpenReader();
        var daily = new SortedDictionary<long, UsageDay>();
        UsageDay Day(long d) => daily.TryGetValue(d, out var u) ? u : daily[d] = new UsageDay { Date = d };

        // Uma passada por série: por dia (fuso local), amostras, soma, máximo e amostras com média ≥ 80.
        List<(long Day, long N, double Sum, double Max, long Heavy)> PerDay(long series)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT (t + $off) / 86400, SUM(n), SUM(avg * n), MAX(max), SUM(CASE WHEN avg >= 80000 THEN n ELSE 0 END) FROM {tier.Table} WHERE series = $s AND t >= $from AND t < $to GROUP BY 1";
            cmd.Parameters.AddWithValue("$off", offset);
            cmd.Parameters.AddWithValue("$s", series);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            using var r = cmd.ExecuteReader();
            var list = new List<(long, long, double, double, long)>();
            while (r.Read()) list.Add((r.GetInt64(0) * 86400 - offset, r.GetInt64(1), r.GetDouble(2) / Scale, Unpack(r.GetInt64(3)), r.GetInt64(4)));
            return list;
        }
        UsagePeak? Peak(long? series)
        {
            if (series is null) return null;
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT t, max FROM {tier.Table} WHERE series = $s AND t >= $from AND t < $to ORDER BY max DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$s", series.Value);
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new UsagePeak { T = r.GetInt64(0), Value = Unpack(r.GetInt64(1)) } : null;
        }

        var cpu = components.FirstOrDefault(x => x.Kind == ComponentKind.Cpu);
        var gpus = components.Where(x => x.Kind == ComponentKind.Gpu).ToList();
        // Placa de vídeo principal = a que mais consumiu no período (a dedicada, não a integrada).
        var gpuEnergy = gpus.Select(g => (g, Energy: Find(g.Id, "power") is { } id ? PerDay(id).Sum(x => x.Sum) : 0, Usage: Find(g.Id, "usage") is { } u ? PerDay(u).Sum(x => x.Sum) : 0)).ToList();
        var gpu = gpuEnergy.OrderByDescending(x => x.Energy).ThenByDescending(x => x.Usage).Select(x => x.g).FirstOrDefault();

        var response = new UsageResponse { From = from, To = to, Since = Since, Resolution = tier.Name, Cpu = cpu?.Name, Gpu = gpu?.Name };

        if ((Find(cpu?.Id, "usage") ?? Find("ram:0", "used")) is { } onSeries)
            foreach (var x in PerDay(onSeries))
            {
                var d = Day(x.Day);
                d.OnSeconds = x.N;
                if (Find(cpu?.Id, "usage") == onSeries)
                {
                    d.CpuUsageAvg = x.Sum / Math.Max(1, x.N);
                    d.CpuHeavySeconds = x.Heavy;
                }
            }
        if (Find(cpu?.Id, "temp") is { } cpuTemp)
            foreach (var x in PerDay(cpuTemp)) (Day(x.Day).CpuTempAvg, Day(x.Day).CpuTempMax) = (x.Sum / Math.Max(1, x.N), x.Max);
        if (Find(gpu?.Id, "usage") is { } gpuUsage)
            foreach (var x in PerDay(gpuUsage)) (Day(x.Day).GpuUsageAvg, Day(x.Day).GpuHeavySeconds) = (x.Sum / Math.Max(1, x.N), x.Heavy);
        if (Find(gpu?.Id, "temp") is { } gpuTemp)
            foreach (var x in PerDay(gpuTemp)) (Day(x.Day).GpuTempAvg, Day(x.Day).GpuTempMax) = (x.Sum / Math.Max(1, x.N), x.Max);

        // Energia: W × s → Wh.
        foreach (var id in new[] { cpu }.Concat(gpus).Where(x => x is not null).Select(x => Find(x!.Id, "power")).OfType<long>())
            foreach (var x in PerDay(id)) Day(x.Day).EnergyWh = (Day(x.Day).EnergyWh ?? 0) + x.Sum / 3600;
        // Rede em Mbit/s × s → MB; disco em MB/s × s → MB.
        foreach (var id in Prefix(ComponentKind.Network + ":", "rx")) foreach (var x in PerDay(id)) Day(x.Day).NetRxMB += x.Sum / 8;
        foreach (var id in Prefix(ComponentKind.Network + ":", "tx")) foreach (var x in PerDay(id)) Day(x.Day).NetTxMB += x.Sum / 8;
        foreach (var id in Prefix(ComponentKind.Disk + ":", "read")) foreach (var x in PerDay(id)) Day(x.Day).DiskReadMB += x.Sum;
        foreach (var id in Prefix(ComponentKind.Disk + ":", "write")) foreach (var x in PerDay(id)) Day(x.Day).DiskWriteMB += x.Sum;

        if (gameSeconds is not null)
            foreach (var (d, s) in gameSeconds(from, to, DayOf)) Day(d).GameSeconds = s;

        response.Days = daily.Values.ToList();
        response.CpuTempPeak = Peak(Find(cpu?.Id, "temp"));
        response.GpuTempPeak = Peak(Find(gpu?.Id, "temp"));
        return response;
    }

    public void Dispose()
    {
        _writer.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
