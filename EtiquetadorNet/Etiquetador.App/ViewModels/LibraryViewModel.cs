using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly AppEngine _engine;
    private readonly LibraryStore _store;

    public ObservableCollection<FolderItem> Folders => _store.Folders;
    public DataGridCollectionView TracksView { get; }

    [ObservableProperty] private string _status = "Añade una o varias carpetas y pulsa Escanear.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _selectedFolder;

    public LibraryViewModel(AppEngine engine)
    {
        _engine = engine;
        _store = engine.Library;
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
        if (_store.Folders.Count == 0)
        {
            Status = "Añade una o varias carpetas y pulsa Escanear.";
            return;
        }

        // Que la lista esté vacía teniendo carpetas no es lo mismo que no tener ninguna, y hasta
        // ahora se decía igual: quien acababa de añadir una carpeta leía «añade una carpeta».
        if (_store.Tracks.Count == 0)
        {
            Status = $"{_store.Folders.Count} carpeta(s) sin escanear. Pulsa Escanear.";
            return;
        }

        var incompletas = _store.Tracks.Count(t => t.IsIncomplete);
        Status = $"{_store.Tracks.Count} canciones · {incompletas} incompletas · {_store.Folders.Count} carpeta(s)"
               + (_store.IsScanned ? "" : " · hay carpetas por escanear, pulsa Escanear");
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
            var overwrite = _engine.Config.Overwrite;
            var undoFile = System.IO.Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var (matched, updated, failures) = await Task.Run(() =>
            {
                var byPath = new Dictionary<string, RekordboxEntry>(StringComparer.OrdinalIgnoreCase);
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
                return (m, u, fails);
            });

            foreach (var f in failures) _engine.Logger.Log("rekordbox: " + f, LogKind.Err);
            await _store.ScanAsync();
            Status = matched == 0
                ? "Rekordbox: 0 coincidencias (¿las rutas del XML no apuntan a estas carpetas?)."
                : $"Rekordbox: {matched} coincidencias · {updated} actualizadas · {failures.Count} fallos. Deshacer disponible en Enriquecer.";
        }
        catch (Exception e) { Status = "Error importando rekordbox: " + e.Message; }
        finally { IsBusy = false; }
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
        try { _engine.Preview.Toggle(ruta); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    public void Editar(string ruta)
    {
        if (Ocupada) { Status = "Espera a que termine el proceso en curso."; return; }
        _engine.RequestEdit(ruta);
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
        _engine.ReleaseAudio();
        IsBusy = true;
        var total = rutas.Count;
        var terminado = false;
        // Un aviso de avance que llegue tarde no debe tapar el resultado.
        var avance = new Progress<int>(n => { if (!terminado) Status = $"Enviando a la papelera… {n:N0} de {total:N0}"; });
        Status = "Enviando a la papelera…";
        try
        {
            var resultado = await PapeleraYQuitarAsync(_store, rutas, Papelera.Enviar, avance);
            terminado = true;
            Status = resultado;
        }
        finally
        {
            terminado = true;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Lo de <see cref="EnviarAPapeleraAsync"/> sin el motor, para poder probarlo con una papelera de
    /// mentira. El envío, archivo a archivo, va fuera del hilo de la interfaz; quitarlas de la biblioteca,
    /// de vuelta en él (la tabla está enlazada a esa colección). Las que la papelera rechaza (unidad sin
    /// papelera, archivo en uso) se quedan en la biblioteca.
    /// </summary>
    internal static async Task<string> PapeleraYQuitarAsync(LibraryStore store, IReadOnlyList<string> rutas,
                                                            Func<string, (bool Ok, string Error)> enviar, IProgress<int>? avance = null)
    {
        var (enviadas, fallos) = await Task.Run(() => EnviarUnaAUna(rutas, enviar, avance));

        // De una sola vez: la biblioteca avisa a todas las pestañas en cada cambio.
        store.RemoveTracks(enviadas);
        return TextoPapelera(enviadas, fallos);
    }

    /// <summary>Envía cada archivo y dice, tras cada uno, cuántos lleva.</summary>
    internal static (List<string> Enviadas, List<string> Fallos) EnviarUnaAUna(IReadOnlyList<string> rutas,
                                                                              Func<string, (bool Ok, string Error)> enviar, IProgress<int>? avance)
    {
        var enviadas = new List<string>();
        var fallos = new List<string>();
        for (var i = 0; i < rutas.Count; i++)
        {
            var r = rutas[i];
            var (ok, error) = enviar(r);
            if (ok) enviadas.Add(r);
            else fallos.Add($"{System.IO.Path.GetFileName(r)}: {(string.IsNullOrWhiteSpace(error) ? "motivo desconocido." : error)}");
            avance?.Report(i + 1);
        }
        return (enviadas, fallos);
    }

    private static string TextoPapelera(IReadOnlyList<string> enviadas, IReadOnlyList<string> fallos)
    {
        if (fallos.Count == 0)
            return enviadas.Count == 1 ? $"Enviado a la papelera: {System.IO.Path.GetFileName(enviadas[0])}."
                                       : $"{enviadas.Count} archivos enviados a la papelera.";
        return enviadas.Count == 0 && fallos.Count == 1
            ? "No se pudo enviar a la papelera. " + fallos[0]
            : $"{enviadas.Count} enviados a la papelera, {fallos.Count} con problemas. El primero: {fallos[0]}";
    }
}
