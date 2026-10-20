using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.Services;

/// <summary>
/// Pendientes y listas seguidas: lo comparten Tendencias, Playlists y la página Pendientes.
///
/// Es uno por motor, igual que el índice de la biblioteca es uno por biblioteca: las tres páginas
/// lo piden con <see cref="De"/> y ven los mismos datos. Nace con la aplicación (lo crea la página
/// Pendientes al construirse el shell) para que las pendientes se tachen al escanear aunque nadie
/// abra esa página.
/// </summary>
public sealed class SeguimientoListas
{
    private static readonly ConditionalWeakTable<AppEngine, SeguimientoListas> PorMotor = new();

    public static SeguimientoListas De(AppEngine engine) => PorMotor.GetValue(engine, e => new SeguimientoListas(e));

    private readonly AppEngine _engine;

    public AlmacenPendientes Pendientes { get; }
    public AlmacenListasSeguidas Seguidas { get; }

    /// <summary>Cambiaron las pendientes (altas, tachadas, quitadas). Siempre en el hilo de la interfaz.</summary>
    public event Action? PendientesCambiadas;

    /// <summary>Cambiaron las listas seguidas o sus novedades. Siempre en el hilo de la interfaz.</summary>
    public event Action? SeguidasCambiadas;

    private SeguimientoListas(AppEngine engine)
    {
        _engine = engine;
        Pendientes = new AlmacenPendientes(Path.Combine(engine.Paths.DataDir, AlmacenPendientes.NombreArchivo), IndiceBiblioteca.ClavesDe);
        Seguidas = new AlmacenListasSeguidas(Path.Combine(engine.Paths.DataDir, AlmacenListasSeguidas.NombreArchivo));
        AvisarCarga("pendientes", Pendientes.AvisoCarga);
        AvisarCarga("listas-seguidas", Seguidas.AvisoCarga);
        engine.Logger.Detail($"Pendientes: {Pendientes.Count} · listas seguidas: {Seguidas.Count}");

        // Escanear, pasar canciones desde la Bandeja, renombrar… todo acaba en un cambio de la
        // biblioteca, y ahí es donde una pendiente puede haberse conseguido.
        engine.Library.Changed += () => _ = SinEsperar.Lanzar(TacharAsync,
            e => _engine.Logger.Error("Pendientes: no se pudieron cruzar con la biblioteca", e));
    }

    private void AvisarCarga(string clave, string aviso)
    {
        if (aviso.Length == 0) return;
        _engine.Logger.Err(aviso);
        _engine.Avisos.Poner("carga:" + clave, aviso);
    }

    /// <summary>Anota en el registro y en los avisos un guardado que falla, y quita el aviso si ahora va bien.</summary>
    private string Registrar(string que, string error)
    {
        if (error.Length == 0) { _engine.Avisos.Quitar("guardar:" + que); return error; }
        _engine.Logger.Err($"No se pudo guardar {que}: {error}");
        _engine.Avisos.Poner("guardar:" + que, $"No se ha podido guardar {que}: {error}");
        return error;
    }

    private string GuardarPendientes() => Registrar("la lista de pendientes", Pendientes.Guardar());
    private string GuardarSeguidas() => Registrar("las listas seguidas", Seguidas.Guardar());

    // --- Pendientes ---

    /// <summary>Lo que dio una operación sobre pendientes, con el error de guardado si lo hubo.</summary>
    public sealed record Resultado(int Hechas, int Otras, string Error);

    /// <summary>Añade a pendientes y guarda. <see cref="Resultado.Otras"/> son las que ya estaban.</summary>
    public Resultado AnadirPendientes(IEnumerable<NuevaPendiente> nuevas)
    {
        var r = Pendientes.Anadir(nuevas, DateTime.Now);
        var error = r.Anadidas > 0 ? GuardarPendientes() : "";
        if (r.Anadidas > 0)
        {
            _engine.Logger.Detail($"Pendientes: {r.Anadidas} añadidas" + (r.YaEstaban > 0 ? $" ({r.YaEstaban} ya estaban)" : "") + ".");
            PendientesCambiadas?.Invoke();
        }
        return new Resultado(r.Anadidas, r.YaEstaban, error);
    }

    public Resultado QuitarPendientes(IEnumerable<string> ids)
    {
        var n = Pendientes.Quitar(ids);
        var error = n > 0 ? GuardarPendientes() : "";
        if (n > 0) PendientesCambiadas?.Invoke();
        return new Resultado(n, 0, error);
    }

