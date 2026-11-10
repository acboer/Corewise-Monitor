using System.Windows;
using System.Windows.Controls;

namespace HwMon.App.Views;

/// <summary>"Fulano quer acompanhar este PC. Permitir?" — aparece por cima de tudo quando outro painel pede acesso.</summary>
public sealed class PairApproveWindow : Window
{
    private PairApproveWindow(string who)
    {
        Title = L.T("Corewise · pedido de acesso");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowActivated = true;
        SetResourceReference(BackgroundProperty, "Dialog.Background");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); }
        catch (Exception) { /* sem ícone */ }

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        var title = new TextBlock { Text = L.T("{0} quer acompanhar este PC.", who), TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = FontWeights.SemiBold };
        root.Children.Add(title);
        var detail = new TextBlock
        {
            Text = L.T("Ele vai poder ver sensores, uso e jogos deste PC e rodar benchmarks aqui. Dá para revogar depois.") + "\n\n" + L.T("Se você não pediu isso, recuse."),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 8, 0, 0),
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        root.Children.Add(detail);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var allow = new Button { Content = L.T("Permitir"), Margin = new Thickness(0, 0, 8, 0) };
        allow.SetResourceReference(StyleProperty, "AccentButton");
        allow.Click += (_, _) => DialogResult = true;
        var deny = new Button { Content = L.T("Recusar"), IsCancel = true, IsDefault = true };
        deny.SetResourceReference(StyleProperty, "FlatButton");
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Verdadeiro se clicou em Permitir. Fecha sozinha (recusa) depois de <paramref name="timeout"/>.</summary>
    public static bool Ask(string who, TimeSpan timeout)
    {
        var w = new PairApproveWindow(who);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = timeout };
        timer.Tick += (_, _) => { timer.Stop(); if (w.IsVisible) w.DialogResult = false; };
        timer.Start();
        w.Loaded += (_, _) => w.Activate();
        var result = w.ShowDialog() == true;
        timer.Stop();
        return result;
    }
}
