using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using HwMon.Collector;
using HwMon.Core.Telemetry;

Console.OutputEncoding = System.Text.Encoding.UTF8;
L.Use(null); // comandos no console (ex.: código de pareamento): idioma do Windows

var dataDir = Option(args, "--dados");
var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != dataDir);
try
{
    return command switch
    {
        null => await RunAsync(args, dataDir),
        "parear" => Pair(dataDir, args.Contains("--pausar")),
        "clientes" => ListClients(dataDir),
        "revogar" => Revoke(dataDir, args.SkipWhile(a => a != "revogar").Skip(1).FirstOrDefault()),
        "instalar" => Installer.Install(DataStore.Open(dataDir).LoadConfig().Port),
        "desinstalar" => Installer.Uninstall(),
        "status" => Status(dataDir),
        "sistema" => PrintSummary(),
        "perifericos" => PrintPeripherals(),
        "benchmark" => await RunDiskBenchmark(args),
        "quadros" => await PrintFrames(),
        "teste" => await RunLoadBench(args.SkipWhile(a => a != "teste").Skip(1).FirstOrDefault()),
        "papel" => SetRole(dataDir, args.SkipWhile(a => a != "papel").Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != dataDir)),
        _ => Help(),
    };
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine(L.T("Sem permissão para acessar {0}. Rode como administrador.", dataDir ?? DataStore.DefaultDirectory));
    return 1;
}

