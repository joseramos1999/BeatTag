using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción del chart cruzada con tu biblioteca.</summary>
public sealed partial class TrendRow : ObservableObject
{
    private static readonly IBrush TengoBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A));

    public int Position { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>ID de Spotify, si el chart viene de ahi: con el se pide la duracion exacta.</summary>
    public string SpotifyId { get; init; } = "";
    /// <summary>
    /// Duracion. Es [ObservableProperty] porque en el chart de Spotify llega DESPUES: la tabla se
    /// pinta al momento con el orden y los nombres, y la ficha exacta de cada pista se va rellenando
    /// en segundo plano sin hacer esperar al usuario.
    /// </summary>
    [ObservableProperty] private string _durText = "";

    /// <summary>Ruta del archivo de tu biblioteca, si la tienes.</summary>
    public string FilePath { get; init; } = "";
    public string FileName => FilePath.Length > 0 ? Path.GetFileName(FilePath) : "";
    public bool Tengo => FilePath.Length > 0;
    public string Estado => Tengo ? "En la biblioteca" : "No disponible";

    /// <summary>
    /// Qué copia se ha elegido teniendo varias. Se enseña para que la preferencia no sea invisible:
    /// si sale «Remix» es que de ese tema no tienes ni la extendida ni la original.
    /// </summary>
    public string Version => Tengo ? PrioridadVersion.Nombre(PrioridadVersion.De(FileName)) : "";

    public IBrush StateBrush => Tengo ? TengoBrush : Brushes.Transparent;
}

