using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly DispatcherTimer _scrollTimer;  // agrupa las peticiones de scroll
    private bool _scrollPending;

    public EnrichView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);

        // Al analizar entran filas muy seguidas: se desplaza como mucho 3 veces por segundo,
        // en vez de una por cada canción (que iría a tirones y cargaría la interfaz).
        _scrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
        {
            if (!_scrollPending) return;
            _scrollPending = false;
            ScrollToEnd();
        });
        _scrollTimer.Start();
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
        // Solo al añadir filas, y solo con la pestaña Enriquecer a la vista: desplazar un grid oculto
        // fue lo que provocó el crash de arranque al cargar la caché.
        if (e.Action != NotifyCollectionChangedAction.Add || !IsEffectivelyVisible) return;
        _scrollPending = true;   // lo atiende el temporizador
    }

    /// <summary>
    /// Lleva la tabla a la última propuesta. El DataGrid de Avalonia NO se desplaza con un
    /// ScrollViewer (usa PART_VerticalScrollbar + PART_RowsPresenter), así que mover un ScrollViewer
    /// no hacía nada: hay que usar su propia API o, en su defecto, la barra de desplazamiento.
    /// </summary>
    private void ScrollToEnd()
    {
        if (!IsEffectivelyVisible) return;
        try
        {
            if (RowsGrid.ItemsSource is not System.Collections.IEnumerable items) return;
            object? last = null;
            foreach (var it in items) last = it;      // la vista agrupada no expone índice directo
            if (last == null) return;
            RowsGrid.ScrollIntoView(last, null);
        }
        catch
        {
            // Respaldo: mover la barra vertical al máximo.
            try
            {
                var bar = RowsGrid.GetVisualDescendants().OfType<ScrollBar>()
                                  .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
                if (bar != null) bar.Value = bar.Maximum;
            }
            catch { }
        }
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
        if (DataContext is not EnrichViewModel vm) return;
        if (vm.SelectedRow is not { } row) { vm.Status = "Selecciona antes una canción de la lista."; return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) { vm.Status = "No se pudo abrir la ventana de búsqueda."; return; }

        // Se proponen SIEMPRE los parámetros ORIGINALES (los que salen del nombre del archivo),
        // no la propuesta ya calculada: si esta era mala, partir de ella arrastraría el error.
        var pr = Etiquetador.Core.Pipeline.FileNameParser.Parse(row.Old);
        var artist = pr.FnArtist;
        var title = pr.QTitle.Length > 0 ? pr.QTitle : pr.FnTitle;
        // Solo si el nombre no daba nada se recurre a lo propuesto.
        if (artist.Length == 0 && title.Length == 0) { artist = row.Artist ?? ""; title = row.Title ?? ""; }

        var svc = new SearchServices(vm.FindCandidatesAsync, vm.ResolveLinkAsync,
            () => vm.IdentifyByFingerprintAsync(row.Result.FilePath));
        var terms = await SearchDialog.AskAsync(owner, row.Old, artist, title, svc);
        if (terms == null) return;   // cancelado
        await vm.ReanalyzeRowAsync(row, terms.Artist, terms.Title, terms.Source);   // la fila capturada, no SelectedRow
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
