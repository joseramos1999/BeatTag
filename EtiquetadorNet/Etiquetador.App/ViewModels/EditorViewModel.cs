using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;
using Etiquetador.App.Views;

namespace Etiquetador.App.ViewModels;

/// <summary>Pestaña Editor: elige una canción de la biblioteca y edita a mano su título y tags.</summary>
public partial class EditorViewModel : ViewModelBase
{
    private readonly AppEngine _engine;
    private LibraryStore Store => _engine.Library;

    public DataGridCollectionView TracksView { get; }

    [ObservableProperty] private Track? _selectedTrack;
    [ObservableProperty] private string _search = "";

    /// <summary>Nombre del archivo, editable: al guardar se renombra si ha cambiado.</summary>
    [ObservableProperty] private string _fileName = "";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _bpm = "";
    [ObservableProperty] private string _comment = "";

    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Selecciona una canción de la lista para editar sus tags.";

    public EditorViewModel(AppEngine engine)
    {
        _engine = engine;
        TracksView = new DataGridCollectionView(Store.Tracks);
        TracksView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(Track.Folder)));
        Store.Changed += () => TracksView.Refresh();
    }

    partial void OnSearchChanged(string value)
    {
        var q = value?.Trim() ?? "";
        TracksView.Filter = q.Length == 0
            ? null
            : o => o is Track t &&
                   ((t.FileName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.Artist?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        TracksView.Refresh();
    }

    /// <summary>Selecciona en la lista el track con esa ruta (para "editar esta canción" desde otra pestaña).</summary>
    public void SelectByPath(string filePath)
    {
        var t = System.Linq.Enumerable.FirstOrDefault(Store.Tracks,
            x => string.Equals(x.FilePath, filePath, System.StringComparison.OrdinalIgnoreCase));
        if (t != null) SelectedTrack = t;
    }

    partial void OnSelectedTrackChanged(Track? value)
    {
        if (value == null) { CanEdit = false; Status = "Selecciona una canción de la lista."; return; }
        LoadFrom(value);
    }

    private void LoadFrom(Track t)
    {
        var v = TagEditor.Read(t.FilePath);   // lee siempre lo real del archivo
        FileName = Path.GetFileName(t.FilePath);
        Title = v.Title; Artist = v.Artist; Album = v.Album; Genre = v.Genre;
        Year = v.Year == 0 ? "" : v.Year.ToString();
        Bpm = v.Bpm == 0 ? "" : v.Bpm.ToString();
        Comment = v.Comment;
        CanEdit = true;
        Status = "Editando: " + t.FileName;
    }

    [RelayCommand]
    private void Reload() { if (SelectedTrack != null) LoadFrom(SelectedTrack); }

    [RelayCommand]
    private void PlayPreview()
    {
        if (SelectedTrack == null) return;
        try { _engine.Preview.Toggle(SelectedTrack.FilePath); Status = "Reproduciendo (≈25% del tema)…"; }
        catch (Exception e) { Status = "No se pudo reproducir (¿formato sin soporte?): " + e.Message; }
    }

    [RelayCommand]
    private void StopPreview() => _engine.Preview.Stop();

    [RelayCommand]
    private async Task SaveAsync()
    {
        var t = SelectedTrack;
        if (t == null) return;
        uint year = uint.TryParse(Year, out var y) ? y : 0;
        uint bpm = uint.TryParse(Bpm, out var b) ? b : 0;
        try
        {
            _engine.ReleaseAudio();   // el reproductor mantiene el archivo abierto y la escritura fallaría
            await Task.Run(() => TagEditor.Write(t.FilePath, Title, Artist, Album, Genre, year, bpm, Comment));
            // Refleja los cambios en la biblioteca en memoria (para las demás pestañas).
            t.Title = Title; t.Artist = Artist; t.Album = Album; t.Genre = Genre; t.Year = year; t.Bpm = bpm;

            var renamed = TryRename(t, out var renameMsg);
            TracksView.Refresh();
            Status = $"Guardado: {t.FileName}" + (renameMsg.Length > 0 ? " · " + renameMsg : "");
            if (renamed) _engine.Logger.Ok($"Editor: renombrado a '{t.FileName}'");
        }
        catch (Exception e) { Status = "Error al guardar: " + e.Message; }
    }

    /// <summary>
    /// Renombra el archivo si cambiaste el nombre. Pasa por la misma validación que Enriquecer
    /// (RenameSafety): no admite rutas ni separadores, fuerza la extensión original y deduplica.
    /// </summary>
    private bool TryRename(Track t, out string message)
    {
        message = "";
        var propuesto = (FileName ?? "").Trim();
        var actual = Path.GetFileName(t.FilePath);
        if (propuesto.Length == 0 || string.Equals(propuesto, actual, StringComparison.Ordinal)) return false;

        var dir = Path.GetDirectoryName(t.FilePath) ?? "";
        if (!RenameSafety.TryResolveTarget(propuesto, actual, dir, out var target, out var error))
        {
            message = "NO se renombró: " + error;
            FileName = actual;
            return false;
        }
        try
        {
            var destino = Path.Combine(dir, target);
            File.Move(t.FilePath, destino);
            t.FilePath = destino;   // FileName se deriva de la ruta
            FileName = target;
            message = $"renombrado a «{target}»";
            return true;
        }
        catch (Exception e)
        {
            message = "NO se pudo renombrar: " + e.Message;
            FileName = actual;
            return false;
        }
    }

    /// <summary>Servicios del diálogo "Reanalizar…" (los mismos que en Enriquecer).</summary>
    public SearchServices SearchServicesFor(string filePath)
        => new(_engine.FindCandidatesAsync, _engine.ResolveLinkAsync,
               () => _engine.IdentifyByFingerprintAsync(filePath));

    /// <summary>
    /// Reanaliza la canción y VUELCA la propuesta en el formulario (no escribe nada): aquí mandas tú,
    /// así que revisas lo propuesto y decides si guardar.
    /// </summary>
    public async Task ReanalyzeIntoFormAsync(Track? t, string searchArtist = "", string searchTitle = "",
        string searchSource = "")
    {
        t ??= SelectedTrack;
        if (t == null) { Status = "Selecciona antes una canción."; return; }
        if (IsBusy) { Status = "Hay un proceso en curso; espera a que termine."; return; }
        IsBusy = true;
        Status = "Buscando…";
        try
        {
            var opts = _engine.BuildOptions();
            opts.SearchArtist = searchArtist;
            opts.SearchTitle = searchTitle;
            opts.SearchSource = searchSource;
            var r = await Task.Run(() => _engine.Processor.ProcessAsync(t.FilePath, isAcapella: false, opts));

            if (!r.Found && !r.CleanOnly) { Status = "Sin resultado. Prueba otra grafía, un enlace o la huella."; return; }

            // Solo se rellena lo que trae la propuesta: no se borra lo que ya tenías.
            if (r.Title.Length > 0) Title = r.Title;
            if (r.Artist.Length > 0) Artist = r.Artist;
            if (r.Album.Length > 0) Album = r.Album;
            if (r.Genre.Length > 0) Genre = r.Genre;
            if (r.Year.Length > 0) Year = r.Year;
            if (r.Bpm.Length > 0) Bpm = r.Bpm;
            if (r.New.Length > 0) FileName = r.New;

            Status = $"Propuesta de {r.Source} (confianza {r.Score}). Revisa y pulsa Guardar.";
        }
        catch (Exception e) { Status = "Error al reanalizar: " + e.Message; }
        finally { IsBusy = false; }
    }
}
