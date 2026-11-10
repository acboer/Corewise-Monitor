using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using HwMon.Core;
using HwMon.Core.Telemetry;

namespace HwMon.Client;

public sealed class CollectorException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;
}

/// <summary>Certificado diferente do fixado no pareamento: outra máquina ou coletor reinstalado. Não tenta de novo.</summary>
public sealed class CertificateMismatchException(string expected, string actual)
    : Exception(L.T("O certificado do serviço mudou ({0}, esperado {1}). Pareie de novo se o Corewise foi reinstalado nessa máquina.", Fingerprint.Short(actual), Fingerprint.Short(expected)))
{
    public string Actual { get; } = actual;
}

/// <summary>
/// Acesso HTTPS a um coletor. A validação do certificado ignora nome/autoridade e aceita só a impressão digital
/// fixada (ou, no pareamento, registra a que foi apresentada).
/// </summary>
public sealed class CollectorClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly string? _expectedFingerprint;

    private readonly IReadOnlyList<string> _knownAddresses;

    public string? PresentedFingerprint { get; private set; }
    /// <summary>IP pelo qual a última conexão abriu (para memorizar).</summary>
    public string? ConnectedAddress { get; private set; }

    public CollectorClient(string host, int port, string? expectedFingerprint, string? token, IEnumerable<string>? knownAddresses = null)
    {
        _expectedFingerprint = expectedFingerprint;
        _knownAddresses = knownAddresses?.ToList() ?? [];
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(6),
            ConnectCallback = ConnectAnyAsync,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    PresentedFingerprint = Fingerprint.Of(cert);
                    return _expectedFingerprint is null || PresentedFingerprint == _expectedFingerprint;
                },
            },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}:{port}/"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // o stream é longo; cada chamada curta usa seu próprio prazo
        };
        if (token is not null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Conecta pelo nome e pelos IPs já conhecidos ao mesmo tempo e usa a primeira conexão que abrir.
    /// - Um nome pode resolver para vários IPs: no NAS, "NAS" devolve o IP da LAN e os das redes virtuais do
    ///   Hyper-V/WSL, inalcançáveis de fora. IPv4 da mesma sub-rede vem primeiro.
    /// - Com VPN ativa, o DNS da VPN pode não conhecer o nome; os IPs conhecidos garantem a conexão.
    /// </summary>
    private async ValueTask<Stream> ConnectAnyAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var attempts = new List<Task<System.Net.Sockets.Socket>>();
        var tried = new HashSet<IPAddress>();
        void Start(IPAddress address, int delayMs)
        {
            if (tried.Add(address)) attempts.Add(Attempt(address, TimeSpan.FromMilliseconds(delayMs)));
        }

        foreach (var known in _knownAddresses)
            if (IPAddress.TryParse(known, out var ip)) Start(ip, 0);

        var dns = IPAddress.TryParse(host, out var literal) ? Task.FromResult<IPAddress[]>([literal]) : ResolveAsync(host, race.Token);
        var dnsDone = false;
        var errors = new List<Exception>();
        while (attempts.Count > 0 || !dnsDone)
        {
            var waiting = attempts.Cast<Task>().ToList();
            if (!dnsDone) waiting.Add(dns);
            var done = await Task.WhenAny(waiting);
            if (done == dns)
            {
                dnsDone = true;
                var ordered = dns.Result.Distinct()
                    .OrderBy(a => SameSubnet(a) ? 0 : a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 1 : 2).ToList();
                for (var i = 0; i < ordered.Count; i++) Start(ordered[i], i == 0 ? 0 : 300);
                continue;
            }
            var attempt = (Task<System.Net.Sockets.Socket>)done;
            attempts.Remove(attempt);
            try
            {
                var socket = await attempt;
                race.Cancel(); // cancela as outras tentativas
                foreach (var other in attempts)
                    _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                ConnectedAddress = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString();
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
            {
                errors.Add(ex);
            }
        }
        cancel.ThrowIfCancellationRequested();
        throw tried.Count == 0
            ? new HttpRequestException(L.T("\"{0}\" não foi encontrado na rede (desligado ou reiniciando?).", host))
            : new HttpRequestException(L.T("Não foi possível conectar a {0}:{1} ({2}).", host, port, string.Join(", ", tried)), errors.FirstOrDefault());

        async Task<System.Net.Sockets.Socket> Attempt(IPAddress address, TimeSpan delay)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, race.Token);
            var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, port, race.Token);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>Resolve o nome sem lançar erro: falha de DNS vira lista vazia (os IPs conhecidos ainda podem servir).</summary>
    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            return await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return [];
        }
    }
    private static bool SameSubnet(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var target = address.GetAddressBytes();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var local = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (Enumerable.Range(0, 4).All(i => (local[i] & mask[i]) == (target[i] & mask[i]))) return true;
            }
        }
        return false;
    }

    public static CollectorClient For(PairedMachine m) => new(m.Host, m.Port, m.Fingerprint, MachineStore.Token(m), m.KnownAddresses);

    /// <summary>O serviço deste próprio PC pela conexão local (127.0.0.1), mesmo que tenha sido pareado pelo nome.</summary>
    public static CollectorClient Local(PairedMachine m) => new("localhost", m.Port, m.Fingerprint, MachineStore.Token(m));

    public Task<AgentInfo> InfoAsync(CancellationToken cancel = default) => GetAsync<AgentInfo>("v1/info", cancel);
    public Task<MachineDescriptor> MachineAsync(CancellationToken cancel = default) => GetAsync<MachineDescriptor>("v1/machine", cancel);
    public Task<TelemetrySnapshot> SnapshotAsync(CancellationToken cancel = default) => GetAsync<TelemetrySnapshot>("v1/snapshot", cancel);

    /// <summary>Histórico de uma métrica (coletor 0.6+). from/to em segundos Unix.</summary>
    public Task<HistoryResponse> HistoryAsync(string component, string metric, long from, long to, int points = 600, CancellationToken cancel = default)
        => GetAsync<HistoryResponse>($"v1/history?component={Uri.EscapeDataString(component)}&metric={Uri.EscapeDataString(metric)}&from={from}&to={to}&points={points}", cancel);

    /// <summary>Mínimo, média e máximo de todas as métricas no período (coletor 0.6+).</summary>
    public Task<HistoryStatsResponse> HistoryStatsAsync(long from, long to, CancellationToken cancel = default)
        => GetAsync<HistoryStatsResponse>($"v1/history/stats?from={from}&to={to}", cancel);

    // ------------------------------------------------------------------ benchmarks (coletor 0.8+)

    public async Task<string> StartDiskBenchAsync(DiskBenchRequest request, CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(15));
        var response = await Send(() => _http.PostAsJsonAsync("v1/bench/disk", request, Json, timeout.Token));
        using var doc = await Read<JsonDocument>(response, timeout.Token);
        return doc.RootElement.GetProperty("id").GetString() ?? "";
    }

    public Task<DiskBenchStatus> DiskBenchStatusAsync(CancellationToken cancel = default) => GetAsync<DiskBenchStatus>("v1/bench/disk/status", cancel);
    public Task<List<DiskBenchResult>> DiskBenchHistoryAsync(CancellationToken cancel = default) => GetAsync<List<DiskBenchResult>>("v1/bench/disk/history", cancel);

    public async Task CancelDiskBenchAsync(CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(10));
        using var response = await Send(() => _http.PostAsync("v1/bench/disk/cancel", null, timeout.Token));
    }

    // ------------------------------------------------------------------ CPU, memória, GPU, estabilidade (coletor 0.11+)

    public async Task StartBenchAsync(BenchRequest request, CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(15));
        using var response = await Send(() => _http.PostAsJsonAsync("v1/bench/run", request, Json, timeout.Token));
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            throw new CollectorException(problem.TryGetProperty("detail", out var d) ? L.T(d.GetString() ?? "Já há um teste rodando.") : L.T("Já há um teste rodando."), response.StatusCode);
        }
        if (!response.IsSuccessStatusCode) throw new CollectorException(L.T("O serviço recusou o teste ({0}).", (int)response.StatusCode), response.StatusCode);
    }

    public Task<BenchStatus> BenchStatusAsync(string kind, CancellationToken cancel = default) => GetAsync<BenchStatus>($"v1/bench/run/status?kind={kind}", cancel);
    public Task<List<BenchResult>> BenchHistoryAsync(string? kind = null, CancellationToken cancel = default)
        => GetAsync<List<BenchResult>>(kind is null ? "v1/bench/run/history" : $"v1/bench/run/history?kind={kind}", cancel, TimeSpan.FromSeconds(20));

    public async Task CancelBenchAsync(CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(10));
        using var response = await Send(() => _http.PostAsync("v1/bench/run/cancel", null, timeout.Token));
    }

    // ------------------------------------------------------------------ jogos e uso consolidado (coletor 0.10+)

    public Task<GamesResponse> GamesAsync(long from, long to, CancellationToken cancel = default)
        => GetAsync<GamesResponse>($"v1/games?from={from}&to={to}", cancel);

    public Task<GameSession> GameSessionAsync(long id, CancellationToken cancel = default)
        => GetAsync<GameSession>($"v1/games/sessions/{id}", cancel);

    public async Task UpdateGameAsync(GameUpdate update, CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(10));
        using var response = await Send(() => _http.PostAsJsonAsync("v1/games/update", update, Json, timeout.Token));
        if (!response.IsSuccessStatusCode) throw new CollectorException(L.T("O serviço recusou a mudança ({0}).", (int)response.StatusCode), response.StatusCode);
    }

    public Task DeleteGameSessionAsync(long id, CancellationToken cancel = default) => DeleteAsync($"v1/games/sessions/{id}", cancel);

    /// <summary>Remove um resultado de benchmark: de disco ("disk") ou de carga (CPU, memória, GPU, estabilidade).</summary>
    public Task DeleteBenchResultAsync(bool disk, string id, CancellationToken cancel = default)
        => DeleteAsync(disk ? $"v1/bench/disk/history/{Uri.EscapeDataString(id)}" : $"v1/bench/run/history/{Uri.EscapeDataString(id)}", cancel);

    private async Task DeleteAsync(string path, CancellationToken cancel)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(10));
        using var response = await Send(() => _http.DeleteAsync(path, timeout.Token));
        if (!response.IsSuccessStatusCode)
            throw new CollectorException(L.T("O serviço recusou a exclusão ({0}).", (int)response.StatusCode), response.StatusCode);
    }

    /// <summary>Apaga o histórico da máquina (sensores e uso, jogos ou os dois). Pode levar alguns segundos.</summary>
    public async Task ClearHistoryAsync(bool sensors, bool games, CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromMinutes(3));
        using var response = await Send(() => _http.PostAsJsonAsync("v1/history/clear", new HistoryClearRequest { Sensors = sensors, Games = games }, timeout.Token));
        if (response.StatusCode == HttpStatusCode.NotFound) throw new CollectorException(L.T("Atualize o Corewise nesta máquina para limpar o histórico."), response.StatusCode);
        if (!response.IsSuccessStatusCode) throw new CollectorException(L.T("O serviço não conseguiu limpar o histórico ({0}).", (int)response.StatusCode), response.StatusCode);
    }

    public Task<UsageResponse> UsageAsync(long from, long to, CancellationToken cancel = default)
        => GetAsync<UsageResponse>($"v1/usage?from={from}&to={to}", cancel, TimeSpan.FromSeconds(30));

    /// <summary>Ida e volta de um pedido vazio (latência da aplicação, já com a conexão aberta).</summary>
    public async Task<TimeSpan> PingAsync(CancellationToken cancel = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(5));
        using var response = await Send(() => _http.GetAsync("v1/bench/ping", timeout.Token));
        await EnsureSuccess(response, timeout.Token);
        return clock.Elapsed;
    }

    /// <summary>Recebe dados do coletor por <paramref name="seconds"/> s; avisa cada bloco recebido.</summary>
    public async Task DownloadAsync(int seconds, Action<int> onBytes, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/bench/net/download?seconds={seconds}");
        using var response = await Send(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel));
        await EnsureSuccess(response, cancel);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[1 << 16];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancel)) > 0) onBytes(n);
    }

    /// <summary>Envia dados ao coletor por <paramref name="seconds"/> s (corpo em partes, sem tamanho fixo).</summary>
    public async Task UploadAsync(int seconds, Action<int> onBytes, CancellationToken cancel)
    {
        using var content = new TimedUploadContent(TimeSpan.FromSeconds(seconds), onBytes);
        using var response = await Send(() => _http.PostAsync("v1/bench/net/upload", content, cancel));
        await EnsureSuccess(response, cancel);
    }

    private sealed class TimedUploadContent(TimeSpan duration, Action<int> onBytes) : HttpContent
    {
        private static readonly byte[] Noise = CreateNoise();

        private static byte[] CreateNoise()
        {
            var b = new byte[1 << 16];
            Random.Shared.NextBytes(b);
            return b;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancel)
        {
            var until = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < until && !cancel.IsCancellationRequested)
            {
                await stream.WriteAsync(Noise, cancel);
                onBytes(Noise.Length);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>FPS de quem está desenhando (coletor 0.9+, como serviço).</summary>
    public Task<FramesResponse> FramesAsync(int? pid = null, CancellationToken cancel = default)
        => GetAsync<FramesResponse>(pid is null ? "v1/frames" : $"v1/frames?pid={pid}", cancel);

    /// <summary>Processos mais pesados (coletor 0.7.4+).</summary>
    public Task<ProcessesResponse> ProcessesAsync(CancellationToken cancel = default) => GetAsync<ProcessesResponse>("v1/processes", cancel);

    /// <summary>Resumo do sistema (coletor 0.5+; os anteriores respondem 404).</summary>
    public Task<SystemSummary> SystemAsync(bool refresh = false, CancellationToken cancel = default)
        => GetAsync<SystemSummary>(refresh ? "v1/system?refresh=true" : "v1/system", cancel);

    /// <summary>Pareamento por aceite (0.13+): pede acesso; o outro PC mostra "Permitir?".</summary>
    public async Task<PairAskResponse> AskPairAsync(PairRequest request, CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(15));
        var response = await Send(() => _http.PostAsJsonAsync("v1/pair/ask", request, Json, timeout.Token));
        return await Read<PairAskResponse>(response, timeout.Token);
    }

    public Task<PairAskStatus> PairStatusAsync(string id, CancellationToken cancel = default)
        => GetAsync<PairAskStatus>($"v1/pair/ask/{Uri.EscapeDataString(id)}", cancel);

    /// <summary>Pedidos de outros PCs esperando o "Permitir?" (só o painel deste PC, pela conexão local).</summary>
    public Task<List<PairPending>> PendingPairsAsync(CancellationToken cancel = default) => GetAsync<List<PairPending>>("v1/pair/pending", cancel);

    public async Task AnswerPairAsync(string id, bool approve, CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(10));
        using var response = await Send(() => _http.PostAsync($"v1/pair/pending/{Uri.EscapeDataString(id)}?approve={(approve ? "true" : "false")}", null, timeout.Token));
        await EnsureSuccess(response, timeout.Token);
    }

    /// <summary>Código de pareamento deste PC (pela conexão local, sem o atalho do menu Iniciar nem administrador).</summary>
    public async Task<PairCodeResponse> PairCodeAsync(CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(10));
        var response = await Send(() => _http.PostAsync("v1/pair/code", null, timeout.Token));
        return await Read<PairCodeResponse>(response, timeout.Token);
    }

    public async Task<PairResponse> PairAsync(PairRequest request, CancellationToken cancel = default)
    {
        using var timeout = Timeout(cancel, TimeSpan.FromSeconds(20));
        var response = await Send(() => _http.PostAsJsonAsync("v1/pair", request, Json, timeout.Token));
        return await Read<PairResponse>(response, timeout.Token);
    }

    /// <summary>
    /// Snapshots ao vivo (Server-Sent Events). Se nada chegar em <paramref name="silence"/> (o coletor manda um
    /// por segundo), a conexão é considerada perdida.
    /// </summary>
    public async IAsyncEnumerable<TelemetrySnapshot> StreamAsync(TimeSpan silence, [EnumeratorCancellation] CancellationToken cancel = default)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/stream");
        var response = await Send(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token));
        using (response)
        {
            await EnsureSuccess(response, watchdog.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token);
            using var reader = new StreamReader(stream);
            while (true)
            {
                watchdog.CancelAfter(silence);
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(watchdog.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new CollectorException(L.T("Sem dados do serviço há {0:0} s.", silence.TotalSeconds));
                }
                catch (IOException ex)
                {
                    throw new CollectorException(L.T("Conexão interrompida."), inner: ex);
                }
                if (line is null) throw new CollectorException(L.T("O serviço encerrou a conexão."));
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var snapshot = JsonSerializer.Deserialize<TelemetrySnapshot>(line.AsSpan(6), Json);
                if (snapshot is not null) yield return snapshot;
            }
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancel, TimeSpan? limit = null)
    {
        using var timeout = Timeout(cancel, limit ?? TimeSpan.FromSeconds(10));
        var response = await Send(() => _http.GetAsync(path, timeout.Token));
        return await Read<T>(response, timeout.Token);
    }

    private static CancellationTokenSource Timeout(CancellationToken cancel, TimeSpan after)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(after);
        return cts;
    }

    /// <summary>Traduz falhas de rede e de certificado em mensagens que fazem sentido para o usuário.</summary>
    private async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException) when (_expectedFingerprint is not null && PresentedFingerprint is { } fp && fp != _expectedFingerprint)
        {
            throw new CertificateMismatchException(_expectedFingerprint, fp);
        }
        catch (HttpRequestException ex)
        {
            throw new CollectorException(ex.InnerException?.Message ?? ex.Message, ex.StatusCode, ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new CollectorException(L.T("O serviço não respondeu a tempo."), inner: ex);
        }
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancel)
    {
        using (response)
        {
            await EnsureSuccess(response, cancel);
            return (await response.Content.ReadFromJsonAsync<T>(Json, cancel))!;
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
            // corpo não é JSON
        }
        // A mensagem vem no idioma do serviço daquela máquina: traduz se for uma das conhecidas.
        throw new CollectorException(detail is null ? $"HTTP {(int)response.StatusCode}" : L.T(detail), response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
