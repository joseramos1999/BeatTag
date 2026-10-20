using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción en las tablas de la página rekordbox (no están, sin preparar, nunca pinchadas).</summary>
public sealed class FilaRekordbox : IFilaConArchivo
{
    public FilaRekordbox(string ruta, string artista, string titulo, int duracion, PistaRekordbox? pista, bool enBiblioteca)
    {
        RutaArchivo = ruta;
        Artista = artista;
        Titulo = titulo;
        DuracionSegundos = duracion;
        Pista = pista;
        EnBiblioteca = enBiblioteca;
    }

    public string RutaArchivo { get; }
    public string Artista { get; }
    public string Titulo { get; }
    public int DuracionSegundos { get; }
    public PistaRekordbox? Pista { get; }
    public bool EnBiblioteca { get; }

    public string Archivo => Path.GetFileName(RutaArchivo);
    public string Carpeta => Path.GetDirectoryName(RutaArchivo) ?? "";
    public string Duracion => DuracionSegundos > 0 ? TimeSpan.FromSeconds(DuracionSegundos).ToString(@"m\:ss", CultureInfo.InvariantCulture) : "";

    /// <summary>«3», «0» o «1 (automático)» para el único hot cue que deja el análisis en el primer tiempo.</summary>
    public string HotCues => Pista == null ? "" : Pista.SoloCueInicial ? "1 (automático)" : Pista.HotCues.ToString(CultureInfo.InvariantCulture);
    public int HotCuesOrden => Pista == null ? -1 : Pista.SoloCueInicial ? 0 : Pista.HotCues;
    public string Beatgrid => Pista == null ? "" : Pista.Beatgrid ? "Sí" : "No";
    public int Reproducciones => Pista?.Reproducciones ?? 0;
    public string Alta => Pista?.Alta?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "";
    public DateTime AltaFecha => Pista?.Alta ?? DateTime.MinValue;
    public string Valoracion => Pista is { Valoracion: > 0 } p ? new string('★', p.Valoracion) : "";
}

/// <summary>Una entrada de rekordbox cuyo archivo no existe, con lo encontrado para ella.</summary>
public sealed class FilaRota : IFilaConArchivo
{
    public FilaRota(PistaRota r) => R = r;

    public PistaRota R { get; }

    /// <summary>El archivo que sí existe (el encontrado), para escucharlo o abrir su carpeta. "" si no hay.</summary>
    public string RutaArchivo => R.NuevaRuta;

    public string Nombre => R.Pista.Nombre;
    public string Artista => R.Pista.Artista;
    public string RutaRekordbox => R.Pista.Ruta;
    public string Como => R.Texto;
    public string NuevaRuta => R.NuevaRuta;
    public bool Reparable => R.Reparable;
    public string HotCues => R.Pista.SoloCueInicial ? "1 (automático)" : R.Pista.HotCues.ToString(CultureInfo.InvariantCulture);
    public int HotCuesOrden => R.Pista.SoloCueInicial ? 0 : R.Pista.HotCues;
    public int Reproducciones => R.Pista.Reproducciones;
}

/// <summary>
/// Página rekordbox (equipo «rekordbox», tanda 4).
///
/// P3: cruza la colección exportada de rekordbox con la biblioteca y enseña cuatro listas: lo que no
/// está en rekordbox, las entradas rotas (con la reparación), lo que no está preparado y lo que nunca
/// se ha pinchado. Solo LEE el XML que exporta el usuario; nunca toca la base de datos de rekordbox
/// (master.db, cifrada en rekordbox 6 y 7) ni el XML original.
///
/// P11: exporta las Colecciones, las listas de Tendencias y Playlists, los órdenes para mezclar y las
/// pendientes conseguidas a un XML de rekordbox con una carpeta «BeatTag», para importarlas de una vez.
///
/// Ninguna de las dos cosas escribe en los archivos de música, así que la página no bloquea el resto de
/// la aplicación mientras trabaja.
/// </summary>
public partial class RekordboxViewModel : ViewModelBase, IEstadoPagina, IPaginaVisible
{
    private readonly AppEngine _engine;
    private readonly Func<IReadOnlyList<ListaParaRekordbox>> _listasAbiertas;
    private readonly RecalculoDiferido _recalculo;

    private ColeccionRekordbox? _coleccion;
    private ResultadoCruce? _cruce;
    private CancellationTokenSource? _ctsCruce;
    private CancellationTokenSource? _ctsExportar;
    private int _vuelta;
    private bool _intentoInicial;

    [ObservableProperty] private string _status = "";

    /// <summary>Leyendo la colección o cruzándola. No bloquea la aplicación (no se escribe en la música).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Libre))]
    private bool _isBusy;

    public bool Libre => !IsBusy;

    [ObservableProperty] private string _infoColeccion = "Ninguna colección abierta.";
    [ObservableProperty] private bool _hayColeccion;

    // --- Las cuatro tablas ---
    [ObservableProperty] private DataGridCollectionView _faltanView = new(new List<FilaRekordbox>());
    [ObservableProperty] private DataGridCollectionView _rotasView = new(new List<FilaRota>());
    [ObservableProperty] private DataGridCollectionView _sinPrepararView = new(new List<FilaRekordbox>());
    [ObservableProperty] private DataGridCollectionView _nuncaView = new(new List<FilaRekordbox>());

    [ObservableProperty] private string _tituloFaltan = "No están en rekordbox";
    [ObservableProperty] private string _tituloRotas = "Rutas rotas";
    [ObservableProperty] private string _tituloSinPreparar = "Sin preparar";
    [ObservableProperty] private string _tituloNunca = "Nunca pinchadas";

    [ObservableProperty] private string _resumenRotas = "";
    [ObservableProperty] private bool _hayReparables;

    // La fila elegida en cada tabla: sobre ella actúan las opciones del menú del botón derecho.
    [ObservableProperty] private FilaRekordbox? _faltaElegida;
    [ObservableProperty] private FilaRota? _rotaElegida;
    [ObservableProperty] private FilaRekordbox? _sinPrepararElegida;
    [ObservableProperty] private FilaRekordbox? _nuncaElegida;

