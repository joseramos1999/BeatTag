using System;
using Avalonia;
using Avalonia.Controls;
using Etiquetador.App.Services;

namespace Etiquetador.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Restaurar();
        Closing += (_, _) => Guardar();
    }

    /// <summary>
    /// Devuelve la ventana al tamaño y sitio en que se dejó.
    ///
    /// Con una comprobación que no es paranoia: si la ventana se cerró en un segundo monitor que ya
    /// no está conectado, sus coordenadas guardadas caen fuera de toda pantalla y la aplicación
    /// abriría invisible, sin forma evidente de recuperarla. En ese caso se ignora la posición
    /// guardada y se centra.
    /// </summary>
    private void Restaurar()
    {
        var cfg = AppEngine.Current?.Config;
        if (cfg == null) return;

        if (cfg.WindowWidth >= MinWidth && cfg.WindowHeight >= MinHeight)
        {
            Width = cfg.WindowWidth;
            Height = cfg.WindowHeight;
        }

        if (cfg.WindowX != int.MinValue && cfg.WindowY != int.MinValue && CabeEnAlgunaPantalla(cfg))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(cfg.WindowX, cfg.WindowY);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (cfg.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>¿La esquina guardada cae dentro de alguna pantalla conectada ahora mismo?</summary>
    private bool CabeEnAlgunaPantalla(Etiquetador.Core.AppConfig cfg)
    {
        try
        {
            var punto = new PixelPoint(cfg.WindowX, cfg.WindowY);
            foreach (var pantalla in Screens.All)
                if (pantalla.Bounds.Contains(punto)) return true;
            return false;
        }
        catch { return false; }   // ante la duda, centrar: es recuperable, quedarse fuera no
    }

    private void Guardar()
    {
        var engine = AppEngine.Current;
        if (engine == null) return;

        try
        {
            var cfg = engine.Config;
            cfg.WindowMaximized = WindowState == WindowState.Maximized;

            // Maximizada, el tamaño que interesa guardar es el que tenía ANTES: al restaurarla se
            // vuelve a ese, no a la pantalla entera.
            if (WindowState == WindowState.Normal)
            {
                cfg.WindowWidth = Width;
                cfg.WindowHeight = Height;
                cfg.WindowX = Position.X;
                cfg.WindowY = Position.Y;
            }

            engine.SaveConfig();
        }
        catch { /* no poder recordar el tamaño no puede impedir cerrar la aplicación */ }
    }
}
