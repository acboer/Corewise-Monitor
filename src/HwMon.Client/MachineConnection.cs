using HwMon.Core.Telemetry;

namespace HwMon.Client;

public enum ConnectionStatus
{
    Connecting,
    Connected,
    /// <summary>Caiu ou não respondeu; tentando de novo sozinho.</summary>
    Disconnected,
    /// <summary>Token revogado ou certificado diferente: só volta com novo pareamento.</summary>
    Failed,
}

public sealed record ConnectionState(
    ConnectionStatus Status,
    DateTimeOffset Since,
    string? Message,
    MachineDescriptor? Descriptor,
    TelemetrySnapshot? Snapshot,
    TimeSpan? NextRetry);

/// <summary>
/// Mantém a ligação com um coletor: assina o stream, recarrega o descritor quando ele muda e, se a conexão cair,
/// sinaliza "desconectado desde…" e tenta de novo com espera crescente (1, 2, 5, 10, 15 s), mantendo os últimos dados.
/// </summary>
public sealed class MachineConnection(PairedMachine machine, MachineStore? store = null)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)];

    public PairedMachine Machine { get; } = machine;

    public async Task RunAsync(Action<ConnectionState> onChange, CancellationToken cancel)
    {
        MachineDescriptor? descriptor = null;
        TelemetrySnapshot? last = null;
        var status = ConnectionStatus.Connecting;
        var since = DateTimeOffset.Now;
        var failures = 0;
        var unauthorized = 0;

        void Set(ConnectionStatus s, string? message, TimeSpan? retry = null)
        {
            if (s != status) since = DateTimeOffset.Now;
            status = s;
            onChange(new ConnectionState(s, since, message, descriptor, last, retry));
        }

        Set(ConnectionStatus.Connecting, $"Conectando a {Machine.Host}:{Machine.Port}...");
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var client = CollectorClient.For(Machine);
                descriptor = await client.MachineAsync(cancel);
                // Memoriza o IP que funcionou: na próxima, conecta mesmo se o DNS (ex.: da VPN) não achar o nome.
                if (client.ConnectedAddress is { } address && Machine.KnownAddresses.FirstOrDefault() != address)
                {
                    Machine.KnownAddresses.Remove(address);
                    Machine.KnownAddresses.Insert(0, address);
                    store?.RememberAddress(Machine.MachineId, address);
                }
                await foreach (var snapshot in client.StreamAsync(TimeSpan.FromSeconds(10), cancel))
                {
                    if (snapshot.DescriptorVersion != descriptor.Version)
                        descriptor = await client.MachineAsync(cancel);
                    last = snapshot;
                    failures = 0;
                    unauthorized = 0;
                    Set(ConnectionStatus.Connected, null);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (CertificateMismatchException ex)
            {
                Set(ConnectionStatus.Failed, ex.Message);
                return;
            }
            catch (CollectorException ex) when (ex.Unauthorized)
            {
                // Token recusado. Pode ser de vez (revogado, serviço reinstalado do zero) ou passageiro (serviço sendo
                // atualizado): antes a conexão desistia até reabrir o painel. Agora continua tentando, devagar.
                var wait = TimeSpan.FromSeconds(Math.Min(30 * ++unauthorized, 300));
                Set(ConnectionStatus.Failed, L.T("Acesso revogado ou pareamento perdido. Pareie de novo."), wait);
                try { await Task.Delay(wait, cancel); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                var wait = Backoff[Math.Min(failures++, Backoff.Length - 1)];
                Set(ConnectionStatus.Disconnected, ex is CollectorException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}", wait);
                try { await Task.Delay(wait, cancel); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
