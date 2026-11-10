using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace HwMon.App.Services;

/// <summary>
/// Atalhos globais (funcionam com qualquer programa na frente, inclusive jogos): modo jogo e widget. Texto no formato
/// "Ctrl+Shift+F10"; vazio = sem atalho. Se outro programa já usa a combinação, o Windows recusa e o erro aparece
/// nas Configurações.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly Dictionary<int, string> _errors = new();

    public HotkeyService()
    {
        // Janela só de mensagens (HWND_MESSAGE) para receber o WM_HOTKEY.
        _window = new HwndSource(new HwndSourceParameters("Corewise-hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        _window.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == 0x0312 && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                action();
                handled = true;
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Registra (ou troca) o atalho <paramref name="id"/>. Devolve o erro, ou nulo se deu certo.</summary>
    public string? Set(int id, string? text, Action action)
    {
        UnregisterHotKey(_window.Handle, id);
        _actions.Remove(id);
        _errors.Remove(id);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Parse(text) is not { } parsed) return _errors[id] = L.T("Atalho inválido.");
        if (!RegisterHotKey(_window.Handle, id, parsed.Modifiers | 0x4000 /* NOREPEAT */, parsed.Vk))
            return _errors[id] = L.T("Outro programa já usa {0}. Escolha outra combinação.", text);
        _actions[id] = action;
        return null;
    }

    public string? Error(int id) => _errors.GetValueOrDefault(id);

    /// <summary>"Ctrl+Shift+F10" → modificadores do Windows e código da tecla.</summary>
    public static (uint Modifiers, uint Vk)? Parse(string text)
    {
        uint mods = 0;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= 0x0002; break;
                case "alt": mods |= 0x0001; break;
                case "shift": mods |= 0x0004; break;
                case "win": mods |= 0x0008; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        if (key is not { } kk) return null;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(kk);
        return vk == 0 ? null : (mods, vk);
    }

    /// <summary>Texto do atalho a partir da tecla apertada; nulo se for só modificador.</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        // Tecla sozinha só vale para F1–F24 (uma letra sozinha atrapalharia a digitação em todo o Windows).
        if (parts.Count == 0 && key is not (>= Key.F1 and <= Key.F24)) return null;
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys.ToList()) UnregisterHotKey(_window.Handle, id);
        _window.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
