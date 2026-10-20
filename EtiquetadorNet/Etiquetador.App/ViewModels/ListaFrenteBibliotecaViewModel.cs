using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Una lista de canciones de fuera -un chart, una playlist- cruzada con la biblioteca: qué tienes,
/// qué te falta, y la lista lista para abrir en rekordbox o copiar a una carpeta.
///
/// Es lo común a Tendencias y Playlists. Cada pestaña solo decide DE DÓNDE sale la lista; el cruce,
/// la elección de la mejor copia y las acciones son las mismas en las dos.
/// </summary>
public abstract partial class ListaFrenteBibliotecaViewModel : ViewModelBase, IEstadoPagina
{
    protected readonly AppEngine _engine;
    protected CancellationTokenSource? _cts;

    public ObservableCollection<TrendRow> Rows { get; } = new();

    /// <summary>Vista de la tabla (permite filtrar por "solo las que tengo").</summary>
    public Avalonia.Collections.DataGridCollectionView RowsView { get; }

    [ObservableProperty] private TrendRow? _selectedRow;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _soloLasQueTengo;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _resumen = "";

    protected ListaFrenteBibliotecaViewModel(AppEngine engine)
    {
        _engine = engine;
        RowsView = new Avalonia.Collections.DataGridCollectionView(Rows);
        // Seguir o dejar de seguir desde Pendientes, o «Actualizar todas», cambian lo que hay que
        // enseñar de la lista abierta aquí: el botón, la casilla «Solo nuevas» y la columna Novedad.
        Seguimiento.SeguidasCambiadas += AlCambiarSeguidas;
    }

    /// <summary>
    /// La fila sobre la que actúan las opciones de UNA canción del menú contextual (escuchar, abrir su
    /// carpeta, buscar compatibles, sus versiones, buscarla en el pool): la pulsada con el botón
    /// derecho o, si no, la elegida en la tabla.
    ///
    /// No basta con <see cref="SelectedRow"/>: con varias filas elegidas, el clic derecho sobre una de
    /// ellas no cambia la selección (para que «Añadir a pendientes» actúe sobre todas), y SelectedRow
    /// puede ser otra. «Escuchar» sonaba una canción distinta de la pulsada. La vista la pone en cada
    /// clic derecho (AccionesLista.ClicEnTabla).
    /// </summary>
    [ObservableProperty] private TrendRow? _filaDelMenu;

    // Elegir otra fila con el ratón o el teclado también cambia sobre cuál actúa el menú.
    partial void OnSelectedRowChanged(TrendRow? value) => FilaDelMenu = value;

    /// <summary>Nombre para la lista M3U8 y la carpeta que se crean desde esta pestaña.</summary>
    public abstract string NombreSugerido { get; }

    /// <summary>Cómo se llama esta pestaña en el registro.</summary>
    protected abstract string Etiqueta { get; }

    partial void OnSoloLasQueTengoChanged(bool value) => AplicarFiltro();

    /// <summary>
    /// Los dos filtros de la tabla a la vez: «solo las disponibles» y «solo las nuevas» de una lista
    /// seguida. Cada casilla por su lado pisaba el filtro de la otra.
    /// </summary>
    private void AplicarFiltro()
    {
        var tengo = SoloLasQueTengo;
        var nuevas = SoloNuevas;
        var propio = TieneFiltroPropio;   // el de la página (Novedades: «solo las que no están»)
        RowsView.Filter = !tengo && !nuevas && !propio ? null
            : o => o is TrendRow r && (!tengo || r.Tengo) && (!nuevas || r.EsNueva) && (!propio || PasaFiltroPropio(r));
        RowsView.Refresh();
    }

    /// <summary>Escanea la biblioteca si todavía no lo está: sin ella no hay nada con qué cruzar.</summary>
    protected async Task AsegurarBibliotecaAsync()
    {
        if (_engine.Library.IsScanned || _engine.Library.Folders.Count == 0) return;
        Status = "Escaneando biblioteca…";
        await _engine.Library.ScanAsync();
    }

    /// <summary>
    /// Rellena la tabla con la lista cruzada contra la biblioteca y devuelve cuántas tienes. Las
    /// duraciones que falten se piden a Spotify después, sin hacer esperar.
    ///
    /// El índice de la biblioteca se prepara FUERA del hilo de la interfaz y se comparte entre
    /// Tendencias y Playlists hasta que la biblioteca cambie. MEDIDO en los registros reales: con
    /// 13.500 canciones, cada «Ver tendencias» dejaba la ventana congelada 5,8 s (16:19:02 → 16:19:08,
    /// con el chart ya sacado de la caché) y cada lista de Playlists 6,2 s; Windows llega a marcarla
    /// como «No responde». Y se repetía en cada país consultado, aunque la biblioteca fuera la misma.
    /// </summary>
    protected async Task<int> CruzarAsync(IReadOnlyList<ChartTrack> lista, CancellationToken ct)
    {
        // La instantánea se toma aquí, en el hilo de la interfaz: es el único que toca la colección
        // de la biblioteca. Copiar las referencias es inmediato; lo caro va dentro de Task.Run.
        var canciones = _engine.Library.Tracks.ToList();
        Status = "Cruzando la lista con la biblioteca…";
        var r = await IndiceBibliotecaCompartido.De(_engine.Library).ObtenerAsync(canciones, ct);
        if (r.Reutilizado)
            _engine.Logger.Detail($"{Etiqueta}: índice de la biblioteca reutilizado ({r.Milisegundos} ms en comprobar que no ha cambiado).");
        else
        {
            _engine.Logger.Detail($"{Etiqueta}: índice de la biblioteca preparado en {r.Milisegundos} ms "
                                + $"({r.Indice.Canciones} canciones, {r.Indice.Claves} claves).");
            if (r.Indice.Apartadas > 0)
                _engine.Logger.Detail($"{Etiqueta}: {r.Indice.Apartadas} acapellas y mashups no cuentan como «En la biblioteca».");
            if (r.Indice.Mejoras > 0)
                _engine.Logger.Detail($"{Etiqueta}: {r.Indice.Mejoras} veces se prefirió una versión mejor teniendo varias copias.");
        }

        // Cada fila del chart pasa por la misma limpieza de títulos (expresiones regulares): con las
        // 456 de una playlist de Deezer no es gratis, así que también va fuera.
        var rutas = await Task.Run(() => lista.Select(t => r.Indice.Buscar(t.Artist, t.Title)).ToList(), ct);
        ct.ThrowIfCancellationRequested();

        OlvidarListaAnterior();
        for (var i = 0; i < lista.Count; i++)
        {
            var t = lista[i];
            Rows.Add(new TrendRow
            {
                Position = t.Position, Artist = t.Artist, Title = t.Title,
                DurText = t.DurText, FilePath = rutas[i], SpotifyId = t.SpotifyId, Enlace = t.Link ?? "",
                Detalle = DetalleDeFila(t),
            });
        }
        RowsView.Refresh();

        var tengo = Rows.Count(r => r.Tengo);
        Resumen = TextoResumen(tengo, Rows.Count);
        _ = RellenarDuracionesAsync(Rows.ToList());
        return tengo;
    }

