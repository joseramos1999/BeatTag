using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción de la lista de pendientes, como se ve en la tabla.</summary>
public sealed class PendienteFila
{
    private static readonly IBrush ConseguidaBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A));

    public Pendiente P { get; }
    public PendienteFila(Pendiente p) => P = p;

    public string Id => P.Id;
    public string Artista => P.Artista;
    public string Titulo => P.Titulo;
    public string Origen => P.Origen;
    public bool Conseguida => P.EstaConseguida;
    public string Estado => P.Conseguida is { } f ? "Conseguida el " + f.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "Pendiente";

    /// <summary>Para ordenar la columna Estado: primero las pendientes, después por fecha.</summary>
    public DateTime EstadoOrden => P.Conseguida ?? DateTime.MinValue;

    public string Alta => P.Alta.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Para ordenar por fecha de alta: el texto dd/MM/yyyy no se ordena bien.</summary>
    public DateTime AltaFecha => P.Alta;

    public string Archivo => P.Archivo.Length > 0 ? Path.GetFileName(P.Archivo) : "";
    public bool TieneEnlace => P.Enlace.Length > 0;
    public IBrush FondoBrush => P.EstaConseguida ? ConseguidaBrush : Brushes.Transparent;

    /// <summary>Las conseguidas se ven tachadas: es lo que se espera de una lista de pendientes.</summary>
    public TextDecorationCollection? Tachado => P.EstaConseguida ? TextDecorations.Strikethrough : null;
}

/// <summary>Una lista seguida, como se ve en la tabla de Pendientes.</summary>
public sealed class ListaSeguidaFila
{
    public ListaSeguida L { get; }

    /// <summary>Lo que falló al revisarla en esta sesión, o vacío.</summary>
    public string Error { get; }

    public ListaSeguidaFila(ListaSeguida l, string error = "")
    {
        L = l;
        Error = error;
    }

    public string Id => L.Id;
    public string Nombre => L.Nombre;

    public string Tipo => L.Tipo == TipoListaSeguida.Chart
        ? $"Chart de {L.Fuente}"
        : L.Clave.StartsWith("deezer:", StringComparison.Ordinal) ? "Playlist de Deezer"
        : L.Clave.StartsWith("spotify:", StringComparison.Ordinal) ? "Playlist de Spotify"
        : "Playlist";

    public string Revisada => L.Revisada?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "";

    /// <summary>«3 desde 12/10». Hasta que no se revisa un día distinto del de la primera lectura, no hay con qué comparar.</summary>
    public string Nuevas => L.Referencia == null
        ? "A partir de mañana"
        : $"{L.Nuevas.Count} desde {L.Referencia.Fecha.ToString("dd/MM", CultureInfo.InvariantCulture)}";

    public int NuevasQueFaltan => L.Nuevas.Count(n => !n.LaTienes);
    public string TeFaltan => L.Referencia == null ? "" : NuevasQueFaltan.ToString(CultureInfo.InvariantCulture);

    /// <summary>El error de la última revisión o, si fue bien, por qué la lectura puede estar incompleta.</summary>
    public string Aviso => Error.Length > 0 ? Error : L.Aviso;
}

/// <summary>
/// «Buscar en mi pool»: abre en el navegador la búsqueda del pool de descargas del usuario, con una
/// dirección que él mismo configura en Ajustes.
///
/// BeatTag no inicia sesión en ningún pool ni lee nada de él: solo abre una dirección, y el navegador
/// usa la sesión que el usuario ya tenga abierta. Cada pool busca a su manera, así que la dirección
/// es una plantilla con {q} donde va lo que se busca.
/// </summary>
public static class BusquedaEnPool
{
    public const string Marcador = "{q}";

    /// <summary>Un ejemplo para la ayuda de Ajustes.</summary>
    public const string Ejemplo = "https://www.mipool.com/search?q={q}";

