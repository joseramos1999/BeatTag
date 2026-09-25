using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class AsistenteView : UserControl
{
    public AsistenteView()
    {
        InitializeComponent();

        // El mismo menú contextual que en el resto de pestañas, en las seis tablas. Cada herramienta
        // tiene su propio tipo de fila; el menú solo necesita saber si la fila es un archivo y si
        // tiene casilla (IFilaConArchivo, IFilaMarcable).
        foreach (var tabla in new[] { TablaBusqueda, TablaFichas, TablaGeneros, TablaRenombrar, TablaCompletar, TablaMezcla })
        {
            tabla.ContextMenu = CrearMenu(tabla);
            // El clic derecho selecciona la fila bajo el puntero, para que el menú actúe sobre ella.
            tabla.AddHandler(PointerPressedEvent, (s, e) => TeclaEnergia.SeleccionarConClicDerecho(s, e),
                             Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private ContextMenu CrearMenu(DataGrid tabla)
    {
        var escuchar = Opcion("Escuchar / Parar", () => ConArchivo(tabla, (vm, ruta) => vm.Engine.Preview.Toggle(ruta)));
        var editar = Opcion("Editar etiquetas de esta canción", () => ConArchivo(tabla, (vm, ruta) => vm.Engine.RequestEdit(ruta)));
        var marcar = Opcion("Marcar las seleccionadas", () => Marcar(tabla, true));
        var desmarcar = Opcion("Desmarcar las seleccionadas", () => Marcar(tabla, false));
        var carpeta = Opcion("Abrir carpeta contenedora", () => ConArchivo(tabla, (vm, ruta) => { _ = Services.Shell.OpenContainingFolderAsync(ruta); }));
        var separaMarcas = new Separator();
        var separaCarpeta = new Separator();

        var menu = new ContextMenu();
        foreach (var c in new Control[] { escuchar, editar, separaMarcas, marcar, desmarcar, separaCarpeta, carpeta })
            menu.Items.Add(c);

        // Qué se enseña depende de la tabla: la de géneros no tiene archivos y la de mezcla no tiene
        // casillas. Se mira la fila seleccionada o, sin selección, la primera.
        menu.Opening += (_, _) =>
        {
            var muestra = tabla.SelectedItem ?? (tabla.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().FirstOrDefault();
            var conArchivo = muestra is IFilaConArchivo;
            var marcable = muestra is IFilaMarcable;
            escuchar.IsVisible = editar.IsVisible = carpeta.IsVisible = separaCarpeta.IsVisible = conArchivo;
            marcar.IsVisible = desmarcar.IsVisible = marcable;
            separaMarcas.IsVisible = conArchivo && marcable;

            var haySeleccion = tabla.SelectedItem != null;
            escuchar.IsEnabled = editar.IsEnabled = carpeta.IsEnabled = marcar.IsEnabled = desmarcar.IsEnabled = haySeleccion;
        };
        return menu;
    }

    private static MenuItem Opcion(string texto, Action accion)
    {
        var item = new MenuItem { Header = texto };
        item.Click += (_, _) => accion();
        return item;
    }

    /// <summary>Ejecuta la acción sobre el archivo de la fila seleccionada; si falla, lo dice en la barra de estado.</summary>
    private void ConArchivo(DataGrid tabla, Action<AsistenteViewModel, string> accion)
    {
        if (DataContext is not AsistenteViewModel vm || tabla.SelectedItem is not IFilaConArchivo fila) return;
        try { accion(vm, fila.RutaArchivo); }
        catch (Exception e) { vm.Status = "No se pudo: " + e.Message; }
    }

    private static void Marcar(DataGrid tabla, bool valor)
    {
        foreach (var fila in tabla.SelectedItems.OfType<IFilaMarcable>().ToList())
            fila.Marcada = valor;
    }

    private async void AplicarGeneros_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Generos.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ningún género marcado."; return; }

        var canciones = marcadas.Sum(f => f.Canciones);
        var quitar = marcadas.Where(f => f.Propuesto.Trim().Length == 0).Sum(f => f.Canciones);
        var deIa = marcadas.Count(f => f.Origen == "IA");
        var nota = "Se escribe el género en las etiquetas de los archivos. Se puede deshacer desde Enriquecer.";
        if (quitar > 0) nota = $"En {quitar} de ellas se quitará el género. " + nota;
        if (deIa > 0) nota = $"⚠ {deIa} de los cambios son propuestas de la IA: comprueba que los has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Unificar géneros",
            $"Se cambiará el género de {canciones} canciones ({marcadas.Count} valores distintos).",
            nota, "Aplicar");
        if (ok) await vm.Generos.AplicarAsync();
    }

    private async void Renombrar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Renombrar.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ninguna propuesta marcada."; return; }

        var conAvisos = marcadas.Count(f => f.HayAvisos);
        var nota = "Solo cambia el nombre del archivo, no sus etiquetas. Si ya existe un archivo con el nombre nuevo, ese no se renombra. "
                 + "Se puede deshacer desde Enriquecer. Si usas rekordbox, después repara la colección en Ajustes para no perder los cue points.";
        if (conAvisos > 0) nota = $"⚠ {conAvisos} de ellas tienen avisos: comprueba que las has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Renombrar con IA",
            $"Se renombrarán {marcadas.Count} archivos con el nombre propuesto (o el que hayas corregido).",
            nota, "Renombrar");
        if (ok) await vm.Renombrar.RenombrarAsync();
    }

    private async void Completar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Completar.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ninguna propuesta marcada."; return; }

        var deIa = marcadas.Count(f => f.Origen.StartsWith("IA", StringComparison.Ordinal));
        var nota = "Solo cambia el nombre del archivo, no sus etiquetas. Si ya existe un archivo con el nombre nuevo, ese no se renombra. "
                 + "Se puede deshacer desde Enriquecer. Si usas rekordbox, después repara la colección en Ajustes para no perder los cue points.";
        if (deIa > 0) nota = $"⚠ {deIa} vienen de la IA y no de las etiquetas del archivo: comprueba que las has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Completar nombres cortados",
            $"Se renombrarán {marcadas.Count} archivos con el nombre completo (o el que hayas corregido).",
            nota, "Completar");
        if (ok) await vm.Completar.RenombrarAsync();
    }

    private async void ExportarMezcla_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar lista ordenada",
            SuggestedFileName = Etiquetador.Core.TextUtils.Sanitize((vm.Mezcla.Coleccion ?? "Sesion") + " (ordenada)"),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Lista de reproducción") { Patterns = new[] { "*.m3u8" } } },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.Mezcla.ExportarM3u(destino);
    }

    /// <summary>Las condiciones descartadas se ven apagadas: siguen ahí para saber qué se ignoró y por qué.</summary>
    public static readonly IValueConverter Atenuar = new FuncValueConverter<bool, double>(usable => usable ? 1.0 : 0.55);
}
