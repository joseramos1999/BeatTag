using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Etiquetador.App.ViewModels;
using Etiquetador.App.Views;
using Etiquetador.Core;

namespace Etiquetador.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _avaloniaListo = DesdeElProceso();
        Services.CrashLog.InstallUiGuard();   // un fallo en un comando ya no tumba la app

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // El tema se lee ANTES de enseñar nada: leer la configuración cuesta unas décimas y así
            // la pantalla de carga ya sale con el aspecto elegido, sin pintarse en claro y
            // corregirse a oscuro a la vista del usuario.
            var paths = new AppPaths();
            Services.Apariencia.Aplicar(AppConfig.Load(paths));

            // Mientras se monta el motor solo existe la pantalla de carga. Con OnMainWindowClose la
            // aplicación no se cierra por no tener todavía ventana principal.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var splash = new SplashWindow();
            splash.Show();

            _ = ArrancarAsync(desktop, splash);

            // Al cerrar se guarda lo que solo vive en memoria. Hoy son las casillas "Aplicar" que el
            // usuario haya tocado en Enriquecer: revisar cientos de propuestas lleva su rato y
            // cerrar la ventana no puede tirar ese trabajo.
            desktop.ShutdownRequested += (_, _) => Services.AppEngine.Current?.Marks.Save();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Monta el motor fuera del hilo de interfaz y abre la ventana cuando está listo.
    ///
    /// El motor lee las cachés del disco, y en una biblioteca grande eso son unas cuantas décimas
    /// de segundo; hacerlo aquí deja la pantalla de carga viva y diciendo por dónde va, en vez de
    /// un escritorio vacío que parece que la aplicación no ha abierto.
    /// </summary>
    private static async Task ArrancarAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        Services.AppEngine.Progreso = texto => Dispatcher.UIThread.Post(() => splash.Paso(texto));

        var motor = await Task.Run(() => new Services.AppEngine());
        Services.AppEngine.Progreso = null;

        // De vuelta en el hilo de interfaz: el ViewModel y la ventana se crean aquí, que es donde
        // viven los enlaces y las colecciones que la ventana va a observar.
        splash.Paso("Preparando la ventana…");
        Services.Apariencia.Aplicar(motor.Config);   // por si la configuración trae algo más que el tema

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var vm = new MainViewModel(motor);
        var tVm = reloj.ElapsedMilliseconds;
        var ventana = new MainWindow { DataContext = vm };
        var tVentana = reloj.ElapsedMilliseconds;
        desktop.MainWindow = ventana;

        // Se anota cuándo se PINTA de verdad, no cuándo se pide: es lo que ve el usuario.
        ventana.Opened += (_, _) =>
        {
            var log = motor.Logger;
            log.Detail($"Arranque · Avalonia listo a los {_avaloniaListo} ms de lanzar el proceso");
            log.Detail($"Arranque · páginas: {tVm} ms · ventana: {tVentana - tVm} ms · abierta a los {DesdeElProceso()} ms de lanzar el proceso");
        };
        ventana.Show();
        splash.Close();
    }

    private static long _avaloniaListo;

    /// <summary>Milisegundos desde que Windows lanzó el proceso: incluye lo que pasa antes de Main.</summary>
    private static long DesdeElProceso()
    {
        try { return (long)(System.DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
        catch { return -1; }
    }
}
