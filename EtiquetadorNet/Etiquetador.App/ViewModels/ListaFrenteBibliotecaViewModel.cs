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
    /// </summary>
    protected int Cruzar(IEnumerable<ChartTrack> lista)
    {
        var indice = BuildLibraryIndex();
        Rows.Clear();
        foreach (var t in lista)
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
            _engine.Logger.Detail($"{Etiqueta}: {apartadas} acapellas y mashups no cuentan para el «lo tengo».");
        if (mejoras > 0)
            _engine.Logger.Detail($"{Etiqueta}: {mejoras} veces se prefirió una versión mejor teniendo varias copias.");

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
                        // El número de posición delante mantiene el orden de la lista en el explorador.
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
