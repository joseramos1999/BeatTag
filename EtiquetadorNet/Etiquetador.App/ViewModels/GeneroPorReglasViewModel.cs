using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

// =====================================================================================================
// Asistente · Género por carpeta y por artista (idea P9, ampliada con el género por artista). Solo
// reglas, sin IA.
// =====================================================================================================

/// <summary>Una carpeta con canciones sin género, y el género que el usuario le ha asignado.</summary>
public sealed partial class CarpetaGeneroFila : ObservableObject
{
    public CarpetaGeneroFila(CarpetaSinGenero c, string genero, bool conSubcarpetas = false, bool excluida = false)
    {
        Datos = c;
        _genero = genero;
        _excluida = excluida;
        ConSubcarpetas = conSubcarpetas || c.Subcarpetas;
    }

    public CarpetaSinGenero Datos { get; }

    /// <summary>Sus cifras incluyen canciones de subcarpetas (a las que también se aplica su regla).</summary>
    public bool ConSubcarpetas { get; }

    /// <summary>El género de la regla de esta carpeta ("" = sin regla).</summary>
    [ObservableProperty] private string _genero;

    /// <summary>Excluida de la regla de su carpeta madre: sus canciones solo dependen del artista.</summary>
    [ObservableProperty] private bool _excluida;

    partial void OnGeneroChanged(string value) => OnPropertyChanged(nameof(Regla));
    partial void OnExcluidaChanged(bool value) => OnPropertyChanged(nameof(Regla));

    /// <summary>La regla, para la columna «Género asignado».</summary>
    public string Regla => Genero.Length > 0 ? Genero : Excluida ? "Excluida (sin regla de carpeta)" : "Sin asignar";

    public string Ruta => Datos.Ruta;
    public string Nombre => GeneroPorReglas.NombreCarpeta(Datos.Ruta) + (ConSubcarpetas ? " (y subcarpetas)" : "");
    public string Ubicacion => Path.GetDirectoryName(Datos.Ruta.TrimEnd('\\', '/')) ?? "";
    public int SinGenero => Datos.SinGenero;
    public int Canciones => Datos.Total;

    /// <summary>Lo que llevan las que sí tienen género. Es una pista para decidir, no se aplica sola.</summary>
    public string Pista => Datos.Dominante.Length == 0
        ? "Ninguna tiene género"
        : $"{Datos.Dominante} ({Datos.ConDominante} de {Datos.ConGenero} con género)";
}

/// <summary>Un artista de la biblioteca, lo que se sabe de su género y la regla que le ha puesto el usuario.</summary>
public sealed partial class ArtistaGeneroFila : ObservableObject
{
    public ArtistaGeneroFila(ResumenArtista datos, string genero = "", bool excluido = false)
    {
        Datos = datos;
        _genero = genero;
        _excluido = excluido;
    }

    public ResumenArtista Datos { get; }

    /// <summary>El género que le ha asignado el usuario ("" = ninguno).</summary>
    [ObservableProperty] private string _genero;

    /// <summary>Excluido de la regla automática del dominante.</summary>
    [ObservableProperty] private bool _excluido;

    partial void OnGeneroChanged(string value) => OnPropertyChanged(nameof(Regla));
    partial void OnExcluidoChanged(bool value) => OnPropertyChanged(nameof(Regla));

    public string Clave => Datos.Clave;
    public string Nombre => Datos.Nombre;
    public int Canciones => Datos.Canciones;
    public int SinGenero => Datos.SinGenero;

    /// <summary>«Reggaeton 71 · Trap 32 · Latin 10», o lo que corresponda si ya no está en la biblioteca.</summary>
    public string Reparto => Datos.Canciones == 0 ? "Ya no está en la biblioteca"
                           : Datos.Reparto.Count == 0 ? "Ninguna tiene género"
                           : GeneroPorReglas.TextoReparto(Datos.Reparto);

    /// <summary>Lo que propone la regla automática: «Reggaeton» o «Reggaeton (fuerte)».</summary>
    public string Sugerencia => Datos.Sugerencia.Length == 0 ? "" : Datos.Fuerte ? Datos.Sugerencia + " (fuerte)" : Datos.Sugerencia;

    public bool TieneSugerencia => Datos.Sugerencia.Length > 0;

    /// <summary>La regla, para la columna «Tu regla».</summary>
    public string Regla => Genero.Length > 0 ? Genero : Excluido ? "Excluido de la regla automática" : "Sin asignar";
}

/// <summary>Un género propuesto para una canción.</summary>
public sealed partial class GeneroReglaFila : ObservableObject, IFilaConArchivo, IFilaMarcable
{
    public PropuestaGeneroRegla Propuesta { get; }
    public Track Track { get; }

    public GeneroReglaFila(PropuestaGeneroRegla p, Track t)
    {
        Propuesta = p;
        Track = t;
        _aplicar = p.Marcada;
        _genero = p.Genero;
    }

    [ObservableProperty] private bool _aplicar;

    /// <summary>Editable: si el género propuesto no es el que quieres, se corrige aquí.</summary>
    [ObservableProperty] private string _genero;

    string IFilaConArchivo.RutaArchivo => Track.FilePath;
    bool IFilaMarcable.Marcada { get => Aplicar; set => Aplicar = value; }

    public string Ruta => Track.FilePath;
    public string Artist => Track.Artist ?? "";
    public string Title => Track.Title ?? Track.FileName;

    /// <summary>El género que tenía al proponer.</summary>
    public string Ahora => Propuesta.GeneroActual.Trim();

    public string Cambio => TextoCambio(Propuesta.Cambio);
    public string Regla => TextoRegla(Propuesta.Regla);
    public string PorQue => Propuesta.PorQue;
    public string Carpeta => GeneroPorReglas.NombreCarpeta(Path.GetDirectoryName(Track.FilePath) ?? "");

