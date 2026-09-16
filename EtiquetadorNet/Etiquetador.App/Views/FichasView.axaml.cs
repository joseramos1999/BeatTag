using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class FichasView : UserControl
{
    public FichasView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "FichasDj");
    }

    private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is FichasViewModel vm)
            vm.Seleccionar(grid.SelectedItems.OfType<FichaRow>());
    }

    private void Grid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FichasViewModel vm || e.KeyModifiers != KeyModifiers.None) return;
        if (TeclaEnergia.De(e.Key) is int energia)
        {
            vm.PonerEnergiaRapida(energia);
            e.Handled = true;
        }
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) => TeclaEnergia.SeleccionarConClicDerecho(sender, e);

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private async void Volcar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FichasViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.CuantasSeleccionadas == 0) { vm.Status = "Selecciona antes las canciones."; return; }
        var ok = await ConfirmDialog.AskAsync(owner,
            "Escribir la ficha en el comentario",
            $"Se copiará la ficha de {vm.CuantasSeleccionadas} canción(es) al comentario de sus archivos, dentro de un bloque «[DJ: …]».",
            "Lo que ya hubiera en el comentario se conserva, y volver a escribirla reemplaza el bloque en vez de repetirlo. El cambio se puede deshacer desde Enriquecer.",
            "Escribir");
        if (ok) await vm.VolcarAlComentarioAsync();
    }

    private async void Borrar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FichasViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.CuantasSeleccionadasConFicha == 0) { vm.Status = "Ninguna de las seleccionadas tiene ficha."; return; }
        var ok = await ConfirmDialog.AskAsync(owner,
            "Borrar ficha",
            $"Se borrará la ficha de {vm.CuantasSeleccionadasConFicha} canción(es).",
            "Los archivos no se modifican. Si la ficha ya se escribió en el comentario, el comentario se queda como está.",
            "Borrar");
        if (ok) vm.BorrarSeleccion();
    }
}

/// <summary>Detalles de teclado y ratón que comparten las tablas con ficha de DJ.</summary>
internal static class TeclaEnergia
{
    /// <summary>1-9 ponen esa energía y 0 pone 10, en el teclado principal o el numérico.</summary>
    public static int? De(Key k) => k switch
    {
        >= Key.D1 and <= Key.D9 => k - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => k - Key.NumPad0,
        Key.D0 or Key.NumPad0 => 10,
        _ => null,
    };

    /// <summary>
    /// El clic derecho sobre una fila no seleccionada la selecciona, para que el menú actúe sobre lo
    /// que se ve. Sobre una fila que ya forma parte de la selección no hace nada, y así no se pierde
    /// una selección múltiple al abrir el menú.
    /// </summary>
    public static void SeleccionarConClicDerecho(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item }
            && !grid.SelectedItems.Contains(item))
            grid.SelectedItem = item;
    }
}
