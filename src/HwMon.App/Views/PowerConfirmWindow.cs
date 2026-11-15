using System.Windows;
using System.Windows.Controls;

namespace HwMon.App.Views;

/// <summary>
/// "Desligar NAS?" com a escolha de quando: agora, ou em 1 minuto (o Windows avisa quem estiver usando aquele PC e
/// dá para cancelar pelo painel). Devolve a espera em segundos, ou nulo se cancelou.
/// </summary>
public sealed class PowerConfirmWindow : Window
{
    private readonly RadioButton _later;

    private PowerConfirmWindow(string action, string machine, bool thisPc)
    {
        var (title, verb) = action switch
        {
            "shutdown" => (L.T("Desligar {0}?", machine), L.T("Desligar")),
            "restart" => (L.T("Reiniciar {0}?", machine), L.T("Reiniciar")),
            _ => (L.T("Suspender {0}?", machine), L.T("Suspender")),
        };
        Title = "Corewise";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Dialog.Background");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); }
        catch (Exception) { /* sem ícone */ }

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock
        {
            Text = thisPc
                ? L.T("É este PC. Salve o que estiver aberto: o Windows fecha os programas no fim da espera.")
                : action == "sleep"
                    ? L.T("Dá para acordar depois com Ligar este PC (Wake-on-LAN), se a placa de rede estiver configurada.")
                    : L.T("Programas abertos naquele PC são fechados no fim da espera; trabalho não salvo se perde."),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 8, 0, 4),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        root.Children.Add(detail);

        var now = new RadioButton { Content = L.T("Agora"), GroupName = "quando", Margin = new Thickness(0, 10, 0, 0), FontSize = 13 };
        _later = new RadioButton
        {
            // Em TextBlock com quebra: o texto longo era cortado na borda da janela.
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = action == "sleep"
                    ? L.T("Em 1 minuto (dá para cancelar pelo painel)")
                    : L.T("Em 1 minuto (o Windows avisa quem estiver usando o PC; dá para cancelar pelo painel)"),
            },
            GroupName = "quando", Margin = new Thickness(0, 6, 0, 0), FontSize = 13, IsChecked = true,
        };
        root.Children.Add(now);
        root.Children.Add(_later);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var confirm = new Button { Content = verb, Margin = new Thickness(0, 0, 8, 0) };
        confirm.SetResourceReference(StyleProperty, "AccentButton");
        confirm.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = L.T("Cancelar"), IsCancel = true, IsDefault = true };
        cancel.SetResourceReference(StyleProperty, "FlatButton");
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Espera escolhida (0 ou 60 s), ou nulo se o usuário desistiu.</summary>
    public static int? Ask(Window owner, string action, string machine, bool thisPc)
    {
        var w = new PowerConfirmWindow(action, machine, thisPc) { Owner = owner };
        return w.ShowDialog() == true ? (w._later.IsChecked == true ? 60 : 0) : null;
    }
}