    // --- Filtros de «sin preparar» y «nunca pinchadas» ---
    [ObservableProperty] private bool _sinHotCues = true;
    [ObservableProperty] private bool _sinBeatgrid = true;

    /// <summary>
    /// Contar como preparada una pista cuyo único marcador es el hot cue que deja el análisis en el
    /// primer tiempo. Desmarcado por defecto: ese cue lo pone rekordbox, no quien prepara la sesión.
    /// </summary>
    [ObservableProperty] private bool _cueInicialCuenta;

    /// <summary>Solo las canciones de la biblioteca de BeatTag (sin los samples de rekordbox ni otras carpetas).</summary>
    [ObservableProperty] private bool _soloBiblioteca = true;

    public IReadOnlyList<Opcion<int>> OpcionesMeses { get; } = new[]
    {
        new Opcion<int>(1, "1 mes"), new Opcion<int>(3, "3 meses"), new Opcion<int>(6, "6 meses"),
        new Opcion<int>(12, "1 año"), new Opcion<int>(24, "2 años"),
    };

    [ObservableProperty] private Opcion<int> _meses;

    // --- Exportar a rekordbox (P11) ---
    [ObservableProperty] private bool _exportarColecciones = true;
    [ObservableProperty] private bool _exportarListasAbiertas = true;
    [ObservableProperty] private bool _exportarSeguidas = true;
    [ObservableProperty] private bool _exportarPendientes = true;
    [ObservableProperty] private string _resumenExportacion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoExportando))]
    private bool _exportando;

    public bool NoExportando => !Exportando;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayUltimaExportacion))]
    [NotifyPropertyChangedFor(nameof(TextoActualizarExportacion))]
    private string _ultimaExportacion = "";

    public bool HayUltimaExportacion => UltimaExportacion.Length > 0;
    public string TextoActualizarExportacion => $"Actualizar «{Path.GetFileName(UltimaExportacion)}»";

    /// <param name="listasAbiertas">
    /// Las listas que hay abiertas en otras páginas (Tendencias, Playlists) y sus órdenes para mezclar,
    /// ya con las rutas de la biblioteca. Las da el shell, que es quien conoce esas páginas.
    /// </param>
    public RekordboxViewModel(AppEngine engine, Func<IReadOnlyList<ListaParaRekordbox>> listasAbiertas)
    {
        _engine = engine;
        _listasAbiertas = listasAbiertas;
        _meses = OpcionesMeses[2];
        _recalculo = new RecalculoDiferido(AlMostrarse);
        // Un escaneo, la papelera o un renombrado cambian lo que está y lo que no: el cruce se rehace
        // con la colección ya leída (sin volver a leer el XML), y solo con la página a la vista.
        _engine.Library.Changed += () => { if (_coleccion != null) _recalculo.Pedir(); };
        var ultima = _engine.Config.RekordboxExportacion ?? "";
        _ultimaExportacion = File.Exists(ultima) ? ultima : "";
        Status = "Exporta tu colección desde rekordbox (Archivo → Exportar colección en formato xml) y ábrela aquí.";
    }

    public bool Visible
    {
        get => _recalculo.Visible;
        set
        {
            var antes = _recalculo.Visible;
            if (value && !antes)
            {
                // Si se ha vuelto a exportar la colección desde rekordbox al mismo archivo, se lee la
                // nueva (y con ella se rehace el cruce, aunque la biblioteca también haya cambiado).
                var ruta = _engine.Config.RekordboxXml ?? "";
                if (_coleccion != null && !IsBusy && ruta.Length > 0 && File.Exists(ruta) && File.GetLastWriteTimeUtc(ruta) != _leidaEn)
                    _ = AbrirAsync(ruta);
            }
            _recalculo.CambiarVisible(value);
            // Lo que se exportaría depende de otras páginas (la lista abierta en Tendencias, las
            // colecciones): se recuenta cada vez que se vuelve aquí, sin leer nada de la red.
            if (value && !antes) ActualizarResumenExportacion();
        }
    }

    /// <summary>Fecha de modificación del XML cuando se leyó, para notar que se ha vuelto a exportar.</summary>
    private DateTime _leidaEn;

    /// <summary>Se está leyendo el XML: al terminar se cruza, así que no hace falta pedir otro cruce.</summary>
    private bool _leyendo;

    /// <summary>
    /// Al abrir la página: la primera vez se vuelve a leer la última colección abierta; después, si la
    /// biblioteca cambió, se rehace el cruce.
    /// </summary>
    private void AlMostrarse()
    {
        if (_coleccion != null) { if (!_leyendo) _ = CruzarAsync(); return; }
        if (_intentoInicial) return;
        _intentoInicial = true;
        var ruta = _engine.Config.RekordboxXml ?? "";
        if (ruta.Length > 0 && File.Exists(ruta)) _ = AbrirAsync(ruta);
    }

    // =============================================================================================
    // P3: leer y cruzar
    // =============================================================================================

    /// <summary>Lee la colección exportada y la cruza con la biblioteca.</summary>
    public async Task AbrirAsync(string ruta)
    {
        if (IsBusy) return;
        IsBusy = true;
        _leyendo = true;
        Status = $"Leyendo «{Path.GetFileName(ruta)}»…";
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var modificada = File.GetLastWriteTimeUtc(ruta);
            var coleccion = await Task.Run(() => LectorRekordbox.Leer(ruta));
            if (EsExportacionDeBeatTag(coleccion))
            {
                Status = $"«{Path.GetFileName(ruta)}» es una exportación de listas hecha por BeatTag, no la colección de rekordbox. "
                       + LectorRekordbox.ComoExportar;
                return;
            }
            _coleccion = coleccion;
            _leidaEn = modificada;
            AvisoNunca = AvisoReproducciones(coleccion);
            _engine.Config.RekordboxXml = ruta;
            _engine.SaveConfig();
            InfoColeccion = TextoColeccion(ruta, coleccion, File.GetLastWriteTime(ruta));
            HayColeccion = true;
            _engine.Logger.Detail($"rekordbox: leída «{ruta}» en {reloj.ElapsedMilliseconds} ms ({coleccion.Pistas.Count} pistas, {coleccion.Listas} listas).");
        }
        catch (RekordboxXmlException e)
        {
            Status = e.Message;
            _engine.Logger.Detail("rekordbox: " + e.Message);
            return;
        }
        catch (Exception e)
        {
            Status = "No se ha podido leer la colección: " + e.Message;
            _engine.Logger.Error("rekordbox: no se pudo leer la colección", e);
            return;
        }
        finally { IsBusy = false; _leyendo = false; }

        await CruzarAsync();
    }

    /// <summary>
    /// Lo que escribe «Llevar listas a rekordbox» también es un DJ_PLAYLISTS con colección, pero solo
    /// trae las canciones de esas listas: cruzarlo daría casi toda la biblioteca como «no está en
    /// rekordbox».
    /// </summary>
    internal static bool EsExportacionDeBeatTag(ColeccionRekordbox c)
        => c.Producto.StartsWith(ExportadorRekordbox.CarpetaRaiz + " ", StringComparison.Ordinal) || c.Producto == ExportadorRekordbox.CarpetaRaiz;

    /// <summary>«musica.xml» · rekordbox 7.2.17 · exportada el 15/08/2026 (hace 66 días) · 19.690 pistas».</summary>
    internal static string TextoColeccion(string ruta, ColeccionRekordbox c, DateTime exportada, DateTime? hoy = null)
    {
        var dias = (int)((hoy ?? DateTime.Today).Date - exportada.Date).TotalDays;
        var cuando = dias <= 0 ? "hoy" : dias == 1 ? "ayer" : $"hace {dias} días";
        var partes = new List<string> { $"«{Path.GetFileName(ruta)}»" };
        if (c.Producto.Length > 0) partes.Add(c.Producto);
        partes.Add($"exportada el {exportada.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} ({cuando})");
        partes.Add(Cuenta(c.Pistas.Count, "pista", "pistas"));
        if (c.SinArchivo > 0) partes.Add(Cuenta(c.SinArchivo, "pista de streaming", "pistas de streaming"));
        return string.Join(" · ", partes);
    }

    [RelayCommand]
    private Task VolverALeerAsync()
    {
        var ruta = _engine.Config.RekordboxXml ?? "";
        if (ruta.Length == 0 || !File.Exists(ruta)) { Status = "Abre antes la colección exportada de rekordbox."; return Task.CompletedTask; }
        return AbrirAsync(ruta);
    }

    /// <summary>
    /// Cruza la colección ya leída con la biblioteca tal como está ahora. Si se pide otro cruce mientras
    /// tanto (la biblioteca ha vuelto a cambiar), el anterior se cancela y solo vale el último.
    /// </summary>
    private async Task CruzarAsync()
    {
        var coleccion = _coleccion;
        if (coleccion == null) return;
        if (PendientesViewModel.MotivoSinBiblioteca(_engine.Library) is { Length: > 0 } motivo) { Status = motivo; return; }

        _ctsCruce?.Cancel();
        var cts = _ctsCruce = new CancellationTokenSource();
        var vuelta = ++_vuelta;
        // La instantánea, en el hilo de la interfaz: es el único que toca la colección de la biblioteca.
        var canciones = _engine.Library.Tracks.ToList();
        var undoDir = _engine.Paths.UndoDir;
        IsBusy = true;
        Status = "Cruzando la colección de rekordbox con la biblioteca…";
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var cruce = await Task.Run(() =>
            {
                var renombrados = RekordboxRelocator.ReadRenames(undoDir);
                return CruceRekordbox.Cruzar(coleccion, canciones, renombrados, new ArchivosEnDisco(), cts.Token);
            }, cts.Token);
            if (vuelta != _vuelta) return;
            _cruce = cruce;
            Mostrar(cruce);
            Status = TextoCruce(cruce);
            _engine.Logger.Sum($"rekordbox: cruce en {reloj.ElapsedMilliseconds} ms · {cruce.FaltanEnRekordbox.Count} no están en rekordbox · "
                             + $"{cruce.Rotas.Count} rotas ({cruce.Reparables} reparables) · {cruce.EnBiblioteca} en la biblioteca · {cruce.FueraDeBiblioteca} fuera de ella.");
        }
        catch (OperationCanceledException) { /* llega otro cruce detrás */ }
        catch (Exception e)
        {
            if (vuelta != _vuelta) return;
            Status = "No se ha podido cruzar la colección con la biblioteca: " + e.Message;
            _engine.Logger.Error("rekordbox: fallo al cruzar la colección", e);
        }
        finally
        {
            if (vuelta == _vuelta) IsBusy = false;
        }
    }

    private void Mostrar(ResultadoCruce cruce)
    {
        var faltan = cruce.FaltanEnRekordbox
            .Select(t => new FilaRekordbox(t.FilePath, t.Artist ?? "", t.Title ?? Path.GetFileNameWithoutExtension(t.FilePath), t.DurationSeconds, null, true))
            .ToList();
        FaltanView = VistaTabla.Nueva(faltan, FaltanView);
        TituloFaltan = $"No están en rekordbox ({faltan.Count:N0})";

        TituloRotas = $"Rutas rotas ({cruce.Rotas.Count:N0})";
        ResumenRotas = TextoRotas(cruce.Rotas);
        HayReparables = cruce.Reparables > 0;
        MostrarRotas();

        AplicarFiltros();
    }

    /// <summary>Por qué «Nunca pinchadas» puede no servir con esta colección, o "".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayAvisoNunca))]
    private string _avisoNunca = "";

    public bool HayAvisoNunca => AvisoNunca.Length > 0;

    /// <summary>
    /// Por debajo de una pista con reproducciones de cada cien, el contador de rekordbox no refleja lo
    /// que se pincha: quien pincha con otro programa, o con un USB cuyo historial no vuelve a rekordbox,
    /// lo tiene a cero en casi toda la colección, y «Nunca pinchadas» sería la colección entera.
    /// MEDIDO en una colección real: 11 de 19.690 pistas (0,06 %) tenían alguna reproducción.
    /// </summary>
    internal const double MinimoConReproducciones = 0.01;

    /// <summary>Con menos pistas no se saca ninguna conclusión del contador.</summary>
    internal const int MinimoParaAvisar = 100;

    internal static string AvisoReproducciones(ColeccionRekordbox c)
    {
        var total = c.Pistas.Count;
        if (total < MinimoParaAvisar) return "";
        var con = c.Pistas.Count(p => p.Reproducciones > 0);
        if (con >= total * MinimoConReproducciones) return "";
        var cuantas = con == 0
            ? "Ninguna pista de esta colección tiene reproducciones anotadas en rekordbox."
            : $"Solo {Cuenta(con, "pista", "pistas")} de {total.ToString("N0", CultureInfo.CurrentCulture)} "
              + (con == 1 ? "tiene reproducciones anotadas en rekordbox." : "tienen reproducciones anotadas en rekordbox.");
        return cuantas + " Si pinchas con otro programa, o con un USB cuyo historial no se importa en rekordbox, esta lista no indica lo que no has pinchado.";
    }

    /// <summary>Ver solo las entradas rotas que se pueden reparar (las demás se explican en el resumen).</summary>
    [ObservableProperty] private bool _soloReparables;

    partial void OnSoloReparablesChanged(bool value) => MostrarRotas();

    private void MostrarRotas()
    {
        if (_cruce == null) return;
        var rotas = _cruce.Rotas.Where(r => !SoloReparables || r.Reparable).Select(r => new FilaRota(r)).ToList();
        RotasView = VistaTabla.Nueva(rotas, RotasView);
    }

    partial void OnSinHotCuesChanged(bool value) => AplicarFiltros();
    partial void OnSinBeatgridChanged(bool value) => AplicarFiltros();
    partial void OnCueInicialCuentaChanged(bool value) => AplicarFiltros();
    partial void OnSoloBibliotecaChanged(bool value) => AplicarFiltros();
    partial void OnMesesChanged(Opcion<int> value) => AplicarFiltros();

    /// <summary>
    /// (c) y (d) con los filtros elegidos. Son comprobaciones sencillas sobre lo ya cruzado: con 20.000
    /// pistas se hacen al momento, sin salir del hilo de la interfaz.
    /// </summary>
    private void AplicarFiltros()
    {
        if (_cruce == null) return;
        var hoy = DateTime.Today;
        var meses = Meses?.Valor ?? 6;
        var base_ = _cruce.Presentes.Where(p => !SoloBiblioteca || p.Cancion != null).ToList();

        var sinPreparar = base_.Where(p => CruceRekordbox.SinPreparar(p.Pista, SinHotCues, SinBeatgrid, CueInicialCuenta)).Select(Fila).ToList();
        SinPrepararView = VistaTabla.Nueva(sinPreparar, SinPrepararView);
        TituloSinPreparar = $"Sin preparar ({sinPreparar.Count:N0})";

        var nunca = base_.Where(p => CruceRekordbox.NuncaPinchada(p.Pista, meses, hoy)).Select(Fila).ToList();
        NuncaView = VistaTabla.Nueva(nunca, NuncaView);
        TituloNunca = $"Nunca pinchadas ({nunca.Count:N0})";
    }

    private static FilaRekordbox Fila(PistaPresente p)
        => p.Cancion is { } t
            ? new FilaRekordbox(t.FilePath, t.Artist ?? p.Pista.Artista, t.Title ?? p.Pista.Nombre, t.DurationSeconds > 0 ? t.DurationSeconds : p.Pista.Duracion, p.Pista, true)
            : new FilaRekordbox(p.Pista.Ruta, p.Pista.Artista, p.Pista.Nombre, p.Pista.Duracion, p.Pista, false);

    /// <summary>Lo que se dice tras cruzar.</summary>
    internal static string TextoCruce(ResultadoCruce c)
    {
        var partes = new List<string>
        {
            c.FaltanEnRekordbox.Count == 0 ? "Todas las canciones de la biblioteca están en rekordbox"
            : c.FaltanEnRekordbox.Count == 1 ? "1 canción de la biblioteca no está en rekordbox"
            : $"{c.FaltanEnRekordbox.Count:N0} canciones de la biblioteca no están en rekordbox",
            c.Rotas.Count == 0 ? "ninguna entrada de rekordbox apunta a un archivo que no existe"
            : c.Rotas.Count == 1 ? "1 entrada de rekordbox apunta a un archivo que no existe"
            : $"{c.Rotas.Count:N0} entradas de rekordbox apuntan a archivos que no existen",
        };
        var texto = string.Join("; ", partes) + ".";
        if (c.FueraDeBiblioteca > 0)
            texto += c.FueraDeBiblioteca == 1
                ? " 1 pista de rekordbox está fuera de las carpetas de la biblioteca."
                : $" {c.FueraDeBiblioteca:N0} pistas de rekordbox están fuera de las carpetas de la biblioteca.";
        return texto;
    }

    /// <summary>«3.375 de 4.343 se pueden reparar: 623 renombradas o movidas por BeatTag, 19 por nombre y tamaño…».</summary>
    internal static string TextoRotas(IReadOnlyList<PistaRota> rotas)
    {
        if (rotas.Count == 0) return "Ninguna entrada de la colección apunta a un archivo que no existe.";
        var reparables = rotas.Count(r => r.Reparable);
        var detalle = new List<string>();
        void Sumar(ComoReparar como, string singular, string plural)
        {
            var n = rotas.Count(r => r.Como == como);
            if (n > 0) detalle.Add(n == 1 ? "1 " + singular : $"{n:N0} {plural}");
        }
        Sumar(ComoReparar.RenombradaPorBeatTag, "renombrada o movida por BeatTag", "renombradas o movidas por BeatTag");
        Sumar(ComoReparar.MismoNombreYTamano, "encontrada por nombre y tamaño", "encontradas por nombre y tamaño");
        Sumar(ComoReparar.MismoNombreYDuracion, "encontrada por nombre y duración", "encontradas por nombre y duración");

        var resto = new List<string>();
        void Contar(ComoReparar como, string singular, string plural)
        {
            var n = rotas.Count(r => r.Como == como);
            if (n > 0) resto.Add(n == 1 ? "1 " + singular : $"{n:N0} {plural}");
        }
        Contar(ComoReparar.YaEnRekordbox, "ya está en rekordbox con la ruta nueva", "ya están en rekordbox con la ruta nueva");
        Contar(ComoReparar.VariasCandidatas, "tiene varios archivos posibles", "tienen varios archivos posibles");
        Contar(ComoReparar.MismoDestino, "comparte archivo con otra entrada", "comparten archivo con otras entradas");
        Contar(ComoReparar.DiscoNoConectado, "está en un disco no conectado", "están en discos no conectados");
        Contar(ComoReparar.SinEncontrar, "no se encuentra en la biblioteca", "no se encuentran en la biblioteca");

        if (reparables == 0)
            return (rotas.Count == 1 ? "La entrada rota no se puede reparar desde aquí: " : $"Ninguna de las {rotas.Count:N0} entradas rotas se puede reparar desde aquí: ")
                 + string.Join(", ", resto) + ".";
        var texto = $"Se pueden reparar {reparables:N0} de {rotas.Count:N0}: {string.Join(", ", detalle)}.";
        if (resto.Count > 0) texto += " De las demás, " + string.Join(", ", resto) + ".";
        return texto;
    }

    // --- Exportar a M3U8 (a, c, d) ---

    /// <summary>Qué tabla se guarda como lista.</summary>
    public enum Tabla { Faltan, SinPreparar, Nunca }

    /// <summary>Las filas de una tabla en el orden en que se ven.</summary>
    public IReadOnlyList<FilaRekordbox> Filas(Tabla t) => (t switch
    {
        Tabla.Faltan => FaltanView,
        Tabla.SinPreparar => SinPrepararView,
        _ => NuncaView,
    }).Cast<FilaRekordbox>().ToList();

    public string NombreSugerido(Tabla t) => t switch
    {
        Tabla.Faltan => $"No están en rekordbox {DateTime.Now:yyyy-MM-dd}",
        Tabla.SinPreparar => $"Sin preparar {DateTime.Now:yyyy-MM-dd}",
        _ => $"Nunca pinchadas {DateTime.Now:yyyy-MM-dd}",
    };

    /// <summary>Guarda la tabla como M3U8, en el orden en que se ve. "" si fue bien, o el motivo.</summary>
    public string ExportarM3u(Tabla t, string destino)
    {
        var filas = Filas(t);
        if (filas.Count == 0) { Status = "La tabla está vacía: no hay nada que guardar."; return "vacio"; }
        var err = PlaylistWriter.Write(destino, filas.Select(f => new PlaylistItem(f.RutaArchivo, f.Artista, f.Titulo, f.DuracionSegundos)));
        Status = err.Length > 0
            ? "No se pudo guardar la lista: " + err
            : $"Lista guardada con {Cuenta(filas.Count, "canción", "canciones")}: {Path.GetFileName(destino)}."
              + (t == Tabla.Faltan ? " En rekordbox, impórtala como lista de reproducción (Archivo → Importar → Importar lista de reproducción) para añadir esas canciones a la colección." : "");
        return err;
    }

    // --- Menú de las tablas ---

    /// <summary>Escucha (o para) el archivo de la fila. En las rutas rotas, el archivo encontrado.</summary>
    public void Escuchar(IFilaConArchivo? fila)
    {
        var ruta = fila?.RutaArchivo ?? "";
        if (ruta.Length == 0) { Status = fila is FilaRota ? "No se ha encontrado ningún archivo para esa entrada." : "Elige antes una canción de la tabla."; return; }
        try { _engine.Preview.Toggle(ruta); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    /// <summary>Abre la carpeta del archivo de la fila. En las rutas rotas, la del archivo encontrado.</summary>
    public async Task AbrirCarpetaAsync(IFilaConArchivo? fila)
    {
        var ruta = fila?.RutaArchivo ?? "";
        if (ruta.Length == 0) { Status = fila is FilaRota ? "No se ha encontrado ningún archivo para esa entrada." : "Elige antes una canción de la tabla."; return; }
        await Shell.OpenContainingFolderAsync(ruta);
    }

    // --- Reparar (b) ---

    public int Reparables => _cruce?.Reparables ?? 0;

    /// <summary>
    /// Escribe una copia de la colección con las rutas reparadas y una lista «Rutas reparadas por
    /// BeatTag». Ni el XML exportado ni la base de datos de rekordbox se tocan.
    /// </summary>
    public async Task RepararAsync(string destino)
    {
        var origen = _engine.Config.RekordboxXml ?? "";
        if (_cruce == null || origen.Length == 0) { Status = "Abre antes la colección exportada de rekordbox."; return; }
        var mapa = CruceRekordbox.MapaReparacion(_cruce.Rotas);
        if (mapa.Count == 0) { Status = "No hay ninguna entrada rota que se pueda reparar."; return; }
        if (IsBusy) return;
        IsBusy = true;
        Status = "Escribiendo la colección reparada…";
        // El título de cada archivo nuevo, de la biblioteca (ver RekordboxRelocator.Repair).
        var titulos = new Dictionary<string, string>(ComparadorRutas.Instancia);
        foreach (var t in _engine.Library.Tracks)
            if (!string.IsNullOrWhiteSpace(t.Title)) titulos.TryAdd(t.FilePath, t.Title!);
        try
        {
            var r = await Task.Run(() => RekordboxRelocator.Repair(origen, destino, mapa, conLista: true,
                                                                   tituloDe: ruta => titulos.GetValueOrDefault(ruta)));
            if (!r.Ok) { Status = "No se pudo reparar: " + r.Error; return; }
            Status = TextoReparada(r.Reparadas, Path.GetFileName(destino));
            _engine.Logger.Sum($"rekordbox: colección reparada en «{destino}»: {r.Reparadas} reparadas · {r.SinCambio} sin cambio · {r.NoEncontradas} no encontradas.");
        }
        catch (Exception e) { Status = "No se pudo reparar: " + e.Message; }
        finally { IsBusy = false; }
    }

    internal static string TextoReparada(int reparadas, string archivo)
        => reparadas == 0
            ? "No había ninguna ruta que reparar: los archivos ya están en su sitio o ya no existen."
            : (reparadas == 1 ? $"1 ruta reparada en «{archivo}». " : $"{reparadas:N0} rutas reparadas en «{archivo}». ")
              + "Para llevarla a rekordbox: en Preferencias → Avanzado → rekordbox xml, elige ese archivo; después, en el apartado "
              + $"«rekordbox xml» de la izquierda, abre la lista «{RekordboxRelocator.ListaReparadas}», selecciona sus pistas e impórtalas a la colección. "
              + "Llegan con sus cue points, su beatgrid, su valoración y sus comentarios. Las entradas antiguas siguen en la colección como "
              + "archivos no encontrados: cuando compruebes que las reparadas están bien, puedes quitarlas.";

    // =============================================================================================
    // P11: exportar las listas de BeatTag
    // =============================================================================================

    partial void OnExportarColeccionesChanged(bool value) => ActualizarResumenExportacion();
    partial void OnExportarListasAbiertasChanged(bool value) => ActualizarResumenExportacion();
    partial void OnExportarSeguidasChanged(bool value) => ActualizarResumenExportacion();
    partial void OnExportarPendientesChanged(bool value) => ActualizarResumenExportacion();

    /// <summary>Qué se va a exportar con las casillas marcadas, contado sin leer nada de la red.</summary>
    public void ActualizarResumenExportacion()
    {
        var colecciones = ExportarColecciones ? _engine.Colecciones.Todas.Count(FiltroColeccion.TieneCriterios) : 0;
        var abiertas = ExportarListasAbiertas ? _listasAbiertas().Count(l => l.Rutas.Count > 0) : 0;
        var seguidas = ExportarSeguidas ? SeguimientoListas.De(_engine).Seguidas.Todas().Count : 0;
        var conseguidas = ExportarPendientes ? SeguimientoListas.De(_engine).Pendientes.Todas().Count(p => p.EstaConseguida) : 0;
        ResumenExportacion = TextoResumenExportacion(colecciones, abiertas, seguidas, conseguidas);
    }

    internal static string TextoResumenExportacion(int colecciones, int abiertas, int seguidas, int conseguidas)
    {
        var partes = new List<string>();
        if (colecciones > 0) partes.Add(Cuenta(colecciones, "colección", "colecciones"));
        // Un orden para mezclar también es una lista para rekordbox: se cuentan juntos.
        if (abiertas > 0) partes.Add(abiertas == 1 ? "1 lista abierta en Tendencias, Playlists o el Asistente" : $"{abiertas} listas abiertas en Tendencias, Playlists o el Asistente");
        if (seguidas > 0) partes.Add(seguidas == 1 ? "1 lista que sigues (se vuelve a leer de internet)" : $"{seguidas} listas que sigues (se vuelven a leer de internet)");
        if (conseguidas > 0) partes.Add(conseguidas == 1 ? "la canción pendiente ya conseguida" : $"las {conseguidas} canciones pendientes ya conseguidas");
        if (partes.Count == 0) return "Con lo marcado no hay nada que exportar.";
        var lista = partes.Count == 1 ? partes[0] : string.Join(", ", partes.Take(partes.Count - 1)) + " y " + partes[^1];
        return "Se exportará: " + lista + ".";
    }

    [RelayCommand]
    private void CancelarExportacion() => _ctsExportar?.Cancel();

    /// <summary>Vuelve a exportar al mismo archivo, el que rekordbox tiene configurado.</summary>
    public Task ActualizarExportacionAsync()
        => UltimaExportacion.Length == 0 ? Task.CompletedTask : ExportarAsync(UltimaExportacion);

    /// <summary>Reúne las listas marcadas y escribe el XML para rekordbox.</summary>
    public async Task ExportarAsync(string destino)
    {
        if (Exportando) return;
        if (PendientesViewModel.MotivoSinBiblioteca(_engine.Library) is { Length: > 0 } motivo) { Status = motivo; return; }
        if (MotivoDestinoNoValido(destino, _engine.Config.RekordboxXml ?? "") is { Length: > 0 } malo) { Status = malo; return; }
        Exportando = true;
        _ctsExportar = new CancellationTokenSource();
        var ct = _ctsExportar.Token;
        var errores = new List<string>();
        try
        {
            var canciones = _engine.Library.Tracks.ToList();
            var listas = new List<ListaParaRekordbox>();

            if (ExportarColecciones)
            {
                Status = "Preparando las colecciones…";
                var colecciones = _engine.Colecciones.Todas.Where(FiltroColeccion.TieneCriterios).Select(c => c.Copia(c.Nombre)).ToList();
                var fichas = FichasActuales();
                listas.AddRange(await Task.Run(() => ListasDeColecciones(colecciones, canciones, fichas), ct));
            }
            if (ExportarSeguidas)
                listas.AddRange(await LeerSeguidasAsync(canciones, errores, ct));
            if (ExportarListasAbiertas)
                listas.AddRange(_listasAbiertas());
            if (ExportarPendientes)
                listas.AddRange(PendientesConseguidas(SeguimientoListas.De(_engine).Pendientes.Todas(), canciones));

            ct.ThrowIfCancellationRequested();
            var porRuta = new Dictionary<string, Track>(ComparadorRutas.Instancia);
            foreach (var t in canciones) porRuta.TryAdd(t.FilePath, t);
            listas = SinRepetidas(SoloDeLaBiblioteca(listas, porRuta));
            var locations = LocationsConocidas(porRuta);

            Status = "Escribiendo el XML para rekordbox…";
            var r = await Task.Run(() => ExportadorRekordbox.Escribir(destino, listas, ruta => DatosDe(porRuta, ruta), locations), ct);
            if (!r.Ok) { Status = "No se ha exportado: " + r.Error + Errores(errores); return; }

            _engine.Config.RekordboxExportacion = destino;
            _engine.SaveConfig();
            UltimaExportacion = destino;
            Status = TextoExportado(r, Path.GetFileName(destino)) + Errores(errores);
            _engine.Logger.Sum($"rekordbox: exportadas {r.Listas} listas ({r.Pistas} canciones) a «{destino}»" + (errores.Count > 0 ? $"; {errores.Count} listas seguidas sin leer" : "") + ".");
            foreach (var e in errores) _engine.Logger.Detail("rekordbox: " + e);
        }
        catch (OperationCanceledException) { Status = "Exportación cancelada. No se ha escrito nada."; }
        catch (Exception e)
        {
            Status = "No se ha exportado: " + e.Message;
            _engine.Logger.Error("rekordbox: fallo al exportar las listas", e);
        }
        finally
        {
            Exportando = false;
            _ctsExportar?.Dispose();
            _ctsExportar = null;
        }
    }

    /// <summary>
    /// Cada lista, solo con los archivos que siguen en la biblioteca. La lista abierta en Tendencias o
    /// el orden del Asistente se cruzaron cuando se abrieron: si desde entonces se envió un archivo a
    /// la papelera o se renombró, en rekordbox entraría como una pista perdida.
    /// </summary>
    internal static List<ListaParaRekordbox> SoloDeLaBiblioteca(IEnumerable<ListaParaRekordbox> listas, IReadOnlyDictionary<string, Track> porRuta)
        => listas.Select(l => l with { Rutas = l.Rutas.Where(porRuta.ContainsKey).ToList() }).ToList();

    /// <summary>
    /// La colección exportada de rekordbox que se ha abierto aquí no se sustituye nunca por las listas:
    /// se perdería la base del cruce y de la reparación. "" si el destino vale.
    /// </summary>
    internal static string MotivoDestinoNoValido(string destino, string coleccionAbierta)
        => coleccionAbierta.Length > 0 && ComparadorRutas.Instancia.Equals(Path.GetFullPath(destino), Path.GetFullPath(coleccionAbierta))
            ? "Ese archivo es la colección exportada de rekordbox que se ha abierto aquí: elige otro nombre para no sustituirla."
            : "";

    /// <summary>
    /// La lista abierta en Tendencias o Playlists (las canciones que están en la biblioteca, en el
    /// orden de la lista) y, si se ordenó para mezclar, ese orden como otra lista. El nombre, sin la
    /// fecha que lleva el del M3U8: rekordbox ve la exportación siempre en el mismo archivo y una
    /// lista con la fecha en el nombre cambiaría de nombre cada día.
    /// </summary>
    internal static IEnumerable<ListaParaRekordbox> DeListaAbierta(string nombreSugerido, IReadOnlyList<TrendRow> filas,
                                                                   IReadOnlyList<string> ordenMezcla)
    {
        var nombre = SinFecha(nombreSugerido);
        var rutas = filas.Where(f => f.Tengo).OrderBy(f => f.Position).Select(f => f.FilePath).ToList();
        if (rutas.Count > 0) yield return new ListaParaRekordbox(CarpetaListas, nombre, rutas);
        if (ordenMezcla.Count > 0) yield return new ListaParaRekordbox(CarpetaOrdenes, nombre, ordenMezcla);
    }

    /// <summary>Subcarpetas de «BeatTag» en rekordbox.</summary>
    internal const string CarpetaColecciones = "Colecciones", CarpetaListas = "Listas", CarpetaOrdenes = "Órdenes para mezclar";

    /// <summary>«Tendencias Spain 2026-10-20» → «Tendencias Spain».</summary>
    internal static string SinFecha(string nombre)
        => System.Text.RegularExpressions.Regex.Replace(nombre ?? "", @"\s+\d{4}-\d{2}-\d{2}\s*$", "").Trim();

    private static string Errores(IReadOnlyList<string> errores)
        => errores.Count == 0 ? ""
         : errores.Count == 1 ? " No se pudo leer una de las listas que sigues: " + errores[0]
         : $" No se pudieron leer {errores.Count} de las listas que sigues; el detalle está en el registro.";

    internal static string TextoExportado(ResultadoExportacionRekordbox r, string archivo)
        => (r.Listas == 1 ? "1 lista exportada" : $"{r.Listas:N0} listas exportadas")
         + $" con {Cuenta(r.Pistas, "canción", "canciones")} a «{archivo}»"
         + (r.Vacias == 0 ? "." : r.Vacias == 1 ? " (1 lista sin canciones de la biblioteca no se incluye)." : $" ({r.Vacias} listas sin canciones de la biblioteca no se incluyen).")
         + " En rekordbox, abre el apartado «rekordbox xml», haz clic derecho en la carpeta «BeatTag» e impórtala.";

    private Dictionary<string, FichaDj> FichasActuales()
    {
        var fichas = new Dictionary<string, FichaDj>(StringComparer.OrdinalIgnoreCase);
        foreach (var ruta in _engine.Fichas.Rutas)
            if (_engine.Fichas.Obtener(ruta) is { } f) fichas[ruta] = f;
        return fichas;
    }

    /// <summary>La Location que ya tiene cada archivo en rekordbox, si se ha abierto su colección.</summary>
    private Dictionary<string, string>? LocationsConocidas(IReadOnlyDictionary<string, Track> porRuta)
        => _coleccion == null ? null : LocationsDeRekordbox(_coleccion.Pistas, porRuta);

    /// <summary>
    /// La Location de cada archivo tal como la tiene rekordbox. Si tiene dos entradas del mismo archivo
    /// que solo se diferencian en mayúsculas, se elige la que se escribe como el archivo de hoy.
    ///
    /// MEDIDO en la colección real (19.690 pistas): rekordbox distingue mayúsculas en la Location y
    /// tenía 8 archivos con dos entradas, «…(No More Love On the Run).mp3» y «…(No More Love on the
    /// Run).mp3», la de antes y la de después de corregir el nombre. Quedándose con la primera, la
    /// lista podía apuntar a la entrada vieja.
    /// </summary>
    internal static Dictionary<string, string> LocationsDeRekordbox(IEnumerable<PistaRekordbox> pistas, IReadOnlyDictionary<string, Track> porRuta)
    {
        var d = new Dictionary<string, (string Ruta, string Location)>(ComparadorRutas.Instancia);
        foreach (var p in pistas)
        {
            if (!d.TryGetValue(p.Ruta, out var ya)) { d[p.Ruta] = (p.Ruta, p.Location); continue; }
            var hoy = porRuta.TryGetValue(p.Ruta, out var t) ? t.FilePath : "";
            if (!string.Equals(ya.Ruta, hoy, StringComparison.Ordinal) && string.Equals(p.Ruta, hoy, StringComparison.Ordinal))
                d[p.Ruta] = (p.Ruta, p.Location);
        }
        return d.ToDictionary(kv => kv.Key, kv => kv.Value.Location, ComparadorRutas.Instancia);
    }

    internal static DatosPistaRekordbox DatosDe(IReadOnlyDictionary<string, Track> porRuta, string ruta)
        => porRuta.TryGetValue(ruta, out var t)
            ? new DatosPistaRekordbox(t.Title ?? "", t.Artist ?? "")
            : new DatosPistaRekordbox("", "");

    /// <summary>
    /// Cada colección con sus canciones, en el mismo orden que en la página Colecciones: por BPM (las
    /// que no lo tienen, al final), energía y artista.
    /// </summary>
    internal static List<ListaParaRekordbox> ListasDeColecciones(IReadOnlyList<ColeccionInteligente> colecciones,
        IReadOnlyList<Track> canciones, IReadOnlyDictionary<string, FichaDj> fichas)
    {
        var listas = new List<ListaParaRekordbox>();
        foreach (var c in colecciones)
        {
            var rutas = canciones
                .Select(t => (t, f: fichas.GetValueOrDefault(t.FilePath)))
                .Where(x => FiltroColeccion.Cumple(c, x.t, x.f))
                .OrderBy(x => x.t.Bpm == 0 ? uint.MaxValue : x.t.Bpm)
                .ThenBy(x => x.f?.Energia ?? 0)
                .ThenBy(x => x.t.Artist, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.t.FilePath)
                .ToList();
            listas.Add(new ListaParaRekordbox(CarpetaColecciones, c.Nombre, rutas));
        }
        return listas;
    }

    /// <summary>Las pendientes ya conseguidas cuyo archivo sigue en la biblioteca, de la más reciente a la más antigua.</summary>
    internal static List<ListaParaRekordbox> PendientesConseguidas(IReadOnlyList<Pendiente> pendientes, IReadOnlyList<Track> canciones)
    {
        var enBiblioteca = new HashSet<string>(canciones.Select(t => t.FilePath), ComparadorRutas.Instancia);
        var rutas = pendientes.Where(p => p.EstaConseguida && p.Archivo.Length > 0 && enBiblioteca.Contains(p.Archivo))
                              .OrderByDescending(p => p.Conseguida)
                              .Select(p => p.Archivo)
                              .ToList();
        return new List<ListaParaRekordbox> { new("", "Pendientes conseguidas", rutas) };
    }

    /// <summary>
    /// Sin listas repetidas: la lista que se está viendo en Tendencias suele ser también una lista que
    /// se sigue, y saldría dos veces con dos nombres. Se queda la primera (la seguida, con su nombre).
    /// </summary>
    internal static List<ListaParaRekordbox> SinRepetidas(IEnumerable<ListaParaRekordbox> listas)
    {
        var res = new List<ListaParaRekordbox>();
        foreach (var l in listas)
        {
            if (l.Rutas.Count > 0 && res.Any(o => o.Rutas.Count == l.Rutas.Count && o.Rutas.SequenceEqual(l.Rutas, ComparadorRutas.Instancia)))
                continue;
            res.Add(l);
        }
        return res;
    }

    /// <summary>
    /// Vuelve a leer las listas seguidas (charts y playlists) y se queda con sus canciones que están en
    /// la biblioteca, en el orden de la lista. Solo lee: no apunta la lectura como revisión (eso lo hace
    /// «Actualizar todas» en Pendientes). Una lista que no se puede leer no impide exportar las demás.
    /// </summary>
    private async Task<List<ListaParaRekordbox>> LeerSeguidasAsync(IReadOnlyList<Track> canciones, List<string> errores, CancellationToken ct)
    {
        var res = new List<ListaParaRekordbox>();
        var seguidas = SeguimientoListas.De(_engine).Seguidas.Todas();
        if (seguidas.Count == 0) return res;
        var indice = (await IndiceBibliotecaCompartido.De(_engine.Library).ObtenerAsync(canciones, ct)).Indice;
        for (var i = 0; i < seguidas.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var l = seguidas[i];
            Status = $"Leyendo «{l.Nombre}» ({i + 1} de {seguidas.Count})…";
            try
            {
                IReadOnlyList<ChartTrack> pistas;
                if (l.Tipo == TipoListaSeguida.Playlist)
                {
                    var p = await _engine.Playlists.LeerAsync(l.Enlace, ct);
                    if (!p.Ok) { errores.Add($"«{l.Nombre}»: {p.Error}"); continue; }
                    pistas = p.Pistas;
                }
                else
                {
                    var fuente = Enum.TryParse<ChartSource>(l.Fuente, out var f) ? f : ChartSource.Spotify;
                    pistas = await _engine.Charts.GetChartAsync(fuente, new ChartCountry(l.Pais, l.ListaPais, l.CodigoPais), l.Tamano > 0 ? l.Tamano : 50, ct);
                    if (pistas.Count == 0) { errores.Add($"«{l.Nombre}»: no se ha podido descargar el chart."); continue; }
                }
                var rutas = await Task.Run(() => pistas.Select(t => indice.Buscar(t.Artist, t.Title)).Where(r => r.Length > 0).ToList(), ct);
                res.Add(new ListaParaRekordbox(CarpetaListas, l.Nombre, rutas));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { errores.Add($"«{l.Nombre}»: {e.Message}"); }
        }
        return res;
    }

    private static string Cuenta(int n, string singular, string plural)
        => n == 1 ? "1 " + singular : $"{n.ToString("N0", CultureInfo.CurrentCulture)} {plural}";
}
