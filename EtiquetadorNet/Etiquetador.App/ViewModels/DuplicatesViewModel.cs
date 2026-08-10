using System.Threading.Tasks;
using System;
using System.Collections.ObjectModel;

using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core.Analysis;
using Microsoft.VisualBasic.FileIO;
// AudioPreview vive en Etiquetador.App.Services (ya importado arriba)

namespace Etiquetador.App.ViewModels;

/// <summary>Una copia dentro de un grupo de duplicados.</summary>
public sealed class DupRow
{
    public string Group { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Quality { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";
}

/// <summary>Opción de criterio de duplicados (con etiqueta legible).</summary>
public sealed record DupModeOption(DuplicateMode Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Pestaña Duplicados: agrupa canciones repetidas de la biblioteca compartida.</summary>
public partial class DuplicatesViewModel : ScanViewModelBase
{
    public ObservableCollection<DupRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    public DupModeOption[] ModeOptions { get; } =
    {
        new(DuplicateMode.ArtistTitle, "Artista + título"),
        new(DuplicateMode.TitleOnly, "Solo título (más agresivo)"),
        new(DuplicateMode.ArtistTitleDuration, "Artista + título + duración (estricto)"),
    };

    private readonly AudioPreview _preview;

    [ObservableProperty] private DupRow? _selectedRow;
    [ObservableProperty] private DupModeOption _selectedMode;

    public DuplicatesViewModel(AppEngine engine) : base(engine.Library)
    {
        _preview = engine.Preview;
        _selectedMode = ModeOptions[0];
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(DupRow.Group)));
        if (Store.IsScanned) Recompute();
    }

    partial void OnSelectedModeChanged(DupModeOption value) { if (Store.IsScanned) Recompute(); }

    protected override void Recompute()
    {
        var groups = DuplicateFinder.Find(Store.Tracks, SelectedMode.Value);
        Rows.Clear();
        int copies = 0;
        foreach (var g in groups)
        {
            var label = $"{(string.IsNullOrWhiteSpace(g.Artist) ? "¿?" : g.Artist)} - {g.Title}   ({g.Tracks.Count} copias)";
            foreach (var t in g.Tracks)
            {
                copies++;
                Rows.Add(new DupRow
                {
                    Group = label,
                    FileName = t.FileName,
                    Quality = t.Quality,
                    Duration = t.Duration,
                    Folder = t.Folder,
                    FilePath = t.FilePath,
                });
            }
        }
        RowsView.Refresh();
        Status = $"{groups.Count} grupo(s) de duplicados · {copies} archivos implicados.";
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private void StopPreview() => _preview.Stop();

    /// <summary>Envía el archivo seleccionado a la Papelera de Windows (recuperable), no lo borra del todo.</summary>
    [RelayCommand]
    private void SendToRecycleBin()
    {
        var row = SelectedRow;
        if (row == null || string.IsNullOrEmpty(row.FilePath)) return;
        try
        {
            _preview.Stop();   // si suena, el archivo está abierto y no se puede mover a la papelera
            FileSystem.DeleteFile(row.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            Store.RemoveTrack(row.FilePath);   // actualiza la biblioteca compartida (dispara Recompute en todas las pestañas)
            Status = $"Enviado a la papelera: {row.FileName}";
        }
        catch (Exception e) { Status = "No se pudo enviar a la papelera: " + e.Message; }
    }
}
