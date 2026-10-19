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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
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
    }

    /// <summary>Nombre para la lista M3U8 y la carpeta que se crean desde esta pestaña.</summary>
    public abstract string NombreSugerido { get; }

    /// <summary>Cómo se llama esta pestaña en el registro.</summary>
    protected abstract string Etiqueta { get; }

    partial void OnSoloLasQueTengoChanged(bool value)
    {
        RowsView.Filter = value ? o => o is TrendRow r && r.Tengo : null;
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
                _engine.Logger.Detail($"{Etiqueta}: {r.Indice.Apartadas} acapellas y mashups no cuentan para el «lo tengo».");
            if (r.Indice.Mejoras > 0)
                _engine.Logger.Detail($"{Etiqueta}: {r.Indice.Mejoras} veces se prefirió una versión mejor teniendo varias copias.");
        }

        // Cada fila del chart pasa por la misma limpieza de títulos (expresiones regulares): con las
        // 456 de una playlist de Deezer no es gratis, así que también va fuera.
        var rutas = await Task.Run(() => lista.Select(t => r.Indice.Buscar(t.Artist, t.Title)).ToList(), ct);
        ct.ThrowIfCancellationRequested();

        Rows.Clear();
        for (var i = 0; i < lista.Count; i++)
        {
            var t = lista[i];
            Rows.Add(new TrendRow
            {
                Position = t.Position, Artist = t.Artist, Title = t.Title,
                DurText = t.DurText, FilePath = rutas[i], SpotifyId = t.SpotifyId,
            });
        }
        RowsView.Refresh();

        var tengo = Rows.Count(r => r.Tengo);
        Resumen = $"{tengo} de {Rows.Count} disponibles en la biblioteca ({(Rows.Count == 0 ? 0 : tengo * 100 / Rows.Count)}%)";
        _ = RellenarDuracionesAsync(Rows.ToList());
        return tengo;
    }

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

        var items = tengo.Select(r => new PlaylistItem(r.FilePath, r.Artist, r.Title, 0));
        var err = PlaylistWriter.Write(destino, items);
        Status = err.Length == 0
            ? $"Lista guardada con {tengo.Count} canciones: {Path.GetFileName(destino)}"
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
        Status = $"Copiando {tengo.Count} canciones…";
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
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) { Status = "Esa grabación no está en la biblioteca."; return; }
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) { Status = "Esa grabación no está en la biblioteca."; return; }
        await Shell.OpenContainingFolderAsync(path);
    }
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

    /// <summary>El título sin adornos ni versiones («Extended», «feat.»…), normalizado.</summary>
    internal static string ClaveTitulo(string? titulo)
        => string.IsNullOrWhiteSpace(titulo) ? "" : TextUtils.NkClave(Descriptors.CleanKeywords(titulo));

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
