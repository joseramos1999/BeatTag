using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
