using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

/// <summary>Página rekordbox (equipo «rekordbox», tanda 4): los cuadros de abrir y guardar, y los menús de las tablas.</summary>
public partial class RekordboxView : UserControl
{
    public RekordboxView()
    {
        InitializeComponent();
        // Cuatro tablas con columnas distintas: cada una recuerda sus anchos por separado.
        AttachedToVisualTree += (_, _) =>
        {
            var engine = Services.AppEngine.Current;
            if (engine == null) return;
            GridBehaviors.RememberColumnWidths(TablaFaltan, "RekordboxFaltan", engine.Config, engine.SaveConfig);
            GridBehaviors.RememberColumnWidths(TablaRotas, "RekordboxRotas", engine.Config, engine.SaveConfig);
            GridBehaviors.RememberColumnWidths(TablaSinPreparar, "RekordboxSinPreparar", engine.Config, engine.SaveConfig);
            GridBehaviors.RememberColumnWidths(TablaNunca, "RekordboxNunca", engine.Config, engine.SaveConfig);
        };
    }

    private RekordboxViewModel? Vm => DataContext as RekordboxViewModel;

    // La página solo rehace el cruce a la vista: el shell cambia IsVisible al cambiar de página.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == IsVisibleProperty || change.Property == DataContextProperty) && DataContext is IPaginaVisible p)
            p.Visible = IsVisible;
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) => TeclaEnergia.SeleccionarConClicDerecho(sender, e);

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private static readonly FilePickerFileType TipoXml = new("XML de rekordbox") { Patterns = new[] { "*.xml" } };
    private static readonly FilePickerFileType TipoM3u = new("Lista de reproducción") { Patterns = new[] { "*.m3u8" } };

    // --- La colección exportada ---

    private async void Abrir_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var elegidos = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abre la colección exportada de rekordbox",
            AllowMultiple = false,
            FileTypeFilter = new[] { TipoXml },
        });
        var ruta = elegidos.Count > 0 ? elegidos[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(ruta)) await vm.AbrirAsync(ruta);
    }

    // --- Menú de las tablas ---

    /// <summary>La fila elegida en la tabla de la que sale el menú (cada opción lleva su tabla en Tag).</summary>
    private IFilaConArchivo? FilaDelMenu(object? sender)
    {
        if (Vm is not { } vm) return null;
        return ((sender as MenuItem)?.Tag as string) switch
        {
            "Rotas" => vm.RotaElegida,
            "SinPreparar" => vm.SinPrepararElegida,
            "Nunca" => vm.NuncaElegida,
            _ => vm.FaltaElegida,
        };
    }

    private void Escuchar_Click(object? sender, RoutedEventArgs e) => Vm?.Escuchar(FilaDelMenu(sender));

    private async void AbrirCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.AbrirCarpetaAsync(FilaDelMenu(sender));
    }

    // --- Guardar las tablas como M3U8 ---

    private void GuardarFaltan_Click(object? sender, RoutedEventArgs e) => GuardarM3u(RekordboxViewModel.Tabla.Faltan);
    private void GuardarSinPreparar_Click(object? sender, RoutedEventArgs e) => GuardarM3u(RekordboxViewModel.Tabla.SinPreparar);
    private void GuardarNunca_Click(object? sender, RoutedEventArgs e) => GuardarM3u(RekordboxViewModel.Tabla.Nunca);

    private async void GuardarM3u(RekordboxViewModel.Tabla tabla)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        if (vm.Filas(tabla).Count == 0) { vm.Status = "La tabla está vacía: no hay nada que guardar."; return; }
        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar lista de reproducción",
            SuggestedFileName = TextUtils.Sanitize(vm.NombreSugerido(tabla)),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { TipoM3u },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.ExportarM3u(tabla, destino);
    }

    // --- Reparar ---

    private async void Reparar_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var origen = Services.AppEngine.Current?.Config.RekordboxXml ?? "";
        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar la colección reparada (un archivo nuevo)",
            SuggestedFileName = (origen.Length > 0 ? Path.GetFileNameWithoutExtension(origen) : "coleccion") + " reparada",
            DefaultExtension = "xml",
            FileTypeChoices = new[] { TipoXml },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) await vm.RepararAsync(destino);
    }

    // --- Exportar las listas (P11) ---

    private async void Exportar_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar las listas para rekordbox",
            SuggestedFileName = "BeatTag para rekordbox",
            DefaultExtension = "xml",
            FileTypeChoices = new[] { TipoXml },
        });
        var destino = archivo?.TryGetLocalPath();
        if (string.IsNullOrEmpty(destino)) return;
        if (!await PuedeSustituirAsync(destino)) return;
        await vm.ExportarAsync(destino);
    }

    private async void ActualizarExportacion_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!await PuedeSustituirAsync(vm.UltimaExportacion)) return;
        await vm.ActualizarExportacionAsync();
    }

    /// <summary>
    /// Un archivo que no escribió BeatTag (casi siempre, la colección exportada de rekordbox) solo se
    /// sustituye si se confirma: el cuadro de guardar pregunta igual por cualquier archivo, y elegir
    /// por error la colección la cambiaría por unas pocas listas.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> PuedeSustituirAsync(string destino)
    {
        if (ExportadorRekordbox.QueHayEn(destino) != DestinoExportacion.Otro) return true;
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await ConfirmDialog.AskAsync(owner,
            "Sustituir un archivo que no es de BeatTag",
            $"«{Path.GetFileName(destino)}» ya existe y no es una exportación de BeatTag: puede ser la colección exportada de rekordbox.",
            "Si lo sustituyes, se perderá lo que contiene y solo quedarán las listas de BeatTag. La base de datos de rekordbox no se toca, "
            + "pero una colección exportada solo se recupera volviendo a exportarla.",
            "Sustituir");
    }
}
