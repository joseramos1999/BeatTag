using System.Threading.Tasks;
using System.Collections.ObjectModel;

using Avalonia.Collections;
using Etiquetador.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core.Analysis;

namespace Etiquetador.App.ViewModels;

public sealed class QualRow
{
    public string FileName { get; init; } = "";
    public string QualityLabel { get; init; } = "";
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public string Duration { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";
    public bool IsPoor { get; init; }
}

/// <summary>Pestaña Calidad: clasifica el audio de la biblioteca compartida.</summary>
public partial class QualityViewModel : ScanViewModelBase
{
    public ObservableCollection<QualRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private QualRow? _selectedRow;
    [ObservableProperty] private bool _onlyPoor;

    public QualityViewModel(AppEngine engine) : base(engine.Library)
    {
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(QualRow.Folder)));
        if (Store.IsScanned) Recompute();
    }

    /// <summary>Cuadro de búsqueda de la tabla. Se combina con el filtro de «solo baja calidad».</summary>
    [ObservableProperty] private string _busqueda = "";

    /// <summary>Cuántas quedan a la vista mientras hay búsqueda. Vacío si no se está filtrando.</summary>
    [ObservableProperty] private string _filtroInfo = "";

    partial void OnOnlyPoorChanged(bool value) => AplicarFiltro();
    partial void OnBusquedaChanged(string value) => AplicarFiltro();

    /// <summary>
    /// Los dos filtros a la vez. Se combinan en vez de pisarse: buscar dentro de lo que ya está
    /// acotado a baja calidad es exactamente lo que se quiere hacer.
    /// </summary>
    private void AplicarFiltro()
    {
        var buscando = Busqueda.Trim().Length > 0;
        RowsView.Filter = !OnlyPoor && !buscando
            ? null
            : o => o is QualRow r
                   && (!OnlyPoor || r.IsPoor)
                   && (!buscando || BusquedaTexto.Coincide(Busqueda, r.FileName, r.Folder, r.QualityLabel));
        RowsView.Refresh();
        FiltroInfo = buscando ? $"{RowsView.Count} de {Rows.Count}" : "";
    }

    protected override void Recompute()
    {
        Rows.Clear();
        int poor = 0;
        foreach (var t in Store.Tracks)
        {
            var tier = AudioQuality.Rate(t);
            var isPoor = AudioQuality.IsPoor(t);
            if (isPoor) poor++;
            Rows.Add(new QualRow
            {
                FileName = t.FileName,
                QualityLabel = AudioQuality.Label(tier),
                Bitrate = t.Bitrate,
                SampleRate = t.SampleRate,
                Channels = t.Channels,
                Duration = t.Duration,
                Folder = t.Folder,
                FilePath = t.FilePath,
                IsPoor = isPoor,
            });
        }
        RowsView.Refresh();
        Status = $"{Rows.Count} archivos · {poor} de baja calidad (< 192 kbps).";
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }
}
