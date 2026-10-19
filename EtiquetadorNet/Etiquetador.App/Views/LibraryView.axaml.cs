using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Etiquetador.App.Acciones;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Biblioteca");
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);

        TablaBiblioteca.ContextMenu = CrearMenu();
        // El clic derecho selecciona la fila bajo el puntero si no estaba ya en la selección, para que
        // el menú actúe sobre lo que se ve sin perder una selección múltiple.
        TablaBiblioteca.AddHandler(PointerPressedEvent, (s, e) => TeclaEnergia.SeleccionarConClicDerecho(s, e),
                                   RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // --- Menú contextual ---

    private List<Track> Seleccion() => TablaBiblioteca.SelectedItems.OfType<Track>().ToList();

    private ContextMenu CrearMenu()
    {
        MenuItem Opcion(string texto, Action accion)
        {
            var item = new MenuItem { Header = texto };
            item.Click += (_, _) => accion();
            return item;
        }

        var escuchar = Opcion("Escuchar / Parar", () => ConUna((vm, ruta) => vm.Escuchar(ruta)));
        var editar = Opcion("Editar etiquetas de esta canción", () => ConUna((vm, ruta) => vm.Editar(ruta)));
        var compatibles = Opcion("Buscar compatibles…", () => ConUna((vm, ruta) => { _ = AccionesCancion.AbrirCompatiblesAsync(ruta); }));
        var versiones = Opcion("Ver sus versiones…", () => ConUna((vm, ruta) => { _ = AccionesCancion.AbrirVersionesAsync(ruta); }));
        var ordenar = Opcion("Ordenar para mezclar la selección…", () => _ = OrdenarSeleccionAsync());
        var carpeta = Opcion("Abrir carpeta contenedora", () => ConUna((vm, ruta) => { _ = Services.Shell.OpenContainingFolderAsync(ruta); }));
        var papelera = Opcion("Enviar a la papelera", () => _ = EnviarAPapeleraAsync());

        var menu = new ContextMenu();
        foreach (var c in new Control[] { escuchar, editar, new Separator(), compatibles, versiones, ordenar, new Separator(), carpeta, papelera })
            menu.Items.Add(c);

        menu.Opening += (_, _) =>
        {
            var vm = DataContext as LibraryViewModel;
            var estado = LibraryViewModel.CalcularMenu(TablaBiblioteca.SelectedItems.Count, vm?.Ocupada == true);
            escuchar.IsEnabled = estado.Escuchar;
            editar.IsEnabled = estado.Editar;
            compatibles.IsEnabled = versiones.IsEnabled = estado.Buscar;
            ordenar.IsEnabled = estado.Ordenar;
            carpeta.IsEnabled = estado.Carpeta;
            papelera.IsEnabled = estado.Papelera;
            escuchar.Header = "Escuchar / Parar" + estado.Espera;
            editar.Header = "Editar etiquetas de esta canción" + estado.Espera;
            ordenar.Header = estado.TextoOrdenar;
            papelera.Header = estado.TextoPapelera + estado.Espera;
        };
        return menu;
    }

    /// <summary>
    /// La acción sobre la canción seleccionada (la que tiene el foco si hay varias). Se vuelve a mirar
    /// al pulsar: el menú pudo abrirse antes de que empezara un escaneo.
    /// </summary>
    private void ConUna(Action<LibraryViewModel, string> accion)
    {
        if (DataContext is not LibraryViewModel vm || TablaBiblioteca.SelectedItem is not Track t) return;
        try { accion(vm, t.FilePath); }
        catch (Exception e) { vm.Status = "No se pudo: " + e.Message; }
    }

    private async Task OrdenarSeleccionAsync()
    {
        if (DataContext is not LibraryViewModel vm) return;
        var seleccion = Seleccion();
        if (!vm.PuedeOrdenar(seleccion.Count)) return;
        await AccionesCancion.AbrirOrdenAsync(seleccion, "Selección de la biblioteca (ordenada)");
    }

    /// <summary>
    /// Envía a la papelera las canciones seleccionadas, tras confirmar. Van a la papelera del sistema,
    /// así que se pueden recuperar; en una unidad sin papelera se niega y lo dice.
    /// </summary>
    private async Task EnviarAPapeleraAsync()
    {
        if (DataContext is not LibraryViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.Ocupada) { vm.Status = "Espera a que termine el proceso en curso."; return; }
        var rutas = Seleccion().Select(t => t.FilePath).ToList();
        if (rutas.Count == 0) return;

        var titulo = rutas.Count == 1 ? "Se va a enviar a la papelera" : $"Se van a enviar {rutas.Count} archivos a la papelera";
        var cuerpo = rutas.Count == 1
            ? Path.GetFileName(rutas[0])
            : string.Join("\n", rutas.Take(5).Select(Path.GetFileName)) + (rutas.Count > 5 ? $"\n… y {rutas.Count - 5} más" : "");
        var ok = await ConfirmDialog.AskAsync(owner, titulo, cuerpo, Services.Papelera.NotaConfirmacion(rutas), "Enviar a la papelera");
        if (ok) vm.EnviarAPapelera(rutas);
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || DataContext is not LibraryViewModel vm) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Añade una carpeta de música",
            AllowMultiple = true
        });
        var added = false;
        foreach (var f in folders)
        {
            var path = f.TryGetLocalPath();
            // Solo se escanea si alguna ha entrado de verdad: una carpeta que ya estaba dentro de
            // otra se rechaza, y ponerse a releer el disco entero por nada sería absurdo.
            if (!string.IsNullOrEmpty(path) && vm.AddFolder(path)) added = true;
        }
        if (added) await vm.ScanAsync();
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);

    private async void ImportRekordbox_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || DataContext is not LibraryViewModel vm) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Elige el XML exportado de rekordbox",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("XML de rekordbox") { Patterns = new[] { "*.xml" } } }
        });
        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) await vm.ImportRekordboxAsync(path);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;
        var added = false;
        foreach (var it in items)
        {
            var p = it.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            var dir = Directory.Exists(p) ? p : Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && vm.AddFolder(dir)) added = true;
        }
        if (added) await vm.ScanAsync();
    }
}
