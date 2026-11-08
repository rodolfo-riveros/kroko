using System.Windows;
using System.Windows.Controls;

namespace Kroko.App.Views;

/// <summary>
/// PasswordBox no deja enlazar su contenido (por seguridad). Este adjunto lo enlaza en un solo sentido
/// (de la caja al ViewModel) y permite vaciarla desde el ViewModel poniendo la propiedad en "".
/// </summary>
public static class PasswordBoxAssist
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssist),
        // Valor inicial null: así el primer enlace ("") dispara el cambio y se engancha el evento
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    /// <summary>Permite que el ViewModel rellene la caja (oculta) con un valor ya guardado, p. ej. el token del cerebro.</summary>
    public static readonly DependencyProperty AllowFillProperty = DependencyProperty.RegisterAttached(
        "AllowFill", typeof(bool), typeof(PasswordBoxAssist), new PropertyMetadata(false));

    public static bool GetAllowFill(DependencyObject d) => (bool)d.GetValue(AllowFillProperty);

    public static void SetAllowFill(DependencyObject d, bool value) => d.SetValue(AllowFillProperty, value);

    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached("IsHooked", typeof(bool), typeof(PasswordBoxAssist));

    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!(bool)box.GetValue(IsHookedProperty))
        {
            box.SetValue(IsHookedProperty, true);
            box.PasswordChanged += (_, _) => SetBoundPassword(box, box.Password);
        }
        // Solo se escribe en la caja para vaciarla (el ViewModel nunca rellena contraseñas)
        if (e.NewValue is "" && box.Password.Length > 0) box.Password = "";
        else if (GetAllowFill(box) && e.NewValue is string value && value.Length > 0 && box.Password != value) box.Password = value;
    }
}