static async Task<int> RunAsync(string[] args, string? dataDir)
{
    var store = DataStore.Open(dataDir);
    var config = store.LoadConfig();
    // O serviço fala português com os painéis (é a "língua do protocolo"); cada painel traduz para a dele.
    L.Use("pt");
    var certificate = CertificateStore.LoadOrCreate(store.CertificatePath, Environment.MachineName);
    var fingerprint = Fingerprint.Of(certificate);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
    builder.Services.AddWindowsService(o => o.ServiceName = Protocol.ServiceName);
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        void Https(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listen)
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(certificate);
        }
        // --somente-local: desenvolvimento/testes, sem expor na rede (e sem aviso do firewall).
        if (args.Contains("--somente-local")) k.ListenLocalhost(config.Port, Https);
        else k.ListenAnyIP(config.Port, Https);
    });
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    var engineOptions = new EngineOptions
    {
        Role = config.Role,
        LhmEnabled = config.LhmEnabled,
        PeripheralsEnabled = config.PeripheralsEnabled,
        SmartInterval = TimeSpan.FromMinutes(Math.Max(1, config.SmartIntervalMinutes)),
        SmartDisabledDisks = new HashSet<string>(config.SmartDisabledDisks, StringComparer.OrdinalIgnoreCase),
        DataDirectory = store.Directory,
    };
    builder.Services.AddSingleton(engineOptions);
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(store);
    builder.Services.AddSingleton(new HwMon.Core.Collection.ProcessMonitor());
    builder.Services.AddSingleton<BenchmarkHost>();
    builder.Services.AddSingleton<LoadBenchHost>();
    builder.Services.AddSingleton(sp => new HwMon.Core.Collection.FrameMonitor(m => sp.GetRequiredService<ILogger<EngineHost>>().LogInformation("{Message}", m)));
    builder.Services.AddSingleton<EngineHost>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineHost>());
    builder.Services.AddSingleton<HwMon.Collector.History.GameRecorder>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HwMon.Collector.History.GameRecorder>());
    builder.Services.AddSingleton(new ClientRegistry(store.ClientsPath));
    builder.Services.AddSingleton(new PairingCodes(store.PairingPath));
    builder.Services.AddSingleton<PairRequests>();

    var app = builder.Build();
    var log = app.Logger;
    log.LogInformation("Corewise Service {Version} na porta {Port}. Dados em {Dir}. Certificado {Fingerprint}.",
        TelemetryEngine.AgentVersion, config.Port, store.Directory, Fingerprint.Short(fingerprint));

    var jsonOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
    var pairGate = new SemaphoreSlim(1, 1);
    var pairQueue = 0;

    // O corpo vem de fora: campo omitido ou null no JSON chega como null mesmo sendo "string" no C#.
    static bool ValidPairRequest(PairRequest? r)
    {
        if (r is null) return false;
        r.Code ??= "";
        r.ClientName ??= "";
        return !string.IsNullOrWhiteSpace(r.ClientId) && r.ClientId.Length <= 100 && r.ClientName.Length <= 100 && r.Code.Length <= 20;
    }

    // ---------------------------------------------------------------- público

    app.MapGet("/v1/info", (EngineHost host) => host.Engine is not { } e
        ? Results.Problem(L.T("O serviço ainda está iniciando."), statusCode: 503)
        : Results.Ok(new AgentInfo
        {
            MachineId = e.MachineId,
            HostName = e.HostName,
            Role = config.Role,
            AgentVersion = TelemetryEngine.AgentVersion,
            CertificateFingerprint = fingerprint,
        }));

    app.MapPost("/v1/pair", async (PairRequest request, EngineHost host, PairingCodes codes, ClientRegistry clients, HttpContext http) =>
    {
        if (host.Engine is not { } e) return Results.Problem(L.T("O serviço ainda está iniciando."), statusCode: 503);
        if (!ValidPairRequest(request)) return Results.BadRequest();

        // Uma tentativa por vez, com 1 s de espera: 6 dígitos não podem ser testados em massa. Fila curta: uma
        // rajada de pedidos recebe 429 em vez de acumular espera (e quem desistiu sai da fila).
        if (Interlocked.Increment(ref pairQueue) > 8)
        {
            Interlocked.Decrement(ref pairQueue);
            return Results.Problem(L.T("Muitas tentativas de pareamento ao mesmo tempo. Tente de novo em alguns segundos."), statusCode: 429);
        }
        try
        {
            await pairGate.WaitAsync(http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref pairQueue);
            return Results.StatusCode(499);
        }
        try
        {
            await Task.Delay(1000, http.RequestAborted);
            // O painel do próprio PC (instalação completa) pareia sem código: o pedido vem de 127.0.0.1/::1, e quem já
            // roda programas neste PC não ganha nada a mais com isso. De outro PC, sempre com código.
            var fromThisPc = http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) && string.IsNullOrEmpty(request.Code);
            var result = fromThisPc ? PairingCodes.Result.Ok : codes.Verify(request.Code);
            log.LogInformation("Pareamento de {Name} ({Ip}): {Result}", request.ClientName, http.Connection.RemoteIpAddress, result);
            return result switch
            {
                PairingCodes.Result.Ok => Results.Ok(new PairResponse
                {
                    Token = clients.Add(request.ClientId, string.IsNullOrWhiteSpace(request.ClientName) ? "sem nome" : request.ClientName.Trim()),
                    MachineId = e.MachineId,
                    HostName = e.HostName,
                }),
                PairingCodes.Result.NoCode => Results.Problem(L.T("Nenhum pareamento aberto. No outro PC: menu Iniciar > Corewise > Código de pareamento."), statusCode: 403),
                PairingCodes.Result.Expired => Results.Problem(L.T("O código expirou. Gere outro."), statusCode: 403),
                PairingCodes.Result.Locked => Results.Problem(L.T("Tentativas esgotadas. Gere outro código."), statusCode: 403),
                _ => Results.Problem(L.T("Código incorreto."), statusCode: 403),
            };
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
        finally
        {
            pairGate.Release();
            Interlocked.Decrement(ref pairQueue);
        }
    });

    // Pareamento por aceite: pedir (de outro PC) e perguntar o resultado. Quem aceita é o painel deste PC.
    app.MapPost("/v1/pair/ask", (PairRequest request, EngineHost host, PairRequests requests, HttpContext http) =>
    {
        if (host.Engine is not { } e) return Results.Problem(L.T("O serviço ainda está iniciando."), statusCode: 503);
        if (!ValidPairRequest(request)) return Results.BadRequest();
        var name = string.IsNullOrWhiteSpace(request.ClientName) ? "sem nome" : request.ClientName.Trim();
        var id = requests.Ask(request.ClientId, name, http.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } v4 ? v4.MapToIPv4().ToString() : http.Connection.RemoteIpAddress?.ToString());
        log.LogInformation("Pedido de pareamento de {Name} ({Ip})", name, http.Connection.RemoteIpAddress);
        return id is null
            ? Results.Problem(L.T("Pedidos de pareamento demais abertos. Tente de novo em 2 minutos."), statusCode: 429)
            : Results.Ok(new PairAskResponse { Id = id, MachineId = e.MachineId, HostName = e.HostName });
    });
    app.MapGet("/v1/pair/ask/{id}", (string id, PairRequests requests) => Results.Ok(requests.Status(id)));
    app.MapDelete("/v1/pair/ask/{id}", (string id, PairRequests requests) => requests.Cancel(id) ? Results.Ok() : Results.NotFound());

    // ---------------------------------------------------------------- autenticado

    var api = app.MapGroup("/v1").AddEndpointFilter(async (ctx, next) =>
    {
        var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)
            || ctx.HttpContext.RequestServices.GetRequiredService<ClientRegistry>().Validate(header["Bearer ".Length..].Trim()) is null)
            return Results.Problem(L.T("Cliente não pareado ou acesso revogado."), statusCode: 401);
        if (ctx.HttpContext.RequestServices.GetRequiredService<EngineHost>().Engine is null)
            return Results.Problem(L.T("O serviço ainda está iniciando."), statusCode: 503);
        return await next(ctx);
    });

    // Só o painel deste próprio PC (conexão local): responder pedidos de pareamento e gerar o código.
    static bool Local(HttpContext http) => http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
    api.MapGet("/pair/pending", (PairRequests requests, HttpContext http) => Local(http) ? Results.Ok(requests.Pending()) : Results.StatusCode(403));
    api.MapPost("/pair/pending/{id}", (string id, bool approve, PairRequests requests, HttpContext http) =>
    {
        if (!Local(http)) return Results.StatusCode(403);
        var ok = requests.Answer(id, approve);
        log.LogInformation("Pedido de pareamento {Id}: {Answer}", id, approve ? "permitido" : "recusado");
        return ok ? Results.Ok() : Results.NotFound();
    });
    api.MapPost("/pair/code", (PairingCodes codes, EngineHost host, HttpContext http) => !Local(http)
        ? Results.StatusCode(403)
        : Results.Ok(new PairCodeResponse
        {
            Code = codes.Create(),
            Minutes = (int)PairingCodes.Validity.TotalMinutes,
            HostName = host.Engine!.HostName,
            Addresses = LocalAddresses(),
        }));

    api.MapGet("/machine", (EngineHost host) => Results.Ok(host.Engine!.Descriptor()));
    api.MapGet("/snapshot", (EngineHost host) => Results.Ok(host.Engine!.Snapshot()));
    api.MapGet("/system", async (EngineHost host, bool? refresh, CancellationToken cancel) => Results.Ok(await host.SummaryAsync(refresh == true, cancel)));

    // ---------------------------------------------------------------- benchmarks

    // Disco (estilo CrystalDiskMark): um por vez, em segundo plano; o painel acompanha pelo status.
    api.MapPost("/bench/disk", (DiskBenchRequest request, BenchmarkHost bench, EngineHost host) =>
    {
        string? DiskFor(string drive)
        {
            var e = host.Engine;
            if (e is null) return null;
            var d = e.Descriptor();
            var volume = d.Components.FirstOrDefault(c => c.Kind == ComponentKind.Volume && c.Name.StartsWith(drive, StringComparison.OrdinalIgnoreCase));
            return volume?.Parents.Select(p => d.Components.FirstOrDefault(c => c.Id == p)?.Name).FirstOrDefault(n => n is not null);
        }
        return bench.Start(request, DiskFor, out var id)
            ? Results.Accepted(value: new { id })
            : Results.Problem(L.T("Já há um teste de disco rodando nesta máquina."), statusCode: 409);
    });
    api.MapGet("/bench/disk/status", (BenchmarkHost bench) => Results.Ok(bench.Status()));
    api.MapPost("/bench/disk/cancel", (BenchmarkHost bench) => { bench.Cancel(); return Results.Ok(); });
    api.MapGet("/bench/disk/history", (BenchmarkHost bench) => Results.Ok(bench.History()));

    // Rede: o painel mede a velocidade entre ele e este coletor pela própria conexão HTTPS (sem porta extra no
    // firewall). download = coletor → painel; upload = painel → coletor; ping = ida e volta de um pedido vazio.
    var noise = new byte[1 << 20];
    Random.Shared.NextBytes(noise); // aleatório: não comprime em nenhum ponto do caminho
    // Teste de rede: até 8 fluxos ao mesmo tempo (o painel usa 4 por teste); além disso, 429. Um teste a mais em
    // paralelo só dividiria a banda e estragaria os dois resultados.
    var netStreams = new SemaphoreSlim(8, 8);
    api.MapGet("/bench/net/download", async (HttpContext http, int? seconds, CancellationToken cancel) =>
    {
        if (!netStreams.Wait(0)) return Results.Problem(L.T("Outro teste de rede está em andamento nesta máquina. Tente de novo em alguns segundos."), statusCode: 429);
        try
        {
            var limit = System.Diagnostics.Stopwatch.StartNew();
            var duration = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 10, 1, 30));
            http.Response.ContentType = "application/octet-stream";
            try
            {
                while (limit.Elapsed < duration && !cancel.IsCancellationRequested)
                    await http.Response.Body.WriteAsync(noise, cancel);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            return Results.Empty;
        }
        finally
        {
            netStreams.Release();
        }
    });
    api.MapPost("/bench/net/upload", async (HttpContext http, CancellationToken cancel) =>
    {
        // Sem o limite padrão de 30 MB por pedido: o teste manda centenas de MB.
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
        if (!netStreams.Wait(0)) return Results.Problem(L.T("Outro teste de rede está em andamento nesta máquina. Tente de novo em alguns segundos."), statusCode: 429);
        var buffer = new byte[1 << 16];
        long total = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // O teste dura até 30 s: um envio que não acaba é cortado em 45 s.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            int n;
            while ((n = await http.Request.Body.ReadAsync(buffer, deadline.Token)) > 0) total += n;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Microsoft.AspNetCore.Connections.ConnectionResetException) { }
        finally
        {
            netStreams.Release();
        }
        return Results.Ok(new { bytes = total, seconds = clock.Elapsed.TotalSeconds });
    });
    api.MapGet("/bench/ping", () => Results.Ok(new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));

    // CPU, memória, GPU e estabilidade (um teste pesado por vez, contando o de disco).
    api.MapPost("/bench/run", (BenchRequest request, LoadBenchHost bench) =>
        bench.Start(request, out var id) is { } problem
            ? Results.Problem(problem, statusCode: 409)
            : Results.Accepted(value: new { id }));
    api.MapGet("/bench/run/status", (LoadBenchHost bench, string kind) => Results.Ok(bench.Status(kind)));
    api.MapPost("/bench/run/cancel", (LoadBenchHost bench) => { bench.Cancel(); return Results.Ok(); });
    api.MapGet("/bench/run/history", (LoadBenchHost bench, string? kind) => Results.Ok(bench.History(kind)));
    api.MapDelete("/bench/run/history/{id}", (LoadBenchHost bench, string id) => bench.Delete(id) ? Results.Ok() : Results.NotFound());
    api.MapDelete("/bench/disk/history/{id}", (BenchmarkHost bench, string id) => bench.Delete(id) ? Results.Ok() : Results.NotFound());

    // Limpar o histórico (aba Histórico do painel): sensores e uso, jogos, ou os dois. Recomeça a gravar na hora.
    api.MapPost("/history/clear", async (EngineHost host, HwMon.Collector.History.GameRecorder games, HistoryClearRequest request) =>
    {
        if (request.Sensors)
        {
            if (host.History is null) return Results.Problem(L.T("Histórico desligado neste coletor."), statusCode: 404);
            await host.ClearHistoryAsync();
        }
        var sessions = request.Games ? games.Clear() : 0;
        return Results.Ok(new { sessions });
    });

    // Quadros por segundo (modo jogo): todos os programas desenhando agora, ou só o pid pedido.
    api.MapGet("/frames", (HwMon.Core.Collection.FrameMonitor frames, int? pid) => Results.Ok(frames.Query(pid)));

    // Processos mais pesados (medidos entre uma chamada e a seguinte; o painel pede a cada 3 s).
    api.MapGet("/processes", (HwMon.Core.Collection.ProcessMonitor monitor) => Results.Ok(monitor.Sample()));

    // Histórico: from/to em segundos Unix (padrão: últimas 24 h); points = máximo de pontos devolvidos.
    api.MapGet("/history", (EngineHost host, string component, string metric, long? from, long? to, int? points) =>
    {
        if (host.History is not { } history) return Results.Problem(L.T("Histórico desligado neste coletor."), statusCode: 404);
        var end = to ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = from ?? end - 86400;
        if (start >= end || end - start > 3L * 366 * 86400) return Results.BadRequest();
        var result = history.Query(component, metric, start, end, points ?? 600);
        return result is null ? Results.Problem(L.T("Sem histórico para essa métrica."), statusCode: 404) : Results.Ok(result);
    });
    api.MapGet("/history/stats", (EngineHost host, long? from, long? to) =>
    {
        if (host.History is not { } history) return Results.Problem(L.T("Histórico desligado neste coletor."), statusCode: 404);
        var end = to ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = from ?? end - 86400;
        return start >= end ? Results.BadRequest() : Results.Ok(history.Stats(start, end));
    });

    // Uso consolidado dia a dia (aba Histórico do painel). Padrão: últimos 30 dias.
    api.MapGet("/usage", (EngineHost host, HwMon.Collector.History.GameRecorder games, long? from, long? to) =>
    {
        if (host.History is not { } history) return Results.Problem(L.T("Histórico desligado neste coletor."), statusCode: 404);
        var end = to ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = from ?? end - 30L * 86400;
        if (start >= end || end - start > 3L * 366 * 86400) return Results.BadRequest();
        return Results.Ok(history.Usage(start, end, host.Engine!.Descriptor().Components, games.Store is { } g ? g.SecondsByDay : null));
    });

    // Jogos: sessões gravadas (FPS, 1 % low, sensores). from/to em segundos Unix (padrão: tudo).
    api.MapGet("/games", (HwMon.Collector.History.GameRecorder games, HwMon.Core.Collection.FrameMonitor frames, long? from, long? to) =>
    {
        if (games.Store is not { } store) return Results.Problem(L.T("Histórico de jogos desligado neste coletor."), statusCode: 404);
        var start = from ?? 0;
        var end = to ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
        var probe = frames.Query(-1);
        var current = games.Current();
        var sessions = store.Sessions(start, end);
        foreach (var live in current)
        {
            var i = sessions.FindIndex(s => s.Id == live.Id);
            if (i >= 0) sessions[i] = live;
            else if (live.Start < end && live.End >= start) sessions.Insert(0, live);
        }
        var infos = store.Games().ToDictionary(g => g.Exe);
        sessions = sessions.Where(s => !(infos.TryGetValue(s.Exe, out var i) && i.Ignored)).ToList();
        foreach (var s in sessions)
            if (infos.TryGetValue(s.Exe, out var info)) s.Name = info.Display;
        var summaries = sessions.GroupBy(s => s.Exe).Select(g =>
        {
            var weight = g.Sum(s => (double)Math.Max(1, s.ActiveSeconds));
            var energy = g.Where(s => s.EnergyWh is not null).ToList();
            return new GameSummary
            {
                Exe = g.Key, Name = g.First().Name, Path = infos.GetValueOrDefault(g.Key)?.Path,
                Sessions = g.Count(), Seconds = g.Sum(s => s.ActiveSeconds), LastPlayed = g.Max(s => s.End),
                FpsAvg = Math.Round(g.Sum(s => s.FpsAvg * Math.Max(1, s.ActiveSeconds)) / weight, 1),
                Low1Fps = Math.Round(g.Sum(s => s.Low1Fps * Math.Max(1, s.ActiveSeconds)) / weight, 1),
                EnergyWh = energy.Count > 0 ? Math.Round(energy.Sum(s => s.EnergyWh!.Value), 1) : null,
            };
        }).OrderByDescending(g => g.Seconds).ToList();
        return Results.Ok(new GamesResponse
        {
            From = start, To = end, Recording = probe.Running, Error = probe.Error,
            Games = summaries, Sessions = sessions.Take(500).ToList(),
            Ignored = infos.Values.Where(i => i.Ignored).Select(i => new GameSummary { Exe = i.Exe, Name = i.Display, Path = i.Path }).ToList(),
        });
    });
    api.MapGet("/games/sessions/{id:long}", (HwMon.Collector.History.GameRecorder games, long id) =>
    {
        if (games.Store is not { } store) return Results.Problem(L.T("Histórico de jogos desligado neste coletor."), statusCode: 404);
        var session = games.CurrentSession(id) ?? store.Session(id);
        if (session is null) return Results.NotFound();
        if (store.Game(session.Exe) is { } info) session.Name = info.Display;
        return Results.Ok(session);
    });
    api.MapDelete("/games/sessions/{id:long}", (HwMon.Collector.History.GameRecorder games, long id) =>
        games.Store is { } store && store.Delete(id) ? Results.Ok() : Results.NotFound());
    api.MapPost("/games/update", (HwMon.Collector.History.GameRecorder games, GameUpdate update) =>
    {
        if (games.Store is not { } store || !store.Update(update)) return Results.NotFound();
        games.Refresh(update.Exe);
        return Results.Ok();
    });

    // Server-Sent Events: um snapshot por segundo enquanto o cliente estiver conectado.
    api.MapGet("/stream", async (EngineHost host, ClientRegistry clients, HttpContext http, CancellationToken cancel) =>
    {
        var engine = host.Engine!;
        var token = http.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        long last = -1;
        var lastCheck = DateTime.UtcNow;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                // Acesso revogado com o stream aberto: encerra em até 10 s.
                if (DateTime.UtcNow - lastCheck > TimeSpan.FromSeconds(10))
                {
                    lastCheck = DateTime.UtcNow;
                    if (clients.Validate(token) is null) return;
                }
                var sequence = engine.Sequence;
                if (sequence != last)
                {
                    last = sequence;
                    var json = JsonSerializer.Serialize(engine.Snapshot(), jsonOptions);
                    await http.Response.WriteAsync($"data: {json}\n\n", cancel);
                    await http.Response.Body.FlushAsync(cancel);
                }
                await Task.Delay(200, cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // cliente desconectou
        }
    });

    await app.RunAsync();
    return 0;
}

