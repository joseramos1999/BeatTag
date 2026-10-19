using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Página Asistente IA: las herramientas que usan la IA local, más una que no la necesita.
///
/// Todas siguen la misma norma, que sale de medir el modelo antes de construir nada: la IA
/// PROPONE, las reglas y los datos reales COMPRUEBAN, y el usuario DECIDE. Nada de lo que diga el
/// modelo se escribe sin pasar por las tres cosas.
/// </summary>
public partial class AsistenteViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina
{
    internal readonly AppEngine Engine;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;

    /// <summary>Qué IA hay: modelo en uso, o por qué no hay.</summary>
    [ObservableProperty] private string _estadoIa = "Comprobando la IA local…";
    [ObservableProperty] private bool _iaDisponible;
    [ObservableProperty] private string _modelo = "";

    public BusquedaFraseViewModel Busqueda { get; }
    public FichasIaViewModel FichasIa { get; }
    public GenerosIaViewModel Generos { get; }
    public MezclaViewModel Mezcla { get; }
    public RenombrarIaViewModel Renombrar { get; }
    public CompletarNombresViewModel Completar { get; }

    /// <summary>Se ha creado una colección desde aquí: el shell la abre en Colecciones.</summary>
    public event Action<ColeccionInteligente>? ColeccionCreada;
    internal void AvisarColeccionCreada(ColeccionInteligente c) => ColeccionCreada?.Invoke(c);

    public AsistenteViewModel(AppEngine engine)
    {
        Engine = engine;
        Busqueda = new BusquedaFraseViewModel(this);
        FichasIa = new FichasIaViewModel(this);
        Generos = new GenerosIaViewModel(this);
        Mezcla = new MezclaViewModel(this);
        Renombrar = new RenombrarIaViewModel(this);
        Completar = new CompletarNombresViewModel(this);
    }

    internal const string TextoIaDesactivada =
        "La IA local está desactivada en Enriquecer. Las herramientas funcionan solo con reglas.";

    /// <summary>Mira si Ollama responde y con qué modelo. Se llama al entrar en la página.</summary>
    [RelayCommand]
    public async Task ComprobarIaAsync()
    {
        // Las colecciones se refrescan SIEMPRE, antes de mirar la IA. Antes se hacía después de salir
        // por «IA desactivada», y con la IA apagada una colección creada con «Buscar con una frase»
        // (que funciona con reglas) no aparecía en «Ordenar para mezclar» hasta reiniciar.
        RellenarAmbitos();

        if (!Engine.Config.UseAi)
        {
            IaDisponible = false;
            Modelo = "";
            EstadoIa = TextoIaDesactivada;
            return;
        }
        EstadoIa = "Comprobando la IA local…";
        var estado = await Engine.Ai.ComprobarModeloAsync(Engine.Config.AiModel);
        (IaDisponible, Modelo, EstadoIa) = DescribirIa(estado, Engine.Config.AiModel);
    }

    /// <summary>
    /// Qué decir de la IA en la cabecera. Que Ollama responda no basta: el modelo elegido en Ajustes
    /// puede no estar descargado (el desplegable ofrece también los sugeridos). Antes la cabecera decía
    /// «IA local: llama3.1:8b» sin estar instalado, y «Proponer fichas» con límite 5000 acababa en
    /// «0 propuestas… 5000 sin respuesta». Ahora se dice el motivo real, tal cual lo da Ollama.
    /// </summary>
    internal static (bool Disponible, string Modelo, string Texto) DescribirIa(EstadoModelo estado, string elegido)
    {
        if (!estado.Disponible)
            return (false, "", $"{estado.Motivo} Mientras tanto, las herramientas funcionan solo con reglas.");
        var automatico = string.IsNullOrWhiteSpace(elegido) ? " (elegido automáticamente)" : "";
        return (true, estado.Modelo, $"IA local: {estado.Modelo}{automatico}");
    }

    /// <summary>
    /// La IA ha dejado de responder a mitad de una herramienta. Se dice en la cabecera, con el motivo,
    /// en vez de seguir dándola por disponible.
    /// </summary>
    internal void IaPerdida(string motivo)
    {
        IaDisponible = false;
        EstadoIa = $"{motivo} Pulsa «Comprobar» cuando esté resuelto.";
    }

    /// <summary>Las listas de colecciones de todas las herramientas, como están ahora en Colecciones.</summary>
    internal void RellenarAmbitos()
    {
        Mezcla.RellenarColecciones();
        FichasIa.RellenarAmbitos();
        Renombrar.RellenarAmbitos();
        Completar.RellenarAmbitos();
    }

    [RelayCommand]
    private void Cancelar() => _cts?.Cancel();

    /// <summary>
    /// Marca la página como ocupada mientras dura <paramref name="trabajo"/>, con cancelación y
    /// avance. Todas las herramientas pasan por aquí para que el bloqueo del resto de la aplicación
    /// y el botón Cancelar funcionen igual en todas.
    /// </summary>
    internal async Task TrabajarAsync(string que, Func<CancellationToken, Task> trabajo)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        Status = que;
        try { await trabajo(_cts.Token); }
        catch (OperationCanceledException) { Status = "Cancelado."; }
        catch (Exception e)
        {
            Engine.Logger.Error("Asistente IA: " + que, e);
            Status = "Error: " + e.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Las fichas de toda la biblioteca de una vez, para no leerlas canción a canción.</summary>
    internal Dictionary<string, FichaDj> FichasActuales()
    {
        var d = new Dictionary<string, FichaDj>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Engine.Fichas.Rutas)
            if (Engine.Fichas.Obtener(r) is { } f) d[r] = f;
        return d;
    }

    internal bool BibliotecaLista(out string motivo)
    {
        motivo = Engine.Library.IsScanned ? "" : "Primero escanea la biblioteca.";
        return motivo.Length == 0;
    }

    /// <summary>
    /// Las canciones de la biblioteca que caen en el ámbito y cumplen <paramref name="filtro"/>,
    /// saltando las ya revisadas en esta sesión, hasta <paramref name="limite"/>. El recorrido va fuera
    /// del hilo de la interfaz: los filtros de las herramientas leen el nombre de cada archivo con
    /// expresiones regulares, y con quince mil canciones eso congelaba la ventana antes de empezar.
    /// </summary>
    internal async Task<(List<Track> Elegidas, int Saltadas)> ElegirCandidatasAsync(
        ColeccionInteligente? coleccion, Func<Track, FichaDj?, bool> filtro, RevisadasEnSesion? revisadas, int limite,
        CancellationToken ct)
    {
        var canciones = Engine.Library.Tracks.ToList();   // instantánea en el hilo de la interfaz
        var fichas = FichasActuales();
        return await Task.Run(() =>
        {
            var candidatas = canciones.Where(t =>
            {
                var f = fichas.GetValueOrDefault(t.FilePath);
                return (coleccion == null || FiltroColeccion.Cumple(coleccion, t, f)) && filtro(t, f);
            });
            return revisadas != null ? revisadas.Elegir(candidatas, limite) : (candidatas.Take(limite).ToList(), 0);
        }, ct);
    }
}

// =====================================================================================================
// Piezas comunes a las herramientas
// =====================================================================================================

/// <summary>
/// Una opción del desplegable de ámbito: toda la biblioteca (sin colección) o una colección.
///
/// Antes el desplegable guardaba solo el NOMBRE y la herramienta buscaba la colección por nombre: si
/// se renombraba en Colecciones, la búsqueda no la encontraba y recorría toda la biblioteca sin avisar;
/// con dos colecciones llamadas igual (o una llamada «Toda la biblioteca») se cogía la primera. Ahora
/// se guarda la colección misma, y su identificador por si la lista se rehízo.
/// </summary>
public sealed record AmbitoOpcion(ColeccionInteligente? Coleccion, string Nombre)
{
    public const string TextoTodaLaBiblioteca = "Toda la biblioteca";
    public static readonly AmbitoOpcion TodaLaBiblioteca = new(null, TextoTodaLaBiblioteca);

    public string Id => Coleccion?.Id ?? "";
    public bool EsTodaLaBiblioteca => Coleccion == null;
    public override string ToString() => Nombre;
}

