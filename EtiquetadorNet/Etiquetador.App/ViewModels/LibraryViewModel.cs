using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

/// <summary>Pestaña Biblioteca: gestiona las carpetas, escanea la biblioteca compartida e importa rekordbox.</summary>
public partial class LibraryViewModel : ViewModelBase, IEstadoPagina
{
    /// <summary>El motor. Solo falta en las pruebas, que montan la página sobre una biblioteca suelta.</summary>
    private readonly AppEngine? _engine;
    private readonly LibraryStore _store;

    private AppEngine Engine => _engine ?? throw new InvalidOperationException("La página Biblioteca se ha montado sin el motor (solo en las pruebas).");

    public ObservableCollection<FolderItem> Folders => _store.Folders;
    public DataGridCollectionView TracksView { get; }

    [ObservableProperty] private string _status = "Añade una o varias carpetas y pulsa Escanear.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _selectedFolder;

    public LibraryViewModel(AppEngine engine) : this(engine.Library, engine) { }

    /// <summary>
    /// La página sobre una biblioteca suelta, sin el motor: así las pruebas recorren el envío a la
    /// papelera de verdad (con <see cref="EnviarUno"/> de mentira), su cancelación con
    /// <see cref="CancelarPapelera"/> y la ocupación de la página mientras dura.
    /// </summary>
    internal LibraryViewModel(LibraryStore store, AppEngine? engine = null)
    {
        _engine = engine;
        _store = store;
        TracksView = new DataGridCollectionView(_store.Tracks);
        TracksView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(Track.Folder)));
        _store.Changed += OnLibraryChanged;
        // Añadir una carpeta no siempre cambia las canciones, pero sí lo que dice el estado.
        _store.Folders.CollectionChanged += (_, _) => UpdateStatus();
        UpdateStatus();
    }

    private void OnLibraryChanged()
    {
        TracksView.Refresh();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        Status = TextoEstado(_store.Folders.Count, _store.Tracks.Count, _store.Tracks.Count(t => t.IsIncomplete), _store.IsScanned);
    }

    /// <summary>
    /// El estado de la página con las carpetas y canciones que hay, concordando en número («1 carpeta
    /// sin escanear», no «1 carpeta(s)»). Puro, para probarlo.
    /// </summary>
    internal static string TextoEstado(int carpetas, int canciones, int incompletas, bool escaneada)
    {
        if (carpetas == 0) return "Añade una o varias carpetas y pulsa Escanear.";

        // Que la lista esté vacía teniendo carpetas no es lo mismo que no tener ninguna, y hasta
        // ahora se decía igual: quien acababa de añadir una carpeta leía «añade una carpeta».
        if (canciones == 0)
            return (carpetas == 1 ? "1 carpeta sin escanear" : $"{carpetas:N0} carpetas sin escanear") + ". Pulsa Escanear.";

        return $"{Cuenta(canciones, "canción", "canciones")} · {Cuenta(incompletas, "incompleta", "incompletas")} · {Cuenta(carpetas, "carpeta", "carpetas")}"
               + (escaneada ? "" : " · hay carpetas por escanear, pulsa Escanear");

        static string Cuenta(int n, string singular, string plural) => n == 1 ? "1 " + singular : $"{n:N0} {plural}";
    }

    /// <summary>
    /// Añade una carpeta y devuelve si de verdad ha entrado. Puede rechazarse por solaparse con
    /// otra que ya estaba; cuando eso pasa hay que decirlo, porque si no el usuario elige una
    /// carpeta, no ve ningún cambio y no tiene forma de saber por qué.
    /// </summary>
    public bool AddFolder(string folder)
    {
        var alta = _store.AddFolder(folder);
        if (alta.Aviso.Length > 0) Status = alta.Aviso;
        return alta.Anadida;
    }

    [RelayCommand]
    private void RemoveFolderPath(string? folder) { if (folder != null) _store.RemoveFolder(folder); }

    [RelayCommand]
    private void ClearFolders() => _store.ClearFolders();

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (_store.Folders.Count == 0) { Status = "Añade al menos una carpeta."; return; }
        IsBusy = true;
        Status = "Escaneando…";
        try { await _store.ScanAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Importa BPM y clave musical desde un XML de rekordbox, casando por ruta de archivo.
    /// Anota un manifiesto reversible (se puede Deshacer desde Enriquecer) y registra los fallos.
    /// </summary>
    public async Task ImportRekordboxAsync(string xmlPath)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Importando de rekordbox…";
        try
        {
            var overwrite = Engine.Config.Overwrite;
            var undoFile = System.IO.Path.Combine(Engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var (entradas, matched, updated, failures) = await Task.Run(() =>
            {
                // Las rutas de un Mac pueden llegar con los acentos en otra forma Unicode (ComparadorRutas).
                var byPath = new Dictionary<string, RekordboxEntry>(ComparadorRutas.Instancia);
                foreach (var e in RekordboxImporter.Parse(xmlPath)) byPath[e.FilePath] = e;
                int m = 0, u = 0;
                var lines = new List<string>();
                var fails = new List<string>();
                foreach (var t in _store.Tracks.ToList())
                    if (byPath.TryGetValue(t.FilePath, out var e))
                    {
                        m++;
                        try
                        {
                            var log = TagEditor.ImportBpmKey(t.FilePath, RekordboxImporter.ParseBpm(e.Bpm), e.Key, overwrite);
                            if (log.Count > 0)
                            {
                                u++;
                                var rec = new UndoRecord { OrigPath = t.FilePath, FinalPath = t.FilePath, Renamed = false, Fields = log, Origen = nameof(TipoEjecucion.Rekordbox) };
                                lines.Add(System.Text.Json.JsonSerializer.Serialize(rec));
                            }
                        }
                        catch (Exception ex) { fails.Add(t.FileName + ": " + ex.Message); }
                    }
                if (lines.Count > 0) System.IO.File.WriteAllLines(undoFile, lines);
                return (byPath.Count, m, u, fails);
            });

            foreach (var f in failures) Engine.Logger.Log("rekordbox: " + f, LogKind.Err);
            await _store.ScanAsync();
            Status = TextoImportacionRekordbox(entradas, matched, updated, failures.Count);
        }
        // Un XML cortado o que no es la colección se dice tal cual (antes salía «0 coincidencias»).
        catch (RekordboxXmlException e) { Status = "No se ha importado nada de rekordbox: " + e.Message; }
        catch (Exception e) { Status = "Error importando rekordbox: " + e.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Lo que se dice tras importar BPM y tonalidad de rekordbox (equipo «rekordbox», tanda 4). Con 0
    /// coincidencias se distingue una colección sin pistas de una cuyas rutas no son las de estas
    /// carpetas, y se remite a la página rekordbox, que enseña cuáles faltan y cuáles están rotas.
    /// </summary>
    internal static string TextoImportacionRekordbox(int entradas, int coincidencias, int actualizadas, int fallos)
    {
        if (entradas == 0) return "La colección de rekordbox no trae ninguna pista con archivo en el disco: no hay nada que importar.";
        if (coincidencias == 0)
            return $"Ninguna de las {entradas:N0} pistas de la colección de rekordbox está en las carpetas de la biblioteca. "
                 + "En la página rekordbox se ve qué falta en cada lado y qué rutas están rotas.";
        var texto = $"rekordbox: {coincidencias:N0} " + (coincidencias == 1 ? "canción encontrada" : "canciones encontradas")
                  + $" · {actualizadas:N0} " + (actualizadas == 1 ? "actualizada" : "actualizadas");
        if (fallos > 0) texto += $" · {fallos:N0} " + (fallos == 1 ? "fallo" : "fallos") + " (el detalle está en el registro)";
        return texto + (actualizadas > 0 ? ". Se puede deshacer desde Enriquecer." : ".");
    }

    // =================================================================================================
    // Menú contextual de la tabla: escuchar, editar, buscar compatibles, ver versiones, ordenar la
    // selección para mezclar, abrir la carpeta y enviar a la papelera. Antes la Biblioteca, que es la
    // tabla con TODAS las canciones, era la única sin menú.
    // =================================================================================================

    /// <summary>
    /// La biblioteca está trabajando: escaneando (desde aquí o desde otra pestaña que la necesitó) o
    /// importando de rekordbox, que escribe en las etiquetas.
    /// </summary>
    public bool Ocupada => IsBusy || _store.IsScanning;

    /// <summary>Qué opciones del menú se pueden usar y cómo se rotulan.</summary>
    internal readonly record struct EstadoMenu(bool Escuchar, bool Editar, bool Buscar, bool Ordenar, bool Carpeta, bool Papelera,
                                               string TextoOrdenar, string TextoPapelera, string Espera);

    /// <summary>
    /// Mientras la biblioteca trabaja no se escucha, ni se edita, ni se manda nada a la papelera: la
    /// importación de rekordbox escribe en las etiquetas y el reproductor dejaría el archivo abierto, y
    /// un escaneo a medias aún no tiene la lista completa. Abrir la carpeta no toca nada y sigue.
    /// </summary>
    internal static EstadoMenu CalcularMenu(int seleccionadas, bool ocupada)
    {
        var hay = seleccionadas > 0 && !ocupada;
        var ordenable = MezclaSeleccionViewModel.MotivoParaNoOrdenar(seleccionadas).Length == 0;
        return new EstadoMenu(
            Escuchar: hay,
            Editar: hay,
            Buscar: hay,
            Ordenar: ordenable && !ocupada,
            Carpeta: seleccionadas > 0,
            Papelera: hay,
            // Pasado el máximo la opción sale desactivada, y el motivo solo llegaba a la barra de estado
            // al pulsarla, cosa que una opción desactivada no deja hacer: el rótulo lo dice.
            TextoOrdenar: seleccionadas > MezclaViewModel.Maximo
                ? $"Ordenar para mezclar la selección ({seleccionadas:N0}; máximo {MezclaViewModel.Maximo:N0})…"
                : seleccionadas >= 2
                ? $"Ordenar para mezclar la selección ({seleccionadas})…"
                : "Ordenar para mezclar la selección (selecciona varias)…",
            TextoPapelera: seleccionadas > 1 ? $"Enviar a la papelera ({seleccionadas})" : "Enviar a la papelera",
            Espera: ocupada ? " (espera a que termine el proceso)" : "");
    }

    public void Escuchar(string ruta)
    {
        if (Ocupada) { Status = "Espera a que termine el proceso en curso."; return; }
        try { Engine.Preview.Toggle(ruta); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    public void Editar(string ruta)
    {
        if (Ocupada) { Status = "Espera a que termine el proceso en curso."; return; }
        Engine.RequestEdit(ruta);
    }

    /// <summary>Si se puede ordenar la selección; si no, dice por qué en la barra de estado.</summary>
    public bool PuedeOrdenar(int seleccionadas)
    {
        var motivo = Ocupada ? "Espera a que termine el proceso en curso." : MezclaSeleccionViewModel.MotivoParaNoOrdenar(seleccionadas);
        if (motivo.Length > 0) Status = motivo;
        return motivo.Length == 0;
    }

    // La papelera, fuera del hilo de la interfaz. Esta tabla admite selección múltiple y tiene TODAS las
    // canciones: con una carpeta entera o Ctrl+A son cientos o miles de archivos, y tanto la nota del
    // diálogo (mira la unidad y la papelera de cada uno) como el envío (una operación del sistema por
    // archivo) dejaban la ventana sin responder durante segundos o minutos.
    //
    // Mientras dura, la página está ocupada (IsBusy) y el resto de la aplicación queda bloqueado, como
    // en cualquier proceso que toca archivos: un escaneo lanzado a mitad del envío devolvería a la
    // biblioteca lo ya enviado. Por eso se puede CANCELAR (revisión de la tanda 3): con miles de
    // archivos, la única salida era esperar o cerrar la aplicación.

    private CancellationTokenSource? _ctsPapelera;

    /// <summary>
    /// Cómo se envía cada archivo: la papelera del sistema. Las pruebas ponen uno de mentira para
    /// recorrer el envío entero, con su ocupación y su cancelación, sin tocar el disco.
    /// </summary>
    internal Func<string, (bool Ok, string Error)> EnviarUno { get; init; } = Papelera.Enviar;

    /// <summary>Se están enviando canciones a la papelera: se enseña «Cancelar el envío».</summary>
    [ObservableProperty] private bool _enviandoPapelera;

    /// <summary>Para el envío a la papelera entre un archivo y el siguiente. Lo ya enviado se queda en la papelera.</summary>
    [RelayCommand]
    private void Cancelar() => CancelarPapelera();

    /// <summary>La nota del diálogo de confirmación (<see cref="Papelera.NotaConfirmacion"/>), calculada fuera del hilo de la interfaz.</summary>
    public async Task<string> NotaPapeleraAsync(IReadOnlyList<string> rutas)
    {
        IsBusy = true;
        Status = "Comprobando la papelera de cada unidad…";
        try { return await Task.Run(() => Papelera.NotaConfirmacion(rutas)); }
        finally
        {
            IsBusy = false;
            UpdateStatus();
        }
    }

    /// <summary>
    /// Envía a la papelera los archivos (la vista ya ha confirmado) y los quita de la biblioteca de una
    /// vez, con el avance en la barra de estado. Para el reproductor antes: si suena uno, está abierto y
    /// no se puede mover.
    /// </summary>
    public async Task EnviarAPapeleraAsync(IReadOnlyList<string> rutas)
    {
        if (Ocupada) { Status = "Espera a que termine el proceso en curso."; return; }
        _engine?.ReleaseAudio();
        using var cts = new CancellationTokenSource();
        _ctsPapelera = cts;
        IsBusy = true;
        EnviandoPapelera = true;
        var terminado = false;
        // Un aviso de avance que llegue tarde no debe tapar el resultado.
        var avance = new Progress<(int Hechas, int Total)>(p => { if (!terminado) Status = EnvioPapelera.TextoAvance(p.Hechas, p.Total); });
        Status = EnvioPapelera.TextoAvance(0, rutas.Count);
        try
        {
            var resultado = await PapeleraYQuitarAsync(_store, rutas, EnviarUno, avance, cts.Token);
            terminado = true;
            Status = resultado;
        }
        finally
        {
            terminado = true;
            _ctsPapelera = null;
            EnviandoPapelera = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Para el envío a la papelera entre un archivo y el siguiente; lo ya enviado sale de la
    /// biblioteca. Lo usa el cierre de la ventana. POR QUÉ (hallazgo rendimiento-3): sin esto, cerrar
    /// a mitad de un envío de cientos de archivos esperaba 15 s y cortaba el proceso de golpe, sin
    /// quitar de la biblioteca lo que ya estaba en la papelera.
    /// </summary>
    public void CancelarPapelera()
    {
        try { _ctsPapelera?.Cancel(); }
        catch (ObjectDisposedException) { /* ya había terminado */ }
    }

    /// <summary>
    /// Lo de <see cref="EnviarAPapeleraAsync"/> sin el motor, para poder probarlo con una papelera de
    /// mentira. El envío, archivo a archivo, va fuera del hilo de la interfaz con el servicio común
    /// (<see cref="EnvioPapelera"/>, el de Duplicados, la Bandeja y el Asistente); quitarlas de la
    /// biblioteca, de vuelta en él (la tabla está enlazada a esa colección). Las que la papelera rechaza
    /// (unidad sin papelera, archivo en uso) y las que no llegaron a enviarse por cancelar se quedan en
    /// la biblioteca.
    /// </summary>
    internal static async Task<string> PapeleraYQuitarAsync(LibraryStore store, IReadOnlyList<string> rutas,
                                                            Func<string, (bool Ok, string Error)> enviar,
                                                            IProgress<(int Hechas, int Total)>? avance = null,
                                                            CancellationToken ct = default)
    {
        var res = await EnvioPapelera.EnviarAsync(rutas, enviar, avance, ct);

        // De una sola vez: la biblioteca avisa a todas las pestañas en cada cambio.
        if (res.Enviadas.Count > 0) store.RemoveTracks(res.Enviadas);
        return EnvioPapelera.Resumen(res, rutas.Count, Sustantivo.Archivo);   // el mismo texto en todas las páginas
    }

    /// <summary>El título y el cuerpo del diálogo de confirmación: cuántos archivos y los primeros nombres.</summary>
    internal static (string Titulo, string Cuerpo) TextosConfirmacionPapelera(IReadOnlyList<string> rutas)
    {
        var titulo = rutas.Count == 1 ? "Se va a enviar a la papelera" : $"Se van a enviar {rutas.Count:N0} archivos a la papelera";
        var cuerpo = rutas.Count == 1
            ? System.IO.Path.GetFileName(rutas[0])
            : string.Join("\n", rutas.Take(5).Select(System.IO.Path.GetFileName))
              + (rutas.Count > 5 ? $"\n… y {rutas.Count - 5:N0} más" : "");
        return (titulo, cuerpo);
    }
}