    /// <summary>«49 de 50 en la biblioteca (98 %)». Sin adjetivo que concordar: vale igual con una canción que con cien.</summary>
    internal static string TextoResumen(int enBiblioteca, int total)
        => $"{enBiblioteca} de {total} en la biblioteca ({(total == 0 ? 0 : enBiblioteca * 100 / total)} %)";

    /// <summary>«Faltan 3.», «Falta 1.» o «No falta ninguna.», tras cargar una lista.</summary>
    internal static string TextoFaltan(int faltan)
        => faltan switch { 0 => "No falta ninguna.", 1 => "Falta 1.", var n => $"Faltan {n}." };

    /// <summary>«1 canción», «3 canciones».</summary>
    internal static string Canciones(int n) => n == 1 ? "1 canción" : $"{n} canciones";

    /// <summary>
    /// Pide a Spotify la duración exacta de cada canción, usando el ID que trae cada fila.
    ///
    /// Va detrás de pintar la tabla y sin marcar la pestaña como ocupada: el usuario ya tiene
    /// delante el orden y los nombres, que es lo que venía a ver. Las respuestas se cachean, así que
    /// esto solo se paga la primera vez que aparece cada canción.
    /// </summary>
    private async Task RellenarDuracionesAsync(List<TrendRow> filas)
    {
        var cfg = _engine.Config;
        if (cfg.SpotifyId.Length == 0 || cfg.SpotifySecret.Length == 0) return;   // sin credenciales, nada que pedir

        var pendientes = filas.Where(f => f.SpotifyId.Length > 0 && f.DurText.Length == 0).ToList();
        if (pendientes.Count == 0) return;

        var puestas = 0;
        foreach (var fila in pendientes)
        {
            try
            {
                var seg = await _engine.Spotify.TrackDurationAsync(fila.SpotifyId, cfg.SpotifyId, cfg.SpotifySecret);
                if (seg > 0) { fila.DurText = $"{seg / 60}:{seg % 60:00}"; puestas++; }
            }
            catch { /* que falte una duración no puede tumbar la pestaña */ }
        }
        if (puestas > 0) _engine.Logger.Detail($"{Etiqueta}: {puestas} duraciones traídas de Spotify.");
    }

    /// <summary>
    /// Tenerla en acapella o dentro de un mashup NO es tenerla.
    ///
    /// La clave de comparación se construye con el título ya limpio, y esa limpieza se lleva por
    /// delante el «(Acapella)»: la acapella de un tema del chart casaba con él y la lista decía que
    /// lo tenías. Para preparar una sesión eso es justo lo contrario de lo que hace falta saber,
    /// porque el día que lo busques no vas a tener más que la voz.
    ///
    /// Los remixes, bootlegs y ediciones SÍ cuentan: siguen siendo la canción y se pueden pinchar.
    /// </summary>
    internal static bool NoCuentaComoTenerla(Track t)
        => Identificacion.EsAcapella(t.FilePath)
        || Matching.IsMashupFolder(Path.GetDirectoryName(t.FilePath))
        || Matching.IsMezclaDeVariosTemas(Path.GetFileNameWithoutExtension(t.FileName));

    /// <summary>
    /// Ancho del número de posición al copiar a una carpeta: dos cifras como mínimo, y las que hagan
    /// falta para la posición más alta de la LISTA (no solo de lo que tienes).
    ///
    /// Antes eran siempre dos, y una playlist de Deezer de 456 canciones (vista en el registro real:
    /// «tienes 0 de 456») quedaba en el USB como 1, 10, 100, 101… 109, 11, 110: los reproductores de
    /// CDJ y muchos programas ordenan por nombre carácter a carácter, y el número dejaba de servir para
    /// lo único que servía, mantener el orden. Se mira la lista entera para que, si mañana consigues
    /// la 105 y vuelves a copiar, sus números sigan teniendo el mismo ancho que los de hoy.
    /// </summary>
    internal static int AnchoNumeracion(IEnumerable<int> posiciones)
    {
        var maxima = posiciones.DefaultIfEmpty(0).Max();
        return Math.Max(2, maxima.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
    }

    /// <summary>Nombre de la copia: la posición con el ancho dado delante del nombre del archivo.</summary>
    internal static string NombreNumerado(int posicion, int ancho, string rutaArchivo)
        => $"{posicion.ToString(new string('0', Math.Max(1, ancho)), System.Globalization.CultureInfo.InvariantCulture)} - {Path.GetFileName(rutaArchivo)}";

    /// <summary>
    /// Guarda como lista M3U8 las canciones de la lista que ya tienes, en su orden. Sirve para
    /// abrirla directamente en rekordbox o Engine sin tener que copiar archivos.
    /// </summary>
    public string ExportarM3u(string destino)
    {
        var tengo = Rows.Where(r => r.Tengo).OrderBy(r => r.Position).ToList();
        if (tengo.Count == 0) { Status = "Ninguna de esta lista está en la biblioteca."; return "vacio"; }
        return EscribirM3u(destino, tengo.Select(r => new PlaylistItem(r.FilePath, r.Artist, r.Title, 0)).ToList(), "");
    }

    /// <summary>
    /// Como <see cref="ExportarM3u"/>, pero en el orden para mezclar (<see cref="OrdenarParaMezclarCommand"/>)
    /// en vez de en el de la lista, y con la duración de cada archivo.
    /// </summary>
    public string ExportarOrdenM3u(string destino)
    {
        if (PasosMezcla.Count == 0) { Status = "Pulsa antes «Ordenar para mezclar»."; return "vacio"; }
        var items = PasosMezcla.Select(p => new PlaylistItem(p.FilePath, p.Artist, p.Title, p.Paso.Track.DurationSeconds)).ToList();
        return EscribirM3u(destino, items, " en el orden para mezclar");
    }

    private string EscribirM3u(string destino, IReadOnlyList<PlaylistItem> items, string comoVa)
    {
        var err = PlaylistWriter.Write(destino, items);
        Status = err.Length == 0
            ? $"Lista guardada con {Canciones(items.Count)}{comoVa}: {Path.GetFileName(destino)}"
            : "No se pudo guardar la lista: " + err;
        return err;
    }

    /// <summary>Copia a una carpeta las canciones de la lista que ya tienes (no las mueve).</summary>
    public async Task CopyToFolderAsync(string destino)
    {
        if (IsBusy) return;
        // Cada archivo, una sola vez y con su mejor posición. Si dos filas de la lista apuntan al mismo
        // archivo, copiarlo dos veces con prefijos distintos solo llena el USB de repetidas: es lo que
        // pasaba con los títulos en alfabetos no latinos, que casaban todos con el mismo archivo.
        var tengo = Rows.Where(r => r.Tengo)
                        .GroupBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.OrderBy(r => r.Position).First())
                        .OrderBy(r => r.Position)
                        .ToList();
        if (tengo.Count == 0) { Status = "Ninguna de esta lista está en la biblioteca."; return; }
        var ancho = AnchoNumeracion(Rows.Select(r => r.Position));

        IsBusy = true;
        _engine.ReleaseAudio();
        Status = $"Copiando {Canciones(tengo.Count)}…";
        try
        {
            var (copiadas, saltadas, errores) = await Task.Run(() =>
            {
                int ok = 0, skip = 0, err = 0;
                Directory.CreateDirectory(destino);
                foreach (var r in tengo)
                {
                    try
                    {
                        // El número de posición delante mantiene el orden de la lista en el explorador
                        // y en el CDJ, con las cifras que hagan falta (001…) en las listas largas.
                        var nombre = NombreNumerado(r.Position, ancho, r.FilePath);
                        var d = Path.Combine(destino, nombre);
                        if (File.Exists(d)) { skip++; continue; }
                        File.Copy(r.FilePath, d);   // COPIA: tu biblioteca no se toca
                        ok++;
                    }
                    catch { err++; }
                }
                return (ok, skip, err);
            });

            Status = $"Copiadas {copiadas} en «{Path.GetFileName(destino)}»"
                   + (saltadas > 0 ? $" · {saltadas} ya estaban" : "")
                   + (errores > 0 ? $" · {errores} con error" : "")
                   + ". La biblioteca original no se modifica.";
            _engine.Logger.Sum($"{Etiqueta}: copiadas {copiadas} a {destino}");
        }
        catch (Exception e) { Status = "No se pudo copiar: " + e.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsBusy) { Status = "Espera a que termine el proceso en curso."; return; }
        var path = RutaSeleccionada;
        if (string.IsNullOrEmpty(path)) { Status = "Esa grabación no está en la biblioteca."; return; }
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = RutaSeleccionada;
        if (string.IsNullOrEmpty(path)) { Status = "Esa grabación no está en la biblioteca."; return; }
        await Shell.OpenContainingFolderAsync(path);
    }

