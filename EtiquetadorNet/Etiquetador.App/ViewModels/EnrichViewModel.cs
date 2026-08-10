using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;
using Etiquetador.App.Views;

namespace Etiquetador.App.ViewModels;

/// <summary>Una fila de la previsualización: cambio propuesto para un archivo, con casilla Aplicar.</summary>
public partial class PreviewRow : ObservableObject
{
    private static readonly IBrush AppliedBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A)); // verde
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xDC, 0x26, 0x26));   // rojo
    private static readonly IBrush CleanBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x6B, 0x72, 0x80));   // gris (solo limpieza)

    [ObservableProperty] private bool _apply = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(StateBrush))] private string _rowStatus = "";
    [ObservableProperty] private string _new = "";      // editable antes de aplicar
    [ObservableProperty] private string _score = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(StateBrush))] private string _source = "";

    /// <summary>Color de fondo de la fila según su estado (aplicada/error/limpieza).</summary>
    public IBrush StateBrush =>
        RowStatus.StartsWith('✔') ? AppliedBrush :
        RowStatus.StartsWith('⚠') ? ErrorBrush :
        Source == "Limpieza" ? CleanBrush :
        Brushes.Transparent;
    // Tags propuestos (mismas columnas que Biblioteca)
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _bpm = "";
    [ObservableProperty] private string _remixer = "";   // quién firma la versión (informativo)

    public string Old { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Duration { get; init; } = "";   // propiedad de audio (no cambia)
    public string Quality { get; init; } = "";
    public required ProcessResult Result { get; set; }

    public void Toggle() => Apply = !Apply;

    /// <summary>Vuelca los campos mostrables desde un ProcessResult (al crear y al reanalizar).</summary>
    public void UpdateFrom(ProcessResult r)
    {
        Result = r;
        New = r.New; Score = r.Score; Source = r.Source;
        Remixer = r.Remixer.Length > 0 ? $"{r.Remixer} ({r.RemixKind})" : r.RemixKind;
        Title = r.Title; Artist = r.Artist; Album = r.Album; Genre = r.Genre; Year = r.Year; Bpm = r.Bpm;
    }

    /// <summary>Copia los valores editados en la tabla al ProcessResult antes de aplicar.</summary>
    public void SyncToResult()
    {
        Result.New = New;
        Result.Title = Title; Result.Artist = Artist; Result.Album = Album;
        Result.Genre = Genre; Result.Year = Year; Result.Bpm = Bpm;
    }
}

