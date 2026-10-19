using System;
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

    /// <summary>
    /// El arranque ha fallado: se dice por qué y se ofrece cerrar. Sin esto la pantalla se quedaba
    /// girando para siempre, sin marco ni botón, y solo se podía cerrar desde el Administrador de
    /// tareas (hallazgo M13). Se llama desde el hilo de interfaz.
    /// </summary>
    public void MostrarError(string texto, Action alCerrar)
    {
        Estado.Text = "No se ha podido abrir BeatTag.";
        Barra.IsVisible = false;
        Error.Text = texto;
        Error.IsVisible = true;
        Cerrar.IsVisible = true;
        Cerrar.Click += (_, _) => alCerrar();
        Cerrar.Focus();
    }
}
