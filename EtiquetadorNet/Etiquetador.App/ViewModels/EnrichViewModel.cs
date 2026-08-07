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
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            int i = 0, fromCache = 0;
            foreach (var t in tracks)
            {
                ct.ThrowIfCancellationRequested();
                while (IsPaused) { ct.ThrowIfCancellationRequested(); await Task.Delay(150, ct); }
                i++;
                seen.Add(t.FilePath);
                if (_engine.Ignored.Contains(t.FilePath)) continue;   // descartada por el usuario
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
                if (r.Skip) continue;
                if (r.Found || r.CleanOnly) AddRow(r, t);
            }
            _engine.Analysis.Prune(seen);
            _engine.Analysis.Save();
            RowsView.Refresh();
            Progress = 100;
            Status = $"Analizadas {tracks.Count} · propuestas {Rows.Count} · {fromCache} de caché · en {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}.";
        }
        catch (OperationCanceledException) { _engine.Analysis.Save(); RowsView.Refresh(); Status = $"Análisis cancelado ({Rows.Count} propuestas hasta ahora)."; }
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

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Rows.Count == 0) { Status = "No hay nada que aplicar. Analiza primero."; return; }
        if (IsBusy) return;
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

            var toApply = Rows.Where(r => r.Apply).ToList();
            int applied = 0, marked = toApply.Count, done = 0;
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
                    if (res.TagOk && res.RenOk) { applied++; appliedRows.Add(row); }
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception e) { row.RowStatus = "⚠ " + e.Message; }
            }
            // Las canciones ya aplicadas salen de la lista de Enriquecer (las que fallaron se quedan con su aviso).
            if (appliedRows.Count > 0)
            {
                foreach (var r in appliedRows) Rows.Remove(r);
                RowsView.Refresh();
            }
            await _engine.Library.ScanAsync();   // refleja renombrados/tags en la biblioteca compartida
            Status = cancelled
                ? $"Cancelado: {applied} aplicados antes de parar. Manifiesto: {Path.GetFileName(undoFile)}"
                : $"Aplicados {applied} de {marked}. Biblioteca actualizada. Manifiesto: {Path.GetFileName(undoFile)}";
        }
        finally { IsBusy = false; _cts.Dispose(); _cts = null; }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Deshaciendo la última ejecución…";
        try
        {
            var res = await Task.Run(() => _engine.Undo.UndoLastRun());
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
        Rows.Remove(row);
        RowsView.Refresh();
        Status = $"Descartada «{row.Old}». No volverá a aparecer (puedes recuperarlas en Ajustes).";
    }

    /// <summary>Coincidencias del catálogo para que el usuario elija (diálogo de "Reanalizar…").</summary>
    public Task<IReadOnlyList<Candidate>> FindCandidatesAsync(string artist, string title)
    {
        // Si el usuario tiene ambas fuentes apagadas, se usa Deezer (no necesita clave) para poder listar.
        var dz = UseDeezer || !UseItunes;
        return _engine.Candidates.FindAsync(artist, title, dz, UseItunes);
    }

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
        if (row == null || IsBusy) return;
        IsBusy = true;
        var manual = searchArtist.Length > 0 || searchTitle.Length > 0;
        Status = manual
            ? $"Buscando «{searchArtist} - {searchTitle}»…"
            : "Reanalizando " + row.Old + "…";
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
        catch (Exception e) { row.RowStatus = "⚠ " + e.Message; Status = "Error al reanalizar."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        var path = SelectedRow?.Result.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignora */ }
    }

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.Result.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }
}