/// <summary>
/// Pestaña Tendencias: qué suena ahora en cada país y cuánto de eso tienes ya.
///
/// La fuente por defecto es el chart de Spotify, que es el que se corresponde con lo que suena de
/// verdad. Su API ya no sirve las listas editoriales (ver ChartsProvider), así que el orden y los
/// IDs se leen del chart publicado por kworb y la ficha exacta de cada pista se le pide a Spotify
/// con esos IDs. Deezer queda como alternativa seleccionable.
/// </summary>
public partial class TrendsViewModel : ViewModelBase, IEstadoPagina
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ChartCountry> Countries { get; } = new();
    public ObservableCollection<TrendRow> Rows { get; } = new();

    /// <summary>Vista de la tabla (permite filtrar por "solo las que tengo").</summary>
    public Avalonia.Collections.DataGridCollectionView RowsView { get; }

    [ObservableProperty] private ChartCountry? _selectedCountry;
    [ObservableProperty] private TrendRow? _selectedRow;
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Cierto solo mientras se descarga la lista de países. Es una precarga que arranca sola con
    /// la aplicación, así que no cuenta como proceso a efectos de bloquear el resto de pestañas.
    /// </summary>
    [ObservableProperty] private bool _loadingCountries;
    [ObservableProperty] private bool _soloLasQueTengo;
    [ObservableProperty] private int _listSize = 50;
    [ObservableProperty] private string _status = "Selecciona un país y pulsa Ver tendencias.";
    [ObservableProperty] private string _resumen = "";

    public int[] ListSizes { get; } = { 20, 50, 100 };

    /// <summary>
    /// De dónde salen las listas. Spotify va primero porque es la que se corresponde con lo que
    /// suena de verdad; Deezer se queda como alternativa por si el otro camino falla.
    /// </summary>
    public string[] Fuentes { get; } = { "Spotify (el que marca lo que suena)", "Deezer" };

    [ObservableProperty] private string _fuente = "Spotify (el que marca lo que suena)";

    private ChartSource FuenteElegida
        => Fuente.StartsWith("Spotify", StringComparison.Ordinal) ? ChartSource.Spotify : ChartSource.Deezer;

    /// <summary>Al cambiar de fuente cambian los países disponibles: hay que rehacer el selector.</summary>
    partial void OnFuenteChanged(string value)
    {
        Countries.Clear();
        Rows.Clear();
        RowsView.Refresh();
        Resumen = "";
        Status = "Cargando países…";
        _ = EnsureCountriesAsync();
    }

    public TrendsViewModel(AppEngine engine)
    {
        _engine = engine;
        RowsView = new Avalonia.Collections.DataGridCollectionView(Rows);
    }

    partial void OnSoloLasQueTengoChanged(bool value)
    {
        RowsView.Filter = value ? o => o is TrendRow r && r.Tengo : null;
        RowsView.Refresh();
    }

    /// <summary>Carga la lista de países la primera vez que se abre la pestaña.</summary>
    public async Task EnsureCountriesAsync()
    {
        if (Countries.Count > 0 || IsBusy) return;
        // Se marca ANTES que IsBusy: es una precarga de fondo que se lanza al arrancar la
        // aplicación, y no debe bloquear el resto de pestañas como sí hace un proceso del usuario.
        LoadingCountries = true;
        IsBusy = true;
        Status = "Cargando países…";
        _engine.Logger.Detail("Tendencias: pidiendo la lista de países…");
        try
        {
            var paises = await _engine.Charts.GetCountriesAsync(FuenteElegida);
            foreach (var p in paises) Countries.Add(p);
            // España por defecto; si no estuviera, el primero.
            SelectedCountry = Countries.FirstOrDefault(c => c.Name.Equals("Spain", StringComparison.OrdinalIgnoreCase))
                              ?? Countries.FirstOrDefault();
            Status = Countries.Count > 0
                ? $"{Countries.Count} países disponibles. Pulsa Ver tendencias."
                : "No se recibió ningún país. ¿Hay conexión a internet?";
            _engine.Logger.Sum($"Tendencias: {Countries.Count} países cargados.");
        }
        catch (Exception e)
        {
            Status = "No se pudieron cargar los países: " + e.Message;
            _engine.Logger.Error("Tendencias: fallo al cargar los países", e);
        }
        finally { IsBusy = false; LoadingCountries = false; }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        // Red de seguridad: si por lo que sea no se cargaron al abrir la pestaña, se cargan aquí.
        if (Countries.Count == 0) await EnsureCountriesAsync();
        if (SelectedCountry is not { } pais) { Status = "Selecciona antes un país."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Status = $"Consultando el Top {ListSize} de {pais.Name}…";
        _engine.Logger.Head($"Tendencias: Top {ListSize} de {pais.Name}");
        try
        {
            if (!_engine.Library.IsScanned && _engine.Library.Folders.Count > 0)
            {
                Status = "Escaneando biblioteca…";
                await _engine.Library.ScanAsync();
            }

            var chart = await _engine.Charts.GetChartAsync(FuenteElegida, pais, ListSize, _cts.Token);
            var indice = BuildLibraryIndex();

            Rows.Clear();
            foreach (var t in chart)
            {
                indice.TryGetValue(Clave(t.Artist, t.Title), out var ruta);
                Rows.Add(new TrendRow
                {
                    Position = t.Position, Artist = t.Artist, Title = t.Title,
                    DurText = t.DurText, FilePath = ruta ?? "", SpotifyId = t.SpotifyId,
                });
            }

            RowsView.Refresh();
            var tengo = Rows.Count(r => r.Tengo);
            Resumen = $"{tengo} de {Rows.Count} disponibles en la biblioteca ({(Rows.Count == 0 ? 0 : tengo * 100 / Rows.Count)}%)";
            Status = $"Top {Rows.Count} de {pais.Name}. {Resumen}. Faltan {Rows.Count - tengo}.";
            _engine.Logger.Sum($"Tendencias {pais.Name}: tienes {tengo} de {Rows.Count}");

            // La tabla ya está en pantalla. Las duraciones se rellenan después, sin bloquear nada:
            // son 50 consultas a Spotify de una en una y no tiene sentido hacer esperar por ellas.
            _ = RellenarDuracionesAsync(Rows.ToList());
        }
        catch (OperationCanceledException) { Status = "Consulta cancelada."; }
        catch (Exception e) { Status = "No se pudieron cargar las tendencias: " + e.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    /// <summary>
    /// Pide a Spotify la duración exacta de cada canción del chart, usando el ID que trae cada fila.
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
        if (puestas > 0) _engine.Logger.Detail($"Tendencias: {puestas} duraciones traídas de Spotify.");
    }

    /// <summary>
    /// Índice de la biblioteca por artista+título normalizados. Se indexa tanto por los tags como
    /// por el nombre del archivo, porque en material de DJ los tags no siempre están.
    /// </summary>
    private Dictionary<string, string> BuildLibraryIndex()
    {
        // Se guarda también CON QUÉ prioridad entró cada ruta, para poder cambiarla si aparece una
        // copia mejor. Antes se usaba TryAdd, que se queda con la primera que aparezca en el
        // escaneo: puro azar del orden de las carpetas.
        var mejor = new Dictionary<string, (string Ruta, int Orden)>(StringComparer.Ordinal);
        var apartadas = 0;
        var mejoras = 0;

        void Ofrecer(string clave, Track t, int orden)
        {
            if (mejor.TryGetValue(clave, out var actual))
            {
                if (orden >= actual.Orden) return;   // la que ya había es igual de buena o mejor
                mejoras++;
            }
            mejor[clave] = (t.FilePath, orden);
        }

        foreach (var t in _engine.Library.Tracks)
        {
            if (NoCuentaComoTenerla(t)) { apartadas++; continue; }

            var orden = PrioridadVersion.OrdenDe(t.FileName);

            if (!string.IsNullOrEmpty(t.Artist) && !string.IsNullOrEmpty(t.Title))
                Ofrecer(Clave(t.Artist, t.Title), t, orden);

            var pr = Core.Pipeline.FileNameParser.Parse(t.FileName);
            if (pr.FnArtist.Length > 0 && pr.QTitle.Length > 0)
                Ofrecer(Clave(pr.FnArtist, pr.QTitle), t, orden);
        }

        if (apartadas > 0)
            _engine.Logger.Detail($"Tendencias: {apartadas} acapellas y mashups no cuentan para el «lo tengo».");
        if (mejoras > 0)
            _engine.Logger.Detail($"Tendencias: {mejoras} veces se prefirió una versión mejor teniendo varias copias.");

        return mejor.ToDictionary(kv => kv.Key, kv => kv.Value.Ruta, StringComparer.Ordinal);
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

    /// <summary>Clave de comparación: artista principal + título, sin adornos ni versiones.</summary>
    private static string Clave(string artist, string title)
    {
        var a = TextUtils.Nk(PrimerArtista(artist));
        var t = TextUtils.Nk(Descriptors.CleanKeywords(title));
        return a + "|" + t;
    }

    private static string PrimerArtista(string s)
        => System.Text.RegularExpressions.Regex.Split(s ?? "", @"(?i)\s*(?:,| x | vs\.?| feat\.?| ft\.?|&)\s*")
                 .FirstOrDefault()?.Trim() ?? "";


    /// <summary>
    /// Guarda como lista M3U8 las canciones del chart que ya tienes, en el orden del top. Sirve
    /// para abrirla directamente en rekordbox o Engine sin tener que copiar archivos.
    /// </summary>
    public string ExportarM3u(string destino)
    {
        var tengo = Rows.Where(r => r.Tengo).OrderBy(r => r.Position).ToList();
        if (tengo.Count == 0) { Status = "Ninguna de esta lista está en la biblioteca."; return "vacio"; }

        var items = tengo.Select(r => new PlaylistItem(r.FilePath, r.Artist, r.Title, 0));
        var err = PlaylistWriter.Write(destino, items);
        Status = err.Length == 0
            ? $"Lista guardada con {tengo.Count} canciones: {System.IO.Path.GetFileName(destino)}"
            : "No se pudo guardar la lista: " + err;
        return err;
    }
    /// <summary>Copia a una carpeta las canciones del chart que ya tienes (no las mueve).</summary>
    public async Task CopyToFolderAsync(string destino)
    {
        if (IsBusy) return;
        var tengo = Rows.Where(r => r.Tengo).ToList();
        if (tengo.Count == 0) { Status = "Ninguna de esta lista está en la biblioteca."; return; }

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
                        // El número de posición delante mantiene el orden del chart en el explorador.
                        var nombre = $"{r.Position:00} - {Path.GetFileName(r.FilePath)}";
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
            _engine.Logger.Sum($"Tendencias: copiadas {copiadas} a {destino}");
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
