using System.Windows.Controls;

namespace Kroko.App.Views;

/// <summary>La isla de arriba (17/11/2026). Solo dibujo: el estado viene de BubbleViewModel.</summary>
public partial class IslandView : UserControl
{
    public IslandView() => InitializeComponent();

    /// <summary>Para las capturas: dibuja la cara ya, sin esperar al refresco de pantalla.</summary>
    internal void RenderFaceNow() => Face.RenderNow();
}
