using System.Windows;
using HwMon.App.ViewModels;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.Services;

/// <summary>
/// Instalação completa: o serviço Corewise roda neste mesmo PC. Na abertura, se ele ainda não está na lista, o painel
/// pareia sozinho (sem código: o serviço aceita pedidos de 127.0.0.1). O serviço pode demorar a subir depois do boot
/// (início atrasado), então tenta por alguns minutos.
/// </summary>
public static class LocalPairing
{
    public static async Task RunAsync(MainViewModel vm)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (await TryOnceAsync(vm)) return;
            }
            catch (Exception)
            {
                // serviço ainda subindo, ou não instalado (só painel)
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt < 6 ? 5 : 15));
        }
    }

    /// <summary>Verdadeiro quando não há mais nada a fazer (já pareado ou acabou de parear).</summary>
    private static async Task<bool> TryOnceAsync(MainViewModel vm)
    {
        using var probe = new CollectorClient("localhost", Protocol.DefaultPort, expectedFingerprint: null, token: null);
        var info = await probe.InfoAsync();
        if (vm.Store.List().Any(m => m.MachineId == info.MachineId)) return true;
        if (probe.PresentedFingerprint != info.CertificateFingerprint) return true;

        using var client = new CollectorClient("localhost", Protocol.DefaultPort, info.CertificateFingerprint, token: null);
        var response = await client.PairAsync(new PairRequest { Code = "", ClientId = vm.Store.ClientId, ClientName = Environment.MachineName });
        var machine = new PairedMachine
        {
            Name = info.HostName,
            Host = "localhost",
            Port = Protocol.DefaultPort,
            MachineId = response.MachineId,
            Fingerprint = info.CertificateFingerprint,
            PairedAt = DateTimeOffset.Now,
        };
        vm.Store.Save(machine, response.Token);
        Application.Current.Dispatcher.Invoke(() => vm.AddMachine(machine));
        return true;
    }
}
