using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using HwMon.App.ViewModels;
using HwMon.Core.Telemetry;
using Forms = System.Windows.Forms;
using IconControl = HwMon.App.Controls.Icon;

namespace HwMon.App.Services;

/// <summary>
/// Ícone na bandeja (perto do relógio). Clique abre o painel; botão direito abre um menu WPF (mesmo tema do
/// programa) com widget, máquina do widget, configurações e sair. A dica mostra CPU e RAM de cada máquina.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _tooltip;

    public TrayIcon(App app, MainViewModel vm)
    {
        _app = app;
        _vm = vm;
        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), Text = "Corewise", Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _app.ShowMain();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        _tooltip = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tooltip.Tick += (_, _) => UpdateTooltip();
        _tooltip.Start();
    }

    public void ShowHint(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    private static System.Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void UpdateTooltip()
    {
        // A dica da bandeja aceita no máximo 127 caracteres.
        var lines = new List<string> { "Corewise" };
        foreach (var m in _vm.Machines)
        {
            var cpu = m.Descriptor?.Components.FirstOrDefault(c => c.Kind == ComponentKind.Cpu)?.Id;
            lines.Add(m.IsConnected && cpu is not null
                ? $"{m.Name}: CPU {m.Text(cpu, "usage")} · RAM {m.Text("ram:0", "load")}"
                : $"{m.Name}: {m.StatusText}");
        }
        var text = string.Join("\n", lines);
        _icon.Text = text.Length <= 127 ? text : text[..126] + "…";
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        MenuItem Item(string header, string? icon, Action click, bool? isChecked = null)
        {
            var item = new MenuItem { Header = header };
            if (icon is not null) item.Icon = new IconControl { Kind = icon };
            if (isChecked is { } c)
            {
                item.IsCheckable = true;
                item.IsChecked = c;
            }
            item.Click += (_, _) => click();
            return item;
        }

        menu.Items.Add(Item(L.T("Abrir painel"), "nav-overview", _app.ShowMain));
        menu.Items.Add(Item(L.T("Mostrar widget"), "action-widget", _app.ToggleWidget, _app.WidgetVisible));
        menu.Items.Add(Item(L.T("Widget sempre no topo"), "action-pin", () => _vm.Widget.Topmost = !_vm.Widget.Topmost, _vm.Widget.Topmost));
        if (_vm.Machines.Count > 1)
        {
            var machines = new MenuItem { Header = L.T("Máquina do widget"), Icon = new IconControl { Kind = "machine-generic" } };
            foreach (var m in _vm.Machines)
                machines.Items.Add(Item(m.Name, null, () =>
                {
                    _vm.Widget.Current = m;
                    if (!_app.WidgetVisible) _app.ShowWidget();
                }, _vm.Widget.Current == m));
            menu.Items.Add(machines);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Código de pareamento"), "status-secure", () => _ = PairRequestWatcher.ShowCodeAsync(_vm, null)));
        menu.Items.Add(Item(L.T("Configurações"), "nav-settings", _app.ShowSettings));
        menu.Items.Add(Item(L.T("Sair"), "action-close", _app.Quit));

        menu.Opened += (_, _) =>
        {
            // Sem trazer o menu para a frente ele não fecha ao clicar fora (limitação conhecida da bandeja).
            if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        _tooltip.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
