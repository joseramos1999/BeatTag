using System;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;

namespace Etiquetador.App.ViewModels;

/// <summary>Pestaña Editor: elige una canción de la biblioteca y edita a mano su título y tags.</summary>
public partial class EditorViewModel : ViewModelBase
{
    private readonly AppEngine _engine;
    private LibraryStore Store => _engine.Library;

    public DataGridCollectionView TracksView { get; }

    [ObservableProperty] private Track? _selectedTrack;
    [ObservableProperty] private string _search = "";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _bpm = "";
    [ObservableProperty] private string _comment = "";

    [ObservableProperty] private bool _canEdit;
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
            TracksView.Refresh();
            Status = $"Guardado: {t.FileName}";
        }
        catch (Exception e) { Status = "Error al guardar: " + e.Message; }
    }
}
