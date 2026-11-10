using System.Net;
using System.Text;
using System.Windows;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>Seção do resumo com o ícone da identidade.</summary>
public sealed class SummarySectionView(SummarySection section)
{
    public string Title => section.Title;
    public string? Headline => section.Headline;
    public List<SummaryGroup> Groups => section.Groups;
    public string Icon => section.Id switch
    {
        "os" => "machine-workstation",
        "board" => "nav-overview",
        "cpu" => "kind-cpu",
        "memory" => "kind-ram",
        "gpu" => "kind-gpu",
        "storage" => "kind-storage",
        "network" => "kind-network",
        "display" => "action-widget",
        "audio" => "metric-usage",
        "power" => "metric-power",
        _ => "nav-overview",
    };
}

/// <summary>
/// Aba "Sistema" de uma máquina: resumo estilo CPU-Z lido do coletor (/v1/system) na primeira vez que a aba abre.
/// </summary>
public sealed class SystemPageViewModel : ObservableObject
{
    private readonly PairedMachine _machine;
    private SystemSummary? _summary;
    private bool _isLoading;
    private string? _error;
    private string? _copied;

    public SystemPageViewModel(PairedMachine machine)
    {
        _machine = machine;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(refresh: true));
        CopyCommand = new RelayCommand(Copy);
    }

    public List<SummarySectionView> Sections { get; private set; } = new();

    /// <summary>O serviço manda o resumo em português: traduz rótulos, títulos e valores conhecidos.</summary>
    private static SystemSummary Translate(SystemSummary summary)
    {
        foreach (var s in summary.Sections)
        {
            s.Title = L.T(s.Title);
            if (s.Headline is not null) s.Headline = L.T(s.Headline);
            foreach (var g in s.Groups)
            {
                if (g.Title is not null) g.Title = L.T(g.Title);
                foreach (var i in g.Items) (i.Label, i.Value) = (L.T(i.Label), L.T(i.Value));
            }
        }
        return summary;
    }
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) Raise(nameof(StatusText)); } }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public bool HasData => _summary is not null;
    public string? CopiedText { get => _copied; private set => Set(ref _copied, value); }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyCommand { get; }

    public string StatusText => _isLoading ? L.T("Lendo as informações da máquina...")
        : _summary is null ? ""
        : L.T("Lido em {0}", Format.When(_summary.CollectedAt));

    /// <summary>Carrega na primeira vez que a aba abre.</summary>
    public void EnsureLoaded()
    {
        if (_summary is null && !_isLoading) _ = LoadAsync(refresh: false);
    }

    private async Task LoadAsync(bool refresh)
    {
        if (_isLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            using var client = CollectorClient.For(_machine);
            _summary = Translate(await client.SystemAsync(refresh));
            Sections = _summary.Sections.Select(s => new SummarySectionView(s)).ToList();
            Raise(nameof(Sections));
            Raise(nameof(HasData));
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            Error = L.T("O serviço desta máquina é de uma versão anterior e não tem o resumo do sistema. Atualize o Corewise nela (o pareamento continua valendo).");
        }
        catch (Exception ex)
        {
            Error = L.T("Não foi possível ler o resumo: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Texto simples para colar num fórum, chamado de suporte ou anotação.</summary>
    private void Copy()
    {
        if (_summary is null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"{_machine.Name} ({_summary.HostName}) — {StatusText}");
        foreach (var section in _summary.Sections)
        {
            sb.AppendLine();
            sb.AppendLine($"== {section.Title}{(section.Headline is null ? "" : $" — {section.Headline}")}");
            foreach (var group in section.Groups)
            {
                if (group.Title is not null) sb.AppendLine($"[{group.Title}]");
                var width = group.Items.Max(i => i.Label.Length) + 2;
                foreach (var item in group.Items) sb.AppendLine($"  {item.Label.PadRight(width)}{item.Value}");
            }
        }
        try
        {
            Clipboard.SetText(sb.ToString());
            CopiedText = L.T("Copiado");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CopiedText = L.T("Área de transferência ocupada; tente de novo");
        }
        _ = Task.Delay(2500).ContinueWith(_ => CopiedText = null, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
