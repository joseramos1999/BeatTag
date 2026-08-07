using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

/// <summary>Pestaña Biblioteca: gestiona las carpetas, escanea la biblioteca compartida e importa rekordbox.</summary>
public partial class LibraryViewModel : ViewModelBase
{
    private readonly AppEngine _engine;
    private readonly LibraryStore _store;

    public ObservableCollection<FolderItem> Folders => _store.Folders;
    public DataGridCollectionView TracksView { get; }

    [ObservableProperty] private string _status = "Añade una o varias carpetas y pulsa Escanear.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _selectedFolder;

    public LibraryViewModel(AppEngine engine)
    {
        _engine = engine;
        _store = engine.Library;
        TracksView = new DataGridCollectionView(_store.Tracks);
        TracksView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(Track.Folder)));
        _store.Changed += OnLibraryChanged;
        UpdateStatus();
    }

    private void OnLibraryChanged()
    {
        TracksView.Refresh();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_store.Tracks.Count == 0) { Status = "Añade una o varias carpetas y pulsa Escanear."; return; }
        var incompletas = _store.Tracks.Count(t => t.IsIncomplete);
        Status = $"{_store.Tracks.Count} canciones · {incompletas} incompletas · {_store.Folders.Count} carpeta(s)";
    }

    public void AddFolder(string folder) => _store.AddFolder(folder);

    [RelayCommand]
    private void RemoveFolderPath(string? folder) { if (folder != null) _store.RemoveFolder(folder); }

    [RelayCommand]
    private void ClearFolders() => _store.ClearFolders();

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (_store.Folders.Count == 0) { Status = "Añade al menos una carpeta."; return; }
        IsBusy = true;
        Status = "Escaneando…";
        try { await _store.ScanAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Importa BPM y clave musical desde un XML de rekordbox, casando por ruta de archivo.
    /// Anota un manifiesto reversible (se puede Deshacer desde Enriquecer) y registra los fallos.
    /// </summary>
    public async Task ImportRekordboxAsync(string xmlPath)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Importando de rekordbox…";
        try
        {
            var overwrite = _engine.Config.Overwrite;
            var undoFile = System.IO.Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var (matched, updated, failures) = await Task.Run(() =>
            {
                var byPath = new Dictionary<string, RekordboxEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in RekordboxImporter.Parse(xmlPath)) byPath[e.FilePath] = e;
                int m = 0, u = 0;
                var lines = new List<string>();
                var fails = new List<string>();
                foreach (var t in _store.Tracks.ToList())
                    if (byPath.TryGetValue(t.FilePath, out var e))
                    {
                        m++;
                        try
                        {
                            var log = TagEditor.ImportBpmKey(t.FilePath, RekordboxImporter.ParseBpm(e.Bpm), e.Key, overwrite);
                            if (log.Count > 0)
                            {
                                u++;
                                var rec = new UndoRecord { OrigPath = t.FilePath, FinalPath = t.FilePath, Renamed = false, Fields = log };
                                lines.Add(System.Text.Json.JsonSerializer.Serialize(rec));
                            }
                        }
                        catch (Exception ex) { fails.Add(t.FileName + ": " + ex.Message); }
                    }
                if (lines.Count > 0) System.IO.File.WriteAllLines(undoFile, lines);
                return (m, u, fails);
            });

            foreach (var f in failures) _engine.Logger.Log("rekordbox: " + f, LogKind.Err);
            await _store.ScanAsync();
            Status = matched == 0
                ? "Rekordbox: 0 coincidencias (¿las rutas del XML no apuntan a estas carpetas?)."
                : $"Rekordbox: {matched} coincidencias · {updated} actualizadas · {failures.Count} fallos. Deshacer disponible en Enriquecer.";
        }
        catch (Exception e) { Status = "Error importando rekordbox: " + e.Message; }
        finally { IsBusy = false; }
    }
}
