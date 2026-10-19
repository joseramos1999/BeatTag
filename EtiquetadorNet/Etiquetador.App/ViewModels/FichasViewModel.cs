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
public partial class FichasViewModel : ScanViewModelBase, IPaginaVisible
{
    private readonly AppEngine _engine;

    /// <summary>Las seleccionadas, y el orden en que se guarda lo escrito al cambiar de selección.</summary>
    private readonly SeleccionConFicha<FichaRow> _sel;
    private IReadOnlyList<FichaRow> Seleccion => _sel.Filas;

    /// <summary>Mientras esta página guarda, no tiene que atender su propio aviso de fichas cambiadas.</summary>
    private bool _guardando;

    /// <summary>El error del último guardado, para que el aviso «guardada para no perderla» no lo tape.</summary>
    private string _errorAlGuardar = "";

    /// <summary>La tabla solo se rehace si la página está a la vista; si no, al volver a ella.</summary>
    private readonly RecalculoDiferido _recalculo;

    private List<FichaRow> _filas = new();
    public IReadOnlyList<FichaRow> Rows => _filas;

    /// <summary>Se sustituye entera al recalcular: un aviso a la tabla en vez de uno por canción.</summary>
    [ObservableProperty] private DataGridCollectionView _rowsView;
    public FichaEditorViewModel Editor { get; } = new();

    [ObservableProperty] private FichaRow? _selectedRow;
    [ObservableProperty] private string _busqueda = "";
    [ObservableProperty] private string _filtroInfo = "";

    public IReadOnlyList<string> Filtros { get; } = new[] { "Todas", "Sin ficha", "Con ficha", "Sin energía" };
    [ObservableProperty] private string _filtro = "Todas";

