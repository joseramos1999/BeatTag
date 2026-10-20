using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción de la bandeja: su ficha, su estado y lo que se ha encontrado al revisarla.</summary>
public sealed partial class BandejaRow : FichaRow
{
    public EntradaBandeja Entrada { get; }
    public IReadOnlyList<AvisoBandeja> Avisos { get; private set; } = Array.Empty<AvisoBandeja>();

    public BandejaRow(Track track, FichaDj? ficha, EntradaBandeja entrada) : base(track, ficha) => Entrada = entrada;

    public EstadoBandeja EstadoValor => Entrada.Estado;
    public string Estado => AlmacenBandeja.Nombre(Entrada.Estado);
    public DateTime Llegada => Entrada.Llegada;
    public string LlegadaTexto => Entrada.Llegada == default ? "" : Entrada.Llegada.ToString("dd/MM/yyyy HH:mm");
    public bool HayAvisos => Avisos.Count > 0;

    /// <summary>
    /// Todos los avisos en corto, para la columna. El detalle (con qué archivo coincide) va en el
    /// panel de la derecha: en la columna, el texto largo del primero tapaba que hubiera un segundo.
    /// Si la descarga mejora la copia de la biblioteca, se dice así en lugar de «Ya en la biblioteca»:
    /// es la que conviene sustituir, no descartar (I14).
    /// </summary>
    public string AvisoPrincipal => string.Join(" · ", Avisos.Select(a =>
        a.Tipo == TipoAviso.YaEnBiblioteca && PuedeSustituir ? "Mejora la de la biblioteca" : RevisionBandeja.NombreCorto(a.Tipo)));

    public string AvisosDetalle
    {
        get
        {
            var texto = string.Join("\n", Avisos.Select(a => "• " + a.Texto));
            if (Sustitucion == null) return texto;
            var linea = $"• Es de mejor calidad que la copia de la biblioteca ({Sustitucion.Motivo}): "
                      + "puedes sustituirla con «Sustituir la copia de la biblioteca».";
            return texto.Length == 0 ? linea : texto + "\n" + linea;
        }
    }

    /// <summary>¿Dice algún aviso que esta canción ya la tienes? Para el filtro «Con aviso de duplicado» (X15).</summary>
    public bool TieneAvisoDeDuplicado => RevisionBandeja.TieneAvisoDeDuplicado(Avisos);

    /// <summary>La copia de la biblioteca que esta descarga puede sustituir (I14), o null.</summary>
    public PropuestaSustitucion? Sustitucion { get; private set; }

    public bool PuedeSustituir => Sustitucion != null;

    public void PonerAvisos(IReadOnlyList<AvisoBandeja> avisos, PropuestaSustitucion? sustitucion = null)
    {
        Avisos = avisos;
        Sustitucion = sustitucion;
        OnPropertyChanged(nameof(Avisos));
        OnPropertyChanged(nameof(HayAvisos));
        OnPropertyChanged(nameof(AvisoPrincipal));
        OnPropertyChanged(nameof(AvisosDetalle));
        OnPropertyChanged(nameof(TieneAvisoDeDuplicado));
        OnPropertyChanged(nameof(Sustitucion));
        OnPropertyChanged(nameof(PuedeSustituir));
    }

    public void EstadoCambiado()
    {
        OnPropertyChanged(nameof(EstadoValor));
        OnPropertyChanged(nameof(Estado));
    }
}

/// <summary>
/// Página Bandeja de entrada: la música recién descargada se revisa ANTES de entrar en la biblioteca.
///
/// La idea es cortar los problemas en la puerta. Un duplicado, un archivo de baja calidad o una
/// canción sin etiquetas que ya ha entrado en rekordbox cuesta mucho más de arreglar que uno que
/// todavía está en una carpeta aparte.
/// </summary>
public partial class BandejaViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina, IPaginaVisible
{
    private readonly AppEngine _engine;
    private readonly LibraryStore _store;

    /// <summary>Las seleccionadas, y el orden en que se guarda lo escrito al cambiar de selección.</summary>
    private readonly SeleccionConFicha<BandejaRow> _sel;
    private IReadOnlyList<BandejaRow> Seleccion => _sel.Filas;
    private bool _guardando;

    /// <summary>El error del último guardado, para que el aviso «guardada para no perderla» no lo tape.</summary>
    private string _errorAlGuardar = "";

    /// <summary>
    /// Revisar la bandeja contra la biblioteca: solo con la página a la vista, y si no, al volver.
    ///
    /// Cada cambio de la biblioteca (un envío a la papelera desde Duplicados, un escaneo tras renombrar)
    /// rehacía en el hilo de la interfaz el índice de TODA la biblioteca para buscar duplicados de la
    /// bandeja: unas 15.000 claves con la limpieza de títulos de cada una, aunque la bandeja no
    /// estuviera abierta. Ahora el índice se construye fuera del hilo de la interfaz y se reutiliza
    /// hasta el siguiente cambio de la biblioteca; cambiar el destino ya no lo rehace.
    /// </summary>
    private readonly RecalculoDiferido _revision;
    private readonly IndiceReutilizable<IndiceCanciones> _indiceBiblioteca = new();
    private int _revisionEnCurso;

