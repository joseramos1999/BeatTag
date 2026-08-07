using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;

namespace Etiquetador.App.ViewModels;

public sealed class IncRow
{
    public string FileName { get; init; } = "";
    public string Missing { get; init; } = "";
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Genre { get; init; }
    public uint Year { get; init; }
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";
}

/// <summary>Pestaña Incompletas: tracks de la biblioteca compartida a los que les falta algún tag.</summary>
public partial class IncompleteViewModel : ScanViewModelBase
{
    private readonly AppEngine _engine;

    public ObservableCollection<IncRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private IncRow? _selectedRow;

    public IncompleteViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(IncRow.Folder)));
        if (Store.IsScanned) Recompute();
    }

    private static string MissingFields(Track t)
    {
        var m = new List<string>();
        if (string.IsNullOrWhiteSpace(t.Title)) m.Add("Título");
        if (string.IsNullOrWhiteSpace(t.Artist)) m.Add("Artista");
        if (string.IsNullOrWhiteSpace(t.Genre)) m.Add("Género");
        if (t.Year == 0) m.Add("Año");
        return string.Join(", ", m);
    }

    protected override void Recompute()
    {
        Rows.Clear();
        foreach (var t in Store.Tracks)
        {
            if (!t.IsIncomplete) continue;
            Rows.Add(new IncRow
            {
                FileName = t.FileName,
                Missing = MissingFields(t),
                Title = t.Title,
                Artist = t.Artist,
                Genre = t.Genre,
                Year = t.Year,
                Folder = t.Folder,
                FilePath = t.FilePath,
            });
        }
        RowsView.Refresh();
        Status = $"{Rows.Count} incompletas de {Store.Tracks.Count} en la biblioteca.";
    }

    [RelayCommand]
    private void EditThis()
    {
        if (SelectedRow != null) _engine.RequestEdit(SelectedRow.FilePath);
    }

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    /// <summary>Reanaliza y, si encuentra la canción, escribe los tags (undo disponible) y refresca.</summary>
    [RelayCommand]
    private async Task ReanalyzeThis()
    {
        var row = SelectedRow;
        if (row == null || IsBusy) return;
        IsBusy = true;
        Status = "Reanalizando " + row.FileName + "…";
        try
        {
            var opts = _engine.BuildOptions();
            var r = await Task.Run(() => _engine.Processor.ProcessAsync(row.FilePath, isAcapella: false, opts));
            if (r.Skip || !r.Found) { Status = "No se encontró nada nuevo para " + row.FileName; return; }
            var fields = _engine.BuildFields();
            var undo = Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var res = await Task.Run(() => _engine.Apply.ApplyOneAsync(r, _engine.Config.Overwrite, fields, "keep", null, undo, _engine.Paths.DoneLog));
            await _engine.Library.ScanAsync();   // Recompute quitará la que ya esté completa
            Status = res.TagOk ? $"Actualizada: {row.FileName}" : $"⚠ {res.TagErr}";
        }
        catch (Exception e) { Status = "Error al reanalizar: " + e.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }
}