    /// <summary>
    /// El archivo de la fila sobre la que actúa el menú en la tabla que se está viendo: la lista (ver
    /// <see cref="FilaDelMenu"/>) o el orden para mezclar, que solo deja elegir una fila.
    /// </summary>
    private string? RutaSeleccionada => VerOrdenMezcla ? SelectedPaso?.FilePath : FilaDelMenu?.FilePath;

    /// <summary>
    /// Lo que dependía de la lista anterior deja de valer: sus filas, su orden para mezclar y si se
    /// sigue. Al cargar otra lista, o al cambiar de fuente en Tendencias.
    /// </summary>
    protected void OlvidarListaAnterior()
    {
        Rows.Clear();
        PasosMezcla.Clear();
        VerOrdenMezcla = false;
        ResumenMezcla = "";
        _leida = null;
        EstaSeguida = false;
        Novedades = "";
    }

    // =================================================================================================
    // Pendientes (P6): lo que falta de la lista, a la lista de pendientes
    // =================================================================================================

    /// <summary>Las pendientes y las listas seguidas, compartidas con la página Pendientes.</summary>
    protected SeguimientoListas Seguimiento => SeguimientoListas.De(_engine);

    /// <summary>
    /// Añade a pendientes las filas que no están en la biblioteca: las elegidas o, con
    /// <paramref name="todasLasQueFaltan"/>, todas las de la lista. Las que ya están en la biblioteca
    /// no se añaden: no hay nada que conseguir.
    /// </summary>
    public void AnadirAPendientes(IEnumerable<TrendRow> elegidas, bool todasLasQueFaltan)
    {
        if (IsBusy) { Status = "Espera a que termine el proceso en curso."; return; }
        if (Rows.Count == 0) { Status = "Carga antes una lista."; return; }
        // Con la biblioteca sin escanear (se canceló o falló el escaneo) todas salen como que faltan, y
        // al escanear se tacharían de golpe con la fecha de hoy. Lo mismo que en la página Pendientes.
        if (PendientesViewModel.MotivoSinBiblioteca(_engine.Library) is { Length: > 0 } motivo)
        {
            Status = motivo;
            return;
        }
        var faltan = (todasLasQueFaltan ? Rows : elegidas).Where(r => !r.Tengo).ToList();
        if (faltan.Count == 0)
        {
            Status = todasLasQueFaltan
                ? "Todas las canciones de esta lista están en la biblioteca."
                : "Elige en la tabla alguna canción que no esté en la biblioteca: las que ya están no se añaden a pendientes.";
            return;
        }
        var origen = DescribirLista()?.Nombre ?? Etiqueta;
        var r = Seguimiento.AnadirPendientes(faltan.Select(f => new NuevaPendiente(f.Artist, f.Title, OrigenPendiente(f, origen), f.Enlace)));
        Status = TextoAlta(r.Hechas, r.Otras) + (r.Error.Length > 0 ? $" No se ha podido guardar: {r.Error}" : "");
    }

    /// <summary>
    /// Abre en el navegador la búsqueda de la fila elegida en el pool del usuario (la plantilla de
    /// Ajustes → Pendientes), igual que desde la página Pendientes.
    /// </summary>
    [RelayCommand]
    private async Task BuscarEnPoolAsync()
    {
        if (FilaDelMenu is not { } fila) { Status = "Elige antes una canción de la tabla."; return; }
        var url = BusquedaEnPool.Url(_engine.Config.PoolBusquedaUrl, fila.Artist, fila.Title, out var motivo);
        if (url.Length == 0) { Status = motivo; return; }
        await Shell.OpenUrlAsync(url);
        Status = $"Buscando «{BusquedaEnPool.TextoBusqueda(fila.Artist, fila.Title)}» en tu pool, en el navegador.";
    }

    /// <summary>«3 canciones añadidas a Pendientes (2 ya estaban).»</summary>
    internal static string TextoAlta(int anadidas, int yaEstaban)
    {
        if (anadidas == 0)
            return yaEstaban == 1 ? "Esa canción ya estaba en Pendientes." : $"Esas {yaEstaban} canciones ya estaban en Pendientes.";
        var texto = anadidas == 1 ? "1 canción añadida a Pendientes" : $"{anadidas} canciones añadidas a Pendientes";
        if (yaEstaban > 0) texto += yaEstaban == 1 ? " (1 ya estaba)" : $" ({yaEstaban} ya estaban)";
        return texto + ". Se tacharán solas cuando aparezcan en la biblioteca.";
    }

    // =================================================================================================
    // Seguir la lista (P7): qué ha entrado desde la última vez
    // =================================================================================================

    [ObservableProperty] private bool _soloNuevas;

    /// <summary>La lista que se está viendo se sigue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoSeguir))]
    private bool _estaSeguida;

    /// <summary>«3 nuevas desde el 12/10», o por qué todavía no hay con qué comparar. Vacío si no se sigue.</summary>
    [ObservableProperty] private string _novedades = "";

