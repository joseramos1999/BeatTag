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
        DataContextChanged += (_, _) => VigilarCriterio();
    }

    // ---- Columna Edición (A14) ----
    // Solo dice algo al agrupar por audio: con los demás criterios un grupo son copias del mismo
    // archivo y la columna saldría siempre vacía, quitando sitio a Archivo y Carpeta.

    private ViewModels.DuplicatesViewModel? _vm;

    private void VigilarCriterio()
    {
        if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as ViewModels.DuplicatesViewModel;
        if (_vm != null) _vm.PropertyChanged += Vm_PropertyChanged;
        MostrarColumnaEdicion();
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.DuplicatesViewModel.EsPorHuella)) MostrarColumnaEdicion();
    }

    private void MostrarColumnaEdicion()
    {
        if (this.FindControl<DataGrid>("Grid") is not { } tabla) return;
        foreach (var columna in tabla.Columns)
            if (Equals(columna.Header, "Edición")) columna.IsVisible = _vm?.EsPorHuella == true;
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
        if (vm.EnviandoAPapelera) return;

        var objetivo = vm.ParaPapelera();
        if (objetivo.Count == 0) { vm.Status = "No hay ninguna copia marcada."; return; }

        // Aviso claro si alguna marcada es la que se recomendaba conservar, o si alguna canción se
        // quedaría sin ninguna copia: puede ser deliberado, pero conviene que no pase inadvertido.
        var ok = await ConfirmDialog.AskAsync(owner,
            objetivo.Count == 1 ? "Se va a enviar 1 archivo a la papelera" : $"Se van a enviar {objetivo.Count} archivos a la papelera",
            ViewModels.DuplicatesViewModel.CuerpoConfirmarPapelera(vm.Rows),
            Services.Papelera.NotaConfirmacion(objetivo.Select(r => r.FilePath)),
            "Enviar a la papelera");

        // Los archivos se envían en segundo plano: la ventana sigue respondiendo y el estado dice por dónde va.
        if (ok) await vm.SendMarkedToRecycleBinAsync();
    }
}