/// <summary>Cómo se rellenan, se conservan y se resuelven los desplegables de ámbito.</summary>
public static class SelectorAmbito
{
    /// <summary>
    /// Las opciones, en el orden de Colecciones. Los nombres repetidos se distinguen con un número, y
    /// una colección llamada «Toda la biblioteca» se señala como colección para no confundirla con la
    /// biblioteca entera.
    /// </summary>
    public static List<AmbitoOpcion> Opciones(IEnumerable<ColeccionInteligente> colecciones, bool conTodaLaBiblioteca)
    {
        var lista = new List<AmbitoOpcion>();
        if (conTodaLaBiblioteca) lista.Add(AmbitoOpcion.TodaLaBiblioteca);
        var vistos = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var c in colecciones)
        {
            var nombre = c.Nombre.Trim().Length > 0 ? c.Nombre.Trim() : "(sin nombre)";
            if (string.Equals(nombre, AmbitoOpcion.TextoTodaLaBiblioteca, StringComparison.CurrentCultureIgnoreCase))
                nombre += " (colección)";
            var n = vistos.GetValueOrDefault(nombre) + 1;
            vistos[nombre] = n;
            lista.Add(new AmbitoOpcion(c, n == 1 ? nombre : $"{nombre} ({n})"));
        }
        return lista;
    }

    /// <summary>
    /// La opción que corresponde a la elegida antes (la misma colección, aunque haya cambiado de
    /// nombre), o la primera si ya no está.
    /// </summary>
    public static AmbitoOpcion? Elegir(IReadOnlyList<AmbitoOpcion> opciones, AmbitoOpcion? anterior)
    {
        if (anterior != null)
        {
            var misma = opciones.FirstOrDefault(o => ReferenceEquals(o.Coleccion, anterior.Coleccion))
                        ?? (anterior.Id.Length > 0 ? opciones.FirstOrDefault(o => o.Id == anterior.Id) : null);
            if (misma != null) return misma;
        }
        return opciones.FirstOrDefault();
    }

    /// <summary>
    /// La colección del ámbito (null = toda la biblioteca). Si la colección elegida ya no existe, NO se
    /// pasa en silencio a la biblioteca entera: se devuelve false con el motivo.
    /// </summary>
    public static bool Resolver(AmbitoOpcion? ambito, IReadOnlyList<ColeccionInteligente> todas,
                                out ColeccionInteligente? coleccion, out string error)
    {
        coleccion = null;
        error = "";
        if (ambito?.Coleccion == null) return true;
        coleccion = todas.FirstOrDefault(c => ReferenceEquals(c, ambito.Coleccion))
                    ?? (ambito.Id.Length > 0 ? todas.FirstOrDefault(c => c.Id == ambito.Id) : null);
        if (coleccion != null) return true;
        error = $"La colección «{ambito.Nombre}» ya no existe. Elige otro ámbito.";
        return false;
    }

    /// <summary>Rehace un desplegable conservando la elección por colección, no por nombre.</summary>
    internal static AmbitoOpcion? Rellenar(ObservableCollection<AmbitoOpcion> destino, IEnumerable<ColeccionInteligente> colecciones,
                                           bool conTodaLaBiblioteca, AmbitoOpcion? anterior)
    {
        var opciones = Opciones(colecciones, conTodaLaBiblioteca);
        destino.Clear();
        foreach (var o in opciones) destino.Add(o);
        return Elegir(opciones, anterior);
    }
}

/// <summary>
/// Las canciones que una herramienta ya ha revisado en esta sesión y no se aceptaron (rechazadas, sin
/// propuesta o sin respuesta). La siguiente pasada las salta.
///
/// Sin esto, «Renombrar con IA» con límite 200 cogía siempre las 200 primeras de la biblioteca: si eran
/// de un pool que la IA no sabe arreglar, cada pasada acababa en «Ninguna propuesta pasó las
/// comprobaciones» y nunca se llegaba a la 201. «Empezar desde el principio» las vuelve a incluir.
/// </summary>
public sealed class RevisadasEnSesion
{
    private readonly HashSet<string> _rutas = new(StringComparer.OrdinalIgnoreCase);

    public int Cuantas => _rutas.Count;
    public bool Contiene(string ruta) => _rutas.Contains(ruta);
    public void Anotar(string ruta) => _rutas.Add(ruta);
    public void Olvidar(string ruta) => _rutas.Remove(ruta);
    public void Vaciar() => _rutas.Clear();

    /// <summary>
    /// Hasta <paramref name="limite"/> candidatas sin revisar, en el orden dado, y cuántas revisadas se
    /// han saltado por el camino. Deja de mirar en cuanto completa el cupo: cada candidata cuesta leer
    /// su nombre, y no hace falta recorrer la biblioteca entera para llenar 200 huecos.
    /// </summary>
    public (List<Track> Elegidas, int Saltadas) Elegir(IEnumerable<Track> candidatas, int limite)
    {
        var elegidas = new List<Track>();
        var saltadas = 0;
        if (limite <= 0) return (elegidas, 0);
        foreach (var t in candidatas)
        {
            if (_rutas.Contains(t.FilePath)) { saltadas++; continue; }
            elegidas.Add(t);
            if (elegidas.Count >= limite) break;
        }
        return (elegidas, saltadas);
    }
}

/// <summary>
/// Cuándo dejar de preguntar a la IA dentro de un bucle: tras <see cref="FallosSeguidos"/> fallos
/// seguidos. Si Ollama se ha caído o el modelo no está, seguir canción a canción solo acumula «sin
/// respuesta» (y, con un cortafuegos que descarta los paquetes, 180 s de espera por canción).
/// Guarda también qué canciones formaban la racha: no se dan por revisadas, porque no se llegó a
/// preguntar por ellas de verdad.
/// </summary>
public sealed class CorteIa
{
    public const int FallosSeguidos = OllamaClient.FallosSeguidosParaPausar;

    private readonly List<string> _racha = new();
    public IReadOnlyList<string> Racha => _racha;
    public string Motivo { get; private set; } = "";
    public bool Cortado => _racha.Count >= FallosSeguidos;

    public void Bien() => _racha.Clear();

    /// <summary>Anota un fallo. Devuelve true si ya hay que dejar de preguntar.</summary>
    public bool Fallo(string ruta, string motivo)
    {
        _racha.Add(ruta);
        Motivo = motivo;
        return Cortado;
    }

    /// <summary>Las canciones de la racha vuelven a estar pendientes.</summary>
    public void DevolverRacha(RevisadasEnSesion revisadas)
    {
        if (!Cortado) return;
        foreach (var r in _racha) revisadas.Olvidar(r);
    }
}

// =====================================================================================================
// Menú contextual de las tablas
// =====================================================================================================

/// <summary>
/// Una fila que corresponde a un archivo. Es lo que necesita el menú contextual para escuchar,
/// editar o abrir su carpeta, sea cual sea la herramienta: cada una tiene su propio tipo de fila.
/// </summary>
public interface IFilaConArchivo { string RutaArchivo { get; } }

/// <summary>Una fila con casilla (Aceptar o Aplicar), para marcar o desmarcar varias de golpe.</summary>
public interface IFilaMarcable { bool Marcada { get; set; } }

// =====================================================================================================
// 1. Buscar con una frase
// =====================================================================================================

/// <summary>Una condición en la lista de revisión, con su casilla.</summary>
public sealed partial class FiltroFila : ObservableObject
{
    public FiltroPropuesto Filtro { get; }
    private readonly Action _alCambiar;

    /// <param name="sinDatos">Nadie en la biblioteca tiene todavía ese dato en su ficha.</param>
    public FiltroFila(FiltroPropuesto filtro, bool sinDatos, Action alCambiar)
    {
        Filtro = filtro;
        _alCambiar = alCambiar;
        SinDatos = sinDatos;

        // Qué sale marcado, medido sobre una biblioteca real:
        //   · Lo leído con reglas, sí.
        //   · Lo que añade la IA, no. Lo que pasaba la validación era discutible («la noche» →
        //     energía alta; «para que baile la gente mayor» → alta con un modelo y apertura con otro).
        //   · Una condición de ficha que no tiene NADIE, no: dejaba la colección en cero canciones
        //     («Bad Bunny con mucha energía» no encontraba ningún tema de Bad Bunny).
        _aceptado = filtro.Valido && filtro.Origen == OrigenFiltro.Frase && !sinDatos;
    }

