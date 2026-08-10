using Etiquetador.App.Views;
using System;
using System.Collections.ObjectModel;

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

public sealed partial class NotFoundRow : ObservableObject
{
    [ObservableProperty] private string _rowStatus = "";
    public string FileName { get; init; } = "";
    public string Query { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";
}

/// <summary>Pestaña No encontradas: procesa la biblioteca y lista las que ninguna fuente identifica.</summary>
public partial class NotFoundViewModel : ViewModelBase
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    public ObservableCollection<NotFoundRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private NotFoundRow? _selectedRow;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Pulsa Analizar para buscar las que ninguna fuente identifica.";

    public NotFoundViewModel(AppEngine engine)
    {
        _engine = engine;
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(NotFoundRow.Folder)));
    }

    [RelayCommand]
    private Task AnalyzeAsync() => RunAnalyzeAsync(force: false);

    [RelayCommand]
    private Task ReanalyzeAllAsync() => RunAnalyzeAsync(force: true);

    /// <summary>
    /// Rellena la lista con lo que YA hay en la caché de análisis, sin volver a consultar la red.
    /// Es lo que se llama al terminar un análisis en Enriquecer y al abrir la app: las no
    /// encontradas aparecen aquí solas, sin tener que reanalizar toda la biblioteca otra vez.
    /// </summary>
    public void LoadFromCache()
    {
        if (IsBusy) return;
        var tracks = _engine.Library.Tracks.ToList();
        if (tracks.Count == 0) return;
        var sig = _engine.BuildOptions().Signature();
        Rows.Clear();
        foreach (var t in tracks)
        {
            if (_engine.Ignored.Contains(t.FilePath)) continue;
            var r = _engine.Analysis.Get(t.FilePath, sig);
            if (r == null || r.Skip) continue;
            if (!r.Found && !r.CleanOnly)
                Rows.Add(new NotFoundRow { FileName = r.Old, Query = r.Kw, Folder = t.Folder, FilePath = t.FilePath });
        }
        RowsView.Refresh();
        if (Rows.Count > 0) Status = $"{Rows.Count} sin identificar (del último análisis).";
    }

    private async Task RunAnalyzeAsync(bool force)
    {
        if (_engine.Library.Folders.Count == 0) { Status = "Añade carpetas en Biblioteca o Enriquecer."; return; }
        if (IsBusy) return;
        IsBusy = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            if (!_engine.Library.IsScanned) { Status = "Escaneando biblioteca…"; await _engine.Library.ScanAsync(); }
            Rows.Clear();
            var opts = _engine.BuildOptions();
            var sig = opts.Signature();
            var tracks = _engine.Library.Tracks.ToList();
            int i = 0;
            foreach (var t in tracks)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                Progress = tracks.Count == 0 ? 0 : (double)i / tracks.Count * 100;
                Status = $"{(force ? "Reanalizando" : "Analizando")} {i}/{tracks.Count}…  {t.FileName}";
                ProcessResult r;
                try { r = await Task.Run(() => _engine.AnalyzeCachedAsync(t.FilePath, opts, sig, force, ct), ct); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                if (r.Skip) continue;
                if (!r.Found && !r.CleanOnly)
                    Rows.Add(new NotFoundRow { FileName = r.Old, Query = r.Kw, Folder = t.Folder, FilePath = t.FilePath });
            }
            _engine.Analysis.Save();
            RowsView.Refresh();
            Status = $"{Rows.Count} no encontradas de {tracks.Count} analizadas.";
        }
        catch (OperationCanceledException) { _engine.Analysis.Save(); RowsView.Refresh(); Status = $"Cancelado ({Rows.Count} no encontradas hasta ahora)."; }
        finally { IsBusy = false; _cts.Dispose(); _cts = null; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void EditThis() { if (SelectedRow != null) _engine.RequestEdit(SelectedRow.FilePath); }

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    /// <summary>Servicios del diálogo "Reanalizar…" (los mismos que en Enriquecer).</summary>
    public SearchServices SearchServicesFor(string filePath)
        => new(_engine.FindCandidatesAsync, _engine.ResolveLinkAsync,
               () => _engine.IdentifyByFingerprintAsync(filePath));

    /// <summary>
    /// Reanaliza una fila con los términos que dicte el usuario (mismo diálogo que Enriquecer) y,
    /// si aparece, aplica el cambio. La fila llega por parámetro: el diálogo es modal y la tabla
    /// puede perder la selección mientras está abierto.
    /// </summary>
    public async Task ReanalyzeRowAsync(NotFoundRow? row, string searchArtist = "", string searchTitle = "",
        string searchSource = "")
    {
        row ??= SelectedRow;
        if (row == null) { Status = "Selecciona antes una canción de la lista."; return; }
        if (IsBusy) { Status = "Hay un proceso en curso; espera a que termine."; return; }
        _engine.ReleaseAudio();   // el reproductor mantiene el archivo abierto y el renombrado fallaría
        IsBusy = true;
        var manual = searchArtist.Length > 0 || searchTitle.Length > 0;
        Status = manual ? $"Buscando «{searchArtist} - {searchTitle}»…" : "Reanalizando " + row.FileName + "…";
        _engine.Logger.Head($"Reanalizar (no encontradas) '{row.FileName}'"
            + (manual ? $" · buscando '{searchArtist} - {searchTitle}'"
                      + (searchSource.Length > 0 ? $" solo en {searchSource}" : "") : ""));
        try
        {
            var opts = _engine.BuildOptions();
            opts.SearchArtist = searchArtist;
            opts.SearchTitle = searchTitle;
            opts.SearchSource = searchSource;
            var r = await Task.Run(() => _engine.Processor.ProcessAsync(row.FilePath, isAcapella: false, opts));
            if (r.Skip || !r.Found) { row.RowStatus = "sigue sin encontrarse"; Status = "Sigue sin encontrarse."; return; }
            var fields = _engine.BuildFields();
            var undo = Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var res = await Task.Run(() => _engine.Apply.ApplyOneAsync(r, _engine.Config.Overwrite, fields, "keep", null, undo, _engine.Paths.DoneLog));
            await _engine.Library.ScanAsync();
            if (res.TagOk)
            {
                Rows.Remove(row);   // solo se quita si de verdad se escribió
                RowsView.Refresh();
                _engine.Applied.Add(res.FinalPath);   // ya aplicada: no reaparecerá al analizar
                _engine.Applied.Save();
                Status = $"¡Encontrada y actualizada!: {row.FileName}";
            }
            else
            {
                row.RowStatus = "⚠ no se pudo escribir: " + res.TagErr;
                Status = "Encontrada pero NO se pudo escribir: " + row.FileName;
            }
        }
        catch (Exception e) { Status = "Error al reanalizar: " + e.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }
}
