using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Etiquetador.App.Views;

public partial class DuplicatesView : UserControl
{
    public DuplicatesView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Duplicados");
    }

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);

    // Clic derecho selecciona la fila bajo el puntero (para el menú contextual).
    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item })
            grid.SelectedItem = item;
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    // Marcar a mano una casilla: hay que rehacer la cuenta que ve el usuario.
    private void Cell_EditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (DataContext is ViewModels.DuplicatesViewModel vm) vm.MarcaCambiada();
    }

    // Enviar a la papelera es masivo y toca archivos: se confirma diciendo cuántos y desde dónde.
    private async void TrashMarked_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.DuplicatesViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var objetivo = vm.ParaPapelera();
        if (objetivo.Count == 0) { vm.Status = "No hay ninguna copia marcada."; return; }

        // Aviso claro si alguna marcada es la que se recomendaba conservar: puede ser deliberado,
        // pero conviene que no pase inadvertido.
        var mejores = objetivo.Count(r => r.EsMejor);
        var aviso = mejores > 0
            ? $" Atención: {mejores} de ellas son la copia recomendada de su grupo."
            : "";

        var ok = await ConfirmDialog.AskAsync(owner,
            $"Se van a enviar {objetivo.Count} archivos a la papelera",
            $"Afecta a {objetivo.Select(r => r.Group).Distinct().Count()} grupo(s) de duplicados." + aviso,
            "Van a la Papelera de Windows, así que puedes recuperarlos desde ahí si te arrepientes.",
            "Enviar a la papelera");

        if (ok) vm.SendMarkedToRecycleBin();
    }
}