    public bool SinDatos { get; }

    [ObservableProperty] private bool _aceptado;
    partial void OnAceptadoChanged(bool value) => _alCambiar();

    public bool SePuedeUsar => Filtro.Valido;
    public string Campo => Filtro.NombreCampo;
    public string Valor => Filtro.Valor;
    public string Cita => Filtro.Cita.Length > 0 ? $"«{Filtro.Cita}»" : "";
    public string Origen => !Filtro.Valido ? "Descartado" : Filtro.Origen == OrigenFiltro.Frase ? "Leído en tu frase" : "Propuesto por la IA: revísalo";
    public string Motivo => Filtro.Descartado.Length > 0
        ? Filtro.Descartado
        : SinDatos ? "· Ninguna canción tiene este dato en su ficha todavía." : "";
    public bool EsIa => Filtro.Origen == OrigenFiltro.Ia;
}

public sealed partial class BusquedaFraseViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;

    public BusquedaFraseViewModel(AsistenteViewModel padre) => _p = padre;

    [ObservableProperty] private string _frase = "";
    [ObservableProperty] private string _nombreColeccion = "";
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _aviso = "";
    [ObservableProperty] private bool _hayFiltros;

    public ObservableCollection<FiltroFila> Filtros { get; } = new();
    public ObservableCollection<FichaRow> Vista { get; } = new();

    public IReadOnlyList<string> Ejemplos { get; } = new[]
    {
        "reggaeton suave para abrir una boda, sin palabrotas",
        "house vocal entre 120 y 124 bpm para el pico",
        "temas en inglés de los 80 para cerrar",
        "cumbia y merengue de los 90",
    };

    [RelayCommand]
    private void UsarEjemplo(string? ejemplo) { if (ejemplo != null) Frase = ejemplo; }

    [RelayCommand]
    private Task InterpretarAsync() => _p.TrabajarAsync("Leyendo la petición…", async ct =>
    {
        var frase = Frase.Trim();
        if (frase.Length == 0) { _p.Status = "Escribe qué buscas."; return; }
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var voc = await Task.Run(() => new VocabularioBiblioteca(_p.Engine.Library.Tracks.ToList()), ct);
        var reglas = BusquedaIa.DesdeFrase(frase, voc);
        var ia = new List<FiltroPropuesto>();
        var nota = "";

        if (_p.IaDisponible)
        {
            _p.Status = "Consultando a la IA…";
            var (json, error) = await _p.Engine.Ai.PedirJsonAsync(BusquedaIa.Sistema, frase, _p.Modelo, 512, ct: ct);
            if (error.Length > 0) nota = $"La IA no respondió ({error}); se usa solo lo leído en la frase.";
            else ia = BusquedaIa.DesdeIa(json, frase, voc);
        }
        else nota = "Sin IA: solo se usa lo que las reglas entienden de la frase.";

        var fichas = _p.FichasActuales().Values.ToList();
        bool SinDatos(string campo) => campo switch
        {
            "energia" => !fichas.Any(x => x.Energia > 0),
            "momento" => !fichas.Any(x => x.Momentos != MomentoSet.Ninguno),
            "voz" => !fichas.Any(x => x.Voz != TipoVoz.SinIndicar),
            "idioma" => !fichas.Any(x => x.Idioma.Trim().Length > 0),
            _ => false,
        };

        Filtros.Clear();
        foreach (var f in BusquedaIa.Combinar(reglas, ia)) Filtros.Add(new FiltroFila(f, SinDatos(f.Campo), Recalcular));
        HayFiltros = Filtros.Any(f => f.SePuedeUsar);
        NombreColeccion = Capitalizar(frase);
        Recalcular();

        var descartados = Filtros.Count(f => !f.SePuedeUsar);
        _p.Status = !HayFiltros
            ? "No se ha encontrado ninguna condición en la frase. Prueba a nombrar un género, un tempo o un momento de la sesión."
            : $"{Filtros.Count(f => f.SePuedeUsar)} condiciones" + (descartados > 0 ? $" · {descartados} propuestas de la IA descartadas" : "")
              + (nota.Length > 0 ? ". " + nota : ".");
    });

    private static string Capitalizar(string s)
    {
        var t = s.Length > 60 ? s[..60].TrimEnd() + "…" : s;
        return t.Length == 0 ? t : char.ToUpper(t[0]) + t[1..];
    }

    /// <summary>Vuelve a contar las canciones con las condiciones marcadas. Se llama al tocar una casilla.</summary>
    private void Recalcular()
    {
        Vista.Clear();
        var aceptados = Filtros.Where(f => f.Aceptado && f.SePuedeUsar).Select(f => f.Filtro).ToList();
        var c = BusquedaIa.AColeccion(aceptados, NombreColeccion);
        if (!FiltroColeccion.TieneCriterios(c)) { Resumen = ""; Aviso = ""; return; }

        var fichas = _p.FichasActuales();
        var encontradas = _p.Engine.Library.Tracks
            .Select(t => (t, f: fichas.GetValueOrDefault(t.FilePath)))
            .Where(x => FiltroColeccion.Cumple(c, x.t, x.f))
            .ToList();
        foreach (var (t, f) in encontradas.Take(300)) Vista.Add(new FichaRow(t, f));

        var segundos = encontradas.Sum(x => (long)x.t.DurationSeconds);
        Resumen = $"{encontradas.Count} canciones · {(int)TimeSpan.FromSeconds(segundos).TotalMinutes} min"
                  + (encontradas.Count > 300 ? " (se muestran las 300 primeras)" : "");

        // Lo que más sorprende: pedir energía o momento y que no salga nada porque nadie los ha
        // rellenado. Se dice cuántas canciones tienen ese dato, en vez de dejar una lista vacía sin más.
        var deFicha = aceptados.Where(f => BusquedaIa.DependeDeFicha(f.Campo)).Select(f => f.NombreCampo.ToLowerInvariant()).Distinct().ToList();
        Aviso = deFicha.Count == 0
            ? ""
            : $"{string.Join(", ", deFicha)} solo se encuentra en canciones con ficha DJ, y ahora hay {fichas.Count} con ficha. "
              + "Desmarca esas condiciones o rellena fichas (a mano o con «Proponer fichas»).";
    }

    [RelayCommand]
    private void CrearColeccion()
    {
        var aceptados = Filtros.Where(f => f.Aceptado && f.SePuedeUsar).Select(f => f.Filtro).ToList();
        var c = BusquedaIa.AColeccion(aceptados, NombreColeccion);
        if (!FiltroColeccion.TieneCriterios(c)) { _p.Status = "Marca al menos una condición."; return; }
        _p.AvisarColeccionCreada(c);
    }
}

// =====================================================================================================
// 2. Proponer fichas
// =====================================================================================================

public sealed partial class PropuestaFichaFila : ObservableObject, IFilaConArchivo, IFilaMarcable
{
    public PropuestaFicha Propuesta { get; }
    public Track Track { get; }

    public PropuestaFichaFila(PropuestaFicha p, Track t)
    {
        Propuesta = p;
        Track = t;
        _aceptar = MarcadaPorDefecto(p);
    }

    /// <summary>
    /// Sale marcada solo lo que dice el propio nombre del archivo («Instrumental»). Lo de la IA sale
    /// desmarcado, como en las demás herramientas: MEDIDO con llama3.2, acierta el idioma en 11 de 14
    /// y los fallos siguen la nacionalidad del artista (Daft Punk → FR, Anitta → PT). Con todo marcado,
    /// aceptar 1000 propuestas dejaba unas 200 fichas con el idioma equivocado, y como esas fichas ya no
    /// se vuelven a proponer, el error se quedaba fijo.
    /// </summary>
    internal static bool MarcadaPorDefecto(PropuestaFicha p) => p.Origen != "IA";

    [ObservableProperty] private bool _aceptar;
    string IFilaConArchivo.RutaArchivo => Track.FilePath;
    bool IFilaMarcable.Marcada { get => Aceptar; set => Aceptar = value; }

    public string Artist => Track.Artist ?? "";
    public string Title => Track.Title ?? Track.FileName;
    public string Idioma => Propuesta.Idioma;
    public string Voz => Fichas.Nombre(Propuesta.Voz);
    public string Origen => Propuesta.Origen;
}

