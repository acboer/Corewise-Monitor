using System.Windows;
using System.Windows.Threading;
using HwMon.App.ViewModels;
using HwMon.Client;

namespace HwMon.App.Services;

/// <summary>
/// Este PC sendo adicionado por outro: o painel pergunta ao serviço local, a cada 2 s, se alguém pediu acesso e mostra
/// "Permitir?". Só o painel deste PC consegue responder (o serviço aceita a resposta só pela conexão local), então
/// quem está na frente do computador decide. Também gera o código de pareamento sem o atalho do menu Iniciar.
/// </summary>
public sealed class PairRequestWatcher
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _seen = new();
    private bool _busy;

    public PairRequestWatcher(MainViewModel vm)
    {
        _vm = vm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    /// <summary>A máquina deste PC na lista (o serviço local), se houver.</summary>
    public static MachineViewModel? LocalMachine(MainViewModel vm)
        => vm.Machines.FirstOrDefault(m => string.Equals(m.Descriptor?.HostName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
           ?? vm.Machines.FirstOrDefault(m => m.Machine.Host is "localhost" or "127.0.0.1");

    private async Task TickAsync()
    {
        if (_busy || LocalMachine(_vm) is not { IsConnected: true } local) return;
        _busy = true;
        try
        {
            List<HwMon.Core.Telemetry.PairPending> pending;
            using (var client = CollectorClient.Local(local.Machine))
                pending = await client.PendingPairsAsync();
            foreach (var p in pending.Where(p => _seen.Add(p.Id)))
            {
                // O pedido vale 2 min a partir de quando foi feito; a janela fecha (recusando) antes disso.
                var left = TimeSpan.FromMinutes(2) - (DateTimeOffset.UtcNow - p.Time);
                if (left < TimeSpan.FromSeconds(5)) continue;
                var allow = Views.PairApproveWindow.Ask(p.ClientName + (p.Address is { } ip ? $" ({ip})" : ""), left);
                using var client = CollectorClient.Local(local.Machine);
                await client.AnswerPairAsync(p.Id, allow);
            }
        }
        catch (Exception)
        {
            // serviço antigo (sem pareamento por aceite), parado ou pedido vencido: tenta de novo no próximo ciclo
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Mostra o código de pareamento deste PC (para o caso de o outro painel preferir o código).</summary>
    public static async Task ShowCodeAsync(MainViewModel vm, Window? owner)
    {
        if (LocalMachine(vm) is not { } local)
        {
            MessageBox.Show(L.T("O serviço do Corewise não está instalado neste PC. Instale com a opção Completo ou Só o serviço."), "Corewise", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            using var client = CollectorClient.Local(local.Machine);
            var c = await client.PairCodeAsync();
            var text = L.T("Código: {0}", $"{c.Code[..3]} {c.Code[3..]}") + "   " + L.T("(vale {0:0} minutos, uma vez)", c.Minutes) + "\n\n" +
                       L.T("Este PC: {0}", c.HostName + (c.Addresses.Count > 0 ? "   " + L.T("ou") + "   " + string.Join("  /  ", c.Addresses) : "")) + "\n\n" +
                       L.T("No outro PC: Corewise > Adicionar máquina, digite o nome (ou o endereço) deste PC e o código.");
            if (owner is { IsVisible: true }) MessageBox.Show(owner, text, L.T("Código de pareamento"), MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(text, L.T("Código de pareamento"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (CollectorException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            MessageBox.Show(L.T("Atualize o Corewise neste PC para gerar o código pelo painel. Enquanto isso: Iniciar › Corewise › Código de pareamento."), "Corewise", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Não foi possível gerar o código: {0}", ex.Message), "Corewise", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
