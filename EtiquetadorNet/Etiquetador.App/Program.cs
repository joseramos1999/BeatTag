using Avalonia;
using System;
using Etiquetador.App.Services;

namespace Etiquetador.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CrashLog.Install();   // registro de excepciones no controladas

        // Una sola ventana a la vez: dos abiertas se pisan los archivos de datos al guardar. Si ya
        // hay una, se le pide que se ponga delante y esta se cierra sin cargar nada.
        using var instancia = InstanciaUnica.Adquirir();
        if (!instancia.EsLaPrimera)
        {
            instancia.AvisarALaPrimera();
            return;
        }
        InstanciaUnica.Actual = instancia;

        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { CrashLog.Write("Fatal", ex); throw; }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