    public Resultado QuitarConseguidas()
    {
        var n = Pendientes.QuitarConseguidas();
        var error = n > 0 ? GuardarPendientes() : "";
        if (n > 0) PendientesCambiadas?.Invoke();
        return new Resultado(n, 0, error);
    }

    public Resultado VolverAPendiente(IEnumerable<string> ids)
    {
        var n = ids.Count(Pendientes.VolverAPendiente);
        var error = n > 0 ? GuardarPendientes() : "";
        if (n > 0) PendientesCambiadas?.Invoke();
        return new Resultado(n, 0, error);
    }

    /// <summary>
    /// Deshace el último «Volver a pendiente» de cada una (<see cref="AlmacenPendientes.RecuperarDescartado"/>).
    /// <see cref="Resultado.Hechas"/> son las que tenían un archivo descartado; <see cref="Resultado.Otras"/>,
    /// las que se han vuelto a tachar con él porque sigue en la biblioteca. Mirar la biblioteca va fuera
    /// del hilo de la interfaz.
    /// </summary>
    public async Task<Resultado> RecuperarDescartadosAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return new Resultado(0, 0, "");
        var canciones = _engine.Library.Tracks.ToList();   // instantánea en el hilo de la interfaz
        var rutas = await Task.Run(() => new HashSet<string>(canciones.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase));

