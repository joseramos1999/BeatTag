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

        // Los anchos guardados con el esquema anterior no valen: se escribían en CUALQUIER clic
        // sobre la tabla, así que reflejan el momento en que se pinchó una fila, no una preferencia.
        // Respetarlos impediría el ajuste automático al ancho de la ventana. Cambiando la clave se
        // ignoran de una vez, sin tener que tocar la configuración del usuario por fuera.
        tableId += "/v2";

        // Restaurar lo guardado. Si el usuario ya colocó las columnas a su gusto, manda su elección
        // y NO se reajusta al ancho de la ventana: sería deshacerle el trabajo en cada arranque.
        var hayGuardados = false;
        if (config.ColumnWidths.TryGetValue(tableId, out var saved))
        {
            foreach (var col in grid.Columns)
            {
                var key = col.Header?.ToString();
                if (key != null && saved.TryGetValue(key, out var w) && w > 20)
                {
                    col.Width = new DataGridLength(w, DataGridLengthUnitType.Pixel);
                    hayGuardados = true;
                }
            }
        }

        if (!hayGuardados) AjustarAlAnchoDisponible(grid);

        // Se guarda solo si el usuario ha MOVIDO de verdad un borde: se anotan los anchos al pulsar
        // y se comparan al soltar. Antes se guardaba en cualquier clic sobre la tabla, de modo que
        // pinchar una fila fijaba los anchos de ese momento como si fueran una preferencia, y a
        // partir de ahí la tabla ya no volvía a ajustarse sola.
        Dictionary<string, double>? alPulsar = null;

        grid.AddHandler(InputElement.PointerPressedEvent, (s, _) =>
        {
            alPulsar = AnchosActuales(grid);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        grid.AddHandler(InputElement.PointerReleasedEvent, (s, _) =>
        {
            try
            {
                var anchos = AnchosActuales(grid);
                if (anchos.Count == 0) return;
                if (alPulsar == null || SameWidths(alPulsar, anchos)) return;   // no hubo redimensión
                if (config.ColumnWidths.TryGetValue(tableId, out var prev) && SameWidths(prev, anchos)) return;
                config.ColumnWidths[tableId] = anchos;
                saveConfig();
            }
            catch { /* recordar anchos nunca debe molestar */ }
            finally { alPulsar = null; }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // El auto-ajuste por doble clic también es una decisión del usuario y debe recordarse, pero
        // ocurre DESPUÉS de soltar el ratón, así que la comparación de arriba no lo ve. Además el
        // ancho resultante no se conoce hasta que la tabla se rediseña, de ahí la espera.
        grid.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Control c || c.FindAncestorOfType<DataGridColumnHeader>() is null) return;
            void TrasAjustar(object? s, EventArgs _)
            {
                grid.LayoutUpdated -= TrasAjustar;
                try
                {
                    var anchos = AnchosActuales(grid);
                    if (anchos.Count == 0) return;
                    if (config.ColumnWidths.TryGetValue(tableId, out var prev) && SameWidths(prev, anchos)) return;
                    config.ColumnWidths[tableId] = anchos;
                    saveConfig();
                }
                catch { }
            }
            grid.LayoutUpdated += TrasAjustar;
        };
    }

    private static Dictionary<string, double> AnchosActuales(DataGrid grid)
    {
        var anchos = new Dictionary<string, double>();
        foreach (var col in grid.Columns)
        {
            var key = col.Header?.ToString();
            if (key != null && col.ActualWidth > 20) anchos[key] = Math.Round(col.ActualWidth);
        }
        return anchos;
    }

    private static bool SameWidths(Dictionary<string, double> a, Dictionary<string, double> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < 1);

    /// <summary>
    /// Hace que la tabla ocupe justo el ancho disponible desde el primer momento, conservando las
    /// proporciones pensadas en el XAML.
    ///
    /// Se espera al primer diseño porque hasta entonces ActualWidth vale 0. En ese momento esos
    /// anchos ya reflejan lo previsto (la columna con «*» se ha quedado con el resto), así que
    /// convertirlos a peso en estrellas reproduce la misma proporción llenando siempre la ventana:
    /// si las columnas no cabían se comprimen —adiós barra horizontal— y si sobraba sitio lo
    /// reparten en lugar de dejar un hueco a la derecha.
    /// </summary>
    private static void AjustarAlAnchoDisponible(DataGrid grid)
    {
        void AlDisenarse(object? s, EventArgs e)
        {
            if (grid.Bounds.Width <= 0) return;                       // aún sin medir
            var anchos = grid.Columns.Select(c => c.ActualWidth).ToList();
            if (anchos.Count == 0 || anchos.Any(w => w <= 0)) return; // alguna columna sin medir

            grid.LayoutUpdated -= AlDisenarse;                        // una sola vez
            for (int i = 0; i < grid.Columns.Count; i++)
                grid.Columns[i].Width = new DataGridLength(anchos[i], DataGridLengthUnitType.Star);
        }
        grid.LayoutUpdated += AlDisenarse;
    }

    /// <summary>
    /// Doble clic en una cabecera: auto-ajusta al contenido SOLO esa columna. Antes las ajustaba
    /// todas, de modo que arreglar una columna estrecha descolocaba el resto de la tabla.
    /// </summary>
    public static bool AutoFitOnHeaderDoubleTap(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return false;
        if (e.Source is not Control c) return false;
        var cabecera = c.FindAncestorOfType<DataGridColumnHeader>();
        if (cabecera is null) return false;

        // DataGridColumnHeader no expone su columna públicamente, pero su contenido es la cabecera
        // declarada en el XAML, y dentro de una tabla no se repiten.
        var texto = cabecera.Content?.ToString();
        var columna = texto == null
            ? null
            : grid.Columns.FirstOrDefault(col => col.Header?.ToString() == texto);

        if (columna == null) return false;
        columna.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
        return true;
    }
}