    /// <summary>"" si la plantilla vale (o está vacía, que es no tenerla configurada); si no, por qué.</summary>
    public static string Validar(string? plantilla)
    {
        var p = (plantilla ?? "").Trim();
        if (p.Length == 0) return "";
        if (p.IndexOf(Marcador, StringComparison.OrdinalIgnoreCase) < 0)
            return "Falta {q} en la dirección: es donde se pone el artista y el título que se buscan.";
        var prueba = p.Replace(Marcador, "prueba", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(prueba, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return "La dirección tiene que empezar por https:// (o http://).";
        return "";
    }

    // «(feat. X)», «[with X]», «(con X)» o un « feat. X» al final del título.
    private static readonly Regex Colaboracion = new(
        @"\s*[\(\[]\s*(?:feat\.?|ft\.?|featuring|with|con)\s[^\)\]]*[\)\]]|\s+(?:feat\.?|ft\.?|featuring)\s.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Lo que se busca: el primer artista del crédito y el título sin las colaboraciones. Los buscadores
    /// de los pools exigen casi siempre todas las palabras, y un crédito con cuatro artistas escritos en
    /// otro orden, o un «(feat. …)» que el pool no pone igual, dejaban la búsqueda sin resultados. La
    /// versión («Remix», «Extended») se deja: si la lista pide un remix, es ese el que hay que buscar.
    /// </summary>
    public static string TextoBusqueda(string? artista, string? titulo)
    {
        var primero = IndiceBiblioteca.PartirArtistas(artista).FirstOrDefault() ?? "";
        var t = Colaboracion.Replace(titulo ?? "", "").Trim();
        if (t.Length == 0) t = (titulo ?? "").Trim();
        return Regex.Replace($"{primero} {t}", @"\s+", " ").Trim();
    }

    /// <summary>La dirección que se abre, o "" con el motivo en <paramref name="motivo"/>.</summary>
    public static string Url(string? plantilla, string? artista, string? titulo, out string motivo)
    {
        var p = (plantilla ?? "").Trim();
        if (p.Length == 0)
        {
            motivo = "Para buscar en tu pool, escribe antes su dirección de búsqueda en Ajustes → Pendientes "
                   + $"(por ejemplo, {Ejemplo}).";
            return "";
        }
        var error = Validar(p);
        if (error.Length > 0) { motivo = "La dirección de búsqueda del pool que hay en Ajustes no es válida. " + error; return ""; }
        var q = TextoBusqueda(artista, titulo);
        if (q.Length == 0) { motivo = "Esa canción no tiene artista ni título que buscar."; return ""; }
        motivo = "";
        return p.Replace(Marcador, Uri.EscapeDataString(q), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Página Pendientes: las canciones que faltan y quieres conseguir, que se tachan solas al aparecer
/// en la biblioteca, y las playlists y charts que sigues.
/// </summary>
public partial class PendientesViewModel : ViewModelBase, IEstadoPagina
{
    private readonly AppEngine _engine;
    private readonly SeguimientoListas _seguimiento;

    public ObservableCollection<PendienteFila> Filas { get; } = new();

    /// <summary>Vista de la tabla (filtra por «solo pendientes»).</summary>
    public DataGridCollectionView FilasView { get; }

    public ObservableCollection<ListaSeguidaFila> Listas { get; } = new();

    [ObservableProperty] private PendienteFila? _seleccionada;
    [ObservableProperty] private ListaSeguidaFila? _listaSeleccionada;
    [ObservableProperty] private bool _soloPendientes;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _resumenListas = "";

    /// <summary>
    /// Nada de esta página bloquea la aplicación: no toca la música. «Actualizar todas» solo lee de la
    /// red y va aparte, en <see cref="Actualizando"/>, para poder seguir trabajando mientras tanto.
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoActualizando))]
    private bool _actualizando;

    public bool NoActualizando => !Actualizando;

    public bool HayListas => Listas.Count > 0;

    private CancellationTokenSource? _cts;

    /// <summary>Lo que falló al revisar cada lista en esta sesión (por Id).</summary>
    private readonly Dictionary<string, string> _errores = new(StringComparer.Ordinal);

    public PendientesViewModel(AppEngine engine)
    {
        _engine = engine;
        _seguimiento = SeguimientoListas.De(engine);
        FilasView = new DataGridCollectionView(Filas);
        _seguimiento.PendientesCambiadas += RellenarFilas;
        _seguimiento.SeguidasCambiadas += RellenarListas;
        RellenarFilas();
        RellenarListas();
        Status = Filas.Count == 0
            ? "Añade canciones desde Tendencias o Playlists con «Añadir a pendientes»."
            : Resumen;
    }

    partial void OnSoloPendientesChanged(bool value)
    {
        FilasView.Filter = value ? o => o is PendienteFila f && !f.Conseguida : null;
        FilasView.Refresh();
    }

    private void RellenarFilas()
    {
        var elegida = Seleccionada?.Id;
        Filas.Clear();
        foreach (var p in _seguimiento.Pendientes.Todas()) Filas.Add(new PendienteFila(p));
        FilasView.Refresh();
        Resumen = TextoResumen(Filas.Count(f => !f.Conseguida), Filas.Count(f => f.Conseguida));
        if (elegida != null) Seleccionada = Filas.FirstOrDefault(f => f.Id == elegida);
    }

    /// <summary>«12 pendientes · 5 conseguidas».</summary>
    internal static string TextoResumen(int pendientes, int conseguidas)
    {
        if (pendientes + conseguidas == 0) return "La lista de pendientes está vacía.";
        var p = pendientes == 1 ? "1 pendiente" : $"{pendientes} pendientes";
        var c = conseguidas == 1 ? "1 conseguida" : $"{conseguidas} conseguidas";
        return $"{p} · {c}";
    }

    private void RellenarListas()
    {
        var elegida = ListaSeleccionada?.Id;
        Listas.Clear();
        foreach (var l in _seguimiento.Seguidas.Todas())
            Listas.Add(new ListaSeguidaFila(l, _errores.GetValueOrDefault(l.Id, "")));
        OnPropertyChanged(nameof(HayListas));
        ResumenListas = Listas.Count == 0
            ? "No sigues ninguna lista. Se siguen desde Tendencias o Playlists con «Seguir esta lista»."
            : Listas.Count == 1 ? "Sigues 1 lista." : $"Sigues {Listas.Count} listas.";
        if (elegida != null) ListaSeleccionada = Listas.FirstOrDefault(f => f.Id == elegida);
    }

    private void DecirError(string que, string error)
    {
        if (error.Length > 0) Status = $"{que} No se ha podido guardar: {error}";
        else Status = que;
    }

    // --- Pendientes ---

    /// <summary>Quita las elegidas (ya confirmado en la vista).</summary>
    public void Quitar(IReadOnlyList<PendienteFila> filas)
    {
        if (filas.Count == 0) { Status = "Elige antes alguna canción de la tabla."; return; }
        var r = _seguimiento.QuitarPendientes(filas.Select(f => f.Id));
        DecirError(r.Hechas == 1 ? "1 canción quitada de pendientes." : $"{r.Hechas} canciones quitadas de pendientes.", r.Error);
    }

    public int CuantasConseguidas => Filas.Count(f => f.Conseguida);

    /// <summary>Quita todas las conseguidas (ya confirmado en la vista).</summary>
    public void QuitarConseguidas()
    {
        var r = _seguimiento.QuitarConseguidas();
        DecirError(r.Hechas == 0 ? "No hay ninguna conseguida que quitar."
                 : r.Hechas == 1 ? "1 conseguida quitada de la lista." : $"{r.Hechas} conseguidas quitadas de la lista.", r.Error);
    }

    /// <summary>
    /// El archivo con el que se tachó no es esa canción (un remix con otro nombre, otra canción con el
    /// mismo título): vuelve a pendiente, y ese archivo ya no la tachará.
    /// </summary>
    public void VolverAPendiente(IReadOnlyList<PendienteFila> filas)
    {
        var conseguidas = filas.Where(f => f.Conseguida).ToList();
        if (conseguidas.Count == 0) { Status = "Elige alguna canción conseguida: las pendientes ya lo están."; return; }
        var r = _seguimiento.VolverAPendiente(conseguidas.Select(f => f.Id));
        DecirError((r.Hechas == 1 ? "1 canción vuelve" : $"{r.Hechas} canciones vuelven") + " a pendientes. El archivo con el que se tacharon ya no cuenta para ellas.", r.Error);
    }

    /// <summary>Abre en el navegador la búsqueda de la canción elegida en el pool configurado en Ajustes.</summary>
    public async Task BuscarEnPoolAsync(PendienteFila? fila)
    {
        if (fila == null) { Status = "Elige antes una canción de la tabla."; return; }
        var url = BusquedaEnPool.Url(_engine.Config.PoolBusquedaUrl, fila.Artista, fila.Titulo, out var motivo);
        if (url.Length == 0) { Status = motivo; return; }
        await Shell.OpenUrlAsync(url);
        Status = $"Buscando «{BusquedaEnPool.TextoBusqueda(fila.Artista, fila.Titulo)}» en tu pool, en el navegador.";
    }

    /// <summary>Abre la canción en la plataforma de la que salió (Spotify, Deezer).</summary>
    public async Task AbrirEnlaceAsync(PendienteFila? fila)
    {
        if (fila == null) { Status = "Elige antes una canción de la tabla."; return; }
        if (!fila.TieneEnlace) { Status = "Esa canción no trae enlace: la lista de la que salió no lo daba."; return; }
        await Shell.OpenUrlAsync(fila.P.Enlace);
    }

    public async Task AbrirCarpetaAsync(PendienteFila? fila)
    {
        if (fila == null || !fila.Conseguida || fila.P.Archivo.Length == 0) { Status = "Elige una canción conseguida para abrir su carpeta."; return; }
        if (!File.Exists(fila.P.Archivo)) { Status = "El archivo ya no está donde se encontró."; return; }
        await Shell.OpenContainingFolderAsync(fila.P.Archivo);
    }

    /// <summary>Las filas que se ven ahora, en el orden de la tabla: es lo que se exporta.</summary>
    public IReadOnlyList<Pendiente> Visibles() => FilasView.Cast<PendienteFila>().Select(f => f.P).ToList();

    /// <summary>Exporta a texto o a CSV según la extensión del destino. Devuelve "" o el error.</summary>
    public string Exportar(string destino, IReadOnlyList<Pendiente> pendientes)
    {
        if (pendientes.Count == 0) { Status = "No hay nada que exportar con el filtro actual."; return "vacio"; }
        var csv = string.Equals(Path.GetExtension(destino), ".csv", StringComparison.OrdinalIgnoreCase);
        try
        {
            // El CSV, con la marca de UTF-8 al principio: sin ella, Excel lee mal los acentos y las eñes.
            File.WriteAllText(destino, csv ? ExportacionPendientes.Csv(pendientes) : ExportacionPendientes.Texto(pendientes),
                              new UTF8Encoding(encoderShouldEmitUTF8Identifier: csv));
            Status = $"{pendientes.Count} canciones exportadas a {Path.GetFileName(destino)}.";
            return "";
        }
        catch (Exception e)
        {
            Status = "No se pudo exportar: " + e.Message;
            return e.Message;
        }
    }

    // --- Listas seguidas ---

    /// <summary>
    /// Vuelve a leer todas las listas que sigues y cuenta, en cada una, las canciones que han entrado
    /// desde la última revisión de un día anterior. No bloquea la aplicación.
    /// </summary>
    [RelayCommand]
    private async Task ActualizarTodasAsync()
    {
        if (Actualizando) return;
        if (Listas.Count == 0) { Status = "No sigues ninguna lista. Se siguen desde Tendencias o Playlists con «Seguir esta lista»."; return; }
        Actualizando = true;
        _cts = new CancellationTokenSource();
        var avance = new Progress<string>(t => Status = t);
        try
        {
            var revisiones = await _seguimiento.ActualizarTodasAsync(avance, _cts.Token);
            _errores.Clear();
            foreach (var r in revisiones.Where(r => r.Error.Length > 0)) _errores[r.Id] = r.Error;
            RellenarListas();
            Status = TextoRevision(revisiones);
        }
        catch (OperationCanceledException) { Status = "Revisión cancelada. Lo ya revisado queda guardado."; }
        catch (Exception e)
        {
            Status = "No se pudieron revisar las listas: " + e.Message;
            _engine.Logger.Error("Listas seguidas: fallo al revisarlas", e);
        }
        finally
        {
            Actualizando = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelarActualizacion() => _cts?.Cancel();

    /// <summary>«4 listas revisadas: 7 canciones nuevas, 5 que no tienes. 1 no se pudo leer.»</summary>
    internal static string TextoRevision(IReadOnlyList<SeguimientoListas.Revision> revisiones)
    {
        var bien = revisiones.Where(r => r.Error.Length == 0).ToList();
        var mal = revisiones.Count - bien.Count;
        var nuevas = bien.Sum(r => r.Nuevas);
        var faltan = bien.Sum(r => r.NuevasQueFaltan);
        var texto = (bien.Count == 1 ? "1 lista revisada" : $"{bien.Count} listas revisadas")
                  + (nuevas == 0 ? ": ninguna canción nueva" : nuevas == 1 ? ": 1 canción nueva" : $": {nuevas} canciones nuevas")
                  + (nuevas == 0 ? "" : faltan == 0 ? ", y ya las tienes todas" : faltan == 1 ? ", 1 que no tienes" : $", {faltan} que no tienes")
                  + ".";
        if (mal > 0) texto += mal == 1 ? " 1 no se pudo leer: mira la columna Aviso." : $" {mal} no se pudieron leer: mira la columna Aviso.";
        if (faltan > 0) texto += " Puedes añadirlas con «Añadir a pendientes las nuevas que faltan».";
        return texto;
    }

    /// <summary>
    /// Añade a pendientes las canciones nuevas que no tienes de las listas elegidas (o de todas, si no
    /// hay ninguna elegida). Se vuelve a mirar la biblioteca: lo conseguido desde la revisión no entra.
    /// </summary>
    public async Task AnadirNuevasAPendientesAsync(IReadOnlyList<ListaSeguidaFila> elegidas)
    {
        var listas = (elegidas.Count > 0 ? elegidas : Listas.ToList()).ToList();
        var candidatas = listas.SelectMany(l => l.L.Nuevas.Where(n => !n.LaTienes)
                                                     .Select(n => new NuevaPendiente(n.Artista, n.Titulo, l.Nombre, n.Enlace)))
                               .ToList();
        if (candidatas.Count == 0)
        {
            Status = listas.Any(l => l.L.Referencia != null)
                ? "No hay canciones nuevas que te falten en " + (elegidas.Count > 0 ? "las listas elegidas." : "las listas que sigues.")
                : "Todavía no hay novedades: las listas se comparan a partir del día siguiente a empezar a seguirlas.";
            return;
        }

        var canciones = _engine.Library.Tracks.ToList();
        var indice = (await IndiceBibliotecaCompartido.De(_engine.Library).ObtenerAsync(canciones)).Indice;
        var faltan = await Task.Run(() => candidatas.Where(c => indice.Buscar(c.Artista, c.Titulo).Length == 0).ToList());
        var yaLasTienes = candidatas.Count - faltan.Count;
        if (faltan.Count == 0) { Status = "Ya tienes en la biblioteca todas las canciones nuevas de esas listas."; return; }

        var r = _seguimiento.AnadirPendientes(faltan);
        var texto = ListaFrenteBibliotecaViewModel.TextoAlta(r.Hechas, r.Otras);
        if (yaLasTienes > 0) texto += yaLasTienes == 1 ? " 1 ya la has conseguido desde la revisión." : $" {yaLasTienes} ya las has conseguido desde la revisión.";
        DecirError(texto, r.Error);
    }

    /// <summary>Deja de seguir las elegidas (ya confirmado en la vista).</summary>
    public void DejarDeSeguir(IReadOnlyList<ListaSeguidaFila> elegidas)
    {
        if (elegidas.Count == 0) { Status = "Elige antes alguna lista de la tabla."; return; }
        var error = _seguimiento.DejarDeSeguir(elegidas.Select(l => l.Id));
        DecirError(elegidas.Count == 1 ? $"Ya no sigues «{elegidas[0].Nombre}»." : $"Ya no sigues esas {elegidas.Count} listas.", error);
    }
}
