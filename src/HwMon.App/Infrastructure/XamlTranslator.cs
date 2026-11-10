using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using HwMon.Core;

namespace HwMon.App.Infrastructure;

/// <summary>
/// Traduz os textos escritos direto no XAML (Text, Content, Header, ToolTip, Title, Run) quando cada elemento é
/// carregado, inclusive os que nascem de modelos (DataTemplate). Só mexe em valores locais simples: textos vindos de
/// Binding são traduzidos no ViewModel. Em português não faz nada.
/// </summary>
public static class XamlTranslator
{
    /// <summary>Elementos já vistos (o SizeChanged volta a cada redimensionamento).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, object> Seen = new();

    public static void Register()
    {
        if (!L.English) return;
        // O Loaded só é disparado para elementos com tratador próprio (otimização do WPF); o SizeChanged chega em
        // todo elemento na primeira medida, antes de desenhar, inclusive os criados por modelos e menus.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((s, args) =>
        {
            var element = (DependencyObject)s;
            if (Seen.TryGetValue(element, out var seen)) return;
            Seen.Add(element, Seen);
            Translate(element);
        }), true);
    }

    private static readonly DependencyProperty[] Properties =
    [
        TextBlock.TextProperty, ContentControl.ContentProperty, HeaderedContentControl.HeaderProperty,
        HeaderedItemsControl.HeaderProperty, FrameworkElement.ToolTipProperty, Window.TitleProperty, FrameworkElement.TagProperty,
    ];

    private static void Translate(DependencyObject element)
    {
        foreach (var p in Properties) TranslateProperty(element, p);
        if (element is TextBlock { Inlines.Count: > 0 } tb) TranslateInlines(tb.Inlines);
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines.ToList()) // trocar o texto de um Run mexe na coleção
        {
            if (inline is Run run) TranslateProperty(run, Run.TextProperty);
            else if (inline is Span span) TranslateInlines(span.Inlines);
        }
    }

    private static void TranslateProperty(DependencyObject element, DependencyProperty property)
    {
        if (!property.OwnerType.IsInstanceOfType(element) && !IsAttachedTo(element, property)) return;
        // Só texto fixo: binding, recurso ou estilo não são valores locais do tipo string.
        if (element.ReadLocalValue(property) is not string text || text.Length < 2) return;
        var trimmed = text.Trim();
        if (!L.Has(trimmed)) return;
        var translated = L.T(trimmed);
        if (!ReferenceEquals(translated, trimmed) && translated != trimmed)
            element.SetCurrentValue(property, text.Replace(trimmed, translated));
    }

    private static bool IsAttachedTo(DependencyObject element, DependencyProperty property)
        => (property == FrameworkElement.ToolTipProperty || property == FrameworkElement.TagProperty) && element is FrameworkElement;
}