public sealed partial class FichasIaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;

    /// <summary>Lo revisado en esta sesión y no aceptado: la siguiente pasada sigue por donde iba.</summary>
    private readonly RevisadasEnSesion _revisadas = new();

    /// <summary>Cada «Aceptar las marcadas», con lo que había antes, para poder deshacerlo.</summary>
    private readonly Stack<(AceptacionFichas Cambios, List<PropuestaFichaFila> Filas)> _aceptaciones = new();

    public FichasIaViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        RellenarAmbitos();
    }

    public ObservableCollection<AmbitoOpcion> Ambitos { get; } = new();
    [ObservableProperty] private AmbitoOpcion? _ambito = AmbitoOpcion.TodaLaBiblioteca;

    public IReadOnlyList<int> Limites { get; } = new[] { 50, 200, 1000, 5000 };
    [ObservableProperty] private int _limite = 200;

    public ObservableCollection<PropuestaFichaFila> Propuestas { get; } = new();
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private bool _hayAceptacionQueDeshacer;

    public void RellenarAmbitos()
        => Ambito = SelectorAmbito.Rellenar(Ambitos, _p.Engine.Colecciones.Todas, conTodaLaBiblioteca: true, Ambito);

    [RelayCommand]
    private Task ProponerAsync() => _p.TrabajarAsync("Buscando canciones sin idioma o sin voz…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }
        if (!SelectorAmbito.Resolver(Ambito, _p.Engine.Colecciones.Todas, out var coleccion, out var error))
        {
            _p.Status = error;
            _p.RellenarAmbitos();
            return;
        }

        // Sin IA solo se puede proponer lo que dice el nombre («Instrumental»): se buscan solo esas, para
        // que el cupo no se llene de canciones a las que no se les puede proponer nada.
        var conIa = _p.IaDisponible;
        var (candidatas, saltadas) = await _p.ElegirCandidatasAsync(
            coleccion,
            (t, f) => FichasIa.NecesitaPropuesta(f) && (conIa || FichasIa.PorNombre(t, f) is { HayAlgo: true }),
            _revisadas, Limite, ct);

        // Las que marcaste y aún no has aceptado no se pierden por pedir más.
        var conservadas = Propuestas.Where(p => p.Aceptar).ToList();
        Propuestas.Clear();
        foreach (var p in conservadas) Propuestas.Add(p);

        if (candidatas.Count == 0)
        {
            _p.Status = saltadas > 0
                ? $"No quedan canciones sin revisar en el ámbito: {saltadas} ya se revisaron en esta sesión. «Empezar desde el principio» las vuelve a incluir."
                : "Todas las canciones del ámbito ya tienen idioma y voz.";
            return;
        }

        var fichas = _p.FichasActuales();
        var sinRespuesta = 0;
        string ultimoError = "";
        var corte = new CorteIa();
        var revisadas = 0;
        for (var i = 0; i < candidatas.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = candidatas[i];
            var f = fichas.GetValueOrDefault(t.FilePath);
            revisadas++;
            _p.Progress = 100.0 * (i + 1) / candidatas.Count;
            _p.Status = $"Proponiendo fichas… {i + 1} de {candidatas.Count}";

            // Revisada (aceptada o no, con respuesta o sin ella): la siguiente pasada sigue con otras.
            _revisadas.Anotar(t.FilePath);
            var p = FichasIa.PorNombre(t, f);
            if (p == null && conIa)
            {
                var (json, err) = await _p.Engine.Ai.PedirJsonAsync(
                    FichasIa.Sistema, FichasIa.Peticion(t), _p.Modelo, 32, FichasIa.ClaveCache(t), ct);
                if (err.Length > 0)
                {
                    sinRespuesta++;
                    ultimoError = err;
                    if (corte.Fallo(t.FilePath, err)) break;
                    continue;
                }
                corte.Bien();
                p = FichasIa.Interpretar(t, f, json);
            }
            if (p is { HayAlgo: true }) Propuestas.Add(new PropuestaFichaFila(p, t));
        }

        // Las de la racha que cortó no se llegaron a preguntar de verdad: siguen pendientes.
        if (corte.Cortado)
        {
            corte.DevolverRacha(_revisadas);
            revisadas -= corte.Racha.Count;
            sinRespuesta -= corte.Racha.Count;
            _p.IaPerdida(corte.Motivo);
        }

        var nuevas = Propuestas.Count - conservadas.Count;
        Resumen = $"{nuevas} propuestas para {revisadas} canciones revisadas"
                  + (saltadas > 0 ? $" · {saltadas} ya revisadas en esta sesión se han saltado" : "") + ".";
        _p.Status = corte.Cortado
            ? $"Se ha dejado de preguntar a la IA tras {CorteIa.FallosSeguidos} fallos seguidos: {corte.Motivo} {Resumen}"
            : !_p.IaDisponible
                ? $"Sin IA solo se reconocen las que dicen «instrumental» en el nombre: {nuevas}."
                : sinRespuesta > 0
                    ? $"{Resumen} {sinRespuesta} sin respuesta de la IA ({ultimoError})."
                    : $"{Resumen} Las de la IA salen desmarcadas: revísalas y marca las correctas antes de aceptar.";
    });

    /// <summary>Vuelve a incluir lo ya revisado en esta sesión.</summary>
    [RelayCommand]
    private void EmpezarDeNuevo()
    {
        _revisadas.Vaciar();
        _p.Status = "La próxima búsqueda empezará desde el principio del ámbito.";
    }

    [RelayCommand]
    private void MarcarTodas() { foreach (var p in Propuestas) p.Aceptar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var p in Propuestas) p.Aceptar = false; }

    [RelayCommand]
    private void Aceptar()
    {
        var elegidas = Propuestas.Where(p => p.Aceptar).ToList();
        if (elegidas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        var cambios = elegidas.Select(p => (p.Track.FilePath, p.Propuesta.Cambios())).ToList();
        // Lo de antes se anota ANTES de escribir: es lo que «Deshacer» devolverá.
        var anotado = AceptacionFichas.Anotar(_p.Engine.Fichas, cambios);
        var err = _p.Engine.GuardarFichas(cambios);
        foreach (var p in elegidas) Propuestas.Remove(p);
        _aceptaciones.Push((anotado, elegidas));
        HayAceptacionQueDeshacer = true;
        _p.Engine.AvisarFichasCambiadas();
        _p.Status = err.Length > 0
            ? $"⚠ No se pudieron guardar las fichas: {err}"
            : $"Fichas actualizadas en {elegidas.Count} canciones. Las desmarcadas siguen en la lista; «Deshacer la última aceptación» devuelve lo que había.";
    }

    /// <summary>
    /// Devuelve las fichas a como estaban antes de la última aceptación y vuelve a poner esas propuestas
    /// en la lista, desmarcadas. Solo toca el idioma y la voz que siguen siendo los que se pusieron: si
    /// después los cambiaste a mano, se respeta lo tuyo.
    /// </summary>
    [RelayCommand]
    private void DeshacerAceptacion()
    {
        if (_aceptaciones.Count == 0) return;
        if (_p.IsBusy) { _p.Status = "Espera a que termine el proceso en curso."; return; }
        var (cambios, filas) = _aceptaciones.Pop();
        HayAceptacionQueDeshacer = _aceptaciones.Count > 0;

        var devueltas = cambios.Deshacer(_p.Engine.Fichas);
        var err = _p.Engine.Fichas.Guardar();
        if (err.Length > 0) _p.Engine.Logger.Err("No se pudieron guardar las fichas de DJ: " + err);
        foreach (var f in filas)
        {
            f.Aceptar = false;
            Propuestas.Insert(0, f);
        }
        _p.Engine.AvisarFichasCambiadas();
        _p.Status = err.Length > 0
            ? $"⚠ No se pudieron guardar las fichas: {err}"
            : $"Deshecha la última aceptación: {devueltas} fichas vuelven a como estaban. Sus propuestas están otra vez en la lista, desmarcadas.";
    }
}