    internal static string TextoRegla(ReglaGenero r) => r switch
    {
        ReglaGenero.Carpeta => "Carpeta",
        ReglaGenero.Version => "Versión",
        ReglaGenero.MapaArtista => "Tu regla de artista",
        ReglaGenero.Deezer => "Deezer",
        _ => "Artista",
    };

    internal static string TextoCambio(CambioGenero c) => c switch
    {
        CambioGenero.SustituirGenerico => "Sustituir genérico",
        CambioGenero.Revisar => "Sustituir estilo",
        _ => "Rellenar",
    };
}

public sealed partial class GeneroPorReglasViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    private MapaGenerosCarpeta? _mapa;
    private MapaGenerosArtista? _mapaArtistas;

    public GeneroPorReglasViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        _propuestasVista = new DataGridCollectionView(_propuestas);
        _artistasVista = new DataGridCollectionView(_artistas);
    }

    [ObservableProperty] private ObservableCollection<CarpetaGeneroFila> _carpetas = new();

    /// <summary>Se sustituye entera al proponer, como en «Limpiar la publicidad de los packs».</summary>
    [ObservableProperty] private ObservableCollection<GeneroReglaFila> _propuestas = new();

    /// <summary>Las propuestas con el filtro de «Mostrar».</summary>
    [ObservableProperty] private DataGridCollectionView _propuestasVista;

    /// <summary>Los artistas de la biblioteca, de más a menos canciones, y los que tienen regla aunque ya no estén.</summary>
    [ObservableProperty] private ObservableCollection<ArtistaGeneroFila> _artistas = new();

    /// <summary>Los artistas con el filtro de búsqueda y de vista.</summary>
    [ObservableProperty] private DataGridCollectionView _artistasVista;

    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _resumenCarpetas = "";
    [ObservableProperty] private string _resumenArtistas = "Pulsa «Proponer géneros» para ver los artistas de la biblioteca.";

    /// <summary>Has cambiado reglas o el alcance y las propuestas aún no lo reflejan.</summary>
    [ObservableProperty] private bool _reglasSinAplicar;

    /// <summary>
    /// Los géneros que ya usa la biblioteca, normalizados y de más a menos canciones: el desplegable de
    /// la regla. Se puede escribir otro.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<string> _generosDisponibles = Array.Empty<string>();

    /// <summary>El género elegido en el desplegable, para ponerlo a las carpetas o los artistas seleccionados.</summary>
    [ObservableProperty] private string _generoElegido = "";

    /// <summary>A qué canciones se proponen géneros. No se guarda: cada sesión empieza con «Todas».</summary>
    public IReadOnlyList<string> Alcances { get; } = new[]
    {
        "Solo canciones sin género",
        "Sin género o con un género genérico",
        "Todas (las de otro estilo, para revisar)",
    };

    [ObservableProperty] private int _alcanceIndice = (int)AlcanceGenero.Todas;

    partial void OnAlcanceIndiceChanged(int value) { if (Propuestas.Count > 0 || Resumen.Length > 0) ReglasSinAplicar = true; }

    internal AlcanceGenero Alcance => Enum.IsDefined(typeof(AlcanceGenero), AlcanceIndice) ? (AlcanceGenero)AlcanceIndice : AlcanceGenero.Todas;

    /// <summary>Qué propuestas se ven: todas, las que salieron marcadas o las que salieron desmarcadas.</summary>
    public IReadOnlyList<string> OpcionesMostrar { get; } = new[] { "Todas", "Marcadas", "Para revisar" };

    [ObservableProperty] private int _mostrarIndice;

    partial void OnMostrarIndiceChanged(int value) => FiltrarPropuestas();

    partial void OnPropuestasChanged(ObservableCollection<GeneroReglaFila> value)
    {
        PropuestasVista = new DataGridCollectionView(value);
        FiltrarPropuestas();
    }

    /// <summary>
    /// Por lo que salió al proponer, no por la casilla: si el filtro siguiera a la casilla, la fila
    /// que se marca o desmarca desaparecería de la vista al momento.
    /// </summary>
    private void FiltrarPropuestas()
    {
        PropuestasVista.Filter = MostrarIndice switch
        {
            1 => o => o is GeneroReglaFila f && f.Propuesta.Marcada,
            2 => o => o is GeneroReglaFila f && !f.Propuesta.Marcada,
            _ => null,
        };
        PropuestasVista.Refresh();
    }

    /// <summary>Qué artistas se ven, además de la búsqueda.</summary>
    public IReadOnlyList<string> VistasArtistas { get; } = new[] { "Todos", "Con sugerencia", "Con tu regla", "Excluidos" };

    [ObservableProperty] private int _vistaArtistasIndice;
    [ObservableProperty] private string _filtroArtista = "";

    partial void OnVistaArtistasIndiceChanged(int value) => FiltrarArtistas();
    partial void OnFiltroArtistaChanged(string value) => FiltrarArtistas();

    partial void OnArtistasChanged(ObservableCollection<ArtistaGeneroFila> value)
    {
        ArtistasVista = new DataGridCollectionView(value);
        FiltrarArtistas();
    }

    /// <summary>
    /// Una vista sobre los artistas ya calculados (unos pocos miles de filas), no sobre las canciones:
    /// filtrar no recorre la biblioteca.
    /// </summary>
    private void FiltrarArtistas()
    {
        var q = (FiltroArtista ?? "").Trim();
        Func<ArtistaGeneroFila, bool> porVista = VistaArtistasIndice switch
        {
            1 => f => f.TieneSugerencia,
            2 => f => f.Genero.Length > 0,
            3 => f => f.Excluido,
            _ => _ => true,
        };
        ArtistasVista.Filter = q.Length == 0 && VistaArtistasIndice == 0
            ? null
            : o => o is ArtistaGeneroFila f && porVista(f) && BusquedaTexto.Coincide(q, f.Nombre);
        ArtistasVista.Refresh();
    }

    /// <summary>Lo que pasó al leer los mapas (apartados por ilegibles, bloqueados…), para decirlo una vez en el estado.</summary>
    private string _avisoMapa = "";

    /// <summary>El mapa carpeta → género, leído la primera vez que hace falta.</summary>
    private MapaGenerosCarpeta Mapa
    {
        get
        {
            if (_mapa != null) return _mapa;
            _mapa = new MapaGenerosCarpeta(MapaGenerosCarpeta.RutaEn(_p.Engine.Paths));
            if (_mapa.AvisoCarga.Length > 0)
            {
                _p.Engine.Logger.Err("Género por carpeta: " + _mapa.AvisoCarga);
                _avisoMapa += "⚠ " + _mapa.AvisoCarga + " ";
            }
            return _mapa;
        }
    }

    /// <summary>El mapa artista → género, leído la primera vez que hace falta.</summary>
    private MapaGenerosArtista MapaArtistas
    {
        get
        {
            if (_mapaArtistas != null) return _mapaArtistas;
            _mapaArtistas = new MapaGenerosArtista(MapaGenerosArtista.RutaEn(_p.Engine.Paths));
            if (_mapaArtistas.AvisoCarga.Length > 0)
            {
                _p.Engine.Logger.Err("Género por artista: " + _mapaArtistas.AvisoCarga);
                _avisoMapa += "⚠ " + _mapaArtistas.AvisoCarga + " ";
            }
            return _mapaArtistas;
        }
    }

    /// <summary>El aviso de lectura de los mapas, la primera vez que se pide; después, nada.</summary>
    private string AvisoMapaUnaVez()
    {
        var aviso = _avisoMapa;
        _avisoMapa = "";
        return aviso;
    }

    [RelayCommand]
    private Task ProponerAsync() => _p.TrabajarAsync("Buscando qué género corresponde a cada canción…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var canciones = _p.Engine.Library.Tracks.ToList();   // instantánea en el hilo de la interfaz
        var raices = _p.Engine.Library.Folders.Select(f => f.Path).ToList();
        var reglas = new Dictionary<string, string>(Mapa.Reglas, StringComparer.OrdinalIgnoreCase);
        var conRegla = Mapa.ConGenero;
        var excluidas = reglas.Count - conRegla;
        var reglasArtistas = MapaArtistas.Reglas.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        var alcance = Alcance;
        var calculo = await Task.Run(() =>
        {
            var generos = GeneroPorReglas.GenerosDeLaBiblioteca(canciones).Select(g => g.Genero).ToList();
            // Lo escrito en los mapas, como lo escribe la biblioteca («reguetón» → «Reggaeton»). Las
            // carpetas y los artistas excluidos ("") siguen vacíos.
            var mapa = reglas.ToDictionary(r => r.Key, r => GeneroPorReglas.ComoEnLaBiblioteca(r.Value, generos), StringComparer.OrdinalIgnoreCase);
            var porArtista = reglasArtistas.ToDictionary(r => r.Key, r => GeneroPorReglas.ComoEnLaBiblioteca(r.Value.Genero, generos), StringComparer.Ordinal);
            var carpetas = GeneroPorReglas.Carpetas(canciones, raices);
            var conocidas = new HashSet<string>(carpetas.Select(c => c.Ruta), StringComparer.OrdinalIgnoreCase);
            // Las carpetas con regla que no salen en la lista (ya no les falta género, o se añadieron a
            // mano por sus subcarpetas) también se enseñan: si no, no habría forma de quitarles la regla.
            var extra = reglas.Keys.Where(r => !conocidas.Contains(r)).Select(r => GeneroPorReglas.ConSubcarpetas(canciones, r)).ToList();
            var artistas = GeneroPorReglas.ResumenArtistas(canciones, ct);
            // Lo mismo con los artistas con regla que ya no están en la biblioteca.
            var vistos = new HashSet<string>(artistas.Select(a => a.Clave), StringComparer.Ordinal);
            artistas.AddRange(reglasArtistas.Where(r => !vistos.Contains(r.Key))
                .Select(r => new ResumenArtista(r.Key, r.Value.Nombre, 0, 0, Array.Empty<RepartoGenero>(), "", 0, 0, false)));
            var propuestas = GeneroPorReglas.Calcular(canciones, new OpcionesGeneroReglas(mapa, porArtista, alcance), ct);
            var sinGenero = canciones.Count(GeneroPorReglas.SinGenero);
            return (generos, carpetas, extra, artistas, propuestas, sinGenero, Variantes: GeneroPorReglas.VariantesSinUnificar(canciones));
        }, ct);

        GenerosDisponibles = calculo.generos;
        Carpetas = new ObservableCollection<CarpetaGeneroFila>(
            calculo.carpetas.Select(c => Fila(c, false)).Concat(calculo.extra.Select(c => Fila(c, true))));
        Artistas = new ObservableCollection<ArtistaGeneroFila>(calculo.artistas.Select(FilaArtista));
        ResumenArtistas = TextoArtistas(calculo.artistas.Count(a => a.Canciones > 0), MapaArtistas.ConGenero, MapaArtistas.Excluidos);

        var porRuta = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var t in canciones) porRuta.TryAdd(t.FilePath, t);
        // Lo que ya dio Deezer en esta sesión se conserva para las canciones que siguen sin género ni regla.
        var conPropuesta = new HashSet<string>(calculo.propuestas.Propuestas.Select(x => x.Ruta), StringComparer.Ordinal);
        var deDeezer = _deezer.Values.Where(x => !conPropuesta.Contains(x.Ruta) && porRuta.TryGetValue(x.Ruta, out var t) && GeneroPorReglas.SinGenero(t)).ToList();
        Propuestas = new ObservableCollection<GeneroReglaFila>(Filas(calculo.propuestas.Propuestas.Concat(deDeezer), porRuta, Propuestas));
        ReglasSinAplicar = false;

        var p = calculo.propuestas;
        var marcadas = p.Propuestas.Count(x => x.Marcada);
        var paraRevisar = p.Propuestas.Count - marcadas + deDeezer.Count;
        ResumenCarpetas = TextoCarpetas(calculo.carpetas.Count, conRegla, excluidas);
        Resumen = TextoResumen(calculo.sinGenero, marcadas, paraRevisar, p.Protegidas);
        _p.Status = AvisoMapaUnaVez()
                    + Resumen + ". "
                    + (p.Propuestas.Count == 0 ? "Ninguna regla propone un género con el alcance elegido." : Consejo(conRegla + MapaArtistas.ConGenero, paraRevisar))
                    + TextoListas(p.Listas)
                    + (calculo.Variantes > 0
                        ? (calculo.Variantes == 1
                            ? " Hay 1 género escrito de varias formas o que no es un género"
                            : $" Hay {calculo.Variantes:N0} géneros escritos de varias formas o que no son un género")
                          + ": pasa antes «Unificar géneros» para que las cuentas por artista sean más fiables."
                        : "");
    });

    // =================================================================================================
    // Deezer, para lo que las reglas no cubren
    // =================================================================================================

    /// <summary>Lo que ha propuesto Deezer en esta sesión, por ruta: se conserva al volver a proponer.</summary>
    private readonly Dictionary<string, PropuestaGeneroRegla> _deezer = new(StringComparer.Ordinal);

    /// <summary>
    /// Busca en Deezer las canciones sin género que siguen sin propuesta y que no son mashups, y
    /// propone el subgénero concreto de su álbum, desmarcado (ver <see cref="GeneroDeezer"/>). Va por
    /// internet, con avance y cancelable; las consultas quedan en la caché de 30 días.
    /// </summary>
    [RelayCommand]
    private Task BuscarEnDeezerAsync() => _p.TrabajarAsync("Buscando en Deezer…", async ct =>
    {
        if (!_p.Engine.Config.UseDeezer)
        {
            _p.Status = "Deezer está desactivado entre las fuentes de Ajustes: actívalo para usar esta búsqueda.";
            return;
        }
        if (Resumen.Length == 0)
        {
            _p.Status = "Pulsa antes «Proponer géneros»: en Deezer solo se buscan las canciones que las reglas no cubren.";
            return;
        }
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var canciones = _p.Engine.Library.Tracks.ToList();
        var conPropuesta = Propuestas.Select(f => f.Ruta).ToList();
        var candidatas = await Task.Run(() => GeneroDeezer.Candidatas(canciones, conPropuesta), ct);
        if (candidatas.Count == 0)
        {
            _p.Status = "No queda ninguna canción sin género y sin propuesta que no sea un mashup.";
            return;
        }

        var progreso = new Progress<(int Hechas, int Total)>(x =>
        {
            if (!_p.IsBusy) return;
            _p.Progress = x.Total > 0 ? 100.0 * x.Hechas / x.Total : 0;
            _p.Status = $"Buscando en Deezer: {x.Hechas:N0} de {x.Total:N0}…";
        });
        var nuevas = await Task.Run(() => GeneroDeezer.BuscarAsync(candidatas, _p.Engine.Deezer, progreso, ct), ct);

        foreach (var n in nuevas) _deezer[n.Ruta] = n;
        var porRuta = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var t in canciones) porRuta.TryAdd(t.FilePath, t);
        var nuevasRutas = new HashSet<string>(nuevas.Select(n => n.Ruta), StringComparer.Ordinal);
        var quedan = Propuestas.Where(f => !nuevasRutas.Contains(f.Ruta)).Select(f => f.Propuesta);
        Propuestas = new ObservableCollection<GeneroReglaFila>(Filas(quedan.Concat(nuevas), porRuta, Propuestas));
        _p.Status = TextoDeezer(candidatas.Count, nuevas.Count);
    });

    internal static string TextoDeezer(int buscadas, int propuestas)
        => $"Buscadas en Deezer {EscrituraPorReglas.Cuenta(buscadas, "canción", "canciones")}: "
           + (propuestas == 0
               ? "ninguna propuesta, porque sus álbumes solo traen géneros que no distinguen estilos."
               : propuestas == 1
                   ? "1 propuesta con un subgénero concreto del álbum, desmarcada para que la revises."
                   : $"{propuestas:N0} propuestas con un subgénero concreto del álbum, desmarcadas para que las revises.");

    /// <summary>
    /// Las filas de las propuestas: primero las marcadas, luego por regla y por ruta. Lo que ya habías
    /// marcado, desmarcado o corregido en una propuesta que sigue igual (la misma canción y el mismo
    /// género) se conserva.
    /// </summary>
    internal static List<GeneroReglaFila> Filas(IEnumerable<PropuestaGeneroRegla> propuestas, IReadOnlyDictionary<string, Track> porRuta,
                                               IEnumerable<GeneroReglaFila> antes)
    {
        var previas = new Dictionary<string, GeneroReglaFila>(StringComparer.Ordinal);
        foreach (var f in antes) previas.TryAdd(f.Ruta + "|" + f.Propuesta.Genero, f);
        var filas = new List<GeneroReglaFila>();
        foreach (var p in propuestas.OrderByDescending(p => p.Marcada).ThenBy(p => p.Regla)
                                    .ThenBy(p => p.Ruta, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!porRuta.TryGetValue(p.Ruta, out var t)) continue;
            var fila = new GeneroReglaFila(p, t);
            if (previas.TryGetValue(p.Ruta + "|" + p.Genero, out var vieja))
            {
                fila.Aplicar = vieja.Aplicar;
                fila.Genero = vieja.Genero;
            }
            filas.Add(fila);
        }
        return filas;
    }

    internal static string TextoCarpetas(int carpetas, int conRegla, int excluidas)
        => $"{EscrituraPorReglas.Cuenta(carpetas, "carpeta con canciones sin género", "carpetas con canciones sin género")} · {conRegla:N0} con regla"
           + (excluidas > 0 ? $" · {excluidas:N0} {(excluidas == 1 ? "excluida" : "excluidas")}" : "");

    internal static string TextoArtistas(int artistas, int conRegla, int excluidos)
        => $"{EscrituraPorReglas.Cuenta(artistas, "artista", "artistas")} · {conRegla:N0} con tu regla"
           + (excluidos > 0 ? $" · {excluidos:N0} {(excluidos == 1 ? "excluido" : "excluidos")}" : "");

    /// <summary>«2.640 canciones sin género · 314 propuestas marcadas · 890 para revisar · 250 protegidas (mashups y remixes firmados)».</summary>
    internal static string TextoResumen(int sinGenero, int marcadas, int paraRevisar, int protegidas)
        => $"{EscrituraPorReglas.Cuenta(sinGenero, "canción sin género", "canciones sin género")} · "
           + $"{EscrituraPorReglas.Cuenta(marcadas, "propuesta marcada", "propuestas marcadas")} · {paraRevisar:N0} para revisar"
           + (protegidas > 0
               ? " · " + EscrituraPorReglas.Cuenta(protegidas, "protegida (mashup o remix firmado)", "protegidas (mashups y remixes firmados)")
               : "");

    internal static string Consejo(int reglas, int paraRevisar)
        => reglas == 0
            ? "Empieza por las carpetas o los artistas con más canciones: selecciónalos en su pestaña, elige su género y ponlo con el botón de al lado."
            : paraRevisar > 0
                ? "Las propuestas para canciones que ya tienen otro estilo, o en las que la regla no es tan segura, salen desmarcadas: revísalas con «Mostrar: Para revisar»."
                : "Revisa las propuestas y aplica las marcadas.";

    /// <summary>Las canciones con varios géneros no se tocan: es cosa de «Unificar géneros».</summary>
    internal static string TextoListas(int listas)
        => listas == 0 ? ""
         : listas == 1 ? " 1 canción tiene varios géneros y no se toca: pásala antes por «Unificar géneros»."
         : $" {listas:N0} canciones tienen varios géneros y no se tocan: pásalas antes por «Unificar géneros».";

    private CarpetaGeneroFila Fila(CarpetaSinGenero c, bool conSubcarpetas) => new(c, Mapa.De(c.Ruta), conSubcarpetas, Mapa.Excluida(c.Ruta));

    private ArtistaGeneroFila FilaArtista(ResumenArtista a)
    {
        var regla = MapaArtistas.Reglas.TryGetValue(a.Clave, out var e) ? e : null;
        return new ArtistaGeneroFila(a, regla?.Genero ?? "", regla is { Genero.Length: 0 });
    }

    /// <summary>
    /// Añade una carpeta que no sale en la lista (por ejemplo, la que agrupa varias): su regla vale para
    /// todas sus subcarpetas, salvo las que tengan la suya. Contar lo que hay dentro va fuera del hilo
    /// de la interfaz: si se elige la carpeta raíz, son todas las canciones de la biblioteca.
    /// </summary>
    public Task AnadirCarpetaAsync(string ruta) => _p.TrabajarAsync("Contando las canciones de la carpeta…", async ct =>
    {
        if (string.IsNullOrWhiteSpace(ruta)) return;
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }
        if (Carpetas.FirstOrDefault(c => Rutas.MismaCarpeta(c.Ruta, ruta)) is { } ya)
        {
            _p.Status = $"«{ya.Nombre}» ya está en la lista.";
            return;
        }
        var canciones = _p.Engine.Library.Tracks.ToList();
        var datos = await Task.Run(() => GeneroPorReglas.ConSubcarpetas(canciones, ruta), ct);
        if (datos.Total == 0)
        {
            _p.Status = "Esa carpeta no tiene canciones de la biblioteca. Elige una de las carpetas que escaneas en Biblioteca.";
            return;
        }
        Carpetas.Insert(0, Fila(datos, true));
        _p.Status = $"«{GeneroPorReglas.NombreCarpeta(ruta)}» añadida al principio de la lista: selecciónala y ponle un género. "
                    + "Vale también para sus subcarpetas que no tengan regla propia.";
    });

    /// <summary>
    /// Pone el género elegido a las carpetas seleccionadas y guarda el mapa. Se escribe como lo escribe
    /// la biblioteca si ya lo usa. Varias a la vez porque es lo habitual: «Mashups Reggaeton1 A-B»,
    /// «… C-E», «… F-L» son el mismo estilo.
    /// </summary>
    public void PonerRegla(IReadOnlyList<CarpetaGeneroFila> carpetas, string? genero)
    {
        if ((genero ?? "").Trim().Length == 0) { _p.Status = "Elige o escribe antes un género en el desplegable."; return; }
        CambiarRegla(carpetas, genero);
    }

    /// <summary>
    /// Quita la regla de las carpetas seleccionadas (o que estén excluidas): vuelve a valer la de su
    /// carpeta madre, si la tiene; si no, sus canciones dependen solo del artista.
    /// </summary>
    public void QuitarRegla(IReadOnlyList<CarpetaGeneroFila> carpetas) => CambiarRegla(carpetas, "");

    /// <summary>
    /// Excluye las carpetas seleccionadas de la regla de su carpeta madre: una «WarmUp» dentro de
    /// «Reggaeton» mezcla estilos y no debe recibir su género. Antes no había forma: dejar el género
    /// vacío borraba la regla, y entonces volvía a valer la de la madre.
    /// </summary>
    public void ExcluirDeLaRegla(IReadOnlyList<CarpetaGeneroFila> carpetas) => CambiarRegla(carpetas, null, excluir: true);

    private void CambiarRegla(IReadOnlyList<CarpetaGeneroFila> carpetas, string? genero, bool excluir = false)
    {
        if (carpetas.Count == 0) { _p.Status = "Selecciona antes una o varias carpetas de la lista."; return; }
        var valor = excluir ? "" : GeneroPorReglas.ComoEnLaBiblioteca(genero, GenerosDisponibles);
        var cambiadas = 0;
        foreach (var c in carpetas)
        {
            if (!(excluir ? Mapa.Excluir(c.Ruta) : Mapa.Poner(c.Ruta, valor))) continue;
            c.Genero = valor;
            c.Excluida = excluir;
            cambiadas++;
        }
        if (cambiadas == 0)
        {
            _p.Status = excluir ? (carpetas.Count == 1 ? "Esa carpeta ya estaba excluida." : "Esas carpetas ya estaban excluidas.")
                      : valor.Length == 0 ? (carpetas.Count == 1 ? "Esa carpeta no tenía regla." : "Esas carpetas no tenían regla.")
                      : carpetas.Count == 1 ? $"Esa carpeta ya tenía «{valor}»." : $"Esas carpetas ya tenían «{valor}».";
            return;
        }

        ReglasSinAplicar = true;
        var err = Mapa.Guardar();
        if (err.Length > 0)
        {
            _p.Engine.Logger.Err("Género por carpeta: no se pudo guardar el mapa: " + err);
            _p.Status = "⚠ La regla se usará en esta sesión, pero no se ha podido guardar: " + err;
            return;
        }
        _p.Status = TextoReglaCambiada(cambiadas, carpetas[0].Nombre, valor, excluir)
                    + " Pulsa «Proponer géneros» para ver a qué canciones se aplica.";
    }

    internal static string TextoReglaCambiada(int cambiadas, string primera, string valor, bool excluir)
    {
        var cuales = cambiadas == 1 ? $"«{primera}»" : $"{cambiadas:N0} carpetas";
        if (excluir)
            return cambiadas == 1
                ? $"{cuales} queda excluida de la regla de su carpeta madre: sus canciones solo dependen del artista."
                : $"{cuales} quedan excluidas de la regla de su carpeta madre: sus canciones solo dependen del artista.";
        return valor.Length == 0 ? $"Regla quitada de {cuales}." : $"{cuales} → {valor}.";
    }

    // =================================================================================================
    // Por artista
    // =================================================================================================

    /// <summary>Qué se hace con los artistas seleccionados.</summary>
    internal enum AccionArtista { Poner, Sugerencia, Quitar, Excluir }

    /// <summary>Pone el género elegido a los artistas seleccionados («Anuel AA» → Reggaeton).</summary>
    public void PonerReglaArtista(IReadOnlyList<ArtistaGeneroFila> artistas, string? genero)
    {
        if ((genero ?? "").Trim().Length == 0) { _p.Status = "Elige o escribe antes un género en el desplegable."; return; }
        var valor = GeneroPorReglas.ComoEnLaBiblioteca(genero, GenerosDisponibles);
        CambiarReglaArtista(artistas, AccionArtista.Poner, _ => valor);
    }

    /// <summary>Pone a cada artista seleccionado su sugerencia, si la tiene. A los que no, nada.</summary>
    public void UsarSugerencia(IReadOnlyList<ArtistaGeneroFila> artistas)
        => CambiarReglaArtista(artistas, AccionArtista.Sugerencia, f => f.Datos.Sugerencia);

    /// <summary>Quita la regla (o la exclusión) de los artistas seleccionados: vuelve a valer la automática.</summary>
    public void QuitarReglaArtista(IReadOnlyList<ArtistaGeneroFila> artistas)
        => CambiarReglaArtista(artistas, AccionArtista.Quitar, _ => "");

    /// <summary>
    /// Excluye a los artistas seleccionados de la regla automática: los que de verdad mezclan estilos
    /// y no quieres asignar a uno solo. Sus canciones siguen recibiendo el género de su carpeta o el
    /// que diga la versión.
    /// </summary>
    public void ExcluirArtista(IReadOnlyList<ArtistaGeneroFila> artistas)
        => CambiarReglaArtista(artistas, AccionArtista.Excluir, _ => "");

    private void CambiarReglaArtista(IReadOnlyList<ArtistaGeneroFila> artistas, AccionArtista accion, Func<ArtistaGeneroFila, string> genero)
    {
        if (artistas.Count == 0) { _p.Status = "Selecciona antes uno o varios artistas de la lista."; return; }
        var cambiados = Aplicar(MapaArtistas, artistas, accion, genero);
        if (cambiados.Count == 0)
        {
            _p.Status = TextoSinCambios(artistas, accion);
            return;
        }

        ReglasSinAplicar = true;
        ResumenArtistas = TextoArtistas(Artistas.Count(a => a.Canciones > 0), MapaArtistas.ConGenero, MapaArtistas.Excluidos);
        var err = MapaArtistas.Guardar();
        if (err.Length > 0)
        {
            _p.Engine.Logger.Err("Género por artista: no se pudo guardar el mapa: " + err);
            _p.Status = "⚠ La regla se usará en esta sesión, pero no se ha podido guardar: " + err;
            return;
        }
        _p.Status = TextoReglaArtista(cambiados.Count, cambiados[0].Nombre, cambiados[0].Genero, accion)
                    + " Pulsa «Proponer géneros» para ver a qué canciones se aplica.";
    }

    /// <summary>
    /// Lo que cambia en el mapa y en las filas, sin guardar. Devuelve las filas que cambiaron. Separado
    /// para probarlo sin la ventana.
    /// </summary>
    internal static List<ArtistaGeneroFila> Aplicar(MapaGenerosArtista mapa, IReadOnlyList<ArtistaGeneroFila> artistas, AccionArtista accion,
                                                   Func<ArtistaGeneroFila, string> genero)
    {
        var cambiados = new List<ArtistaGeneroFila>();
        foreach (var f in artistas)
        {
            var valor = (genero(f) ?? "").Trim();
            var cambia = accion switch
            {
                AccionArtista.Excluir => mapa.Excluir(f.Clave, f.Nombre),
                AccionArtista.Quitar => mapa.Quitar(f.Clave),
                _ => valor.Length > 0 && mapa.Poner(f.Clave, f.Nombre, valor),
            };
            if (!cambia) continue;
            f.Genero = accion is AccionArtista.Poner or AccionArtista.Sugerencia ? valor : "";
            f.Excluido = accion == AccionArtista.Excluir;
            cambiados.Add(f);
        }
        return cambiados;
    }

    internal static string TextoReglaArtista(int cambiados, string primero, string valor, AccionArtista accion)
    {
        var cuales = cambiados == 1 ? $"«{primero}»" : $"{cambiados:N0} artistas";
        return accion switch
        {
            AccionArtista.Excluir => cambiados == 1
                ? $"{cuales} queda excluido de la regla automática: sus canciones solo reciben el género de su carpeta o el que indique la versión."
                : $"{cuales} quedan excluidos de la regla automática: sus canciones solo reciben el género de su carpeta o el que indique la versión.",
            AccionArtista.Quitar => $"Regla quitada de {cuales}: vuelve a valer la regla automática.",
            AccionArtista.Sugerencia => cambiados == 1 ? $"{cuales} → {valor}, su sugerencia." : $"Sugerencia puesta a {cuales}.",
            _ => $"{cuales} → {valor}.",
        };
    }

    internal static string TextoSinCambios(IReadOnlyList<ArtistaGeneroFila> artistas, AccionArtista accion)
    {
        var uno = artistas.Count == 1;
        return accion switch
        {
            AccionArtista.Excluir => uno ? "Ese artista ya estaba excluido." : "Esos artistas ya estaban excluidos.",
            AccionArtista.Quitar => uno ? "Ese artista no tenía regla." : "Esos artistas no tenían regla.",
            AccionArtista.Sugerencia when artistas.All(a => !a.TieneSugerencia) => uno
                ? "Ese artista no tiene sugerencia: sus canciones no muestran un estilo claro. Elige tú su género."
                : "Ninguno de esos artistas tiene sugerencia: sus canciones no muestran un estilo claro. Elige tú su género.",
            _ => uno ? "Ese artista ya tenía ese género." : "Esos artistas ya tenían ese género.",
        };
    }

    // =================================================================================================
    // Aplicar
    // =================================================================================================

    /// <summary>Marca las propuestas que se ven con el filtro de «Mostrar».</summary>
    [RelayCommand]
    private void MarcarTodas() { foreach (var f in PropuestasVista.Cast<GeneroReglaFila>()) f.Aplicar = f.Genero.Trim().Length > 0; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Propuestas) f.Aplicar = false; }

    public IReadOnlyList<GeneroReglaFila> Marcadas => Propuestas.Where(f => f.Aplicar && f.Genero.Trim().Length > 0).ToList();

    /// <summary>Los textos de la confirmación de «Aplicar las marcadas»: cuántas, de qué tipo, y lo que hay que saber.</summary>
    internal static (string Cuerpo, string Nota) Confirmacion(IReadOnlyList<GeneroReglaFila> marcadas)
    {
        int Con(CambioGenero c) => marcadas.Count(f => f.Propuesta.Cambio == c);
        var partes = new[]
            {
                (N: Con(CambioGenero.Rellenar), Texto: "sin género"),
                (N: Con(CambioGenero.SustituirGenerico), Texto: "con un género genérico"),
                (N: Con(CambioGenero.Revisar), Texto: "con otro estilo"),
            }
            .Where(p => p.N > 0).Select(p => $"{p.N:N0} {p.Texto}").ToList();
        var lista = partes.Count <= 1 ? string.Concat(partes) : string.Join(", ", partes.Take(partes.Count - 1)) + " y " + partes[^1];
        var cuerpo = $"Se cambiará el género de {EscrituraPorReglas.Cuenta(marcadas.Count, "canción", "canciones")}: {lista}.";
        var nota = "Solo se escribe en las canciones que sigan como estaban al proponer; lo que hayas cambiado después se respeta. "
                 + "rekordbox no vuelve a leer las etiquetas de las pistas ya importadas hasta que usas «Recargar etiqueta». "
                 + "Se puede deshacer desde Enriquecer.";
        var dudosas = marcadas.Count(f => !f.Propuesta.Marcada);
        if (dudosas > 0)
            nota = (dudosas == 1
                       ? "⚠ 1 no salía marcada porque la regla no es tan segura en su caso: comprueba que la has revisado. "
                       : $"⚠ {dudosas:N0} no salían marcadas porque la regla no es tan segura en su caso: comprueba que las has revisado. ")
                   + nota;
        return (cuerpo, nota);
    }

    /// <summary>
    /// Los cambios: solo el género, y solo si el archivo sigue como se vio al proponer (sin género, o
    /// con el mismo género). El género pasa por <see cref="GeneroPorReglas.ComoEnLaBiblioteca"/>, como
    /// el del mapa: el que se corrige a mano en la columna «Género» («reguetón») se escribe como lo
    /// escribe la biblioteca («Reggaeton») y no crea otra variante que luego haya que unificar.
    /// </summary>
    internal static List<CambioEtiquetas> Cambios(IEnumerable<GeneroReglaFila> marcadas, IReadOnlyList<string> generosBiblioteca)
        => CambiosDe(marcadas.Select(Instantanea).ToList(), generosBiblioteca);

    /// <summary>
    /// Lo que hay que llevarse de una fila al hilo de trabajo. Lo visto es lo que tenía la canción al
    /// PROPONER, no lo que diga ahora la biblioteca: si el usuario la editó después, su cambio se
    /// respeta. Una canción que no tenía género se rellena solo si sigue sin ninguno (sin lo visto),
    /// como antes.
    /// </summary>
    internal static (string Ruta, string Genero, string? GeneroVisto) Instantanea(GeneroReglaFila f)
        => (f.Ruta, f.Genero, f.Propuesta.Cambio == CambioGenero.Rellenar ? null : f.Propuesta.GeneroActual);

    /// <summary>
    /// Lo mismo a partir de una instantánea (ruta, género, lo visto) tomada en el hilo de la interfaz,
    /// para calcularlo FUERA de él: <see cref="GeneroPorReglas.ComoEnLaBiblioteca"/> recorre todos los
    /// géneros de la biblioteca (dos veces si el género es nuevo), y por fila eran 566 ms con 2.600
    /// filas y 183 géneros. Además cada género distinto se calcula UNA vez: miles de propuestas suelen
    /// repetir unos pocos géneros.
    /// </summary>
    /// <summary>Sin lo visto: solo se pone el género si el archivo sigue sin ninguno (como antes de poder sustituir).</summary>
    internal static List<CambioEtiquetas> CambiosDe(IReadOnlyList<(string Ruta, string Genero)> filas, IReadOnlyList<string> generosBiblioteca)
        => CambiosDe(filas.Select(f => (f.Ruta, f.Genero, (string?)null)).ToList(), generosBiblioteca);

    internal static List<CambioEtiquetas> CambiosDe(IReadOnlyList<(string Ruta, string Genero, string? GeneroVisto)> filas,
                                                    IReadOnlyList<string> generosBiblioteca)
    {
        var comoQueda = new Dictionary<string, string>(StringComparer.Ordinal);
        var cambios = new List<CambioEtiquetas>(filas.Count);
        foreach (var (ruta, genero, visto) in filas)
        {
            var tecleado = genero ?? "";
            if (!comoQueda.TryGetValue(tecleado, out var queda))
                comoQueda[tecleado] = queda = GeneroPorReglas.ComoEnLaBiblioteca(tecleado, generosBiblioteca);
            if (queda.Length > 0) cambios.Add(new CambioEtiquetas(ruta) { GeneroNuevo = queda, GeneroVisto = visto });
        }
        return cambios;
    }

    /// <summary>
    /// Con qué nombre sale en el historial de cambios («Género por carpeta y artista»). Solo escribe el
    /// género, como «Unificar géneros» o una edición del Editor, y sin su propio tipo no se distinguiría
    /// de ellos. En una constante para que una prueba lo compruebe.
    /// </summary>
    internal const Etiquetador.Core.Pipeline.TipoEjecucion TipoEnElHistorial = Etiquetador.Core.Pipeline.TipoEjecucion.GeneroPorReglas;

    /// <summary>
    /// Las propuestas que siguen en la tabla tras aplicar: salen las aplicadas cuyo archivo se escribió
    /// o había cambiado después de proponer. Las desmarcadas se quedan siempre.
    /// </summary>
    internal static List<GeneroReglaFila> QuedanTrasAplicar(IEnumerable<GeneroReglaFila> filas, IReadOnlyCollection<GeneroReglaFila> aplicadas,
                                                          EscrituraPorReglas.Resultado res)
    {
        var hechos = new HashSet<string>(res.Escritos.Concat(res.Respetados), StringComparer.OrdinalIgnoreCase);
        var enviadas = new HashSet<GeneroReglaFila>(aplicadas);
        return filas.Where(f => !(enviadas.Contains(f) && hechos.Contains(f.Ruta))).ToList();
    }

    /// <summary>Escribe los géneros marcados. La confirmación la pide la vista.</summary>
    public Task AplicarAsync() => _p.EscribirArchivosAsync("Escribiendo géneros…", async ct =>
    {
        var marcadas = Marcadas;
        if (marcadas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        _p.Engine.ReleaseAudio();
        // En el hilo de la interfaz solo la instantánea (la columna «Género» es editable); pasar cada
        // género por los de la biblioteca va dentro del Task.Run, con miles de filas tarda.
        var filas = marcadas.Select(Instantanea).ToList();
        var generos = GenerosDisponibles.ToList();
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => EscrituraPorReglas.EscribirConHistorial(_p.Engine, CambiosDe(filas, generos), progreso, ct,
                                                                                "Género por reglas", TipoEnElHistorial));

        var quedan = QuedanTrasAplicar(Propuestas, marcadas, res);
        if (quedan.Count != Propuestas.Count) Propuestas = new ObservableCollection<GeneroReglaFila>(quedan);
        if (res.Escritos.Count > 0) await _p.Engine.Library.RefrescarAsync(res.Escritos);   // solo lo escrito, sin recorrer las carpetas

        _p.Status = $"Género puesto en {EscrituraPorReglas.Cuenta(res.Escritos.Count, "canción", "canciones")}" + EscrituraPorReglas.Cola(res)
                    + (res.Escritos.Count > 0
                        ? " rekordbox no vuelve a leer las etiquetas de las pistas que ya tiene importadas: selecciónalas allí y usa «Recargar etiqueta»."
                        : "");
    });
}
