using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Microsoft.VisualBasic.FileIO;
// AudioPreview vive en Etiquetador.App.Services (ya importado arriba)

namespace Etiquetador.App.ViewModels;

/// <summary>Una copia dentro de un grupo de duplicados.</summary>
public partial class DupRow : ObservableObject
{
    public string Group { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Quality { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";

    /// <summary>Marcada para enviar a la papelera.</summary>
    [ObservableProperty] private bool _marcada;

    /// <summary>Es la copia que conviene conservar de su grupo.</summary>
    [ObservableProperty] private bool _esMejor;

    /// <summary>Por qué se considera la mejor (o qué le falta para serlo).</summary>
    [ObservableProperty] private string _motivo = "";

    /// <summary>Verde para la copia a conservar. El resto sin fondo, para que destaque una sola.</summary>
    public IBrush? Fondo => EsMejor ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)) : null;

    partial void OnEsMejorChanged(bool value) => OnPropertyChanged(nameof(Fondo));
}

/// <summary>Opción de criterio de duplicados (con etiqueta legible).</summary>
public sealed record DupModeOption(DuplicateMode Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Cómo se decide qué copia conservar de cada grupo.</summary>
public enum DupKeep { PrioridadYCalidad, Calidad, Duracion }

public sealed record DupKeepOption(DupKeep Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Una carpeta de la biblioteca, con su papel en la búsqueda de duplicados.</summary>
public partial class DupFolderOption : ObservableObject
{
    public string Path { get; init; } = "";
    public string Nombre => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;

    [ObservableProperty] private bool _prioritaria;
    [ObservableProperty] private bool _excluida;

    public Action? AlCambiar { get; set; }

    // Prioritaria y excluida se contradicen: una carpeta que no se mira no puede preferirse.
    partial void OnPrioritariaChanged(bool value)
    {
        if (value && Excluida) Excluida = false;
        AlCambiar?.Invoke();
    }

    partial void OnExcluidaChanged(bool value)
    {
        if (value && Prioritaria) Prioritaria = false;
        AlCambiar?.Invoke();
    }
}

/// <summary>Pestaña Duplicados: agrupa canciones repetidas de la biblioteca compartida.</summary>
public partial class DuplicatesViewModel : ScanViewModelBase
{
    public ObservableCollection<DupRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    /// <summary>Carpetas de la biblioteca, para marcarlas como prioritarias o excluidas.</summary>
    public ObservableCollection<DupFolderOption> Folders { get; } = new();

    public DupModeOption[] ModeOptions { get; } =
    {
        new(DuplicateMode.ArtistTitle, "Artista + título"),
        new(DuplicateMode.TitleOnly, "Solo título (más agresivo)"),
        new(DuplicateMode.ArtistTitleDuration, "Artista + título + duración (estricto)"),
    };

    public DupKeepOption[] KeepOptions { get; } =
    {
        new(DupKeep.PrioridadYCalidad, "Carpeta prioritaria, luego calidad"),
        new(DupKeep.Calidad, "Mejor calidad"),
        new(DupKeep.Duracion, "Mayor duración"),
    };

    private readonly AudioPreview _preview;
    private readonly AppEngine _engine;
    private bool _cargando;

    [ObservableProperty] private DupRow? _selectedRow;
    [ObservableProperty] private DupModeOption _selectedMode;
    [ObservableProperty] private DupKeepOption _selectedKeep;

    /// <summary>Cuántas copias hay marcadas ahora mismo (para el botón de la papelera).</summary>
    [ObservableProperty] private int _marcadas;

    public DuplicatesViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        _preview = engine.Preview;
        _selectedMode = ModeOptions[0];
        _selectedKeep = KeepOptions.FirstOrDefault(k => k.Value.ToString() == engine.Config.DupKeepCriterion)
                        ?? KeepOptions[0];

        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(DupRow.Group)));

        CargarCarpetas();
        Store.Changed += CargarCarpetas;   // al reescanear pueden aparecer carpetas nuevas
        if (Store.IsScanned) Recompute();
    }

    /// <summary>Rehace la lista de carpetas a partir de la biblioteca, conservando lo ya marcado.</summary>
    private void CargarCarpetas()
    {
        _cargando = true;
        try
        {
            var cfg = _engine.Config;
            Folders.Clear();
            foreach (var f in Store.Folders.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                var op = new DupFolderOption
                {
                    Path = f,
                    Prioritaria = cfg.PriorityFolders.Contains(f, StringComparer.OrdinalIgnoreCase),
                    Excluida = cfg.ExcludedDupFolders.Contains(f, StringComparer.OrdinalIgnoreCase),
                };
                op.AlCambiar = GuardarCarpetasYRecalcular;
                Folders.Add(op);
            }
        }
        finally { _cargando = false; }
    }

    private void GuardarCarpetasYRecalcular()
    {
        if (_cargando) return;
        var cfg = _engine.Config;
        cfg.PriorityFolders = Folders.Where(f => f.Prioritaria).Select(f => f.Path).ToList();
        cfg.ExcludedDupFolders = Folders.Where(f => f.Excluida).Select(f => f.Path).ToList();
        _engine.SaveConfig();
        if (Store.IsScanned) Recompute();
    }

    partial void OnSelectedModeChanged(DupModeOption value) { if (Store.IsScanned) Recompute(); }

    partial void OnSelectedKeepChanged(DupKeepOption value)
    {
        _engine.Config.DupKeepCriterion = value.Value.ToString();
        _engine.SaveConfig();
        if (Store.IsScanned) Recompute();
    }

    private bool EsPrioritaria(string carpeta)
        => _engine.Config.PriorityFolders.Contains(carpeta, StringComparer.OrdinalIgnoreCase);

    /// <summary>Puntuación de una copia: la más alta del grupo es la que conviene conservar.</summary>
    private double Puntuar(Track t) => SelectedKeep.Value switch
    {
        // La carpeta prioritaria pesa más que cualquier diferencia técnica: es una decisión del
        // usuario sobre dónde vive su colección buena.
        DupKeep.PrioridadYCalidad => (EsPrioritaria(t.Folder) ? 1_000_000 : 0) + t.Bitrate * 1000.0 + t.DurationSeconds,
        DupKeep.Calidad => t.Bitrate * 1000.0 + t.DurationSeconds,
        DupKeep.Duracion => t.DurationSeconds * 1000.0 + t.Bitrate,
        _ => 0,
    };

    private string MotivoDe(Track t) => SelectedKeep.Value switch
    {
        DupKeep.PrioridadYCalidad => EsPrioritaria(t.Folder)
            ? "Carpeta prioritaria"
            : (t.Bitrate > 0 ? $"Mejor calidad ({t.Bitrate} kbps)" : "Mejor copia"),
        DupKeep.Calidad => t.Bitrate > 0 ? $"Mejor calidad ({t.Bitrate} kbps)" : "Mejor copia",
        DupKeep.Duracion => $"Mayor duración ({TimeSpan.FromSeconds(t.DurationSeconds):m\\:ss})",
        _ => "",
    };

    protected override void Recompute()
    {
        var excluidas = _engine.Config.ExcludedDupFolders;
        var fuente = excluidas.Count == 0
            ? Store.Tracks.AsEnumerable()
            : Store.Tracks.Where(t => !excluidas.Contains(t.Folder, StringComparer.OrdinalIgnoreCase));

        var lista = fuente.ToList();
        var groups = DuplicateFinder.Find(lista, SelectedMode.Value);

        Rows.Clear();
        int copies = 0, conPrioridad = 0;
        foreach (var g in groups)
        {
            var label = $"{(string.IsNullOrWhiteSpace(g.Artist) ? "¿?" : g.Artist)} - {g.Title}   ({g.Tracks.Count} copias)";

            // La mejor del grupo según el criterio elegido.
            Track? mejor = null;
            double mejorPunt = double.NegativeInfinity;
            foreach (var t in g.Tracks)
            {
                var p = Puntuar(t);
                if (p > mejorPunt) { mejorPunt = p; mejor = t; }
            }
            if (mejor != null && EsPrioritaria(mejor.Folder)) conPrioridad++;

            foreach (var t in g.Tracks)
            {
                copies++;
                var esMejor = ReferenceEquals(t, mejor);
                Rows.Add(new DupRow
                {
                    Group = label,
                    FileName = t.FileName,
                    Quality = t.Quality,
                    Duration = t.Duration,
                    Folder = t.Folder,
                    FilePath = t.FilePath,
                    EsMejor = esMejor,
                    Motivo = esMejor ? MotivoDe(t) : "",
                });
            }
        }

        // Al recalcular no queda nada marcado: marcar es una decisión sobre la lista que se ve.
        RowsView.Refresh();
        Marcadas = 0;

        var excl = excluidas.Count > 0 ? $" · {excluidas.Count} carpeta(s) excluida(s)" : "";
        var prio = conPrioridad > 0 ? $" · {conPrioridad} grupo(s) resueltos por carpeta prioritaria" : "";
        Status = $"{groups.Count} grupo(s) de duplicados · {copies} archivos implicados{excl}{prio}.";
    }

    private void RecontarMarcadas() => Marcadas = Rows.Count(r => r.Marcada);

    /// <summary>
    /// Marca todas las copias MENOS la mejor de cada grupo: deja la biblioteca a un clic de quedar
    /// limpia, conservando siempre un ejemplar de cada canción.
    /// </summary>
    [RelayCommand]
    private void MarkAllButBest()
    {
        foreach (var r in Rows) r.Marcada = !r.EsMejor;
        RecontarMarcadas();
        Status = $"{Marcadas} copia(s) marcadas. Se conserva una de cada canción.";
    }

    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var r in Rows) r.Marcada = false;
        RecontarMarcadas();
        Status = "Marcas quitadas.";
    }

    /// <summary>Recuenta tras marcar o desmarcar a mano desde la tabla.</summary>
    public void MarcaCambiada() => RecontarMarcadas();

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

    /// <summary>Las copias marcadas, para que la vista pueda confirmar antes de borrar.</summary>
    public List<DupRow> ParaPapelera() => Rows.Where(r => r.Marcada).ToList();

    /// <summary>Envía el archivo seleccionado a la Papelera de Windows (recuperable), no lo borra del todo.</summary>
    [RelayCommand]
    private void SendToRecycleBin()
    {
        var row = SelectedRow;
        if (row == null || string.IsNullOrEmpty(row.FilePath)) return;
        _preview.Stop();   // si suena, el archivo está abierto y no se puede mover a la papelera
        var (ok, err) = Papelera(row.FilePath);
        Status = ok ? $"Enviado a la papelera: {row.FileName}" : "No se pudo enviar a la papelera: " + err;
        if (ok) Store.RemoveTrack(row.FilePath);
    }

    /// <summary>Envía a la papelera TODAS las copias marcadas. La vista confirma antes de llamar.</summary>
    public void SendMarkedToRecycleBin()
    {
        var objetivo = ParaPapelera();
        if (objetivo.Count == 0) { Status = "No hay ninguna copia marcada."; return; }

        _preview.Stop();
        var borrados = new List<string>();
        var fallos = new List<string>();
        foreach (var r in objetivo)
        {
            var (ok, err) = Papelera(r.FilePath);
            if (ok) borrados.Add(r.FilePath);
            else fallos.Add($"{r.FileName}: {err}");
        }

        // De una sola vez: quitarlos uno a uno recalcularía la biblioteca entera por cada archivo.
        Store.RemoveTracks(borrados);

        // RemoveTracks dispara Changed -> Recompute, que rehace la lista; el estado se pone después.
        Status = fallos.Count == 0
            ? $"{borrados.Count} copia(s) enviadas a la papelera."
            : $"{borrados.Count} enviadas, {fallos.Count} con problemas. La primera: {fallos[0]}";
    }

    private static (bool Ok, string Error) Papelera(string ruta)
    {
        try
        {
            FileSystem.DeleteFile(ruta, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return (true, "");
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
