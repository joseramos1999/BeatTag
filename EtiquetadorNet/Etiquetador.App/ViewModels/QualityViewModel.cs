using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Collections;
using Etiquetador.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core.Analysis;

namespace Etiquetador.App.ViewModels;

public sealed partial class QualRow : ObservableObject, IFilaBuscable
{
    public string FileName { get; init; } = "";
    public string QualityLabel { get; init; } = "";
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public string Duration { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";
    public bool IsPoor { get; init; }

    /// <summary>El formato es sin pérdida (FLAC, WAV, AIFF…): cambia lo que se espera del espectro.</summary>
    public bool SinPerdida { get; init; }

    private string? _textoBusqueda;

    /// <summary>Lo que encuentra el cuadro «Buscar…». Los campos no cambian: se prepara una sola vez.</summary>
    public string TextoBusqueda => _textoBusqueda ??= ConsultaTabla.Preparar(FileName, Folder, QualityLabel);

    // --- Calidad real por espectro (P4, equipo audio_medido) ---

    /// <summary>Columna «Corte medido»: la frecuencia, «Sin corte», «No concluyente» o vacío si no se ha analizado.</summary>
    [ObservableProperty] private string _corteMedido = "";

    /// <summary>Columna «Calidad real (estimada)», con la valoración si es dudosa o sospechosa.</summary>
    [ObservableProperty] private string _calidadReal = "";

    /// <summary>Para ordenar por «Corte medido» de forma numérica (−1 sin medida; 0 no concluyente).</summary>
    [ObservableProperty] private double _corteOrden = -1;

    [ObservableProperty] private ValoracionCalidad _valoracion;

    /// <summary>Pone (o quita, con null) la medida del espectro.</summary>
    public void PonerMedida(MedidaEspectro? medida)
    {
        if (medida is not { } m)
        {
            CorteMedido = "";
            CalidadReal = "";
            CorteOrden = -1;
            Valoracion = ValoracionCalidad.SinValorar;
            return;
        }
        var v = CorteEspectral.Valorar(m, SinPerdida, Bitrate);
        var estimada = CorteEspectral.TextoCalidadReal(m, SinPerdida);
        CorteMedido = CorteEspectral.TextoCorte(m);
        CalidadReal = v is ValoracionCalidad.Sospechosa or ValoracionCalidad.Dudosa
            ? $"{estimada} · {CorteEspectral.TextoValoracion(v)}"
            : estimada;
        CorteOrden = m.Estado switch
        {
            EstadoCorte.Corte => m.CorteHz,
            EstadoCorte.SinCorte => m.TechoHz + AnchoSinCorte,   // por encima de cualquier corte medido
            EstadoCorte.NoConcluyente => 0,
            _ => -1,
        };
        Valoracion = v;
    }

    private const double AnchoSinCorte = 50;
}

/// <summary>Pestaña Calidad: clasifica el audio de la biblioteca compartida.</summary>
public partial class QualityViewModel : ScanViewModelBase, IPaginaVisible, IProgresoPagina
{
    private readonly AppEngine _engine;

    public ObservableCollection<QualRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    [ObservableProperty] private QualRow? _selectedRow;
    [ObservableProperty] private bool _onlyPoor;

    /// <summary>
    /// La tabla tiene una fila por canción (15.000 en una biblioteca grande): solo se rehace con la
    /// página a la vista, y si no, al volver a ella. Antes se rehacía con cada cambio de la biblioteca
    /// aunque nadie la mirase, y desde el arranque; ahora además relee las medidas del espectro, que
    /// no tiene sentido hacer para una página oculta.
    /// </summary>
    private readonly RecalculoDiferido _recalculo;

