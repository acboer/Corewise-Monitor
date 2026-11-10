using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using HwMon.App.Infrastructure;
using HwMon.App.Services;
using HwMon.Core;

namespace HwMon.App.ViewModels;

/// <summary>Página Configurações: início com o Windows, bandeja, widget e tema. Cada mudança grava na hora.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly WidgetViewModel _widget;
    private string? _startupError;

    public SettingsViewModel(SettingsStore settings, WidgetViewModel widget)
    {
        _settings = settings;
        _widget = widget;
        widget.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WidgetViewModel.Topmost)) Raise(nameof(WidgetTopmost));
        };
        OpenDataFolderCommand = new RelayCommand(() => Open(HwMon.Core.AppPaths.UserData));
        ShowPairCodeCommand = new RelayCommand(() => _ = PairRequestWatcher.ShowCodeAsync(App.ViewModel, Application.Current.MainWindow));
    }

    private static App App => (App)Application.Current;
    private StartupSettings Startup => _settings.Current.Startup;

    // ------------------------------------------------------------------ inicialização

    public bool StartWithWindows
    {
        get => StartupService.IsEnabled;
        set
        {
            try
            {
                StartupService.SetEnabled(value);
                Startup.Configured = true;
                _settings.SaveSoon();
                StartupError = null;
            }
            catch (Exception ex)
            {
                StartupError = L.T("Não foi possível alterar: ") + ex.Message;
            }
            Raise();
        }
    }

    public string? StartupError { get => _startupError; private set => Set(ref _startupError, value); }

    public bool ModeWidget { get => Startup.Mode == StartupMode.Widget; set { if (value) SetMode(StartupMode.Widget); } }
    public bool ModePanelAndWidget { get => Startup.Mode == StartupMode.PanelAndWidget; set { if (value) SetMode(StartupMode.PanelAndWidget); } }
    public bool ModeTray { get => Startup.Mode == StartupMode.Tray; set { if (value) SetMode(StartupMode.Tray); } }

    private void SetMode(StartupMode mode)
    {
        Startup.Mode = mode;
        _settings.SaveSoon();
        Raise(nameof(ModeWidget));
        Raise(nameof(ModePanelAndWidget));
        Raise(nameof(ModeTray));
    }

    public bool CloseToTray
    {
        get => Startup.CloseToTray;
        set
        {
            Startup.CloseToTray = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool IsDevBuild => StartupService.IsDevBuild;
    public string ExePath => StartupService.ExePath;

    // ------------------------------------------------------------------ widget

    public bool WidgetVisible
    {
        get => App.WidgetVisible;
        set
        {
            if (value == App.WidgetVisible) return;
            App.ToggleWidget();
            Raise();
        }
    }

    public bool WidgetTopmost { get => _widget.Topmost; set => _widget.Topmost = value; }

    /// <summary>O próprio widget (transparência do fundo).</summary>
    public WidgetViewModel Widget => _widget;

    // ------------------------------------------------------------------ modo jogo

    private GameModeSettings Game => _settings.Current.GameMode;

    private void SetGame(Action<GameModeSettings> change, params string[] names)
    {
        change(Game);
        _settings.SaveSoon();
        foreach (var n in names) Raise(n);
    }

    public bool GameAuto { get => Game.Mode == "auto"; set { if (value) SetGame(g => g.Mode = "auto", nameof(GameAuto), nameof(GameHotkey), nameof(GameOff)); } }
    public bool GameHotkey { get => Game.Mode == "hotkey"; set { if (value) SetGame(g => g.Mode = "hotkey", nameof(GameAuto), nameof(GameHotkey), nameof(GameOff)); } }
    public bool GameOff { get => Game.Mode == "off"; set { if (value) SetGame(g => g.Mode = "off", nameof(GameAuto), nameof(GameHotkey), nameof(GameOff)); } }
    public bool GameHideWidget { get => Game.HideWidget; set => SetGame(g => g.HideWidget = value, nameof(GameHideWidget)); }
    public bool GameShowGraph { get => Game.ShowGraph; set => SetGame(g => g.ShowGraph = value, nameof(GameShowGraph)); }
    public bool CornerTopLeft { get => Game.Corner == "top-left"; set { if (value) SetCorner("top-left"); } }
    public bool CornerTopRight { get => Game.Corner == "top-right"; set { if (value) SetCorner("top-right"); } }
    public bool CornerBottomLeft { get => Game.Corner == "bottom-left"; set { if (value) SetCorner("bottom-left"); } }
    public bool CornerBottomRight { get => Game.Corner == "bottom-right"; set { if (value) SetCorner("bottom-right"); } }
    public string GameHotkeyText => string.IsNullOrWhiteSpace(Game.Hotkey)
        ? L.T("Sem atalho: o overlay só aparece sozinho, no modo automático.")
        : L.T("{0} mostra ou esconde o overlay a qualquer momento (também em jogos em janela).", Game.Hotkey);

    // ------------------------------------------------------------------ atalhos

    private string? _gameHotkeyError, _widgetHotkeyError;

    public string GameHotkeyKeys
    {
        get => Game.Hotkey;
        set
        {
            Game.Hotkey = value ?? "";
            _settings.SaveSoon();
            ApplyHotkeys();
            Raise();
            Raise(nameof(GameHotkeyText));
        }
    }

    public string WidgetHotkeyKeys
    {
        get => _settings.Current.Widget.Hotkey;
        set
        {
            _settings.Current.Widget.Hotkey = value ?? "";
            _settings.SaveSoon();
            ApplyHotkeys();
            Raise();
        }
    }

    public string? GameHotkeyError { get => _gameHotkeyError; private set => Set(ref _gameHotkeyError, value); }
    public string? WidgetHotkeyError { get => _widgetHotkeyError; private set => Set(ref _widgetHotkeyError, value); }

    private void ApplyHotkeys() => (GameHotkeyError, WidgetHotkeyError) = App.ApplyHotkeys();

    // ------------------------------------------------------------------ widget: um ou um por PC, quais PCs

    public bool WidgetSingle { get => !_settings.Current.Widget.PerMachine; set { if (value) SetPerMachine(false); } }
    public bool WidgetPerMachine { get => _settings.Current.Widget.PerMachine; set { if (value) SetPerMachine(true); } }

    private void SetPerMachine(bool perMachine)
    {
        _settings.Current.Widget.PerMachine = perMachine;
        _settings.SaveSoon();
        Raise(nameof(WidgetSingle));
        Raise(nameof(WidgetPerMachine));
        App.RebuildWidgets();
    }

    /// <summary>PCs que aparecem no widget (marcar/desmarcar refaz os widgets abertos).</summary>
    public ObservableCollection<WidgetMachineOption> WidgetMachines { get; } = new();

    private void FillWidgetMachines()
    {
        WidgetMachines.Clear();
        var chosen = _settings.Current.Widget.Machines;
        foreach (var m in App.ViewModel.Machines)
        {
            var option = new WidgetMachineOption(m.MachineId, m.Name, chosen is null || chosen.Contains(m.MachineId));
            option.PropertyChanged += (_, _) => SaveWidgetMachines();
            WidgetMachines.Add(option);
        }
    }

    private void SaveWidgetMachines()
    {
        var picked = WidgetMachines.Where(o => o.IsSelected).Select(o => o.MachineId).ToList();
        // Todas marcadas = "todas" (as que forem pareadas depois entram sozinhas).
        _settings.Current.Widget.Machines = picked.Count == WidgetMachines.Count ? null : picked;
        _settings.SaveSoon();
        App.RebuildWidgets();
    }

    private void SetCorner(string corner) => SetGame(g => g.Corner = corner,
        nameof(CornerTopLeft), nameof(CornerTopRight), nameof(CornerBottomLeft), nameof(CornerBottomRight));

    // ------------------------------------------------------------------ aparência

    public bool ThemeSystem { get => _settings.Current.Theme == "system"; set { if (value) SetTheme("system"); } }
    public bool ThemeLight { get => _settings.Current.Theme == "light"; set { if (value) SetTheme("light"); } }
    public bool ThemeDark { get => _settings.Current.Theme == "dark"; set { if (value) SetTheme("dark"); } }

    private void SetTheme(string theme)
    {
        _settings.Current.Theme = theme;
        _settings.SaveSoon();
        App.ApplyTheme(theme);
        Raise(nameof(ThemeSystem));
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
    }

    // ------------------------------------------------------------------ idioma

    public bool LangAuto { get => _settings.Current.Language is not ("pt" or "en"); set { if (value) SetLanguage("auto"); } }
    public bool LangPt { get => _settings.Current.Language == "pt"; set { if (value) SetLanguage("pt"); } }
    public bool LangEn { get => _settings.Current.Language == "en"; set { if (value) SetLanguage("en"); } }

    /// <summary>O idioma escolhido é diferente do que está aparecendo: precisa reabrir.</summary>
    public bool LanguagePending => (_settings.Current.Language is "pt" or "en" ? _settings.Current.Language : HwMon.Core.L.Detect()) != HwMon.Core.L.Language;
    public RelayCommand RestartCommand { get; } = new(() => App.Restart());

    private void SetLanguage(string lang)
    {
        _settings.Current.Language = lang;
        _settings.SaveNow();
        Raise(nameof(LangAuto));
        Raise(nameof(LangPt));
        Raise(nameof(LangEn));
        Raise(nameof(LanguagePending));
    }

    // ------------------------------------------------------------------ sobre

    public string Version => L.T("Versão") + " " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand ShowPairCodeCommand { get; }

    private static void Open(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>Relê o que pode ter mudado por fora (registro, widget fechado pelo X ou pela bandeja).</summary>
    public void Refresh()
    {
        FillWidgetMachines();
        ApplyHotkeys();
        Raise(nameof(StartWithWindows));
        Raise(nameof(WidgetVisible));
        Raise(nameof(WidgetTopmost));
    }
}

/// <summary>Um PC na lista "mostrar no widget".</summary>
public sealed class WidgetMachineOption(string machineId, string name, bool selected) : ObservableObject
{
    private bool _isSelected = selected;
    public string MachineId { get; } = machineId;
    public string Name { get; } = name;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}
