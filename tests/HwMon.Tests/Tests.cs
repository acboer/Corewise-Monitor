using System.Text.Json;
using HwMon.Client;
using HwMon.Collector;
using HwMon.Core;
using HwMon.Core.Telemetry;
using Xunit;

namespace HwMon.Tests;

/// <summary>Pasta temporária própria de cada teste (nada fora dela é tocado).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "corewise-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

// A07: jitter na ordem das medidas; mediana numa cópia ordenada.
public class LatencyTests
{
    [Fact]
    public void Jitter_uses_the_order_of_the_measurements()
    {
        var rtts = new List<double> { 1, 100, 1, 100 };
        var (median, jitter) = NetBenchmark.Latency(rtts);
        Assert.Equal(99, jitter, 6);
        Assert.Equal(50.5, median, 6);
        Assert.Equal(new double[] { 1, 100, 1, 100 }, rtts); // original intacto
    }

    [Fact]
    public void Constant_and_rising_sequences()
    {
        Assert.Equal(0, NetBenchmark.Latency([5, 5, 5, 5]).Jitter);
        Assert.Equal(1, NetBenchmark.Latency([1, 2, 3, 4, 5]).Jitter, 6);
        Assert.Equal(3, NetBenchmark.Latency([1, 2, 3, 4, 5]).Median, 6);
    }

    [Fact]
    public void Too_few_samples()
    {
        Assert.Equal((0d, 0d), NetBenchmark.Latency([]));
        Assert.Equal((7d, 0d), NetBenchmark.Latency([7]));
    }
}

// A05: gravação atômica com backup e recuperação.
public class SafeFileTests
{
    [Fact]
    public void Overwrite_keeps_previous_version_as_backup()
    {
        using var dir = new TempDir();
        var path = dir.File("estado.json");
        SafeFile.WriteAllText(path, "[1]");
        SafeFile.WriteAllText(path, "[1,2]");
        Assert.Equal("[1,2]", System.IO.File.ReadAllText(path));
        Assert.Equal("[1]", System.IO.File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_backup_and_is_kept()
    {
        using var dir = new TempDir();
        var path = dir.File("estado.json");
        SafeFile.WriteAllText(path, "[1]");
        SafeFile.WriteAllText(path, "[1,2]");
        System.IO.File.WriteAllText(path, "[1,2"); // gravação interrompida
        Assert.Equal([1], SafeFile.ReadJson<List<int>>(path));
        Assert.Equal("[1,2", System.IO.File.ReadAllText(path + ".corrompido"));
    }

    [Fact]
    public void Missing_file_is_null()
    {
        using var dir = new TempDir();
        Assert.Null(SafeFile.ReadJson<List<int>>(dir.File("nada.json")));
    }
}

// A09: pareamento por aceite.
public class PairRequestTests
{
    [Fact]
    public void Parallel_approvals_issue_a_single_token()
    {
        using var dir = new TempDir();
        var clients = new ClientRegistry(dir.File("clientes.json"));
        var requests = new PairRequests(clients);
        var id = requests.Ask("painel-1", "PC", "192.168.0.2")!;
        var answers = new bool[16];
        Parallel.For(0, answers.Length, i => answers[i] = requests.Answer(id, approve: true));
        Assert.Equal(1, answers.Count(a => a));
        var status = requests.Status(id);
        Assert.Equal("approved", status.State);
        Assert.NotNull(clients.Validate(status.Token!));
        Assert.Single(clients.List());
    }

    [Fact]
    public void Approved_token_can_be_read_again_after_a_lost_response()
    {
        using var dir = new TempDir();
        var requests = new PairRequests(new ClientRegistry(dir.File("clientes.json")));
        var id = requests.Ask("painel-1", "PC", null)!;
        requests.Answer(id, approve: true);
        var first = requests.Status(id);
        var again = requests.Status(id);
        Assert.NotNull(first.Token);
        Assert.Equal(first.Token, again.Token);
    }

    [Fact]
    public void Open_requests_are_limited()
    {
        using var dir = new TempDir();
        var requests = new PairRequests(new ClientRegistry(dir.File("clientes.json")));
        var ids = new string?[20];
        Parallel.For(0, ids.Length, i => ids[i] = requests.Ask($"painel-{i}", "PC", null));
        Assert.Equal(5, ids.Count(i => i is not null));
    }

    [Fact]
    public void Denied_request_issues_no_token()
    {
        using var dir = new TempDir();
        var clients = new ClientRegistry(dir.File("clientes.json"));
        var requests = new PairRequests(clients);
        var id = requests.Ask("painel-1", "PC", null)!;
        Assert.True(requests.Answer(id, approve: false));
        Assert.False(requests.Answer(id, approve: true));
        Assert.Equal("denied", requests.Status(id).State);
        Assert.Empty(clients.List());
    }
}

// Token continua válido se o arquivo de clientes sumir ou estiver ilegível por um instante.
public class ClientRegistryTests
{
    [Fact]
    public void Token_survives_a_missing_or_corrupt_file()
    {
        using var dir = new TempDir();
        var path = dir.File("clientes.json");
        var clients = new ClientRegistry(path);
        var token = clients.Add("painel-1", "PC");
        clients.Add("painel-2", "Outro PC"); // o .bak passa a ter o painel-1
        Assert.NotNull(clients.Validate(token));

        System.IO.File.Move(path, path + ".fora");
        Assert.NotNull(clients.Validate(token));

        System.IO.File.Move(path + ".fora", path);
        System.IO.File.WriteAllText(path, "[{\"Id\":"); // gravação interrompida de outro processo
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.NotNull(clients.Validate(token)); // cai no .bak
    }

    [Fact]
    public void Revoked_client_stays_revoked()
    {
        using var dir = new TempDir();
        var path = dir.File("clientes.json");
        var service = new ClientRegistry(path);
        var token = service.Add("painel-1", "PC");
        Assert.NotNull(service.Validate(token));
        // O comando "revogar" roda em outro processo, com a sua própria cópia.
        Assert.Equal(1, new ClientRegistry(path).Revoke("painel-1"));
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Null(service.Validate(token));
    }
}

// A14: totais do dia.
public class NetDailyTotalsTests
{
    [Fact]
    public void Counts_increments_and_handles_counter_resets_separately()
    {
        var totals = new NetDailyTotals(null);
        totals.Update("eth", 1000, 1000); // base
        var (rx, tx) = totals.Update("eth", 1500, 1200);
        Assert.Equal(500, rx);
        Assert.Equal(200, tx);
        // Recebido voltou (contador zerou), enviado continuou: o enviado ainda conta.
        (rx, tx) = totals.Update("eth", 100, 1300);
        Assert.Equal(500, rx);
        Assert.Equal(300, tx);
    }

    [Fact]
    public void State_file_from_older_version_starts_a_new_base()
    {
        using var dir = new TempDir();
        var path = dir.File("rede.json");
        // Arquivo antigo, sem a hora da última leitura: a primeira leitura não soma nada.
        var boot = DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64);
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Adapters = new Dictionary<string, object>
            {
                ["eth"] = new { Date = today, RxToday = 10.0, TxToday = 10.0, LastRx = 0.0, LastTx = 0.0, Boot = boot },
            },
        }));
        var totals = new NetDailyTotals(path);
        Assert.Equal((10d, 10d), totals.Update("eth", 5_000_000, 5_000_000));
    }
}

