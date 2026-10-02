using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Playlists");
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    // Clic derecho: selecciona la fila bajo el puntero para que el menú actúe sobre ella.
    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) => TeclaEnergia.SeleccionarConClicDerecho(sender, e);

    // Intro en el cuadro del enlace hace lo mismo que el botón: pegar y pulsar Intro es lo natural.
    private void Enlace_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not PlaylistViewModel vm) return;
        if (sender is TextBox tb) vm.Enlace = tb.Text ?? "";   // por si el enlace no se ha volcado aún
        if (vm.LoadCommand.CanExecute(null)) vm.LoadCommand.Execute(null);
        e.Handled = true;
    }

    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm) await AccionesLista.CrearCarpetaAsync(this, vm);
    }

    private async void ExportM3u_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaylistViewModel vm) await AccionesLista.ExportarM3uAsync(this, vm);
    }
}
