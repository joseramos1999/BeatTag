using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Etiquetador.App.ViewModels;
using Etiquetador.App.Views;

namespace Etiquetador.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services.CrashLog.InstallUiGuard();   // un fallo en un comando ya no tumba la app

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // El motor (y con él la configuración) nace aquí dentro, así que el aspecto se aplica
            // DESPUÉS de tener el ViewModel y ANTES de crear la ventana: así abre ya con el tema y
            // la densidad elegidos, sin el parpadeo de pintarse en claro y corregirse a oscuro.
            var vm = new MainViewModel();
            if (Services.AppEngine.Current is { } motor) Services.Apariencia.Aplicar(motor.Config);

            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Al cerrar se guarda lo que solo vive en memoria. Hoy son las casillas "Aplicar" que el
            // usuario haya tocado en Enriquecer: revisar cientos de propuestas lleva su rato y
            // cerrar la ventana no puede tirar ese trabajo.
            desktop.ShutdownRequested += (_, _) => Services.AppEngine.Current?.Marks.Save();
        }

        base.OnFrameworkInitializationCompleted();
    }
}