        int hechas = 0, tachadas = 0;
        foreach (var id in ids)
        {
            var (ruta, tachada) = Pendientes.RecuperarDescartado(id, rutas.Contains);
            if (ruta.Length == 0) continue;
            hechas++;
            if (tachada) tachadas++;
        }
        if (hechas == 0) return new Resultado(0, 0, "");
        var error = GuardarPendientes();
        _engine.Logger.Detail($"Pendientes: {hechas} archivos descartados vuelven a contar ({tachadas} tachadas otra vez).");
        PendientesCambiadas?.Invoke();
        // Las que no se han vuelto a tachar puede tacharlas ahora otra copia, o un descartado anterior.
        _ = SinEsperar.Lanzar(TacharAsync, e => _engine.Logger.Error("Pendientes: no se pudieron cruzar con la biblioteca", e));
        return new Resultado(hechas, tachadas, error);
    }

    /// <summary>
    /// Una lista que se siguió con su enlace corto pasa a la clave del enlace largo (ver
    /// <see cref="AlmacenListasSeguidas.CambiarClave"/>), y se guarda. Devuelve si ha cambiado algo.
    /// </summary>
    public bool ReconocerClave(string antigua, string nueva, string enlace)
    {
        if (!Seguidas.CambiarClave(antigua, nueva, enlace)) return false;
        GuardarSeguidas();
        _engine.Logger.Detail($"Listas seguidas: «{antigua}» pasa a identificarse como «{nueva}».");
        SeguidasCambiadas?.Invoke();
        return true;
    }

    private bool _tachando;
    private bool _otraVez;

    /// <summary>Cuántas veces se ha cruzado con la biblioteca (para el registro y las pruebas manuales).</summary>
    public int Cruces { get; private set; }

    /// <summary>
    /// Tacha las pendientes que ya están en la biblioteca. Se llama en el hilo de la interfaz, tras
    /// cada cambio de la biblioteca.
    ///
    /// Lo caro va fuera: el índice es el de Tendencias y Playlists (<see cref="IndiceBibliotecaCompartido"/>),
    /// que solo se rehace si la biblioteca ha cambiado de verdad, y la búsqueda de cada pendiente
    /// también va en Task.Run. Sin pendientes que buscar no se toca el índice. Si llega otro cambio
    /// mientras se cruza, se atiende al terminar, una sola vez y con la biblioteca más reciente.
    /// </summary>
    public async Task TacharAsync()
    {
        if (_tachando) { _otraVez = true; return; }
        _tachando = true;
        try
        {
            do
            {
                _otraVez = false;
                await TacharUnaVezAsync();
            } while (_otraVez);
        }
        finally { _tachando = false; }
    }

    private async Task TacharUnaVezAsync()
    {
        if (!_engine.Library.IsScanned) return;
        var lista = Pendientes.Todas();
        if (lista.Count == 0) return;

        // La instantánea, en el hilo de la interfaz: es el único que toca la colección.
        var canciones = _engine.Library.Tracks.ToList();
        var rutas = await Task.Run(() => new HashSet<string>(canciones.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase));

        // Nada que buscar: todas conseguidas y con su archivo en su sitio.
        if (!lista.Any(p => !p.EstaConseguida || (p.Archivo.Length > 0 && !rutas.Contains(p.Archivo)))) return;

        // Antes de cruzar, los descartes siguen a los archivos que BeatTag ha renombrado: si no, el
        // remix descartado volvería a tachar la canción con su nombre nuevo.
        var descartesMovidos = await SeguirDescartadosAsync(lista, rutas);
        if (descartesMovidos > 0) lista = Pendientes.Todas();

        var indice = (await IndiceBibliotecaCompartido.De(_engine.Library).ObtenerAsync(canciones)).Indice;
        // Mirar si existe el archivo de una conseguida que ha salido de la biblioteca toca el disco:
        // va dentro del Task.Run, y solo para esas, que son pocas.
        var cambios = await Task.Run(() => CrucePendientes.Calcular(lista, indice.Buscar, rutas.Contains,
                                                                    CrucePendientes.ArchivoDesaparecido));
        Cruces++;

        var tachadas = Pendientes.MarcarConseguidas(cambios.Conseguidas, DateTime.Now);
        var movidas = Pendientes.CambiarArchivos(cambios.Movidas);
        var reabiertas = Pendientes.Reabrir(cambios.SinArchivo);
        if (tachadas + movidas + reabiertas + descartesMovidos == 0) return;
        GuardarPendientes();
        if (tachadas > 0) _engine.Logger.Sum($"Pendientes: {tachadas} conseguida(s) al aparecer en la biblioteca.");
        if (movidas > 0) _engine.Logger.Detail($"Pendientes: {movidas} conseguida(s) siguen a su archivo, que cambió de nombre o de carpeta.");
        if (reabiertas > 0) _engine.Logger.Sum($"Pendientes: {reabiertas} conseguida(s) vuelven a pendiente porque su archivo ya no existe (papelera o borrado) y no hay otra copia.");
        if (descartesMovidos > 0) _engine.Logger.Detail($"Pendientes: {descartesMovidos} archivo(s) descartado(s) siguen descartados con su nombre nuevo.");
        PendientesCambiadas?.Invoke();
    }

    /// <summary>
    /// Descartados que ya se buscaron en los manifiestos de deshacer sin encontrar adónde fueron. Un
    /// archivo fuera de la biblioteca no lo renombra BeatTag, así que mientras sigan fuera no hace
    /// falta volver a leer los manifiestos por ellos (ver AppEngine.ReubicarFichas, que hace lo mismo).
    /// </summary>
    private IReadOnlySet<string> _descartadosBuscados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lleva los descartes de <see cref="Pendiente.Descartados"/> a la ruta nueva de los archivos que
    /// BeatTag ha renombrado o movido (<see cref="CrucePendientes.SeguirDescartados"/>). Leer los
    /// manifiestos va fuera del hilo de la interfaz, y solo cuando hay un descartado fuera de la
    /// biblioteca que no se haya buscado ya. Devuelve cuántos ha cambiado (sin guardar).
    /// </summary>
    private async Task<int> SeguirDescartadosAsync(IReadOnlyList<Pendiente> lista, HashSet<string> rutas)
    {
        var fuera = CrucePendientes.DescartadosFuera(lista, rutas.Contains);
        if (fuera.All(_descartadosBuscados.Contains))
        {
            // Los que han vuelto a la biblioteca salen de los ya buscados: si se renombran, hay que buscarlos.
            _descartadosBuscados = fuera;
            return 0;
        }
        var carpeta = _engine.Paths.UndoDir;
        try
        {
            var cambios = await Task.Run(() => CrucePendientes.SeguirDescartados(lista, RekordboxRelocator.ReadRenames(carpeta), rutas.Contains));
            var n = Pendientes.CambiarDescartados(cambios);
            _descartadosBuscados = n == 0 ? fuera : CrucePendientes.DescartadosFuera(Pendientes.Todas(), rutas.Contains);
            return n;
        }
        catch (Exception e)
        {
            // Sin los manifiestos se cruza igual: lo peor es que un descartado renombrado vuelva a tachar.
            _engine.Logger.Error("Pendientes: no se pudieron leer los renombrados para seguir los archivos descartados", e);
            _descartadosBuscados = fuera;
            return 0;
        }
    }

    // --- Listas seguidas ---

    /// <summary>Las canciones de una lectura, como las guarda una lista seguida.</summary>
    /// <param name="laTienes">Para cada canción, si está en la biblioteca (en el mismo orden).</param>
    public static List<PistaLista> PistasDe(IReadOnlyList<ChartTrack> pistas, Func<int, bool> laTienes)
        => pistas.Select((t, i) => new PistaLista
        {
            Artista = t.Artist ?? "", Titulo = t.Title ?? "",
            IdPista = t.SpotifyId.Length > 0 ? t.SpotifyId : t.Link ?? "",
            Enlace = t.Link ?? "",
            LaTienes = laTienes(i),
        }).ToList();

    /// <summary>Empieza a seguir una lista, con lo que tiene ahora como primera lectura. Devuelve "" o el error al guardar.</summary>
    public string Seguir(ListaSeguida descripcion, IReadOnlyList<PistaLista> pistas, DateTime fecha)
    {
        descripcion.Alta = DateTime.Now;
        Seguidas.Seguir(descripcion, pistas, fecha);
        var error = GuardarSeguidas();
        _engine.Logger.Detail($"Listas seguidas: ahora se sigue «{descripcion.Nombre}» ({pistas.Count} canciones).");
        SeguidasCambiadas?.Invoke();
        return error;
    }

    public string DejarDeSeguir(IEnumerable<string> ids)
    {
        var n = Seguidas.DejarDeSeguir(ids);
        if (n == 0) return "";
        var error = GuardarSeguidas();
        SeguidasCambiadas?.Invoke();
        return error;
    }

    /// <summary>Apunta la lectura de una lista seguida que se acaba de abrir en su página, y la guarda.</summary>
    public NovedadesLista RegistrarLectura(string clave, IReadOnlyList<PistaLista> pistas, DateTime fecha, bool completa,
                                           string nombre, string aviso)
    {
        var novedades = Seguidas.Registrar(clave, pistas, fecha, completa, DateTime.Now, nombre, aviso);
        GuardarSeguidas();
        SeguidasCambiadas?.Invoke();
        return novedades;
    }

    /// <summary>Cómo fue revisar una lista en «Actualizar todas».</summary>
    public sealed record Revision(string Id, string Nombre, int Canciones, int Nuevas, int NuevasQueFaltan,
                                  DateTime? Desde, string Aviso, string Error);

    /// <summary>
    /// Vuelve a leer todas las listas seguidas, una detrás de otra, y apunta qué ha entrado en cada una.
    /// Solo lee de la red y escribe el archivo de listas seguidas: no toca la música, así que no
    /// bloquea la aplicación.
    ///
    /// Los charts se piden como en Tendencias, con la copia guardada si tiene menos de unas horas (es
    /// una lista diaria). Las playlists se leen siempre de la red, a propósito: no se cachean
    /// (<see cref="PlaylistProvider"/>), y una de Deezer de 500 canciones son 5 peticiones.
    /// </summary>
    public async Task<IReadOnlyList<Revision>> ActualizarTodasAsync(IProgress<string>? avance, CancellationToken ct)
    {
        var listas = Seguidas.Todas();
        var res = new List<Revision>();
        if (listas.Count == 0) return res;

        var canciones = _engine.Library.Tracks.ToList();
        var indice = (await IndiceBibliotecaCompartido.De(_engine.Library).ObtenerAsync(canciones, ct)).Indice;
        _engine.Logger.Head($"Listas seguidas: revisando {listas.Count}…");

        try
        {
            for (var i = 0; i < listas.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var l = listas[i];
                avance?.Report($"Revisando «{l.Nombre}» ({i + 1} de {listas.Count})…");
                try
                {
                    var lectura = await LeerAsync(l, ct);
                    if (lectura.Error.Length > 0)
                    {
                        res.Add(new Revision(l.Id, l.Nombre, 0, 0, 0, null, "", lectura.Error));
                        _engine.Logger.Detail($"Listas seguidas: «{l.Nombre}»: {lectura.Error}");
                        continue;
                    }
                    var pistas = await Task.Run(() => PistasDe(lectura.Pistas, k => indice.Buscar(lectura.Pistas[k].Artist, lectura.Pistas[k].Title).Length > 0), ct);
                    var clave = ClaveTrasLeer(l, lectura.Lista);
                    var nov = Seguidas.Registrar(clave, pistas, lectura.Fecha, lectura.Completa, DateTime.Now, lectura.Nombre, lectura.Aviso);
                    // Lista a lista: si se cierra la aplicación a mitad, lo revisado no se pierde.
                    GuardarSeguidas();
                    var faltan = nov.Nuevas.Count(k => !pistas[k].LaTienes);
                    res.Add(new Revision(l.Id, lectura.Nombre.Length > 0 ? lectura.Nombre : l.Nombre, pistas.Count,
                                         nov.Nuevas.Count, faltan, nov.Desde, lectura.Aviso, ""));
                    _engine.Logger.Detail($"Listas seguidas: «{l.Nombre}»: {pistas.Count} canciones, {nov.Nuevas.Count} nuevas ({faltan} no están en la biblioteca).");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    res.Add(new Revision(l.Id, l.Nombre, 0, 0, 0, null, "", e.Message));
                    _engine.Logger.Error($"Listas seguidas: no se pudo revisar «{l.Nombre}»", e);
                }
            }
        }
        finally
        {
            // Lo revisado antes de cancelar también cuenta.
            GuardarSeguidas();
            SeguidasCambiadas?.Invoke();
        }
        _engine.Logger.Sum($"Listas seguidas: {res.Count(r => r.Error.Length == 0)} revisadas, {res.Sum(r => r.Nuevas)} canciones nuevas.");
        return res;
    }

    /// <summary>
    /// La clave con la que apuntar la lectura de una playlist seguida. Si se siguió pegando un enlace
    /// corto, al leerla se sabe a qué lista apuntaba y pasa a la clave (y al enlace) del largo, igual
    /// que al abrirla en Playlists: así se reconoce se pegue como se pegue, y el enlace corto, que
    /// caduca, no hace falta seguirlo en cada revisión.
    /// </summary>
    private string ClaveTrasLeer(ListaSeguida l, PlaylistLink? leida)
    {
        if (leida is not { } lk) return l.Clave;
        var nueva = ListasSeguidas.ClavePlaylist(lk);
        if (nueva == l.Clave || !Seguidas.CambiarClave(l.Clave, nueva, ListasSeguidas.EnlaceCanonico(lk))) return l.Clave;
        _engine.Logger.Detail($"Listas seguidas: «{l.Clave}» pasa a identificarse como «{nueva}».");
        return nueva;
    }

    /// <param name="Lista">La playlist leída, ya reconocida (null en un chart o si no se pudo leer).</param>
    private sealed record Lectura(IReadOnlyList<ChartTrack> Pistas, DateTime Fecha, bool Completa, string Nombre, string Aviso, string Error,
                                  PlaylistLink? Lista = null);

    /// <summary>
    /// Lo que hay que saber de una playlist leída a medias, pensando en seguirla. El tope de Spotify
    /// deja siempre las mismas 100 primeras: si la lista crece por el final, lo nuevo no se ve nunca.
    /// </summary>
    public static string AvisoLectura(PlaylistLeida p)
    {
        if (!p.Recortada) return "";
        return p.Plataforma == PlataformaLista.Spotify
            ? $"Spotify solo deja leer las {PlaylistProvider.LimiteSpotify} primeras canciones de una lista: si crece por el final, las canciones nuevas no se verán."
            : p.Aviso;
    }

    /// <summary>
    /// Si una lectura vale para comparar con la siguiente. El tope de Spotify es estable (siempre las
    /// mismas 100); un corte de Deezer a mitad, no: la siguiente lectura entera daría por nuevas las
    /// canciones que faltaron.
    /// </summary>
    public static bool LecturaCompleta(PlaylistLeida p) => !(p.Recortada && p.Plataforma == PlataformaLista.Deezer);

    private async Task<Lectura> LeerAsync(ListaSeguida l, CancellationToken ct)
    {
        if (l.Tipo == TipoListaSeguida.Playlist)
        {
            var p = await _engine.Playlists.LeerAsync(l.Enlace, ct);
            if (!p.Ok) return new Lectura(Array.Empty<ChartTrack>(), DateTime.Now, false, "", "", p.Error);
            return new Lectura(p.Pistas, DateTime.Now, LecturaCompleta(p), p.Nombre, AvisoLectura(p), "", p.Lista);
        }

        var fuente = Enum.TryParse<ChartSource>(l.Fuente, out var f) ? f : ChartSource.Spotify;
        var pais = new ChartCountry(l.Pais, l.ListaPais, l.CodigoPais);
        var chart = await _engine.Charts.GetChartAsync(fuente, pais, l.Tamano > 0 ? l.Tamano : 50, ct);
        if (chart.Count == 0) return new Lectura(chart, DateTime.Now, false, "", "", "No se ha podido descargar el chart. ¿Hay conexión a internet?");
        var fecha = (_engine.Api.CacheOn ? _engine.Charts.FechaDeLaLista(fuente, pais, l.Tamano) : null) ?? DateTime.Now;
        return new Lectura(chart, fecha, true, "", "", "");
    }
}
