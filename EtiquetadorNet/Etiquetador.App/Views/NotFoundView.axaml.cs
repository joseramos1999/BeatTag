using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class NotFoundView : UserControl
{
    public NotFoundView() => InitializeComponent();

    // "Reanalizar…": mismo diálogo que en Enriquecer (corregir la búsqueda, elegir entre las
    // coincidencias, pegar un enlace o identificar por huella). Si aparece, se aplica.
    private async void Reanalyze_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NotFoundViewModel vm) return;
        if (vm.SelectedRow is not { } row) { vm.Status = "Selecciona antes una canción de la lista."; return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) { vm.Status = "No se pudo abrir la ventana de búsqueda."; return; }

        // Parámetros ORIGINALES, los deducidos del nombre del archivo.
        var pr = Etiquetador.Core.Pipeline.FileNameParser.Parse(row.FileName);
        var artist = pr.FnArtist;
        var title = pr.QTitle.Length > 0 ? pr.QTitle : pr.FnTitle;

        var terms = await SearchDialog.AskAsync(owner, row.FileName, artist, title, vm.SearchServicesFor(row.FilePath));
        if (terms == null) return;   // cancelado
        await vm.ReanalyzeRowAsync(row, terms.Artist, terms.Title, terms.Source);
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item })
            grid.SelectedItem = item;
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);
}
