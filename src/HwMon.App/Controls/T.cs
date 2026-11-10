using System.Windows.Markup;

namespace HwMon.App.Controls;

/// <summary>
/// Texto traduzido direto no XAML, para os casos que a tradução automática não alcança (ex.: Run dentro de
/// Hyperlink num modelo): <c>Text="{controls:T 'Abrir página do fabricante ↗'}"</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
