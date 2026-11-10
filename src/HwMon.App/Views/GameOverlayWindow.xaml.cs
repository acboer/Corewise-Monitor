using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HwMon.App.ViewModels;

namespace HwMon.App.Views;

/// <summary>
/// Overlay do modo jogo: sempre no topo, sem foco e "transparente para o mouse" (os cliques vão para o jogo).
/// Aparece sobre jogos em tela cheia sem borda e na maioria dos jogos DX11/DX12 atuais (o Windows converte a tela
/// cheia exclusiva em "flip"); jogos antigos em tela cheia exclusiva de verdade podem cobri-lo.
/// </summary>
public partial class GameOverlayWindow : Window
{
    private Rect _monitor;
    private string _corner = "top-left";

    public GameOverlayWindow(GameOverlayViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */).ToInt64();
            // TRANSPARENT (cliques passam) | LAYERED | TOOLWINDOW (fora do Alt+Tab) | NOACTIVATE
            SetWindowLongPtr(hwnd, -20, new IntPtr(style | 0x20 | 0x80000 | 0x80 | 0x08000000));
        };
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Canto escolhido no monitor (em pixels) onde o jogo está.</summary>
    public void PlaceAt(string corner, Rect monitorPixels)
    {
        _corner = corner;
        _monitor = monitorPixels;
        Reposition();
    }

    private void Reposition()
    {
        if (_monitor.IsEmpty) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var m = new Rect(_monitor.X / dpi.DpiScaleX, _monitor.Y / dpi.DpiScaleY, _monitor.Width / dpi.DpiScaleX, _monitor.Height / dpi.DpiScaleY);
        var w = ActualWidth > 0 ? ActualWidth : 260;
        var h = ActualHeight > 0 ? ActualHeight : 140;
        const double margin = 8;
        Left = _corner.EndsWith("right", StringComparison.Ordinal) ? m.Right - w - margin : m.Left + margin;
        Top = _corner.StartsWith("bottom", StringComparison.Ordinal) ? m.Bottom - h - margin : m.Top + margin;
    }

    /// <summary>Jogos às vezes se põem por cima: reafirma o "sempre no topo" sem roubar o foco.</summary>
    public void KeepOnTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1) /* TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
