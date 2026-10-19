using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Lo que los diálogos de una canción (compatibles, versiones, ordenar una selección) necesitan del
/// resto de la aplicación: una instantánea de la biblioteca, las fichas y el reproductor.
///
/// Se toma en el hilo de la interfaz, que es el único que toca la colección de la biblioteca; copiar
/// las referencias es inmediato, y lo caro (recorrerlas) va después en Task.Run. Separado del motor
/// para poder probar los diálogos sin montarlo.
/// </summary>
public sealed class EntornoCanciones
{
    public IReadOnlyList<Track> Biblioteca { get; }
    public IReadOnlyDictionary<string, FichaDj> Fichas { get; }
    public AudioPreview? Preview { get; }

    /// <summary>La biblioteca de la que sale la instantánea: el índice de versiones se guarda por ella.</summary>
    public LibraryStore? Store { get; }

    public EntornoCanciones(IReadOnlyList<Track> biblioteca, IReadOnlyDictionary<string, FichaDj>? fichas = null,
                            AudioPreview? preview = null, LibraryStore? store = null)
    {
        Biblioteca = biblioteca;
        Fichas = fichas ?? new Dictionary<string, FichaDj>(StringComparer.OrdinalIgnoreCase);
        Preview = preview;
        Store = store;
    }

