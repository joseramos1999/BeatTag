using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Etiquetador.App.Views;

/// <summary>Comportamientos compartidos para los DataGrid.</summary>
public static class GridBehaviors
{
    /// <summary>Despliega o cierra TODOS los grupos (carpetas) del DataGrid agrupado.</summary>
    public static void SetAllGroups(DataGrid? grid, bool expand)
    {
        if (grid?.ItemsSource is not DataGridCollectionView view || view.Groups is null) return;
        try { grid.CommitEdit(); } catch { }
        grid.SelectedItem = null;

        // CLAVE: ir arriba del todo antes de colapsar. Si no, el DataGrid intenta pintar un slot
        // que ya no existe tras colapsar y peta en el layout (InsertDisplayedElement fuera de rango).
        try
        {
            var sv = grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv != null) sv.Offset = new Vector(sv.Offset.X, 0);
        }
        catch { }

        // Colapsar de abajo a arriba (no desplaza los slots aún por procesar); expandir de arriba a abajo.
        var groups = view.Groups.OfType<DataGridCollectionViewGroup>().ToList();
        if (!expand) groups.Reverse();
        foreach (var g in groups)
        {
            try { if (expand) grid.ExpandRowGroup(g, true); else grid.CollapseRowGroup(g, true); }
            catch { }
        }
    }

    /// <summary>Doble clic en una cabecera de columna: auto-ajusta TODAS las columnas al contenido.</summary>
    public static bool AutoFitOnHeaderDoubleTap(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return false;
        if (e.Source is not Control c || c.FindAncestorOfType<DataGridColumnHeader>() is null) return false;
        foreach (var col in grid.Columns)
            col.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
        return true;
    }
}
