using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class TrendsView : UserControl
{
    public TrendsView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Tendencias");

        // La lista de países se pide la primera vez que se abre la pestaña, no al arrancar la app.
        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is TrendsViewModel vm) await vm.EnsureCountriesAsync();
        };
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    // Clic derecho: selecciona la fila bajo el puntero sin perder una selección de varias (para «Añadir a
    // pendientes»), y las opciones de una sola canción del menú actúan sobre la fila pulsada.
    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
        => AccionesLista.ClicEnTabla(sender, e, DataContext as ListaFrenteBibliotecaViewModel);

    private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => AccionesLista.SeleccionCambiada(sender, DataContext as ListaFrenteBibliotecaViewModel);

    // Copia a una carpeta las canciones del chart que ya tienes.
    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrendsViewModel vm) await AccionesLista.CrearCarpetaAsync(this, vm);
    }

    // Guardar la lista no copia archivos: apunta a los que ya tienes donde estan.
    private async void ExportM3u_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrendsViewModel vm) await AccionesLista.ExportarM3uAsync(this, vm);
    }

    // Lo que falta, a Pendientes: las filas elegidas o todas las que faltan.
    private void AnadirSeleccionadas_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrendsViewModel vm) AccionesLista.AnadirAPendientes(RowsGrid, vm, todasLasQueFaltan: false);
    }

    private void AnadirTodas_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrendsViewModel vm) AccionesLista.AnadirAPendientes(RowsGrid, vm, todasLasQueFaltan: true);
    }
}
