using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Linq;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class NotFoundView : UserControl
{
    public NotFoundView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "NoEncontradas");
    }

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

        // Pulsar con el derecho apunta a la fila de debajo, salvo que YA forme parte de una
        // selección: ahí se deja intacta, o abrir el menú para actuar sobre un bloque lo reduciría
        // a una sola fila justo antes de ejecutar la acción.
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item }
            && !grid.SelectedItems.Contains(item))
            grid.SelectedItem = item;
    }

    /// <summary>Lleva la selección al modelo: el DataGrid de Avalonia no deja enlazar SelectedItems.</summary>
    private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not NotFoundViewModel vm) return;
        vm.SetSelection(grid.SelectedItems.OfType<NotFoundRow>());
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);
}
