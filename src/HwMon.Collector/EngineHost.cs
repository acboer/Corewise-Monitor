using HwMon.Collector.History;
using HwMon.Core.Collection;
using HwMon.Core.Telemetry;

namespace HwMon.Collector;

/// <summary>
/// Liga o motor de telemetria ao ciclo de vida do serviço. A descoberta leva alguns segundos (WMI, LHM);
/// até terminar, a API responde 503 e o cliente tenta de novo. Também guarda o resumo do sistema (estático)
/// e grava o histórico.
/// </summary>
public sealed class EngineHost(EngineOptions options, CollectorConfig config, DataStore store, ILogger<EngineHost> log) : BackgroundService
{
    private static readonly TimeSpan MinRefresh = TimeSpan.FromSeconds(15);
    private volatile TelemetryEngine? _engine;
    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private SystemSummary? _summary;

    public TelemetryEngine? Engine => _engine;

    /// <summary>Banco do histórico; nulo se desligado na configuração ou se não abriu.</summary>
    public HistoryStore? History { get; private set; }

    /// <summary>Resumo do sistema. Lido na primeira vez e quando pedem para atualizar (no máximo a cada 15 s).</summary>
    public async Task<SystemSummary> SummaryAsync(bool refresh, CancellationToken cancel)
    {
        await _summaryGate.WaitAsync(cancel);
        try
        {
            if (_summary is null || (refresh && DateTimeOffset.Now - _summary.CollectedAt > MinRefresh))
            {
                _summary = await Task.Run(SummaryCollector.Collect, cancel);
                foreach (var p in _summary.Problems) log.LogInformation("Resumo do sistema: {Problem}", p);
            }
            return _summary;
        }
        finally
        {
            _summaryGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var engine = new TelemetryEngine(options, message => log.LogWarning("{Message}", message));
        try
        {
            await Task.Run(engine.Initialize, stoppingToken);
            _engine = engine;
            _ = SummaryAsync(false, stoppingToken); // aquece o cache em segundo plano
            if (config.HistoryEnabled) _ = Task.Run(() => RecordAsync(engine, stoppingToken), stoppingToken);
            await engine.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // parando o serviço
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Falha no motor de telemetria");
            throw;
        }
        finally
        {
            _engine = null;
            engine.Dispose();
        }
    }

    private TaskCompletionSource<bool>? _clearRequest;

    /// <summary>Apaga o histórico de sensores e uso e recomeça a gravar a partir de agora.</summary>
    public async Task ClearHistoryAsync()
    {
        if (History is null) throw new InvalidOperationException("Histórico desligado neste coletor.");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _clearRequest, tcs)?.TrySetCanceled();
        await tcs.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    /// <summary>Um snapshot por segundo para o gravador; limpeza dos dados vencidos a cada hora.</summary>
    private async Task RecordAsync(TelemetryEngine engine, CancellationToken cancel)
    {
        HistoryStore history;
        try
        {
            history = new HistoryStore(store.HistoryPath);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Histórico desligado: não foi possível abrir {Path}", store.HistoryPath);
            return;
        }
        History = history;
        log.LogInformation("Histórico em {Path}, gravando desde {Since:dd/MM/yyyy HH:mm}", history.Path, DateTimeOffset.FromUnixTimeSeconds(history.Since).ToLocalTime());
        var recorder = new HistoryRecorder(history);
        long last = -1;
        var nextCleanup = DateTimeOffset.UtcNow.AddMinutes(1);
        var failures = 0;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(250, cancel);
                try
                {
                    if (Interlocked.Exchange(ref _clearRequest, null) is { } clear)
                    {
                        // Na mesma thread que grava: nada é escrito no meio da limpeza.
                        try
                        {
                            recorder = new HistoryRecorder(history); // descarta os blocos abertos
                            history.Clear();
                            log.LogInformation("Histórico de sensores e uso apagado a pedido do painel");
                            clear.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            clear.TrySetException(ex);
                        }
                    }
                    if (engine.Sequence != last)
                    {
                        last = engine.Sequence;
                        recorder.Add(engine.Snapshot());
                    }
                    if (DateTimeOffset.UtcNow >= nextCleanup)
                    {
                        nextCleanup = DateTimeOffset.UtcNow.AddHours(1);
                        var removed = history.Cleanup(DateTimeOffset.UtcNow);
                        if (removed > 0) log.LogInformation("Histórico: {Removed} linhas vencidas apagadas", removed);
                    }
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Disco cheio ou banco travado: registra (sem inundar o log) e continua tentando.
                    if (failures++ % 60 == 0) log.LogWarning(ex, "Histórico: falha ao gravar");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // parando
        }
        finally
        {
            History = null;
            history.Dispose();
        }
    }
}
