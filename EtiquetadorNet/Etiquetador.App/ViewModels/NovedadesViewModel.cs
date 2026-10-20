using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Página Novedades (equipo «novedades», tanda 4, P5): los lanzamientos recientes en Deezer de los
/// artistas con más canciones en la biblioteca, cruzados con ella.
///
/// Tendencias dice qué suena en cada país, y en una biblioteca bien surtida casi todo está ya («49 de
/// 50» en los registros reales). Lo que llega tarde son los estrenos de los artistas que más se pinchan,
/// antes de que entren en un chart o en los packs del pool. La lista sale de
/// <see cref="NovedadesDeezer"/>; el cruce, la columna de disponibilidad, la mejor copia, el M3U8, la
/// carpeta, el orden para mezclar y «Añadir a pendientes» son los de
/// <see cref="ListaFrenteBibliotecaViewModel"/>, como en Tendencias y Playlists.
///
/// La búsqueda solo lee de la red y no toca la música, así que va en segundo plano (<see cref="Buscando"/>)
/// y no bloquea el resto de la aplicación. IsBusy queda para lo que sí toca archivos (copiar a una
/// carpeta, ordenar para mezclar), como en las otras dos páginas.
/// </summary>
public partial class NovedadesViewModel : ListaFrenteBibliotecaViewModel
{
    public NovedadesViewModel(AppEngine engine) : base(engine)
    {
        _numArtistas = PresentacionNovedades.Normalizar(engine.Config.NovedadesArtistas, PresentacionNovedades.OpcionesArtistas,
                                                        PresentacionNovedades.ArtistasPorDefecto);
        _dias = PresentacionNovedades.Normalizar(engine.Config.NovedadesDias, PresentacionNovedades.OpcionesDias,
                                                 PresentacionNovedades.DiasPorDefecto);
        Status = "Elige cuántos artistas consultar y de cuántos días atrás, y pulsa «Buscar novedades».";
    }

    public override string NombreSugerido => $"Novedades {DateTime.Now:yyyy-MM-dd}";

    protected override string Etiqueta => "Novedades";

    /// <summary>No se sigue: la página entera ya trata de lo que ha salido desde la última vez.</summary>
    protected override ListaSeguida? DescribirLista() => null;

    // =============================================================================================
    // Opciones
    // =============================================================================================

    public int[] OpcionesArtistas => PresentacionNovedades.OpcionesArtistas;
    public int[] OpcionesDias => PresentacionNovedades.OpcionesDias;

    /// <summary>Los tipos del selector, en el orden de <see cref="FiltroTipoLanzamiento"/>.</summary>
    public string[] Tipos { get; } = { "Todos los tipos", "Solo singles", "Solo EP", "Solo álbumes" };

    [ObservableProperty] private int _numArtistas;
    [ObservableProperty] private int _dias;
    [ObservableProperty] private string _tipo = "Todos los tipos";

    /// <summary>Solo las canciones que no están en la biblioteca (filtro de la tabla, no cambia la lista).</summary>
    [ObservableProperty] private bool _soloQueFaltan;

    /// <summary>Fuera las que ya se habían publicado antes del plazo y las reediciones.</summary>
    [ObservableProperty] private bool _soloEstrenos;

    /// <summary>
    /// Con las canciones de artistas que no se han podido confirmar en Deezer. Desactivado por defecto:
    /// MEDIDO con los 200 artistas de una biblioteca real (90 días), 9 quedaron sin confirmar y casi
    /// todos eran homónimos (un «Manu» brasileño con 28 lanzamientos, un «Kike» con 34 seguidores);
    /// aportaban 97 de 934 canciones.
    /// </summary>
    [ObservableProperty] private bool _incluirSinConfirmar;

    partial void OnNumArtistasChanged(int value)
    {
        _engine.Config.NovedadesArtistas = value;
        _engine.SaveConfig();
    }

    partial void OnDiasChanged(int value)
    {
        _engine.Config.NovedadesDias = value;
        _engine.SaveConfig();
    }

    partial void OnTipoChanged(string value) => _ = MostrarAsync();
    partial void OnSoloEstrenosChanged(bool value) => _ = MostrarAsync();
    partial void OnIncluirSinConfirmarChanged(bool value) => _ = MostrarAsync();
    partial void OnSoloQueFaltanChanged(bool value) => ReaplicarFiltro();

