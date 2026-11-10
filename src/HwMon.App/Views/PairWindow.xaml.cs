using System.Windows;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.Views;

/// <summary>Parear pela interface: procurar o coletor, conferir o certificado, digitar o código.</summary>
public partial class PairWindow : Window
{
    private readonly MachineStore _store;
    private string? _host;
    private string? _fingerprint;
    private AgentInfo? _info;

    public PairWindow(MachineStore store)
    {
        InitializeComponent();
        _store = store;
        Loaded += (_, _) => HostBox.Focus();
        CodeExpander.Expanded += (_, _) => { PairButton.Content = L.T("Parear com código"); CodeBox.Focus(); };
        CodeExpander.Collapsed += (_, _) => PairButton.Content = L.T("Pedir acesso");
    }

    public PairedMachine? Result { get; private set; }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) return;
        ShowMessage(null);
        InfoPanel.Visibility = CodePanel.Visibility = Visibility.Collapsed;
        PairButton.IsEnabled = false;
        IsEnabled = false;
        try
        {
            using var client = new CollectorClient(host, Protocol.DefaultPort, expectedFingerprint: null, token: null);
            var info = await client.InfoAsync();
            if (client.PresentedFingerprint != info.CertificateFingerprint)
                throw new CollectorException(L.T("O certificado da conexão não confere com o anunciado pelo serviço."));
            (_host, _fingerprint, _info) = (host, info.CertificateFingerprint, info);
            InfoTitle.Text = info.Role is null ? info.HostName : $"{info.HostName} · {info.Role}";
            InfoDetail.Text = L.T("Corewise {0} respondeu em {1}:{2}.", info.AgentVersion, host, Protocol.DefaultPort);
            InfoFingerprint.Text = Fingerprint.Short(info.CertificateFingerprint);
            NameBox.Text = info.HostName;
            InfoPanel.Visibility = CodePanel.Visibility = Visibility.Visible;
            PairButton.IsEnabled = true;
            PairButton.Focus();
        }
        catch (Exception ex) when (ex is CollectorException or CertificateMismatchException)
        {
            ShowMessage(ex.Message + "\n" + L.T("Confira se o Corewise está instalado nesse PC e se a rede dele está como Privada."));
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private readonly CancellationTokenSource _closing = new();

    protected override void OnClosed(EventArgs e)
    {
        _closing.Cancel();
        base.OnClosed(e);
    }

    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (CodeExpander.IsExpanded) PairWithCode();
        else _ = AskAsync();
    }

    /// <summary>Pede acesso e espera alguém clicar em Permitir no outro PC (até 2 minutos).</summary>
    private async Task AskAsync()
    {
        if (_host is null || _fingerprint is null || _info is null) return;
        ShowMessage(null);
        PairButton.IsEnabled = HostBox.IsEnabled = false;
        try
        {
            using var client = new CollectorClient(_host, Protocol.DefaultPort, _fingerprint, token: null);
            PairAskResponse ask;
            try
            {
                ask = await client.AskPairAsync(new PairRequest { ClientId = _store.ClientId, ClientName = Environment.MachineName }, _closing.Token);
            }
            catch (CollectorException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
            {
                ShowMessage(L.T("O outro PC tem uma versão anterior do Corewise, sem pedido de acesso. Use o código: lá, Iniciar › Corewise › Código de pareamento."));
                CodeExpander.IsExpanded = true;
                return;
            }
            Waiting.Text = L.T("Aguardando… clique em Permitir na janela que apareceu em {0}. O pedido vale 2 minutos.", _info.HostName);
            Waiting.Visibility = Visibility.Visible;
            var until = DateTime.UtcNow.AddMinutes(2.2);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(1500, _closing.Token);
                var status = await client.PairStatusAsync(ask.Id, _closing.Token);
                switch (status.State)
                {
                    case "approved" when status.Token is { } token:
                        Finish(client, ask.MachineId, token);
                        return;
                    case "denied":
                        ShowMessage(L.T("O pedido foi recusado em {0}.", _info.HostName));
                        return;
                    case "expired":
                        ShowMessage(L.T("Ninguém respondeu a tempo. O painel do Corewise precisa estar aberto (ou na bandeja) no outro PC; sem ele, use o código."));
                        CodeExpander.IsExpanded = true;
                        return;
                }
            }
            ShowMessage(L.T("Ninguém respondeu a tempo. O painel do Corewise precisa estar aberto (ou na bandeja) no outro PC; sem ele, use o código."));
            CodeExpander.IsExpanded = true;
        }
        catch (OperationCanceledException)
        {
            // janela fechada
        }
        catch (Exception ex) when (ex is CollectorException or CertificateMismatchException or System.Net.Http.HttpRequestException)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            Waiting.Visibility = Visibility.Collapsed;
            if (!_closing.IsCancellationRequested) PairButton.IsEnabled = HostBox.IsEnabled = true;
        }
    }

    private void Finish(CollectorClient client, string machineId, string token)
    {
        var machine = new PairedMachine
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? _info!.HostName : NameBox.Text.Trim(),
            Host = _host!,
            Port = Protocol.DefaultPort,
            MachineId = machineId,
            Fingerprint = _fingerprint!,
            PairedAt = DateTimeOffset.Now,
            KnownAddresses = client.ConnectedAddress is { } ip ? [ip] : [],
        };
        _store.Save(machine, token);
        Result = machine;
        DialogResult = true;
    }

    private async void PairWithCode()
    {
        if (_host is null || _fingerprint is null || _info is null) return;
        var code = new string(CodeBox.Text.Where(char.IsDigit).ToArray());
        if (code.Length != 6)
        {
            ShowMessage(L.T("O código tem 6 dígitos."));
            return;
        }
        ShowMessage(null);
        IsEnabled = false;
        try
        {
            using var client = new CollectorClient(_host, Protocol.DefaultPort, _fingerprint, token: null);
            var response = await client.PairAsync(new PairRequest { Code = code, ClientId = _store.ClientId, ClientName = Environment.MachineName });
            Finish(client, response.MachineId, response.Token);
        }
        catch (Exception ex) when (ex is CollectorException or CertificateMismatchException)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ShowMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