    public string TextoSeguir => EstaSeguida ? "Dejar de seguir" : "Seguir esta lista";

    partial void OnSoloNuevasChanged(bool value) => AplicarFiltro();

    /// <summary>La última lectura de la lista que se está viendo, para poder empezar a seguirla sin volver a pedirla.</summary>
    private (IReadOnlyList<ChartTrack> Pistas, DateTime Fecha, bool Completa, string Aviso)? _leida;

    /// <summary>
    /// La lista que se está viendo, descrita para poder seguirla y para la columna «Origen» de
    /// Pendientes. La que se CARGÓ, no lo que haya ahora en los selectores. Null si no hay ninguna.
    /// </summary>
    protected abstract ListaSeguida? DescribirLista();

    /// <summary>
    /// Apunta la lectura recién cruzada: si la lista se sigue, marca las nuevas y guarda lo que tiene
    /// hoy. Se llama después de <see cref="CruzarAsync"/>, con la misma lista en el mismo orden.
    /// </summary>
    /// <param name="fecha">De cuándo es el contenido: la descarga de un chart guardado, o ahora.</param>
    /// <param name="completa">Se leyó entera (un corte de Deezer a mitad no lo es; el tope de Spotify sí).</param>
    protected void AnotarLectura(IReadOnlyList<ChartTrack> lista, DateTime fecha, bool completa, string aviso)
    {
        _leida = (lista, fecha, completa, aviso);
        var lista0 = DescribirLista();
        if (lista0 == null || !Seguimiento.Seguidas.Sigue(lista0.Clave))
        {
            MostrarSeguimiento(seguida: false, NovedadesLista.Ninguna, completa);
            return;
        }

        NovedadesLista nov;
        // Apuntar la lectura avisa de que han cambiado las listas seguidas; ese aviso no hace falta
        // atenderlo aquí, que ya se pone todo al día justo después.
        _anotando = true;
        try { nov = Seguimiento.RegistrarLectura(lista0.Clave, PistasDeLaTabla(lista), fecha, completa, lista0.Nombre, aviso); }
        finally { _anotando = false; }
        MostrarSeguimiento(seguida: true, nov, completa);
    }

    private bool _anotando;

    /// <summary>Las canciones de la lectura con lo que dice la tabla de cada una: si está en la biblioteca.</summary>
    private List<PistaLista> PistasDeLaTabla(IReadOnlyList<ChartTrack> lista)
        => SeguimientoListas.PistasDe(lista, i => i < Rows.Count && Rows[i].Tengo);

    /// <summary>
    /// Otra página ha cambiado las listas seguidas: se ha dejado de seguir esta desde Pendientes, o
    /// «Actualizar todas» ha avanzado su referencia. La lista que se está viendo se pone al día con lo
    /// guardado, sin volver a leerla ni apuntar nada. Antes el botón seguía diciendo «Dejar de seguir»
    /// y la casilla «Solo nuevas» seguía activa con una lista que ya no se seguía.
    /// </summary>
    private void AlCambiarSeguidas()
    {
        if (_anotando || _leida is not { } leida || DescribirLista() is not { } desc) return;
        var guardada = Seguimiento.Seguidas.Buscar(desc.Clave);
        MostrarSeguimiento(guardada != null, ListasSeguidas.Comparar(guardada, PistasDeLaTabla(leida.Pistas)), leida.Completa);
    }

    /// <summary>El botón, la línea de novedades, la columna Novedad y el filtro «Solo nuevas», a la vez.</summary>
    private void MostrarSeguimiento(bool seguida, NovedadesLista nov, bool completa)
    {
        EstaSeguida = seguida;
        foreach (var r in Rows) r.Novedad = "";
        if (!seguida)
        {
            Novedades = "";
            // «Solo nuevas» sobre una lista que no se sigue dejaría la tabla vacía sin motivo aparente.
            SoloNuevas = false;
            AplicarFiltro();
            return;
        }
        Novedades = TextoNovedades(nov, completa);
        foreach (var (i, texto) in MarcasNovedad(nov, Rows.Count)) Rows[i].Novedad = texto;
        if (SoloNuevas) AplicarFiltro();
    }

    /// <summary>Qué fila lleva qué marca en la columna Novedad: «Nueva desde 12/10» en las nuevas que estén en la tabla.</summary>
    internal static IEnumerable<(int Fila, string Texto)> MarcasNovedad(NovedadesLista nov, int filas)
    {
        if (nov.Desde is not { } desde) yield break;
        var texto = $"Nueva desde {desde.ToString("dd/MM", System.Globalization.CultureInfo.InvariantCulture)}";
        foreach (var i in nov.Nuevas)
            if (i >= 0 && i < filas) yield return (i, texto);
    }

    /// <summary>
    /// Lo que se dice de las novedades de una lista seguida al abrirla.
    ///
    /// Sin referencia no se dice «desde hoy» ni «a partir de mañana»: una lista seguida hace días que no
    /// se ha vuelto a revisar, o un chart servido de la copia guardada, tampoco tienen todavía con qué
    /// comparar, y la comparación llega en la primera revisión hecha otro día, no necesariamente mañana.
    /// </summary>
    internal static string TextoNovedades(NovedadesLista nov, bool completa)
    {
        var aviso = completa ? "" : " La lista no se leyó entera: esta lectura no cuenta para la próxima comparación.";
        if (nov.Desde is not { } desde)
            return "Aún no hay con qué comparar: las canciones que entren se marcarán cuando se revise la lista otro día." + aviso;
        var dia = desde.ToString("dd/MM", System.Globalization.CultureInfo.InvariantCulture);
        return (nov.Nuevas.Count switch
        {
            0 => $"Ninguna canción nueva desde el {dia}.",
            1 => $"1 canción nueva desde el {dia}.",
            var n => $"{n} canciones nuevas desde el {dia}.",
        }) + aviso;
    }

    /// <summary>Lo que se dice al empezar a seguir una lista.</summary>
    internal static string TextoAlSeguir(string nombre)
        => $"Sigues «{nombre}». Cuando se revise otro día, al abrirla aquí o con «Actualizar todas» en Pendientes, "
         + "se marcarán las canciones que hayan entrado.";

