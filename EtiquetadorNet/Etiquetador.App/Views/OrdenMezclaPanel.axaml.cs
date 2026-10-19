using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

/// <summary>El orden para mezclar de Tendencias y Playlists: la misma tabla en las dos pestañas.</summary>
public partial class OrdenMezclaPanel : UserControl
{
    public OrdenMezclaPanel()
    {
        InitializeComponent();
        // Recuerda los anchos de su tabla aparte de los de la lista: las columnas son otras.
        AttachedToVisualTree += (_, _) =>
        {
            var engine = Services.AppEngine.Current;
            if (engine != null) GridBehaviors.RememberColumnWidths(TablaMezcla, "OrdenMezclaLista", engine.Config, engine.SaveConfig);
        };
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) => TeclaEnergia.SeleccionarConClicDerecho(sender, e);

    private async void ExportarOrden_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ListaFrenteBibliotecaViewModel vm) await AccionesLista.ExportarOrdenM3uAsync(this, vm);
    }
}
