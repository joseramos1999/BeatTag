using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

/// <summary>
/// Lo que hacen los botones de Tendencias y Playlists, que necesita el selector de archivos de la
/// ventana y por eso vive en la vista. Es igual en las dos pestañas.
/// </summary>
internal static class AccionesLista
{
    /// <summary>Guarda como M3U8 lo que ya tienes de la lista. No copia archivos: apunta a ellos.</summary>
    public static async Task ExportarM3uAsync(Control vista, ListaFrenteBibliotecaViewModel vm)
    {
        var top = TopLevel.GetTopLevel(vista);
        if (top is null) return;

        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar lista de reproducción",
            SuggestedFileName = TextUtils.Sanitize(vm.NombreSugerido),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Lista de reproducción") { Patterns = new[] { "*.m3u8" } } },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.ExportarM3u(destino);
    }

    /// <summary>Guarda como M3U8 lo que tienes de la lista, en el orden para mezclar.</summary>
    public static async Task ExportarOrdenM3uAsync(Control vista, ListaFrenteBibliotecaViewModel vm)
    {
        var top = TopLevel.GetTopLevel(vista);
        if (top is null) return;

        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar en el orden para mezclar",
            SuggestedFileName = TextUtils.Sanitize(vm.NombreSugerido + " (orden de mezcla)"),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Lista de reproducción") { Patterns = new[] { "*.m3u8" } } },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.ExportarOrdenM3u(destino);
    }

    /// <summary>
    /// «Añadir a pendientes» de Tendencias y Playlists: las filas elegidas en la tabla que no están en
    /// la biblioteca, o todas las de la lista que no están.
    /// </summary>
    public static void AnadirAPendientes(DataGrid tabla, ListaFrenteBibliotecaViewModel vm, bool todasLasQueFaltan)
        => vm.AnadirAPendientes(tabla.SelectedItems.OfType<TrendRow>().ToList(), todasLasQueFaltan);

    /// <summary>
    /// Clic derecho en la tabla de Tendencias o Playlists: la fila pulsada queda elegida sin perder una
    /// selección de varias (<see cref="TeclaEnergia.SeleccionarConClicDerecho"/>), y las opciones de UNA
    /// canción del menú actúan sobre ella (<see cref="ListaFrenteBibliotecaViewModel.FilaDelMenu"/>).
    /// «Añadir a pendientes» sigue actuando sobre todas las elegidas, que es lo que se ve resaltado.
    /// </summary>
    public static void ClicEnTabla(object? sender, PointerPressedEventArgs e, ListaFrenteBibliotecaViewModel? vm)
    {
        TeclaEnergia.SeleccionarConClicDerecho(sender, e);
        if (vm != null && FilaPulsadaConDerecho<TrendRow>(sender, e) is { } fila) vm.FilaDelMenu = fila;
    }

    /// <summary>
    /// La selección de la tabla ha cambiado (clic, Ctrl+clic, teclado): el menú vuelve a actuar sobre la
    /// fila elegida hasta el próximo clic derecho. Si no, una fila pulsada hace rato seguiría mandando.
    /// </summary>
    public static void SeleccionCambiada(object? sender, ListaFrenteBibliotecaViewModel? vm)
    {
        if (vm != null && sender is DataGrid grid) vm.FilaDelMenu = grid.SelectedItem as TrendRow;
    }

    /// <summary>La fila de la tabla pulsada con el botón derecho, o null si no es un clic derecho sobre una fila.</summary>
    public static T? FilaPulsadaConDerecho<T>(object? sender, PointerPressedEventArgs e) where T : class
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return null;
        return e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: T fila } ? fila : null;
    }

    /// <summary>
    /// Sobre qué fila actúa una opción de UNA sola canción: la pulsada con el botón derecho si sigue
    /// elegida, o la elegida en la tabla, o la primera de la selección. Así, con varias filas elegidas,
    /// «Escuchar» o «Buscar en mi pool» actúan sobre la que se pulsó y no sobre otra cualquiera de las
    /// elegidas.
    /// </summary>
    public static T? FilaParaUnaCancion<T>(T? pulsada, T? elegida, IReadOnlyList<T> seleccion) where T : class
        => pulsada != null && seleccion.Contains(pulsada) ? pulsada
         : elegida ?? seleccion.FirstOrDefault();

    /// <summary>Copia a una carpeta nueva, con el nombre de la lista y la fecha, lo que ya tienes de ella.</summary>
    public static async Task CrearCarpetaAsync(Control vista, ListaFrenteBibliotecaViewModel vm)
    {
        var top = TopLevel.GetTopLevel(vista);
        if (top is null) return;

        var carpetas = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Selecciona dónde crear la carpeta",
            AllowMultiple = false,
        });
        if (carpetas.Count == 0) return;
        var baseDir = carpetas[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(baseDir)) return;

        // Subcarpeta con el nombre de la lista y la fecha, para no mezclar tiradas.
        await vm.CopyToFolderAsync(System.IO.Path.Combine(baseDir, TextUtils.Sanitize(vm.NombreSugerido)));
    }
}