/// <summary>IPv4 das placas de rede ligadas (sem loopback nem APIPA), para quem vai digitar o endereço.</summary>
static List<string> LocalAddresses() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
    .Select(a => a.Address.ToString()).Distinct().ToList();

static int Pair(string? dataDir, bool pause)
{
    // Atalho "Código de pareamento" do menu Iniciar: sem administrador, pede a elevação e abre numa janela que espera Enter.
    if (dataDir is null && !DataStore.IsAdministrator())
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "parear --pausar") { UseShellExecute = true, Verb = "runas" });
            return 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(L.T("É preciso permitir o acesso de administrador para gerar o código."));
            return 1;
        }
    }
    var store = DataStore.Open(dataDir);
    using var certificate = CertificateStore.LoadOrCreate(store.CertificatePath, Environment.MachineName);
    var code = new PairingCodes(store.PairingPath).Create();
    var config = store.LoadConfig();
    var addresses = LocalAddresses();
    Console.WriteLine();
    static string Label(string pt) => ("  " + L.T(pt)).PadRight(15);
    Console.WriteLine("  " + L.T("Corewise · parear este PC"));
    Console.WriteLine();
    Console.WriteLine(Label("Código:") + $"{code[..3]} {code[3..]}     " + L.T("(vale {0:0} minutos, uma vez)", PairingCodes.Validity.TotalMinutes));
    Console.WriteLine(Label("Este PC:") + Environment.MachineName + (addresses.Count > 0 ? "   " + L.T("ou") + "   " + string.Join("  /  ", addresses) : ""));
    if (config.Port != Protocol.DefaultPort) Console.WriteLine(Label("Porta:") + config.Port);
    Console.WriteLine(Label("Certificado:") + Fingerprint.Short(Fingerprint.Of(certificate)));
    Console.WriteLine();
    Console.WriteLine("  " + L.T("No outro PC: Corewise > Adicionar máquina, digite o nome (ou o endereço) deste PC e o código."));
    Console.WriteLine();
    if (pause)
    {
        Console.Write("  " + L.T("Enter para fechar") + " ");
        Console.ReadLine();
    }
    return 0;
}

