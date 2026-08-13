using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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

    // Clic derecho: selecciona la fila bajo el puntero para que el menú actúe sobre ella.
    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item })
            grid.SelectedItem = item;
    }

    // Copia a una carpeta las canciones del chart que ya tienes.
    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TrendsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var carpetas = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Selecciona dónde crear la carpeta",
            AllowMultiple = false,
        });
        if (carpetas.Count == 0) return;
        var baseDir = carpetas[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(baseDir)) return;

        // Subcarpeta con el país y la fecha, para no mezclar tiradas.
        var nombre = $"Tendencias {vm.SelectedCountry?.Name} {System.DateTime.Now:yyyy-MM-dd}";
        var destino = System.IO.Path.Combine(baseDir, Etiquetador.Core.TextUtils.Sanitize(nombre));
        await vm.CopyToFolderAsync(destino);
    }
}
