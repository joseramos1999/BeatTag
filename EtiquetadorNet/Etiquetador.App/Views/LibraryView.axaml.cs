using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

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
            if (!string.IsNullOrEmpty(path)) { vm.AddFolder(path); added = true; }
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
            if (!string.IsNullOrEmpty(dir)) { vm.AddFolder(dir); added = true; }
        }
        if (added) await vm.ScanAsync();
    }
}
