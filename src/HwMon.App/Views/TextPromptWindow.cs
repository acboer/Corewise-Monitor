using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HwMon.App.Views;

/// <summary>Pergunta curta com uma caixa de texto (ex.: renomear um jogo). Usa os estilos do app.</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _box;

    private TextPromptWindow(string title, string message, string value)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Dialog.Background");

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        root.Children.Add(text);
        _box = new TextBox { Text = value, FontSize = 14 };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        };
        root.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = HwMon.Core.L.T("Salvar"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButton");
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = HwMon.Core.L.T("Cancelar"), IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "FlatButton");
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    /// <summary>O texto digitado, ou nulo se cancelou.</summary>
    public static string? Ask(Window? owner, string title, string message, string value)
    {
        var w = new TextPromptWindow(title, message, value);
        if (owner is { IsVisible: true }) w.Owner = owner;
        return w.ShowDialog() == true ? w._box.Text.Trim() : null;
    }
}
