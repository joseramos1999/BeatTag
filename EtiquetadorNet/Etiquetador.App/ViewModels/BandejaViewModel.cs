using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción de la bandeja: su ficha, su estado y lo que se ha encontrado al revisarla.</summary>
public sealed partial class BandejaRow : FichaRow
{
    public EntradaBandeja Entrada { get; }
    public IReadOnlyList<AvisoBandeja> Avisos { get; private set; } = Array.Empty<AvisoBandeja>();

    public BandejaRow(Track track, FichaDj? ficha, EntradaBandeja entrada) : base(track, ficha) => Entrada = entrada;

    public EstadoBandeja EstadoValor => Entrada.Estado;
    public string Estado => AlmacenBandeja.Nombre(Entrada.Estado);
    public DateTime Llegada => Entrada.Llegada;
    public string LlegadaTexto => Entrada.Llegada == default ? "" : Entrada.Llegada.ToString("dd/MM/yyyy HH:mm");
    public bool HayAvisos => Avisos.Count > 0;

    /// <summary>
    /// Todos los avisos en corto, para la columna. El detalle (con qué archivo coincide) va en el
    /// panel de la derecha: en la columna, el texto largo del primero tapaba que hubiera un segundo.
    /// </summary>
    public string AvisoPrincipal => string.Join(" · ", Avisos.Select(a => RevisionBandeja.NombreCorto(a.Tipo)));

    public string AvisosDetalle => string.Join("\n", Avisos.Select(a => "• " + a.Texto));

    public void PonerAvisos(IReadOnlyList<AvisoBandeja> avisos)
    {
        Avisos = avisos;
        OnPropertyChanged(nameof(Avisos));
        OnPropertyChanged(nameof(HayAvisos));
        OnPropertyChanged(nameof(AvisoPrincipal));
        OnPropertyChanged(nameof(AvisosDetalle));
    }

    public void EstadoCambiado()
    {
        OnPropertyChanged(nameof(EstadoValor));
        OnPropertyChanged(nameof(Estado));
    }
}

/// <summary>
/// Página Bandeja de entrada: la música recién descargada se revisa ANTES de entrar en la biblioteca.
///
/// La idea es cortar los problemas en la puerta. Un duplicado, un archivo de baja calidad o una
/// canción sin etiquetas que ya ha entrado en rekordbox cuesta mucho más de arreglar que uno que
/// todavía está en una carpeta aparte.
/// </summary>
public partial class BandejaViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina
{
    private readonly AppEngine _engine;
    private readonly LibraryStore _store;
    private List<BandejaRow> _seleccion = new();
    private bool _guardando;