    public QualityViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(QualRow.Folder)));
        _filtroDiferido = new BusquedaDiferida(AplicarFiltro);
        // Sin recálculo aquí: se hace la primera vez que la página se muestra.
        _recalculo = new RecalculoDiferido(Recompute);
    }

    /// <summary>Lo avisa la vista.</summary>
    public bool Visible
    {
        get => _recalculo.Visible;
        set => _recalculo.CambiarVisible(value);
    }

    protected override void RecomputeReactive() => _recalculo.Pedir();

    private readonly BusquedaDiferida _filtroDiferido;

    /// <summary>Cuadro de búsqueda de la tabla. Se combina con los filtros de «solo baja calidad» y «solo sospechosos».</summary>
    [ObservableProperty] private string _busqueda = "";

    /// <summary>Cuántas quedan a la vista mientras hay búsqueda o filtro de sospechosos. Vacío si no.</summary>
    [ObservableProperty] private string _filtroInfo = "";

    // La casilla filtra al momento; el cuadro de búsqueda, al dejar de teclear.
    partial void OnOnlyPoorChanged(bool value) => _filtroDiferido.Ahora();

    partial void OnBusquedaChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) _filtroDiferido.Ahora();
        else _filtroDiferido.Pedir();
    }

    /// <summary>
    /// Los filtros a la vez. Se combinan en vez de pisarse: buscar dentro de lo que ya está acotado a
    /// baja calidad o a sospechosos es exactamente lo que se quiere hacer.
    ///
    /// Es la tabla más grande (una fila por canción, unas 15.000 en una biblioteca real): por eso
    /// la consulta se prepara una vez, cada fila guarda su texto normalizado y no se refresca dos
    /// veces (asignar el filtro ya rehace la vista).
    /// </summary>
    private void AplicarFiltro()
    {
        var consulta = ConsultaTabla.De(Busqueda);
        var soloMalas = OnlyPoor;
        var soloSospechosos = SoloSospechosos;
        RowsView.Filter = !soloMalas && !soloSospechosos && consulta.Vacia
            ? null
            : o => o is QualRow r
                   && (!soloMalas || r.IsPoor)
                   && (!soloSospechosos || r.Valoracion == ValoracionCalidad.Sospechosa)
                   && (consulta.Vacia || consulta.Coincide(r));
        ContarFiltradas();
    }

    private void ContarFiltradas()
        => FiltroInfo = TextoFiltro(!ConsultaTabla.De(Busqueda).Vacia, SoloSospechosos, RowsView.Count, Rows.Count, _medidas.Count > 0,
                                    _leyendoMedidas);

    /// <summary>
    /// «N de M» mientras hay búsqueda o filtro de sospechosos. Con «Solo sospechosos» y nada medido,
    /// la tabla vacía no quiere decir que no haya sospechosos: se dice que hay que analizar antes
    /// («0 de 15000» se leía como «ninguno»). Mientras se releen las medidas guardadas
    /// (<paramref name="leyendoMedidas"/>) tampoco hay ninguna a la vista, pero analizar no hace falta:
    /// con 15.000 archivos en un disco duro esa lectura dura varios segundos, y pedir que se analizara
    /// lo que ya está medido confundía.
    /// </summary>
    internal static string TextoFiltro(bool hayBusqueda, bool soloSospechosos, int vistas, int total, bool hayMedidas,
                                       bool leyendoMedidas = false)
    {
        if (!hayBusqueda && !soloSospechosos) return "";
        var texto = $"{vistas} de {total}";
        if (!soloSospechosos || hayMedidas) return texto;
        return texto + (leyendoMedidas
            ? " · Leyendo las medidas guardadas…"
            : " · Pulsa «Analizar calidad real» para medir los archivos.");
    }

    protected override void Recompute()
    {
        Rows.Clear();
        // Sin biblioteca escaneada no hay nada que contar: se deja la indicación de pulsar «Analizar».
        if (!Store.IsScanned) { ContarFiltradas(); return; }
        foreach (var t in Store.Tracks)
        {
            var tier = AudioQuality.Rate(t);
            var fila = new QualRow
            {
                FileName = t.FileName,
                QualityLabel = AudioQuality.Label(tier),
                Bitrate = t.Bitrate,
                SampleRate = t.SampleRate,
                Channels = t.Channels,
                Duration = t.Duration,
                Folder = t.Folder,
                FilePath = t.FilePath,
                IsPoor = AudioQuality.IsPoor(t),
                SinPerdida = tier == QualityTier.Lossless,
            };
            // Lo último leído de la caché, para no dejar las columnas vacías mientras se relee.
            if (_medidas.TryGetValue(t.FilePath, out var m)) fila.PonerMedida(m);
            Rows.Add(fila);
        }
        RowsView.Refresh();
        ContarFiltradas();
        ActualizarEstado();
        // Las medidas guardadas se validan contra el disco (fecha y tamaño de cada archivo) fuera del
        // hilo de la interfaz.
        _ = SinEsperar.Lanzar(CargarMedidasAsync, e => _engine.Logger.Error("Calidad: no se pudieron leer las medidas del espectro", e));
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }

    // --- Calidad real por espectro (P4, equipo audio_medido) ---
    //
    // El bitrate declarado no distingue casi nada: en una biblioteca real, el 88 % de los archivos
    // declara 320 kbps. Lo que delata un «320» hecho de un 128 es el espectro: el audio se acaba en el
    // corte del 128 (hacia 16 kHz). Esto lo mide en segundo plano, lo guarda en caché y lo enseña en
    // dos columnas, siempre como SOSPECHA y con la frecuencia medida a la vista. No borra nada.

    /// <summary>Filtro «Solo sospechosos»: los que declaran mucha más calidad de la que tiene su audio.</summary>
    [ObservableProperty] private bool _soloSospechosos;

    /// <summary>Hay un análisis del espectro en marcha (para el botón «Cancelar» y la barra).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OcupadaSinAvance))]
    [NotifyPropertyChangedFor(nameof(ConAvance))]
    private bool _analizandoEspectro;

    /// <summary>
    /// Solo el análisis del espectro dice por dónde va. Escanear la biblioteca («Analizar», o el
    /// escaneo previo al análisis) no toca <see cref="Progress"/>, y la barra del pie quedaba parada
    /// en 0 o en el 100 de la vez anterior.
    /// </summary>
    public bool ConAvance => AnalizandoEspectro;

    /// <summary>Avance del análisis del espectro, de 0 a 100. También lo enseña la barra del pie.</summary>
    [ObservableProperty] private double _progress;

    /// <summary>
    /// Ocupada en algo que no dice cuánto lleva (escanear la biblioteca): la barra sin avance. El
    /// análisis del espectro tiene la suya, con el avance, y no se enseñan las dos a la vez.
    /// </summary>
    public bool OcupadaSinAvance => IsBusy && !AnalizandoEspectro;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) OnPropertyChanged(nameof(OcupadaSinAvance));
    }

    partial void OnSoloSospechososChanged(bool value) => _filtroDiferido.Ahora();

    /// <summary>Las medidas válidas leídas la última vez, por ruta.</summary>
    private Dictionary<string, MedidaEspectro> _medidas = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _ctsEspectro;
    private CancellationTokenSource? _ctsCarga;

    /// <summary>
    /// Lee de la caché las medidas que siguen valiendo para las filas de ahora y las pone en la tabla.
    /// Si nunca se ha analizado nada (no hay caché en disco ni cargada), no hace nada: ni siquiera
    /// crea la caché. Comprobar la fecha y el tamaño de 15.000 archivos lleva su tiempo en un disco
    /// duro, así que va fuera del hilo de la interfaz; si mientras tanto se pide otra lectura (la
    /// biblioteca volvió a cambiar), esta se abandona sin más: la nueva ya trae lo último.
    /// </summary>
    private async Task CargarMedidasAsync()
    {
        if (!File.Exists(EspectroScanner.RutaCache(_engine.Paths)) && _medidas.Count == 0 && !AnalizandoEspectro) return;
        _ctsCarga?.Cancel();
        var cts = _ctsCarga = new CancellationTokenSource();
        var ct = cts.Token;
        var rutas = Rows.Select(r => r.FilePath).ToList();
        PonerLeyendoMedidas(true);
        Dictionary<string, MedidaEspectro> medidas;
        try
        {
            medidas = await Task.Run(() =>
            {
                var cache = _engine.Espectro;
                var d = new Dictionary<string, MedidaEspectro>(StringComparer.OrdinalIgnoreCase);
                foreach (var ruta in rutas)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cache.Get(ruta) is { } m) d[ruta] = m;
                }
                return d;
            }, ct);
        }
        // Abandonada por otra lectura más reciente: esa sigue leyendo y dirá cuándo termina.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch
        {
            // El fallo llega al registro (SinEsperar.Lanzar); el pie deja de decir que se está leyendo.
            if (ReferenceEquals(_ctsCarga, cts)) PonerLeyendoMedidas(false);
            throw;
        }
        if (ct.IsCancellationRequested) return;
        _medidas = medidas;
        _leyendoMedidas = false;   // AplicarFiltro vuelve a contar ya con las medidas puestas
        foreach (var r in Rows) r.PonerMedida(medidas.TryGetValue(r.FilePath, out var m) ? m : null);
        AplicarFiltro();
        ActualizarEstado();
    }

    /// <summary>
    /// Se están releyendo las medidas guardadas (<see cref="CargarMedidasAsync"/>). Mientras tanto, con
    /// «Solo sospechosos» el pie lo dice en vez de pedir que se analice: tras arrancar o cuando cambia la
    /// biblioteca, la tabla se rehace sin medidas y se quedaba en «0 de N · Pulsa «Analizar calidad
    /// real»…» durante los segundos que tarda la lectura, aunque todo estuviera medido.
    /// </summary>
    private bool _leyendoMedidas;

    private void PonerLeyendoMedidas(bool leyendo)
    {
        if (_leyendoMedidas == leyendo) return;
        _leyendoMedidas = leyendo;
        ContarFiltradas();
    }

    /// <summary>
    /// Analiza el espectro de toda la biblioteca. Lo ya analizado (y sin cambios desde entonces) no
    /// se vuelve a leer, así que la segunda vez solo se miran las canciones nuevas o modificadas.
    /// </summary>
    [RelayCommand]
    private async Task AnalizarEspectroAsync()
    {
        if (IsBusy) return;
        if (Store.Folders.Count == 0) { Status = "No hay carpetas configuradas. Añádelas en la pestaña Biblioteca."; return; }
        IsBusy = true;
        var cts = _ctsEspectro = new CancellationTokenSource();
        _engine.ReleaseAudio();   // no conviene leer mientras suena algo
        try
        {
            // Mientras se escanea, la barra sin avance (OcupadaSinAvance): el escaneo no dice por dónde
            // va, y la del análisis se quedaba parada en 0. La del análisis sale después.
            if (!Store.IsScanned) { Status = "Escaneando biblioteca…"; await Store.ScanAsync(); }
            Progress = 0;
            AnalizandoEspectro = true;
            var todas = Store.Tracks.Select(t => t.FilePath).ToList();
            var analizables = todas.Where(EspectroScanner.Analizable).ToList();
            _engine.Logger.Head($"Calidad: analizando el espectro de {analizables.Count} {Sustantivo.Archivo.De(analizables.Count)}");
            var reloj = System.Diagnostics.Stopwatch.StartNew();
            var avance = new Progress<(int done, int total, string file)>(p =>
            {
                Progress = p.total == 0 ? 0 : p.done * 100.0 / p.total;
                Status = $"Analizando la calidad real: {p.done} de {p.total}…  {p.file}";
            });
            var cache = await Task.Run(() => _engine.Espectro);
            await cache.MeasureManyAsync(analizables, force: false, avance, cts.Token);
            await CargarMedidasAsync();
            Progress = 100;
            var resumen = ResumenEspectro(todas.Count, analizables.Count);
            Status = $"Análisis de la calidad real terminado en {TextUtils.FormatEta(reloj.Elapsed.TotalSeconds)}. {resumen}";
            _engine.Logger.Sum("Calidad: " + resumen);
        }
        catch (OperationCanceledException)
        {
            await CargarMedidasAsync();
            Status = "Análisis de la calidad real cancelado. Lo analizado hasta ahora queda guardado.";
        }
        catch (Exception e)
        {
            Status = "Error durante el análisis de la calidad real: " + e.Message;
            _engine.Logger.Error("Calidad: fallo al analizar el espectro", e);
        }
        finally
        {
            IsBusy = false;
            AnalizandoEspectro = false;
            _ctsEspectro = null;
            cts.Dispose();
        }
    }

    /// <summary>Para el análisis entre un archivo y el siguiente. Lo usa también el cierre de la ventana.</summary>
    [RelayCommand]
    public void CancelarEspectro() => _ctsEspectro?.Cancel();

    private void ActualizarEstado()
    {
        if (IsBusy) return;   // mientras se analiza, la línea de estado la lleva el avance
        var poor = Rows.Count(r => r.IsPoor);
        var basico = $"{Rows.Count} {Sustantivo.Archivo.De(Rows.Count)} · {poor} de baja calidad (< 192 kbps).";
        Status = _medidas.Count == 0 ? basico : basico + " " + ResumenEspectro(Rows.Count, Rows.Count(r => EspectroScanner.Analizable(r.FilePath)));
    }

    private string ResumenEspectro(int total, int analizables)
    {
        var filas = Rows.Where(r => _medidas.ContainsKey(r.FilePath)).ToList();
        return TextoResumenEspectro(
            filas.Count,
            filas.Count(r => r.Valoracion == ValoracionCalidad.Sospechosa),
            filas.Count(r => r.Valoracion == ValoracionCalidad.Dudosa),
            Math.Max(0, analizables - filas.Count),
            total - analizables);
    }

    /// <summary>
    /// Resumen del análisis del espectro, con el número bien puesto. Los que no se pudieron leer y los
    /// que este sistema no sabe decodificar se dicen aparte: no es lo mismo un archivo dañado que un
    /// FLAC en macOS.
    /// </summary>
    internal static string TextoResumenEspectro(int analizados, int sospechosos, int dudosos, int sinLeer, int noAnalizables)
    {
        var partes = new List<string>
        {
            $"Calidad real: {analizados} {(analizados == 1 ? "archivo analizado" : "archivos analizados")}",
            $"{sospechosos} {(sospechosos == 1 ? "sospechoso" : "sospechosos")}",
            $"{dudosos} {(dudosos == 1 ? "dudoso" : "dudosos")}",
        };
        if (sinLeer > 0) partes.Add(sinLeer == 1 ? "1 sin analizar o ilegible" : $"{sinLeer} sin analizar o ilegibles");
        var texto = string.Join(" · ", partes) + ".";
        if (noAnalizables > 0)
            texto += noAnalizables == 1
                ? " En este sistema solo se analizan archivos MP3, WAV y AIFF: 1 archivo de otro formato queda sin analizar."
                : $" En este sistema solo se analizan archivos MP3, WAV y AIFF: {noAnalizables} archivos de otros formatos quedan sin analizar.";
        return texto;
    }
}
