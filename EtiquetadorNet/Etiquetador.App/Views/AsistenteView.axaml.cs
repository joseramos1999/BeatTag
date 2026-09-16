using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class AsistenteView : UserControl
{
    public AsistenteView() => InitializeComponent();

    private async void AplicarGeneros_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Generos.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ningún género marcado."; return; }

        var canciones = marcadas.Sum(f => f.Canciones);
        var quitar = marcadas.Where(f => f.Propuesto.Trim().Length == 0).Sum(f => f.Canciones);
        var deIa = marcadas.Count(f => f.Origen == "IA");
        var nota = "Se escribe el género en las etiquetas de los archivos. Se puede deshacer desde Enriquecer.";
        if (quitar > 0) nota = $"En {quitar} de ellas se quitará el género. " + nota;
        if (deIa > 0) nota = $"⚠ {deIa} de los cambios son propuestas de la IA: comprueba que los has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Unificar géneros",
            $"Se cambiará el género de {canciones} canciones ({marcadas.Count} valores distintos).",
            nota, "Aplicar");
        if (ok) await vm.Generos.AplicarAsync();
    }

    private async void Renombrar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Renombrar.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ninguna propuesta marcada."; return; }

        var conAvisos = marcadas.Count(f => f.HayAvisos);
        var nota = "Solo cambia el nombre del archivo, no sus etiquetas. Si ya existe un archivo con el nombre nuevo, ese no se renombra. "
                 + "Se puede deshacer desde Enriquecer. Si usas rekordbox, después repara la colección en Ajustes para no perder los cue points.";
        if (conAvisos > 0) nota = $"⚠ {conAvisos} de ellas tienen avisos: comprueba que las has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Renombrar con IA",
            $"Se renombrarán {marcadas.Count} archivos con el nombre propuesto (o el que hayas corregido).",
            nota, "Renombrar");
        if (ok) await vm.Renombrar.RenombrarAsync();
    }

    private async void ExportarMezcla_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar lista ordenada",
            SuggestedFileName = Etiquetador.Core.TextUtils.Sanitize((vm.Mezcla.Coleccion ?? "Sesion") + " (ordenada)"),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Lista de reproducción") { Patterns = new[] { "*.m3u8" } } },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.Mezcla.ExportarM3u(destino);
    }

    /// <summary>Las condiciones descartadas se ven apagadas: siguen ahí para saber qué se ignoró y por qué.</summary>
    public static readonly IValueConverter Atenuar = new FuncValueConverter<bool, double>(usable => usable ? 1.0 : 0.55);
}