/// <summary>
/// Lo que cambió al aceptar unas propuestas de ficha (idioma y voz), para poder deshacerlo.
///
/// Las propuestas de la IA fallan en 1 de cada 5, y una ficha ya rellena no se vuelve a proponer: sin
/// deshacer, un «Aceptar» precipitado dejaba los errores fijos, y la única forma de arreglarlos era a
/// mano, canción a canción. Al deshacer solo se devuelve un campo si sigue teniendo el valor que se
/// puso: lo que el usuario cambiara después no se pisa.
/// </summary>
public sealed class AceptacionFichas
{
    public sealed record Cambio(string Ruta, string IdiomaAntes, TipoVoz VozAntes, string? IdiomaPuesto, TipoVoz? VozPuesta);

    public IReadOnlyList<Cambio> Cambios { get; }
    private AceptacionFichas(IReadOnlyList<Cambio> cambios) => Cambios = cambios;

    /// <summary>Anota cómo están ahora las fichas que se van a cambiar. Se llama ANTES de guardar.</summary>
    public static AceptacionFichas Anotar(AlmacenFichas fichas, IEnumerable<(string Ruta, CambiosFicha Cambios)> cambios)
        => new(cambios.Select(c =>
        {
            var antes = fichas.Obtener(c.Ruta);
            return new Cambio(c.Ruta, antes?.Idioma ?? "", antes?.Voz ?? TipoVoz.SinIndicar, c.Cambios.Idioma, c.Cambios.Voz);
        }).ToList());

    /// <summary>Devuelve los campos que siguen como se pusieron. Devuelve cuántas fichas ha tocado. No guarda en disco.</summary>
    public int Deshacer(AlmacenFichas fichas)
    {
        var tocadas = 0;
        foreach (var c in Cambios)
        {
            var f = fichas.Obtener(c.Ruta) ?? new FichaDj();
            var tocada = false;
            if (c.IdiomaPuesto != null && string.Equals(f.Idioma, c.IdiomaPuesto, StringComparison.Ordinal))
            {
                f.Idioma = c.IdiomaAntes;
                tocada = true;
            }
            if (c.VozPuesta is { } voz && f.Voz == voz)
            {
                f.Voz = c.VozAntes;
                tocada = true;
            }
            if (!tocada) continue;
            fichas.Poner(c.Ruta, f);   // si queda vacía, Poner la quita
            tocadas++;
        }
        return tocadas;
    }
}

// =====================================================================================================
// 3. Unificar géneros
// =====================================================================================================

public sealed partial class GeneroFila : ObservableObject, IFilaMarcable
{
    public string Original { get; }
    public int Canciones { get; }
    public OrigenGenero OrigenValor { get; private set; }

    public GeneroFila(PropuestaGenero p)
    {
        Original = p.Original;
        Canciones = p.Canciones;
        OrigenValor = p.Origen;
        _propuesto = p.Propuesto;
        // Lo que dicen las reglas sale marcado; lo que propone la IA, no: hay que elegirlo a propósito.
        _aplicar = p.Origen is OrigenGenero.Regla or OrigenGenero.NoEsGenero;
    }

    [ObservableProperty] private string _propuesto;
    [ObservableProperty] private bool _aplicar;
    bool IFilaMarcable.Marcada { get => Aplicar; set => Aplicar = value; }

    public bool Cambia => !string.Equals(Original, Propuesto, StringComparison.Ordinal);
    public string PropuestoTexto => Propuesto.Length == 0 ? "(quitar género)" : Propuesto;

    public string Origen => OrigenValor switch
    {
        OrigenGenero.Regla => "Regla",
        OrigenGenero.NoEsGenero => "No es un género",
        OrigenGenero.Ia => "IA",
        _ => "",
    };

    public void PonerIa(string destino)
    {
        OrigenValor = OrigenGenero.Ia;
        Propuesto = destino;
        OnPropertyChanged(nameof(Origen));
    }

    partial void OnPropuestoChanged(string value)
    {
        OnPropertyChanged(nameof(Cambia));
        OnPropertyChanged(nameof(PropuestoTexto));
    }
}

public sealed partial class GenerosIaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    public GenerosIaViewModel(AsistenteViewModel padre) => _p = padre;

    public ObservableCollection<GeneroFila> Filas { get; } = new();
    [ObservableProperty] private string _resumen = "";

    [RelayCommand]
    private Task AnalizarAsync() => _p.TrabajarAsync("Contando géneros…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var propuestas = GenerosIa.Contar(_p.Engine.Library.Tracks.ToList())
                                  .Select(x => GenerosIa.PorReglas(x.Original, x.Canciones)).ToList();
        var destinos = GenerosIa.Destinos(propuestas);
        var filas = propuestas.Select(p => new GeneroFila(p)).ToDictionary(f => f.Original, StringComparer.Ordinal);

        var preguntar = _p.IaDisponible ? GenerosIa.ParaIa(propuestas) : Array.Empty<string>();
        // La clave de caché incluye la lista de destinos: si la biblioteca cambia, la respuesta de antes
        // pudo elegir entre otras opciones y no vale.
        var huella = Huella(string.Join("|", destinos));

        // Las variantes que solo se distinguen en mayúsculas o espacios se preguntan UNA vez. Medido
        // en una biblioteca real: «RKT» salió como Reggaeton y «rkt» y «Rkt» como Rock. Con una sola
        // pregunta, al menos la respuesta es la misma para las tres y se revisa una vez.
        var grupos = preguntar.GroupBy(VocabularioBiblioteca.Clave, StringComparer.Ordinal)
                              .Where(g => g.Key.Length > 0).ToList();
        var corte = new CorteIa();
        for (var i = 0; i < grupos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var representante = grupos[i].OrderByDescending(r => filas[r].Canciones).First();
            var (json, error) = await _p.Engine.Ai.PedirJsonAsync(
                GenerosIa.Sistema, GenerosIa.Peticion(representante, destinos), _p.Modelo, 48,
                $"genero:{huella}:{grupos[i].Key}", ct);
            if (error.Length > 0)
            {
                // Si la IA ha dejado de responder, lo que queda se propone solo con reglas.
                if (corte.Fallo(representante, error)) break;
            }
            else
            {
                corte.Bien();
                if (GenerosIa.Interpretar(json, representante, destinos) is { } destino)
                    foreach (var variante in grupos[i])
                        if (!string.Equals(variante.Trim(), destino, StringComparison.OrdinalIgnoreCase))
                            filas[variante].PonerIa(destino);
            }
            _p.Progress = 100.0 * (i + 1) / grupos.Count;
            _p.Status = $"Consultando a la IA… {i + 1} de {grupos.Count} géneros";
        }
        if (corte.Cortado) _p.IaPerdida(corte.Motivo);

        Filas.Clear();
        foreach (var f in filas.Values.Where(f => f.Cambia).OrderByDescending(f => f.Canciones)) Filas.Add(f);

        var cambian = Filas.Sum(f => f.Canciones);
        Resumen = $"{propuestas.Count} géneros distintos · {Filas.Count} con propuesta ({cambian} canciones)";
        _p.Status = (corte.Cortado ? $"Se ha dejado de preguntar a la IA tras {CorteIa.FallosSeguidos} fallos seguidos: {corte.Motivo} " : "")
                  + (Filas.Count == 0
                      ? "No hay nada que unificar."
                      : $"{Resumen}. Las de la IA salen desmarcadas: revísalas antes de marcarlas.");
    });

    private static string Huella(string s)
    {
        // FNV-1a: estable entre ejecuciones, a diferencia de string.GetHashCode.
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }

    [RelayCommand]
    private void MarcarTodas() { foreach (var f in Filas) f.Aplicar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Filas) f.Aplicar = false; }

    public IReadOnlyList<GeneroFila> Marcadas => Filas.Where(f => f.Aplicar && f.Cambia).ToList();

    /// <summary>Escribe los géneros marcados. La confirmación la pide la vista.</summary>
    public Task AplicarAsync() => _p.TrabajarAsync("Escribiendo géneros…", async ct =>
    {
        var marcadas = Marcadas.ToDictionary(f => f.Original, f => f.Propuesto.Trim(), StringComparer.Ordinal);
        if (marcadas.Count == 0) { _p.Status = "No hay ningún género marcado."; return; }

        var cambios = _p.Engine.Library.Tracks
            .Where(t => t.Genre != null && marcadas.ContainsKey(t.Genre))
            .Select(t => (t.FilePath, marcadas[t.Genre!]))
            .ToList();

        _p.Engine.ReleaseAudio();
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => _p.Engine.AplicarGeneros(cambios, progreso, ct));

        await _p.Engine.Library.ScanAsync();   // los géneros nuevos, en todas las pestañas
        foreach (var f in Filas.Where(f => f.Aplicar).ToList()) Filas.Remove(f);

        var texto = $"Género actualizado en {res.Escritas} canciones";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no se pudieron escribir (detalle en el registro)";
        if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ El cambio NO quedó anotado y no se podrá deshacer: {res.UndoErr}"
            : res.Escritas > 0 ? ". Se puede deshacer desde Enriquecer." : ".";
        _p.Status = texto;
    });
}