    protected override bool TieneFiltroPropio => SoloQueFaltan;
    protected override bool PasaFiltroPropio(TrendRow fila) => !fila.Tengo;

    private FiltroNovedades Filtro => new(
        (FiltroTipoLanzamiento)Math.Max(0, Array.IndexOf(Tipos, Tipo)), SoloEstrenos, IncluirSinConfirmar);

    // =============================================================================================
    // Búsqueda
    // =============================================================================================

    /// <summary>Se está buscando en Deezer, en segundo plano.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeBuscar), nameof(PuedeActuar), nameof(PuedeTocarArtista))]
    private bool _buscando;

    /// <summary>Avance de la búsqueda, de 0 a 100.</summary>
    [ObservableProperty] private double _avance;

    /// <summary>El resumen de la última búsqueda, encima de la tabla.</summary>
    [ObservableProperty] private string _resumenBusqueda = "";

    /// <summary>
    /// Se está comprobando en Deezer el artista que el usuario indica para corregir uno. Esa consulta
    /// pasa por el limitador compartido de api.deezer.com y puede tardar segundos: mientras tanto no se
    /// busca, porque la búsqueda leería el estado anterior del artista y al terminar volvería a poner
    /// en la tabla las canciones del homónimo recién corregido.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeBuscar), nameof(PuedeTocarArtista))]
    private bool _corrigiendo;

    /// <summary>Se puede empezar una búsqueda.</summary>
    public bool PuedeBuscar => !Buscando && !IsBusy && !Corrigiendo;