static int ListClients(string? dataDir)
{
    var clients = new ClientRegistry(DataStore.Open(dataDir).ClientsPath).List();
    if (clients.Count == 0)
    {
        Console.WriteLine("Nenhum cliente pareado.");
        return 0;
    }
    foreach (var c in clients)
        Console.WriteLine($"{c.Name,-24} pareado {c.PairedAt:dd/MM/yyyy HH:mm}   último acesso {(c.LastSeen is { } s ? s.ToString("dd/MM/yyyy HH:mm") : "nunca")}   id {c.Id}");
    return 0;
}

static int Revoke(string? dataDir, string? who)
{
    if (string.IsNullOrWhiteSpace(who))
    {
        Console.Error.WriteLine("Informe o nome ou o id do cliente (veja \"clientes\").");
        return 2;
    }
    var removed = new ClientRegistry(DataStore.Open(dataDir).ClientsPath).Revoke(who);
    Console.WriteLine(removed > 0 ? $"Acesso revogado ({removed})." : "Nenhum cliente com esse nome ou id.");
    return removed > 0 ? 0 : 1;
}

static int SetRole(string? dataDir, string? role)
{
    var store = DataStore.Open(dataDir);
    var config = store.LoadConfig();
    config.Role = string.IsNullOrWhiteSpace(role) ? null : role.Trim();
    store.SaveConfig(config);
    Console.WriteLine($"Papel: {config.Role ?? "(nenhum)"}. Reinicie o serviço para aplicar: sc.exe stop {Protocol.ServiceName} e sc.exe start {Protocol.ServiceName}");
    return 0;
}

