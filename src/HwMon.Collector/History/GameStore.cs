using System.Text.Json;
using Microsoft.Data.Sqlite;
using HwMon.Core.Telemetry;

namespace HwMon.Collector.History;

/// <summary>
/// Sessões de jogo em %ProgramData%\HwMon\jogos.db. Cada sessão é uma linha com o resumo (JSON) e os pontos de
/// 5 s (JSON à parte, só lido no detalhe). Os jogos (nome escolhido, ignorado) ficam numa tabela própria.
/// Pouca coisa e pouca escrita (uma por minuto por jogo): uma conexão só, protegida por lock.
/// </summary>
public sealed class GameStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public GameStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("CREATE TABLE IF NOT EXISTS games (exe TEXT PRIMARY KEY, name TEXT NOT NULL, custom_name TEXT, path TEXT, ignored INTEGER NOT NULL DEFAULT 0);");
        Exec("CREATE TABLE IF NOT EXISTS sessions (id INTEGER PRIMARY KEY, exe TEXT NOT NULL, start INTEGER NOT NULL, finish INTEGER NOT NULL, data TEXT NOT NULL, points TEXT);");
        Exec("CREATE INDEX IF NOT EXISTS sessions_start ON sessions(start);");
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    // ------------------------------------------------------------------ jogos

    public sealed record GameInfo(string Exe, string Name, string? CustomName, string? Path, bool Ignored)
    {
        public string Display => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName!;
    }

    /// <summary>Registra o jogo (ou atualiza o nome detectado e o caminho) e devolve como está gravado.</summary>
    public GameInfo Touch(string exe, string detectedName, string? path)
    {
        lock (_lock)
        {
            using (var cmd = Command("INSERT INTO games (exe, name, path) VALUES ($e, $n, $p) ON CONFLICT(exe) DO UPDATE SET name = $n, path = COALESCE($p, path)",
                       ("$e", exe), ("$n", detectedName), ("$p", path)))
                cmd.ExecuteNonQuery();
            return Game(exe)!;
        }
    }

    public GameInfo? Game(string exe)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT exe, name, custom_name, path, ignored FROM games WHERE exe = $e", ("$e", exe));
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    private static GameInfo Read(SqliteDataReader r)
        => new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) != 0);

    public List<GameInfo> Games()
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT exe, name, custom_name, path, ignored FROM games");
            using var r = cmd.ExecuteReader();
            var list = new List<GameInfo>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    public bool Update(GameUpdate update)
    {
        lock (_lock)
        {
            var changed = 0;
            if (update.Name is not null)
            {
                using var cmd = Command("UPDATE games SET custom_name = $n WHERE exe = $e",
                    ("$n", string.IsNullOrWhiteSpace(update.Name) ? null : update.Name.Trim()), ("$e", update.Exe));
                changed += cmd.ExecuteNonQuery();
            }
            if (update.Ignored is { } ignored)
            {
                using var cmd = Command("UPDATE games SET ignored = $i WHERE exe = $e", ("$i", ignored ? 1 : 0), ("$e", update.Exe));
                changed += cmd.ExecuteNonQuery();
            }
            return changed > 0;
        }
    }

    // ------------------------------------------------------------------ sessões

    /// <summary>Grava (nova: Id = 0) ou atualiza a sessão, com os pontos.</summary>
    public void Save(GameSession session)
    {
        var points = session.Points;
        session.Points = null;
        try
        {
            var data = JsonSerializer.Serialize(session, Json);
            var pts = points is null ? null : JsonSerializer.Serialize(points, Json);
            lock (_lock)
            {
                if (session.Id == 0)
                {
                    using var cmd = Command("INSERT INTO sessions (exe, start, finish, data, points) VALUES ($e, $s, $f, $d, $p) RETURNING id",
                        ("$e", session.Exe), ("$s", session.Start), ("$f", session.End), ("$d", data), ("$p", pts));
                    session.Id = (long)cmd.ExecuteScalar()!;
                    // o id entra no JSON também
                    using var upd = Command("UPDATE sessions SET data = $d WHERE id = $id", ("$d", JsonSerializer.Serialize(session, Json)), ("$id", session.Id));
                    upd.ExecuteNonQuery();
                }
                else
                {
                    using var cmd = Command("UPDATE sessions SET finish = $f, data = $d, points = $p WHERE id = $id",
                        ("$f", session.End), ("$d", data), ("$p", pts), ("$id", session.Id));
                    cmd.ExecuteNonQuery();
                }
            }
        }
        finally
        {
            session.Points = points;
        }
    }

    /// <summary>Sessões que começaram no período (sem os pontos), mais recentes primeiro.</summary>
    public List<GameSession> Sessions(long from, long to)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT data FROM sessions WHERE finish >= $from AND start < $to ORDER BY start DESC", ("$from", from), ("$to", to));
            using var r = cmd.ExecuteReader();
            var list = new List<GameSession>();
            while (r.Read())
                if (JsonSerializer.Deserialize<GameSession>(r.GetString(0), Json) is { } s) list.Add(s);
            return list;
        }
    }

    public GameSession? Session(long id)
    {
        lock (_lock)
        {
            using var cmd = Command("SELECT data, points FROM sessions WHERE id = $id", ("$id", id));
            using var r = cmd.ExecuteReader();
            if (!r.Read() || JsonSerializer.Deserialize<GameSession>(r.GetString(0), Json) is not { } s) return null;
            s.Points = r.IsDBNull(1) ? new() : JsonSerializer.Deserialize<List<GamePoint>>(r.GetString(1), Json) ?? new();
            return s;
        }
    }

    public bool Delete(long id)
    {
        lock (_lock)
        {
            using var cmd = Command("DELETE FROM sessions WHERE id = $id", ("$id", id));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Uma vez só (0.10.1): a 0.10.0 gravava como jogo qualquer programa que redesenhava a tela (Dropbox, VPN…),
    /// sem olhar o uso de GPU, que não ficou gravado. Apaga as sessões de programas fora das bibliotecas de jogos.
    /// </summary>
    public int CleanupBeforeGpuFilter(Func<string?, bool> inLibrary)
    {
        lock (_lock)
        {
            Exec("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
            using (var check = Command("SELECT value FROM meta WHERE key = 'filtro-gpu'"))
                if (check.ExecuteScalar() is not null) return 0;
            var removed = 0;
            foreach (var g in Games().Where(g => !inLibrary(g.Path)))
            {
                using var del = Command("DELETE FROM sessions WHERE exe = $e", ("$e", g.Exe));
                removed += del.ExecuteNonQuery();
                using var forget = Command("DELETE FROM games WHERE exe = $e AND ignored = 0", ("$e", g.Exe));
                forget.ExecuteNonQuery();
            }
            using var mark = Command("INSERT INTO meta (key, value) VALUES ('filtro-gpu', $v)", ("$v", DateTimeOffset.UtcNow.ToString("O")));
            mark.ExecuteNonQuery();
            return removed;
        }
    }

    /// <summary>
    /// Uma vez só (0.13): até a 0.12 o tempo jogado contava jogo esquecido aberto em segundo plano, e launchers de
    /// anticheat viravam sessão. O tempo passa a ser o coberto por quadros (quadros × frametime médio) e as sessões
    /// dos launchers somem.
    /// </summary>
    public int FixActiveTime(Func<string, bool> notGame)
    {
        lock (_lock)
        {
            Exec("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
            using (var check = Command("SELECT value FROM meta WHERE key = 'tempo-ativo'"))
                if (check.ExecuteScalar() is not null) return 0;
            var rows = new List<(long Id, string Exe, string Data)>();
            using (var cmd = Command("SELECT id, exe, data FROM sessions"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
            var changed = 0;
            foreach (var (id, exe, data) in rows)
            {
                if (notGame(Path.GetFileNameWithoutExtension(exe)))
                {
                    using var del = Command("DELETE FROM sessions WHERE id = $id", ("$id", id));
                    changed += del.ExecuteNonQuery();
                    continue;
                }
                if (JsonSerializer.Deserialize<GameSession>(data, Json) is not { Frames: > 0, FrametimeAvgMs: > 0 } s) continue;
                var played = (long)Math.Round(s.Frames * s.FrametimeAvgMs / 1000);
                if (played >= s.ActiveSeconds) continue;
                s.ActiveSeconds = played;
                using var upd = Command("UPDATE sessions SET data = $d WHERE id = $id", ("$d", JsonSerializer.Serialize(s, Json)), ("$id", id));
                changed += upd.ExecuteNonQuery();
            }
            using var mark = Command("INSERT INTO meta (key, value) VALUES ('tempo-ativo', $v)", ("$v", DateTimeOffset.UtcNow.ToString("O")));
            mark.ExecuteNonQuery();
            return changed;
        }
    }

    /// <summary>Apaga todas as sessões (os nomes escolhidos e os programas ignorados continuam).</summary>
    public int Clear()
    {
        lock (_lock)
        {
            using var cmd = Command("DELETE FROM sessions");
            var removed = cmd.ExecuteNonQuery();
            Exec("VACUUM;");
            return removed;
        }
    }

    /// <summary>Sessões que ficaram abertas (serviço parou no meio do jogo): fecham onde a última gravação parou.</summary>
    public int CloseOrphans()
    {
        var fixedCount = 0;
        lock (_lock)
        {
            var open = new List<(long Id, string Data)>();
            using (var cmd = Command("SELECT id, data FROM sessions WHERE data LIKE '%\"open\":true%'"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) open.Add((r.GetInt64(0), r.GetString(1)));
            foreach (var (id, data) in open)
            {
                if (JsonSerializer.Deserialize<GameSession>(data, Json) is not { } s) continue;
                s.Open = false;
                using var upd = Command("UPDATE sessions SET data = $d WHERE id = $id", ("$d", JsonSerializer.Serialize(s, Json)), ("$id", id));
                fixedCount += upd.ExecuteNonQuery();
            }
        }
        return fixedCount;
    }

    /// <summary>Segundos jogados por dia (meia-noite local → segundos), para o uso consolidado.</summary>
    public Dictionary<long, long> SecondsByDay(long from, long to, Func<long, long> dayOf)
    {
        var result = new Dictionary<long, long>();
        foreach (var s in Sessions(from, to))
        {
            var day = dayOf(s.Start);
            result[day] = result.GetValueOrDefault(day) + s.ActiveSeconds;
        }
        return result;
    }

    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