    /// <summary>
    /// Se puede copiar a una carpeta u ordenar. Mientras se busca no: al terminar, la lista se
    /// sustituye y el orden para mezclar quedaría hecho sobre la anterior.
    /// </summary>
    public bool PuedeActuar => !Buscando && !IsBusy;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(PuedeBuscar));
            OnPropertyChanged(nameof(PuedeActuar));
        }
    }

    private AlmacenArtistasDeezer? _almacen;

    /// <summary>Las correspondencias artista → Deezer, que se leen la primera vez que hacen falta.</summary>
    private AlmacenArtistasDeezer Almacen
    {
        get
        {
            if (_almacen != null) return _almacen;
            _almacen = new AlmacenArtistasDeezer(Path.Combine(_engine.Paths.DataDir, AlmacenArtistasDeezer.NombreArchivo));
            if (_almacen.AvisoCarga.Length > 0)
            {
                _engine.Logger.Err("Novedades: " + _almacen.AvisoCarga);
                _engine.Avisos.Poner("carga:novedades", _almacen.AvisoCarga);
            }
            return _almacen;
        }
    }

    /// <summary>Lo que dio la última búsqueda (sin filtrar), y de cuántos días era.</summary>
    private ResultadoNovedades? _resultado;
    private int _diasResultado;

    /// <summary>Las canciones de la última búsqueda, sin las de artistas corregidos o excluidos después.</summary>
    private IReadOnlyList<PistaNovedad> _pistas = Array.Empty<PistaNovedad>();

    [RelayCommand]
    private async Task BuscarAsync()
    {
        if (!PuedeBuscar) return;
        if (PendientesViewModel.MotivoSinBiblioteca(_engine.Library) is { Length: > 0 } motivo) { Status = motivo; return; }

        // La instantánea de la biblioteca, en el hilo de la interfaz; lo caro, fuera.
        var canciones = _engine.Library.Tracks.ToList();
        var almacen = Almacen;
        var (n, dias, hoy) = (NumArtistas, Dias, DateTime.Today);
        Buscando = true;
        Avance = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Status = "Eligiendo los artistas con más canciones de la biblioteca…";
        _engine.Logger.Head($"Novedades: {n} artistas, lanzamientos de los últimos {dias} días");
        try
        {
            var artistas = await Task.Run(() => ArtistasDeLaBiblioteca.Principales(canciones, n, null, almacen.EstaExcluido), ct);
            if (artistas.Count == 0)
            {
                Status = "No hay en la biblioteca ningún artista con dos canciones o más que consultar.";
                return;
            }

            var progreso = new Progress<ProgresoNovedades>(p =>
            {
                Avance = PresentacionNovedades.Avance(p);
                Status = PresentacionNovedades.TextoAvance(p);
            });
            var deezer = new NovedadesDeezer(_engine.Api);
            var r = await Task.Run(() => deezer.BuscarAsync(artistas, almacen, dias, hoy, progreso, ct, AlGuardar, guardarCada: 10),
                                   CancellationToken.None);

            // Cancelada antes de tener ninguna canción: la tabla sigue con la búsqueda anterior, que es
            // mejor que dejarla vacía. Lo averiguado de cada artista ya se ha guardado.
            if (r.Cancelada && r.Pistas.Count == 0)
            {
                Status = "Búsqueda cancelada. Lo averiguado de cada artista queda guardado"
                       + (_resultado != null ? "; la tabla sigue mostrando la búsqueda anterior." : ".");
                _engine.Logger.Sum($"Novedades: cancelada tras consultar {r.Artistas.Count} artistas.");
                return;
            }

            _resultado = r;
            _diasResultado = dias;
            _pistas = r.Pistas;
            _corregidos.Clear();
            RellenarArtistas();

            // Un «Crear carpeta» u «Ordenar para mezclar» empezado antes de la búsqueda trabaja sobre la
            // lista anterior: MostrarAsync espera a que termine antes de sustituirla.
            await MostrarAsync();

            Avance = 100;
            _engine.Logger.Sum($"Novedades: {r.Artistas.Count} artistas, {r.Lanzamientos} lanzamientos, {r.Pistas.Count} canciones "
                             + $"en {r.Duracion.TotalSeconds:0} s" + (r.Cancelada ? " (cancelada)" : ""));
            foreach (var a in r.Artistas.Where(a => a.Deezer is { Estado: EstadoArtistaDeezer.Dudoso }))
                _engine.Logger.Detail($"Novedades: «{a.Artista.Nombre}» sin confirmar en Deezer (se ha tomado «{a.Deezer!.DeezerNombre}», "
                                    + $"{a.Deezer.Fans} seguidores).");
        }
        catch (OperationCanceledException) { Status = "Búsqueda cancelada. Lo averiguado de cada artista queda guardado."; }
        catch (Exception e)
        {
            Status = "No se pudieron buscar las novedades: " + e.Message;
            _engine.Logger.Error("Novedades: fallo al buscar", e);
        }
        finally
        {
            Buscando = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Lo que dice cada guardado del almacén durante la búsqueda (llega desde otro hilo).</summary>
    private void AlGuardar(string error)
    {
        if (error.Length == 0) { _engine.Avisos.Quitar("guardar:novedades"); return; }
        _engine.Logger.Err("Novedades: no se pudo guardar " + AlmacenArtistasDeezer.NombreArchivo + ": " + error);
        _engine.Avisos.Poner("guardar:novedades", $"No se pudo guardar {AlmacenArtistasDeezer.NombreArchivo}: {error}");
    }

    /// <summary>Las fichas de cada fila de la tabla, por la fila de la lista (por referencia: dos filas pueden ser iguales).</summary>
    private Dictionary<ChartTrack, DetalleNovedad> _detalles = new(ReferenceEqualityComparer.Instance);

    protected override DetalleNovedad? DetalleDeFila(ChartTrack pista) => _detalles.GetValueOrDefault(pista);

    protected override string OrigenPendiente(TrendRow fila, string origenLista)
        => fila.Detalle is { } d ? PresentacionNovedades.Origen(d.Pista) : origenLista;

    private bool _mostrando, _otraVez;

    /// <summary>
    /// Rehace la tabla con los filtros de ahora. Si ya se está rehaciendo (se han tocado dos filtros
    /// seguidos), se repite al terminar con los últimos: nunca se cruzan dos a la vez.
    ///
    /// Con «Crear carpeta» u «Ordenar para mezclar» en marcha, espera a que terminen: rehacer la tabla
    /// borra el orden para mezclar, y el que estaba calculándose se habría enseñado sobre una lista que
    /// ya no es la de la tabla. Los filtros se desactivan mientras tanto; esto cubre lo demás que la
    /// rehace (corregir o excluir un artista).
    /// </summary>
    private async Task MostrarAsync()
    {
        if (_resultado == null) return;
        if (_mostrando) { _otraVez = true; return; }
        _mostrando = true;
        try
        {
            do
            {
                while (IsBusy) await Task.Delay(100);
                _otraVez = false;
                // Se vuelve a leer en cada vuelta: la repetición puede venir de una búsqueda recién terminada.
                if (_resultado is not { } r) break;
                var filtro = Filtro;
                var visibles = PresentacionNovedades.Filtrar(_pistas, filtro);
                var filas = PresentacionNovedades.ComoFilas(visibles);
                var detalles = new Dictionary<ChartTrack, DetalleNovedad>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < filas.Count; i++) detalles[filas[i]] = new DetalleNovedad(visibles[i]);
                _detalles = detalles;

                var tengo = await CruzarAsync(filas, CancellationToken.None);

                var ocultas = filtro.IncluirSinConfirmar ? 0 : _pistas.Count(p => p.DeArtistaDudoso);
                ResumenBusqueda = PresentacionNovedades.Resumen(r, _diasResultado, ocultas);
                Status = PresentacionNovedades.TextoTabla(_pistas.Count, Rows.Count, Resumen, TextoFaltan(Rows.Count - tengo),
                                                          ResumenBusqueda, r.Cancelada);
            } while (_otraVez);
        }
        catch (Exception e)
        {
            Status = "No se pudo cruzar la lista con la biblioteca: " + e.Message;
            _engine.Logger.Error("Novedades: fallo al cruzar con la biblioteca", e);
        }
        finally { _mostrando = false; }
    }

    // =============================================================================================
    // Artistas: comprobar y corregir a quién se consulta
    // =============================================================================================

    public ObservableCollection<FilaArtistaNovedades> Artistas { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoExcluir), nameof(PuedeTocarArtista))]
    private FilaArtistaNovedades? _artistaElegido;

    /// <summary>
    /// Hay un artista elegido, no se está buscando (la búsqueda escribe en el mismo almacén) ni se está
    /// comprobando otra corrección.
    /// </summary>
    public bool PuedeTocarArtista => ArtistaElegido != null && !Buscando && !Corrigiendo;

    /// <summary>Por qué no se puede tocar ahora un artista, o null si se puede.</summary>
    private string? EsperaArtista()
        => Buscando ? "Espera a que termine la búsqueda."
         : Corrigiendo ? "Espera a que termine la comprobación del artista en Deezer."
         : null;

    public string TextoExcluir => ArtistaElegido?.Excluido == true ? "Volver a incluir" : "No buscar sus novedades";

    /// <summary>El enlace (o el número) del artista de Deezer que el usuario indica para el elegido.</summary>
    [ObservableProperty] private string _correccion = "";

    /// <summary>Artistas corregidos, excluidos u olvidados después de la última búsqueda: lo que se sabía de sus lanzamientos ya no vale.</summary>
    private readonly HashSet<string> _corregidos = new(StringComparer.Ordinal);

    private void RellenarArtistas()
    {
        var elegido = ArtistaElegido?.Clave;
        Artistas.Clear();
        foreach (var f in PresentacionNovedades.FilasArtistas(_resultado?.Artistas ?? Array.Empty<ConsultaArtista>(), Almacen, _corregidos))
            Artistas.Add(f);
        ArtistaElegido = elegido == null ? null : Artistas.FirstOrDefault(a => a.Clave == elegido);
    }

    /// <summary>
    /// Lo que se ha cambiado de un artista invalida sus canciones de la búsqueda anterior: se quitan de
    /// la lista hasta la próxima búsqueda, que ya consultará al artista bueno.
    /// </summary>
    private async Task QuitarCancionesDeAsync(string nombre)
    {
        var antes = _pistas.Count;
        _pistas = PresentacionNovedades.SinLasDe(_pistas, nombre);
        if (_pistas.Count != antes) await MostrarAsync();
    }

    [RelayCommand]
    private async Task CorregirArtistaAsync()
    {
        if (ArtistaElegido is not { } a) { Status = "Elige antes un artista en la tabla."; return; }
        if (EsperaArtista() is { } espera) { Status = espera; return; }
        if (NovedadesDeezer.IdDeEnlace(Correccion) is not { } id)
        {
            Status = "Pega el enlace del artista en Deezer (https://www.deezer.com/artist/…) o su número.";
            return;
        }

        Corrigiendo = true;
        try
        {
            Status = "Comprobando el artista en Deezer…";
            var (artista, fallo) = await ConsultarArtista(id);
            if (fallo) { Status = "Deezer no responde. Inténtalo de nuevo dentro de unos minutos."; return; }
            if (artista == null) { Status = $"Deezer no tiene ningún artista con el número {id}."; return; }

            Almacen.Corregir(a.Clave, a.Nombre, artista, DateTime.Now);
            var error = Almacen.Guardar();
            _corregidos.Add(a.Clave);
            Correccion = "";
            RellenarArtistas();
            await QuitarCancionesDeAsync(a.Nombre);
            Status = $"«{a.Nombre}» queda asociado a «{artista.Nombre}» de Deezer. Sus novedades saldrán en la próxima búsqueda."
                   + (error.Length > 0 ? $" No se ha podido guardar: {error}" : "");
            _engine.Logger.Detail($"Novedades: «{a.Nombre}» corregido a mano → Deezer {artista.Id} «{artista.Nombre}».");
        }
        finally { Corrigiendo = false; }
    }

    /// <summary>La consulta del artista a Deezer. Se puede sustituir en las pruebas.</summary>
    internal Func<long, Task<(ArtistaDeezer? Artista, bool FalloDeRed)>>? ConsultaArtistaDePrueba { get; set; }

    private Task<(ArtistaDeezer? Artista, bool FalloDeRed)> ConsultarArtista(long id)
        => ConsultaArtistaDePrueba?.Invoke(id) ?? new NovedadesDeezer(_engine.Api).ArtistaAsync(id, CancellationToken.None);

    [RelayCommand]
    private void VolverAIdentificar()
    {
        if (ArtistaElegido is not { } a) { Status = "Elige antes un artista en la tabla."; return; }
        if (EsperaArtista() is { } espera) { Status = espera; return; }
        // Queda «por identificar» aunque no se hubiera llegado a identificar: así la próxima búsqueda
        // pregunta de nuevo a Deezer, sin la caché de red.
        Almacen.Olvidar(a.Clave, a.Nombre);
        var error = Almacen.Guardar();
        _corregidos.Add(a.Clave);
        RellenarArtistas();
        Status = (a.Excluido
                     ? $"«{a.Nombre}» se volverá a buscar en Deezer en la primera búsqueda después de volver a incluirlo."
                     : $"«{a.Nombre}» se volverá a buscar en Deezer en la próxima búsqueda.")
               + (error.Length > 0 ? $" No se ha podido guardar: {error}" : "");
    }

    [RelayCommand]
    private async Task ExcluirArtistaAsync()
    {
        if (ArtistaElegido is not { } a) { Status = "Elige antes un artista en la tabla."; return; }
        if (EsperaArtista() is { } espera) { Status = espera; return; }
        var excluir = !a.Excluido;
        Almacen.Excluir(a.Clave, a.Nombre, excluir, DateTime.Now);
        var error = Almacen.Guardar();
        _corregidos.Add(a.Clave);
        RellenarArtistas();
        if (excluir) await QuitarCancionesDeAsync(a.Nombre);
        Status = (excluir
                     ? $"Ya no se buscan las novedades de «{a.Nombre}», y su sitio lo ocupa el siguiente artista con más canciones."
                     : $"Las novedades de «{a.Nombre}» se buscarán de nuevo a partir de la próxima búsqueda.")
               + (error.Length > 0 ? $" No se ha podido guardar: {error}" : "");
    }

    [RelayCommand]
    private async Task AbrirArtistaEnDeezerAsync()
    {
        if (ArtistaElegido is not { TieneEnlace: true } a) { Status = "Ese artista no tiene artista de Deezer asociado."; return; }
        await Shell.OpenUrlAsync(a.Enlace);
    }

    /// <summary>Abre en el navegador la canción elegida en la tabla, en Deezer.</summary>
    [RelayCommand]
    private async Task AbrirEnDeezerAsync()
    {
        if (FilaDelMenu is not { } fila || fila.Enlace.Length == 0) { Status = "Elige antes una canción de la tabla."; return; }
        await Shell.OpenUrlAsync(fila.Enlace);
    }
}
