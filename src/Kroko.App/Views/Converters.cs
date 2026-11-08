using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kroko.App.Views;

/// <summary>Texto vacío → Collapsed; con texto → Visible. Con parámetro "invert" hace lo contrario.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        if (parameter as string == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Visible, false → Collapsed. Con parámetro "invert" hace lo contrario.</summary>
public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>0 → Visible (para mostrar "no hay nada"), cualquier otro número → Collapsed.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Progreso (0–1) → StrokeDashArray de un anillo. Parámetro "diámetro,grosor" (p. ej. "40,4").
/// </summary>
public sealed class ProgressToDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "40,4").Split(',');
        var d = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var t = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var circumference = Math.PI * (d - t) / t; // en unidades de grosor
        var p = value is double v ? Math.Clamp(v, 0, 1) : 0;
        return new System.Windows.Media.DoubleCollection { p * circumference, circumference + 1 };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible si alguno de los valores enlazados es true.</summary>
public sealed class AnyTrueToVisibleConverter : IMultiValueConverter
{
    public static readonly AnyTrueToVisibleConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Any(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Nivel 0–1 → escala de una barra de la onda de voz (parámetro = cuánto reacciona esa barra).</summary>
public sealed class LevelToScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is double d ? d : 0;
        var factor = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1;
        return Math.Clamp(0.18 + level * factor, 0.18, 1);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible si el valor no es null (parámetro "invert": al revés).</summary>
public sealed class NotNullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
