using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
// AudioPreview vive en Etiquetador.App.Services (ya importado arriba)

namespace Etiquetador.App.ViewModels;

/// <summary>Una copia dentro de un grupo de duplicados.</summary>
public partial class DupRow : ObservableObject
{
    public string Group { get; init; } = "";

    /// <summary>
    /// Qué grupo es, por posición. La etiqueta <see cref="Group"/> no basta para contar grupos: dos
    /// grabaciones distintas con el mismo nombre y el mismo número de copias dan la misma etiqueta.
    /// </summary>
    public int GrupoId { get; init; }

    public string FileName { get; init; } = "";
    public string Quality { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";

    /// <summary>Marcada para enviar a la papelera.</summary>
    [ObservableProperty] private bool _marcada;

    /// <summary>Es la copia que conviene conservar de su grupo.</summary>
    [ObservableProperty] private bool _esMejor;

    /// <summary>Por qué se considera la mejor (o qué le falta para serlo).</summary>
    [ObservableProperty] private string _motivo = "";

    /// <summary>
    /// Descriptor de edición del archivo ("Aca Out", "Melodic Intro"…), o vacío si es el original.
    /// Al agrupar por audio es lo que distingue una copia sobrante de una herramienta distinta.
    /// </summary>
    public string Version { get; init; } = "";

    /// <summary>Verde para la copia a conservar. El resto sin fondo, para que destaque una sola.</summary>
    public IBrush? Fondo => EsMejor ? VerdeConservar : null;

    /// <summary>
    /// TRANSLÚCIDO a propósito, igual que los verdes de Comprobar audio y Tendencias. Antes era un
    /// pastel opaco (#DCFCE7): en claro se veía bien, pero en modo oscuro el texto es blanco y la
    /// fila quedaba como una franja luminosa ilegible. Con transparencia el verde se mezcla con el
    /// fondo de cada tema y el texto se lee en los dos.
    /// </summary>
    private static readonly IBrush VerdeConservar = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A));

    partial void OnEsMejorChanged(bool value) => OnPropertyChanged(nameof(Fondo));
}

