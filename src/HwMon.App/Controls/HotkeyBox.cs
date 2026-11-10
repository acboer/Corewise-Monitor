using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HwMon.App.Services;

namespace HwMon.App.Controls;

/// <summary>
/// Caixa que grava um atalho: clique e aperte a combinação (ex.: Ctrl+Shift+F10). Backspace ou Delete apaga
/// (sem atalho); Esc desiste.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Show()));

    public string Hotkey { get => (string)GetValue(HotkeyProperty); set => SetValue(HotkeyProperty, value); }

    public HotkeyBox()
    {
        // Mesmo visual das caixas de texto do tema (o estilo implícito não vale para a subclasse).
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        MinWidth = 170;
        GotKeyboardFocus += (_, _) => Text = L.T("aperte a combinação…");
        LostKeyboardFocus += (_, _) => Show();
        Loaded += (_, _) => Show();
    }

    private void Show()
    {
        if (!IsKeyboardFocused) Text = string.IsNullOrWhiteSpace(Hotkey) ? L.T("(sem atalho)") : Hotkey;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { MoveFocusAway(); return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            Hotkey = "";
            MoveFocusAway();
            return;
        }
        if (key == Key.Tab) { e.Handled = false; return; }
        if (HotkeyService.Format(Keyboard.Modifiers, key) is { } text)
        {
            Hotkey = text;
            MoveFocusAway();
        }
    }

    private void MoveFocusAway()
    {
        Keyboard.ClearFocus();
        Show();
    }
}