    public static EntornoCanciones De(AppEngine engine)
    {
        var fichas = new Dictionary<string, FichaDj>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in engine.Fichas.Rutas)
            if (engine.Fichas.Obtener(r) is { } f) fichas[r] = f;
        return new EntornoCanciones(engine.Library.Tracks.ToList(), fichas, engine.Preview, engine.Library);
    }

    public FichaDj? FichaDe(Track t) => Fichas.GetValueOrDefault(t.FilePath);

    /// <summary>La canción de la biblioteca con esa ruta, o null si ya no está.</summary>
    public Track? Buscar(string? ruta)
        => string.IsNullOrEmpty(ruta) ? null
           : Biblioteca.FirstOrDefault(t => string.Equals(t.FilePath, ruta, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Escucha o para una canción. Devuelve el motivo si no se pudo ("" si fue bien): el archivo puede
    /// haberse movido o estar en una unidad desconectada.
    /// </summary>
    public string Escuchar(string ruta)
    {
        if (Preview == null || ruta.Length == 0) return "";
        try { Preview.Toggle(ruta); return ""; }
        catch (Exception e) { return "No se pudo reproducir: " + e.Message; }
    }

    /// <summary>Si lo que suena es una de estas rutas, lo para. Al cerrar un diálogo, para lo que se puso desde él.</summary>
    public void PararSiSuena(IEnumerable<string> rutas)
    {
        if (Preview?.CurrentPath is not { } actual) return;
        if (rutas.Contains(actual, StringComparer.OrdinalIgnoreCase)) Preview.Stop();
    }

    /// <summary>«Artista – Título», o el nombre del archivo si faltan las etiquetas.</summary>
    public static string Nombre(Track t)
    {
        var titulo = string.IsNullOrWhiteSpace(t.Title) ? Path.GetFileNameWithoutExtension(t.FilePath) : t.Title.Trim();
        return string.IsNullOrWhiteSpace(t.Artist) ? titulo : $"{t.Artist.Trim()} – {titulo}";
    }

    /// <summary>«Tono 8A · 124 BPM», con lo que falte dicho.</summary>
    public static string TonoYTempo(Track t)
        => $"{(t.KeyCamelot.Length > 0 ? "Tono " + t.KeyCamelot : "Sin tonalidad")} · {(t.Bpm > 0 ? t.Bpm + " BPM" : "sin BPM")}";
}

/// <summary>Una fila de la lista de compatibles.</summary>
public sealed class CompatibleFila : IFilaConArchivo
{
    public Compatible Dato { get; }
    public CompatibleFila(Compatible dato) => Dato = dato;
    string IFilaConArchivo.RutaArchivo => FilePath;

    public string FilePath => Dato.Track.FilePath;
    public string Archivo => Dato.Track.FileName;
    public string Titulo => string.IsNullOrWhiteSpace(Dato.Track.Title) ? Path.GetFileNameWithoutExtension(FilePath) : Dato.Track.Title;
    public string Artista => Dato.Track.Artist ?? "";
    public string Tipo => IndiceVersiones.Nombre(Dato.Tipo);
    public uint BpmValor => Dato.Track.Bpm;
    public string Bpm => Dato.Track.Bpm > 0 ? Dato.Track.Bpm.ToString() : "";
    public string Tono => Dato.Track.KeyCamelot;
    public string Encaje => OrdenMezcla.Nombre(Dato.Tono);
    public string Diferencia => Dato.TextoDiferencia;
    public double Coste => Dato.Coste;
}

/// <summary>Qué tipo de piezas se enseñan en la lista de compatibles.</summary>
public enum FiltroCompatibles { Todas, SinAcapellas, SoloAcapellas }

/// <summary>
/// «Buscar compatibles»: qué entra bien después de una canción, o sobre qué base lanzar una acapella.
/// Las reglas están en <see cref="Compatibles"/>; aquí solo se presentan, se filtran y se exportan.
/// </summary>
public sealed partial class CompatiblesViewModel : ObservableObject
{
    /// <summary>
    /// Cuántas se enseñan como mucho. En una biblioteca grande hay cientos de compatibles con cualquier
    /// canción (MEDIDO: una media de 585 en una de 13.590), y a partir de las primeras decenas ya
    /// entran peor de tempo; la lista va de la que mejor entra a la que peor, así que se corta por abajo.
    /// </summary>
    public const int Maximo = 300;

    private readonly EntornoCanciones _entorno;
    private ResultadoCompatibles? _resultado;
    private readonly HashSet<string> _escuchadas = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<CompatibleFila> Filas { get; } = new();

    /// <summary>
    /// Lo que enseña la tabla: las filas en el orden en que se ven. La tabla deja ordenar por columnas
    /// (por BPM, por tipo…), y la lista M3U8 se guarda en ese orden, que es el que el DJ tiene delante.
    /// </summary>
    public DataGridCollectionView Vista { get; }

    public IReadOnlyList<Opcion<FiltroCompatibles>> Filtros { get; } = new[]
    {
        new Opcion<FiltroCompatibles>(FiltroCompatibles.Todas, "Todas"),
        new Opcion<FiltroCompatibles>(FiltroCompatibles.SinAcapellas, "Sin acapellas"),
        new Opcion<FiltroCompatibles>(FiltroCompatibles.SoloAcapellas, "Solo acapellas"),
    };

    [ObservableProperty] private Track? _consultada;
    [ObservableProperty] private Opcion<FiltroCompatibles> _filtro;
    [ObservableProperty] private bool _incluirSaltos;
    [ObservableProperty] private CompatibleFila? _seleccionada;
    [ObservableProperty] private string _titulo = "";
    [ObservableProperty] private string _detalle = "";
    [ObservableProperty] private string _aviso = "";
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _ocupado;
    [ObservableProperty] private bool _esAcapella;

    public CompatiblesViewModel(EntornoCanciones entorno, Track? consultada)
    {
        _entorno = entorno;
        _filtro = Filtros[0];
        _consultada = consultada;
        Vista = new DataGridCollectionView(Filas);
    }

    public bool HayFilas => Filas.Count > 0;

    /// <summary>Nombre que se propone para la lista M3U8.</summary>
    public string NombreSugerido => Consultada == null ? "Compatibles" : $"Compatibles con {EntornoCanciones.Nombre(Consultada)}";

    /// <summary>Busca de nuevo con la canción y las opciones actuales. El recorrido va fuera del hilo de la interfaz.</summary>
    public async Task BuscarAsync()
    {
        if (Consultada is not { } x)
        {
            Titulo = "Buscar compatibles";
            Detalle = "";
            Aviso = "Esa canción ya no está en la biblioteca. Vuelve a escanear y prueba otra vez.";
            _resultado = null;
            Rellenar();
            return;
        }

        Ocupado = true;
        Status = "Buscando…";
        var opciones = new OpcionesCompatibles { IncluirSaltos = IncluirSaltos };
        try
        {
            _resultado = await Task.Run(() => Compatibles.Buscar(x, _entorno.Biblioteca, opciones, _entorno.FichaDe));
        }
        finally { Ocupado = false; }

        EsAcapella = _resultado.EsAcapella;
        Titulo = (EsAcapella ? "Bases para «" : "Compatibles con «") + EntornoCanciones.Nombre(x) + "»";
        var margen = $"{_resultado.Margen * 100:0} %";
        var margenVoz = $"{opciones.MargenBpmAcapella * 100:0} %";
        Detalle = EntornoCanciones.TonoYTempo(x) + ", según las etiquetas del archivo. "
                  + (EsAcapella
                      ? $"Es una acapella: se buscan bases en el mismo tono o a un paso en la rueda Camelot, a menos de un {margen} de tempo, sin otras acapellas ni mashups."
                      : $"Entran las del mismo tono o a un paso en la rueda Camelot{(IncluirSaltos ? " (o a dos, con los saltos)" : "")}, a menos de un {margen} de tempo (un {margenVoz} para las acapellas), contando doble y mitad.");
        Aviso = _resultado.Aviso;
        Rellenar();
        Status = "";
    }

    partial void OnIncluirSaltosChanged(bool value) => _ = BuscarAsync();

    partial void OnFiltroChanged(Opcion<FiltroCompatibles> value) => Rellenar();

    /// <summary>Las del resultado que pasan el filtro, hasta <see cref="Maximo"/>.</summary>
    internal static IReadOnlyList<Compatible> Filtrar(IEnumerable<Compatible> todas, FiltroCompatibles filtro) => filtro switch
    {
        FiltroCompatibles.SinAcapellas => todas.Where(c => c.Tipo != TipoPieza.Acapella).ToList(),
        FiltroCompatibles.SoloAcapellas => todas.Where(c => c.Tipo == TipoPieza.Acapella).ToList(),
        _ => todas.ToList(),
    };

    private void Rellenar()
    {
        Filas.Clear();
        if (_resultado == null) { Resumen = ""; OnPropertyChanged(nameof(HayFilas)); return; }

        var filtradas = Filtrar(_resultado.Canciones, EsAcapella ? FiltroCompatibles.Todas : Filtro.Valor ?? FiltroCompatibles.Todas);
        foreach (var c in filtradas.Take(Maximo)) Filas.Add(new CompatibleFila(c));
        Resumen = TextoResumen(filtradas.Count, _resultado.SinBpm);
        OnPropertyChanged(nameof(HayFilas));
    }

    /// <summary>«585 compatibles · se muestran las 300 que mejor entran · 12 sin BPM…».</summary>
    internal static string TextoResumen(int total, int sinBpm)
    {
        var texto = total == 0 ? "Ninguna compatible"
                  : total == 1 ? "1 compatible"
                  : $"{total} compatibles";
        if (total > Maximo) texto += $" · se muestran las {Maximo} que mejor entran";
        if (sinBpm > 0)
            texto += sinBpm == 1
                ? " · 1 con tono compatible no tiene BPM en las etiquetas y no se ha tenido en cuenta"
                : $" · {sinBpm} con tono compatible no tienen BPM en las etiquetas y no se han tenido en cuenta";
        return texto;
    }

    [RelayCommand]
    private void Escuchar()
    {
        if (Seleccionada is not { } f) { Status = "Selecciona antes una canción."; return; }
        _escuchadas.Add(f.FilePath);
        Status = _entorno.Escuchar(f.FilePath);
    }

    [RelayCommand]
    private async Task AbrirCarpetaAsync()
    {
        if (Seleccionada is { } f) await Shell.OpenContainingFolderAsync(f.FilePath);
    }

    /// <summary>
    /// Sigue la cadena: busca las que entran después de la seleccionada. Así se prepara un tramo de
    /// sesión canción a canción sin salir del diálogo.
    /// </summary>
    [RelayCommand]
    private async Task BuscarDesdeEstaAsync()
    {
        if (Seleccionada is not { } f) { Status = "Selecciona antes una canción."; return; }
        Consultada = f.Dato.Track;
        await BuscarAsync();
    }

    /// <summary>
    /// Guarda como M3U8 la canción consultada seguida de las compatibles, en el orden en que se ven en
    /// la tabla (<see cref="Vista"/>): si el DJ la ha ordenado por una columna, en ese. No copia ni
    /// mueve archivos.
    /// </summary>
    public string ExportarM3u(string destino)
    {
        if (Consultada == null || Filas.Count == 0) { Status = "No hay ninguna canción en la lista."; return "vacia"; }
        var items = new[] { Consultada }.Concat(Vista.OfType<CompatibleFila>().Select(f => f.Dato.Track))
            .Select(t => new PlaylistItem(t.FilePath, t.Artist ?? "", string.IsNullOrWhiteSpace(t.Title) ? t.FileName : t.Title, t.DurationSeconds));
        var err = PlaylistWriter.Write(destino, items);
        Status = err.Length == 0
            ? $"Lista guardada con {Filas.Count + 1} canciones: {Path.GetFileName(destino)}"
            : "No se pudo guardar la lista: " + err;
        return err;
    }

    /// <summary>Al cerrar: si suena algo que se puso desde aquí, se para.</summary>
    public void AlCerrar() => _entorno.PararSiSuena(_escuchadas);
}