/// <summary>Opción de criterio de duplicados (con etiqueta legible).</summary>
public sealed record DupModeOption(DuplicateMode Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Cómo se decide qué copia conservar de cada grupo.</summary>
public enum DupKeep { PrioridadYCalidad, Calidad, Duracion }

public sealed record DupKeepOption(DupKeep Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Una carpeta de la biblioteca, con su papel en la búsqueda de duplicados.</summary>
public partial class DupFolderOption : ObservableObject
{
    public string Path { get; init; } = "";
    public string Nombre => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;

    [ObservableProperty] private bool _prioritaria;
    [ObservableProperty] private bool _excluida;

    public Action? AlCambiar { get; set; }

    // Prioritaria y excluida se contradicen: una carpeta que no se mira no puede preferirse.
    partial void OnPrioritariaChanged(bool value)
    {
        if (value && Excluida) Excluida = false;
        AlCambiar?.Invoke();
    }

    partial void OnExcluidaChanged(bool value)
    {
        if (value && Prioritaria) Prioritaria = false;
        AlCambiar?.Invoke();
    }
}

/// <summary>Pestaña Duplicados: agrupa canciones repetidas de la biblioteca compartida.</summary>
public partial class DuplicatesViewModel : ScanViewModelBase
{
    public ObservableCollection<DupRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    /// <summary>Carpetas de la biblioteca, para marcarlas como prioritarias o excluidas.</summary>
    public ObservableCollection<DupFolderOption> Folders { get; } = new();

    /// <summary>Cuadro de búsqueda de la tabla.</summary>
    [ObservableProperty] private string _busqueda = "";

    /// <summary>Cuántos grupos quedan a la vista mientras hay búsqueda. Vacío si no se filtra.</summary>
    [ObservableProperty] private string _filtroInfo = "";

    /// <summary>
    /// Aquí el filtro trabaja por GRUPOS enteros, no por filas sueltas. Un duplicado solo significa
    /// algo al lado de su pareja: dejar a la vista la copia que casa con lo buscado y esconder las
    /// otras convertiría la tabla en una lista de canciones sueltas donde no se puede decidir nada.
    /// Basta con que UNA copia del grupo coincida para que se vea el grupo completo.
    /// </summary>
    partial void OnBusquedaChanged(string value)
    {
        if (Busqueda.Trim().Length == 0)
        {
            RowsView.Filter = null;
            RowsView.Refresh();
            FiltroInfo = "";
            return;
        }

        var grupos = Rows.Where(r => BusquedaTexto.Coincide(Busqueda, r.FileName, r.Folder, r.Group))
                         .Select(r => r.Group)
                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RowsView.Filter = o => o is DupRow r && grupos.Contains(r.Group);
        RowsView.Refresh();
        FiltroInfo = $"{grupos.Count} grupos";
    }

    /// <summary>
    /// Cabecera del desplegable de carpetas. Con muchas carpetas conviene poder ver de un vistazo
    /// qué hay configurado sin tener que abrirlo.
    /// </summary>
    public string ResumenCarpetas
    {
        get
        {
            var p = Folders.Count(f => f.Prioritaria);
            var x = Folders.Count(f => f.Excluida);
            if (p == 0 && x == 0) return $"Carpetas prioritarias y excluidas  ·  {Folders.Count} carpetas, ninguna configurada";
            var partes = new List<string>();
            if (p > 0) partes.Add($"{p} prioritaria{(p == 1 ? "" : "s")}");
            if (x > 0) partes.Add($"{x} excluida{(x == 1 ? "" : "s")}");
            return $"Carpetas prioritarias y excluidas  ·  {string.Join(" · ", partes)}";
        }
    }

    public DupModeOption[] ModeOptions { get; } =
    {
        new(DuplicateMode.ArtistTitle, "Artista + título"),
        new(DuplicateMode.TitleOnly, "Solo título (más agresivo)"),
        new(DuplicateMode.ArtistTitleDuration, "Artista + título + duración (estricto)"),
        new(DuplicateMode.Fingerprint, "Mismo audio (huella acústica)"),
    };

    public DupKeepOption[] KeepOptions { get; } =
    {
        new(DupKeep.PrioridadYCalidad, "Carpeta prioritaria, luego calidad"),
        new(DupKeep.Calidad, "Mejor calidad"),
        new(DupKeep.Duracion, "Mayor duración"),
    };

    private readonly AudioPreview _preview;
    private readonly AppEngine _engine;
    private bool _cargando;
    private bool _agrupando;

    [ObservableProperty] private DupRow? _selectedRow;
    [ObservableProperty] private DupModeOption _selectedMode;
    [ObservableProperty] private DupKeepOption _selectedKeep;

    /// <summary>Cuántas copias hay marcadas ahora mismo (para el botón de la papelera).</summary>
    [ObservableProperty] private int _marcadas;

    public DuplicatesViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        _preview = engine.Preview;
        _selectedMode = ModeOptions[0];
        _selectedKeep = KeepOptions.FirstOrDefault(k => k.Value.ToString() == engine.Config.DupKeepCriterion)
                        ?? KeepOptions[0];
        _excluirEdicionesDj = engine.Config.DupExcluirEdicionesDj;

        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(DupRow.Group)));

        CargarCarpetas();
        Store.Changed += CargarCarpetas;   // al reescanear pueden aparecer carpetas nuevas
        if (Store.IsScanned) Recompute();
    }

    /// <summary>Rehace la lista de carpetas a partir de la biblioteca, conservando lo ya marcado.</summary>
    private void CargarCarpetas()
    {
        _cargando = true;
        try
        {
            var cfg = _engine.Config;
            Folders.Clear();
            foreach (var f in Store.Folders.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                var op = new DupFolderOption
                {
                    Path = f,
                    Prioritaria = cfg.PriorityFolders.Contains(f, StringComparer.OrdinalIgnoreCase),
                    Excluida = cfg.ExcludedDupFolders.Contains(f, StringComparer.OrdinalIgnoreCase),
                };
                op.AlCambiar = GuardarCarpetasYRecalcular;
                Folders.Add(op);
            }
        }
        finally { _cargando = false; }
        OnPropertyChanged(nameof(ResumenCarpetas));
    }

    private void GuardarCarpetasYRecalcular()
    {
        if (_cargando) return;
        var cfg = _engine.Config;
        cfg.PriorityFolders = Folders.Where(f => f.Prioritaria).Select(f => f.Path).ToList();
        cfg.ExcludedDupFolders = Folders.Where(f => f.Excluida).Select(f => f.Path).ToList();
        _engine.SaveConfig();
        OnPropertyChanged(nameof(ResumenCarpetas));
        if (Store.IsScanned) Recompute();
    }

    /// <summary>El criterio elegido es el de audio: solo entonces hacen falta las huellas.</summary>
    public bool EsPorHuella => SelectedMode?.Value == DuplicateMode.Fingerprint;

    partial void OnSelectedModeChanged(DupModeOption value)
    {
        OnPropertyChanged(nameof(EsPorHuella));
        _ = ActualizarResumenHuellasAsync();
        if (Store.IsScanned) Recompute();
    }

    partial void OnSelectedKeepChanged(DupKeepOption value)
    {
        _engine.Config.DupKeepCriterion = value.Value.ToString();
        _engine.SaveConfig();
        if (Store.IsScanned) Recompute();
    }

    /// <summary>
    /// Dejar fuera del análisis las ediciones para pinchar (intros, open shows, acapellas, mashups,
    /// transiciones). No son copias sobrantes: son herramientas distintas, y al emparejarse una y
    /// otra vez con su original tapan los duplicados de verdad.
    /// </summary>
    [ObservableProperty] private bool _excluirEdicionesDj;

    partial void OnExcluirEdicionesDjChanged(bool value)
    {
        _engine.Config.DupExcluirEdicionesDj = value;
        _engine.SaveConfig();
        if (Store.IsScanned) Recompute();
    }

    private bool EsPrioritaria(string carpeta)
        => _engine.Config.PriorityFolders.Contains(carpeta, StringComparer.OrdinalIgnoreCase);

    /// <summary>Puntuación de una copia: la más alta del grupo es la que conviene conservar.</summary>
    private double Puntuar(Track t) => SelectedKeep.Value switch
    {
        // La carpeta prioritaria pesa más que cualquier diferencia técnica: es una decisión del
        // usuario sobre dónde vive su colección buena.
        DupKeep.PrioridadYCalidad => (EsPrioritaria(t.Folder) ? 1_000_000 : 0) + t.Bitrate * 1000.0 + t.DurationSeconds,
        DupKeep.Calidad => t.Bitrate * 1000.0 + t.DurationSeconds,
        DupKeep.Duracion => t.DurationSeconds * 1000.0 + t.Bitrate,
        _ => 0,
    };

    private string MotivoDe(Track t) => SelectedKeep.Value switch
    {
        DupKeep.PrioridadYCalidad => EsPrioritaria(t.Folder)
            ? "Carpeta prioritaria"
            : (t.Bitrate > 0 ? $"Mejor calidad ({t.Bitrate} kbps)" : "Mejor copia"),
        DupKeep.Calidad => t.Bitrate > 0 ? $"Mejor calidad ({t.Bitrate} kbps)" : "Mejor copia",
        DupKeep.Duracion => $"Mayor duración ({TimeSpan.FromSeconds(t.DurationSeconds):m\\:ss})",
        _ => "",
    };

    /// <summary>
    /// La biblioteca ha cambiado por una acción hecha en OTRA pestaña (aplicar, aceptar una
    /// sugerencia, descartar…). Agrupar por audio aquí sería doblemente malo:
    ///
    /// - Es carísimo: son millones de comparaciones de huellas, y se lanzaría por cada canción que
    ///   el usuario aplique en cualquier parte de la aplicación.
    /// - Marca esta pestaña como ocupada, y el bloqueo global entonces deshabilita las demás y
    ///   arrastra al usuario hasta aquí. Que aplicar una canción en "No encontradas" te plante en
    ///   Duplicados no hay forma de explicarlo.
    ///
    /// Así que en ese modo no se recalcula solo: se anota que la tabla se ha quedado vieja y se
    /// dice. El usuario vuelve cuando quiera y pulsa Analizar.
    ///
    /// Con los demás criterios sí se recalcula, pero CONSERVANDO las marcas de las copias que
    /// sigan en la tabla: el usuario puede haber revisado y marcado una a una decenas de copias, y
    /// aplicar algo en otra pestaña no es motivo para tirar ese trabajo.
    /// </summary>
    protected override void RecomputeReactive()
    {
        // El cambio lo ha hecho esta misma pestaña al enviar copias a la papelera: ya se encarga
        // QuitarDeLaTabla, sin recalcular y sin perder marcas.
        if (_quitandoBorradas) return;

        if (SelectedMode.Value == DuplicateMode.Fingerprint)
        {
            _ = ActualizarResumenHuellasAsync();
            if (Rows.Count == 0) return;   // no hay nada que se haya quedado viejo
            Desactualizada = true;
            Status = "La biblioteca ha cambiado: pulsa Analizar para volver a comparar el audio.";
            return;
        }
        var desmarcadas = Recalcular(conservarMarcas: true);
        Status += NotaDesmarcadas(desmarcadas);
    }

    /// <summary>Cuántas ediciones de DJ se dejaron fuera en el último recálculo (para el estado).</summary>
    private int _edicionesFuera;

    /// <summary>La tabla ya no refleja la biblioteca actual (se avisa en pantalla).</summary>
    [ObservableProperty] private bool _desactualizada;

    /// <summary>Los grupos que se ven ahora en la tabla, para poder quitar copias sin recalcular.</summary>
    private IReadOnlyList<DuplicateGroup> _grupos = Array.Empty<DuplicateGroup>();

    /// <summary>Esta pestaña está quitando de la biblioteca lo que acaba de enviar a la papelera.</summary>
    private bool _quitandoBorradas;

    /// <summary>
    /// Recalcular a petición del usuario (Analizar, cambiar el criterio, las carpetas…). Ahí sí se
    /// empieza sin marcas: marcar es una decisión sobre la lista que se ve, y la lista es otra.
    /// </summary>
    protected override void Recompute() => Recalcular(conservarMarcas: false);

    /// <returns>Cuántas copias se han desmarcado para no dejar un grupo sin ninguna (ver <see cref="ConstruirFilas"/>).</returns>
    private int Recalcular(bool conservarMarcas)
    {
        Desactualizada = false;
        var excluidas = _engine.Config.ExcludedDupFolders;
        var fuente = excluidas.Count == 0
            ? Store.Tracks.AsEnumerable()
            : Store.Tracks.Where(t => !excluidas.Contains(t.Folder, StringComparer.OrdinalIgnoreCase));

        var lista = fuente.ToList();
        if (ExcluirEdicionesDj)
        {
            var antes = lista.Count;
            lista = lista.Where(t => !EdicionDj.Es(t.FilePath)).ToList();
            _edicionesFuera = antes - lista.Count;
        }
        else _edicionesFuera = 0;

        // Agrupar por audio es caro y NO puede hacerse aquí: Recompute corre en el hilo de la
        // interfaz y la dejaría congelada. Se desvía a un método propio en segundo plano.
        if (SelectedMode.Value == DuplicateMode.Fingerprint) { _ = RecomputePorHuellaAsync(lista); return 0; }

        return Pintar(DuplicateFinder.Find(lista, SelectedMode.Value), conservarMarcas ? MarcasActuales() : null);
    }

    /// <summary>Las marcas de la tabla antes de repintarla.</summary>
    /// <param name="Marcadas">Rutas de las copias marcadas.</param>
    /// <param name="Recomendadas">
    /// Las marcadas que además eran la copia recomendada de su grupo: esas se marcaron a sabiendas.
    /// </param>
    private sealed record Marcas(HashSet<string> Marcadas, HashSet<string> Recomendadas);

    private Marcas MarcasActuales()
        => new(Rows.Where(r => r.Marcada).Select(r => r.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase),
               Rows.Where(r => r.Marcada && r.EsMejor).Select(r => r.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Vuelca los grupos en la tabla. Compartido por los dos caminos de agrupación.
    /// </summary>
    /// <param name="conservar">
    /// Marcas que tienen que seguir. Null al recalcular a petición del usuario, que empieza sin
    /// marcas.
    /// </param>
    /// <returns>Cuántas copias se han desmarcado para no dejar un grupo sin ninguna.</returns>
    private int Pintar(IReadOnlyList<DuplicateGroup> groups, Marcas? conservar = null)
    {
        _grupos = groups;
        var excluidas = _engine.Config.ExcludedDupFolders;
        var porAudio = SelectedMode.Value == DuplicateMode.Fingerprint;
        var (filas, conPrioridad) = ConstruirFilas(groups, porAudio, Puntuar, MotivoDe,
                                                   t => EsPrioritaria(t.Folder), conservar?.Marcadas,
                                                   conservar?.Recomendadas);
        Rows.Clear();
        foreach (var f in filas) Rows.Add(f);
        var copies = filas.Count;

        RowsView.Refresh();
        RecontarMarcadas();
        // El filtro de búsqueda guarda los grupos que casaban, y la etiqueta de un grupo cambia con
        // su número de copias: se rehace para no esconder grupos que siguen coincidiendo.
        if (Busqueda.Trim().Length > 0) OnBusquedaChanged(Busqueda);

        var excl = excluidas.Count > 0 ? $" · {excluidas.Count} carpeta(s) excluida(s)" : "";
        var edic = _edicionesFuera > 0 ? $" · {_edicionesFuera} edición(es) de DJ fuera" : "";
        var prio = conPrioridad > 0 ? $" · {conPrioridad} resueltos por carpeta prioritaria" : "";
        var nota = porAudio ? " · cada versión conserva un ejemplar" : "";
        Status = $"{groups.Count} grupo(s) de duplicados · {copies} archivos implicados{excl}{edic}{prio}{nota}.";

        // Las desmarcadas por ConstruirFilas: las marcadas que siguen en la tabla y ya no lo están.
        return conservar == null ? 0 : filas.Count(f => !f.Marcada && conservar.Marcadas.Contains(f.FilePath));
    }

    /// <summary>
    /// Las filas de la tabla para unos grupos: la mejor copia de cada uno (o de cada versión, al
    /// agrupar por audio) y las marcas que haya que conservar. Estático y sin interfaz, para poder
    /// probarlo sin montar la aplicación.
    ///
    /// Al conservar marcas, ningún grupo (ni versión, al agrupar por audio) se queda con TODAS sus
    /// copias marcadas por culpa del repintado. Visto en revisión: «Marcar sobrantes» en un grupo de
    /// 3 y enviar después a la papelera, con el clic derecho, la copia recomendada. Las otras dos
    /// seguían marcadas, una pasaba a ser la recomendada y «Enviar marcadas» se llevaba la canción
    /// entera. Ahora esa nueva recomendada se desmarca. No se toca una recomendada que el usuario ya
    /// tenía marcada (<paramref name="recomendadasMarcadas"/>): esa la marcó sabiendo lo que era, y
    /// el diálogo de la papelera lo avisa (<see cref="CuerpoConfirmarPapelera"/>).
    /// </summary>
    /// <param name="marcadas">Copias que tienen que seguir marcadas, o null para empezar sin marcas.</param>
    /// <param name="recomendadasMarcadas">De ellas, las que ya eran la copia recomendada.</param>
    /// <returns>Las filas y cuántos grupos se resolvieron por carpeta prioritaria.</returns>
    internal static (List<DupRow> Filas, int ConPrioridad) ConstruirFilas(
        IReadOnlyList<DuplicateGroup> groups, bool porAudio,
        Func<Track, double> puntuar, Func<Track, string> motivoDe, Func<Track, bool> esPrioritaria,
        IReadOnlySet<string>? marcadas = null, IReadOnlySet<string>? recomendadasMarcadas = null)
    {
        var filas = new List<DupRow>();
        var conPrioridad = 0;
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var label = $"{(string.IsNullOrWhiteSpace(g.Artist) ? "¿?" : g.Artist)} - {g.Title}   ({g.Tracks.Count} copias)";

            // Al agrupar por AUDIO, un grupo reúne el original con sus ediciones de DJ: comparten la
            // grabación pero no son copias sobrantes, son herramientas distintas para pinchar. Por
            // eso cada versión conserva SU ejemplar: se elige la mejor dentro de cada descriptor,
            // no una sola para todo el grupo. Con los demás criterios no aplica: ahí un grupo son
            // copias del mismo archivo y solo debe quedar una.
            var porVersion = g.Tracks
                .GroupBy(t => porAudio ? VersionDe(t) : "", StringComparer.OrdinalIgnoreCase);

            foreach (var v in porVersion)
            {
                Track? mejor = null;
                double mejorPunt = double.NegativeInfinity;
                foreach (var t in v)
                {
                    var p = puntuar(t);
                    if (p > mejorPunt) { mejorPunt = p; mejor = t; }
                }
                if (mejor != null && esPrioritaria(mejor)) conPrioridad++;

                // La recomendada pierde su marca solo si con ella se irían todas y no estaba ya
                // marcada como recomendada antes de repintar.
                var desmarcarMejor = mejor != null && marcadas != null
                                     && v.All(t => marcadas.Contains(t.FilePath))
                                     && recomendadasMarcadas?.Contains(mejor.FilePath) != true;

                foreach (var t in v)
                {
                    var esMejor = ReferenceEquals(t, mejor);
                    var version = porAudio ? VersionDe(t) : "";
                    filas.Add(new DupRow
                    {
                        Group = label,
                        GrupoId = gi,
                        FileName = t.FileName,
                        Quality = t.Quality,
                        Duration = t.Duration,
                        Folder = t.Folder,
                        FilePath = t.FilePath,
                        Version = version,
                        EsMejor = esMejor,
                        Motivo = esMejor
                            ? (version.Length > 0 ? $"Se conserva ({version})" : motivoDe(t))
                            : "",
                        Marcada = marcadas?.Contains(t.FilePath) == true && !(esMejor && desmarcarMejor),
                    });
                }
            }
        }
        return (filas, conPrioridad);
    }

    /// <summary>Lo que se añade al estado cuando el repintado ha desmarcado copias. Puro.</summary>
    internal static string NotaDesmarcadas(int desmarcadas) => desmarcadas switch
    {
        <= 0 => "",
        1 => " Se ha desmarcado 1 copia: ahora es la que se conserva de su grupo y, enviándola, no quedaría ninguna.",
        _ => $" Se han desmarcado {desmarcadas} copias: ahora son las que se conservan de su grupo y, enviándolas, no quedaría ninguna.",
    };

    /// <summary>
    /// Cuántas canciones se quedarían sin ninguna copia si se envían las marcadas: grupos (o, al
    /// agrupar por audio, versiones de un grupo) con TODAS sus copias marcadas.
    /// </summary>
    internal static int SinNingunaCopia(IEnumerable<DupRow> filas)
        => filas.GroupBy(f => (f.GrupoId, f.Version.ToUpperInvariant()))
                .Count(g => g.All(f => f.Marcada));

    /// <summary>
    /// El texto del diálogo antes de enviar las marcadas a la papelera. Puro, para probarlo.
    ///
    /// Avisa por separado de las dos cosas que conviene no pasar por alto: que se va alguna copia
    /// recomendada (puede ser deliberado) y, sobre todo, que alguna canción se quedaría sin ninguna
    /// copia en la biblioteca.
    /// </summary>
    internal static string CuerpoConfirmarPapelera(IReadOnlyList<DupRow> filas)
    {
        var marcadas = filas.Where(r => r.Marcada).ToList();
        var grupos = marcadas.Select(r => r.GrupoId).Distinct().Count();
        var texto = $"Afecta a {grupos} grupo(s) de duplicados.";

        var mejores = marcadas.Count(r => r.EsMejor);
        if (mejores > 0)
            texto += mejores == 1
                ? " Atención: 1 de ellas es la copia recomendada de su grupo."
                : $" Atención: {mejores} de ellas son la copia recomendada de su grupo.";

        var sinCopia = SinNingunaCopia(filas);
        if (sinCopia > 0)
            texto += sinCopia == 1
                ? " Hay 1 canción con TODAS sus copias marcadas: desaparecería de la biblioteca."
                : $" Hay {sinCopia} canciones con TODAS sus copias marcadas: desaparecerían de la biblioteca.";
        return texto;
    }

    /// <summary>
    /// Los mismos grupos sin las copias que ya no están (enviadas a la papelera). Un grupo que se
    /// queda con una sola copia deja de ser un duplicado y desaparece.
    ///
    /// Es lo que permite quitar lo borrado SIN volver a agrupar: al agrupar por audio, recalcular
    /// son millones de comparaciones, y antes ni se hacía: las copias ya en la papelera seguían en
    /// la tabla, marcadas, y repetir el envío daba «0 enviadas, 120 con problemas».
    /// </summary>
    internal static IReadOnlyList<DuplicateGroup> SinBorradas(IReadOnlyList<DuplicateGroup> grupos,
                                                              IEnumerable<string> borradas)
    {
        var fuera = new HashSet<string>(borradas, StringComparer.OrdinalIgnoreCase);
        if (fuera.Count == 0) return grupos;
        var quedan = new List<DuplicateGroup>(grupos.Count);
        foreach (var g in grupos)
        {
            if (!g.Tracks.Any(t => fuera.Contains(t.FilePath))) { quedan.Add(g); continue; }
            var resto = g.Tracks.Where(t => !fuera.Contains(t.FilePath)).ToList();
            if (resto.Count >= 2) quedan.Add(g with { Tracks = resto });
        }
        return quedan;
    }

    /// <summary>
    /// Saca de la tabla y de la biblioteca las copias que acaban de ir a la papelera, sin recalcular
    /// y sin tocar las marcas del resto.
    ///
    /// Visto en uso real: agrupando por artista y título, marcar a mano 30 copias y enviar otra a la
    /// papelera desde el menú contextual rehacía la tabla entera y las 30 marcas desaparecían.
    /// </summary>
    /// <returns>Cuántas copias se han desmarcado para no dejar un grupo sin ninguna.</returns>
    private int QuitarDeLaTabla(IReadOnlyCollection<string> borradas)
    {
        if (borradas.Count == 0) return 0;
        var marcas = MarcasActuales();

        // La biblioteca avisa a TODAS las pestañas; esta se salta su recálculo (RecomputeReactive)
        // porque sabe exactamente qué ha cambiado.
        _quitandoBorradas = true;
        try { Store.RemoveTracks(borradas); }
        finally { _quitandoBorradas = false; }

        var desmarcadas = Pintar(SinBorradas(_grupos, borradas), marcas);

        // Saltarse RecomputeReactive también se saltaba el recuento de huellas: «N de M analizadas»
        // seguía contando las canciones recién enviadas a la papelera. Solo hace algo por audio.
        _ = ActualizarResumenHuellasAsync();
        return desmarcadas;
    }

    /// <summary>
    /// El estado tras enviar a la papelera. Cuando todas fallan por el MISMO motivo (por ejemplo, una
    /// unidad sin papelera, donde enviar sería borrar para siempre), se dice ese motivo una vez en
    /// lugar de «0 enviadas, 120 con problemas» y el primero de la lista.
    /// </summary>
    internal static string ResumenPapelera(int enviadas, IReadOnlyList<(string Archivo, string Motivo)> fallos)
    {
        if (fallos.Count == 0) return $"{enviadas} copia(s) enviadas a la papelera.";

        var motivos = fallos.Select(f => f.Motivo).Distinct(StringComparer.Ordinal).ToList();
        if (motivos.Count == 1)
            return enviadas == 0
                ? $"No se ha enviado ninguna copia a la papelera: {Motivo(motivos[0])}"
                : $"{enviadas} copia(s) enviadas a la papelera · {fallos.Count} no se han enviado: {Motivo(motivos[0])}";

        return $"{enviadas} enviadas, {fallos.Count} con problemas. La primera: {fallos[0].Archivo}: {Motivo(fallos[0].Motivo)}";

        static string Motivo(string m) => string.IsNullOrWhiteSpace(m) ? "motivo desconocido." : m;
    }

    /// <summary>
    /// Descriptor de edición sacado del nombre del archivo ("Aca Out", "Melodic Intro"…). Vacío si
    /// es el original. Es lo que separa una copia sobrante de una edición que el usuario quiere.
    /// </summary>
    private static string VersionDe(Track t)
    {
        var info = RemixParser.Parse(System.IO.Path.GetFileNameWithoutExtension(t.FilePath));
        return info.IsVersion ? info.Label.Trim() : "";
    }


    // ---- Huellas acústicas ----

    /// <summary>Avance del cálculo de huellas (0-100) y si conviene enseñar la barra.</summary>
    [ObservableProperty] private double _fpProgress;
    [ObservableProperty] private bool _fpBusy;

    /// <summary>
    /// Estado de las huellas, para saber si hace falta calcularlas antes de agrupar. Es lo último
    /// calculado: leerlo no toca el disco.
    ///
    /// Antes era una propiedad calculada al vuelo, y su enlace se evalúa al crear la vista aunque el
    /// recuadro esté oculto. Así, abrir Duplicados -con cualquier criterio- leía en el hilo de la
    /// interfaz los ~160 MB de huellas y la fecha de 15.000 archivos: la ventana se quedaba
    /// congelada entre 1,3 y 3,5 s (medido en los registros reales) sin que nadie usara las huellas.
    /// </summary>
    [ObservableProperty] private string _resumenHuellas = "";

    /// <summary>Para que un recuento viejo que termine tarde no pise a uno más reciente.</summary>
    private int _resumenVersion;

    /// <summary>
    /// Texto del estado de las huellas. Puro, para probarlo.
    /// </summary>
    internal static string TextoResumenHuellas(bool fpcalcDisponible, int total, int hechas)
    {
        if (!fpcalcDisponible) return "No está disponible el analizador de audio (fpcalc).";
        if (total == 0) return "";
        if (hechas == 0) return $"Sin calcular. Hay que analizar el audio de las {total} canciones (la primera vez tarda bastante).";
        if (hechas < total) return $"{hechas} de {total} analizadas. Faltan {total - hechas}.";
        return $"Las {total} analizadas.";
    }

    /// <summary>
    /// Cuenta en segundo plano cuántas canciones tienen ya su huella. La primera vez incluye leer la
    /// caché del disco, que también va fuera del hilo de la interfaz.
    ///
    /// Si las huellas guardadas no se han podido leer, se dice eso. Contar sobre una caché sin leer
    /// daba «Sin calcular. Hay que analizar el audio de las 15000 canciones», que invitaba a pasar
    /// horas recalculando lo que ya estaba en el disco.
    /// </summary>
    internal static Task<string> CalcularResumenHuellasAsync(FingerprintScanner huellas, IReadOnlyList<string> rutas)
        => Task.Run(async () =>
        {
            if (!huellas.FpcalcDisponible) return TextoResumenHuellas(false, rutas.Count, 0);
            if (rutas.Count == 0) return TextoResumenHuellas(true, 0, 0);
            await huellas.CargarAsync().ConfigureAwait(false);
            if (huellas.AvisoCarga.Length > 0) return huellas.AvisoCarga;
            return TextoResumenHuellas(true, rutas.Count, huellas.Contar(rutas));
        });

    /// <summary>
    /// Rehace <see cref="ResumenHuellas"/>, solo si el criterio es el de audio: con los demás no se
    /// enseña y no hay por qué pagar la lectura.
    /// </summary>
    private async Task ActualizarResumenHuellasAsync()
    {
        if (!EsPorHuella) return;
        var version = ++_resumenVersion;
        var rutas = Store.Tracks.Select(t => t.FilePath).ToList();
        if (rutas.Count > 0 && _engine.Fingerprints.FpcalcDisponible)
            ResumenHuellas = _engine.Fingerprints.EstaCargada
                ? "Comprobando qué canciones tienen ya su huella…"
                : "Leyendo las huellas guardadas…";
        try
        {
            var texto = await CalcularResumenHuellasAsync(_engine.Fingerprints, rutas);
            if (version == _resumenVersion) ResumenHuellas = texto;
        }
        catch (Exception e)
        {
            if (version == _resumenVersion) ResumenHuellas = "No se pudo comprobar el estado de las huellas: " + e.Message;
        }
    }

    private CancellationTokenSource? _fpCts;

    /// <summary>
    /// Calcula las huellas que falten. Es lo único de la pestaña que tarda: se hace bajo petición
    /// expresa y no al abrir, porque la primera vez son horas.
    /// </summary>
    [RelayCommand]
    private async Task ScanFingerprintsAsync()
    {
        if (FpBusy) return;
        if (!_engine.Fingerprints.FpcalcDisponible)
        {
            Status = "No está disponible el analizador de audio (fpcalc). Se descarga solo al usar AcoustID en Enriquecer.";
            return;
        }
        if (!Store.IsScanned || Store.Tracks.Count == 0) { Status = "Escanea antes la biblioteca."; return; }

        _fpCts = new CancellationTokenSource();
        FpBusy = true; IsBusy = true; FpProgress = 0;
        try
        {
            var rutas = Store.Tracks.Select(t => t.FilePath).ToList();
            var avance = new Progress<(int Hechas, int Total, string Archivo)>(p =>
            {
                FpProgress = p.Total > 0 ? p.Hechas * 100.0 / p.Total : 0;
                Status = $"Analizando el audio: {p.Hechas} de {p.Total}… {p.Archivo}";
            });
            var motivo = await _engine.Fingerprints.ScanAsync(rutas, force: false, avance, _fpCts.Token);
            _ = ActualizarResumenHuellasAsync();
            if (motivo.Length > 0) { Status = "No se ha analizado nada. " + motivo; return; }
            Status = _fpCts.IsCancellationRequested
                ? "Análisis interrumpido. Lo calculado queda guardado; puedes retomarlo cuando quieras."
                : "Audio analizado.";
            if (SelectedMode.Value == DuplicateMode.Fingerprint) Recompute();
        }
        catch (Exception e) { Status = "No se pudo analizar el audio: " + e.Message; }
        finally { FpBusy = false; IsBusy = false; FpProgress = 0; _fpCts?.Dispose(); _fpCts = null; }
    }

    [RelayCommand]
    private void CancelFingerprints() => _fpCts?.Cancel();


    /// <summary>
    /// Agrupa por audio FUERA del hilo de la interfaz. Con 12.927 huellas la comparación son
    /// millones de parejas: hacerlo en Recompute dejaba la aplicación congelada sin previo aviso.
    /// </summary>
    private async Task RecomputePorHuellaAsync(List<Track> lista)
    {
        if (_agrupando) return;
        _agrupando = true;
        IsBusy = true;
        Status = "Comparando el audio…";
        _fpCts ??= new CancellationTokenSource();
        var ct = _fpCts.Token;

        try
        {
            var avance = new Progress<(int Hechas, int Total)>(p =>
                Status = $"Comparando el audio: {p.Hechas} de {p.Total} parejas…");

            // Sin las huellas guardadas, comparar daría «0 grupos con el mismo audio», que es falso:
            // se dice por qué no se puede comparar y la tabla se queda como estaba.
            await _engine.Fingerprints.CargarAsync();
            if (_engine.Fingerprints.AvisoCarga is { Length: > 0 } aviso) { Status = aviso; return; }

            var groups = await Task.Run(
                () => FingerprintDuplicates.Find(lista, t => _engine.Fingerprints.Get(t.FilePath),
                                                 AudioFingerprint.UmbralIgual, avance, ct), ct);

            // El usuario puede haber cambiado de criterio mientras esto corría: no pisar la tabla.
            if (SelectedMode.Value != DuplicateMode.Fingerprint) return;

            Pintar(groups);
            var copias = groups.Sum(g => g.Tracks.Count);
            Status = $"{groups.Count} grupo(s) con el mismo audio · {copias} archivos implicados.";
        }
        catch (OperationCanceledException) { Status = "Comparación cancelada."; }
        catch (Exception e) { Status = "No se pudo comparar el audio: " + e.Message; }
        finally { _agrupando = false; IsBusy = false; }
    }

    private void RecontarMarcadas() => Marcadas = Rows.Count(r => r.Marcada);

    /// <summary>
    /// Marca todas las copias MENOS la mejor de cada grupo: deja la biblioteca a un clic de quedar
    /// limpia, conservando siempre un ejemplar de cada canción.
    /// </summary>
    [RelayCommand]
    private void MarkAllButBest()
    {
        foreach (var r in Rows) r.Marcada = !r.EsMejor;
        RecontarMarcadas();
        Status = $"{Marcadas} copia(s) marcadas. Se conserva una de cada canción.";
    }

    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var r in Rows) r.Marcada = false;
        RecontarMarcadas();
        Status = "Marcas quitadas.";
    }

    /// <summary>Recuenta tras marcar o desmarcar a mano desde la tabla.</summary>
    public void MarcaCambiada() => RecontarMarcadas();

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private void StopPreview() => _preview.Stop();

    /// <summary>Las copias marcadas, para que la vista pueda confirmar antes de borrar.</summary>
    public List<DupRow> ParaPapelera() => Rows.Where(r => r.Marcada).ToList();

    /// <summary>
    /// Envía el archivo seleccionado a la papelera del sistema (recuperable), no lo borra del todo.
    /// Si la papelera se niega (por ejemplo, en una unidad que no tiene), se dice el motivo y la
    /// copia se queda en la tabla con su marca.
    /// </summary>
    [RelayCommand]
    private void SendToRecycleBin()
    {
        var row = SelectedRow;
        if (row == null || string.IsNullOrEmpty(row.FilePath)) return;
        _preview.Stop();   // si suena, el archivo está abierto y no se puede mover a la papelera
        var (ok, err) = Services.Papelera.Enviar(row.FilePath);
        var desmarcadas = ok ? QuitarDeLaTabla(new[] { row.FilePath }) : 0;
        // El estado va después: QuitarDeLaTabla repinta y pone el resumen de grupos.
        Status = ok
            ? $"Enviado a la papelera: {row.FileName}." + NotaDesmarcadas(desmarcadas)
            : "No se pudo enviar a la papelera: " + (string.IsNullOrWhiteSpace(err) ? "motivo desconocido." : err);
    }

    /// <summary>Envía a la papelera TODAS las copias marcadas. La vista confirma antes de llamar.</summary>
    public void SendMarkedToRecycleBin()
    {
        var objetivo = ParaPapelera();
        if (objetivo.Count == 0) { Status = "No hay ninguna copia marcada."; return; }

        _preview.Stop();
        var borrados = new List<string>();
        var fallos = new List<(string Archivo, string Motivo)>();
        foreach (var r in objetivo)
        {
            var (ok, err) = Services.Papelera.Enviar(r.FilePath);
            if (ok) borrados.Add(r.FilePath);
            else fallos.Add((r.FileName, err));
        }

        // De una sola vez: quitarlos uno a uno recalcularía la biblioteca entera por cada archivo.
        // Las que no se han podido enviar siguen en la tabla y marcadas, para reintentarlo o decidir.
        var desmarcadas = QuitarDeLaTabla(borrados);

        Status = ResumenPapelera(borrados.Count, fallos) + NotaDesmarcadas(desmarcadas);
    }
}