static int Status(string? dataDir)
{
    var store = DataStore.Open(dataDir);
    var config = store.LoadConfig();
    using var certificate = CertificateStore.LoadOrCreate(store.CertificatePath, Environment.MachineName);
    Console.WriteLine($"Serviço:      {Installer.ServiceState()}");
    Console.WriteLine($"Porta:        {config.Port}");
    Console.WriteLine($"Papel:        {config.Role ?? "(não definido)"}");
    Console.WriteLine($"Certificado:  {Fingerprint.Short(Fingerprint.Of(certificate))}");
    Console.WriteLine($"Clientes:     {new ClientRegistry(store.ClientsPath).List().Count}");
    Console.WriteLine($"Dados:        {store.Directory}");
    return 0;
}

static int PrintSummary()
{
    var summary = HwMon.Core.Collection.SummaryCollector.Collect();
    foreach (var section in summary.Sections)
    {
        Console.WriteLine($"\n== {section.Title}{(section.Headline is null ? "" : $" — {section.Headline}")}");
        foreach (var group in section.Groups)
        {
            if (group.Title is not null) Console.WriteLine($"  [{group.Title}]");
            foreach (var item in group.Items) Console.WriteLine($"    {item.Label,-28} {item.Value}");
        }
    }
    foreach (var p in summary.Problems) Console.WriteLine($"\n(!) {p}");
    return 0;
}

