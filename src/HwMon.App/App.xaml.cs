using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;
using HwMon.App.Infrastructure;
using HwMon.App.Services;
using HwMon.Core;
using HwMon.App.ViewModels;
using HwMon.App.Views;
using HwMon.Client;

namespace HwMon.App;

/// <summary>
/// Painel, widget e ícone na bandeja no mesmo programa, com uma cópia só por usuário (abrir de novo traz o painel
/// para a frente). Com "manter na bandeja" ligado, fechar as janelas não encerra: sai-se pelo menu da bandeja.
/// Argumentos: --inicio (aberto pelo Windows; segue a configuração), --widget, --com-widget, --abrir &lt;nome&gt;.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\Corewise.App";
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private MainViewModel _vm = null!;
    private MainWindow? _main;
    private readonly List<WidgetWindow> _widgets = new();
    private bool _rebuildingWidgets, _hidingWidgets;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private GameModeService? _gameMode;
    private PairRequestWatcher? _pairWatcher;
    private bool _widgetSuspended;
    private bool _exiting;

    public bool WidgetVisible => _widgets.Count > 0;
    public GameModeService? GameMode => _gameMode;

    public const int HotkeyGame = 1, HotkeyWidget = 2;

    /// <summary>(Re)registra os atalhos globais do modo jogo e do widget. Devolve os erros (atalho já em uso).</summary>
    public (string? Game, string? Widget) ApplyHotkeys()
    {
        _hotkeys ??= new HotkeyService();
        var s = _vm.Settings.Current;
        return (_hotkeys.Set(HotkeyGame, s.GameMode.Hotkey, () => _gameMode?.Toggle()),
                _hotkeys.Set(HotkeyWidget, s.Widget.Hotkey, ToggleWidget));
    }
    public MainViewModel ViewModel => _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --perfil <pasta>: outra lista de máquinas (testes), sem mexer nas do dia a dia; vira uma cópia à parte.
        var profile = Array.IndexOf(e.Args, "--perfil") is var pi and >= 0 && pi + 1 < e.Args.Length ? e.Args[pi + 1] : null;
        var instanceName = profile is null ? InstanceName : $"{InstanceName}.{(uint)profile.ToLowerInvariant().GetHashCode():X8}";

        // Cópia única: a segunda só avisa a primeira para mostrar o painel e sai.
        _instance = new Mutex(true, instanceName, out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
        if (!first)
        {
            if (!e.Args.Contains(StartupService.StartupArg)) _showSignal.Set();
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                if (!_exiting) Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true, Name = "show-signal" };
        listener.Start();

        // Erro inesperado na interface: registra em %AppData%\HwMon\erro.log em vez de fechar sem aviso.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            MessageBox.Show(L.T("Ocorreu um erro inesperado:\n\n{0}\n\nDetalhes em %AppData%\\Corewise\\erro.log", args.Exception.Message), "Corewise",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);

        var settings = new SettingsStore();
        // --idioma pt|en: força o idioma só nesta execução (testes), sem mudar a configuração.
        UseLanguage(Array.IndexOf(e.Args, "--idioma") is var li and >= 0 && li + 1 < e.Args.Length ? e.Args[li + 1] : settings.Current.Language);
        ApplyTheme(settings.Current.Theme);
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General) Dispatcher.Invoke(() => ApplyTheme(_vm.Settings.Current.Theme));
        };
        StartupService.EnsureDefaults(settings);

        _vm = new MainViewModel(settings, new MachineStore(profile));
        _tray = new TrayIcon(this, _vm);
        _gameMode = new GameModeService(this, _vm, settings);
        if (profile is null) _ = LocalPairing.RunAsync(_vm);
        _pairWatcher = new PairRequestWatcher(_vm);
        ApplyHotkeys();
        _vm.Machines.CollectionChanged += (_, _) => { if (_vm.Settings.Current.Widget.PerMachine && WidgetVisible) RebuildWidgets(); };

        // --abrir <nome>: abre direto na página de uma máquina.
        var open = Array.IndexOf(e.Args, "--abrir") is var i and >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        if (open is not null && _vm.Machines.FirstOrDefault(m => m.Name.Equals(open, StringComparison.OrdinalIgnoreCase)) is { } target)
            _vm.Selected = target;
        else if (open is "historico" or "histórico") _vm.Selected = _vm.HistoryHub;
        else if (open is "sensores") _vm.Selected = _vm.Sensors;

        if (e.Args.Contains(StartupService.StartupArg))
        {
            switch (settings.Current.Startup.Mode)
            {
                case StartupMode.Widget:
                    ShowWidget();
                    break;
                case StartupMode.PanelAndWidget:
                    ShowMain();
                    ShowWidget();
                    break;
                case StartupMode.Tray:
                    break;
            }
            return;
        }
        var widgetOnly = e.Args.Contains("--widget");
        if (e.Args.Contains("--configuracoes")) ShowSettings();
        else if (!widgetOnly) ShowMain();
        if (widgetOnly || e.Args.Contains("--com-widget") || settings.Current.Widget.Visible) ShowWidget();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _gameMode?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_instance is not null)
        {
            _vm.Shutdown();
            _instance.ReleaseMutex();
            _instance.Dispose();
        }
        base.OnExit(e);
    }

    public void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow(_vm);
            _main.Closed += (_, _) =>
            {
                _main = null;
                OnWindowClosed();
            };
            _main.Show();
        }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ShowSettings()
    {
        ShowMain();
        _vm.ShowingSettings = true;
    }

    public void ShowWidget()
    {
        if (_widgets.Count == 0) CreateWidgets();
        _vm.Settings.Current.Widget.Visible = true;
        _vm.Settings.SaveSoon();
        _main?.UpdateWidgetButton();
        _vm.SettingsPage.Refresh();
    }

    /// <summary>Modo jogo: esconde o widget enquanto o jogo está na frente, sem mudar a preferência salva.</summary>
    public void SetWidgetSuspended(bool suspended)
    {
        if (_widgetSuspended == suspended) return;
        _widgetSuspended = suspended;
        foreach (var w in _widgets)
            if (suspended) w.Hide();
            else w.Show();
    }

    /// <summary>Um widget só (troca de máquina pelas setas) ou um para cada máquina escolhida.</summary>
    private void CreateWidgets()
    {
        var settings = _vm.Settings;
        _vm.Widget.RefreshShown();
        if (settings.Current.Widget.PerMachine)
        {
            var index = 0;
            foreach (var m in _vm.Widget.Shown)
                Add(new WidgetWindow(new WidgetViewModel(_vm.Machines, settings, m), settings, index++));
        }
        else Add(new WidgetWindow(_vm.Widget, settings));

        void Add(WidgetWindow w)
        {
            _widgets.Add(w);
            w.Closed += (_, _) =>
            {
                _widgets.Remove(w);
                if (_rebuildingWidgets || _hidingWidgets) return;
                // Fechado por fora (Alt+F4): fecha os outros também, como o atalho.
                HideWidget();
            };
            if (_widgetSuspended) return;
            w.Show();
        }
    }

    /// <summary>Mudou o modo (um/um por PC) ou as máquinas escolhidas: refaz os widgets abertos.</summary>
    public void RebuildWidgets()
    {
        _vm.Widget.RefreshShown();
        if (_widgets.Count == 0) return;
        _rebuildingWidgets = true;
        try
        {
            foreach (var w in _widgets.ToList()) w.Close();
        }
        finally
        {
            _rebuildingWidgets = false;
        }
        CreateWidgets();
    }

    public void HideWidget()
    {
        _vm.Settings.Current.Widget.Visible = false;
        _vm.Settings.SaveNow();
        _hidingWidgets = true;
        try
        {
            foreach (var w in _widgets.ToList()) w.Close();
        }
        finally
        {
            _hidingWidgets = false;
        }
        _main?.UpdateWidgetButton();
        _vm.SettingsPage.Refresh();
        OnWindowClosed();
    }

    /// <summary>Mostra ou esconde todos os widgets (botão, bandeja e atalho).</summary>
    public void ToggleWidget()
    {
        if (_widgets.Count == 0) ShowWidget();
        else HideWidget();
    }

    /// <summary>Sair de verdade (menu da bandeja).</summary>
    public void Quit()
    {
        _exiting = true;
        _main?.Close();
        Shutdown();
    }

    private void OnWindowClosed()
    {
        if (_exiting || _main is not null || _widgets.Count > 0) return;
        var startup = _vm.Settings.Current.Startup;
        if (!startup.CloseToTray)
        {
            Shutdown();
            return;
        }
        if (!startup.TrayHintShown)
        {
            startup.TrayHintShown = true;
            _vm.Settings.SaveSoon();
            _tray?.ShowHint(L.T("Corewise continua na bandeja"), L.T("Clique no ícone perto do relógio para abrir o painel. Para encerrar, use Sair no menu do ícone."));
        }
    }

    private static void LogError(Exception? ex)
    {
        try
        {
            var dir = HwMon.Core.AppPaths.UserData;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "erro.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // sem onde registrar
        }
    }

    private static void UseLanguage(string setting)
    {
        L.Use(setting);
        Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = L.Culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = L.Culture;
        // StringFormat dos bindings no XAML usa a língua do elemento (padrão: en-US).
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag)));
        XamlTranslator.Register();
    }

    /// <summary>Reabre o painel (para trocar o idioma): solta a cópia única, abre outra e sai.</summary>
    public void Restart()
    {
        _exiting = true;
        _vm.Shutdown();
        _instance?.ReleaseMutex();
        _instance?.Dispose();
        _instance = null;
        var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Where(a => a != StartupService.StartupArg).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, (args + " --configuracoes").Trim()) { UseShellExecute = false });
        _main?.Close();
        Shutdown();
    }

    /// <summary>Tema "system" segue o Windows (claro/escuro); "light"/"dark" fixam. Paleta própria + controles Fluent.</summary>
    public void ApplyTheme(string theme)
    {
        var light = theme switch
        {
            "light" => true,
            "dark" => false,
            _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1,
        };
        ThemeMode = light ? ThemeMode.Light : ThemeMode.Dark;
        var palette = new ResourceDictionary { Source = new Uri(light ? "Theme/Light.xaml" : "Theme/Dark.xaml", UriKind.Relative) };
        var dictionaries = Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Theme/Dark.xaml") == true || d.Source?.OriginalString.Contains("Theme/Light.xaml") == true);
        if (current is not null) dictionaries[dictionaries.IndexOf(current)] = palette;
        else dictionaries.Insert(0, palette);
    }
}
