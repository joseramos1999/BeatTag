using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Restaurar();
        Closing += (_, _) => Guardar();
        DataContextChanged += (_, _) => SeguirPaginaActiva();
    }

    // --- Páginas perezosas ---
    //
    // MEDIDO: construir las 17 páginas de golpe costaba entre 1,3 y 1,7 s del arranque, más que leer
    // todas las cachés juntas, para enseñar luego una sola. Ahora cada página nace la primera vez que
    // se abre. El índice es el mismo que usa la barra lateral (PaginaNav.Indice).

    private static readonly Dictionary<int, (Func<Control> Vista, Func<MainViewModel, object?> Vm)> Fabricas = new()
    {
        [0]  = (() => new LibraryView(),     vm => vm.Library),
        [1]  = (() => new EnrichView(),      vm => vm.Enrich),
        [2]  = (() => new EditorView(),      vm => vm.Editor),
        [3]  = (() => new DuplicatesView(),  vm => vm.Duplicates),
        [4]  = (() => new QualityView(),     vm => vm.Quality),
        [5]  = (() => new IncompleteView(),  vm => vm.Incomplete),
        [6]  = (() => new NotFoundView(),    vm => vm.NotFound),
        [7]  = (() => new IdentifyView(),    vm => vm.Identify),
        [8]  = (() => new StatsView(),       vm => vm.Stats),
        [9]  = (() => new TrendsView(),      vm => vm.Trends),
        [10] = (() => new LoudnessView(),    vm => vm.Loudness),
        [11] = (() => new SettingsView(),    vm => vm.Settings),
        [12] = (() => new HelpView(),        vm => vm),          // Ayuda lee directamente del principal
        [13] = (() => new BandejaView(),     vm => vm.Bandeja),
        [14] = (() => new FichasView(),      vm => vm.Fichas),
        [15] = (() => new ColeccionesView(), vm => vm.Colecciones),
        [16] = (() => new AsistenteView(),   vm => vm.Asistente),
    };

    private readonly Dictionary<int, Control> _creadas = new();
    private MainViewModel? _vm;

    private void SeguirPaginaActiva()
    {
        if (_vm != null) _vm.PropertyChanged -= AlCambiarPagina;
        _vm = DataContext as MainViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += AlCambiarPagina;
        Mostrar(_vm.SelectedTabIndex);
    }

    private void AlCambiarPagina(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTabIndex) && _vm != null) Mostrar(_vm.SelectedTabIndex);
    }

    private void Mostrar(int indice)
    {
        if (_vm == null) return;
        if (!_creadas.ContainsKey(indice) && Fabricas.TryGetValue(indice, out var f))
        {
            // El DataContext se pone ANTES de meterla en el árbol, para que sus enlaces se resuelvan
            // una sola vez y ya contra su ViewModel.
            var vista = f.Vista();
            vista.DataContext = f.Vm(_vm);
            _creadas[indice] = vista;
            Contenido.Children.Add(vista);
        }
        foreach (var (i, vista) in _creadas) vista.IsVisible = i == indice;
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