// FPS de quem está desenhando (precisa de administrador): Corewise.Service.exe quadros
// Testes de carga no console (sem serviço): "teste cpu", "teste memoria", "teste gpu".
static async Task<int> RunLoadBench(string? kind)
{
    var progress = new Action<string, double>((phase, p) => Console.WriteLine($"  [{p,4:P0}] {phase}"));
    var partial = new Action<BenchScore>(s => Console.WriteLine($"      {s.Label}: {s.Value:N2} {s.Unit}"));
    BenchResult r = kind switch
    {
        "cpu" => await HwMon.Core.Collection.Bench.CpuBenchmark.RunAsync(progress, partial, default),
        "memoria" => await HwMon.Core.Collection.Bench.MemoryBenchmark.RunAsync(progress, partial, default),
        "gpu" => await HwMon.Core.Collection.Bench.GpuBenchmark.RunAsync(progress, partial, default),
        _ => throw new ArgumentException("Use: teste cpu | memoria | gpu"),
    };
    Console.WriteLine();
    foreach (var s in r.Scores) Console.WriteLine($"{s.Key,-10} {s.Label,-22} {s.Value,12:N2} {s.Unit}  {s.Detail}");
    Console.WriteLine($"({r.Seconds} s; {r.Device} {r.Detail})");
    return 0;
}

