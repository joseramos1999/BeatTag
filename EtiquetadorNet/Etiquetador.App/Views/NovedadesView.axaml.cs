using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

/// <summary>Página Novedades (equipo «novedades», tanda 4, P5). Lo que necesita la ventana: selector de archivos y la selección de la tabla.</summary>
public partial class NovedadesView : UserControl
{
    public NovedadesView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Novedades");
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    // Clic derecho: selecciona la fila bajo el puntero sin perder una selección de varias (para «Añadir a
    // pendientes»), y las opciones de una sola canción del menú actúan sobre la fila pulsada.
    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
        => AccionesLista.ClicEnTabla(sender, e, DataContext as ListaFrenteBibliotecaViewModel);

    private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => AccionesLista.SeleccionCambiada(sender, DataContext as ListaFrenteBibliotecaViewModel);

    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NovedadesViewModel vm) await AccionesLista.CrearCarpetaAsync(this, vm);
    }

    private async void ExportM3u_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NovedadesViewModel vm) await AccionesLista.ExportarM3uAsync(this, vm);
    }

    private void AnadirSeleccionadas_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NovedadesViewModel vm) AccionesLista.AnadirAPendientes(RowsGrid, vm, todasLasQueFaltan: false);
    }

    private void AnadirTodas_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NovedadesViewModel vm) AccionesLista.AnadirAPendientes(RowsGrid, vm, todasLasQueFaltan: true);
    }

    // Intro en el cuadro del enlace corrige, como el botón: pegar y pulsar Intro es lo natural.
    private void Correccion_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not NovedadesViewModel vm) return;
        if (sender is TextBox tb) vm.Correccion = tb.Text ?? "";   // por si el texto no se ha volcado aún
        if (vm.CorregirArtistaCommand.CanExecute(null)) vm.CorregirArtistaCommand.Execute(null);
        e.Handled = true;
    }
}