    /// <summary>Empieza a seguir la lista que se está viendo, o deja de seguirla.</summary>
    [RelayCommand]
    private void SeguirLista()
    {
        if (IsBusy) { Status = "Espera a que termine el proceso en curso."; return; }
        var desc = DescribirLista();
        if (desc == null || _leida is not { } leida || Rows.Count == 0) { Status = "Carga antes una lista para poder seguirla."; return; }

        // Se pregunta a lo guardado, no al botón: pudo dejar de seguirse desde Pendientes.
        if (Seguimiento.Seguidas.Buscar(desc.Clave) is { } guardada)
        {
            var err = Seguimiento.DejarDeSeguir(new[] { guardada.Id });
            MostrarSeguimiento(seguida: false, NovedadesLista.Ninguna, leida.Completa);
            Status = $"Ya no sigues «{desc.Nombre}»." + (err.Length > 0 ? $" No se ha podido guardar: {err}" : "");
            return;
        }

        if (!leida.Completa)
        {
            Status = "La lista no se leyó entera. Vuelve a cargarla antes de seguirla: con una lectura a medias, la próxima vez saldrían como nuevas canciones que ya estaban.";
            return;
        }
        var error = Seguimiento.Seguir(desc, PistasDeLaTabla(leida.Pistas), leida.Fecha);
        MostrarSeguimiento(seguida: true, NovedadesLista.Ninguna, completa: true);
        Status = TextoAlSeguir(desc.Nombre)
               + (leida.Aviso.Length > 0 ? " " + leida.Aviso : "")
               + (error.Length > 0 ? $" No se ha podido guardar: {error}" : "");
    }

    // =================================================================================================
    // Ordenar para mezclar (P1)
    // =================================================================================================

    /// <summary>
    /// Lo más que se ordena de una vez, como en el Asistente: el orden es voraz (cada paso mira todas
    /// las que quedan), y con miles de canciones dejaría de ser instantáneo sin que una sesión lo pida.
    /// </summary>
    public const int MaximoParaOrdenar = 1000;

    /// <summary>Las canciones de la lista que están en la biblioteca, en el orden para pincharlas seguidas.</summary>
    public ObservableCollection<PasoListaFila> PasosMezcla { get; } = new();

    [ObservableProperty] private PasoListaFila? _selectedPaso;

    /// <summary>Se está enseñando el orden para mezclar en lugar de la lista.</summary>
    [ObservableProperty] private bool _verOrdenMezcla;

    [ObservableProperty] private string _resumenMezcla = "";

    [RelayCommand]
    private void VolverALaLista() => VerOrdenMezcla = false;

    /// <summary>
    /// Ordena para pincharlas seguidas las canciones de la lista que están en la biblioteca, con las
    /// mismas reglas que «Ordenar para mezclar» del Asistente (<see cref="OrdenMezcla"/>): tono en la
    /// rueda Camelot, tempo contando doble y mitad y, si tienen ficha, que la energía no baje y que se
    /// respete el orden de los momentos de la sesión.
    ///
    /// Visto en una biblioteca real: el Top 50 de España se consultó en diez sesiones con 49 de 50 en
    /// la biblioteca, y el M3U8 salía en el orden del chart, que no se puede pinchar tal cual.
    /// </summary>
    [RelayCommand]
    private Task OrdenarParaMezclarAsync() => OrdenarAsync(rutaPrimera: null);

    /// <summary>
    /// «Empezar por esta», en el menú de la tabla del orden: lo rehace abriendo con la canción elegida,
    /// como en el Asistente y en «Ordenar la selección» de Biblioteca. Vuelve a preparar las canciones
    /// desde la biblioteca de AHORA, así que lo que se haya enviado a la papelera mientras tanto no
    /// vuelve a aparecer.
    /// </summary>
    [RelayCommand]
    private Task EmpezarPorEstaAsync()
    {
        if (SelectedPaso is not { } paso) { Status = "Elige antes en la tabla la canción con la que quieres empezar."; return Task.CompletedTask; }
        return OrdenarAsync(paso.FilePath);
    }

