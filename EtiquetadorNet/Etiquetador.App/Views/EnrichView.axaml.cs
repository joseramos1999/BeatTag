using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class EnrichView : UserControl
{
    private bool _suppressToggle;   // true si el doble clic entró a editar una celda
    private INotifyCollectionChanged? _rowsHook;   // coleccion de filas seguida para el auto-scroll

    public EnrichView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
    }

    // Sigue la coleccion de filas del VM para desplazar al final cada vez que el analisis
    // añade una propuesta nueva.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_rowsHook != null) { _rowsHook.CollectionChanged -= OnRowsChanged; _rowsHook = null; }
        if (DataContext is EnrichViewModel vm)
        {
            _rowsHook = vm.Rows;
            _rowsHook.CollectionChanged += OnRowsChanged;
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Solo al añadir filas, y solo si la pestaña Enriquecer está visible. Este guardado
        // evita la cascada de arranque (cargar la caché con el grid oculto) que provocaba el crash.
        if (e.Action != NotifyCollectionChangedAction.Add || !IsEffectivelyVisible) return;
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    // Desplaza el ScrollViewer interno hasta el fondo. Deliberadamente NO usa ScrollIntoView(item):
    // en un DataGrid agrupado eso dispara el bug de layout de Avalonia (InsertDisplayedElement fuera
    // de rango). Mover el offset del ScrollViewer es seguro y Avalonia lo recorta al máximo válido.
    private void ScrollToEnd()
    {
        try
        {
            var sv = RowsGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv != null) sv.Offset = new Vector(sv.Offset.X, sv.Extent.Height);
        }
        catch { }
    }

    private async void PickCover_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || DataContext is not EnrichViewModel vm) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Elige la imagen de portada",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Imágenes") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } } }
        });
        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) vm.CoverPath = path;
        }
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || DataContext is not EnrichViewModel vm) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Añade una carpeta a enriquecer",
            AllowMultiple = true
        });
        foreach (var f in folders)
        {
            var path = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) vm.AddFolder(path);
        }
    }

    // Clic derecho: selecciona la fila bajo el puntero para que el menú contextual actúe sobre ella.
    private void Rows_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _suppressToggle = false;
        var props = e.GetCurrentPoint(RowsGrid).Properties;
        if (props.IsRightButtonPressed && e.Source is Control c)
        {
            var row = c.FindAncestorOfType<DataGridRow>();
            if (row?.DataContext is PreviewRow pr) RowsGrid.SelectedItem = pr;
        }
    }

    private void Rows_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e) => _suppressToggle = true;

    // "Reanalizar…": primero pregunta QUÉ buscar (para afinar cuando el nombre del archivo engaña).
    private async void Reanalyze_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EnrichViewModel vm || vm.SelectedRow is not { } row) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // Se propone lo que se buscaría por defecto: lo deducido del nombre del archivo.
        var pr = Etiquetador.Core.Pipeline.FileNameParser.Parse(row.Old);
        var artist = pr.FnArtist.Length > 0 ? pr.FnArtist : (row.Artist ?? "");
        var title = pr.QTitle.Length > 0 ? pr.QTitle : (row.Title ?? "");

        var terms = await SearchDialog.AskAsync(owner, row.Old, artist, title);
        if (terms == null) return;   // cancelado
        await vm.ReanalyzeSelectedAsync(terms.Artist, terms.Title);
    }

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);

    // Doble clic: en una cabecera auto-ajusta columnas; en una fila (celda no editable) marca/desmarca.
    private void Rows_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e)) return;
        if (_suppressToggle) { _suppressToggle = false; return; }
        if (sender is DataGrid { SelectedItem: PreviewRow row }) row.Toggle();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not EnrichViewModel vm) return;
        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;
        foreach (var it in items)
        {
            var p = it.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            var dir = Directory.Exists(p) ? p : Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) vm.AddFolder(dir);
        }
    }
}
