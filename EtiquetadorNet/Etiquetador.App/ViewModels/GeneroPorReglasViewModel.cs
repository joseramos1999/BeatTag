using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

// =====================================================================================================
// Asistente · Género por carpeta y por artista (idea P9). Solo reglas, sin IA.
// =====================================================================================================

/// <summary>Una carpeta con canciones sin género, y el género que el usuario le ha asignado.</summary>
public sealed partial class CarpetaGeneroFila : ObservableObject
{
    public CarpetaGeneroFila(CarpetaSinGenero c, string genero, bool conSubcarpetas = false)
    {
        Datos = c;
        _genero = genero;
        ConSubcarpetas = conSubcarpetas;
    }

    public CarpetaSinGenero Datos { get; }

    /// <summary>La añadió el usuario a mano: sus cifras incluyen las subcarpetas.</summary>
    public bool ConSubcarpetas { get; }

    /// <summary>El género de la regla de esta carpeta ("" = sin regla).</summary>
    [ObservableProperty] private string _genero;

    partial void OnGeneroChanged(string value) => OnPropertyChanged(nameof(Regla));

    /// <summary>La regla, para la columna «Género asignado».</summary>
    public string Regla => Genero.Length > 0 ? Genero : "Sin asignar";

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

/// <summary>Un género propuesto para una canción sin género.</summary>
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
    public string Regla => Propuesta.Regla == ReglaGenero.Carpeta ? "Carpeta" : "Artista";
    public string PorQue => Propuesta.PorQue;
    public string Carpeta => GeneroPorReglas.NombreCarpeta(Path.GetDirectoryName(Track.FilePath) ?? "");
}