static async Task<int> PrintFrames()
{
    Console.WriteLine($"(estrutura EVENT_TRACE_LOGFILEW: {HwMon.Core.Collection.FrameMonitor.LogfileStructSize} bytes; esperado 448)");
    using var monitor = new HwMon.Core.Collection.FrameMonitor(Console.WriteLine);
    for (var i = 0; i < 5; i++)
    {
        await Task.Delay(2000);
        var r = monitor.Query(null);
        Console.WriteLine($"--- {DateTime.Now:HH:mm:ss} sessão {(r.Running ? "ligada" : "desligada")} {r.Error}");
        foreach (var p in r.Processes.Take(8))
            Console.WriteLine($"  {p.Name,-28} {p.Fps,7:0.0} FPS  {p.FrametimeMs,6:0.00} ms  1% low {p.Low1Fps,6:0.0}");
    }
    return 0;
}

// Teste de disco no console: Corewise.Service.exe benchmark D [MB] [segundos]
static async Task<int> RunDiskBenchmark(string[] args)
{
    var rest = args.SkipWhile(a => a != "benchmark").Skip(1).ToArray();
    var request = new DiskBenchRequest
    {
        Drive = rest.ElementAtOrDefault(0) ?? "C",
        SizeMb = int.TryParse(rest.ElementAtOrDefault(1), out var mb) ? mb : 1024,
        Seconds = int.TryParse(rest.ElementAtOrDefault(2), out var s) ? s : 5,
    };
    var last = "";
    var result = await HwMon.Core.Collection.DiskBenchmark.RunAsync(request, (phase, p) =>
    {
        if (phase != last) { Console.WriteLine($"  {phase}..."); last = phase; }
    }, CancellationToken.None);
    Console.WriteLine($"\n{result.Drive} {result.VolumeLabel} · arquivo de {result.SizeMb} MB · {result.Seconds} s por medição");
    Console.WriteLine($"{"Teste",-12} {"Leitura",12} {"Escrita",12}   {"IOPS L/E",18}   latência L/E");
    foreach (var l in result.Lines)
        Console.WriteLine($"{l.Test,-12} {l.ReadMBs,9:0.0} MB/s {l.WriteMBs,9:0.0} MB/s   {l.ReadIops,8:0}/{l.WriteIops,-8:0}   {l.ReadLatencyUs:0} / {l.WriteLatencyUs:0} µs");
    return 0;
}