// =====================================================================================================
// 5. Renombrar con IA
// =====================================================================================================

public sealed partial class NombreFila : ObservableObject, IFilaConArchivo, IFilaMarcable
{
    public string Ruta { get; }
    public string Actual { get; }
    public string Carpeta { get; }
    public IReadOnlyList<string> ListaAvisos { get; }

    public NombreFila(PropuestaNombre p)
    {
        Ruta = p.Ruta;
        Actual = p.Actual;
        Carpeta = Path.GetFileName(Path.GetDirectoryName(p.Ruta) ?? "");
        ListaAvisos = p.Avisos;
        _propuesto = p.Propuesto;
    }

    /// <summary>Editable: si la propuesta está casi bien, se corrige aquí antes de aceptarla.</summary>
    [ObservableProperty] private string _propuesto;

    /// <summary>Todo sale desmarcado: medido, la propuesta es buena en unas 3 de cada 4.</summary>
    [ObservableProperty] private bool _aceptar;
    string IFilaConArchivo.RutaArchivo => Ruta;
    bool IFilaMarcable.Marcada { get => Aceptar; set => Aceptar = value; }

    public bool HayAvisos => ListaAvisos.Count > 0;
    public string Avisos => string.Join(" ", ListaAvisos.Select(a => "⚠ " + a));
}

public sealed partial class RenombrarIaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;

    /// <summary>Lo revisado en esta sesión y no renombrado: la siguiente pasada sigue por donde iba.</summary>
    private readonly RevisadasEnSesion _revisadas = new();

    public RenombrarIaViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        FilasView = new Avalonia.Collections.DataGridCollectionView(Filas);
        RellenarAmbitos();
    }

    public ObservableCollection<AmbitoOpcion> Ambitos { get; } = new();
    [ObservableProperty] private AmbitoOpcion? _ambito = AmbitoOpcion.TodaLaBiblioteca;

    public IReadOnlyList<int> Limites { get; } = new[] { 50, 200, 1000, 5000 };
    [ObservableProperty] private int _limite = 200;

    public ObservableCollection<NombreFila> Filas { get; } = new();
    public Avalonia.Collections.DataGridCollectionView FilasView { get; }

    [ObservableProperty] private bool _soloSinAvisos;
    [ObservableProperty] private string _resumen = "";

    partial void OnSoloSinAvisosChanged(bool value)
    {
        FilasView.Filter = value ? o => o is NombreFila f && !f.HayAvisos : null;
        FilasView.Refresh();
    }

    public void RellenarAmbitos()
        => Ambito = SelectorAmbito.Rellenar(Ambitos, _p.Engine.Colecciones.Todas, conTodaLaBiblioteca: true, Ambito);

    [RelayCommand]
    private Task ProponerAsync() => _p.TrabajarAsync("Buscando nombres sucios…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }
        if (!_p.IaDisponible) { _p.Status = "Esta herramienta necesita la IA local. " + _p.EstadoIa; return; }
        if (!SelectorAmbito.Resolver(Ambito, _p.Engine.Colecciones.Todas, out var coleccion, out var error))
        {
            _p.Status = error;
            _p.RellenarAmbitos();
            return;
        }

        var (candidatas, saltadas) = await _p.ElegirCandidatasAsync(
            coleccion, (t, _) => RenombradoIa.PareceSucio(t.FilePath), _revisadas, Limite, ct);

        // Las que marcaste y aún no has renombrado no se pierden por pedir más.
        var conservadas = Filas.Where(f => f.Aceptar).ToList();
        Filas.Clear();
        foreach (var f in conservadas) Filas.Add(f);

        if (candidatas.Count == 0)
        {
            Resumen = "";
            _p.Status = saltadas > 0
                ? $"No quedan nombres sucios sin revisar en el ámbito: {saltadas} ya se revisaron en esta sesión. «Empezar desde el principio» los vuelve a incluir."
                : "No hay nombres sucios en el ámbito elegido.";
            return;
        }

        _p.Engine.Ai.Reset();   // por si se desactivó sola en una tirada anterior
        var descartes = new Dictionary<string, int>();
        var revisadas = 0;
        var motivoCorte = "";
        for (var i = 0; i < candidatas.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = candidatas[i];
            var ia = await _p.Engine.Ai.ParseAsync(Path.GetFileNameWithoutExtension(t.FilePath), t.Artist ?? "", t.Title ?? "", _p.Modelo, ct);

            // La IA se ha desactivado (Ollama caído, modelo sin descargar, tiempos agotados): seguir solo
            // apuntaría «la IA no respondió» en el resto. Se para y se dice por qué; esta canción y las
            // siguientes siguen pendientes para la próxima pasada.
            if (ia == null && _p.Engine.Ai.AiBlocked)
            {
                motivoCorte = _p.Engine.Ai.MotivoBloqueo;
                break;
            }

            revisadas++;
            _revisadas.Anotar(t.FilePath);
            var (propuesta, por) = RenombradoIa.Evaluar(t.FilePath, t.Artist ?? "", t.Title ?? "", ia);
            if (propuesta != null) Filas.Add(new NombreFila(propuesta));
            else descartes[por] = descartes.GetValueOrDefault(por) + 1;

            _p.Progress = 100.0 * (i + 1) / candidatas.Count;
            _p.Status = $"Consultando a la IA… {i + 1} de {candidatas.Count}";
        }
        if (motivoCorte.Length > 0) _p.IaPerdida(motivoCorte);

        var nuevas = Filas.Count - conservadas.Count;
        var principales = string.Join(" · ", descartes.OrderByDescending(d => d.Value).Take(3).Select(d => $"{d.Value} {d.Key.TrimEnd('.').ToLowerInvariant()}"));
        Resumen = $"{nuevas} propuestas de {revisadas} nombres sucios · {Filas.Count(f => f.HayAvisos)} con avisos"
                  + (saltadas > 0 ? $" · {saltadas} ya revisados en esta sesión se han saltado" : "");
        var texto = nuevas == 0
            ? $"Ninguna propuesta pasó las comprobaciones. {principales}"
            : $"{Resumen}. Descartadas: {principales}.";
        _p.Status = motivoCorte.Length > 0 ? $"La IA local se ha desactivado: {motivoCorte} {texto}" : texto;
    });

    /// <summary>Vuelve a incluir lo ya revisado en esta sesión.</summary>
    [RelayCommand]
    private void EmpezarDeNuevo()
    {
        _revisadas.Vaciar();
        _p.Status = "La próxima búsqueda empezará desde el principio del ámbito.";
    }

    [RelayCommand]
    private void MarcarTodas() { foreach (var f in FilasView.Cast<NombreFila>()) f.Aceptar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Filas) f.Aceptar = false; }

    public IReadOnlyList<NombreFila> Marcadas => Filas.Where(f => f.Aceptar && f.Propuesto.Trim().Length > 0).ToList();

    /// <summary>Renombra las marcadas. La confirmación la pide la vista.</summary>
    public Task RenombrarAsync() => _p.TrabajarAsync("Renombrando…", async ct =>
    {
        var marcadas = Marcadas;
        if (marcadas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        _p.Engine.ReleaseAudio();
        var cambios = marcadas.Select(f => (f.Ruta, f.Propuesto.Trim())).ToList();
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => _p.Engine.RenombrarArchivos(cambios, progreso, ct));

        var hechos = new HashSet<string>(res.Hechos.Select(h => h.Origen), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Filas.Where(f => hechos.Contains(f.Ruta)).ToList()) Filas.Remove(f);
        if (res.Hechos.Count > 0) await _p.Engine.Library.ScanAsync();
        if (res.Hechos.Count > 0) _p.Engine.AvisarFichasCambiadas();

        var texto = $"{res.Hechos.Count} archivos renombrados";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no ({res.Fallos[0]})";
        if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ El cambio NO quedó anotado y no se podrá deshacer: {res.UndoErr}"
            : res.Hechos.Count > 0 ? ". Se puede deshacer desde Enriquecer; si usas rekordbox, repara la colección en Ajustes." : ".";
        _p.Status = texto;
    });
}

