using System.Diagnostics;
using System.Runtime.InteropServices;
using HwMon.Core.Telemetry;

namespace HwMon.Collector;

/// <summary>
/// Desligar, reiniciar e suspender pedidos pelo painel. Desligar/reiniciar usam o shutdown.exe do próprio Windows:
/// com espera, o Windows mostra o aviso padrão para quem estiver usando o PC e o pedido pode ser cancelado
/// (shutdown /a). Suspender não tem aviso do Windows; com espera, fica um temporizador aqui, que também cancela.
/// </summary>
public sealed class PowerControl(ILogger<PowerControl> log)
{
    public const int MaxDelaySeconds = 600;
    private readonly object _lock = new();
    private CancellationTokenSource? _pendingSleep;

    public PowerResponse Run(PowerRequest request, string requestedBy)
    {
        var delay = Math.Clamp(request.DelaySeconds, 0, MaxDelaySeconds);
        var who = new string(requestedBy.Where(c => !char.IsControl(c) && c != '"').ToArray()).Trim();
        if (who.Length == 0) who = "outro PC";
        if (who.Length > 60) who = who[..60];
        log.LogWarning("Energia: {Action} em {Delay} s, pedido por {Who}", request.Action, delay, who);

        switch (request.Action)
        {
            case "shutdown":
            case "restart":
            {
                CancelSleep();
                var flag = request.Action == "shutdown" ? "/s" : "/r";
                var verb = request.Action == "shutdown" ? "Desligamento" : "Reinício";
                // /d p:0:0 = "outro (planejado)"; /t maior que 0 já implica fechar os programas no fim da espera.
                var code = Shutdown($"{flag} /t {delay} /d p:0:0 /c \"Corewise: {verb} pedido por {who}.\"");
                if (code != 0) throw new InvalidOperationException(code == 1190
                    ? "Já há um desligamento ou reinício agendado neste PC."
                    : $"O Windows recusou o pedido (shutdown.exe saiu com {code}).");
                return new PowerResponse { Action = request.Action, At = DateTimeOffset.Now.AddSeconds(delay) };
            }
            case "sleep":
            {
                CancellationTokenSource cts;
                lock (_lock)
                {
                    _pendingSleep?.Cancel();
                    _pendingSleep = cts = new CancellationTokenSource();
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Um instante mesmo sem espera: a resposta HTTP sai antes de o PC dormir.
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(delay, 2)), cts.Token);
                        if (!SetSuspendState(false, false, false))
                            log.LogWarning("Energia: o Windows recusou suspender (erro {Error}).", Marshal.GetLastWin32Error());
                    }
                    catch (OperationCanceledException)
                    {
                        // cancelado pelo painel
                    }
                });
                return new PowerResponse { Action = "sleep", At = DateTimeOffset.Now.AddSeconds(Math.Max(delay, 2)) };
            }
            case "cancel":
            {
                var hadSleep = CancelSleep();
                var code = Shutdown("/a");
                // 1116 = nenhum desligamento agendado: só não havia o que cancelar.
                if (code != 0 && code != 1116 && !hadSleep) throw new InvalidOperationException($"Não foi possível cancelar (shutdown.exe saiu com {code}).");
                return new PowerResponse { Action = "cancel" };
            }
            default:
                throw new ArgumentException("Ação inválida. Use shutdown, restart, sleep ou cancel.");
        }
    }

    private bool CancelSleep()
    {
        lock (_lock)
        {
            if (_pendingSleep is null) return false;
            _pendingSleep.Cancel();
            _pendingSleep = null;
            return true;
        }
    }

    private static int Shutdown(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit(10_000);
        return p.HasExited ? p.ExitCode : -1;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