/// <summary>Pestaña Enriquecer: analiza varias carpetas, previsualiza (agrupado) y aplica/deshace.</summary>
public partial class EnrichViewModel : ViewModelBase
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    // Confianza (Score) por debajo de la cual una propuesta se auto-desmarca para revisión.
    private const double LowConfidence = 2.0;

    /// <summary>Visible solo cuando el modo de carátula es "png" (para el botón/ruta de imagen).</summary>
    public static readonly IValueConverter IsPngMode = new FuncValueConverter<string?, bool>(s => s == "png");

    /// <summary>Texto del botón de pausa según el estado.</summary>
    public static readonly IValueConverter PauseLabel = new FuncValueConverter<bool, string>(p => p ? "▶ Reanudar" : "⏸ Pausar");

    public ObservableCollection<PreviewRow> Rows { get; } = new();
    public ObservableCollection<FolderItem> Folders => _engine.Library.Folders;   // compartidas (con marca)
    public DataGridCollectionView RowsView { get; }

    // Fuentes
    [ObservableProperty] private bool _useDeezer;
    [ObservableProperty] private bool _useItunes;
    [ObservableProperty] private bool _useSpotify;
    [ObservableProperty] private bool _useDiscogs;
    [ObservableProperty] private bool _useMusicBrainz;
    [ObservableProperty] private bool _useAcoustId;
    [ObservableProperty] private bool _useAi;
    // Campos a escribir
    [ObservableProperty] private bool _writeTitle;
    [ObservableProperty] private bool _writeArtist;
    [ObservableProperty] private bool _writeAlbum;
    [ObservableProperty] private bool _writeGenre;
    [ObservableProperty] private bool _writeYear;
    [ObservableProperty] private bool _writeBpm;
    // Opciones
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private bool _cleanOnly;
    [ObservableProperty] private string _coverMode = "keep";
    [ObservableProperty] private string _coverPath = "";
    [ObservableProperty] private string _status = "Añade o arrastra carpetas y pulsa Analizar.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _progress;      // 0..100 (barra real)
    [ObservableProperty] private string _timeInfo = ""; // transcurrido + estimado restante
    [ObservableProperty] private double _stepProgress;      // 0..100: avance DENTRO de la canción actual
    [ObservableProperty] private string _stepInfo = "";     // fase en curso (Deezer, iTunes, IA…)
    [ObservableProperty] private string? _selectedFolder;
    [ObservableProperty] private PreviewRow? _selectedRow;

    private static readonly HashSet<string> OptionProps = new()
    {
        nameof(UseDeezer), nameof(UseItunes), nameof(UseSpotify), nameof(UseDiscogs), nameof(UseMusicBrainz),
        nameof(UseAcoustId), nameof(UseAi), nameof(WriteTitle), nameof(WriteArtist), nameof(WriteAlbum),
        nameof(WriteGenre), nameof(WriteYear), nameof(WriteBpm), nameof(Overwrite), nameof(CleanOnly),
        nameof(CoverMode), nameof(CoverPath),
    };

    public EnrichViewModel(AppEngine engine)
    {
        _engine = engine;
        var c = engine.Config;
        _useDeezer = c.UseDeezer; _useItunes = c.UseItunes; _useSpotify = c.UseSpotify;
        _useDiscogs = c.UseDiscogs; _useMusicBrainz = c.UseMusicBrainz; _useAcoustId = c.UseAcoustId; _useAi = c.UseAi;
        _writeTitle = c.WriteTitle; _writeArtist = c.WriteArtist; _writeAlbum = c.WriteAlbum;
        _writeGenre = c.WriteGenre; _writeYear = c.WriteYear; _writeBpm = c.WriteBpm;
        _overwrite = c.Overwrite; _cleanOnly = c.CleanOnly; _coverMode = c.CoverMode; _coverPath = c.CoverPath;

        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(PreviewRow.Folder)));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != null && OptionProps.Contains(e.PropertyName)) PushToConfig();
    }

    public void AddFolder(string folder) => _engine.Library.AddFolder(folder);

    [RelayCommand]
    private void RemoveFolderPath(string? folder) { if (folder != null) _engine.Library.RemoveFolder(folder); }

    [RelayCommand]
    private void ClearFolders() => _engine.Library.ClearFolders();

    private void PushToConfig()
    {
        var c = _engine.Config;
        c.UseDeezer = UseDeezer; c.UseItunes = UseItunes; c.UseSpotify = UseSpotify;
        c.UseDiscogs = UseDiscogs; c.UseMusicBrainz = UseMusicBrainz; c.UseAcoustId = UseAcoustId; c.UseAi = UseAi;
        c.WriteTitle = WriteTitle; c.WriteArtist = WriteArtist; c.WriteAlbum = WriteAlbum;
        c.WriteGenre = WriteGenre; c.WriteYear = WriteYear; c.WriteBpm = WriteBpm;
        c.Overwrite = Overwrite; c.CleanOnly = CleanOnly; c.CoverMode = CoverMode; c.CoverPath = CoverPath;
        _engine.SaveConfig();   // las carpetas las persiste el LibraryStore
    }

    /// <summary>Analiza usando la caché de resultados (rápido; solo reprocesa lo nuevo/cambiado).</summary>
    [RelayCommand]
    public Task AnalyzeAsync() => RunAnalyzeAsync(force: false);

    /// <summary>Reanaliza TODO ignorando la caché (cuando el usuario lo requiere).</summary>
    [RelayCommand]
    private Task ReanalyzeAllAsync() => RunAnalyzeAsync(force: true);

    private async Task RunAnalyzeAsync(bool force)
    {
        if (_engine.Library.Folders.Count == 0) { Status = "Añade al menos una carpeta."; return; }
        if (IsBusy) return;
        PushToConfig();
        IsBusy = true;
        IsPaused = false;
        Progress = 0; TimeInfo = "";
        StepProgress = 0; StepInfo = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();

        // Barra secundaria: avance dentro de la canción en curso. Progress<T> marshalea al hilo de UI.
        _engine.StepProgress = new Progress<(string Phase, double Fraction)>(p =>
        {
            StepInfo = p.Phase;
            StepProgress = p.Fraction * 100;
        });
        try
        {
            if (!_engine.Library.IsScanned) { Status = "Escaneando biblioteca…"; await _engine.Library.ScanAsync(); }
            Rows.Clear();
            var opts = _engine.BuildOptions();
            var sig = opts.Signature();
            var tracks = _engine.Library.Tracks.ToList();   // biblioteca compartida (sin re-escanear)
            _engine.Logger.Head($"{(force ? "REANÁLISIS COMPLETO" : "Análisis")} de {tracks.Count} canciones"
                              + (_engine.Ignored.Count > 0 ? $" ({_engine.Ignored.Count} descartadas se omiten)" : ""));
            _engine.Logger.Detail($"Firma de opciones: {sig}");
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            int i = 0, fromCache = 0, alreadyApplied = 0;
            var seenResults = new List<ProcessResult>();   // para el resumen y el informe del final
            foreach (var t in tracks)
            {
                ct.ThrowIfCancellationRequested();
                while (IsPaused) { ct.ThrowIfCancellationRequested(); await Task.Delay(150, ct); }
                i++;
                seen.Add(t.FilePath);
                if (_engine.Ignored.Contains(t.FilePath)) continue;   // descartada por el usuario
                // Ya aplicada: no se vuelve a proponer en el análisis normal ("Reanalizar todo" sí la incluye).
                if (!force && _engine.Applied.Contains(t.FilePath)) { alreadyApplied++; continue; }
                Status = $"{(force ? "Reanalizando" : "Analizando")} {i}/{tracks.Count}…  {t.FileName}";
                Progress = tracks.Count == 0 ? 0 : (double)i / tracks.Count * 100;
                var per = sw.Elapsed.TotalSeconds / i;
                TimeInfo = $"⏱ {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)} · resto ~{TextUtils.FormatEta(per * (tracks.Count - i))}";

                StepProgress = 0; StepInfo = "";   // la barra secundaria arranca de cero en cada canción

                var cached = force ? null : _engine.Analysis.Get(t.FilePath, sig);
                ProcessResult r;
                if (cached != null) { r = cached; fromCache++; StepProgress = 100; StepInfo = "de caché"; }
                else
                {
                    try { r = await Task.Run(() => _engine.AnalyzeCachedAsync(t.FilePath, opts, sig, force, ct), ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { continue; }
                }
                seenResults.Add(r);
                if (r.Skip) continue;
                if (r.Found || r.CleanOnly) AddRow(r, t);
            }
            _engine.Analysis.Prune(seen);
            _engine.Analysis.Save();
            RowsView.Refresh();
            Progress = 100;
            Status = $"Analizadas {tracks.Count} · propuestas {Rows.Count} · {fromCache} de caché"
                   + (alreadyApplied > 0 ? $" · {alreadyApplied} ya aplicadas (omitidas)" : "")
                   + $" · en {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}.";
            var low = Rows.Count(r => r.RowStatus.StartsWith('⚠'));
            _engine.Logger.Sum($"Análisis terminado: {tracks.Count} revisadas · {Rows.Count} propuestas · {fromCache} de caché · "
                             + $"{low} de baja confianza · {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}");
            _engine.Logger.Detail($"Caché de red: {_engine.Api.CacheHits} aciertos / {_engine.Api.CacheMiss} peticiones nuevas");
            WriteAnalysisReport(seenResults);
            AnalysisCompleted?.Invoke();   // las no encontradas van solas a su pestaña
        }
        catch (OperationCanceledException)
        {
            _engine.Analysis.Save(); RowsView.Refresh();
            Status = $"Análisis cancelado ({Rows.Count} propuestas hasta ahora).";
            _engine.Logger.Err($"Análisis cancelado por el usuario ({Rows.Count} propuestas hasta ahora).");
        }
        finally
        {
            _engine.StepProgress = null;   // deja de reportar al salir
            StepProgress = 0; StepInfo = "";
            IsBusy = false; _cts.Dispose(); _cts = null;
        }
    }

    // Crea la fila de previsualización; auto-desmarca y marca "baja confianza" si el score es muy bajo.
    private void AddRow(ProcessResult r, Track t)
    {
        var row = new PreviewRow { Result = r, Old = r.Old, Folder = t.Folder, Duration = t.Duration, Quality = t.Quality };
        row.UpdateFrom(r);
        if (double.TryParse(r.Score, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sc) && sc < LowConfidence)
        {
            row.Apply = false;
            row.RowStatus = "⚠ baja confianza — revisar";
        }
        Rows.Add(row);
    }

    /// <summary>Al iniciar: rellena la previsualización SOLO con lo que ya hay en la caché (sin red).</summary>
    public void LoadCached()
    {
        if (IsBusy) return;
        var tracks = _engine.Library.Tracks.ToList();
        if (tracks.Count == 0) return;
        var sig = _engine.BuildOptions().Signature();
        Rows.Clear();
        int loaded = 0;
        foreach (var t in tracks)
        {
            if (_engine.Ignored.Contains(t.FilePath)) continue;   // descartada por el usuario
            if (_engine.Applied.Contains(t.FilePath)) continue;   // ya aplicada
            var c = _engine.Analysis.Get(t.FilePath, sig);
            if (c == null || c.Skip) continue;
            if (c.Found || c.CleanOnly) { AddRow(c, t); loaded++; }
        }
        RowsView.Refresh();
        if (loaded > 0) Status = $"{loaded} propuestas cargadas de la caché. Pulsa Analizar para completar el resto.";
    }

    [RelayCommand]
    private void Pause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void Cancel() { IsPaused = false; _cts?.Cancel(); }

    /// <summary>Aplica todas las filas marcadas.</summary>
    [RelayCommand]
    private Task ApplyAsync()
    {
        if (Rows.Count == 0) { Status = "No hay nada que aplicar. Analiza primero."; return Task.CompletedTask; }
        var marked = Rows.Where(r => r.Apply).ToList();
        if (marked.Count == 0) { Status = "No hay ninguna canción marcada."; return Task.CompletedTask; }
        return ApplyRowsAsync(marked);
    }

    /// <summary>Aplica SOLO la canción seleccionada (menú contextual), esté marcada o no.</summary>
    [RelayCommand]
    private Task ApplySelectedAsync()
    {
        var row = SelectedRow;
        if (row == null) { Status = "Selecciona antes una canción."; return Task.CompletedTask; }
        return ApplyRowsAsync(new List<PreviewRow> { row });
    }

    private async Task ApplyRowsAsync(List<PreviewRow> toApply)
    {
        if (IsBusy) return;
        _engine.ReleaseAudio();   // si suena la canción, el archivo está abierto y fallaría al escribir
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var fields = _engine.BuildFields();
            var undoFile = Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var doneLog = _engine.Paths.DoneLog;

            // Portada PNG local: se carga una vez para toda la tirada.
            TagLib.Picture? png = null;
            if (CoverMode == "png")
            {
                if (!string.IsNullOrWhiteSpace(CoverPath) && File.Exists(CoverPath))
                {
                    try { png = new TagLib.Picture(CoverPath) { Type = TagLib.PictureType.FrontCover }; }
                    catch (Exception e) { Status = "No se pudo cargar la imagen de portada: " + e.Message; IsBusy = false; return; }
                }
                else { Status = "Elige una imagen PNG/JPG válida para la portada."; IsBusy = false; return; }
            }

            int applied = 0, marked = toApply.Count, done = 0;
            _engine.Logger.Head($"Aplicando {marked} canción(es) · manifiesto {Path.GetFileName(undoFile)}");
            Progress = 0; TimeInfo = "";
            var sw = Stopwatch.StartNew();
            var cancelled = false;
            var appliedRows = new List<PreviewRow>();   // filas aplicadas con éxito: se quitan de la lista al terminar
            foreach (var row in toApply)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                done++;
                Progress = marked == 0 ? 0 : (double)done / marked * 100;
                Status = $"Aplicando {done}/{marked}…  {row.Old}";
                try
                {
                    row.SyncToResult();   // respeta lo editado en la tabla (nombre y tags)
                    var res = await Task.Run(() => _engine.Apply.ApplyOneAsync(row.Result, Overwrite, fields, CoverMode, png, undoFile, doneLog, ct), ct);
                    row.RowStatus = res is { TagOk: true, RenOk: true } ? "✔ aplicado" : $"⚠ {res.TagErr}{res.RenErr}";
                    if (res.TagOk && res.RenOk)
                    {
                        applied++; appliedRows.Add(row);
                        // Queda marcada como aplicada: "Analizar" ya no volverá a proponerla
                        // (sí lo hará "Reanalizar todo", que ignora estas marcas a propósito).
                        _engine.Applied.Add(res.FinalPath);
                        _engine.Logger.Detail($"    OK  '{row.Old}' -> '{row.New}'");
                    }
                    else
                        _engine.Logger.Err($"No se pudo aplicar '{row.Old}': {res.TagErr}{res.RenErr}");
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception e)
                {
                    row.RowStatus = "⚠ " + e.Message;
                    _engine.Logger.Error($"Error al aplicar '{row.Old}'", e);
                }
            }
            // Las canciones ya aplicadas salen de la lista de Enriquecer (las que fallaron se quedan con su aviso).
            if (appliedRows.Count > 0)
            {
                foreach (var r in appliedRows) Rows.Remove(r);
                RowsView.Refresh();
            }
            _engine.Applied.Save();
            _engine.Logger.Sum($"Aplicación terminada: {applied} de {marked} correctas"
                             + (applied < marked ? $" · {marked - applied} con problemas" : "")
                             + (cancelled ? " (cancelada)" : ""));
            await _engine.Library.ScanAsync();   // refleja renombrados/tags en la biblioteca compartida
            Status = cancelled
                ? $"Cancelado: {applied} aplicados antes de parar. Manifiesto: {Path.GetFileName(undoFile)}"
                : marked == 1
                    ? (applied == 1
                        ? $"Aplicada «{toApply[0].Old}». Manifiesto: {Path.GetFileName(undoFile)}"
                        : $"No se pudo aplicar «{toApply[0].Old}»: {toApply[0].RowStatus}")
                    : $"Aplicados {applied} de {marked}. Biblioteca actualizada. Manifiesto: {Path.GetFileName(undoFile)}";
        }
        finally { IsBusy = false; _cts.Dispose(); _cts = null; }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (IsBusy) return;
        _engine.ReleaseAudio();   // deshacer también renombra: el archivo no puede estar sonando
        IsBusy = true;
        Status = "Deshaciendo la última ejecución…";
        _engine.Logger.Head("Deshaciendo la última ejecución…");
        try
        {
            var res = await Task.Run(() => _engine.Undo.UndoLastRun());
            _engine.Logger.Sum($"Deshacer: {res.Reverted} revertidos · {res.Manual} conservados · {res.Missing} ausentes · {res.Errors} errores.");
            if (!res.None) await _engine.Library.ScanAsync();   // refleja el estado revertido
            Status = res.None
                ? "No hay ninguna ejecución que deshacer."
                : $"Deshecho: {res.Reverted} revertidos · {res.Manual} conservados · {res.Missing} ausentes · {res.Errors} errores.";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void MarkAll() { foreach (var r in Rows) r.Apply = true; }

    [RelayCommand]
    private void MarkNone() { foreach (var r in Rows) r.Apply = false; }

    // --- Menú contextual (sobre la fila seleccionada) ---
    [RelayCommand]
    private void ToggleSelected() => SelectedRow?.Toggle();

    /// <summary>Quita la fila y DESCARTA la canción: no volverá a aparecer en Enriquecer.</summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        var row = SelectedRow;
        if (row == null) return;
        _engine.IgnoreTrack(row.Result.FilePath);
        _engine.Logger.Log($"Descartada '{row.Old}' (total descartadas: {_engine.Ignored.Count})");
        Rows.Remove(row);
        RowsView.Refresh();
        Status = $"Descartada «{row.Old}». No volverá a aparecer (puedes recuperarlas en Ajustes).";
    }

    /// <summary>
    /// Cierra la tirada con las cifras que sirven para afinar el algoritmo: reparto por fuente y por
    /// desenlace, cómo cuadran las duraciones y qué casos fallaron. Además deja un CSV en "informes"
    /// para poder mirarlo en una hoja de cálculo.
    /// </summary>
    private void WriteAnalysisReport(List<ProcessResult> res)
    {
        if (res.Count == 0) return;
        var log = _engine.Logger;

        var found = res.Where(r => !r.Skip && r.Found).ToList();
        var noRes = res.Where(r => !r.Skip && !r.Found && !r.CleanOnly).ToList();
        var skipped = res.Where(r => r.Skip).ToList();

        log.Sum("── Resumen para afinar el algoritmo ─────────────");
        log.Sum($"   Identificadas {found.Count} · sin resultado {noRes.Count} · saltadas como mezcla {skipped.Count}");

        // Reparto por fuente y por estrategia de búsqueda (qué variante acertó).
        foreach (var g in found.GroupBy(r => r.Source).OrderByDescending(g => g.Count()))
            log.Sum($"   fuente {g.Key,-14} {g.Count(),6}");
        foreach (var g in found.Where(r => r.Variant.Length > 0).GroupBy(r => r.Variant).OrderByDescending(g => g.Count()))
            log.Detail($"   via {g.Key,-16} {g.Count(),6}");

        // Confianza: dónde se concentra y cuántas quedan por debajo del umbral.
        var scores = found
            .Select(r => double.TryParse(r.Score, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v)).ToList();
        if (scores.Count > 0)
        {
            log.Sum($"   confianza: media {scores.Average():0.0} · <2 (dudosas) {scores.Count(v => v < 2)} · "
                  + $"2-9 {scores.Count(v => v >= 2 && v < 10)} · >=10 {scores.Count(v => v >= 10)}");
        }

        // Duración: la señal más fiable de "me han dado otra versión".
        var withDur = found.Where(r => r.DurLocal > 0 && int.TryParse(r.DurMatch, out var d) && d > 0)
                           .Select(r => Math.Abs(int.Parse(r.DurMatch) - r.DurLocal)).ToList();
        if (withDur.Count > 0)
            log.Sum($"   duración: ≤2s {withDur.Count(d => d <= 2)} · ≤5s {withDur.Count(d => d is > 2 and <= 5)} · "
                  + $"≤20s {withDur.Count(d => d is > 5 and <= 20)} · >20s {withDur.Count(d => d > 20)} (sospechosas)");

        // Versiones detectadas (remix/edit y quién las firma).
        var vers = found.Where(r => r.RemixKind.Length > 0).ToList();
        log.Sum($"   versiones: {vers.Count} detectadas · {vers.Count(r => r.Remixer.Length > 0)} con autor identificado");

        // Lo más accionable: los casos que fallaron, para leerlos y sacar patrones.
        if (noRes.Count > 0)
        {
            log.Sum($"   — primeras {Math.Min(40, noRes.Count)} sin resultado (candidatas a mejorar):");
            foreach (var r in noRes.Take(40)) log.Sum($"       {r.Old}   [query: {r.Kw}]");
        }
        if (skipped.Count > 0)
        {
            log.Detail($"   — primeras {Math.Min(20, skipped.Count)} saltadas como mezcla:");
            foreach (var r in skipped.Take(20)) log.Detail($"       {r.Old}");
        }

        // CSV completo para hoja de cálculo.
        try
        {
            Directory.CreateDirectory(_engine.Paths.ReportsDir);
            var csv = Path.Combine(_engine.Paths.ReportsDir, $"analisis_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("archivo;estado;fuente;via;confianza;dur_local;dur_match;dur_delta;query;artista;titulo;remixer;tipo_version;genero;bpm;anio;propuesto");
            foreach (var r in res)
            {
                var dm = int.TryParse(r.DurMatch, out var d) ? d : 0;
                var delta = (r.DurLocal > 0 && dm > 0) ? Math.Abs(dm - r.DurLocal) : -1;
                var estado = r.Skip ? "MEZCLA" : r.Found ? "OK" : r.CleanOnly ? "LIMPIEZA" : "SIN-RESULTADO";
                sb.AppendLine(string.Join(";", new[]
                {
                    Q(r.Old), estado, Q(r.Source), Q(r.Variant), Q(r.Score.Replace('.', ',')),   // decimal ES para Excel
                    r.DurLocal.ToString(), dm.ToString(), delta.ToString(),
                    Q(r.Kw), Q(r.Artist), Q(r.Title), Q(r.Remixer), Q(r.RemixKind),
                    Q(r.Genre), Q(r.Bpm), Q(r.Year), Q(r.New),
                }));
            }
            File.WriteAllText(csv, sb.ToString(), System.Text.Encoding.UTF8);
            log.Sum($"   Informe para analizar: {csv}");
        }
        catch (Exception e) { log.Error("No se pudo escribir el informe CSV", e); }

        log.Sum("─────────────────────────────────────────────────");
    }

    /// <summary>Entrecomilla un campo del CSV (separador ';', como espera Excel en español).</summary>
    private static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace('\n', ' ').Replace('\r', ' ') + "\"";

    /// <summary>Se dispara al terminar un análisis, para que otras pestañas se refresquen.</summary>
    public event Action? AnalysisCompleted;

    /// <summary>Servicios del diálogo "Reanalizar…" (los mismos que usa No encontradas).</summary>
    public SearchServices SearchServicesFor(string filePath)
        => new(_engine.FindCandidatesAsync, _engine.ResolveLinkAsync,
               () => _engine.IdentifyByFingerprintAsync(filePath));

    /// <summary>
    /// Reanaliza UNA fila. Si se pasan términos, se buscan ESOS en vez de deducirlos del nombre del
    /// archivo. El resultado se guarda en la caché: la corrección persiste entre sesiones.
    /// La fila llega por parámetro a propósito: el diálogo previo es modal y la tabla puede perder
    /// la selección mientras está abierto (releer SelectedRow aquí dejaba el reanálisis sin efecto).
    /// </summary>
    public async Task ReanalyzeRowAsync(PreviewRow? row, string searchArtist = "", string searchTitle = "",
        string searchSource = "")
    {
        row ??= SelectedRow;
        // Antes se salía en silencio y parecía que "no funcionaba": ahora se dice por qué.
        if (row == null) { Status = "Selecciona antes una canción de la lista."; return; }
        if (IsBusy) { Status = "Hay un proceso en curso; espera a que termine para reanalizar."; return; }
        _engine.ReleaseAudio();
        IsBusy = true;
        var manual = searchArtist.Length > 0 || searchTitle.Length > 0;
        Status = manual
            ? $"Buscando «{searchArtist} - {searchTitle}»…"
            : "Reanalizando " + row.Old + "…";
        _engine.Logger.Head($"Reanalizar '{row.Old}'"
            + (manual ? $" · buscando '{searchArtist} - {searchTitle}'"
                      + (searchSource.Length > 0 ? $" solo en {searchSource}" : "") : ""));
        try
        {
            var opts = _engine.BuildOptions();
            opts.SearchArtist = searchArtist;
            opts.SearchTitle = searchTitle;
            opts.SearchSource = searchSource;
            var r = await Task.Run(() => _engine.Processor.ProcessAsync(row.Result.FilePath, isAcapella: false, opts));
            row.UpdateFrom(r);
            RowsView.Refresh();   // repinta la celda en la vista agrupada

            if (r.Found || r.CleanOnly)
            {
                row.RowStatus = manual ? "✔ búsqueda manual" : "reanalizado";
                // La propuesta pasa a ser la buena de este archivo (misma firma que el análisis normal).
                _engine.Analysis.Set(row.Result.FilePath, _engine.BuildOptions().Signature(), r);
                _engine.Analysis.Save();
            }
            else row.RowStatus = r.Skip ? "saltado" : "sin resultado";

            Status = r.Found || r.CleanOnly
                ? "Reanalizado: " + row.Old
                : $"Sin resultado para «{searchArtist} - {searchTitle}». Prueba otra grafía.";
        }
        catch (Exception e)
        {
            row.RowStatus = "⚠ " + e.Message;
            Status = "Error al reanalizar.";
            _engine.Logger.Error($"Error al reanalizar '{row.Old}'", e);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.Result.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsBusy) { Status = "Espera a que termine el proceso en curso para escuchar."; return; }
        var path = SelectedRow?.Result.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }
}
