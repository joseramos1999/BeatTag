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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };

            // Al cerrar se guarda lo que solo vive en memoria. Hoy son las casillas "Aplicar" que el
            // usuario haya tocado en Enriquecer: revisar cientos de propuestas lleva su rato y
            // cerrar la ventana no puede tirar ese trabajo.
            desktop.ShutdownRequested += (_, _) => Services.AppEngine.Current?.Marks.Save();
        }

        base.OnFrameworkInitializationCompleted();
    }
}