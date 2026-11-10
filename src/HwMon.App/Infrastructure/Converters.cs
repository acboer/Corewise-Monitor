using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HwMon.App.ViewModels;
using HwMon.Client;

namespace HwMon.App.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Leitura antiga (ou máquina desconectada) aparece esmaecida.</summary>
public sealed class StaleToOpacityConverter : IValueConverter
{
    /// <remarks>ConverterParameter "Invert": esmaece quando o valor é falso (ex.: IsConnected).</remarks>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ^ (parameter as string == "Invert") ? 0.45 : 1.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Severidade → cor da paleta (ConverterParameter "Border" usa a cor neutra da borda quando normal).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Severity.Critical => "Status.Critical",
            Severity.Caution => "Status.Caution",
            _ => parameter as string == "Border" ? "Card.Border" : parameter as string == "Text" ? "Text.Primary" : "Accent",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ConnectionStatus.Connected => "Status.Ok",
            ConnectionStatus.Connecting => "Text.Tertiary",
            ConnectionStatus.Disconnected => "Status.Caution",
            _ => "Status.Critical",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Estado da conexão → ícone (formas diferentes além da cor).</summary>
public sealed class StatusToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ConnectionStatus.Connected => "status-connected",
        ConnectionStatus.Connecting => "status-connecting",
        ConnectionStatus.Disconnected => "status-disconnected",
        _ => "status-failed",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Severidade → cor fixa para o overlay do jogo (sempre escuro, não segue o tema).</summary>
public sealed class OverlaySeverityConverter : IValueConverter
{
    private static readonly Brush Normal = Frozen("#F2F5F8"), Caution = Frozen("#F5B13D"), Critical = Frozen("#F2555A");

    private static Brush Frozen(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch { Severity.Critical => Critical, Severity.Caution => Caution, _ => Normal };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Todos os valores verdadeiros → visível.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Texto que é um endereço web (http/https) → visível; ConverterParameter "Invert" faz o contrário.</summary>
public sealed class UrlToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isUrl = value is string s && (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        return isUrl ^ (parameter as string == "Invert") ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Contagem &gt; 0 → visível.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullableDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value is double d ? d : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
