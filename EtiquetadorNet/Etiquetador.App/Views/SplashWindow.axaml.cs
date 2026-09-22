using Avalonia.Controls;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

/// <summary>
/// Pantalla de carga: se muestra mientras se monta el motor y se cierra en cuanto la ventana
/// principal está lista.
///
/// Sin ViewModel y sin enlaces a propósito: tiene que poder pintarse cuando todavía no existe
/// ninguna otra pieza de la aplicación. El texto se cambia desde fuera con <see cref="Paso"/>.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Version.Text = $"versión {AppInfo.Version}";
    }

    /// <summary>Dice en qué va el arranque. Se llama desde el hilo de interfaz.</summary>
    public void Paso(string texto)
    {
        if (texto.Length > 0) Estado.Text = texto;
    }
}
