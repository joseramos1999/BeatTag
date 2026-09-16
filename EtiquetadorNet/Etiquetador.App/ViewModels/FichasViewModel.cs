using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Página Ficha DJ: rellenar, canción a canción o por lotes, lo que un DJ sabe de su música y no
/// cabe en las etiquetas de siempre.
/// </summary>
public partial class FichasViewModel : ScanViewModelBase
{
    private readonly AppEngine _engine;
    private List<FichaRow> _seleccion = new();

    /// <summary>Mientras esta página guarda, no tiene que atender su propio aviso de fichas cambiadas.</summary>
    private bool _guardando;

    public ObservableCollection<FichaRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }
    public FichaEditorViewModel Editor { get; } = new();

    [ObservableProperty] private FichaRow? _selectedRow;
    [ObservableProperty] private string _busqueda = "";
    [ObservableProperty] private string _filtroInfo = "";

    public IReadOnlyList<string> Filtros { get; } = new[] { "Todas", "Sin ficha", "Con ficha", "Sin energía" };
    [ObservableProperty] private string _filtro = "Todas";

    public FichasViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        RowsView = new DataGridCollectionView(Rows);
        Editor.GuardarPedido += GuardarSeleccion;
        _engine.FichasCambiadas += () => { if (!_guardando) RefrescarFichas(); };
        Status = "Selecciona canciones y rellena su ficha a la derecha.";
        if (Store.IsScanned) Recompute();
    }

    partial void OnBusquedaChanged(string value) => AplicarFiltro();
    partial void OnFiltroChanged(string value) => AplicarFiltro();

    private void AplicarFiltro()
    {
        var q = Busqueda.Trim();
        Func<FichaRow, bool> porFicha = Filtro switch
        {
            "Sin ficha" => r => !r.TieneFicha,
            "Con ficha" => r => r.TieneFicha,
            "Sin energía" => r => r.EnergiaValor == 0,
            _ => _ => true,
        };
        RowsView.Filter = q.Length == 0 && Filtro == "Todas"
            ? null
            : o => o is FichaRow r && porFicha(r)
                   && (q.Length == 0 || BusquedaTexto.Coincide(q, r.FileName, r.Artist, r.Title, r.Genre, r.Ambiente, r.Etiquetas));
        RowsView.Refresh();
        FiltroInfo = RowsView.Filter == null ? "" : $"{RowsView.Count} de {Rows.Count}";
    }

    protected override void Recompute()
    {
        var filas = Store.Tracks.Select(t => new FichaRow(t, _engine.Fichas.Obtener(t.FilePath))).ToList();

        // Reponer de una vez: cada alta suelta avisaría a la tabla por separado.
        Rows.Clear();
        foreach (var f in filas) Rows.Add(f);
        AplicarFiltro();
        Seleccionar(Array.Empty<FichaRow>());
        ActualizarEstado();
    }

    private void ActualizarEstado()
    {
        var con = Rows.Count(r => r.TieneFicha);
        Status = $"{con} de {Rows.Count} canciones con ficha.";
    }

    /// <summary>La selección de la tabla ha cambiado. Se llama desde la vista.</summary>
    public void Seleccionar(IEnumerable<FichaRow> filas)
    {
        _seleccion = filas.ToList();
        Editor.Cargar(_seleccion.Select(r => r.Ficha).ToList());
    }

    public int CuantasSeleccionadas => _seleccion.Count;
    public int CuantasSeleccionadasConFicha => _seleccion.Count(r => r.TieneFicha);

    /// <summary>Tras guardar, las filas afectadas se releen del almacén: es lo que de verdad quedó guardado.</summary>
    private void Releer(IEnumerable<FichaRow> filas)
    {
        foreach (var r in filas) r.Actualizar(_engine.Fichas.Obtener(r.FilePath));
    }

    private void GuardarSeleccion() => Aplicar(Editor.Cambios());

    /// <summary>
    /// Energía con el teclado: con varias seleccionadas, pulsar un número la pone en todas. Fichar
    /// una biblioteca grande a golpe de ratón no es realista; así se hace con una mano.
    /// </summary>
    public void PonerEnergiaRapida(int energia)
    {
        if (_seleccion.Count == 0) return;
        Aplicar(new CambiosFicha { Energia = Math.Clamp(energia, 0, Fichas.EnergiaMaxima) });
    }

    private void Aplicar(CambiosFicha cambios)
    {
        if (_seleccion.Count == 0) return;
        if (!cambios.HayAlguno) { Status = "No hay cambios que guardar."; return; }

        var err = _engine.GuardarFichas(_seleccion.Select(r => r.FilePath).ToList(), cambios);
        Releer(_seleccion);
        Editor.Cargar(_seleccion.Select(r => r.Ficha).ToList());
        AvisarSinEco();

        Status = err.Length > 0
            ? $"⚠ No se pudo guardar en disco: {err}"
            : _seleccion.Count == 1 ? "Ficha guardada." : $"Ficha guardada en {_seleccion.Count} canciones.";
    }

    /// <summary>Borra la ficha de las seleccionadas. La confirmación la pide la vista.</summary>
    public void BorrarSeleccion()
    {
        if (_seleccion.Count == 0) return;
        var err = _engine.BorrarFichas(_seleccion.Select(r => r.FilePath).ToList());
        Releer(_seleccion);
        Editor.Cargar(_seleccion.Select(r => r.Ficha).ToList());
        AvisarSinEco();
        Status = err.Length > 0 ? $"⚠ No se pudo guardar en disco: {err}" : $"Ficha borrada en {_seleccion.Count} canción(es).";
    }

    /// <summary>Copia la ficha de las seleccionadas a su comentario. La confirmación la pide la vista.</summary>
    public async Task VolcarAlComentarioAsync()
    {
        if (IsBusy || _seleccion.Count == 0) return;
        var rutas = _seleccion.Select(r => r.FilePath).ToList();
        IsBusy = true;
        Status = $"Escribiendo la ficha en el comentario de {rutas.Count} canción(es)…";
        try
        {
            _engine.ReleaseAudio();
            var res = await Task.Run(() => _engine.VolcarFichasAlComentario(rutas));
            Status = TextoVolcado(res);
        }
        catch (Exception e) { Status = "Error al escribir el comentario: " + e.Message; }
        finally { IsBusy = false; }
    }

    internal static string TextoVolcado(AppEngine.ResultadoVolcado res)
    {
        var texto = $"Comentario actualizado en {res.Escritas} canción(es)";
        if (res.SinCambio > 0) texto += $" · {res.SinCambio} ya estaban al día";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no se pudieron escribir (detalle en el registro)";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ El cambio NO quedó anotado y no se podrá deshacer: {res.UndoErr}"
            : res.Escritas > 0 ? ". Se puede deshacer desde Enriquecer." : ".";
        return texto;
    }

    private void AvisarSinEco()
    {
        _guardando = true;
        try { _engine.AvisarFichasCambiadas(); }
        finally { _guardando = false; }
        ActualizarEstado();
        if (Filtro != "Todas") AplicarFiltro();
    }

    /// <summary>Otra página ha cambiado fichas: se releen todas las filas.</summary>
    private void RefrescarFichas()
    {
        Releer(Rows);
        Editor.Cargar(_seleccion.Select(r => r.Ficha).ToList());
        ActualizarEstado();
    }

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private void EditThis()
    {
        if (SelectedRow != null) _engine.RequestEdit(SelectedRow.FilePath);
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync() => await Shell.OpenContainingFolderAsync(SelectedRow?.FilePath);
}
