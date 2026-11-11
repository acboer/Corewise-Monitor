using System.Collections.ObjectModel;
using HwMon.App.Infrastructure;

namespace HwMon.App.ViewModels;

public enum Severity { Normal, Caution, Critical }

/// <summary>Uma linha "rótulo · valor" dentro de um cartão.</summary>
public sealed class MetricLine(string componentId, string metric, string label) : ObservableObject
{
    private string _value = "—";
    private bool _isStale;
    private bool _hasValue;
    private string? _note;
    private Severity _severity;

    public string ComponentId { get; } = componentId;
    public string Metric { get; } = metric;
    public string Label { get; } = label;
    public string Value { get => _value; set => Set(ref _value, value); }
    public bool IsStale { get => _isStale; set => Set(ref _isStale, value); }
    public bool HasValue { get => _hasValue; set => Set(ref _hasValue, value); }
    public string? Note { get => _note; set => Set(ref _note, value); }
    public Severity Severity { get => _severity; set => Set(ref _severity, value); }
}

/// <summary>Volume dentro do cartão do disco: nome, barra de ocupação e texto.</summary>
public sealed class VolumeItem(string componentId, string name) : ObservableObject
{
    private double _fraction;
    private string _text = "";
    private bool _isStale;
    private bool _isVisible = true;

    public string ComponentId { get; } = componentId;
    public string Name { get; } = name;
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public bool IsStale { get => _isStale; set => Set(ref _isStale, value); }
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
}

/// <summary>
/// Cartão no estilo dos painéis de NAS: valor principal em destaque, gráfico curto e detalhes.
/// Os itens (linhas, volumes) são criados uma vez por descritor e só têm os valores atualizados a cada segundo.
/// </summary>
public sealed class CardViewModel : ObservableObject
{
    private string _mainValue = "—";
    private string? _mainCaption;
    private double[] _history = [];
    private double _historyMax = 100;
    private bool _isHidden;
    private bool _isExpanded;
    private Severity _severity;
    private bool _mainStale;

    public CardViewModel(string componentId, string kind, string badge, string title, string? subtitle)
    {
        ComponentId = componentId;
        Kind = kind;
        Badge = badge;
        Title = title;
        Subtitle = subtitle;
    }

    public string ComponentId { get; }
    public string Kind { get; }
    /// <summary>Etiqueta curta no canto (CPU, GPU, HDD, SSD, NET...).</summary>
    public string Badge { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    /// <summary>Componentes extras representados neste cartão (módulos de RAM, ventoinhas).</summary>
    public List<string> ExtraComponentIds { get; } = new();

    public ObservableCollection<MetricLine> Lines { get; } = new();
    public ObservableCollection<MetricLine> DetailLines { get; } = new();
    public ObservableCollection<VolumeItem> Volumes { get; } = new();

    public string MainValue
    {
        get => _mainValue;
        set
        {
            if (!Set(ref _mainValue, value)) return;
            Raise(nameof(MainNumber));
            Raise(nameof(MainUnit));
        }
    }

    /// <summary>"12,5 %" → "12,5" (número grande) e "%" (unidade menor ao lado).</summary>
    public string MainNumber => SplitValue(_mainValue).Number;
    public string? MainUnit => SplitValue(_mainValue).Unit;

    private static (string Number, string? Unit) SplitValue(string text)
    {
        var space = text.IndexOf(' ');
        return space > 0 && (char.IsDigit(text[0]) || text[0] == '-') ? (text[..space], text[(space + 1)..]) : (text, null);
    }

    /// <summary>Nome do ícone (icons/*.svg) do tipo do componente.</summary>
    public string Icon { get; init; } = "kind-storage";

    /// <summary>Ocupa duas colunas por padrão (CPU e GPU principal da workstation).</summary>
    public bool BaseWide { get; init; }

    /// <summary>Largo na grade: os principais e qualquer um em atenção ou crítico.</summary>
    public bool IsWide => BaseWide || _severity == Severity.Critical;
    public int Span => IsWide ? 2 : 1;

    /// <summary>
    /// Só o crítico (vermelho) muda a posição: sobe enquanto durar. Atenção (amarelo) só muda a cor e fica onde o
    /// usuário arrastou (ex.: HD externo quase cheio o tempo todo não pode ficar preso no topo).
    /// </summary>
    public int Priority => _severity == Severity.Critical ? 3 : 0;
    public string? MainCaption { get => _mainCaption; set => Set(ref _mainCaption, value); }
    public bool MainStale { get => _mainStale; set => Set(ref _mainStale, value); }
    public double[] History { get => _history; set => Set(ref _history, value); }
    public double HistoryMax { get => _historyMax; set => Set(ref _historyMax, value); }
    public bool IsHidden { get => _isHidden; set => Set(ref _isHidden, value); }

    private string? _statsText;
    /// <summary>"24 h · mín 4 % · méd 5 % · máx 11 %" (do histórico do coletor).</summary>
    public string? StatsText { get => _statsText; set => Set(ref _statsText, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public Severity Severity
    {
        get => _severity;
        set
        {
            if (!Set(ref _severity, value)) return;
            Raise(nameof(IsWide));
            Raise(nameof(Span));
            Raise(nameof(Priority));
        }
    }

    /// <summary>Linha principal usada para o valor grande e o gráfico.</summary>
    public (string ComponentId, string Metric)? MainMetric { get; init; }
}

/// <summary>Bloco de resumo no cartão da visão geral.</summary>
public sealed class TileViewModel(string title, string icon) : ObservableObject
{
    private string _value = "—";
    private string? _sub;
    private double? _fraction;
    private Severity _severity;

    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public string Value { get => _value; set => Set(ref _value, value); }
    public string? Sub { get => _sub; set => Set(ref _sub, value); }
    public double? Fraction { get => _fraction; set => Set(ref _fraction, value); }
    public Severity Severity { get => _severity; set => Set(ref _severity, value); }
    /// <summary>Cartão (ComponentId) aberto ao clicar no bloco da Visão geral; nulo abre só a página da máquina.</summary>
    public string? Target { get; set; }
}
