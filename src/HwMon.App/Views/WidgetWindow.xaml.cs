using System.Windows;
using System.Windows.Input;
using HwMon.App.Services;
using HwMon.App.ViewModels;

namespace HwMon.App.Views;

public partial class WidgetWindow : Window
{
    private readonly WidgetViewModel _vm;

    /// <param name="index">Posição na fila dos widgets por PC, para a primeira vez não ficarem um em cima do outro.</param>
    public WidgetWindow(WidgetViewModel vm, SettingsStore settings, int index = 0)
    {
        InitializeComponent();
        _vm = vm;
        vm.Attach();
        DataContext = vm;

        var s = settings.Current.Widget;
        // Widget único: a posição de sempre. Um por PC: cada máquina guarda a sua.
        var key = vm.Fixed?.MachineId;
        double? left = key is null ? s.Left : s.Positions.GetValueOrDefault(key)?.Left;
        double? top = key is null ? s.Top : s.Positions.GetValueOrDefault(key)?.Top;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (left is { } l && top is { } t && MainWindow.IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Primeira vez: canto superior direito da área de trabalho (os seguintes à esquerda do anterior).
            Left = SystemParameters.WorkArea.Right - (Width + 8) * (index + 1) - 8;
            Top = SystemParameters.WorkArea.Top + 16;
        }
        LocationChanged += (_, _) =>
        {
            if (key is null)
            {
                s.Left = Left;
                s.Top = Top;
            }
            else s.Positions[key] = new WidgetPosition { Left = Left, Top = Top };
            settings.SaveSoon();
        };
        Closed += (_, _) => vm.Detach();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OpenPanel_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowMain();

    private void Close_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).HideWidget();

    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Current?.Descriptor is null) return;
        var dialog = new WidgetMetricsWindow(_vm, _vm.Current) { Owner = this, Topmost = Topmost };
        dialog.ShowDialog();
    }
}