static int PrintPeripherals()
{
    var problems = new List<string>();
    using var collector = new HwMon.Core.Collection.Peripherals.PeripheralCollector(problems);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    collector.Scan();
    Console.WriteLine($"Busca em {watch.ElapsedMilliseconds} ms: {collector.Devices.Count} aparelho(s).");
    var readings = collector.Sample().ToList();
    foreach (var d in collector.Devices)
    {
        var r = readings.First(x => x.Instance == d.Key);
        Console.WriteLine($"  {d.Name,-28} {d.Type,-9} {d.Connection,-10} #{d.Index:X2}  bateria {(r.Value is { } v ? $"{v:0} %" : "—"),-6} {r.Note}   [{d.Key}, função 0x{d.BatteryFeature:X4}]");
    }
    foreach (var p in problems) Console.WriteLine($"(!) {p}");

    // Diagnóstico dos receptores (HID++ 1.0, relatório curto): registro 0x02 = aparelhos conectados.
    foreach (var shortCol in HwMon.Core.Collection.Peripherals.HidDevice.Enumerate(0x046D).Where(h => h.UsagePage == 0xFF00 && h.OutputLength == 7))
    {
        using var s = HwMon.Core.Collection.Peripherals.HidDevice.Open(shortCol.Path);
        if (s is null) continue;
        s.Write([0x10, 0xFF, 0x81, 0x02, 0, 0, 0]);
        var buf = new byte[7];
        using var cts = new CancellationTokenSource(800);
        try
        {
            while (true)
            {
                var n = s.ReadAsync(buf, 0, 7, cts.Token).GetAwaiter().GetResult();
                if (n >= 7 && buf[2] == 0x81 && buf[3] == 0x02) { Console.WriteLine($"Receptor {shortCol.ProductId:X4}: {buf[5]} aparelho(s) conectado(s) agora (registro 0x02: {Convert.ToHexString(buf)})"); break; }
                if (n >= 7 && buf[2] == 0x8F) { Console.WriteLine($"Receptor {shortCol.ProductId:X4}: erro {Convert.ToHexString(buf)}"); break; }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine($"Receptor {shortCol.ProductId:X4}: sem resposta ao registro 0x02"); }
    }
    // Outras marcas (Dell): lista o que dá para ler. --escutar N fica N segundos lendo os relatórios do fabricante.
    var listen = Array.IndexOf(Environment.GetCommandLineArgs(), "--escutar") is var li and >= 0 && li + 1 < Environment.GetCommandLineArgs().Length
        ? int.Parse(Environment.GetCommandLineArgs()[li + 1]) : 0;
    foreach (var h in HwMon.Core.Collection.Peripherals.HidDevice.Enumerate().Where(h => h.VendorId == 0x413C && h.UsagePage >= 0xFF00))
    {
        Console.WriteLine($"Dell {h.ProductId:X4} página {h.UsagePage:X4}/{h.Usage:X4}: entrada {h.InputLength}, saída {h.OutputLength}, feature {h.FeatureLength}");
        var answered = 0;
        for (var id = 0; id < 256 && h.FeatureLength > 0; id++)
            if (HwMon.Core.Collection.Peripherals.HidDevice.GetFeature(h.Path, (byte)id, h.FeatureLength) is { } f)
            {
                answered++;
                Console.WriteLine($"  feature #{id}: {Convert.ToHexString(f)}");
            }
        if (h.FeatureLength > 0 && answered == 0) Console.WriteLine("  nenhum relatório feature respondeu (ids 0–255)");
        if (listen > 0 && h.InputLength > 0 && HwMon.Core.Collection.Peripherals.HidDevice.Open(h.Path) is { } s)
        {
            using (s)
            {
                var buf = new byte[h.InputLength];
                using var c = new CancellationTokenSource(TimeSpan.FromSeconds(listen));
                try { while (true) { var n = s.ReadAsync(buf, 0, buf.Length, c.Token).GetAwaiter().GetResult(); Console.WriteLine($"  {DateTime.Now:HH:mm:ss} entrada: {Convert.ToHexString(buf, 0, n)}"); } }
                catch (OperationCanceledException) { Console.WriteLine($"  ({listen} s escutando)"); }
            }
        }
    }
    Console.WriteLine();
    Console.WriteLine("Coleções Logitech/Dell encontradas:");
    foreach (var h in HwMon.Core.Collection.Peripherals.HidDevice.Enumerate().Where(h => h.VendorId is 0x046D or 0x413C))
        Console.WriteLine($"  {h.VendorId:X4}:{h.ProductId:X4} página {h.UsagePage:X4}/{h.Usage:X4} entrada {h.InputLength} saída {h.OutputLength}  {h.Product}");
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        HwMon.Collector — coletor de telemetria (serviço do Windows)

          Corewise.Service.exe                  roda o coletor (como serviço ou no console)
          Corewise.Service.exe instalar         instala e inicia o serviço + regra de firewall   (administrador)
          Corewise.Service.exe desinstalar      remove o serviço e a regra de firewall           (administrador)
          Corewise.Service.exe parear           gera um código de 6 dígitos para parear um PC    (administrador)
          Corewise.Service.exe clientes         lista os PCs pareados
          Corewise.Service.exe revogar <nome>   corta o acesso de um PC pareado
          Corewise.Service.exe status           estado do serviço, porta e certificado
          Corewise.Service.exe sistema          mostra o resumo do sistema (placa-mãe, BIOS, CPU, memória...)
          Corewise.Service.exe papel <texto>    nome do papel mostrado no painel (ex.: NAS)      (administrador)

        Configuração: %ProgramData%\HwMon\config.json (porta, papel, discos sem SMART). Reinicie o serviço após editar.
        Opção de desenvolvimento: --dados <pasta> usa outra pasta de dados.
        """);
    return 0;
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
