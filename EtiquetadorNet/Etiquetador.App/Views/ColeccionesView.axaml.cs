using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class ColeccionesView : UserControl
{
    public ColeccionesView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Colecciones");
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) => TeclaEnergia.SeleccionarConClicDerecho(sender, e);

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private async void Eliminar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ColeccionesViewModel vm || vm.Seleccionada == null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var ok = await ConfirmDialog.AskAsync(owner,
            "Eliminar colección",
            $"Se eliminará la colección «{vm.Seleccionada.Nombre}».",
            "Solo se borran sus condiciones. Las canciones, sus fichas y las listas M3U8 ya guardadas no se tocan.",
            "Eliminar");
        if (ok) vm.EliminarSeleccionada();
    }

    private async void ExportM3u_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ColeccionesViewModel vm || vm.Seleccionada == null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar lista de reproducción",
            SuggestedFileName = Etiquetador.Core.TextUtils.Sanitize(vm.Seleccionada.Nombre),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Lista de reproducción") { Patterns = new[] { "*.m3u8" } } },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.ExportarM3u(destino);
    }
}
