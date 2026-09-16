using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class BandejaView : UserControl
{
    public BandejaView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Bandeja");
    }

    private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is BandejaViewModel vm)
            vm.Seleccionar(grid.SelectedItems.OfType<BandejaRow>());
    }

    private void Grid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not BandejaViewModel vm || e.KeyModifiers != KeyModifiers.None) return;
        if (TeclaEnergia.De(e.Key) is int energia)
        {
            vm.PonerEnergiaRapida(energia);
            e.Handled = true;
        }
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) => TeclaEnergia.SeleccionarConClicDerecho(sender, e);

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private async void ElegirCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BandejaViewModel vm) return;
        var ruta = await ElegirAsync("Elige la carpeta de la bandeja de entrada");
        if (ruta != null) await vm.ElegirCarpetaAsync(ruta);
    }

    private async void ElegirDestino_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BandejaViewModel vm) return;
        var ruta = await ElegirAsync("Elige a qué carpeta de la biblioteca pasan las canciones");
        if (ruta != null) vm.ElegirDestino(ruta);
    }

    private async System.Threading.Tasks.Task<string?> ElegirAsync(string titulo)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var carpetas = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = titulo, AllowMultiple = false });
        var ruta = carpetas.Count > 0 ? carpetas[0].TryGetLocalPath() : null;
        return string.IsNullOrEmpty(ruta) ? null : ruta;
    }

    private async void Pasar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BandejaViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var preparadas = vm.Preparadas;
        if (preparadas.Count == 0)
        {
            vm.Status = "No hay canciones preparadas. Márcalas como preparadas cuando estén listas.";
            return;
        }
        if (string.IsNullOrEmpty(vm.Destino)) { vm.Status = "Elige a qué carpeta de la biblioteca van."; return; }

        var conAvisos = preparadas.Count(r => r.HayAvisos);
        var nota = "Los archivos se mueven conservando su nombre. Si en el destino ya hay uno con el mismo nombre, esa canción se queda en la bandeja: nunca se sobrescribe nada. Se puede deshacer desde Enriquecer.";
        if (conAvisos > 0)
            nota = $"⚠ {conAvisos} de ellas tienen avisos sin resolver (duplicados, calidad baja o etiquetas incompletas). " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Pasar a la biblioteca",
            $"Se moverán {preparadas.Count} canción(es) preparadas a «{vm.Destino}».",
            nota,
            "Mover");
        if (ok) await vm.PasarABibliotecaAsync();
    }

    private async void Volcar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BandejaViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.CuantasSeleccionadas == 0) { vm.Status = "Selecciona antes las canciones."; return; }
        var ok = await ConfirmDialog.AskAsync(owner,
            "Escribir la ficha en el comentario",
            $"Se copiará la ficha de {vm.CuantasSeleccionadas} canción(es) al comentario de sus archivos, dentro de un bloque «[DJ: …]».",
            "Lo que ya hubiera en el comentario se conserva. El cambio se puede deshacer desde Enriquecer.",
            "Escribir");
        if (ok) await vm.VolcarAlComentarioAsync();
    }
}
