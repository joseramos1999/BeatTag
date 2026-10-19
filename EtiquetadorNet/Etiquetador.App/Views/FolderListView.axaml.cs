using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

/// <summary>Lista de carpetas reutilizable (cabecera con recuento + ✕ por carpeta). Hereda el DataContext del VM.</summary>
public partial class FolderListView : UserControl
{
    public FolderListView() => InitializeComponent();

    // --- F1: analizar una carpeta sin desmarcar las demás ---

    /// <summary>
    /// Enseña en cada carpeta «Analizar solo esta» y «Subcarpeta…». Solo lo pide Enriquecer: la
    /// misma lista sale en Biblioteca, donde no hay nada que analizar por carpeta.
    /// </summary>
    public static readonly StyledProperty<bool> PermiteAnalizarProperty =
        AvaloniaProperty.Register<FolderListView, bool>(nameof(PermiteAnalizar));

    public bool PermiteAnalizar
    {
        get => GetValue(PermiteAnalizarProperty);
        set => SetValue(PermiteAnalizarProperty, value);
    }

    private async void AnalizarCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is FolderItem carpeta) await AnalizarAsync(carpeta.Path);
    }

    // La subcarpeta se elige con el selector del sistema, abierto ya en esa carpeta. Lo que se elija
    // fuera de las carpetas de la lista se rechaza con su motivo (lo comprueba PrepararAmbito).
    private async void AnalizarSubcarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FolderItem carpeta || DataContext is not EnrichViewModel { IsBusy: false } vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IStorageFolder? inicio = null;
        try { inicio = await top.StorageProvider.TryGetFolderFromPathAsync(carpeta.Path); } catch { /* sin carpeta inicial */ }
        var nombre = Path.GetFileName(carpeta.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var elegidas = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Elige la carpeta de «{nombre}» que quieres analizar",
            AllowMultiple = false,
            SuggestedStartLocation = inicio,
        });
        if (elegidas.Count == 0) return;
        var ruta = elegidas[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(ruta)) { vm.Status = "Esa carpeta no está en este equipo; elige una carpeta local."; return; }
        await AnalizarAsync(ruta);
    }

    /// <summary>
    /// Analiza solo <paramref name="ruta"/>. Si su carpeta de la lista está desmarcada, pregunta antes
    /// de marcarla (ver <see cref="EnrichViewModel.PrepararAmbito(System.Collections.Generic.IEnumerable{FolderItem}, string)"/>).
    /// </summary>
    private async Task AnalizarAsync(string ruta)
    {
        if (DataContext is not EnrichViewModel { IsBusy: false } vm) return;
        var prep = vm.PrepararAmbito(ruta);
        if (prep.Ambito == null) { vm.Status = prep.Motivo; return; }
        if (prep.RaizDesmarcada)
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var c = EnrichViewModel.ConfirmacionCarpetaDesmarcada(prep.Ambito);
            if (!await ConfirmDialog.AskAsync(owner, c.Titulo, c.Cuerpo, c.Nota, c.Aceptar)) return;
        }
        await vm.AnalizarAmbitoAsync(prep.Ambito, marcarRaiz: prep.RaizDesmarcada);
    }
}
