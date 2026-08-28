using Etiquetador.App.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

public sealed partial class NotFoundRow : ObservableObject
{
    [ObservableProperty] private string _rowStatus = "";
    public string FileName { get; init; } = "";
    public string Query { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";

    /// <summary>Nombre que propone la IA local (sin extensión), o "" si no propuso nada aprovechable.</summary>
    public string Suggestion { get; init; } = "";
    public string AiArtist { get; init; } = "";
    public string AiTitle { get; init; } = "";
    public string AiVersion { get; init; } = "";
    public bool HasSuggestion => Suggestion.Length > 0;
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

    /// <summary>Cuadro de búsqueda de la tabla: filtra lo ya listado, sin volver a analizar nada.</summary>
    [ObservableProperty] private string _busqueda = "";

    /// <summary>Cuántas quedan a la vista mientras hay búsqueda. Vacío si no se está filtrando.</summary>
    [ObservableProperty] private string _filtroInfo = "";

    /// <summary>Cuántas hay seleccionadas, para que se vea que hay acciones para el bloque entero.</summary>
    [ObservableProperty] private string _seleccionadasInfo = "";

    private IReadOnlyList<NotFoundRow> _seleccion = Array.Empty<NotFoundRow>();

    public NotFoundViewModel(AppEngine engine)
    {
        _engine = engine;
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(NotFoundRow.Folder)));
    }

    partial void OnBusquedaChanged(string value)
    {
        RowsView.Filter = Busqueda.Trim().Length == 0
            ? null
            : o => o is NotFoundRow r && BusquedaTexto.Coincide(Busqueda, r.FileName, r.Query, r.Suggestion, r.Folder);
        RowsView.Refresh();
        FiltroInfo = Busqueda.Trim().Length == 0 ? "" : $"{RowsView.Count} de {Rows.Count}";
    }

    /// <summary>
    /// Filas seleccionadas en la tabla, que mantiene al día la vista: el DataGrid de Avalonia no
    /// permite enlazar SelectedItems.
    /// </summary>
    public void SetSelection(IEnumerable<NotFoundRow> filas)
    {
        _seleccion = filas.ToList();
        SeleccionadasInfo = _seleccion.Count > 1 ? $"{_seleccion.Count} seleccionadas" : "";
    }

    /// <summary>Sobre qué actúa una acción: lo seleccionado, y si no hay nada, la fila en curso.</summary>
    private List<NotFoundRow> Objetivo()
        => _seleccion.Count > 0 ? _seleccion.ToList()
         : SelectedRow != null ? new List<NotFoundRow> { SelectedRow }
         : new List<NotFoundRow>();

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
                Rows.Add(MakeRow(r, t));
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
                if (_engine.Ignored.Contains(t.FilePath)) continue;   // descartada por el usuario
                Progress = tracks.Count == 0 ? 0 : (double)i / tracks.Count * 100;
                Status = $"{(force ? "Reanalizando" : "Analizando")} {i}/{tracks.Count}…  {t.FileName}";
                ProcessResult r;
                try { r = await Task.Run(() => _engine.AnalyzeCachedAsync(t.FilePath, opts, sig, force, ct), ct); }
                catch (OperationCanceledException) { throw; }
                catch { continue; }
                if (r.Skip) continue;
                if (!r.Found && !r.CleanOnly)
                    Rows.Add(MakeRow(r, t));
            }
            _engine.Analysis.Save();
            RowsView.Refresh();
            Status = $"{Rows.Count} no encontradas de {tracks.Count} analizadas.";
        }
        catch (OperationCanceledException) { _engine.Analysis.Save(); RowsView.Refresh(); Status = $"Cancelado ({Rows.Count} no encontradas hasta ahora)."; }
        finally { IsBusy = false; _cts.Dispose(); _cts = null; }
    }

    /// <summary>Una fila de la tabla, con la sugerencia de la IA local si la hubo.</summary>
    private static NotFoundRow MakeRow(ProcessResult r, Track t) => new()
    {
        FileName = r.Old,
        Query = r.Kw,
        Folder = t.Folder,
        FilePath = t.FilePath,
        Suggestion = AiSuggestion.For(r),
        AiArtist = r.AiArtist,
        AiTitle = r.AiTitle,
        AiVersion = r.AiVersion,
    };

    /// <summary>
    /// Acepta la propuesta de la IA local: renombra el archivo y escribe artista y título.
    ///
    /// Estas propuestas NO están confirmadas por ningún catálogo, y por eso la aplicación no las
    /// escribe sola en ningún momento. Aquí las escribe porque el usuario acaba de leerlas en la
    /// tabla y ha pulsado el botón: es él quien las verifica. Como cualquier otro cambio, queda
    /// registrado en el historial y se puede deshacer.
    /// </summary>
    [RelayCommand]
    private async Task AcceptSuggestionAsync()
    {
        var filas = Objetivo().Where(f => f.HasSuggestion).ToList();
        if (filas.Count == 0)
        {
            Status = Objetivo().Count == 0
                ? "Selecciona antes una o varias canciones."
                : "Ninguna de las seleccionadas tiene sugerencia de la IA.";
            return;
        }
        if (IsBusy) { Status = "Hay un proceso en curso; espera a que termine."; return; }

        _engine.ReleaseAudio();   // el reproductor mantiene el archivo abierto y el renombrado fallaría
        IsBusy = true;
        Status = filas.Count == 1 ? $"Aplicando «{filas[0].Suggestion}»…" : $"Aplicando {filas.Count} sugerencias…";
        _engine.Logger.Head($"Sugerencias de la IA aceptadas por el usuario: {filas.Count}");

        // Un solo manifiesto para todo el lote: deshacerlo devuelve las mismas canciones que se
        // aceptaron juntas, en vez de obligar a deshacer una por una.
        var undo = Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        var campos = new FieldFlags { Title = true, Artist = true, Album = false, Genre = false, Year = false, Bpm = false };
        int hechas = 0, fallidas = 0, sinHistorial = 0;

        try
        {
            foreach (var row in filas)
            {
                var titulo = row.AiVersion.Trim().Length > 0 ? $"{row.AiTitle} ({row.AiVersion.Trim()})" : row.AiTitle;
                var info = new ProcessResult
                {
                    FilePath = row.FilePath,
                    Old = row.FileName,
                    New = row.Suggestion + Path.GetExtension(row.FilePath),
                    Artist = row.AiArtist,
                    Title = titulo,
                    Found = true,
                    Source = "IA (aceptada)",
                };

                // Solo artista y título: lo demás (álbum, año, género, BPM) no lo sabe la IA, y
                // sobrescribir a ciegas se pisa: el usuario ha aceptado un NOMBRE, no una ficha entera.
                var res = await Task.Run(() => _engine.Apply.ApplyOneAsync(info, over: true, campos, "keep", null, undo, _engine.Paths.DoneLog));

                if (res.TagOk || res.DidRename)
                {
                    hechas++;
                    if (res.UndoErr.Length > 0) sinHistorial++;
                    Rows.Remove(row);
                    _engine.Applied.Add(res.FinalPath);   // ya aplicada: no reaparecerá al analizar
                    _engine.Logger.Detail($"    '{row.FileName}' -> '{row.Suggestion}'");
                }
                else
                {
                    fallidas++;
                    row.RowStatus = "⚠ no se pudo escribir: " + res.TagErr;
                    _engine.Logger.Err($"No se pudo aplicar la sugerencia en '{row.FileName}': {res.TagErr}");
                }
            }

            _engine.Applied.Save();
            RowsView.Refresh();
            SetSelection(Array.Empty<NotFoundRow>());
            await _engine.Library.ScanAsync();

            Status = (hechas == 1 && fallidas == 0
                        ? $"Aplicada la sugerencia: {filas[0].Suggestion}"
                        : $"Aplicadas {hechas} de {filas.Count}" + (fallidas > 0 ? $" · {fallidas} sin poder escribir" : ""))
                   + (sinHistorial > 0 ? $"  ⚠ {sinHistorial} no quedaron anotadas: esos cambios no se pueden deshacer." : "");
        }
        catch (Exception e) { Status = "Error al aplicar la sugerencia: " + e.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void EditThis() { if (SelectedRow != null) _engine.RequestEdit(SelectedRow.FilePath); }

    /// <summary>
    /// Descarta la canción: no volverá a aparecer ni aquí ni en Enriquecer. Útil para lo que nunca
    /// se va a identificar (himnos, sintonías, grabaciones propias). Se recupera desde Ajustes.
    /// </summary>
    [RelayCommand]
    private void DiscardSelected()
    {
        var filas = Objetivo();
        if (filas.Count == 0) { Status = "Selecciona antes una o varias canciones."; return; }

        _engine.IgnoreTracks(filas.Select(f => f.FilePath));
        foreach (var f in filas) Rows.Remove(f);
        RowsView.Refresh();
        SetSelection(Array.Empty<NotFoundRow>());

        _engine.Logger.Log($"Descartadas {filas.Count} (total descartadas: {_engine.Ignored.Count})");
        foreach (var f in filas) _engine.Logger.Detail($"    descartada '{f.FileName}'");

        Status = filas.Count == 1
            ? $"Descartada «{filas[0].FileName}». No volverá a aparecer (puedes recuperarlas en Ajustes)."
            : $"Descartadas {filas.Count} canciones. No volverán a aparecer (puedes recuperarlas en Ajustes).";
    }

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
                Status = res.UndoErr.Length > 0
                    ? $"¡Encontrada y actualizada!: {row.FileName}  ⚠ el cambio NO quedó anotado, no se podrá deshacer ({res.UndoErr})"
                    : $"¡Encontrada y actualizada!: {row.FileName}";
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