// =====================================================================================================
// 6. Completar nombres cortados
// =====================================================================================================

public sealed partial class CortadoFila : ObservableObject, IFilaConArchivo, IFilaMarcable
{
    public Completado Completado { get; }
    public CortadoFila(Completado c)
    {
        Completado = c;
        _propuesto = c.Propuesto;
        // Lo que sale de las ETIQUETAS del propio archivo se marca: no lo ha inventado nadie, ya
        // estaba dentro del archivo. Lo de la IA, no.
        _aceptar = c.Origen == OrigenCompletado.Etiquetas;
    }

    [ObservableProperty] private string _propuesto;
    [ObservableProperty] private bool _aceptar;
    string IFilaConArchivo.RutaArchivo => Ruta;
    bool IFilaMarcable.Marcada { get => Aceptar; set => Aceptar = value; }

    /// <summary>
    /// Escribir el nombre a mano en una fila sin propuesta la marca: si lo has escrito tú, es que lo
    /// quieres. Y si lo vacías, se desmarca, porque sin nombre no hay nada que renombrar.
    /// </summary>
    partial void OnPropuestoChanged(string value)
    {
        if (Completado.Origen != OrigenCompletado.SinPropuesta) return;
        var cambia = value.Trim().Length > 0 && !string.Equals(value.Trim(), Actual, StringComparison.Ordinal);
        if (Aceptar != cambia) Aceptar = cambia;
    }

    public bool TienePropuesta => Completado.Origen != OrigenCompletado.SinPropuesta;
    public string Ruta => Completado.Ruta;
    public string Actual => Completado.Actual;
    public string Detalle => Completado.Detalle;
    public string Origen => TextoOrigen(Completado.Origen);

    /// <summary>
    /// De dónde sale la propuesta, en la columna. Las de la IA empiezan siempre por «IA»: la
    /// confirmación de «Completar las marcadas» las cuenta así.
    ///
    /// Deezer solo dice que la CANCIÓN existe, no que el nombre completo sea ese: lo que falta suele
    /// ser el editor o la versión, que ningún catálogo conoce. Antes la fila decía «IA, confirmada en el
    /// catálogo», como si el nombre estuviera comprobado. Visto en los registros reales: en una tanda de
    /// 27 nombres completados, 4 quedaron peor que antes («Bzrp 48 x Aprovecha ((97-90 Bpm) (Try It
    /// Mashup))»).
    /// </summary>
    internal static string TextoOrigen(OrigenCompletado origen) => origen switch
    {
        OrigenCompletado.Etiquetas => "Etiquetas del archivo",
        OrigenCompletado.IaConfirmada => "IA (la canción existe en Deezer)",
        OrigenCompletado.IaSinConfirmar => "IA sin confirmar",
        _ => "Sin propuesta: escríbelo tú",
    };
}

public sealed partial class CompletarNombresViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;

    public CompletarNombresViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        RellenarAmbitos();
    }

    public ObservableCollection<AmbitoOpcion> Ambitos { get; } = new();
    [ObservableProperty] private AmbitoOpcion? _ambito = AmbitoOpcion.TodaLaBiblioteca;

    public IReadOnlyList<int> Limites { get; } = new[] { 50, 200, 1000, 5000 };
    [ObservableProperty] private int _limite = 1000;

    /// <summary>Consultar a la IA los que no se arreglan con sus etiquetas. Cuesta ~0,5 s por canción.</summary>
    [ObservableProperty] private bool _usarIa = true;

    public ObservableCollection<CortadoFila> Filas { get; } = new();
    [ObservableProperty] private string _resumen = "";

    public void RellenarAmbitos()
        => Ambito = SelectorAmbito.Rellenar(Ambitos, _p.Engine.Colecciones.Todas, conTodaLaBiblioteca: true, Ambito);

    [RelayCommand]
    private Task BuscarAsync() => _p.TrabajarAsync("Buscando nombres cortados…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }
        if (!SelectorAmbito.Resolver(Ambito, _p.Engine.Colecciones.Todas, out var coleccion, out var error))
        {
            _p.Status = error;
            _p.RellenarAmbitos();
            return;
        }

        // Aquí se enseñan TODOS los cortados (también los que hay que escribir a mano), así que no se
        // salta lo ya visto: no hay «revisadas» como en Proponer fichas o Renombrar.
        var (cortados, _) = await _p.ElegirCandidatasAsync(
            coleccion, (t, _) => NombreCortado.Parece(t.FilePath, t.Artist ?? "", t.Title ?? ""), null, Limite, ct);

        Filas.Clear();
        if (cortados.Count == 0) { Resumen = ""; _p.Status = "No hay nombres cortados en el ámbito elegido."; return; }

        // Se enseñan TODOS los cortados, no solo los que tienen propuesta. Antes la cifra decía 261
        // y la tabla enseñaba 12: los demás existían pero no había forma de verlos ni de arreglarlos.
        var resultado = new List<Completado>();
        static string Nombre(Track t) => Path.GetFileNameWithoutExtension(t.FilePath);

        // 1) Las etiquetas del propio archivo. Ni IA ni red: es lo que ya está dentro del archivo.
        var paraIa = new List<Track>();
        foreach (var t in cortados)
        {
            var conTags = NombreCortado.DesdeEtiquetas(t.FilePath, t.Artist ?? "", t.Title ?? "");
            if (conTags != null)
                resultado.Add(new Completado(t.FilePath, Nombre(t), conTags, OrigenCompletado.Etiquetas, "Estaba en las etiquetas del archivo"));
            else paraIa.Add(t);
        }
        var conEtiquetas = resultado.Count;

        // 2) Los que llegaron sin etiquetas útiles: la IA, y su propuesta se contrasta con el catálogo.
        var conIa = 0;
        var consultarIa = UsarIa && _p.IaDisponible;
        var motivoCorte = "";
        if (consultarIa && paraIa.Count > 0) _p.Engine.Ai.Reset();
        for (var i = 0; i < paraIa.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = paraIa[i];
            string porQueNo;
            if (!consultarIa)
                porQueNo = motivoCorte.Length > 0 ? "Sus etiquetas no lo completan y la IA local se ha desactivado a mitad"
                         : UsarIa ? "Sus etiquetas no lo completan y la IA local no está disponible"
                                  : "Sus etiquetas no lo completan y la IA está desactivada";
            else
            {
                var ia = await _p.Engine.Ai.ParseAsync(Nombre(t), t.Artist ?? "", t.Title ?? "", _p.Modelo, ct);
                _p.Progress = 100.0 * (i + 1) / paraIa.Count;
                _p.Status = $"Consultando a la IA… {i + 1} de {paraIa.Count}";

                // La IA se ha desactivado sola: el resto se enseña sin propuesta y sin seguir preguntando.
                if (ia == null && _p.Engine.Ai.AiBlocked)
                {
                    motivoCorte = _p.Engine.Ai.MotivoBloqueo;
                    consultarIa = false;
                    resultado.Add(new Completado(t.FilePath, Nombre(t), "", OrigenCompletado.SinPropuesta,
                        "Sus etiquetas no lo completan y la IA local se ha desactivado a mitad"));
                    continue;
                }

                var completo = ia == null ? null : NombreCortado.DesdeIa(t.FilePath, ia.Artist, ia.Title, ia.Version);
                if (completo != null)
                {
                    var (origen, detalle) = await ConfirmarAsync(ia!.Artist, ia.Title, ct);
                    resultado.Add(new Completado(t.FilePath, Nombre(t), completo, origen, detalle));
                    conIa++;
                    continue;
                }
                porQueNo = ia == null
                    ? "Sus etiquetas no lo completan y la IA no respondió"
                    : "Sus etiquetas no lo completan y lo que propuso la IA no continuaba el nombre";
            }
            resultado.Add(new Completado(t.FilePath, Nombre(t), "", OrigenCompletado.SinPropuesta, porQueNo));
        }

        // Primero lo que tiene propuesta, de lo más fiable a lo menos; al final, lo que hay que escribir.
        foreach (var c in resultado.OrderBy(c => c.Origen).ThenBy(c => c.Actual, StringComparer.CurrentCultureIgnoreCase))
            Filas.Add(new CortadoFila(c));

        var sinPropuesta = cortados.Count - conEtiquetas - conIa;
        Resumen = $"{cortados.Count} nombres cortados · {conEtiquetas} se completan con sus etiquetas · {conIa} con la IA"
                + (sinPropuesta > 0 ? $" · {sinPropuesta} sin propuesta" : "");
        if (motivoCorte.Length > 0) _p.IaPerdida(motivoCorte);
        _p.Status = (motivoCorte.Length > 0 ? $"La IA local se ha desactivado: {motivoCorte} " : "")
                  + Resumen + ". Lo que sale de las etiquetas va marcado; lo de la IA, no."
                  + (sinPropuesta > 0 ? " Los que no tienen propuesta van al final: escribe el nombre completo y se marcan solos." : "");
    });

    /// <summary>
    /// ¿Existe esa canción en el catálogo? No decide el nombre —lo cortado suele ser el editor, que
    /// ningún catálogo conoce— pero sí dice si la IA está hablando de una canción real.
    /// </summary>
    private async Task<(OrigenCompletado, string)> ConfirmarAsync(string artista, string titulo, CancellationToken ct)
    {
        if (!_p.Engine.Config.UseDeezer || titulo.Trim().Length == 0)
            return (OrigenCompletado.IaSinConfirmar, "La IA lo completó; no se ha contrastado con ningún catálogo");
        try
        {
            var hit = await _p.Engine.Deezer.SearchAsync(artista, titulo, false, false, 0, false, ct);
            return hit != null
                ? (OrigenCompletado.IaConfirmada, $"Deezer: {hit.Artist} - {hit.Title}")
                : (OrigenCompletado.IaSinConfirmar, "La IA lo completó; Deezer no encuentra esa canción");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (OrigenCompletado.IaSinConfirmar, "La IA lo completó; no se pudo consultar el catálogo"); }
    }

    [RelayCommand]
    // Las filas sin nombre escrito no se marcan: no habría nada con qué renombrarlas.
    private void MarcarTodas() { foreach (var f in Filas) f.Aceptar = f.Propuesto.Trim().Length > 0; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Filas) f.Aceptar = false; }

    public IReadOnlyList<CortadoFila> Marcadas => Filas.Where(f => f.Aceptar && f.Propuesto.Trim().Length > 0
                                                                   && !string.Equals(f.Propuesto.Trim(), f.Actual, StringComparison.Ordinal)).ToList();

    /// <summary>Renombra las marcadas. La confirmación la pide la vista.</summary>
    public Task RenombrarAsync() => _p.TrabajarAsync("Completando nombres…", async ct =>
    {
        var marcadas = Marcadas;
        if (marcadas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        _p.Engine.ReleaseAudio();
        var cambios = marcadas.Select(f => (f.Ruta, f.Propuesto.Trim())).ToList();
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => _p.Engine.RenombrarArchivos(cambios, progreso, ct));

        var hechos = new HashSet<string>(res.Hechos.Select(h => h.Origen), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Filas.Where(f => hechos.Contains(f.Ruta)).ToList()) Filas.Remove(f);
        if (res.Hechos.Count > 0)
        {
            await _p.Engine.Library.ScanAsync();
            _p.Engine.AvisarFichasCambiadas();
        }

        var texto = $"{res.Hechos.Count} nombres completados";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no ({res.Fallos[0]})";
        if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ El cambio NO quedó anotado y no se podrá deshacer: {res.UndoErr}"
            : res.Hechos.Count > 0 ? ". Se puede deshacer desde Enriquecer; si usas rekordbox, repara la colección en Ajustes." : ".";
        _p.Status = texto;
    });
}

