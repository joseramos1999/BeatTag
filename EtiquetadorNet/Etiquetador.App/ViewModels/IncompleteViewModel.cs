using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using System.IO;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

public sealed class IncRow : IFilaBuscable
{
    public string FileName { get; init; } = "";
    public string Missing { get; init; } = "";
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Genre { get; init; }
    public uint Year { get; init; }
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";

    private string? _textoBusqueda;

    /// <summary>Lo que encuentra el cuadro «Buscar…». Los campos no cambian: se prepara una sola vez.</summary>
    public string TextoBusqueda => _textoBusqueda ??= ConsultaTabla.Preparar(FileName, Folder, Missing, Title, Artist, Genre);
}

/// <summary>Pestaña Incompletas: tracks de la biblioteca compartida a los que les falta algún tag.</summary>
public partial class IncompleteViewModel : ScanViewModelBase
{
    private readonly AppEngine _engine;

    public ObservableCollection<IncRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private IncRow? _selectedRow;

    /// <summary>Cuadro de busqueda de la tabla: filtra lo ya listado.</summary>
    [ObservableProperty] private string _busqueda = "";

    /// <summary>Cuantas quedan a la vista mientras hay busqueda. Vacio si no se esta filtrando.</summary>
    [ObservableProperty] private string _filtroInfo = "";

    partial void OnBusquedaChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) _filtroDiferido.Ahora();
        else _filtroDiferido.Pedir();   // se filtra al dejar de teclear, no en cada pulsación
    }

    private void AplicarFiltro()
    {
        var consulta = ConsultaTabla.De(Busqueda);
        // Asignar el filtro ya rehace la vista; no hace falta otro Refresh().
        RowsView.Filter = consulta.Vacia ? null : o => o is IncRow r && consulta.Coincide(r);
        FiltroInfo = consulta.Vacia ? "" : $"{RowsView.Count} de {Rows.Count}";
    }

    private readonly BusquedaDiferida _filtroDiferido;

    public IncompleteViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(IncRow.Folder)));
        _filtroDiferido = new BusquedaDiferida(AplicarFiltro);
        if (Store.IsScanned) Recompute();
    }

    private static string MissingFields(Track t) => string.Join(", ", t.CamposQueFaltan());

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

    /// <summary>
    /// Lleva a Enriquecer, para revisarla allí, una propuesta que no debe aplicarse sola. Devuelve si
    /// ha entrado en la lista. Lo conecta MainViewModel; sin conectar, la propuesta queda en la caché.
    /// </summary>
    public Func<ProcessResult, bool>? MostrarParaRevisar { get; set; }

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
            if (r.Revisar.Length > 0)
            {
                // Lo que solo identificó MusicBrainz o AcoustID, o un nombre con huellas de una regla
                // fallida, no se escribe sin que lo veas: antes se aplicaba aquí directamente. Se
                // guarda como propuesta y se revisa en Enriquecer, desmarcada y con el motivo.
                _engine.Analysis.Set(row.FilePath, opts.Signature(), r);
                _engine.Analysis.Save();
                var enLista = MostrarParaRevisar?.Invoke(r) ?? false;
                Status = $"Encontrada, pero no se aplica automáticamente: {r.Revisar}. Propuesta: «{r.New}»."
                       + (enLista ? " Está en Enriquecer, desmarcada, pendiente de revisión." : "");
                return;
            }
            var fields = _engine.BuildFields();
            var undo = _engine.Paths.NuevoManifiesto();
            var res = await Task.Run(() => _engine.AplicarUnoAsync(r, _engine.Config.Overwrite, fields, "keep", null, undo, _engine.Paths.DoneLog));
            // M12: se relee solo este archivo (con su nombre de antes y el de después), no todas las
            // carpetas. Al terminar, la biblioteca avisa y Recompute quita la fila si ya está completa.
            await _engine.Library.RefrescarAsync(new[] { row.FilePath, res.FinalPath });
            Status = EstadoTrasReanalizar(row.FileName, res);
        }
        catch (Exception e) { Status = "Error al reanalizar: " + e.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Qué se dice tras reanalizar y aplicar una canción. Si el renombrado falla, se dice: antes solo
    /// se miraban las etiquetas y salía «Actualizada» con el archivo todavía con su nombre viejo.
    /// </summary>
    public static string EstadoTrasReanalizar(string archivo, ApplyOneResult res)
    {
        var sinAnotar = res.UndoErr.Length > 0
            ? $"  ⚠ el cambio NO quedó anotado, no se podrá deshacer ({res.UndoErr})"
            : "";
        return ResultadoAplicacion.Clasificar(res) switch
        {
            DesenlaceAplicacion.Fallida => $"⚠ No se pudo escribir en «{archivo}»: {(res.TagErr + " " + res.RenErr).Trim()}",
            DesenlaceAplicacion.SinRenombrar => $"Etiquetas actualizadas en «{archivo}», pero no se pudo renombrar: {res.RenErr}" + sinAnotar,
            _ => $"Actualizada: {archivo}" + sinAnotar,
        };
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }
}
