using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HwMon.App.ViewModels;

namespace HwMon.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RestorePlacement();
        Closing += (_, _) => SavePlacement();
        // Processos só são consultados com o painel aberto na página da máquina.
        Loaded += (_, _) => (_vm.Selected as MachineViewModel)?.SetActive(true);
        Closed += (_, _) => (_vm.Selected as MachineViewModel)?.SetActive(false);
        UpdateWidgetButton();
    }

    public void UpdateWidgetButton() => WidgetButtonText.Text = ((App)Application.Current).WidgetVisible ? L.T("Ocultar widget") : L.T("Mostrar widget");

    // ------------------------------------------------------------------ arrastar cartões

    private Point? _dragStart;
    private bool _dragged;

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragged = false;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not Border { DataContext: CardViewModel card } border) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) is not null) return;
        _dragged = true;
        _dragStart = null;
        border.Opacity = 0.5;
        try
        {
            DragDrop.DoDragDrop(border, new DataObject(typeof(CardViewModel), card), DragDropEffects.Move);
        }
        finally
        {
            border.ClearValue(OpacityProperty); // volta ao estilo (cartão oculto continua esmaecido)
        }
    }

    private void ResetTiles_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MachineViewModel m }) m.ResetTiles();
    }

    private void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is MachineViewModel m) m.ResetCardOrder();
    }

    private void Card_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border b && e.Data.GetData(typeof(CardViewModel)) is CardViewModel source && b.DataContext != source)
            b.BorderBrush = (System.Windows.Media.Brush)FindResource("Accent");
    }

    private void Card_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border { DataContext: CardViewModel card } b)
            b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(CardViewModel.Severity))
            {
                Converter = (System.Windows.Data.IValueConverter)FindResource("SeverityToBrush"), ConverterParameter = "Border",
            });
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        Card_DragLeave(sender, e);
        if (sender is Border { DataContext: CardViewModel target } && e.Data.GetData(typeof(CardViewModel)) is CardViewModel source
            && _vm.Selected is MachineViewModel m)
            m.MoveCard(source, target);
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (_dragged)
        {
            _dragged = false;
            return; // foi arrasto, não clique
        }
        // Clique no checkbox do modo Personalizar não expande o cartão.
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) is not null) return;
        if (_vm.IsCustomizing) return;
        if (sender is FrameworkElement { DataContext: CardViewModel card } && _vm.Selected is MachineViewModel m) m.OpenHistory(card);
    }

    private void AddMachine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PairWindow(_vm.Store) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } machine)
            _vm.Selected = _vm.AddMachine(machine);
    }

    private void RemoveMachine_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not MachineViewModel m) return;
        var answer = MessageBox.Show(this,
            L.T("Remover \"{0}\" deste painel?\n\nO Corewise continua instalado na máquina. Para cortar o acesso deste PC lá também, rode nela, como administrador: Corewise.Service.exe revogar", m.Name),
            L.T("Remover máquina"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) _vm.RemoveMachine(m);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _vm.ShowingSettings = true;

    /// <summary>Link do fabricante (resumo do sistema): abre no navegador padrão.</summary>
    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme is "https" or "http")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ------------------------------------------------------------------ aba Sensores: exportar

    private void SensorsText_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"{L.T("Sensores")} {DateTime.Now:yyyy-MM-dd HHmm}.txt", Filter = L.T("Texto (*.txt)|*.txt") };
        if (dialog.ShowDialog(this) == true) _vm.Sensors.ExportText(dialog.FileName);
    }

    /// <summary>PNG da lista inteira (não só a parte visível), com o fundo do tema.</summary>
    private void SensorsImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: FrameworkElement content }) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"{L.T("Sensores")} {DateTime.Now:yyyy-MM-dd HHmm}.png", Filter = L.T("Imagem PNG (*.png)|*.png") };
        if (dialog.ShowDialog(this) != true) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(content);
        var size = new Size(content.ActualWidth, content.ActualHeight);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = (System.Windows.Media.Brush)FindResource("App.Background");
            dc.DrawRectangle(background, null, new Rect(new Point(-16, -16), new Size(size.Width + 32, size.Height + 32)));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(size));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(dialog.FileName);
        encoder.Save(file);
    }

    // ------------------------------------------------------------------ renomear máquina

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is MachineViewModel m) m.BeginRename();
    }

    private void Name_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _vm.Selected is MachineViewModel m) m.BeginRename();
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Selected is not MachineViewModel m) return;
        if (e.Key == Key.Enter) { m.CommitRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { m.CancelRename(); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm.Selected is MachineViewModel { IsRenaming: true } m && !m.CommitRename()) m.CancelRename();
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
            Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Clicar num item da barra lateral (mesmo o já selecionado) sai das Configurações.</summary>
    private void Nav_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || FindParent<ListBoxItem>(d) is not { } item) return;
        _vm.ShowingSettings = false;
        if (item.DataContext is MachineViewModel m) m.CloseHistory(); // clicar na máquina volta para os cartões
    }

    private void ToggleWidget_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ToggleWidget();
        UpdateWidgetButton();
    }

    private void RestorePlacement()
    {
        var p = _vm.Settings.Current.MainWindow;
        if (p.Width is { } w && p.Height is { } h)
        {
            Width = w;
            Height = h;
        }
        if (p.Left is { } l && p.Top is { } t && IsOnScreen(l, t))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var p = _vm.Settings.Current.MainWindow;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        (p.Left, p.Top, p.Width, p.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        p.Maximized = WindowState == WindowState.Maximized;
        _vm.Settings.SaveNow();
    }

    internal static bool IsOnScreen(double left, double top)
        => left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 10
           && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
           && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null and not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