// =====================================================================================================
// 4. Ordenar para mezclar
// =====================================================================================================

public sealed class PasoFila : IFilaConArchivo
{
    public PasoMezcla Paso { get; }
    public PasoFila(PasoMezcla p) => Paso = p;
    string IFilaConArchivo.RutaArchivo => Paso.Track.FilePath;

    public int Posicion => Paso.Posicion;
    public string Artist => Paso.Track.Artist ?? "";
    public string Title => Paso.Track.Title ?? Paso.Track.FileName;
    public string Bpm => Paso.Track.Bpm > 0 ? Paso.Track.Bpm.ToString() : "";
    public string Camelot => Paso.Track.KeyCamelot;
    public string Energia => Paso.Ficha?.Energia is > 0 ? Paso.Ficha.Energia.ToString() : "";
    public string Transicion => Paso.Transicion;
    public bool Choca => Paso.Posicion > 1 && Paso.Tono == EncajeTono.Choque;
    public string FilePath => Paso.Track.FilePath;
}

public sealed partial class MezclaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    public const int Maximo = 1000;

    public MezclaViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        RellenarColecciones();
    }

    public ObservableCollection<AmbitoOpcion> Colecciones { get; } = new();
    [ObservableProperty] private AmbitoOpcion? _coleccion;
    public ObservableCollection<PasoFila> Pasos { get; } = new();
    [ObservableProperty] private string _resumen = "";

    public void RellenarColecciones()
        => Coleccion = SelectorAmbito.Rellenar(Colecciones, _p.Engine.Colecciones.Todas, conTodaLaBiblioteca: false, Coleccion);

    [RelayCommand]
    private Task OrdenarAsync() => _p.TrabajarAsync("Ordenando…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }
        if (Coleccion == null) { _p.Status = "Elige una colección. Se crean en Colecciones o con «Buscar con una frase»."; return; }
        if (!SelectorAmbito.Resolver(Coleccion, _p.Engine.Colecciones.Todas, out var c, out var error) || c == null)
        {
            _p.Status = error.Length > 0 ? error : "Elige una colección.";
            _p.RellenarAmbitos();
            return;
        }

        var fichas = _p.FichasActuales();
        var canciones = _p.Engine.Library.Tracks
            .Select(t => (t, fichas.GetValueOrDefault(t.FilePath)))
            .Where(x => FiltroColeccion.Cumple(c, x.t, x.Item2))
            .ToList();
        if (canciones.Count == 0) { Pasos.Clear(); Resumen = ""; _p.Status = "La colección está vacía."; return; }
        if (canciones.Count > Maximo)
        {
            _p.Status = $"La colección tiene {canciones.Count} canciones; para ordenar una sesión el máximo es {Maximo}. Afina sus condiciones.";
            return;
        }

        var orden = await Task.Run(() => OrdenMezcla.Ordenar(canciones.Select(x => (x.t, x.Item2)).ToList()), ct);
        Pasos.Clear();
        foreach (var paso in orden) Pasos.Add(new PasoFila(paso));

        var transiciones = Math.Max(0, orden.Count - 1);
        int N(EncajeTono e) => orden.Skip(1).Count(x => x.Tono == e);
        Resumen = $"{orden.Count} canciones · {N(EncajeTono.Igual) + N(EncajeTono.Compatible)} de {transiciones} transiciones armónicas"
                  + $" · {N(EncajeTono.Choque)} choques de tono · {N(EncajeTono.Desconocido)} sin tonalidad";
        _p.Status = N(EncajeTono.Desconocido) > transiciones / 2
            ? Resumen + ". Muchas canciones no tienen tonalidad en las etiquetas: impórtala de rekordbox en Biblioteca."
            : Resumen + ".";
    });

    public void ExportarM3u(string destino)
    {
        if (Pasos.Count == 0) { _p.Status = "Ordena antes una colección."; return; }
        var err = PlaylistWriter.Write(destino, Pasos.Select(p =>
            new PlaylistItem(p.FilePath, p.Artist, p.Title, p.Paso.Track.DurationSeconds)));
        _p.Status = err.Length > 0 ? "No se pudo guardar la lista: " + err : $"Lista guardada en ese orden: {Path.GetFileName(destino)}";
    }
}
