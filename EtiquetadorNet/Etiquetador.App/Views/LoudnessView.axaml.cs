using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Linq;
using Avalonia.VisualTree;

namespace Etiquetador.App.Views;

public partial class LoudnessView : UserControl
{
    public LoudnessView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Volumen");
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    // Ajustar el volumen modifica los archivos del usuario: se confirma antes, indicando
    // exactamente cuántos se van a tocar y qué implica.
    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.LoudnessViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var objetivo = vm.ParaAjustar();
        if (objetivo.Count == 0)
        {
            vm.Status = "No hay ninguna grabación que requiera ajuste.";
            return;
        }

        var suben = objetivo.Count(r => r.Gain > 0);
        var bajan = objetivo.Count - suben;
        var parciales = objetivo.Count(r => r.Satura);

        var ok = await ConfirmDialog.AskAsync(owner,
            $"Se va a ajustar el volumen de {objetivo.Count} grabaciones",
            $"Se aumentará el nivel de {suben} y se reducirá el de {bajan}. Solo se modifican las que "
            + $"se desvían más de {ViewModels.LoudnessViewModel.UmbralAjuste:0} dB del nivel de referencia."
            + (parciales > 0
                ? $" En {parciales} el aumento será parcial, porque no admiten más sin distorsionar."
                : ""),
            "El audio no se recodifica, por lo que no hay pérdida de calidad y los archivos mantienen su tamaño. "
            + "El cambio queda registrado y se puede revertir con «Deshacer última» desde Enriquecer.",
            "Ajustar volumen");

        if (ok) await vm.ApplyAsync();
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item })
            grid.SelectedItem = item;
    }
}