public sealed partial class GeneroPorReglasViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    private MapaGenerosCarpeta? _mapa;

    public GeneroPorReglasViewModel(AsistenteViewModel padre) => _p = padre;

    [ObservableProperty] private ObservableCollection<CarpetaGeneroFila> _carpetas = new();

    /// <summary>Se sustituye entera al proponer, como en «Limpiar la publicidad de los packs».</summary>
    [ObservableProperty] private ObservableCollection<GeneroReglaFila> _propuestas = new();

    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _resumenCarpetas = "";

    /// <summary>Has cambiado reglas de carpeta y las propuestas aún no las reflejan.</summary>
    [ObservableProperty] private bool _reglasSinAplicar;

    /// <summary>
    /// Los géneros que ya usa la biblioteca, normalizados y de más a menos canciones: el desplegable de
    /// la regla. Se puede escribir otro.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<string> _generosDisponibles = Array.Empty<string>();

    /// <summary>El género elegido en el desplegable, para ponerlo a las carpetas seleccionadas.</summary>
    [ObservableProperty] private string _generoElegido = "";

    /// <summary>Lo que pasó al leer el mapa (apartado por ilegible, bloqueado…), para decirlo una vez en el estado.</summary>
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
                _avisoMapa = "⚠ " + _mapa.AvisoCarga + " ";
            }
            return _mapa;
        }
    }

    /// <summary>El aviso de lectura del mapa, la primera vez que se pide; después, nada.</summary>
    private string AvisoMapaUnaVez()
    {
        var aviso = _avisoMapa;
        _avisoMapa = "";
        return aviso;
    }

    [RelayCommand]
    private Task ProponerAsync() => _p.TrabajarAsync("Buscando canciones sin género…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var canciones = _p.Engine.Library.Tracks.ToList();   // instantánea en el hilo de la interfaz
        var reglas = new Dictionary<string, string>(Mapa.Reglas, StringComparer.OrdinalIgnoreCase);
        var calculo = await Task.Run(() =>
        {
            var generos = GeneroPorReglas.GenerosDeLaBiblioteca(canciones).Select(g => g.Genero).ToList();
            // Lo escrito en el mapa, como lo escribe la biblioteca («reguetón» → «Reggaeton»).
            var mapa = reglas.ToDictionary(r => r.Key, r => GeneroPorReglas.ComoEnLaBiblioteca(r.Value, generos), StringComparer.OrdinalIgnoreCase);
            var carpetas = GeneroPorReglas.Carpetas(canciones);
            var conocidas = new HashSet<string>(carpetas.Select(c => c.Ruta), StringComparer.OrdinalIgnoreCase);
            // Las carpetas con regla que no salen en la lista (ya no les falta género, o se añadieron a
            // mano por sus subcarpetas) también se enseñan: si no, no habría forma de quitarles la regla.
            var extra = reglas.Keys.Where(r => !conocidas.Contains(r)).Select(r => GeneroPorReglas.ConSubcarpetas(canciones, r)).ToList();
            var propuestas = GeneroPorReglas.Proponer(canciones, mapa, ct);
            var sinGenero = canciones.Count(GeneroPorReglas.SinGenero);
            return (generos, carpetas, extra, propuestas, sinGenero, Variantes: GeneroPorReglas.VariantesSinUnificar(canciones));
        }, ct);

        GenerosDisponibles = calculo.generos;
        Carpetas = new ObservableCollection<CarpetaGeneroFila>(
            calculo.carpetas.Select(c => Fila(c, false)).Concat(calculo.extra.Select(c => Fila(c, true))));

        // Lo que ya habías marcado, desmarcado o corregido en una propuesta que sigue igual se conserva.
        var antes = new Dictionary<string, GeneroReglaFila>(StringComparer.Ordinal);
        foreach (var f in Propuestas) antes.TryAdd(f.Ruta + "|" + f.Propuesta.Genero, f);
        var porRuta = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var t in canciones) porRuta.TryAdd(t.FilePath, t);
        var filas = new List<GeneroReglaFila>();
        foreach (var p in calculo.propuestas.OrderBy(p => p.Regla).ThenBy(p => p.Ruta, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!porRuta.TryGetValue(p.Ruta, out var t)) continue;
            var fila = new GeneroReglaFila(p, t);
            if (antes.TryGetValue(p.Ruta + "|" + p.Genero, out var vieja))
            {
                fila.Aplicar = vieja.Aplicar;
                fila.Genero = vieja.Genero;
            }
            filas.Add(fila);
        }
        Propuestas = new ObservableCollection<GeneroReglaFila>(filas);
        ReglasSinAplicar = false;

        var porCarpeta = calculo.propuestas.Count(p => p.Regla == ReglaGenero.Carpeta);
        var porArtista = calculo.propuestas.Count - porCarpeta;
        ResumenCarpetas = $"{calculo.carpetas.Count} carpetas con canciones sin género · {reglas.Count} con regla";
        Resumen = $"{calculo.sinGenero} canciones sin género · {porCarpeta} por su carpeta · {porArtista} por su artista "
                  + $"({calculo.propuestas.Count(p => p.Regla == ReglaGenero.Artista && p.Marcada)} marcadas)";
        _p.Status = AvisoMapaUnaVez()
                    + (calculo.sinGenero == 0 ? "Todas las canciones tienen género." : Resumen + ". " + Consejo(reglas.Count, porArtista))
                    + (calculo.Variantes > 0
                        ? $" Hay {calculo.Variantes} géneros escritos de varias formas o que no son un género: pasa antes «Unificar géneros» para que las cuentas por artista sean más fiables."
                        : "");
    });

    internal static string Consejo(int reglas, int porArtista)
        => reglas == 0
            ? "Empieza por las carpetas de arriba: selecciónalas, elige su género y pulsa «Poner a las seleccionadas»."
            : porArtista > 0
                ? "Las del artista salen marcadas solo cuando casi todas sus canciones llevan el mismo género: revisa el resto."
                : "Revisa las propuestas y aplica las marcadas.";

    private CarpetaGeneroFila Fila(CarpetaSinGenero c, bool conSubcarpetas) => new(c, Mapa.De(c.Ruta), conSubcarpetas);

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

    /// <summary>Quita la regla de las carpetas seleccionadas: sus canciones vuelven a depender solo del artista.</summary>
    public void QuitarRegla(IReadOnlyList<CarpetaGeneroFila> carpetas) => CambiarRegla(carpetas, "");

    private void CambiarRegla(IReadOnlyList<CarpetaGeneroFila> carpetas, string? genero)
    {
        if (carpetas.Count == 0) { _p.Status = "Selecciona antes una o varias carpetas de la lista."; return; }
        var valor = GeneroPorReglas.ComoEnLaBiblioteca(genero, GenerosDisponibles);
        var cambiadas = 0;
        foreach (var c in carpetas)
        {
            if (!Mapa.Poner(c.Ruta, valor)) continue;
            c.Genero = valor;
            cambiadas++;
        }
        if (cambiadas == 0) { _p.Status = valor.Length == 0 ? "Esas carpetas no tenían regla." : $"Esas carpetas ya tenían «{valor}»."; return; }

        ReglasSinAplicar = true;
        var err = Mapa.Guardar();
        if (err.Length > 0)
        {
            _p.Engine.Logger.Err("Género por carpeta: no se pudo guardar el mapa: " + err);
            _p.Status = "⚠ La regla se usará en esta sesión, pero no se ha podido guardar: " + err;
            return;
        }
        var cuales = cambiadas == 1 ? $"«{carpetas[0].Nombre}»" : $"{cambiadas} carpetas";
        _p.Status = (valor.Length == 0 ? $"Regla quitada de {cuales}." : $"{cuales} → {valor}.")
                    + " Pulsa «Proponer géneros» para ver a qué canciones se aplica.";
    }

    [RelayCommand]
    private void MarcarTodas() { foreach (var f in Propuestas) f.Aplicar = f.Genero.Trim().Length > 0; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Propuestas) f.Aplicar = false; }

    public IReadOnlyList<GeneroReglaFila> Marcadas => Propuestas.Where(f => f.Aplicar && f.Genero.Trim().Length > 0).ToList();

    /// <summary>Los textos de la confirmación de «Aplicar las marcadas»: cuántas, por qué regla, y lo que hay que saber.</summary>
    internal static (string Cuerpo, string Nota) Confirmacion(IReadOnlyList<GeneroReglaFila> marcadas)
    {
        var porCarpeta = marcadas.Count(f => f.Propuesta.Regla == ReglaGenero.Carpeta);
        var porArtista = marcadas.Count - porCarpeta;
        var cuerpo = $"Se pondrá género a {marcadas.Count} canciones que no tienen: {porCarpeta} por su carpeta y {porArtista} por su artista.";
        var nota = "Solo se escribe en las que sigan sin género: si alguna ya tiene uno, se respeta. "
                 + "rekordbox no vuelve a leer las etiquetas de las pistas ya importadas hasta que usas «Recargar etiqueta». "
                 + "Se puede deshacer desde Enriquecer.";
        var debiles = marcadas.Count(f => !f.Propuesta.Marcada);
        if (debiles > 0) nota = $"⚠ {debiles} no salían marcadas porque la regla no es tan fuerte: comprueba que las has revisado. " + nota;
        return (cuerpo, nota);
    }

    /// <summary>Los cambios: solo el género, y solo si el archivo sigue sin él.</summary>
    internal static List<CambioEtiquetas> Cambios(IEnumerable<GeneroReglaFila> marcadas)
        => marcadas.Select(f => new CambioEtiquetas(f.Ruta) { GeneroNuevo = f.Genero.Trim() }).ToList();

    /// <summary>Escribe los géneros marcados. La confirmación la pide la vista.</summary>
    public Task AplicarAsync() => _p.EscribirArchivosAsync("Escribiendo géneros…", async ct =>
    {
        var marcadas = Marcadas;
        if (marcadas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        _p.Engine.ReleaseAudio();
        var cambios = Cambios(marcadas);
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => EscrituraPorReglas.EscribirConHistorial(_p.Engine, cambios, progreso, ct, "Género por reglas"));

        var hechos = new HashSet<string>(res.Escritos.Concat(res.Respetados), StringComparer.OrdinalIgnoreCase);
        var quedan = Propuestas.Where(f => !hechos.Contains(f.Ruta)).ToList();
        if (quedan.Count != Propuestas.Count) Propuestas = new ObservableCollection<GeneroReglaFila>(quedan);
        if (res.Escritos.Count > 0) await _p.Engine.Library.RefrescarAsync(res.Escritos);   // solo lo escrito, sin recorrer las carpetas

        _p.Status = $"Género puesto en {res.Escritos.Count} canciones" + EscrituraPorReglas.Cola(res)
                    + (res.Escritos.Count > 0
                        ? " rekordbox no vuelve a leer las etiquetas de las pistas que ya tiene importadas: selecciónalas allí y usa «Recargar etiqueta»."
                        : "");
    });
}