    public FichasViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        _rowsView = new DataGridCollectionView(_filas);
        _sel = new SeleccionConFicha<FichaRow>(Editor);
        // Un cambio de la biblioteca rehace la tabla; uno de fichas hecho en otra página solo relee las
        // fichas, y así se conservan la selección y la posición de la tabla.
        _recalculo = new RecalculoDiferido(Recompute, RefrescarFichas);
        Editor.GuardarPedido += GuardarSeleccion;
        _engine.FichasCambiadas += () =>
        {
            if (_guardando) return;
            // A la vista, se releen las fichas al momento; oculta, al volver.
            _recalculo.PedirRefresco();
        };
        Status = "Selecciona canciones y rellena su ficha a la derecha.";
        // Sin recálculo aquí: se hace la primera vez que la página se muestra.
    }

    /// <summary>
    /// Lo avisa la vista. Al salir de la página se guarda lo escrito en el panel: al volver, la tabla
    /// puede rehacerse (si la biblioteca cambió mientras tanto) y lo escrito se perdería.
    /// </summary>
    public bool Visible
    {
        get => _recalculo.Visible;
        set
        {
            if (!value && _recalculo.Visible) GuardarLoEscrito("al salir de la página");
            _recalculo.CambiarVisible(value);
        }
    }

    protected override void RecomputeReactive() => _recalculo.Pedir();

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
        // Lo escrito en el panel se guarda antes de rehacer la tabla, que deja la selección vacía.
        var guardada = GuardarLoEscrito("antes de actualizar la tabla");
        var error = _errorAlGuardar;

        // Reponer de una vez: cada alta suelta avisaría a la tabla por separado. El comentario ya lo
        // decía, pero el código hacía Clear y un Add por canción.
        _filas = Store.Tracks.Select(t => new FichaRow(t, _engine.Fichas.Obtener(t.FilePath))).ToList();
        RowsView = VistaTabla.Nueva(_filas, RowsView);
        AplicarFiltro();
        Seleccionar(Array.Empty<FichaRow>());
        ActualizarEstado();
        if (error.Length > 0) Status = $"⚠ No se pudo guardar en disco lo escrito en la ficha: {error}";
        else if (guardada) Status = "Lo escrito en la ficha se guardó antes de actualizar la tabla. " + Status;
    }

    /// <summary>
    /// Guarda lo escrito en el panel de ficha para la selección ACTUAL, si hay algo. Devuelve true si
    /// guardó, y lo dice en el estado (salvo que no se pudiera escribir en disco: entonces se queda a la
    /// vista ese aviso, que es el que importa).
    /// </summary>
    private bool GuardarLoEscrito(string cuando)
    {
        var cuantas = Seleccion.Count;
        _errorAlGuardar = "";
        if (!_sel.GuardarLoEscrito()) return false;
        if (_errorAlGuardar.Length == 0) Status = TextoGuardadoParaNoPerder(cuantas, cuando);
        return true;
    }

    internal static string TextoGuardadoParaNoPerder(int cuantas, string cuando)
        => cuantas == 1
            ? $"Ficha guardada {cuando}, para no perder lo escrito."
            : $"Ficha guardada en {cuantas} canciones {cuando}, para no perder lo escrito.";

    /// <summary>
    /// Se cierra la aplicación: lo escrito en el panel y no guardado se guarda. Visto en la revisión:
    /// cambiar de fila o de página ya lo guardaba, pero cerrar la ventana con texto a medio escribir en
    /// Etiquetas lo tiraba sin avisar.
    /// </summary>
    public void GuardarAlCerrar() => GuardarLoEscrito("al cerrar la aplicación");

    private void ActualizarEstado()
    {
        var con = Rows.Count(r => r.TieneFicha);
        Status = $"{con} de {Rows.Count} canciones con ficha.";
    }

    /// <summary>
    /// La selección de la tabla ha cambiado. Se llama desde la vista. Lo escrito y no guardado se
    /// guarda en la selección ANTERIOR antes de cargar la nueva.
    /// </summary>
    public void Seleccionar(IEnumerable<FichaRow> filas)
    {
        var anteriores = Seleccion.Count;
        _errorAlGuardar = "";
        if (_sel.Cambiar(filas) && _errorAlGuardar.Length == 0)
            Status = TextoGuardadoParaNoPerder(anteriores, "al cambiar de selección");
    }

    public int CuantasSeleccionadas => Seleccion.Count;
    public int CuantasSeleccionadasConFicha => Seleccion.Count(r => r.TieneFicha);

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
        if (Seleccion.Count == 0) return;
        Aplicar(new CambiosFicha { Energia = Math.Clamp(energia, 0, Fichas.EnergiaMaxima) });
    }

    private void Aplicar(CambiosFicha cambios)
    {
        if (Seleccion.Count == 0) return;
        if (!cambios.HayAlguno) { Status = "No hay cambios que guardar."; return; }

        var err = _engine.GuardarFichas(Seleccion.Select(r => r.FilePath).ToList(), cambios);
        _errorAlGuardar = err;
        Releer(Seleccion);
        _sel.Recargar();
        AvisarSinEco();

        Status = err.Length > 0
            ? $"⚠ No se pudo guardar en disco: {err}"
            : Seleccion.Count == 1 ? "Ficha guardada." : $"Ficha guardada en {Seleccion.Count} canciones.";
    }

    /// <summary>Borra la ficha de las seleccionadas. La confirmación la pide la vista.</summary>
    public void BorrarSeleccion()
    {
        if (Seleccion.Count == 0) return;
        var err = _engine.BorrarFichas(Seleccion.Select(r => r.FilePath).ToList());
        Releer(Seleccion);
        _sel.Recargar();
        AvisarSinEco();
        Status = err.Length > 0 ? $"⚠ No se pudo guardar en disco: {err}" : $"Ficha borrada en {Seleccion.Count} canción(es).";
    }

    /// <summary>Copia la ficha de las seleccionadas a su comentario. La confirmación la pide la vista.</summary>
    public async Task VolcarAlComentarioAsync()
    {
        if (IsBusy || Seleccion.Count == 0) return;
        var rutas = Seleccion.Select(r => r.FilePath).ToList();
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

    /// <summary>
    /// Otra página ha cambiado fichas: se releen todas las filas, sin rehacer la tabla, así que la
    /// selección y la posición se conservan.
    /// </summary>
    private void RefrescarFichas()
    {
        Releer(Rows);
        _sel.Recargar();
        ActualizarEstado();
        if (Filtro != "Todas") AplicarFiltro();
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
