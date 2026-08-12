using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Etiquetador.Core;

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
        // El DataGrid no lleva ScrollViewer dentro (comprobado): se mueve su barra vertical.
        try
        {
            var bar = grid.GetVisualDescendants().OfType<ScrollBar>()
                          .FirstOrDefault(b => b.Orientation == Orientation.Vertical);
            if (bar != null) bar.Value = 0;
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

    /// <summary>
    /// Recuerda el ancho de las columnas que el usuario ajusta a mano: al soltar el ratón se guarda,
    /// y al abrir la tabla se restaura. Así no hay que recolocarlas en cada sesión.
    /// Se identifica cada tabla por <paramref name="tableId"/> y cada columna por su cabecera.
    /// </summary>
    /// <summary>Activa la memoria de anchos en la tabla de esta vista (se llama en su constructor).</summary>
    public static void EnableWidthMemory(Control view, string tableId)
    {
        view.AttachedToVisualTree += (_, _) =>
        {
            var engine = Services.AppEngine.Current;
            if (engine == null) return;
            RememberColumnWidths(view.FindDescendantOfType<DataGrid>(), tableId, engine.Config, engine.SaveConfig);
        };
    }

    public static void RememberColumnWidths(DataGrid? grid, string tableId, AppConfig config, Action saveConfig)
    {
        if (grid == null) return;

        // Restaurar lo guardado.
        if (config.ColumnWidths.TryGetValue(tableId, out var saved))
        {
            foreach (var col in grid.Columns)
            {
                var key = col.Header?.ToString();
                if (key != null && saved.TryGetValue(key, out var w) && w > 20)
                    col.Width = new DataGridLength(w, DataGridLengthUnitType.Pixel);
            }
        }

        // Guardar cuando el usuario suelta el borde de una columna.
        grid.AddHandler(InputElement.PointerReleasedEvent, (s, _) =>
        {
            try
            {
                var anchos = new Dictionary<string, double>();
                foreach (var col in grid.Columns)
                {
                    var key = col.Header?.ToString();
                    if (key != null && col.ActualWidth > 20) anchos[key] = Math.Round(col.ActualWidth);
                }
                if (anchos.Count == 0) return;
                if (config.ColumnWidths.TryGetValue(tableId, out var prev) && SameWidths(prev, anchos)) return;
                config.ColumnWidths[tableId] = anchos;
                saveConfig();
            }
            catch { /* recordar anchos nunca debe molestar */ }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static bool SameWidths(Dictionary<string, double> a, Dictionary<string, double> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < 1);

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