    private async Task OrdenarAsync(string? rutaPrimera)
    {
        if (IsBusy) { Status = "Espera a que termine el proceso en curso."; return; }
        if (Rows.Count == 0) { Status = "Carga antes una lista."; return; }
        if (!Rows.Any(r => r.Tengo)) { Status = "Ninguna canción de esta lista está en la biblioteca: no hay nada que ordenar."; return; }

        // Las instantáneas, aquí: la biblioteca y las filas solo se tocan desde el hilo de la interfaz.
        var filas = Rows.ToList();
        var biblioteca = _engine.Library.Tracks.ToList();
        IsBusy = true;
        Status = rutaPrimera == null ? "Ordenando para mezclar…" : "Reordenando…";
        try
        {
            var prep = await Task.Run(() => PrepararOrden(filas, biblioteca, _engine.Fichas.Obtener));
            if (prep.ParaOrdenar.Count > MaximoParaOrdenar)
            {
                Status = $"Hay {prep.ParaOrdenar.Count} canciones de esta lista en la biblioteca; para ordenar una sesión el máximo es {MaximoParaOrdenar}.";
                return;
            }
            if (prep.ParaOrdenar.Count == 0)
            {
                Status = "Ninguna de las canciones de esta lista que están en la biblioteca trae BPM ni tonalidad en las etiquetas: no hay con qué ordenarlas. "
                       + "Analízalas en tu programa de DJ y escribe esos datos en las etiquetas, o impórtalos de rekordbox en Biblioteca.";
                return;
            }
            var primera = rutaPrimera == null ? null : PrimeraDelOrden(prep, rutaPrimera);
            if (rutaPrimera != null && primera == null)
            {
                Status = "Esa canción ya no está en la biblioteca, o ya no trae BPM ni tonalidad: no puede abrir el orden. Pulsa «Ordenar para mezclar» para rehacerlo.";
                return;
            }
            var orden = await Task.Run(() => OrdenMezcla.Ordenar(prep.ParaOrdenar, primera));
            PasosMezcla.Clear();
            foreach (var paso in orden)
                PasosMezcla.Add(new PasoListaFila(paso, prep.PosicionEnLista.GetValueOrDefault(paso.Track.FilePath)));
            ResumenMezcla = ResumenOrden(orden, prep);
            VerOrdenMezcla = true;
            Status = (primera != null ? $"Ordenada empezando por «{EntornoCanciones.Nombre(primera)}». " : "") + ResumenMezcla;
            _engine.Logger.Detail($"{Etiqueta}: {Status}");
        }
        catch (Exception e) { Status = "No se pudo ordenar: " + e.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// La canción con la que se quiere abrir, entre las que entran en el orden, o null si ya no está
    /// (se envió a la papelera, se renombró o perdió el BPM y la tonalidad desde el último orden).
    /// </summary>
    internal static Track? PrimeraDelOrden(PreparacionOrden prep, string ruta)
    {
        foreach (var (t, _) in prep.ParaOrdenar)
            if (string.Equals(t.FilePath, ruta, StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    /// <summary>Lo que entra en el orden y lo que se queda fuera, y por qué.</summary>
    /// <param name="ParaOrdenar">Cada archivo una vez, con su ficha de DJ si la tiene.</param>
    /// <param name="NoEnBiblioteca">Filas de la lista que no están en la biblioteca.</param>
    /// <param name="SinDatos">Archivos sin BPM ni tonalidad: no hay con qué colocarlos.</param>
    /// <param name="Repetidas">Filas que apuntan a un archivo que ya entra por otra fila.</param>
    /// <param name="FueraDeLaBiblioteca">Archivos que ya no están en la biblioteca (cambió desde que se cruzó la lista).</param>
    /// <param name="PosicionEnLista">La posición de cada archivo en la lista, para enseñarla junto al orden.</param>
    internal sealed record PreparacionOrden(IReadOnlyList<(Track Track, FichaDj? Ficha)> ParaOrdenar, int NoEnBiblioteca,
        int SinDatos, int Repetidas, int FueraDeLaBiblioteca, IReadOnlyDictionary<string, int> PosicionEnLista);

    /// <summary>
    /// Prepara lo que se ordena. Una canción sin BPM pero con tonalidad (o al revés) SÍ entra: el orden
    /// la coloca con lo que tiene y la transición lo dice («Tono desconocido»). Solo se queda fuera la
    /// que no tiene ninguno de los dos, porque entraría en un sitio cualquiera y partiría la subida.
    /// </summary>
    internal static PreparacionOrden PrepararOrden(IReadOnlyList<TrendRow> filas, IReadOnlyList<Track> biblioteca,
                                                   Func<string, FichaDj?> ficha)
    {
        var porRuta = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in biblioteca) porRuta.TryAdd(t.FilePath, t);

        var posicion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var para = new List<(Track, FichaDj?)>();
        int noEnBiblioteca = 0, sinDatos = 0, repetidas = 0, fuera = 0;
        foreach (var f in filas.OrderBy(f => f.Position))
        {
            if (!f.Tengo) { noEnBiblioteca++; continue; }
            if (!posicion.TryAdd(f.FilePath, f.Position)) { repetidas++; continue; }
            if (!porRuta.TryGetValue(f.FilePath, out var t)) { fuera++; continue; }
            if (t.Bpm == 0 && OrdenMezcla.Camelot(t.KeyCamelot) == null) { sinDatos++; continue; }
            para.Add((t, ficha(t.FilePath)));
        }
        return new PreparacionOrden(para, noEnBiblioteca, sinDatos, repetidas, fuera, posicion);
    }

    /// <summary>
    /// «38 canciones en orden · 30 de 37 transiciones armónicas · 2 choques de tono · 1 vuelve a un
    /// momento anterior de la sesión. Fuera del orden: 10 que no están en la biblioteca, 2 sin BPM ni
    /// tonalidad.»
    ///
    /// Las cifras salen de <see cref="OrdenMezcla.Resumir"/>, las mismas que da el Asistente, también
    /// las de los momentos de la sesión que antes no se contaban aquí aunque la columna de la entrada
    /// dijera «vuelve a…» o «salta a…». Cada cifra concuerda en número («1 choque de tono»), y con una
    /// sola canción no se habla de transiciones: «0 de 0 transiciones armónicas» no informa de nada.
    /// </summary>
    internal static string ResumenOrden(IReadOnlyList<PasoMezcla> orden, PreparacionOrden prep)
    {
        var r = OrdenMezcla.Resumir(orden);
        var partes = new List<string> { r.Canciones == 1 ? "1 canción en orden" : $"{r.Canciones} canciones en orden" };
        if (r.Transiciones > 0)
        {
            partes.Add($"{r.Armonicas} de {r.Transiciones} " + (r.Transiciones == 1 ? "transición armónica" : "transiciones armónicas"));
            partes.Add(r.Choques switch
            {
                0 => "ningún choque de tono",
                1 => "1 choque de tono",
                var n => $"{n} choques de tono",
            });
            if (r.SinTonalidad > 0) partes.Add($"{r.SinTonalidad} sin tonalidad para comparar");
            if (r.VueltasAtras > 0)
                partes.Add(r.VueltasAtras == 1 ? "1 vuelve a un momento anterior de la sesión" : $"{r.VueltasAtras} vuelven a un momento anterior de la sesión");
            if (r.Saltos > 0)
                partes.Add(r.Saltos == 1 ? "1 se salta un momento de la sesión" : $"{r.Saltos} se saltan un momento de la sesión");
        }
        var texto = string.Join(" · ", partes) + ".";

        var fuera = new List<string>();
        if (prep.NoEnBiblioteca > 0)
            fuera.Add(prep.NoEnBiblioteca == 1 ? "1 que no está en la biblioteca" : $"{prep.NoEnBiblioteca} que no están en la biblioteca");
        if (prep.SinDatos > 0) fuera.Add($"{prep.SinDatos} sin BPM ni tonalidad");
        if (prep.FueraDeLaBiblioteca > 0)
            fuera.Add((prep.FueraDeLaBiblioteca == 1 ? "1 cuyo archivo ya ha salido" : $"{prep.FueraDeLaBiblioteca} cuyos archivos ya han salido")
                      + " de la biblioteca (vuelve a cargar la lista)");
        if (fuera.Count > 0) texto += " Fuera del orden: " + string.Join(", ", fuera) + ".";
        if (prep.Repetidas > 0) texto += prep.Repetidas == 1
            ? " Una fila apunta al mismo archivo que otra y entra una sola vez."
            : $" {prep.Repetidas} filas apuntan al mismo archivo que otra y entran una sola vez.";
        if (r.FaltaTonalidad)
            texto += " Muchas canciones no traen tonalidad en las etiquetas: impórtala de rekordbox en Biblioteca.";
        // Las fichas aportan la energía y el momento de la sesión: sin ninguna de las dos, solo cuentan tono y BPM.
        if (orden.Count > 0 && orden.All(p => p.Ficha is not { } f || (f.Energia <= 0 && f.Momentos == MomentoSet.Ninguno)))
            texto += " Sin fichas de DJ, el orden sale solo por tono y BPM.";
        return texto;
    }

    // =================================================================================================
    // Puntos de extensión para páginas con más datos por canción (equipo «novedades», tanda 4, P5)
    // =================================================================================================
    //
    // Novedades enseña de cada canción su lanzamiento, la fecha y el tipo, filtra por «solo las que no
    // están en la biblioteca» y apunta en Pendientes de qué lanzamiento salió cada una. Tendencias y
    // Playlists no los usan: los valores por defecto dejan su comportamiento como estaba.

    /// <summary>Lo que la página quiere enseñar de esa canción además de lo común (<see cref="TrendRow.Detalle"/>).</summary>
    protected virtual DetalleNovedad? DetalleDeFila(ChartTrack pista) => null;

    /// <summary>El origen que se apunta en Pendientes para esa fila; por defecto, el de la lista entera.</summary>
    protected virtual string OrigenPendiente(TrendRow fila, string origenLista) => origenLista;

    /// <summary>La página tiene ahora un filtro propio activo, que se suma a los comunes.</summary>
    protected virtual bool TieneFiltroPropio => false;

    /// <summary>¿Pasa la fila el filtro propio de la página? Solo se pregunta si <see cref="TieneFiltroPropio"/>.</summary>
    protected virtual bool PasaFiltroPropio(TrendRow fila) => true;

    /// <summary>Vuelve a aplicar los filtros de la tabla, tras cambiar el propio de la página.</summary>
    protected void ReaplicarFiltro() => AplicarFiltro();
}

/// <summary>
/// Una canción en el orden para mezclar de una lista de Tendencias o Playlists. Tiene archivo
/// (<see cref="IFilaConArchivo"/>), así que «Buscar compatibles» y «Ver sus versiones» funcionan
/// también desde su menú.
/// </summary>
public sealed class PasoListaFila : IFilaConArchivo
{
    string IFilaConArchivo.RutaArchivo => FilePath;

    private static readonly IBrush ChoqueBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xDC, 0x26, 0x26));

    public PasoMezcla Paso { get; }

    /// <summary>Su posición en la lista original (el chart, la playlist).</summary>
    public int EnLista { get; }

    public PasoListaFila(PasoMezcla paso, int enLista)
    {
        Paso = paso;
        EnLista = enLista;
    }

    public int Posicion => Paso.Posicion;
    public string Artist => Paso.Track.Artist ?? "";
    public string Title => Paso.Track.Title ?? Paso.Track.FileName;
    public string Bpm => Paso.Track.Bpm > 0 ? Paso.Track.Bpm.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public string Camelot => Paso.Track.KeyCamelot;
    public string Energia => Paso.Ficha?.Energia is > 0 ? Paso.Ficha.Energia.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
    public string Transicion => Paso.Transicion;
    public bool Choca => Paso.Posicion > 1 && Paso.Tono == EncajeTono.Choque;
    public IBrush FondoBrush => Choca ? ChoqueBrush : Brushes.Transparent;
    public string FilePath => Paso.Track.FilePath;
    public string FileName => Paso.Track.FileName;
}

/// <summary>
/// La biblioteca vista desde una lista de fuera: para cada pareja (artista, título) normalizada, la
/// mejor copia que tienes. Se indexa tanto por las etiquetas como por el nombre del archivo, porque en
/// material de DJ las etiquetas no siempre están.
///
/// Cada canción entra bajo TODOS los artistas de su crédito, y una fila de la lista casa si casa
/// cualquiera de los suyos. Antes solo contaba el primero, y el orden no es el mismo en todas partes:
/// el chart dice «Bizarrap, Shakira - Bzrp Music Sessions, Vol. 53» y el archivo se llama «Shakira,
/// Bizarrap - Bzrp Music Sessions Vol 53.mp3», y salía «No disponible» teniéndolo. El título tiene que
/// coincidir igualmente, así que partir el crédito no abre la puerta a falsos «lo tengo».
/// </summary>
public sealed class IndiceBiblioteca
{
    private readonly Dictionary<string, (string Ruta, int Orden)> _mejor;

    /// <summary>Canciones recorridas, contadas también las que no cuentan como tenerla.</summary>
    public int Canciones { get; }

    /// <summary>Acapellas y mezclas de varios temas: están, pero no cuentan como tener la canción.</summary>
    public int Apartadas { get; }

    /// <summary>Veces que una copia mejor sustituyó a otra ya indexada bajo la misma clave.</summary>
    public int Mejoras { get; }

    public int Claves => _mejor.Count;

    private IndiceBiblioteca(Dictionary<string, (string Ruta, int Orden)> mejor, int canciones, int apartadas, int mejoras)
    {
        _mejor = mejor;
        Canciones = canciones;
        Apartadas = apartadas;
        Mejoras = mejoras;
    }

    /// <summary>
    /// Recorre la biblioteca y se queda, bajo cada clave, con la copia que antes se quiere pinchar
    /// (<see cref="PrioridadVersion"/>). Es lo caro: cada canción pasa por la lectura del nombre y por
    /// la limpieza de títulos. Se llama fuera del hilo de la interfaz.
    /// </summary>
    public static IndiceBiblioteca Construir(IReadOnlyList<Track> canciones)
    {
        // Se guarda también CON QUÉ prioridad entró cada ruta, para poder cambiarla si aparece una
        // copia mejor. Antes se usaba TryAdd, que se queda con la primera que aparezca en el
        // escaneo: puro azar del orden de las carpetas.
        var mejor = new Dictionary<string, (string Ruta, int Orden)>(StringComparer.Ordinal);
        int apartadas = 0, mejoras = 0;
        var claves = new HashSet<string>(StringComparer.Ordinal);

        foreach (var t in canciones)
        {
            if (ListaFrenteBibliotecaViewModel.NoCuentaComoTenerla(t)) { apartadas++; continue; }

            // Las claves de las etiquetas y las del nombre suelen repetirse: una vez por canción basta.
            claves.Clear();
            if (!string.IsNullOrEmpty(t.Artist) && !string.IsNullOrEmpty(t.Title))
                claves.UnionWith(ClavesDe(t.Artist, t.Title));
            var pr = Core.Pipeline.FileNameParser.Parse(t.FileName);
            if (pr.FnArtist.Length > 0 && pr.QTitle.Length > 0)
                claves.UnionWith(ClavesDe(pr.FnArtist, pr.QTitle));
            if (claves.Count == 0) continue;

            var orden = PrioridadVersion.OrdenDe(t.FileName);
            foreach (var clave in claves)
            {
                if (mejor.TryGetValue(clave, out var actual))
                {
                    if (orden >= actual.Orden) continue;   // la que ya había es igual de buena o mejor
                    mejoras++;
                }
                mejor[clave] = (t.FilePath, orden);
            }
        }
        return new IndiceBiblioteca(mejor, canciones.Count, apartadas, mejoras);
    }

    /// <summary>
    /// La ruta de la mejor copia de esa canción, o "" si no la tienes. Si la fila trae varios artistas,
    /// casa con cualquiera de ellos, y entre las copias que encuentre se queda con la mejor versión.
    /// </summary>
    public string Buscar(string? artistas, string? titulo)
    {
        string? ruta = null;
        var orden = int.MaxValue;
        foreach (var clave in ClavesDe(artistas, titulo))
            if (_mejor.TryGetValue(clave, out var x) && x.Orden < orden) { ruta = x.Ruta; orden = x.Orden; }
        return ruta ?? "";
    }

    /// <summary>
    /// Las claves «artista|título» de un crédito, una por artista. Ninguna si el título o todos los
    /// artistas se quedan en nada al normalizar: una clave a medias NUNCA casa.
    ///
    /// Visto al revisar Tendencias con países de alfabetos no latinos: la normalización de siempre solo
    /// conserva a-z y 0-9, así que un título en hangul o en cirílico quedaba en "" y todas esas filas
    /// compartían la clave «|» (o «bts|»): todas salían «En la biblioteca» apuntando al MISMO archivo,
    /// y «Crear carpeta» lo copiaba decenas de veces. Ahora se normaliza con <see cref="TextUtils.NkClave"/>,
    /// que conserva esas letras, y lo que aun así quede vacío no se indexa ni se busca.
    /// </summary>
    internal static IReadOnlyList<string> ClavesDe(string? artistas, string? titulo)
    {
        var t = ClaveTitulo(titulo);
        if (t.Length == 0) return Array.Empty<string>();
        var a = ClavesArtista(artistas);
        if (a.Count == 0) return Array.Empty<string>();
        var salida = new List<string>(a.Count);
        foreach (var x in a) salida.Add(x + "|" + t);
        return salida;
    }

    /// <summary>
    /// El título sin adornos ni versiones («Extended», «feat.»…), normalizado.
    ///
    /// El número de un volumen o de una parte se protege antes («Vol. 53» → «n53») para que la
    /// limpieza no se lo lleve: quita los números sueltos de dos y tres cifras, que en un nombre de
    /// archivo suelen ser el BPM o la pista, y «Bizarrap, Quevedo - Bzrp Music Sessions, Vol. 52» y
    /// «Bizarrap, Shakira - Bzrp Music Sessions, Vol. 53» quedaban con la misma clave por Bizarrap:
    /// teniendo la 52, Tendencias daba la 53 por «En la biblioteca» y Pendientes la tachaba. MEDIDO en
    /// una biblioteca real: 84 de 13.590 archivos llevan un número de serie de dos o más cifras tras
    /// «Vol.», «Part» o «#», casi todos sesiones de Bizarrap escritas de varias formas («Vol. 57»,
    /// «#54», «Vol.066»). Por eso «Vol», «Volume», «Part» y «#» quedan igual y sin ceros delante.
    /// </summary>
    internal static string ClaveTitulo(string? titulo)
        => string.IsNullOrWhiteSpace(titulo) ? "" : TextUtils.NkClave(Descriptors.CleanKeywords(PegarNumeroDeSerie(titulo)));

    private static readonly Regex NumeroDeSerie = new(
        @"(?:\b(?:vol|volume|volumen|pt|part|parte)\.?\s*#?|#)\s*(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>«Vol. 066», «Volume 66», «Part 66» y «#66» quedan todos como «n66».</summary>
    internal static string PegarNumeroDeSerie(string titulo)
        => NumeroDeSerie.Replace(titulo, m =>
        {
            var numero = m.Groups[1].Value.TrimStart('0');
            return "n" + (numero.Length > 0 ? numero : "0");
        });

    /// <summary>
    /// Cada artista del crédito normalizado y, si es un alias registrado, con el nombre canónico:
    /// «Cruzzi» y «Cruz Cafuné» son la misma clave, como en el resto de la aplicación.
    /// </summary>
    internal static IReadOnlyList<string> ClavesArtista(string? credito)
    {
        var salida = new List<string>();
        foreach (var parte in PartirArtistas(credito))
        {
            var nk = TextUtils.Nk(TextUtils.ToAscii(parte));
            var canonico = nk.Length > 0 ? ArtistAliases.Current.Canonical(nk) : null;
            var clave = TextUtils.NkClave(canonico ?? parte);
            if (clave.Length > 0 && !salida.Contains(clave)) salida.Add(clave);
        }
        return salida;
    }

    /// <summary>
    /// Separadores de un crédito con varios artistas. A los de siempre (coma, «x», «vs», «feat», «ft»,
    /// «&amp;») se suman « y », « and », « with » y «+»: «Wisin &amp; Yandel» en el chart y «Wisin y
    /// Yandel» en el archivo son el mismo dúo. Las palabras van entre espacios para no partir nombres
    /// como «Xavi» o «Andy».
    /// </summary>
    private static readonly Regex Separadores = new(
        @"\s*(?:[,;&+]|\s(?:x|vs\.?|feat\.?|featuring|ft\.?|y|and|with)\s)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> PartirArtistas(string? credito)
    {
        if (string.IsNullOrWhiteSpace(credito)) return Array.Empty<string>();
        return Separadores.Split(credito)
                          .Select(p => p.Trim())
                          .Where(p => p.Length > 0)
                          .ToList();
    }

    /// <summary>
    /// Huella de lo que el índice usa de cada canción: ruta, artista y título, en orden. Si no ha
    /// cambiado, el índice anterior sigue valiendo. Cubre también lo que se cambia sin reescanear (el
    /// Editor reescribe artista y título en la misma canción de la biblioteca sin avisar a nadie).
    /// FNV-1a de 64 bits: con 13.500 canciones son unos milisegundos, frente al segundo largo de
    /// rehacer el índice.
    /// </summary>
    public static ulong FirmaDe(IReadOnlyList<Track> canciones)
    {
        var h = 14695981039346656037UL;
        void Mezclar(string? s)
        {
            foreach (var c in s ?? "") { h ^= c; h *= 1099511628211UL; }
            h ^= 0x1F; h *= 1099511628211UL;   // separador: «ab|c» y «a|bc» no pueden dar lo mismo
        }
        foreach (var t in canciones)
        {
            Mezclar(t.FilePath);
            Mezclar(t.Artist);
            Mezclar(t.Title);
        }
        return h;
    }
}

/// <summary>
/// El índice de la biblioteca, compartido por Tendencias y Playlists y reutilizado mientras la
/// biblioteca no cambie. Consultar tres países seguidos, o pegar varios enlaces, solo paga el índice
/// la primera vez después de cada cambio, y siempre fuera del hilo de la interfaz.
/// </summary>
internal sealed class IndiceBibliotecaCompartido
{
    private static readonly ConditionalWeakTable<LibraryStore, IndiceBibliotecaCompartido> PorBiblioteca = new();

    /// <summary>El de esa biblioteca. Uno por biblioteca, no uno global: las pruebas crean varias.</summary>
    public static IndiceBibliotecaCompartido De(LibraryStore biblioteca) => PorBiblioteca.GetValue(biblioteca, _ => new());

    private readonly object _cerrojo = new();
    private (ulong Firma, IndiceBiblioteca Indice)? _actual;

    /// <summary>Cuántas veces se ha construido el índice. Para las pruebas.</summary>
    internal int Construcciones { get; private set; }

    internal sealed record Resultado(IndiceBiblioteca Indice, bool Reutilizado, long Milisegundos);

    /// <summary>
    /// El índice de esas canciones: el guardado si la biblioteca no ha cambiado, o uno nuevo. Todo el
    /// trabajo, también calcular la firma, va en Task.Run.
    /// </summary>
    /// <param name="canciones">Instantánea de la biblioteca tomada en el hilo de la interfaz.</param>
    public Task<Resultado> ObtenerAsync(IReadOnlyList<Track> canciones, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var reloj = Stopwatch.StartNew();
            var firma = IndiceBiblioteca.FirmaDe(canciones);
            lock (_cerrojo)
            {
                if (_actual is { } a && a.Firma == firma) return new Resultado(a.Indice, true, reloj.ElapsedMilliseconds);
            }
            ct.ThrowIfCancellationRequested();
            var indice = IndiceBiblioteca.Construir(canciones);
            lock (_cerrojo)
            {
                _actual = (firma, indice);
                Construcciones++;
            }
            return new Resultado(indice, false, reloj.ElapsedMilliseconds);
        }, ct);
}