    public ObservableCollection<BandejaRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }
    public FichaEditorViewModel Editor { get; } = new();

    /// <summary>Carpetas de la biblioteca a las que se puede pasar la música.</summary>
    public ObservableCollection<string> Destinos { get; } = new();

    [ObservableProperty] private string _carpeta = "";
    [ObservableProperty] private string? _destino;
    [ObservableProperty] private BandejaRow? _selectedRow;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _busqueda = "";
    [ObservableProperty] private string _filtroInfo = "";
    [ObservableProperty] private string _recuento = "";
    [ObservableProperty] private string _detalle = "";

    public IReadOnlyList<string> Filtros => TodosLosFiltros;

    internal static readonly IReadOnlyList<string> TodosLosFiltros =
        new[] { "Todas", "Con avisos", FiltroDuplicado, "Recibidas", "Analizadas", "Revisadas", "Preparadas" };

    /// <summary>
    /// Las que ya tienes: copia exacta en la biblioteca o repetida en la bandeja (X15). Con este filtro
    /// y Ctrl+A se descartan de una vez las descargas sobrantes.
    /// </summary>
    internal const string FiltroDuplicado = "Con aviso de duplicado";
    [ObservableProperty] private string _filtro = "Todas";

    public bool HayCarpeta => Carpeta.Length > 0;

    public BandejaViewModel(AppEngine engine)
    {
        _engine = engine;
        _store = engine.Library;
        RowsView = new DataGridCollectionView(Rows);
        _sel = new SeleccionConFicha<BandejaRow>(Editor);
        Editor.GuardarPedido += GuardarFicha;

        _carpeta = engine.Config.BandejaCarpeta;
        _destino = engine.Config.BandejaDestino;
        // Nadie espera a esta revisión: si falla, se dice en el estado y en el registro.
        _revision = new RecalculoDiferido(() => _ = SinEsperar.Lanzar(RevisarAsync, ErrorAlRevisar));
        RellenarDestinos();

        // La biblioteca cambia (se escanea, se añaden carpetas): hay que volver a buscar duplicados
        // contra lo que hay AHORA. No se relee ningún archivo de la bandeja, solo se revisa.
        _store.Changed += () =>
        {
            _indiceBiblioteca.Invalidar();
            RellenarDestinos();
            _revision.Pedir();
        };
        _store.Folders.CollectionChanged += (_, _) => RellenarDestinos();
        _engine.FichasCambiadas += () => { if (!_guardando) RefrescarFichas(); };

        Status = Carpeta.Length == 0
            ? "Elige la carpeta donde dejas la música nueva. Tiene que estar fuera de la biblioteca."
            : "Pulsa «Analizar» para revisar lo que hay en la bandeja.";
    }

    partial void OnCarpetaChanged(string value) => OnPropertyChanged(nameof(HayCarpeta));

    partial void OnDestinoChanged(string? value)
    {
        if (value == null) return;
        _engine.Config.BandejaDestino = value;
        _engine.SaveConfig();
        _revision?.Pedir();   // el aviso de nombre ocupado depende del destino (el índice no cambia)
    }

    /// <summary>
    /// Lo avisa la vista. Al salir de la página se guarda lo escrito en el panel de ficha, para que una
    /// revisión al volver no lo pierda.
    /// </summary>
    public bool Visible
    {
        get => _revision.Visible;
        set
        {
            if (!value && _revision.Visible) GuardarLoEscrito("al salir de la página");
            _revision.CambiarVisible(value);
        }
    }

    /// <summary>
    /// Guarda lo escrito en el panel de ficha para la selección ACTUAL, si hay algo. Si no se pudo
    /// escribir en disco, se queda a la vista ese aviso.
    /// </summary>
    private bool GuardarLoEscrito(string cuando)
    {
        var cuantas = Seleccion.Count;
        _errorAlGuardar = "";
        if (!_sel.GuardarLoEscrito()) return false;
        if (_errorAlGuardar.Length == 0) Status = FichasViewModel.TextoGuardadoParaNoPerder(cuantas, cuando);
        return true;
    }

    /// <summary>
    /// Se cierra la aplicación: lo escrito en el panel y no guardado se guarda, igual que al cambiar de
    /// fila o de página.
    /// </summary>
    public void GuardarAlCerrar() => GuardarLoEscrito("al cerrar la aplicación");

    partial void OnBusquedaChanged(string value) => AplicarFiltro();
    partial void OnFiltroChanged(string value) => AplicarFiltro();

    // --- Carpetas ---

    /// <summary>
    /// Destinos posibles: las carpetas marcadas de la biblioteca, más la que el usuario eligiera a
    /// mano si cuelga de una de ellas. Una carpeta fuera de la biblioteca no vale como destino: la
    /// canción «entraría en la biblioteca» y no aparecería en ella.
    /// </summary>
    private void RellenarDestinos()
    {
        var actual = Destino;
        var raices = _store.EnabledPaths();
        var lista = new List<string>(raices);
        if (!string.IsNullOrEmpty(actual) && !lista.Contains(actual, StringComparer.OrdinalIgnoreCase)
            && raices.Any(r => Rutas.EstaDentroDe(actual, r)))
            lista.Add(actual);

        Destinos.Clear();
        foreach (var d in lista) Destinos.Add(d);

        var elegido = lista.FirstOrDefault(d => string.Equals(d, actual, StringComparison.OrdinalIgnoreCase))
                      ?? lista.FirstOrDefault();
        if (!string.Equals(elegido, Destino, StringComparison.Ordinal)) Destino = elegido;
    }

    /// <summary>Fija la carpeta de la bandeja, si es válida, y la analiza. Devuelve "" o el motivo del rechazo.</summary>
    public async Task<string> ElegirCarpetaAsync(string carpeta)
    {
        var motivo = RevisionBandeja.ValidarCarpeta(carpeta, _store.Folders.Select(f => f.Path), Directory.Exists);
        if (motivo.Length > 0) { Status = motivo; return motivo; }

        Carpeta = carpeta;
        _engine.Config.BandejaCarpeta = carpeta;
        _engine.SaveConfig();
        _engine.Logger.Detail($"Bandeja de entrada: {carpeta}");
        await AnalizarAsync();
        return "";
    }

    /// <summary>Elige un destino dentro de la biblioteca. Devuelve "" o el motivo del rechazo.</summary>
    public string ElegirDestino(string carpeta)
    {
        var raices = _store.EnabledPaths();
        if (!raices.Any(r => Rutas.MismaCarpeta(carpeta, r) || Rutas.EstaDentroDe(carpeta, r)))
        {
            Status = "El destino tiene que ser una carpeta de la biblioteca (o estar dentro de una), para que las canciones aparezcan en ella.";
            return Status;
        }
        if (!Destinos.Contains(carpeta, StringComparer.OrdinalIgnoreCase)) Destinos.Add(carpeta);
        Destino = Destinos.First(d => string.Equals(d, carpeta, StringComparison.OrdinalIgnoreCase));
        return "";
    }

    [RelayCommand]
    private async Task AbrirCarpetaAsync()
    {
        if (Carpeta.Length > 0 && Directory.Exists(Carpeta)) await Shell.OpenFolderAsync(Carpeta);
    }

    // --- Analizar ---

    [RelayCommand]
    public async Task AnalizarAsync()
    {
        if (IsBusy) return;
        var motivo = RevisionBandeja.ValidarCarpeta(Carpeta, _store.Folders.Select(f => f.Path), Directory.Exists);
        if (motivo.Length > 0) { Status = motivo; return; }

        // La tabla se va a rehacer: lo escrito en el panel se guarda antes.
        GuardarLoEscrito("antes de volver a analizar");
        IsBusy = true;
        Progress = 0;
        Status = "Leyendo la bandeja…";
        try
        {
            var carpeta = Carpeta;
            var progreso = new Progress<double>(p => Progress = p);
            var diario = SustitucionBandeja.CarpetaDiario(_engine.Paths.DataDir);
            IReadOnlyList<string> recogidas = Array.Empty<string>();
            var tracks = await Task.Run(() =>
            {
                // Antes de leer la carpeta: una sustitución que se cortó al cerrarse la aplicación
                // devuelve aquí la descarga a la bandeja (I14), y así entra en esta misma lectura.
                recogidas = SustitucionBandeja.Recuperar(diario);
                var rutas = LibraryScanner.EnumerateFiles(carpeta, recursive: true).ToList();
                var lista = new List<Track>(rutas.Count);
                for (var i = 0; i < rutas.Count; i++)
                {
                    var t = LibraryScanner.ReadTrack(rutas[i]);
                    t.Folder = carpeta;
                    lista.Add(t);
                    ((IProgress<double>)progreso).Report(100.0 * (i + 1) / rutas.Count);
                }
                return lista;
            });

            var ahora = DateTime.Now;
            var nuevas = 0;
            Rows.Clear();
            foreach (var t in tracks.OrderBy(t => t.FileName, StringComparer.CurrentCultureIgnoreCase))
            {
                var e = _engine.Bandeja.Registrar(t.FilePath, ahora);
                if (e.Estado == EstadoBandeja.Recibida) nuevas++;
                e.Estado = AlmacenBandeja.TrasAnalizar(e.Estado);
                Rows.Add(new BandejaRow(t, _engine.Fichas.Obtener(t.FilePath), e));
            }
            var olvidadas = _engine.Bandeja.Podar(tracks.Select(t => t.FilePath));
            GuardarBandeja();

            Seleccionar(Array.Empty<BandejaRow>());
            await RevisarAsync();
            _engine.Logger.Detail($"Bandeja: {tracks.Count} canciones ({nuevas} nuevas; {olvidadas} ya no estaban).");
            Status = tracks.Count == 0
                ? "La bandeja está vacía."
                : $"{tracks.Count} canciones en la bandeja" + (nuevas > 0 ? $", {nuevas} nuevas." : ".");
            foreach (var r in recogidas) _engine.Logger.Log("Bandeja: " + r, LogKind.Err);
            if (recogidas.Count > 0) Status = "⚠ " + string.Join(" ", recogidas) + " " + Status;
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Error al analizar la bandeja", e);
            Status = "Error al leer la bandeja: " + e.Message;
        }
        finally { IsBusy = false; Progress = 0; }
    }

    /// <summary>
    /// Vuelve a buscar avisos sin releer los archivos. El índice de la biblioteca se construye fuera del
    /// hilo de la interfaz, una vez por cada cambio de la biblioteca; el de la bandeja es pequeño.
    /// </summary>
    private async Task RevisarAsync()
    {
        var revision = ++_revisionEnCurso;
        if (Rows.Count == 0) { ActualizarRecuento(); return; }

        var canciones = _store.IsScanned ? _store.Tracks.ToList() : null;   // instantánea en el hilo de la interfaz
        // Construir el índice de la biblioteca lleva 1-2 s con 15.000 canciones, y mientras tanto la
        // tabla sigue enseñando los avisos de antes. Visto en la revisión: no se decía nada. Ahora sí.
        if (canciones != null && !_indiceBiblioteca.Listo) Status = TextoRevisando;
        var biblioteca = canciones == null
            ? IndiceCanciones.Vacio
            : await _indiceBiblioteca.ObtenerAsync(() => new IndiceCanciones(canciones));
        // Si mientras tanto se pidió otra revisión, esa se encarga de poner los avisos.
        if (revision != _revisionEnCurso) return;

        var bandeja = new IndiceCanciones(Rows.Select(r => r.Track));
        var destino = Destino ?? "";
        foreach (var r in Rows)
            r.PonerAvisos(RevisionBandeja.Revisar(r.Track, biblioteca, bandeja, destino, File.Exists),
                          SustitucionBandeja.Proponer(r.Track, biblioteca));
        AplicarFiltro();
        ActualizarRecuento();
        MostrarDetalle();
        // Solo si nadie ha escrito otra cosa en el estado mientras tanto.
        if (Status == TextoRevisando) Status = TextoRevisada(Rows.Count, Rows.Count(r => r.HayAvisos));
    }

    internal const string TextoRevisando = "Revisando la bandeja contra la biblioteca…";

    internal static string TextoRevisada(int canciones, int conAvisos)
        => canciones == 1
            ? (conAvisos == 0 ? "Bandeja revisada contra la biblioteca: la canción no tiene avisos."
                              : "Bandeja revisada contra la biblioteca: la canción tiene avisos.")
            : conAvisos == 0
                ? $"Bandeja revisada contra la biblioteca: ninguna de las {canciones} canciones tiene avisos."
                : $"Bandeja revisada contra la biblioteca: {conAvisos} de {canciones} canciones con avisos.";

    /// <summary>Una revisión que nadie esperaba ha fallado: se dice, en vez de dejar los avisos como estaban.</summary>
    private void ErrorAlRevisar(Exception e)
    {
        _engine.Logger.Error("Bandeja: error al revisar contra la biblioteca", e);
        Status = $"No se pudo revisar la bandeja contra la biblioteca ({e.Message}). "
               + "Los avisos de la tabla pueden no estar al día: pulsa «Analizar» para volver a intentarlo.";
    }

    private void AplicarFiltro()
    {
        var q = Busqueda.Trim();
        Func<BandejaRow, bool> porEstado = Filtro switch
        {
            "Con avisos" => r => r.HayAvisos,
            FiltroDuplicado => r => r.TieneAvisoDeDuplicado,
            "Recibidas" => r => r.EstadoValor == EstadoBandeja.Recibida,
            "Analizadas" => r => r.EstadoValor == EstadoBandeja.Analizada,
            "Revisadas" => r => r.EstadoValor == EstadoBandeja.Revisada,
            "Preparadas" => r => r.EstadoValor == EstadoBandeja.Preparada,
            _ => _ => true,
        };
        RowsView.Filter = q.Length == 0 && Filtro == "Todas"
            ? null
            : o => o is BandejaRow r && porEstado(r)
                   && (q.Length == 0 || BusquedaTexto.Coincide(q, r.FileName, r.Artist, r.Title, r.Genre, r.AvisosDetalle));
        RowsView.Refresh();
        FiltroInfo = RowsView.Filter == null ? "" : $"{RowsView.Count} de {Rows.Count}";
    }

    private void ActualizarRecuento()
    {
        int N(EstadoBandeja e) => Rows.Count(r => r.EstadoValor == e);
        Recuento = Rows.Count == 0
            ? ""
            : $"Recibidas {N(EstadoBandeja.Recibida)} · Analizadas {N(EstadoBandeja.Analizada)} · "
              + $"Revisadas {N(EstadoBandeja.Revisada)} · Preparadas {N(EstadoBandeja.Preparada)} · "
              + $"Con avisos {Rows.Count(r => r.HayAvisos)}";
    }

    // --- Selección, estados y ficha ---

    /// <summary>
    /// La selección ha cambiado. Lo escrito y no guardado en el panel de ficha se guarda en la
    /// selección ANTERIOR antes de cargar la nueva: antes se perdía sin aviso al hacer clic en otra fila.
    /// </summary>
    public void Seleccionar(IEnumerable<BandejaRow> filas)
    {
        var anteriores = Seleccion.Count;
        _errorAlGuardar = "";
        if (_sel.Cambiar(filas) && _errorAlGuardar.Length == 0)
            Status = FichasViewModel.TextoGuardadoParaNoPerder(anteriores, "al cambiar de selección");
        MostrarDetalle();
    }

    public int CuantasSeleccionadas => Seleccion.Count;

    private void MostrarDetalle()
    {
        Detalle = Seleccion.Count switch
        {
            0 => "",
            1 => Seleccion[0].HayAvisos || Seleccion[0].PuedeSustituir ? Seleccion[0].AvisosDetalle : "Sin avisos.",
            _ => $"{Seleccion.Count} seleccionadas · {Seleccion.Count(r => r.HayAvisos)} con avisos.",
        };
        OnPropertyChanged(nameof(PuedeSustituirSeleccion));
    }

    /// <summary>Las filas seleccionadas ahora, para que la vista confirme antes de descartar.</summary>
    public IReadOnlyList<BandejaRow> Seleccionadas => Seleccion.ToList();

    /// <summary>Una sola fila seleccionada y que puede sustituir a su copia de la biblioteca (I14).</summary>
    public bool PuedeSustituirSeleccion => Seleccion.Count == 1 && Seleccion[0].PuedeSustituir;

    /// <summary>La fila que se sustituiría ahora, o null.</summary>
    public BandejaRow? FilaASustituir => PuedeSustituirSeleccion ? Seleccion[0] : null;

    [RelayCommand]
    private void MarcarRevisadas() => Marcar(EstadoBandeja.Revisada);

    [RelayCommand]
    private void MarcarPreparadas() => Marcar(EstadoBandeja.Preparada);

    /// <summary>Quita la marca que puso el usuario y la deja como la dejó el análisis.</summary>
    [RelayCommand]
    private void QuitarMarca() => Marcar(EstadoBandeja.Analizada);

    private void Marcar(EstadoBandeja estado)
    {
        if (Seleccion.Count == 0) { Status = "Selecciona antes las canciones."; return; }
        foreach (var r in Seleccion)
        {
            _engine.Bandeja.Marcar(r.FilePath, estado);
            r.EstadoCambiado();
        }
        GuardarBandeja();
        ActualizarRecuento();
        if (Filtro != "Todas") AplicarFiltro();
        Status = $"{Seleccion.Count} canción(es) marcadas como {AlmacenBandeja.Nombre(estado).ToLowerInvariant()}s.";
    }

    private void GuardarFicha() => AplicarFicha(Editor.Cambios());

    public void PonerEnergiaRapida(int energia)
    {
        if (Seleccion.Count == 0) return;
        AplicarFicha(new CambiosFicha { Energia = Math.Clamp(energia, 0, Fichas.EnergiaMaxima) });
    }

    private void AplicarFicha(CambiosFicha cambios)
    {
        if (Seleccion.Count == 0) return;
        if (!cambios.HayAlguno) { Status = "No hay cambios que guardar."; return; }
        var err = _engine.GuardarFichas(Seleccion.Select(r => r.FilePath).ToList(), cambios);
        _errorAlGuardar = err;
        foreach (var r in Seleccion) r.Actualizar(_engine.Fichas.Obtener(r.FilePath));
        _sel.Recargar();
        _guardando = true;
        try { _engine.AvisarFichasCambiadas(); }
        finally { _guardando = false; }
        Status = err.Length > 0 ? $"⚠ No se pudo guardar en disco: {err}" : "Ficha guardada.";
    }

    private void RefrescarFichas()
    {
        foreach (var r in Rows) r.Actualizar(_engine.Fichas.Obtener(r.FilePath));
        _sel.Recargar();
    }

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
            Status = FichasViewModel.TextoVolcado(res);
        }
        catch (Exception e) { Status = "Error al escribir el comentario: " + e.Message; }
        finally { IsBusy = false; }
    }

    // --- Pasar a la biblioteca ---

    public IReadOnlyList<BandejaRow> Preparadas => Rows.Where(r => r.EstadoValor == EstadoBandeja.Preparada).ToList();

    /// <summary>
    /// Mueve las preparadas a la carpeta de destino. La confirmación la pide la vista.
    ///
    /// Cada movimiento se anota para deshacer EN CUANTO se hace, así que si algo se corta a medias
    /// lo ya movido sigue siendo reversible. Un archivo que no se puede mover (por ejemplo, porque en
    /// el destino ya hay uno con ese nombre) se queda en la bandeja con su estado: no se pierde nada.
    /// </summary>
    public async Task PasarABibliotecaAsync()
    {
        if (IsBusy) return;
        var filas = Preparadas;
        var destino = Destino ?? "";
        if (filas.Count == 0) { Status = "No hay canciones preparadas. Márcalas como preparadas cuando estén listas."; return; }
        if (destino.Length == 0) { Status = "Elige a qué carpeta de la biblioteca van."; return; }

        // Lo escrito en el panel se guarda ANTES de mover: después, las rutas de la selección ya no existen.
        GuardarLoEscrito("antes de pasar a la biblioteca");
        IsBusy = true;
        Progress = 0;
        Status = $"Pasando {filas.Count} canción(es) a la biblioteca…";
        try
        {
            _engine.ReleaseAudio();
            var undo = Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var rutas = filas.Select(r => r.FilePath).ToList();
            var progreso = new Progress<double>(p => Progress = p);

            var (hechos, fallos, undoErr) = await Task.Run(() =>
            {
                var ok = new List<Traslado>();
                var mal = new List<Traslado>();
                var errUndo = "";
                for (var i = 0; i < rutas.Count; i++)
                {
                    var t = TrasladoBandeja.Mover(rutas[i], destino);
                    if (t.Ok)
                    {
                        ok.Add(t);
                        var rec = new UndoRecord { OrigPath = t.Origen, FinalPath = t.Destino, Renamed = true };
                        try { File.AppendAllText(undo, JsonSerializer.Serialize(rec) + "\n"); }
                        catch (Exception e) { errUndo = e.Message; }
                    }
                    else mal.Add(t);
                    ((IProgress<double>)progreso).Report(100.0 * (i + 1) / rutas.Count);
                }
                return (ok, mal, errUndo);
            });

            foreach (var t in hechos)
            {
                _engine.Fichas.Mover(t.Origen, t.Destino);
                _engine.SeguirArchivo(t.Origen, t.Destino, default);   // huella, identificación y sonoridad, también
                _engine.Bandeja.Olvidar(t.Origen);
                _engine.Logger.Detail($"Bandeja → biblioteca: {Path.GetFileName(t.Origen)} → {t.Destino}");
            }
            foreach (var t in fallos)
                _engine.Logger.Log($"Bandeja: no se movió {Path.GetFileName(t.Origen)}: {t.Error}", LogKind.Err);

            var errFichas = _engine.Fichas.Guardar();
            GuardarBandeja();

            var movidas = new HashSet<string>(hechos.Select(h => h.Origen), StringComparer.OrdinalIgnoreCase);
            foreach (var r in Rows.Where(r => movidas.Contains(r.FilePath)).ToList()) Rows.Remove(r);
            Seleccionar(Array.Empty<BandejaRow>());

            // Ya están en una carpeta de la biblioteca: se reescanea para que aparezcan en todas las
            // páginas. Esto vuelve a revisar la bandeja con la biblioteca nueva.
            if (hechos.Count > 0) await _store.ScanAsync();
            else await RevisarAsync();

            var texto = $"{hechos.Count} canción(es) pasadas a la biblioteca";
            if (fallos.Count > 0) texto += $" · {fallos.Count} se quedan en la bandeja: {fallos[0].Error}";
            texto += undoErr.Length > 0
                ? $". ⚠ El traslado NO quedó anotado y no se podrá deshacer: {undoErr}"
                : hechos.Count > 0 ? ". Se puede deshacer desde Enriquecer." : ".";
            if (errFichas.Length > 0) texto += $" ⚠ No se pudieron guardar las fichas: {errFichas}";
            Status = texto;
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Error al pasar canciones a la biblioteca", e);
            Status = "Error al pasar a la biblioteca: " + e.Message;
        }
        finally { IsBusy = false; Progress = 0; }
    }

    // --- Descartar a la papelera (X15) ---
    //
    // Entraban 80 descargas, 25 salían con «Ya está en la biblioteca», y para quitarlas había que
    // abrir la carpeta en el explorador, buscarlas a mano, borrarlas y volver a Analizar. Ahora se
    // envían a la papelera desde aquí, en segundo plano, y salen de la tabla sin releer la bandeja.

    /// <summary>
    /// Qué se descarta de verdad de las filas seleccionadas: de cada canción repetida en la bandeja
    /// que no está en la biblioteca y tiene todas sus copias seleccionadas, la mejor se conserva (ver
    /// <see cref="RevisionBandeja.PlanearDescarte{T}"/>). La vista confirma con este plan.
    /// </summary>
    public PlanDescarte<BandejaRow> PlanearDescarte(IReadOnlyList<BandejaRow> filas)
        => RevisionBandeja.PlanearDescarte(filas, Rows, r => r.Track,
                                           r => r.Avisos.Any(a => a.Tipo == TipoAviso.YaEnBiblioteca));

    /// <summary>
    /// Envía a la papelera las descargas del plan (la vista ya ha confirmado), olvida sus entradas y
    /// las quita de la tabla. Las que la papelera rechaza (una unidad sin papelera, un archivo en uso)
    /// se quedan en la tabla y se dice el motivo. Las que el plan conserva no se tocan.
    /// </summary>
    public async Task DescartarAsync(PlanDescarte<BandejaRow> plan)
    {
        var filas = plan.Descartar;
        if (IsBusy || filas.Count == 0) return;

        // Lo escrito en el panel se guarda antes, como al pasar a la biblioteca: después, las rutas
        // de la selección ya no existen.
        GuardarLoEscrito("antes de descartar");
        IsBusy = true;
        Progress = 0;
        Status = EnvioPapelera.TextoAvance(0, filas.Count);
        using var cts = new CancellationTokenSource();
        _descarteCts = cts;
        try
        {
            _engine.ReleaseAudio();   // si suena, el archivo está abierto y no se puede mover a la papelera
            var rutas = filas.Select(r => r.FilePath).ToList();
            var avance = new Progress<(int Hechas, int Total)>(p =>
            {
                if (!IsBusy) return;
                Progress = p.Total > 0 ? 100.0 * p.Hechas / p.Total : 0;
                Status = EnvioPapelera.TextoAvance(p.Hechas, p.Total);
            });
            // Con token: al cerrar la ventana se para entre un archivo y el siguiente, y lo ya enviado
            // se olvida y se guarda como siempre (hallazgo rendimiento-3).
            var res = await EnvioPapelera.EnviarAsync(rutas, progreso: avance, ct: cts.Token);

            foreach (var ruta in res.Enviadas)
            {
                _engine.Bandeja.Olvidar(ruta);
                _engine.Logger.Detail($"Bandeja: descartada a la papelera {Path.GetFileName(ruta)}");
            }
            foreach (var (archivo, motivo) in res.Fallos)
                _engine.Logger.Log($"Bandeja: no se descartó {archivo}: {motivo}", LogKind.Err);
            if (res.Enviadas.Count > 0) GuardarBandeja();

            QuitarFilas(res.Enviadas);
            // Sin releer ningún archivo: solo se rehacen los avisos, porque la que estaba «repetida en
            // la bandeja» puede haber dejado de estarlo. Interrumpido es que la ventana se cierra: no
            // hace falta.
            if (res.Enviadas.Count > 0 && !res.Interrumpido) await RevisarAsync();
            Status = (res.Interrumpido ? "Descarte detenido. " : "")
                   + EnvioPapelera.Resumen(res.Enviadas.Count, res.Fallos, "descarga")
                   + (plan.Conservar.Count > 0 ? " " + RevisionBandeja.TextoConservadasTrasDescartar(plan.Conservar.Count) : "");
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Error al descartar descargas de la bandeja", e);
            Status = "Error al descartar: " + e.Message;
        }
        finally { _descarteCts = null; IsBusy = false; Progress = 0; }
    }

    private CancellationTokenSource? _descarteCts;

    /// <summary>Para el descarte a la papelera entre un archivo y el siguiente. Lo usa el cierre de la ventana.</summary>
    public void CancelarDescarte()
    {
        try { _descarteCts?.Cancel(); }
        catch (ObjectDisposedException) { /* ya había terminado */ }
    }

    /// <summary>Quita de la tabla las filas de esas rutas, sin releer la bandeja.</summary>
    private void QuitarFilas(IReadOnlyCollection<string> rutas)
    {
        if (rutas.Count == 0) return;
        var fuera = new HashSet<string>(rutas, StringComparer.OrdinalIgnoreCase);
        // La selección se suelta ANTES de quitar las filas: lo escrito en el panel ya se guardó.
        if (Seleccion.Any(r => fuera.Contains(r.FilePath))) Seleccionar(Array.Empty<BandejaRow>());
        foreach (var r in Rows.Where(r => fuera.Contains(r.FilePath)).ToList()) Rows.Remove(r);
        AplicarFiltro();
        ActualizarRecuento();
    }

    // --- Sustituir la copia de la biblioteca (I14) ---

    /// <summary>
    /// La descarga ocupa el lugar de su copia de la biblioteca: la antigua va a la papelera, la nueva
    /// toma su carpeta y su nombre (con su propia extensión) y se lleva la ficha de DJ fusionada. Queda
    /// anotado para deshacer. La vista ya ha confirmado.
    ///
    /// Si la unidad de la copia antigua no tiene papelera, no se sustituye: enviar ahí sería borrar
    /// para siempre. Si el historial para deshacer no se puede abrir, no se toca ningún archivo.
    /// </summary>
    public async Task SustituirAsync(BandejaRow fila)
    {
        if (IsBusy || fila.Sustitucion is not { } propuesta) return;
        var nueva = fila.FilePath;
        var vieja = propuesta.Vieja.FilePath;

        var sinPapelera = MotivoParaNoSustituir(fila);
        if (sinPapelera.Length > 0) { Status = sinPapelera; return; }

        GuardarLoEscrito("antes de sustituir");
        IsBusy = true;
        Status = $"Sustituyendo «{propuesta.Vieja.FileName}» por la descarga…";
        try
        {
            _engine.ReleaseAudio();
            var paths = _engine.Paths;
            var res = await Task.Run(() =>
            {
                using var manifiesto = ManifiestoDeshacer.Abrir(paths.NuevoManifiesto(), out var error);
                if (manifiesto == null)
                    return new ResultadoSustitucion(vieja,
                        $"No se puede anotar el cambio para deshacer ({error}), así que no se ha tocado ningún archivo.", false);
                return SustitucionBandeja.Ejecutar(nueva, vieja, Papelera.Enviar, manifiesto.Anotar,
                                                   SustitucionBandeja.CarpetaDiario(paths.DataDir));
            });

            if (!res.Ok)
            {
                _engine.Logger.Log($"Bandeja: no se sustituyó {Path.GetFileName(vieja)}: {res.Error}", LogKind.Err);
                Status = res.Error;
                // La antigua ya no está aunque algo fallara después: la biblioteca tiene que saberlo.
                if (res.ViejaEnPapelera) await _store.RefrescarAsync(new[] { vieja });
                return;
            }

            SustitucionBandeja.LlevarFicha(_engine.Fichas, vieja, nueva, res.Destino);
            var errFichas = _engine.Fichas.Guardar();
            // ANTES de que las cachés sigan a la descarga: lo decidido sobre la copia antigua no vale
            // para ella (aplicada, descartada, casillas, avisos de audio aceptados).
            var errDecisiones = OlvidarLoDecididoSobre(new[] { vieja, res.Destino }, _engine.Applied, _engine.Ignored,
                                                       _engine.Marks, _engine.AudioAceptadas)
                ? GuardarDecisiones()
                : "";
            _engine.SeguirArchivo(nueva, res.Destino, default);   // huella, identificación y sonoridad de la descarga
            _engine.Bandeja.Olvidar(nueva);
            GuardarBandeja();
            QuitarFilas(new[] { nueva });
            _guardando = true;
            try { _engine.AvisarFichasCambiadas(); }
            finally { _guardando = false; }
            _engine.Logger.Detail($"Bandeja: {Path.GetFileName(nueva)} sustituye a {vieja} (la antigua, a la papelera) → {res.Destino}");

            // Solo esas dos rutas: la antigua sale y la nueva entra. Recorrer la biblioteca entera
            // costaría segundos con 15.000 canciones (M12). Esto vuelve a revisar la bandeja.
            await _store.RefrescarAsync(new[] { vieja, res.Destino });

            Status = TextoSustituida(propuesta.Vieja.FileName, res.Destino, SustitucionBandeja.TextoRekordbox(vieja, res.Destino),
                                     errFichas, errDecisiones);
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Error al sustituir una copia de la biblioteca", e);
            Status = "Error al sustituir: " + e.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Por qué no se puede sustituir ahora la copia de la biblioteca de esta fila, o "". Lo mira la
    /// vista ANTES de abrir el diálogo: visto en revisión, con la copia antigua en una memoria USB el
    /// diálogo prometía «se puede recuperar desde la papelera» y, tras confirmar, se negaba.
    /// </summary>
    public string MotivoParaNoSustituir(BandejaRow fila)
    {
        if (fila.Sustitucion is not { } propuesta) return "";
        var motivo = Papelera.MotivoSinPapelera(propuesta.Vieja.FilePath);
        return motivo.Length == 0 ? "" : TextoSinPapelera(motivo);
    }

    internal static string TextoSinPapelera(string motivo)
        => "No se sustituye la copia de la biblioteca: " + (motivo.Length == 0 ? "" : char.ToLowerInvariant(motivo[0]) + motivo[1..]);

    /// <summary>El estado tras sustituir. Puro, para probarlo.</summary>
    internal static string TextoSustituida(string viejaNombre, string destino, string textoRekordbox, string errFichas,
                                           string errDecisiones = "")
    {
        var texto = $"Sustituida: «{viejaNombre}» está en la papelera y la descarga ocupa su lugar como «{Path.GetFileName(destino)}». "
                  + "Se puede deshacer desde Enriquecer; la copia antigua se recupera desde la papelera. " + textoRekordbox;
        if (errFichas.Length > 0) texto += $" ⚠ No se pudieron guardar las fichas: {errFichas}";
        if (errDecisiones.Length > 0)
            texto += $" ⚠ No se pudo guardar que la descarga está pendiente de revisar en Enriquecer: {errDecisiones}";
        return texto;
    }

    /// <summary>
    /// Lo que se decidió sobre el archivo que había en esas rutas deja de valer: ahora hay OTRO archivo.
    /// Devuelve si ha cambiado algo (y entonces hay que guardar).
    ///
    /// Visto en revisión (I14): con la misma extensión, el caso habitual (MP3 de 128 por MP3 de 320),
    /// la descarga ocupa la ruta de la copia antigua, y esa ruta seguía en aplicadas.json. Enriquecer
    /// la saltaba como «ya aplicada» incluso con «Reanalizar todo», así que la descarga se quedaba con
    /// las etiquetas de la tienda (alias y grafías del artista, género) mientras las normalizadas se
    /// iban a la papelera con la vieja; y la copia que se sustituye suele ser justo una que BeatTag ya
    /// había procesado. Por lo mismo se olvidan las descartadas a mano en Enriquecer, las casillas
    /// «Aplicar» (que se guardan por ruta y propuesta) y los avisos aceptados de «Comprobar audio»:
    /// todo eso se decidió mirando el otro archivo.
    /// </summary>
    internal static bool OlvidarLoDecididoSobre(IEnumerable<string> rutas, IgnoreList aplicadas, IgnoreList descartadas,
                                                ApplyMarks marcas, IgnoreList audioAceptadas)
    {
        var cambia = false;
        foreach (var ruta in rutas.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var lista in new[] { aplicadas, descartadas, audioAceptadas })
                if (lista.Contains(ruta)) { lista.Remove(ruta); cambia = true; }
            // La casilla va por ruta y huella de la propuesta (y, la de versiones antiguas, solo por ruta).
            var prefijo = EnrichViewModel.ClaveMarca(ruta, "");
            if (marcas.OlvidarSi(k => string.Equals(k, ruta, StringComparison.OrdinalIgnoreCase)
                                      || k.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)) > 0)
                cambia = true;
        }
        return cambia;
    }

    /// <summary>Guarda lo que <see cref="OlvidarLoDecididoSobre"/> ha cambiado. Devuelve "" o los fallos.</summary>
    private string GuardarDecisiones()
    {
        var fallos = new List<string>();
        foreach (var (que, guardar) in new (string, Func<string>)[]
                 {
                     ("aplicadas", _engine.Applied.Save), ("descartadas", _engine.Ignored.Save),
                     ("casillas «Aplicar»", _engine.Marks.Save), ("avisos aceptados de Comprobar audio", _engine.AudioAceptadas.Save),
                 })
        {
            string err;
            try { err = guardar(); }
            catch (Exception e) { err = e.Message; }
            if (err.Length > 0) fallos.Add($"{que}: {err}");
        }
        foreach (var f in fallos) _engine.Logger.Err("Bandeja: no se pudo guardar " + f);
        return string.Join(" · ", fallos);
    }

    private void GuardarBandeja()
    {
        var err = _engine.Bandeja.Guardar();
        if (err.Length > 0)
        {
            _engine.Logger.Err("No se pudo guardar el estado de la bandeja: " + err);
            Status = "⚠ No se pudo guardar el estado de la bandeja: " + err;
        }
    }

    // --- Menú contextual ---

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync() => await Shell.OpenContainingFolderAsync(SelectedRow?.FilePath);
}
