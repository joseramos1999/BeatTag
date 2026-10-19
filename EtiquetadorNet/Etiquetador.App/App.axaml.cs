using System;
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
            // Solo el aspecto: sin descifrar (en macOS iría al Llavero) ni apartar un archivo
            // ilegible, que es cosa del motor y de su aviso.
            Services.Apariencia.Aplicar(AppConfig.LeerAspecto(paths));

            // Mientras se monta el motor solo existe la pantalla de carga. Con OnMainWindowClose la
            // aplicación no se cierra por no tener todavía ventana principal.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            var splash = new SplashWindow();
            splash.Show();

            // Si se intenta abrir BeatTag otra vez, la que ya está abierta se pone delante (la otra
            // se cierra sola: ver Program).
            if (Services.InstanciaUnica.Actual is { } instancia)
                instancia.OtraQuiereAbrir += () => Dispatcher.UIThread.Post(
                    () => TraerAlFrente(desktop.MainWindow ?? (splash.IsVisible ? splash : null)));

            _ = ArrancarAsync(desktop, splash);

            // Al cerrar se guarda todo lo que solo vive en memoria: análisis, casillas «Aplicar»,
            // huellas, respuestas de «Comprobar audio», medidas de sonoridad. Antes solo se guardaban
            // las casillas. La ventana ya lo guarda al cerrarse; esto cubre lo que no pasa por ella,
            // como cerrar la sesión de Windows o salir desde el menú de macOS.
            desktop.ShutdownRequested += (_, _) => GuardarAlSalir("al cerrar la sesión");
            desktop.Exit += (_, _) => GuardarAlSalir("al salir");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void GuardarAlSalir(string cuando)
    {
        try { Services.AppEngine.Current?.GuardarTodo(cuando); }
        catch (Exception e) { Services.CrashLog.Write("Guardar " + cuando, e); }
    }

    /// <summary>
    /// Monta el motor fuera del hilo de interfaz y abre la ventana cuando está listo.
    ///
    /// El motor lee las cachés del disco, y en una biblioteca grande eso son unas cuantas décimas
    /// de segundo; hacerlo aquí deja la pantalla de carga viva y diciendo por dónde va, en vez de
    /// un escritorio vacío que parece que la aplicación no ha abierto.
    ///
    /// Si algo falla, se dice en la propia pantalla de carga (hallazgo M13). Antes la excepción se
    /// quedaba dentro de esta tarea, que nadie esperaba: la pantalla de carga seguía girando para
    /// siempre, sin marco ni botón para cerrarla, y no quedaba ningún informe que enviar.
    /// </summary>
    private static async Task ArrancarAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        Services.AppEngine? motor = null;
        try
        {
            Services.AppEngine.Progreso = texto => Dispatcher.UIThread.Post(() => splash.Paso(texto));

            motor = await Task.Run(() => new Services.AppEngine());
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
        catch (Exception e)
        {
            Services.AppEngine.Progreso = null;
            FalloAlArrancar(desktop, splash, motor ?? Services.AppEngine.Current, e);
        }
    }

    /// <summary>Registra el fallo, lo explica en la pantalla de carga y deja cerrar la aplicación.</summary>
    private static void FalloAlArrancar(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash,
                                        Services.AppEngine? motor, Exception e)
    {
        Services.CrashLog.Write("Arranque", e);
        try { motor?.Logger.Error("No se ha podido abrir la aplicación", e); } catch { /* el registro también puede ser lo que falla */ }

        var texto = $"{e.Message}\n\nSe ha guardado un informe del error en {Services.CrashLog.Carpeta}. "
                  + "Si vuelve a pasar, adjúntalo al informar del problema.";
        try
        {
            // Que cerrar una ventana a medio crear no cierre la aplicación antes de poder leer el motivo.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (desktop.MainWindow is { } ventana)
            {
                desktop.MainWindow = null;
                try { ventana.Close(); } catch { }
            }
            if (!splash.IsVisible) splash.Show();
            splash.MostrarError(texto, () => desktop.Shutdown(1));
        }
        catch
        {
            desktop.Shutdown(1);
        }
    }

    /// <summary>Pone delante la ventana abierta (o la de carga, si aún no hay otra).</summary>
    private static void TraerAlFrente(Window? ventana)
    {
        if (ventana == null) return;
        try
        {
            if (ventana.WindowState == WindowState.Minimized) ventana.WindowState = WindowState.Normal;
            ventana.Show();
            ventana.Activate();
            // Windows no deja que un proceso le quite el foco a otro; ponerla encima un instante sí la
            // trae delante, aunque el foco se quede donde estaba.
            ventana.Topmost = true;
            ventana.Topmost = false;
        }
        catch { /* no poder traerla delante no es motivo para fallar */ }
    }

    private static long _avaloniaListo;

    /// <summary>Milisegundos desde que Windows lanzó el proceso: incluye lo que pasa antes de Main.</summary>
    private static long DesdeElProceso()
    {
        try { return (long)(System.DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds; }
        catch { return -1; }
    }
}