    public ObservableCollection<BandejaRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }
    public FichaEditorViewModel Editor { get; } = new();

    /// <summary>Carpetas de la biblioteca a las que se puede pasar la música.</summary>
    public ObservableCollection<string> Destinos { get; } = new();

    [ObservableProperty] private string _carpeta = "";
    [ObservableProperty] private string? _destino;
    [ObservableProperty] private BandejaRow? _selectedRow;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _busqueda = "";
    [ObservableProperty] private string _filtroInfo = "";
    [ObservableProperty] private string _recuento = "";
    [ObservableProperty] private string _detalle = "";

    public IReadOnlyList<string> Filtros { get; } = new[] { "Todas", "Con avisos", "Recibidas", "Analizadas", "Revisadas", "Preparadas" };
    [ObservableProperty] private string _filtro = "Todas";

    public bool HayCarpeta => Carpeta.Length > 0;

    public BandejaViewModel(AppEngine engine)
    {
        _engine = engine;
        _store = engine.Library;
        RowsView = new DataGridCollectionView(Rows);
        Editor.GuardarPedido += GuardarFicha;

        _carpeta = engine.Config.BandejaCarpeta;
        _destino = engine.Config.BandejaDestino;
        RellenarDestinos();

        // La biblioteca cambia (se escanea, se añaden carpetas): hay que volver a buscar duplicados
        // contra lo que hay AHORA. No se relee ningún archivo de la bandeja, solo se revisa.
        _store.Changed += () => { RellenarDestinos(); Revisar(); };
        _store.Folders.CollectionChanged += (_, _) => RellenarDestinos();
        _engine.FichasCambiadas += () => { if (!_guardando) RefrescarFichas(); };

        Status = Carpeta.Length == 0
            ? "Elige la carpeta donde dejas la música nueva. Tiene que estar fuera de la biblioteca."
            : "Pulsa «Analizar» para revisar lo que hay en la bandeja.";
    }

    partial void OnCarpetaChanged(string value) => OnPropertyChanged(nameof(HayCarpeta));

    partial void OnDestinoChanged(string? value)
    {
        if (value == null) return;
        _engine.Config.BandejaDestino = value;
        _engine.SaveConfig();
        Revisar();   // el aviso de nombre ocupado depende del destino
    }

    partial void OnBusquedaChanged(string value) => AplicarFiltro();
    partial void OnFiltroChanged(string value) => AplicarFiltro();

    // --- Carpetas ---

    /// <summary>
    /// Destinos posibles: las carpetas marcadas de la biblioteca, más la que el usuario eligiera a
    /// mano si cuelga de una de ellas. Una carpeta fuera de la biblioteca no vale como destino: la
    /// canción «entraría en la biblioteca» y no aparecería en ella.
    /// </summary>
    private void RellenarDestinos()
    {
        var actual = Destino;
        var raices = _store.EnabledPaths();
        var lista = new List<string>(raices);
        if (!string.IsNullOrEmpty(actual) && !lista.Contains(actual, StringComparer.OrdinalIgnoreCase)
            && raices.Any(r => Rutas.EstaDentroDe(actual, r)))
            lista.Add(actual);

        Destinos.Clear();
        foreach (var d in lista) Destinos.Add(d);

        var elegido = lista.FirstOrDefault(d => string.Equals(d, actual, StringComparison.OrdinalIgnoreCase))
                      ?? lista.FirstOrDefault();
        if (!string.Equals(elegido, Destino, StringComparison.Ordinal)) Destino = elegido;
    }

    /// <summary>Fija la carpeta de la bandeja, si es válida, y la analiza. Devuelve "" o el motivo del rechazo.</summary>
    public async Task<string> ElegirCarpetaAsync(string carpeta)
    {
        var motivo = RevisionBandeja.ValidarCarpeta(carpeta, _store.Folders.Select(f => f.Path), Directory.Exists);
        if (motivo.Length > 0) { Status = motivo; return motivo; }

        Carpeta = carpeta;
        _engine.Config.BandejaCarpeta = carpeta;
        _engine.SaveConfig();
        _engine.Logger.Detail($"Bandeja de entrada: {carpeta}");
        await AnalizarAsync();
        return "";
    }

    /// <summary>Elige un destino dentro de la biblioteca. Devuelve "" o el motivo del rechazo.</summary>
    public string ElegirDestino(string carpeta)
    {
        var raices = _store.EnabledPaths();
        if (!raices.Any(r => Rutas.MismaCarpeta(carpeta, r) || Rutas.EstaDentroDe(carpeta, r)))
        {
            Status = "El destino tiene que ser una carpeta de la biblioteca (o estar dentro de una), para que las canciones aparezcan en ella.";
            return Status;
        }
        if (!Destinos.Contains(carpeta, StringComparer.OrdinalIgnoreCase)) Destinos.Add(carpeta);
        Destino = Destinos.First(d => string.Equals(d, carpeta, StringComparison.OrdinalIgnoreCase));
        return "";
    }

    [RelayCommand]
    private async Task AbrirCarpetaAsync()
    {
        if (Carpeta.Length > 0 && Directory.Exists(Carpeta)) await Shell.OpenFolderAsync(Carpeta);
    }

    // --- Analizar ---

    [RelayCommand]
    public async Task AnalizarAsync()
    {
        if (IsBusy) return;
        var motivo = RevisionBandeja.ValidarCarpeta(Carpeta, _store.Folders.Select(f => f.Path), Directory.Exists);
        if (motivo.Length > 0) { Status = motivo; return; }

        IsBusy = true;
        Progress = 0;
        Status = "Leyendo la bandeja…";
        try
        {
            var carpeta = Carpeta;
            var progreso = new Progress<double>(p => Progress = p);
            var tracks = await Task.Run(() =>
            {
                var rutas = LibraryScanner.EnumerateFiles(carpeta, recursive: true).ToList();
                var lista = new List<Track>(rutas.Count);
                for (var i = 0; i < rutas.Count; i++)
                {
                    var t = LibraryScanner.ReadTrack(rutas[i]);
                    t.Folder = carpeta;
                    lista.Add(t);
                    ((IProgress<double>)progreso).Report(100.0 * (i + 1) / rutas.Count);
                }
                return lista;
            });

            var ahora = DateTime.Now;
            var nuevas = 0;
            Rows.Clear();
            foreach (var t in tracks.OrderBy(t => t.FileName, StringComparer.CurrentCultureIgnoreCase))
            {
                var e = _engine.Bandeja.Registrar(t.FilePath, ahora);
                if (e.Estado == EstadoBandeja.Recibida) nuevas++;
                e.Estado = AlmacenBandeja.TrasAnalizar(e.Estado);
                Rows.Add(new BandejaRow(t, _engine.Fichas.Obtener(t.FilePath), e));
            }
            var olvidadas = _engine.Bandeja.Podar(tracks.Select(t => t.FilePath));
            GuardarBandeja();

            Revisar();
            Seleccionar(Array.Empty<BandejaRow>());
            _engine.Logger.Detail($"Bandeja: {tracks.Count} canciones ({nuevas} nuevas; {olvidadas} ya no estaban).");
            Status = tracks.Count == 0
                ? "La bandeja está vacía."
                : $"{tracks.Count} canciones en la bandeja" + (nuevas > 0 ? $", {nuevas} nuevas." : ".");
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Error al analizar la bandeja", e);
            Status = "Error al leer la bandeja: " + e.Message;
        }
        finally { IsBusy = false; Progress = 0; }
    }

    /// <summary>Vuelve a buscar avisos sin releer los archivos. Barato: se llama cada vez que cambia la biblioteca.</summary>
    private void Revisar()
    {
        if (Rows.Count == 0) { ActualizarRecuento(); return; }
        var biblioteca = _store.IsScanned ? new IndiceCanciones(_store.Tracks) : IndiceCanciones.Vacio;
        var bandeja = new IndiceCanciones(Rows.Select(r => r.Track));
        var destino = Destino ?? "";
        foreach (var r in Rows)
            r.PonerAvisos(RevisionBandeja.Revisar(r.Track, biblioteca, bandeja, destino, File.Exists));
        AplicarFiltro();
        ActualizarRecuento();
        MostrarDetalle();
    }

    private void AplicarFiltro()
    {
        var q = Busqueda.Trim();
        Func<BandejaRow, bool> porEstado = Filtro switch
        {
            "Con avisos" => r => r.HayAvisos,
            "Recibidas" => r => r.EstadoValor == EstadoBandeja.Recibida,
            "Analizadas" => r => r.EstadoValor == EstadoBandeja.Analizada,
            "Revisadas" => r => r.EstadoValor == EstadoBandeja.Revisada,
            "Preparadas" => r => r.EstadoValor == EstadoBandeja.Preparada,
            _ => _ => true,
        };
        RowsView.Filter = q.Length == 0 && Filtro == "Todas"
            ? null
            : o => o is BandejaRow r && porEstado(r)
                   && (q.Length == 0 || BusquedaTexto.Coincide(q, r.FileName, r.Artist, r.Title, r.Genre, r.AvisosDetalle));
        RowsView.Refresh();
        FiltroInfo = RowsView.Filter == null ? "" : $"{RowsView.Count} de {Rows.Count}";
    }

    private void ActualizarRecuento()
    {
        int N(EstadoBandeja e) => Rows.Count(r => r.EstadoValor == e);
        Recuento = Rows.Count == 0
            ? ""
            : $"Recibidas {N(EstadoBandeja.Recibida)} · Analizadas {N(EstadoBandeja.Analizada)} · "
              + $"Revisadas {N(EstadoBandeja.Revisada)} · Preparadas {N(EstadoBandeja.Preparada)} · "
              + $"Con avisos {Rows.Count(r => r.HayAvisos)}";
    }

    // --- Selección, estados y ficha ---

    public void Seleccionar(IEnumerable<BandejaRow> filas)
    {
        _seleccion = filas.ToList();
        Editor.Cargar(_seleccion.Select(r => r.Ficha).ToList());
        MostrarDetalle();
    }

    public int CuantasSeleccionadas => _seleccion.Count;

    private void MostrarDetalle()
    {
        Detalle = _seleccion.Count switch
        {
            0 => "",
            1 => _seleccion[0].HayAvisos ? _seleccion[0].AvisosDetalle : "Sin avisos.",
            _ => $"{_seleccion.Count} seleccionadas · {_seleccion.Count(r => r.HayAvisos)} con avisos.",
        };
    }

    [RelayCommand]
    private void MarcarRevisadas() => Marcar(EstadoBandeja.Revisada);

    [RelayCommand]
    private void MarcarPreparadas() => Marcar(EstadoBandeja.Preparada);

    /// <summary>Quita la marca que puso el usuario y la deja como la dejó el análisis.</summary>
    [RelayCommand]
    private void QuitarMarca() => Marcar(EstadoBandeja.Analizada);

    private void Marcar(EstadoBandeja estado)
    {
        if (_seleccion.Count == 0) { Status = "Selecciona antes las canciones."; return; }
        foreach (var r in _seleccion)
        {
            _engine.Bandeja.Marcar(r.FilePath, estado);
            r.EstadoCambiado();
        }
        GuardarBandeja();
        ActualizarRecuento();
        if (Filtro != "Todas") AplicarFiltro();
        Status = $"{_seleccion.Count} canción(es) marcadas como {AlmacenBandeja.Nombre(estado).ToLowerInvariant()}s.";
    }

    private void GuardarFicha() => AplicarFicha(Editor.Cambios());

    public void PonerEnergiaRapida(int energia)
    {
        if (_seleccion.Count == 0) return;
        AplicarFicha(new CambiosFicha { Energia = Math.Clamp(energia, 0, Fichas.EnergiaMaxima) });
    }

    private void AplicarFicha(CambiosFicha cambios)
    {
        if (_seleccion.Count == 0) return;
        if (!cambios.HayAlguno) { Status = "No hay cambios que guardar."; return; }
        var err = _engine.GuardarFichas(_seleccion.Select(r => r.FilePath).ToList(), cambios);
        foreach (var r in _seleccion) r.Actualizar(_engine.Fichas.Obtener(r.FilePath));
        Editor.Cargar(_seleccion.Select(r => r.Ficha).ToList());
        _guardando = true;
        try { _engine.AvisarFichasCambiadas(); }
        finally { _guardando = false; }
        Status = err.Length > 0 ? $"⚠ No se pudo guardar en disco: {err}" : "Ficha guardada.";
    }

    private void RefrescarFichas()
    {
        foreach (var r in Rows) r.Actualizar(_engine.Fichas.Obtener(r.FilePath));
        Editor.Cargar(_seleccion.Select(r => r.Ficha).ToList());
    }

    public async Task VolcarAlComentarioAsync()
    {
        if (IsBusy || _seleccion.Count == 0) return;
        var rutas = _seleccion.Select(r => r.FilePath).ToList();
        IsBusy = true;
        Status = $"Escribiendo la ficha en el comentario de {rutas.Count} canción(es)…";
        try
        {
            _engine.ReleaseAudio();
            var res = await Task.Run(() => _engine.VolcarFichasAlComentario(rutas));
            Status = FichasViewModel.TextoVolcado(res);
        }
        catch (Exception e) { Status = "Error al escribir el comentario: " + e.Message; }
        finally { IsBusy = false; }
    }

    // --- Pasar a la biblioteca ---

    public IReadOnlyList<BandejaRow> Preparadas => Rows.Where(r => r.EstadoValor == EstadoBandeja.Preparada).ToList();

    /// <summary>
    /// Mueve las preparadas a la carpeta de destino. La confirmación la pide la vista.
    ///
    /// Cada movimiento se anota para deshacer EN CUANTO se hace, así que si algo se corta a medias
    /// lo ya movido sigue siendo reversible. Un archivo que no se puede mover (por ejemplo, porque en
    /// el destino ya hay uno con ese nombre) se queda en la bandeja con su estado: no se pierde nada.
    /// </summary>
    public async Task PasarABibliotecaAsync()
    {
        if (IsBusy) return;
        var filas = Preparadas;
        var destino = Destino ?? "";
        if (filas.Count == 0) { Status = "No hay canciones preparadas. Márcalas como preparadas cuando estén listas."; return; }
        if (destino.Length == 0) { Status = "Elige a qué carpeta de la biblioteca van."; return; }

        IsBusy = true;
        Progress = 0;
        Status = $"Pasando {filas.Count} canción(es) a la biblioteca…";
        try
        {
            _engine.ReleaseAudio();
            var undo = Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            var rutas = filas.Select(r => r.FilePath).ToList();
            var progreso = new Progress<double>(p => Progress = p);

            var (hechos, fallos, undoErr) = await Task.Run(() =>
            {
                var ok = new List<Traslado>();
                var mal = new List<Traslado>();
                var errUndo = "";
                for (var i = 0; i < rutas.Count; i++)
                {
                    var t = TrasladoBandeja.Mover(rutas[i], destino);
                    if (t.Ok)
                    {
                        ok.Add(t);
                        var rec = new UndoRecord { OrigPath = t.Origen, FinalPath = t.Destino, Renamed = true };
                        try { File.AppendAllText(undo, JsonSerializer.Serialize(rec) + "\n"); }
                        catch (Exception e) { errUndo = e.Message; }
                    }
                    else mal.Add(t);
                    ((IProgress<double>)progreso).Report(100.0 * (i + 1) / rutas.Count);
                }
                return (ok, mal, errUndo);
            });

            foreach (var t in hechos)
            {
                _engine.Fichas.Mover(t.Origen, t.Destino);
                _engine.Bandeja.Olvidar(t.Origen);
                _engine.Logger.Detail($"Bandeja → biblioteca: {Path.GetFileName(t.Origen)} → {t.Destino}");
            }
            foreach (var t in fallos)
                _engine.Logger.Log($"Bandeja: no se movió {Path.GetFileName(t.Origen)}: {t.Error}", LogKind.Err);

            var errFichas = _engine.Fichas.Guardar();
            GuardarBandeja();

            var movidas = new HashSet<string>(hechos.Select(h => h.Origen), StringComparer.OrdinalIgnoreCase);
            foreach (var r in Rows.Where(r => movidas.Contains(r.FilePath)).ToList()) Rows.Remove(r);
            Seleccionar(Array.Empty<BandejaRow>());

            // Ya están en una carpeta de la biblioteca: se reescanea para que aparezcan en todas las
            // páginas. Esto vuelve a revisar la bandeja con la biblioteca nueva.
            if (hechos.Count > 0) await _store.ScanAsync();
            else Revisar();

            var texto = $"{hechos.Count} canción(es) pasadas a la biblioteca";
            if (fallos.Count > 0) texto += $" · {fallos.Count} se quedan en la bandeja: {fallos[0].Error}";
            texto += undoErr.Length > 0
                ? $". ⚠ El traslado NO quedó anotado y no se podrá deshacer: {undoErr}"
                : hechos.Count > 0 ? ". Se puede deshacer desde Enriquecer." : ".";
            if (errFichas.Length > 0) texto += $" ⚠ No se pudieron guardar las fichas: {errFichas}";
            Status = texto;
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Error al pasar canciones a la biblioteca", e);
            Status = "Error al pasar a la biblioteca: " + e.Message;
        }
        finally { IsBusy = false; Progress = 0; }
    }

    private void GuardarBandeja()
    {
        var err = _engine.Bandeja.Guardar();
        if (err.Length > 0)
        {
            _engine.Logger.Err("No se pudo guardar el estado de la bandeja: " + err);
            Status = "⚠ No se pudo guardar el estado de la bandeja: " + err;
        }
    }

    // --- Menú contextual ---

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync() => await Shell.OpenContainingFolderAsync(SelectedRow?.FilePath);
}
