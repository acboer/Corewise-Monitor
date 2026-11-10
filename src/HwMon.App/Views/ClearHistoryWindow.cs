using System.Windows;
using System.Windows.Controls;

namespace HwMon.App.Views;

/// <summary>Limpar o histórico: o quê (uso e sensores, jogos) e de qual máquina. Usa os estilos do app.</summary>
public sealed class ClearHistoryWindow : Window
{
    public sealed record Choice(bool Sensors, bool Games, string? Machine);

    private readonly CheckBox _sensors, _games;
    private readonly ComboBox _machine;
    private readonly Button _ok;

    private ClearHistoryWindow(IReadOnlyList<string> machines)
    {
        Title = L.T("Limpar histórico");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Dialog.Background");

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        var intro = new TextBlock
        {
            Text = L.T("Apaga o que já foi gravado e recomeça do zero a partir de agora. Não dá para desfazer."),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 14),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        root.Children.Add(intro);

        _sensors = new CheckBox { Content = L.T("Uso e sensores (gráficos, tempo ligada, energia, temperaturas, rede e disco)"), IsChecked = true, FontSize = 13 };
        _games = new CheckBox { Content = L.T("Jogos (sessões, FPS e tempo jogado; os nomes escolhidos e os programas ignorados ficam)"), IsChecked = true, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var box in new[] { _sensors, _games })
        {
            if (box.Content is string s) box.Content = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap };
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
            root.Children.Add(box);
        }

        var label = new TextBlock { Text = L.T("Máquina"), FontSize = 13, Margin = new Thickness(0, 16, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        root.Children.Add(label);
        _machine = new ComboBox { FontSize = 13 };
        _machine.Items.Add(L.T("Todas as máquinas"));
        foreach (var m in machines) _machine.Items.Add(m);
        _machine.SelectedIndex = 0;
        root.Children.Add(_machine);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _ok = new Button { Content = L.T("Limpar"), Margin = new Thickness(0, 0, 8, 0) };
        _ok.SetResourceReference(StyleProperty, "AccentButton");
        _ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = L.T("Cancelar"), IsCancel = true, IsDefault = true };
        cancel.SetResourceReference(StyleProperty, "FlatButton");
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    private void Update() => _ok.IsEnabled = _sensors.IsChecked == true || _games.IsChecked == true;

    /// <summary>O que limpar, ou nulo se cancelou.</summary>
    public static Choice? Ask(Window? owner, IReadOnlyList<string> machines)
    {
        var w = new ClearHistoryWindow(machines);
        if (owner is { IsVisible: true }) w.Owner = owner;
        if (w.ShowDialog() != true) return null;
        var machine = w._machine.SelectedIndex <= 0 ? null : (string)w._machine.SelectedItem;
        var what = new[] { w._sensors.IsChecked == true ? L.T("uso e sensores") : null, w._games.IsChecked == true ? L.T("jogos") : null }.OfType<string>();
        var confirm = MessageBox.Show(owner!, L.T("Apagar {0} de {1}?\n\nNão dá para desfazer.", string.Join(L.T(" e "), what), machine ?? L.T("todas as máquinas")),
            L.T("Limpar histórico"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return confirm == MessageBoxResult.OK ? new Choice(w._sensors.IsChecked == true, w._games.IsChecked == true, machine) : null;
    }
}
