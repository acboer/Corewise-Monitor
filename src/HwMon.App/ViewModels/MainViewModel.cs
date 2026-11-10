using System.Collections.ObjectModel;
using System.Windows.Threading;
using HwMon.App.Infrastructure;
using HwMon.App.Services;
using HwMon.Client;

namespace HwMon.App.ViewModels;

/// <summary>Item fixo "Visão geral" na barra lateral.</summary>
public sealed class OverviewViewModel(ObservableCollection<MachineViewModel> machines) : ObservableObject
{
    public ObservableCollection<MachineViewModel> Machines { get; } = machines;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _clock;
    private object? _selected;
    private bool _isCustomizing;
    private bool _showingSettings;

    public MainViewModel(SettingsStore settings, MachineStore store)
    {
        Settings = settings;
        Store = store;
        Overview = new OverviewViewModel(Machines);
        Sensors = new SensorsViewModel(Machines);
        NavItems.Add(Overview);
        NavItems.Add(Sensors); // as máquinas entram entre a Visão geral e Sensores
        HistoryHub = new HistoryHubViewModel(Machines);
        NavItems.Add(HistoryHub);
        foreach (var m in store.List()) AddMachine(m);
        Widget = new WidgetViewModel(Machines, settings);
        SettingsPage = new SettingsViewModel(settings, Widget);
        Selected = Overview;

        OpenCommand = new RelayCommand(p => { if (p is MachineViewModel m && !IsCustomizingOverview) Selected = m; });
        ToggleCustomizeCommand = new RelayCommand(() => IsCustomizing = !IsCustomizing);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => { foreach (var m in Machines) m.RefreshAge(); };
        _clock.Start();
    }

    public SettingsStore Settings { get; }
    public MachineStore Store { get; }
    public OverviewViewModel Overview { get; }
    public SensorsViewModel Sensors { get; }
    public HistoryHubViewModel HistoryHub { get; }
    public WidgetViewModel Widget { get; }
    public ObservableCollection<MachineViewModel> Machines { get; } = new();
    public ObservableCollection<object> NavItems { get; } = new();
    public RelayCommand OpenCommand { get; }
    public RelayCommand ToggleCustomizeCommand { get; }

    public SettingsViewModel SettingsPage { get; }

    private bool _isCustomizingOverview;
    /// <summary>Visão geral em modo Personalizar: escolher e ordenar os blocos de cada máquina.</summary>
    public bool IsCustomizingOverview { get => _isCustomizingOverview; set => Set(ref _isCustomizingOverview, value); }

    public object? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (Set(ref _selected, value ?? Overview))
            {
                IsCustomizing = false;
                IsCustomizingOverview = false;
                (previous as MachineViewModel)?.SetActive(false);
                (_selected as MachineViewModel)?.SetActive(true);
            }
            ShowingSettings = false;
            HistoryHub.SetActive(_selected == HistoryHub);
            Raise(nameof(Page));
        }
    }

    /// <summary>Configurações fica fora da lista da barra lateral (botão no rodapé).</summary>
    public bool ShowingSettings
    {
        get => _showingSettings;
        set
        {
            if (!Set(ref _showingSettings, value)) return;
            if (value)
            {
                IsCustomizing = false;
                SettingsPage.Refresh();
            }
            HistoryHub.SetActive(!value && _selected == HistoryHub);
            Raise(nameof(Page));
        }
    }

    /// <summary>O que aparece na área principal.</summary>
    public object? Page => _showingSettings ? SettingsPage : _selected;

    public bool IsCustomizing
    {
        get => _isCustomizing;
        set
        {
            if (!Set(ref _isCustomizing, value)) return;
            foreach (var m in Machines) m.IsCustomizing = value && m == _selected;
        }
    }

    public MachineViewModel AddMachine(PairedMachine machine)
    {
        var existing = Machines.FirstOrDefault(m => m.MachineId == machine.MachineId);
        if (existing is not null) RemoveMachine(existing, forget: false);
        var vm = new MachineViewModel(machine, Settings, Store);
        Machines.Add(vm);
        NavItems.Insert(NavItems.IndexOf(Sensors), vm);
        vm.Start();
        return vm;
    }

    public void RemoveMachine(MachineViewModel vm, bool forget = true)
    {
        vm.Stop();
        Machines.Remove(vm);
        NavItems.Remove(vm);
        if (forget) Store.Remove(vm.Name);
        if (_selected == vm) Selected = Overview;
    }

    public void Shutdown()
    {
        _clock.Stop();
        foreach (var m in Machines) m.Stop();
        Settings.SaveNow();
    }
}
