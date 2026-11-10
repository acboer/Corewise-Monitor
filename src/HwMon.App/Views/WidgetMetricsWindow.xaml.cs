using System.Windows;
using System.Windows.Data;
using HwMon.App.ViewModels;

namespace HwMon.App.Views;

public partial class WidgetMetricsWindow : Window
{
    private readonly WidgetViewModel _widget;
    private readonly MachineViewModel _machine;
    private readonly List<MetricOption> _options;

    public WidgetMetricsWindow(WidgetViewModel widget, MachineViewModel machine)
    {
        InitializeComponent();
        _widget = widget;
        _machine = machine;
        Heading.Text = $"Widget · {machine.Name}";
        _options = widget.Options(machine);
        var view = new ListCollectionView(_options);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MetricOption.Group)));
        List.ItemsSource = view;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _widget.SaveKeys(_machine, _options.Where(o => o.Selected).Select(o => o.Key));
        DialogResult = true;
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        _widget.ResetKeys(_machine);
        DialogResult = true;
    }
}