// Tradução: trecho de frase com espaço na ponta (o aviso de conexão perdida aparecia em português).
public class TranslationTests
{
    [Fact]
    public void Leading_space_fragment_is_translated()
    {
        L.Use("en");
        try
        {
            Assert.Equal(" — showing the last data received. The connection will come back on its own.",
                L.T(" — mostrando os últimos dados recebidos. A conexão volta sozinha."));
        }
        finally
        {
            L.Use("pt");
        }
    }
}

// Pareamento perdido no HTPC: aprovação que o painel não guardou não pode derrubar o acesso que funciona.
public class RepairTests
{
    [Fact]
    public void Unsaved_new_pairing_keeps_the_working_token()
    {
        using var dir = new TempDir();
        var clients = new ClientRegistry(dir.File("clientes.json"));
        var working = clients.Add("painel-1", "WORKSTATION");
        var lost = clients.Add("painel-1", "WORKSTATION"); // aprovado depois de a janela fechar
        Assert.NotNull(clients.Validate(working));
        // Também depois de o serviço reiniciar (lido do arquivo).
        Assert.NotNull(new ClientRegistry(dir.File("clientes.json")).Validate(working));
        Assert.NotNull(clients.Validate(lost));
    }

    [Fact]
    public void Old_token_stops_working_once_the_new_one_is_used()
    {
        using var dir = new TempDir();
        var clients = new ClientRegistry(dir.File("clientes.json"));
        var old = clients.Add("painel-1", "PC");
        var fresh = clients.Add("painel-1", "PC");
        Assert.NotNull(clients.Validate(fresh));
        Assert.Null(clients.Validate(old));
    }

    [Fact]
    public void Revoke_removes_both_tokens()
    {
        using var dir = new TempDir();
        var clients = new ClientRegistry(dir.File("clientes.json"));
        var old = clients.Add("painel-1", "PC");
        var fresh = clients.Add("painel-1", "PC");
        Assert.Equal(1, clients.Revoke("painel-1"));
        Assert.Null(clients.Validate(old));
        Assert.Null(clients.Validate(fresh));
    }

    [Fact]
    public void Cancelled_request_cannot_be_approved()
    {
        using var dir = new TempDir();
        var clients = new ClientRegistry(dir.File("clientes.json"));
        var working = clients.Add("painel-1", "PC");
        var requests = new PairRequests(clients);
        var id = requests.Ask("painel-1", "PC", null)!;
        Assert.True(requests.Cancel(id)); // a janela do painel fechou
        Assert.False(requests.Answer(id, approve: true)); // "Permitir" atrasado no outro PC
        Assert.NotNull(clients.Validate(working));
    }
}
