using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;
using Etiquetador.App.Views;

namespace Etiquetador.App.ViewModels;

/// <summary>Una fila de la previsualización: cambio propuesto para un archivo, con casilla Aplicar.</summary>
public partial class PreviewRow : ObservableObject, IFilaBuscable
{
    private static readonly IBrush AppliedBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A)); // verde
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xDC, 0x26, 0x26));   // rojo
    private static readonly IBrush CleanBrush = new SolidColorBrush(Color.FromArgb(0x22, 0x6B, 0x72, 0x80));   // gris (solo limpieza)

    [ObservableProperty] private bool _apply = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(StateBrush))] private string _rowStatus = "";
    [ObservableProperty] private string _new = "";      // editable antes de aplicar
    [ObservableProperty] private string _score = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(StateBrush))] private string _source = "";

    /// <summary>Color de fondo de la fila según su estado (aplicada/error/limpieza).</summary>
    public IBrush StateBrush =>
        RowStatus.StartsWith('✔') ? AppliedBrush :
        RowStatus.StartsWith('⚠') ? ErrorBrush :
        Source == "Limpieza" ? CleanBrush :
        Brushes.Transparent;
    // Tags propuestos (mismas columnas que Biblioteca)
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _bpm = "";
    [ObservableProperty] private string _remixer = "";   // quién firma la versión (informativo)

    public string Old { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Duration { get; init; } = "";   // propiedad de audio (no cambia)
    public string Quality { get; init; } = "";

    /// <summary>
    /// Por qué se propuso esto, en claro. Hasta ahora ese razonamiento solo existía en el registro:
    /// en pantalla se veía un número de confianza y poco más, y para juzgar una propuesta dudosa
    /// había que salir de la aplicación a leer un archivo de texto.
    /// </summary>
    public string Why { get; init; } = "";

    /// <summary>Cuánto cambia el archivo si se aplica, de 0 a 10 (ver Tagging.ChangeIndex).</summary>
    public double Cambio { get; init; }

    public required ProcessResult Result { get; set; }

    /// <summary>
    /// Huella de la propuesta del análisis (<see cref="EnrichViewModel.HuellaPropuesta"/>). Se fija al
    /// crear la fila y al reanalizarla, no al editar una celda: la casilla que se guarda se refiere a
    /// lo que propuso el análisis, que es lo que volverá a salir al abrir la aplicación.
    /// </summary>
    public string Huella { get; private set; } = "";

    private string? _textoBusqueda;

    /// <summary>Lo que encuentra el cuadro «Buscar…», preparado una vez y rehecho si se edita una celda.</summary>
    public string TextoBusqueda => _textoBusqueda ??= ConsultaTabla.Preparar(Old, New, Artist, Title, Album, Genre);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        _textoBusqueda = null;
        base.OnPropertyChanged(e);
    }

    public void Toggle() => Apply = !Apply;

    /// <summary>Vuelca los campos mostrables desde un ProcessResult (al crear y al reanalizar).</summary>
    public void UpdateFrom(ProcessResult r)
    {
        Result = r;
        Huella = EnrichViewModel.HuellaPropuesta(r);
        New = r.New; Score = r.Score; Source = r.Source;
        Remixer = r.Remixer.Length > 0 ? $"{r.Remixer} ({r.RemixKind})" : r.RemixKind;
        Title = r.Title; Artist = r.Artist; Album = r.Album; Genre = r.Genre; Year = r.Year; Bpm = r.Bpm;
    }

    /// <summary>Copia los valores editados en la tabla al ProcessResult antes de aplicar.</summary>
    public void SyncToResult()
    {
        Result.New = New;
        Result.Title = Title; Result.Artist = Artist; Result.Album = Album;
        Result.Genre = Genre; Result.Year = Year; Result.Bpm = Bpm;
    }
}

/// <summary>
/// Cuántas filas de Enriquecer están marcadas y cuántas de ellas renombran, al día con cada cambio:
/// filas que entran o salen, la casilla (también el doble clic) y el nombre propuesto editado.
///
/// Antes se recontaba a mano en unos pocos sitios (tras analizar y tras aplicar). El aviso de
/// rekordbox («Se van a renombrar N archivos») no salía al arrancar con 300 propuestas en caché
/// que renombraban, y tras «Desmarcar todo» seguía diciendo 300.
/// </summary>
public sealed partial class RecuentoMarcadas : ObservableObject
{
    [ObservableProperty] private int _marcadas;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HayRenombrados))] private int _renombran;

    public bool HayRenombrados => Renombran > 0;

    private readonly IReadOnlyList<PreviewRow> _filas;
    private int _lote;

    public RecuentoMarcadas(ObservableCollection<PreviewRow> filas)
    {
        _filas = filas;
        foreach (var f in filas) f.PropertyChanged += AlCambiarFila;
        filas.CollectionChanged += AlCambiarLista;
        Recontar();
    }

    /// <summary>¿Renombra el archivo esta propuesta?</summary>
    public static bool Renombra(PreviewRow r)
        => r.New.Length > 0 && !string.Equals(r.New, r.Old, StringComparison.OrdinalIgnoreCase);

    /// <summary>Marcadas y, de ellas, las que renombran.</summary>
    public static (int Marcadas, int Renombran) Contar(IEnumerable<PreviewRow> filas)
    {
        int marcadas = 0, renombran = 0;
        foreach (var r in filas)
        {
            if (!r.Apply) continue;
            marcadas++;
            if (Renombra(r)) renombran++;
        }
        return (marcadas, renombran);
    }

    public void Recontar() => (Marcadas, Renombran) = Contar(_filas);

    /// <summary>
    /// Para cambiar muchas filas de golpe («Marcar todo»): se recuenta una sola vez al terminar, no
    /// una por fila, que con cientos de propuestas recorrería la lista cientos de veces.
    /// </summary>
    public IDisposable EnLote()
    {
        _lote++;
        return new FinDeLote(this);
    }

    private sealed class FinDeLote(RecuentoMarcadas r) : IDisposable
    {
        private bool _hecho;
        public void Dispose()
        {
            if (_hecho) return;
            _hecho = true;
            if (--r._lote == 0) r.Recontar();
        }
    }

    private void AlCambiarLista(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (PreviewRow f in e.OldItems) f.PropertyChanged -= AlCambiarFila;
        if (e.NewItems != null) foreach (PreviewRow f in e.NewItems) f.PropertyChanged += AlCambiarFila;
        if (_lote > 0) return;

        // Al analizar, las filas llegan de una en una: se suman sin recorrer la lista entera.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (PreviewRow f in e.NewItems)
            {
                if (!f.Apply) continue;
                Marcadas++;
                if (Renombra(f)) Renombran++;
            }
            return;
        }
        Recontar();
    }

    private void AlCambiarFila(object? sender, PropertyChangedEventArgs e)
    {
        if (_lote > 0) return;
        // Editar «Archivo propuesto» puede hacer que una fila pase a renombrar o deje de hacerlo.
        if (e.PropertyName is nameof(PreviewRow.Apply) or nameof(PreviewRow.New)) Recontar();
    }
}

/// <summary>Pestaña Enriquecer: analiza varias carpetas, previsualiza (agrupado) y aplica/deshace.</summary>
public partial class EnrichViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    // Confianza (Score) por debajo de la cual una propuesta se auto-desmarca para revisión.
    private const double LowConfidence = 2.0;

    /// <summary>Visible solo cuando el modo de carátula es "png" (para el botón/ruta de imagen).</summary>
    public static readonly IValueConverter IsPngMode = new FuncValueConverter<string?, bool>(s => s == "png");

    /// <summary>
    /// Nombre de cada modo de carátula en el selector. El valor que se guarda en la configuración
    /// sigue siendo keep/spotify/png; antes el selector enseñaba esos códigos tal cual, y «spotify»
    /// ni siquiera es lo que hace: la carátula sale del catálogo que identificó la canción.
    /// </summary>
    public static readonly IValueConverter NombreModoCaratula = new FuncValueConverter<string?, string>(s => s switch
    {
        "keep" => "Conservar la actual",
        "spotify" => "Del catálogo",
        "png" => "Imagen propia",
        _ => s ?? "",
    });

    /// <summary>Texto del botón de pausa según el estado.</summary>
    public static readonly IValueConverter PauseLabel = new FuncValueConverter<bool, string>(p => p ? "▶ Reanudar" : "⏸ Pausar");

    public ObservableCollection<PreviewRow> Rows { get; } = new();
    public ObservableCollection<FolderItem> Folders => _engine.Library.Folders;   // compartidas (con marca)
    public DataGridCollectionView RowsView { get; }

    // Fuentes
    [ObservableProperty] private bool _useDeezer;
    [ObservableProperty] private bool _useItunes;
    [ObservableProperty] private bool _useSpotify;
    [ObservableProperty] private bool _useDiscogs;
    [ObservableProperty] private bool _useMusicBrainz;
    [ObservableProperty] private bool _useAcoustId;
    [ObservableProperty] private bool _useAi;
    // Campos a escribir
    [ObservableProperty] private bool _writeTitle;
    [ObservableProperty] private bool _writeArtist;
    [ObservableProperty] private bool _writeAlbum;
    [ObservableProperty] private bool _writeGenre;
    [ObservableProperty] private bool _writeYear;
    [ObservableProperty] private bool _writeBpm;
    // Opciones
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private bool _cleanOnly;
    [ObservableProperty] private bool _saltarYaCorrectas;
    [ObservableProperty] private string _coverMode = "keep";
    [ObservableProperty] private string _coverPath = "";

    /// <summary>
    /// Índice de cambio mínimo para listar una propuesta. Por debajo se considera cosmético y se
    /// aparta: en una biblioteca ya ordenada esas filas tapan las que sí hay que revisar.
    /// </summary>
    [ObservableProperty] private double _minChangeIndex = 3.0;

    public double[] UmbralesCambio { get; } = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

    // Cambiar el umbral rehace la lista al momento: es un filtro de lo ya analizado, no hace falta
    // volver a consultar nada.
    partial void OnMinChangeIndexChanged(double value) { if (!IsBusy) LoadCached(); }
    [ObservableProperty] private string _status = "Añade o arrastra carpetas y pulsa Analizar.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private double _progress;      // 0..100 (barra real)
    [ObservableProperty] private string _timeInfo = ""; // transcurrido + estimado restante
    [ObservableProperty] private double _stepProgress;      // 0..100: avance DENTRO de la canción actual
    [ObservableProperty] private string _stepInfo = "";     // fase en curso (Deezer, iTunes, IA…)
    [ObservableProperty] private string? _selectedFolder;
    [ObservableProperty] private PreviewRow? _selectedRow;

    /// <summary>
    /// Cuadro de búsqueda de la tabla. Filtra lo YA analizado, sin volver a consultar nada: en una
    /// lista de cientos de propuestas, dar con una canción concreta no puede ser bajar con la rueda.
    /// </summary>
    [ObservableProperty] private string _busqueda = "";

    partial void OnBusquedaChanged(string value)
    {
        // Vaciar el cuadro devuelve la lista al momento; mientras se escribe, se espera a la pausa.
        if (string.IsNullOrWhiteSpace(value)) _filtroDiferido.Ahora();
        else _filtroDiferido.Pedir();
    }

    /// <summary>La búsqueda que está aplicada a la tabla (no la que se está tecleando).</summary>
    private ConsultaTabla _consulta = ConsultaTabla.De("");
    private readonly BusquedaDiferida _filtroDiferido;

    private void AplicarFiltro()
    {
        var consulta = _consulta = ConsultaTabla.De(Busqueda);
        // Asignar el filtro ya rehace la vista: el Refresh() que venía detrás la recorría otra vez.
        RowsView.Filter = consulta.Vacia ? null : o => o is PreviewRow r && consulta.Coincide(r);
        FiltroInfo = consulta.Vacia ? "" : $"{RowsView.Count} de {Rows.Count}";
    }

    /// <summary>¿La deja ver la búsqueda aplicada?</summary>
    private bool EsVisible(PreviewRow r) => _consulta.Vacia || _consulta.Coincide(r);

    /// <summary>Cuántas quedan a la vista mientras hay búsqueda. Vacío si no se está filtrando.</summary>
    [ObservableProperty] private string _filtroInfo = "";

    private static readonly HashSet<string> OptionProps = new()
    {
        nameof(UseDeezer), nameof(UseItunes), nameof(UseSpotify), nameof(UseDiscogs), nameof(UseMusicBrainz),
        nameof(UseAcoustId), nameof(UseAi), nameof(WriteTitle), nameof(WriteArtist), nameof(WriteAlbum),
        nameof(WriteGenre), nameof(WriteYear), nameof(WriteBpm), nameof(Overwrite), nameof(CleanOnly), nameof(SaltarYaCorrectas),
        nameof(CoverMode), nameof(CoverPath), nameof(MinChangeIndex),
    };

    public EnrichViewModel(AppEngine engine) : this(engine, engine.Config, retirar => engine.ArchivosSustituidos += retirar) { }

    /// <summary>
    /// Con la configuración y el aviso de «Sustituir» por separado, para que una prueba recorra la
    /// conexión entera (el aviso de la Bandeja retira la propuesta) sin montar el motor. En las
    /// pruebas <paramref name="engine"/> es null y solo se usa lo que no lo necesita: la tabla.
    /// </summary>
    /// <param name="alSustituir">Cómo se suscribe la página al aviso de archivos sustituidos (<see cref="AppEngine.ArchivosSustituidos"/>).</param>
    internal EnrichViewModel(AppEngine? engine, AppConfig c, Action<Action<IReadOnlyCollection<string>>> alSustituir)
    {
        _engine = engine!;
        _useDeezer = c.UseDeezer; _useItunes = c.UseItunes; _useSpotify = c.UseSpotify;
        _useDiscogs = c.UseDiscogs; _useMusicBrainz = c.UseMusicBrainz; _useAcoustId = c.UseAcoustId; _useAi = c.UseAi;
        _writeTitle = c.WriteTitle; _writeArtist = c.WriteArtist; _writeAlbum = c.WriteAlbum;
        _writeGenre = c.WriteGenre; _writeYear = c.WriteYear; _writeBpm = c.WriteBpm;
        _overwrite = c.Overwrite; _cleanOnly = c.CleanOnly; _coverMode = c.CoverMode; _coverPath = c.CoverPath;
        _minChangeIndex = c.MinChangeIndex; _saltarYaCorrectas = c.SaltarYaCorrectas;

        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(PreviewRow.Folder)));
        _filtroDiferido = new BusquedaDiferida(AplicarFiltro);

        Recuento = new RecuentoMarcadas(Rows);
        Recuento.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecuentoMarcadas.Marcadas)) OnPropertyChanged(nameof(Marcadas));
            else if (e.PropertyName == nameof(RecuentoMarcadas.Renombran))
            {
                OnPropertyChanged(nameof(RenombradosPendientes));
                OnPropertyChanged(nameof(TextoRenombradosPendientes));
                OnPropertyChanged(nameof(HayRenombrados));
            }
        };
        // I14 (equipo catalogo): «Sustituir» de la Bandeja pone otro archivo en la ruta de una copia
        // de la biblioteca. Su propuesta se calculó con el archivo anterior y, con la casilla marcada,
        // «Aplicar marcadas» la escribiría en la descarga: se retira, como tras deshacer.
        alSustituir(rutas => RetirarFilasDe(rutas));
    }

    /// <summary>Quita de la lista las propuestas de esas rutas y devuelve cuántas (I14, equipo catalogo).</summary>
    public int RetirarFilasDe(IEnumerable<string> rutas)
    {
        var quitar = FilasRevertidas(Rows, rutas);
        if (quitar.Count == 0) return 0;
        using (Recuento.EnLote())
            foreach (var f in quitar) Rows.Remove(f);
        RowsView.Refresh();
        return quitar.Count;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != null && OptionProps.Contains(e.PropertyName)) PushToConfig();
    }

    /// <summary>
    /// Añade una carpeta. Puede rechazarse por solaparse con otra que ya estaba, y entonces hay que
    /// decirlo: si no, el usuario elige una carpeta, no ve ningún cambio y no sabe por qué.
    /// </summary>
    public void AddFolder(string folder)
    {
        var alta = _engine.Library.AddFolder(folder);
        if (alta.Aviso.Length > 0) Status = alta.Aviso;
    }

    [RelayCommand]
    private void RemoveFolderPath(string? folder)
    {
        if (folder == null) return;
        _engine.Library.RemoveFolder(folder);
        OlvidarAmbitoSiCaduco();
    }

    [RelayCommand]
    private void ClearFolders()
    {
        _engine.Library.ClearFolders();
        OlvidarAmbitoSiCaduco();
    }

    /// <summary>
    /// ¿Se salta sin consultar? Depende solo de su casilla, también con «Sobrescribir tags
    /// existentes»: si el nombre y los tags ya dicen la misma canción, sobrescribir solo cambiaría
    /// género, año o álbum por lo que diga un catálogo, y eso no es arreglar nada. Quien lo quiera
    /// igualmente desmarca la casilla.
    /// </summary>
    private bool SeSalta(Track t, FieldFlags campos)
        => SaltarYaCorrectas && YaCorrecta.SeSalta(t, campos);

    private void PushToConfig()
    {
        var c = _engine.Config;
        c.UseDeezer = UseDeezer; c.UseItunes = UseItunes; c.UseSpotify = UseSpotify;
        c.UseDiscogs = UseDiscogs; c.UseMusicBrainz = UseMusicBrainz; c.UseAcoustId = UseAcoustId; c.UseAi = UseAi;
        c.WriteTitle = WriteTitle; c.WriteArtist = WriteArtist; c.WriteAlbum = WriteAlbum;
        c.WriteGenre = WriteGenre; c.WriteYear = WriteYear; c.WriteBpm = WriteBpm;
        c.Overwrite = Overwrite; c.CleanOnly = CleanOnly; c.CoverMode = CoverMode; c.CoverPath = CoverPath;
        c.MinChangeIndex = MinChangeIndex; c.SaltarYaCorrectas = SaltarYaCorrectas;
        _engine.SaveConfig();   // las carpetas las persiste el LibraryStore
    }

    /// <summary>Analiza usando la caché de resultados (rápido; solo reprocesa lo nuevo/cambiado).</summary>
    [RelayCommand]
    public Task AnalyzeAsync() => RunAnalyzeAsync(force: false);

    /// <summary>Reanaliza TODO ignorando la caché (cuando el usuario lo requiere).</summary>
    [RelayCommand]
    private Task ReanalyzeAllAsync() => RunAnalyzeAsync(force: true);

    /// <param name="marcar">
    /// F1: carpeta desmarcada que el usuario ha aceptado marcar para analizarla. Se marca con la
    /// pestaña ya ocupada: marcarla antes deja la biblioteca «sin escanear», y eso bloquea un momento
    /// el resto de pestañas y lleva a Biblioteca.
    /// </param>
    private async Task RunAnalyzeAsync(bool force, FolderItem? marcar = null)
    {
        if (_engine.Library.Folders.Count == 0) { Status = "Añade al menos una carpeta."; return; }
        if (IsBusy) return;
        // F1: si la carpeta del ámbito se ha quitado o desmarcado, no se analiza «todo» por sorpresa
        // (una pasada completa puede durar horas): se vuelve a la vista de todas y se dice.
        if (marcar == null && Ambito is { } caducado && !AmbitoVigente(Folders, caducado))
        {
            Ambito = null;
            LoadCached();
            Status = $"«{NombreAmbito(caducado)}» ya no está entre las carpetas marcadas: se muestran de nuevo todas. Pulsa «Analizar» para analizarlas.";
            return;
        }
        PushToConfig();
        IsBusy = true;
        if (marcar != null) marcar.Enabled = true;   // la biblioteca pide escanear; se escanea justo debajo
        IsPaused = false;
        Progress = 0; TimeInfo = "";
        StepProgress = 0; StepInfo = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var sw = Stopwatch.StartNew();

        // Spotify y la IA local, si se desactivaron solos en un análisis anterior, vuelven a probarse.
        TiradaAnalisis.Empezar(_engine.Spotify, _engine.Ai, UseAi);
        AvisoIa = "";

        // Barra secundaria: avance dentro de la canción en curso. Progress<T> marshalea al hilo de UI.
        _engine.StepProgress = new Progress<(string Phase, double Fraction)>(p =>
        {
            StepInfo = p.Phase;
            StepProgress = p.Fraction * 100;
        });

        // Fuera del try a propósito: si la tirada se cancela, el catch necesita lo acumulado para
        // poder escribir igualmente el resumen.
        var seenResults = new List<ProcessResult>();

        try
        {
            if (!_engine.Library.IsScanned) { Status = "Escaneando biblioteca…"; await _engine.Library.ScanAsync(); }
            Rows.Clear();
            var opts = _engine.BuildOptions();
            var sig = opts.Signature();
            // Biblioteca compartida (sin re-escanear), o solo la carpeta elegida (F1).
            var ambito = Ambito;
            var tracks = PistasDelAmbito(_engine.Library.Tracks, ambito);
            var donde = ambito == null ? "" : $" de «{NombreAmbito(ambito)}»";
            if (ambito != null) AmbitoTexto = TextoAmbito(ambito, tracks.Count);
            _engine.Logger.Head($"{(force ? "REANÁLISIS COMPLETO" : "Análisis")} de {tracks.Count} canciones{(ambito == null ? "" : $" de {ambito.Carpeta} (solo esa carpeta)")}"
                              + (_engine.Ignored.Count > 0 ? $" ({_engine.Ignored.Count} descartadas se omiten)" : ""));
            _engine.Logger.Detail($"Firma de opciones: {sig}");
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            int i = 0, fromCache = 0, alreadyApplied = 0, sinCambio = 0, yaCorrectas = 0;
            var campos = _engine.BuildFields();
            foreach (var t in tracks)
            {
                ct.ThrowIfCancellationRequested();
                while (IsPaused) { ct.ThrowIfCancellationRequested(); await Task.Delay(150, ct); }
                i++;
                seen.Add(t.FilePath);
                if (_engine.Ignored.Contains(t.FilePath)) continue;   // descartada por el usuario
                // Ya aplicada: no se vuelve a proponer, tampoco al reanalizar. Son dos cosas distintas:
                // "Reanalizar todo" sirve para ignorar la CACHÉ y recalcular, no para volver a ofrecer
                // canciones que el usuario ya dio por terminadas. Para recuperarlas está
                // "Olvidar aplicadas", en Ajustes, que es una decisión explícita.
                if (_engine.Applied.Contains(t.FilePath)) { alreadyApplied++; continue; }
                // Nombre y tags ya dicen lo mismo y no le falta nada de lo que se escribe: consultarla
                // solo gastaría tiempo y peticiones. También al reanalizar, igual que las aplicadas.
                if (SeSalta(t, campos)) { yaCorrectas++; continue; }
                Status = $"{(force ? "Reanalizando" : "Analizando")} {i}/{tracks.Count}…  {t.FileName}";
                Progress = tracks.Count == 0 ? 0 : (double)i / tracks.Count * 100;
                var per = sw.Elapsed.TotalSeconds / i;
                TimeInfo = $"⏱ {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)} · resto ~{TextUtils.FormatEta(per * (tracks.Count - i))}";

                StepProgress = 0; StepInfo = "";   // la barra secundaria arranca de cero en cada canción

                var cached = force ? null : _engine.Analysis.Get(t.FilePath, sig);
                ProcessResult r;
                if (cached != null) { r = cached; fromCache++; StepProgress = 100; StepInfo = "de caché"; }
                else
                {
                    try { r = await Task.Run(() => _engine.AnalyzeCachedAsync(t.FilePath, opts, sig, force, ct), ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { continue; }
                    // Restos de la tanda 2: una pasada completa puede durar horas; cada ~200
                    // canciones consultadas o 5 minutos se guarda lo hecho, y un cierre a la fuerza
                    // ya no tira la tirada entera. Las que salen de la caché no cuentan: no hay nada
                    // nuevo que guardar.
                    await _engine.PuntoDeControlAsync();
                }
                seenResults.Add(r);
                VigilarIa();
                if (r.Skip) continue;
                if (r.Found || r.CleanOnly) { if (!AddRow(r, t)) sinCambio++; }
            }
            // Solo se poda dentro de las carpetas que esta pasada ha recorrido de verdad, las que han
            // aportado canciones. Visto en una biblioteca real: con 42 de 43 carpetas desmarcadas,
            // analizar 59 canciones dejó analysis-cache.json en 57.730 bytes (antes, ~6 MB). Una
            // carpeta sin canciones -un disco desconectado- tampoco se poda. Con una subcarpeta (F1)
            // la raíz es la subcarpeta, no la carpeta de la lista: si no, se borraría el resto.
            var raices = RaicesAPodar(tracks, ambito);
            _engine.Analysis.Prune(seen, raices);
            _engine.Analysis.Save();
            PodarMarcas(_engine.Marks, HuellasVigentes(seenResults));
            _engine.Marks.Save();
            RowsView.Refresh();
            Progress = 100;
            Status = $"Analizadas {tracks.Count}{donde} · propuestas {Rows.Count} · {fromCache} de caché"
                   + (alreadyApplied > 0 ? $" · {alreadyApplied} ya aplicadas (omitidas)" : "")
                   + (yaCorrectas > 0 ? $" · {yaCorrectas} saltadas (nombre y tags ya coinciden)" : "")
                   + (sinCambio > 0 ? $" · {sinCambio} ya estaban bien" : "")
                   + $" · en {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}."
                   + (AvisoIa.Length > 0 ? "  ⚠ " + AvisoIa : "");
            var low = Rows.Count(r => r.RowStatus.StartsWith('⚠'));
            if (alreadyApplied > 0)
                _engine.Logger.Detail($"    {alreadyApplied} ya aplicadas, omitidas (se recuperan con «Olvidar aplicadas» en Ajustes).");
            if (yaCorrectas > 0)
                _engine.Logger.Detail($"    {yaCorrectas} saltadas sin consultar: nombre y tags ya coinciden y no les falta nada de lo que se escribe.");
            if (sinCambio > 0)
                _engine.Logger.Detail($"    {sinCambio} no se muestran porque aplicarlas no cambiaría nada del archivo.");
            _engine.Logger.Sum($"Análisis terminado: {tracks.Count} revisadas · {Rows.Count} propuestas · {fromCache} de caché · "
                             + $"{low} de baja confianza · {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}");
            _engine.Logger.Detail($"Caché de red: {_engine.Api.CacheHits} aciertos / {_engine.Api.CacheMiss} peticiones nuevas");
            WriteAnalysisReport(seenResults);
            AnalysisCompleted?.Invoke();   // las no encontradas van solas a su pestaña
        }
        catch (OperationCanceledException)
        {
            _engine.Analysis.Save(); RowsView.Refresh();
            PodarMarcas(_engine.Marks, HuellasVigentes(seenResults));   // de lo que sí se llegó a ver
            _engine.Marks.Save();
            Status = $"Análisis cancelado ({Rows.Count} propuestas hasta ahora)."
                   + (AvisoIa.Length > 0 ? "  ⚠ " + AvisoIa : "");
            _engine.Logger.Err($"Análisis cancelado por el usuario ({Rows.Count} propuestas hasta ahora).");
            // El resumen también se escribe al cancelar: es lo que hace comparable una tirada con
            // otra, y una pasada larga que se corta a medias no debe dejar el registro sin cifras.
            _engine.Logger.Sum("(tirada CANCELADA: las cifras siguientes son parciales)");
            WriteAnalysisReport(seenResults);
        }
        finally
        {
            _engine.StepProgress = null;   // deja de reportar al salir
            StepProgress = 0; StepInfo = "";
            IsBusy = false; _cts.Dispose(); _cts = null;
        }
    }

    /// <summary>
    /// Crea la fila de previsualización. Devuelve false si la propuesta no cambiaría NADA del
    /// archivo, en cuyo caso no se añade: en una biblioteca ya ordenada esas filas son la mayoría y
    /// solo entorpecen la revisión de lo que sí hay que mirar.
    /// </summary>
    private bool AddRow(ProcessResult r, Track t)
    {
        var indice = Tagging.ChangeIndex(r, t, _engine.Config.Overwrite, _engine.BuildFields());
        if (indice < MinChangeIndex) return false;

        var row = new PreviewRow { Result = r, Old = r.Old, Folder = t.Folder, Duration = t.Duration, Quality = t.Quality, Cambio = indice, Why = r.Why };
        row.UpdateFrom(r);
        var (apply, estado) = EstadoInicial(r);
        row.Apply = apply;
        if (estado.Length > 0) row.RowStatus = estado;

        // Si el usuario ya decidió sobre ESTA propuesta, manda su decisión sobre el valor por
        // defecto: revisar cientos de propuestas lleva su rato y cerrar la aplicación no puede tirarlo.
        var decidida = DecisionGuardada(_engine.Marks, r.FilePath, row.Huella);
        if (decidida is bool v) row.Apply = v;

        // A partir de aquí, cualquier cambio de la casilla es cosa del usuario (la casilla de la
        // tabla escribe directamente en la propiedad, sin pasar por ningún comando).
        row.PropertyChanged += RowChanged;
        Rows.Add(row);
        return true;
    }

    /// <summary>
    /// Casilla y estado con los que nace una propuesta, antes de mirar lo que decidió el usuario: lo
    /// que hay que revisar (fuente de rescate, nombre con huellas de una regla fallida) y lo de baja
    /// confianza salen desmarcados y con el motivo.
    /// </summary>
    public static (bool Apply, string Estado) EstadoInicial(ProcessResult r)
    {
        if (r.Revisar.Length > 0) return (false, "⚠ revisar: " + r.Revisar);
        if (double.TryParse(r.Score, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sc)
            && sc < LowConfidence)
            return (false, "⚠ baja confianza — revisar");
        return (true, "");
    }

    // --- Casillas guardadas (E13) ---
    //
    // Antes la casilla se guardaba solo por ruta y sobrevivía a cambios de propuesta: tras «Marcar
    // todo», una propuesta distinta para esa canción semanas después (otra coincidencia, confianza
    // 0,8, fondo rojo) salía ya marcada y «Aplicar» la escribía. Ahora la decisión se guarda junto con
    // la propuesta a la que se refería, y si la propuesta cambia, decide otra vez el análisis.

    /// <summary>
    /// Huella corta de una propuesta: el nombre propuesto, artista y título, la fuente y la
    /// confianza. Es lo que identifica la coincidencia; si cambia cualquiera de esas cosas, la
    /// propuesta ya es otra y la casilla que el usuario puso en la anterior no vale para ella.
    /// </summary>
    public static string HuellaPropuesta(ProcessResult r)
    {
        var texto = string.Join("\u001F", r.New, r.Artist, r.Title, r.Source, r.Score);
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(texto));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Clave con la que se guarda la casilla: la ruta y la huella de la propuesta.</summary>
    public static string ClaveMarca(string ruta, string huella) => ruta + "\u001F" + huella;

    /// <summary>
    /// Lo que el usuario decidió para ESTA propuesta, o null si no decidió nada o decidió sobre otra.
    ///
    /// Las casillas de versiones anteriores se guardaron solo por ruta y no dicen a qué propuesta se
    /// referían. Un «no» se respeta (desmarcar nunca escribe nada) y pasa a la clave nueva; un «sí»
    /// se descarta y decide el análisis, porque es justo lo que podía aprobar de antemano una
    /// propuesta distinta. En una propuesta que el análisis ya marca, eso no cambia nada.
    /// </summary>
    public static bool? DecisionGuardada(ApplyMarks marcas, string ruta, string huella)
    {
        if (marcas.Get(ClaveMarca(ruta, huella)) is bool propia) return propia;
        if (marcas.Get(ruta) is not bool antigua) return null;
        marcas.Forget(ruta);
        if (antigua) return null;
        marcas.Set(ClaveMarca(ruta, huella), false);
        return false;
    }

    /// <summary>Guarda la casilla que el usuario ha puesto en esta propuesta.</summary>
    public static void GuardarDecision(ApplyMarks marcas, string ruta, string huella, bool aplicar)
        => marcas.Set(ClaveMarca(ruta, huella), aplicar);

    /// <summary>Olvida la decisión sobre esta propuesta (y la antigua por ruta, si quedaba).</summary>
    public static void OlvidarDecision(ApplyMarks marcas, string ruta, string huella)
    {
        marcas.Forget(ClaveMarca(ruta, huella));
        marcas.Forget(ruta);
    }

    /// <summary>
    /// La huella de la propuesta vigente de cada canción vista en una pasada (analizar o cargar de la
    /// caché). Las que no tienen propuesta no entran: de esas no se sabe qué casilla sobra.
    /// </summary>
    public static Dictionary<string, string> HuellasVigentes(IEnumerable<ProcessResult> vistas)
    {
        var vigentes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in vistas)
            if (!r.Skip && (r.Found || r.CleanOnly) && r.FilePath.Length > 0)
                vigentes[r.FilePath] = HuellaPropuesta(r);
        return vigentes;
    }

    /// <summary>
    /// Olvida las casillas de propuestas que ya no son la vigente: misma ruta, otra huella. Devuelve
    /// cuántas.
    ///
    /// Visto en revisión: con la huella en la clave, cada reanálisis que daba otra propuesta dejaba
    /// la clave de la anterior para siempre, y «Marcar todo» o «Desmarcar todo» escriben una por fila.
    /// Antes había una sola clave por ruta; marcas-aplicar.json (84 KB en el equipo del usuario)
    /// crecía con cada «Reanalizar todo». Esa casilla ya no se puede usar: la propuesta a la que se
    /// refería no es la que hay. Las de canciones que no se han visto en esta pasada (carpetas
    /// desmarcadas, un disco desconectado) y las antiguas guardadas solo por ruta (las migra
    /// <see cref="DecisionGuardada"/>) se quedan.
    /// </summary>
    public static int PodarMarcas(ApplyMarks marcas, IReadOnlyDictionary<string, string> huellaVigente)
    {
        if (huellaVigente.Count == 0) return 0;
        return marcas.OlvidarSi(clave =>
        {
            var corte = clave.LastIndexOf('\u001F');
            return corte > 0
                && huellaVigente.TryGetValue(clave[..corte], out var vigente)
                && !string.Equals(clave[(corte + 1)..], vigente, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewRow.Apply) || sender is not PreviewRow row) return;
        GuardarDecision(_engine.Marks, row.Result.FilePath, row.Huella, row.Apply);
    }

    /// <summary>Pone la casilla sin anotarla como decisión del usuario (es el valor por defecto).</summary>
    private void PonerSinAnotar(PreviewRow row, bool aplicar)
    {
        row.PropertyChanged -= RowChanged;
        row.Apply = aplicar;
        row.PropertyChanged += RowChanged;
    }

    /// <summary>Al iniciar: rellena la previsualización SOLO con lo que ya hay en la caché (sin red).</summary>
    public void LoadCached()
    {
        if (IsBusy) return;
        if (_engine.Library.Tracks.Count == 0) return;
        // Con un ámbito (F1), solo lo de esa carpeta; si la carpeta ya no cuenta, otra vez todas.
        if (Ambito != null && !AmbitoVigente(Folders, Ambito)) Ambito = null;
        var tracks = PistasDelAmbito(_engine.Library.Tracks, Ambito);
        if (Ambito != null) AmbitoTexto = TextoAmbito(Ambito, tracks.Count);
        var sig = _engine.BuildOptions().Signature();
        Rows.Clear();
        int loaded = 0;
        var campos = _engine.BuildFields();
        var vistas = new List<ProcessResult>();
        foreach (var t in tracks)
        {
            if (_engine.Ignored.Contains(t.FilePath)) continue;   // descartada por el usuario
            if (_engine.Applied.Contains(t.FilePath)) continue;   // ya aplicada
            // Lo analizado antes de existir esta regla sigue en la caché: sin esto, reaparecería.
            if (SeSalta(t, campos)) continue;
            var c = _engine.Analysis.Get(t.FilePath, sig);
            if (c == null || c.Skip) continue;
            vistas.Add(c);
            if (c.Found || c.CleanOnly) { if (AddRow(c, t)) loaded++; }
        }
        PodarMarcas(_engine.Marks, HuellasVigentes(vistas));
        RowsView.Refresh();
        if (loaded > 0) Status = $"{loaded} propuestas cargadas de la caché. Pulsa Analizar para completar el resto.";
    }

    [RelayCommand]
    private void Pause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void Cancel() { IsPaused = false; _cts?.Cancel(); }

    /// <summary>
    /// Cuántas propuestas marcadas van a RENOMBRAR el archivo. Importa avisarlo: rekordbox guarda
    /// los cue points y el beatgrid en su base de datos ligados a la ruta, así que al renombrar los
    /// da por perdidos. Se puede reparar después (página rekordbox, pestaña «Rutas rotas»), pero
    /// más vale saberlo antes que descubrirlo en cabina.
    /// </summary>
    public int RenombradosPendientes => Recuento.Renombran;

    /// <summary>La banda que lo avisa, con el número bien puesto (antes decía «Se van a renombrar 1 archivos»).</summary>
    public string TextoRenombradosPendientes => TextoRenombrados(RenombradosPendientes);

    internal static string TextoRenombrados(int n)
        => (n == 1 ? "Se va a renombrar 1 archivo." : $"Se van a renombrar {n} archivos.")
         + " Si usas rekordbox, al cambiar el nombre pierde sus cue points y su beatgrid, porque los guarda ligados a la ruta. "
         + $"Puedes devolvérselos después {RekordboxViewModel.DondeSeRepara}.";

    /// <summary>
    /// Cuántas filas están marcadas: es lo que aplica el botón, y lo dice en su texto. Antes ponía
    /// «Aplicar seleccionados» al lado de la insignia «3 seleccionadas» de la selección de la tabla,
    /// y aplicaba las ~640 MARCADAS, no las 3 seleccionadas.
    /// </summary>
    public int Marcadas => Recuento.Marcadas;

    /// <summary>Lleva al día marcadas y renombrados con cada cambio de la lista (ver <see cref="RecuentoMarcadas"/>).</summary>
    public RecuentoMarcadas Recuento { get; }

    /// <summary>
    /// Por qué el análisis en curso (o el último) sigue sin IA local, o "" si no se ha desactivado.
    /// Se enseña encima de la tabla mientras dure, no solo en el registro.
    /// </summary>
    [ObservableProperty] private string _avisoIa = "";

    /// <summary>Mira, canción a canción, si la IA local se acaba de desactivar sola, y lo dice una vez.</summary>
    private void VigilarIa()
    {
        if (AvisoIa.Length > 0) return;
        var aviso = TiradaAnalisis.AvisoIa(_engine.Ai, UseAi);
        if (aviso.Length == 0) return;
        AvisoIa = aviso;
        _engine.Logger.Err(aviso);
    }

    public bool HayRenombrados => Recuento.HayRenombrados;

    /// <summary>¿Renombra el archivo esta propuesta?</summary>
    public static bool Renombra(PreviewRow r) => RecuentoMarcadas.Renombra(r);

    // --- Aplicar las marcadas (E3) ---

    /// <summary>
    /// Qué aplicaría «Aplicar marcadas»: cuántas, cuántas renombran y cuántas tapa la búsqueda.
    /// </summary>
    public sealed record PlanAplicar(int Marcadas, int Renombran, int Visibles, int VisiblesRenombran, string Busqueda)
    {
        public int Ocultas => Marcadas - Visibles;
    }

    public static PlanAplicar Planificar(IEnumerable<PreviewRow> filas, Func<PreviewRow, bool> visible, string busqueda)
    {
        int marcadas = 0, renombran = 0, visibles = 0, visiblesRenombran = 0;
        foreach (var r in filas)
        {
            if (!r.Apply) continue;
            var renombra = Renombra(r);
            marcadas++;
            if (renombra) renombran++;
            if (!visible(r)) continue;
            visibles++;
            if (renombra) visiblesRenombran++;
        }
        return new PlanAplicar(marcadas, renombran, visibles, visiblesRenombran, busqueda.Trim());
    }

    /// <summary>El plan con lo que hay ahora en la tabla y en el cuadro de búsqueda.</summary>
    public PlanAplicar PlanDeAplicacion()
    {
        _filtroDiferido.AplicarSiPendiente();   // «visibles» tiene que ser lo que dice el cuadro
        return Planificar(Rows, EsVisible, Busqueda);
    }

    /// <summary>Por qué no hay nada que confirmar, o "" si lo hay.</summary>
    public string MotivoParaNoAplicar(PlanAplicar p)
        => Rows.Count == 0 ? "No hay nada que aplicar. Analiza primero."
         : p.Marcadas == 0 ? "No hay ninguna canción marcada."
         : "";

    /// <summary>Textos del diálogo de confirmación.</summary>
    /// <param name="Alternativa">Segundo botón ("" si no hay): con búsqueda activa, aplicar todas las marcadas.</param>
    /// <param name="AceptarSoloVisibles">El botón principal aplica solo las que deja ver la búsqueda.</param>
    public sealed record ConfirmacionAplicar(string Titulo, string Cuerpo, string Nota, string Aceptar,
                                             string Alternativa, bool AceptarSoloVisibles);

    /// <summary>
    /// Lo que se le pregunta al usuario antes de aplicar. Visto en un registro real: 728 propuestas
    /// («15184 revisadas · 728 propuestas»), unas 640 marcadas; se busca «bunny», se ven 3, y
    /// aplicar sin preguntar renombraba las ~640. Con búsqueda activa, lo que se ofrece primero son
    /// las que se ven: es lo que espera quien acaba de buscar, y es lo menos que se puede tocar.
    /// </summary>
    public static ConfirmacionAplicar Confirmacion(PlanAplicar p)
    {
        static string Canciones(int n) => n == 1 ? "1 canción" : $"{n} canciones";
        static string Archivos(int n) => n == 1 ? "1 archivo" : $"{n} archivos";
        static string QueHace(int n, int renombran)
            => $"se escribirán las etiquetas de {Canciones(n)}"
             + (renombran > 0 ? $" y se renombrarán {Archivos(renombran)}." : "; ningún archivo cambia de nombre.");
        static string Frase(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

        var nota = "Se puede deshacer con «Deshacer última» o, más adelante, desde «Historial de cambios»."
                 + (p.Renombran > 0
                     ? $" Si usas rekordbox, los archivos renombrados pierden allí sus cue points y su beatgrid hasta que repares la colección {RekordboxViewModel.DondeSeRepara}."
                     : "");

        if (p.Ocultas == 0)
            return new ConfirmacionAplicar($"Aplicar {Canciones(p.Marcadas)} {(p.Marcadas == 1 ? "marcada" : "marcadas")}",
                                           Frase(QueHace(p.Marcadas, p.Renombran)), nota, $"Aplicar ({p.Marcadas})", "", false);

        if (p.Visibles == 0)
            return new ConfirmacionAplicar("Las canciones marcadas están ocultas por la búsqueda",
                $"La búsqueda «{p.Busqueda}» no deja ver ninguna de las {Canciones(p.Marcadas)} marcadas. "
                + "Si continúas, se aplicarán todas aunque no las estés viendo: " + QueHace(p.Marcadas, p.Renombran),
                nota, $"Aplicar las {p.Marcadas} marcadas", "", false);

        return new ConfirmacionAplicar($"Aplicar con la búsqueda «{p.Busqueda}» activa",
            $"Hay {Canciones(p.Marcadas)} marcadas, pero la búsqueda solo deja ver {p.Visibles}; "
            + $"{(p.Ocultas == 1 ? "la otra está oculta" : $"las otras {p.Ocultas} están ocultas")} y también se aplicaría"
            + $"{(p.Ocultas == 1 ? "" : "n")} si eliges todas.\n\n"
            + $"Solo las visibles: {QueHace(p.Visibles, p.VisiblesRenombran)}\n"
            + $"Todas las marcadas: {QueHace(p.Marcadas, p.Renombran)}",
            nota, $"Solo las visibles ({p.Visibles})", $"Todas las marcadas ({p.Marcadas})", true);
    }

    /// <summary>Las filas que se aplican: las marcadas, y con <paramref name="soloVisibles"/> solo las que se ven.</summary>
    public static List<PreviewRow> FilasAAplicar(IEnumerable<PreviewRow> filas, Func<PreviewRow, bool> visible, bool soloVisibles)
        => filas.Where(r => r.Apply && (!soloVisibles || visible(r))).ToList();

    /// <summary>Aplica las marcadas, ya confirmadas en la vista (ConfirmDialog, como el resto de la aplicación).</summary>
    public Task AplicarMarcadasAsync(bool soloVisibles)
    {
        var filas = FilasAAplicar(Rows, EsVisible, soloVisibles);
        if (filas.Count == 0) { Status = "No hay ninguna canción marcada."; return Task.CompletedTask; }
        return ApplyRowsAsync(filas);
    }

    /// <summary>Aplica SOLO la canción seleccionada (menú contextual), esté marcada o no.</summary>
    [RelayCommand]
    private Task ApplySelectedAsync()
    {
        var row = SelectedRow;
        if (row == null) { Status = "Selecciona antes una canción."; return Task.CompletedTask; }
        return ApplyRowsAsync(new List<PreviewRow> { row });
    }

    private async Task ApplyRowsAsync(List<PreviewRow> toApply)
    {
        if (IsBusy) return;
        _engine.ReleaseAudio();   // si suena la canción, el archivo está abierto y fallaría al escribir
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var fields = _engine.BuildFields();
            var undoFile = _engine.Paths.NuevoManifiesto();   // dos tiradas en el mismo segundo no se mezclan
            var doneLog = _engine.Paths.DoneLog;

            // Portada PNG local: se carga una vez para toda la tirada.
            TagLib.Picture? png = null;
            if (CoverMode == "png")
            {
                if (!string.IsNullOrWhiteSpace(CoverPath) && File.Exists(CoverPath))
                {
                    try { png = new TagLib.Picture(CoverPath) { Type = TagLib.PictureType.FrontCover }; }
                    catch (Exception e) { Status = "No se pudo cargar la imagen de portada: " + e.Message; IsBusy = false; return; }
                }
                else { Status = "Elige una imagen PNG/JPG válida para la portada."; IsBusy = false; return; }
            }

            int applied = 0, marked = toApply.Count, done = 0;
            int sinHistorial = 0;   // cambios que ya están en disco pero no se pudieron anotar
            _engine.Logger.Head($"Aplicando {marked} canción(es) · manifiesto {Path.GetFileName(undoFile)}");
            Progress = 0; TimeInfo = "";
            var sw = Stopwatch.StartNew();
            var cancelled = false;
            var appliedRows = new List<PreviewRow>();   // filas aplicadas con éxito: se quitan de la lista al terminar
            var tocadas = new List<string>();           // origen y destino de cada una: lo que se relee en la biblioteca
            foreach (var row in toApply)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                done++;
                Progress = marked == 0 ? 0 : (double)done / marked * 100;
                Status = $"Aplicando {done}/{marked}…  {row.Old}";
                var origen = row.Result.FilePath;
                tocadas.Add(origen);
                try
                {
                    row.SyncToResult();   // respeta lo editado en la tabla (nombre y tags)
                    var res = await Task.Run(() => _engine.AplicarUnoAsync(row.Result, Overwrite, fields, CoverMode, png, undoFile, doneLog, ct, aplicada: true), ct);
                    tocadas.Add(res.FinalPath);
                    row.RowStatus = res is { TagOk: true, RenOk: true }
                        ? (res.UndoErr.Length > 0 ? "✔ aplicado (sin marcha atrás)" : "✔ aplicado")
                        : $"⚠ {res.TagErr}{res.RenErr}";
                    if (res.UndoErr.Length > 0)
                    {
                        // El archivo ya está cambiado y no ha quedado anotado: hay que decirlo.
                        sinHistorial++;
                        _engine.Logger.Err($"'{row.Old}' se aplicó pero NO se pudo anotar para deshacer: {res.UndoErr}");
                    }
                    if (res.TagOk && res.RenOk)
                    {
                        applied++; appliedRows.Add(row);
                        // Queda marcada como aplicada: ni "Analizar" ni "Reanalizar todo" volverán a
                        // proponerla. Se recupera con "Olvidar aplicadas" o deshaciendo la tirada.
                        _engine.Applied.Add(res.FinalPath);
                        OlvidarDecision(_engine.Marks, row.Result.FilePath, row.Huella);   // ya aplicada: su casilla deja de tener sentido
                        _engine.Logger.Detail($"    OK  '{row.Old}' -> '{row.New}'");
                    }
                    else
                        _engine.Logger.Err($"No se pudo aplicar '{row.Old}': {res.TagErr}{res.RenErr}");
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (Exception e)
                {
                    row.RowStatus = "⚠ " + e.Message;
                    _engine.Logger.Error($"Error al aplicar '{row.Old}'", e);
                }
                // Restos de la tanda 2: aplicar cientos de canciones lleva su rato; lo dado por
                // aplicado se guarda cada ~200 canciones o 5 minutos, no solo al terminar.
                await _engine.PuntoDeControlAsync();
            }
            // Las canciones ya aplicadas salen de la lista de Enriquecer (las que fallaron se quedan con su aviso).
            if (appliedRows.Count > 0)
            {
                foreach (var r in appliedRows) Rows.Remove(r);
                RowsView.Refresh();
            }
            _engine.Applied.Save();
            _engine.Marks.Save();
            _engine.Logger.Sum($"Aplicación terminada: {applied} de {marked} correctas"
                             + (applied < marked ? $" · {marked - applied} con problemas" : "")
                             + (cancelled ? " (cancelada)" : ""));
            // M12: se releen solo los archivos tocados (de cada renombrado, el nombre de antes y el de
            // después) en lugar de recorrer las 15-20k canciones de todas las carpetas.
            if (tocadas.Count > 0) await _engine.Library.RefrescarAsync(tocadas);
            Status = cancelled
                ? $"Cancelado: {applied} aplicados antes de parar. Manifiesto: {Path.GetFileName(undoFile)}"
                : marked == 1
                    ? (applied == 1
                        ? $"Aplicada «{toApply[0].Old}». Manifiesto: {Path.GetFileName(undoFile)}"
                        : $"No se pudo aplicar «{toApply[0].Old}»: {toApply[0].RowStatus}")
                    : $"Aplicados {applied} de {marked}. Biblioteca actualizada. Manifiesto: {Path.GetFileName(undoFile)}";
            if (sinHistorial > 0)
                Status += $"  ⚠ {sinHistorial} no se pudieron anotar en el historial: esos cambios NO se pueden deshacer desde la aplicación.";
        }
        finally { IsBusy = false; _cts.Dispose(); _cts = null; }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (IsBusy) return;
        _engine.ReleaseAudio();   // deshacer también renombra: el archivo no puede estar sonando
        IsBusy = true;
        Status = "Deshaciendo la última ejecución…";
        _engine.Logger.Head("Deshaciendo la última ejecución…");
        UndoResult res;
        var vuelven = 0;
        try
        {
            res = await Task.Run(_engine.DeshacerUltima);   // con las cachés de audio al día (datos-3)
            vuelven = await AsentarDeshacerAsync(res);
        }
        finally { IsBusy = false; }

        Status = ResumenDeshacer(res, vuelven, RetirarRevertidas(res),
                                 nada: "No hay ninguna ejecución que deshacer.",
                                 reintento: "«Deshacer última» reintentará solo lo pendiente.");
    }

    /// <summary>
    /// Lo que se hace tras deshacer, venga de «Deshacer última» o del historial (E14): olvidar como
    /// aplicado lo revertido y releer en la biblioteca lo tocado. Devuelve cuántas canciones vuelven
    /// a estar pendientes.
    /// </summary>
    private async Task<int> AsentarDeshacerAsync(UndoResult res)
    {
        if (res.None) return 0;
        _engine.Logger.Sum($"Deshacer: {res.Reverted} revertidos · {res.Manual} conservados · {res.Missing} ausentes · {res.Errors} errores.");
        // Solo lo que se había dado por aplicado: deshacer un ajuste de volumen, Unificar géneros
        // o la importación de rekordbox no devuelve a pendientes nada de Enriquecer.
        var vuelven = OlvidarRevertidas(_engine.Applied, _engine.Marks, res.AplicadasRevertidas);
        // Lo que una ejecución posterior dio por aplicado sigue al archivo que vuelve a su nombre
        // anterior, como su registro (datos-1). Va después: lo que esta ejecución dio por aplicado
        // ya se ha olvidado arriba.
        var siguen = SeguirAplicadas(_engine.Applied, res.Vueltas);
        if (vuelven + siguen > 0)
        {
            _engine.Applied.Save();
            _engine.Marks.Save();
            if (vuelven > 0) _engine.Logger.Detail($"    {vuelven} canciones revertidas dejan de contar como aplicadas.");
        }
        // M12: refleja el estado revertido releyendo solo esos archivos (las dos rutas de cada
        // registro), no las 15-20k canciones de todas las carpetas.
        if (res.Tocadas.Count > 0) await _engine.Library.RefrescarAsync(res.Tocadas);
        return vuelven;
    }

    /// <summary>
    /// Quita de la lista las propuestas de los archivos revertidos y devuelve cuántas.
    ///
    /// Salen SOLO las de los archivos cuyo nombre o etiquetas acaba de devolver el deshacer (no las
    /// de un deshacer de volumen): se calcularon sobre etiquetas o nombres que ya no son los que hay.
    /// Antes se rehacía la lista entera desde la caché, que da lo mismo para esas (deshacer cambia la
    /// fecha del archivo y su caché deja de valer) pero tiraba lo editado en las celdas y los avisos
    /// «⚠» de todas las demás, y miraba en disco las 15-20k pistas desde el hilo de la interfaz.
    /// </summary>
    private int RetirarRevertidas(UndoResult res)
    {
        if (res.None) return 0;
        var quitar = FilasRevertidas(Rows, res.Revertidas);
        if (quitar.Count == 0) return 0;
        using (Recuento.EnLote())
            foreach (var f in quitar) Rows.Remove(f);
        RowsView.Refresh();
        return quitar.Count;
    }

    /// <summary>Lo que se dice tras deshacer.</summary>
    /// <param name="nada">Qué decir si no había nada que deshacer.</param>
    /// <param name="reintento">Cómo se reintenta lo que ha quedado pendiente.</param>
    public static string ResumenDeshacer(UndoResult res, int vuelven, int retiradas, string nada, string reintento)
        => res.None
            ? nada
            : $"Deshecho: {Cuenta(res.Reverted, "revertido", "revertidos")} · {Cuenta(res.Manual, "conservado", "conservados")} · "
              + $"{Cuenta(res.Missing, "ausente", "ausentes")} · {Cuenta(res.Errors, "error", "errores")}."
              + (res.Pendientes > 0 ? $" {(res.Pendientes == 1 ? "Queda 1 pendiente" : $"Quedan {res.Pendientes} pendientes")}: {reintento}" : "")
              + (vuelven > 0 ? $" {(vuelven == 1 ? "1 canción vuelve" : $"{vuelven} canciones vuelven")} a estar pendiente{(vuelven == 1 ? "" : "s")}: pulsa «Analizar» para ver de nuevo sus propuestas." : "")
              + (retiradas > 0 ? $" {(retiradas == 1 ? "Se retira 1 propuesta" : $"Se retiran {retiradas} propuestas")} de la lista porque su archivo ha cambiado: «Analizar» la{(retiradas == 1 ? "" : "s")} vuelve a calcular." : "")
              + (res.Aviso.Length > 0 ? " ⚠ " + res.Aviso : "");

    /// <summary>«1 ausente», «0 ausentes», «3 ausentes».</summary>
    private static string Cuenta(int n, string singular, string plural) => n == 1 ? $"1 {singular}" : $"{n} {plural}";

    /// <summary>
    /// Filas cuyo archivo acaba de revertirse (por su ruta antes o después del cambio deshecho).
    /// </summary>
    public static List<PreviewRow> FilasRevertidas(IEnumerable<PreviewRow> filas, IEnumerable<string> rutas)
    {
        var revertidas = new HashSet<string>(rutas, StringComparer.OrdinalIgnoreCase);
        return revertidas.Count == 0 ? new List<PreviewRow>()
             : filas.Where(f => revertidas.Contains(f.Result.FilePath)).ToList();
    }

    /// <summary>
    /// Deja de dar por aplicado lo que se acaba de revertir, para que Analizar lo vuelva a proponer.
    /// Devuelve cuántas canciones dejan de contar como aplicadas.
    ///
    /// Visto en el código: aplicadas.json solo crecía (Add y Clear, nunca Remove). Una canción que
    /// solo cambió tags conserva la misma ruta al aplicarse, así que tras deshacerla seguía omitida
    /// como «ya aplicada» para siempre; las renombradas sí volvían porque su ruta cambiaba. La única
    /// salida era «Olvidar aplicadas», que devolvía también las miles aplicadas de verdad.
    ///
    /// Recibe <see cref="UndoResult.AplicadasRevertidas"/>, no todo lo revertido: si no, deshacer un
    /// ajuste de volumen de la biblioteca devolvía a pendientes las 640 aplicadas en Enriquecer.
    /// </summary>
    public static int OlvidarRevertidas(IgnoreList aplicadas, ApplyMarks marcas, IEnumerable<string> rutas)
    {
        var n = 0;
        foreach (var ruta in rutas.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (aplicadas.Contains(ruta)) { aplicadas.Remove(ruta); n++; }
            marcas.Forget(ruta);   // casilla antigua guardada solo por ruta, si quedaba alguna
        }
        return n;
    }

    /// <summary>
    /// Lleva la marca de «aplicada» de cada archivo que el deshacer ha devuelto a su nombre anterior
    /// (<see cref="UndoResult.Vueltas"/>) a la ruta en que queda. Devuelve cuántas se han movido.
    ///
    /// POR QUÉ (hallazgo datos-1). Al deshacer desde el historial una sustitución de la Bandeja, la
    /// descarga vuelve a la bandeja con lo que Enriquecer le aplicó después; su marca se quedaba en la
    /// ruta de la biblioteca, y la copia antigua que se recuperase ahí de la papelera dejaba de
    /// proponerse por una aplicación que nunca fue suya.
    /// </summary>
    public static int SeguirAplicadas(IgnoreList aplicadas, IEnumerable<(string Desde, string Hasta)> vueltas)
    {
        var n = 0;
        foreach (var (desde, hasta) in vueltas)
        {
            if (!aplicadas.Contains(desde)) continue;
            aplicadas.Remove(desde);
            aplicadas.Add(hasta);
            n++;
        }
        return n;
    }

    [RelayCommand]
    private void MarkAll() => MarcarEnLote(Rows, true);

    [RelayCommand]
    private void MarkNone() => MarcarEnLote(Rows, false);

    /// <summary>Marca o desmarca muchas filas y recuenta una sola vez al final.</summary>
    private void MarcarEnLote(IEnumerable<PreviewRow> filas, bool valor)
    {
        using (Recuento.EnLote())
            foreach (var r in filas) r.Apply = valor;
    }

    // --- Menú contextual (sobre lo que haya seleccionado) ---

    /// <summary>
    /// Filas seleccionadas en la tabla. Las mantiene al día la vista, porque el DataGrid de Avalonia
    /// no deja enlazar SelectedItems. Con una sola fila seleccionada contiene esa fila, de modo que
    /// las acciones no tienen que distinguir entre "una" y "varias".
    /// </summary>
    private IReadOnlyList<PreviewRow> _seleccion = Array.Empty<PreviewRow>();

    public void SetSelection(IEnumerable<PreviewRow> filas)
    {
        _seleccion = filas.ToList();
        SeleccionadasInfo = _seleccion.Count > 1 ? $"{_seleccion.Count} seleccionadas" : "";
    }

    /// <summary>Cuántas hay seleccionadas, para decirlo en los botones. Vacío si es una o ninguna.</summary>
    [ObservableProperty] private string _seleccionadasInfo = "";

    /// <summary>
    /// Sobre qué actúa una acción del menú: lo seleccionado, y si no hay nada, la fila en curso.
    /// Así una acción no tiene que preguntarse si el usuario marcó un bloque o pinchó una sola fila.
    /// </summary>
    public static List<PreviewRow> Objetivo(IReadOnlyList<PreviewRow> seleccion, PreviewRow? actual)
        => seleccion.Count > 0 ? seleccion.ToList()
         : actual != null ? new List<PreviewRow> { actual }
         : new List<PreviewRow>();

    /// <summary>
    /// Al marcar/desmarcar un bloque: se marca salvo que ya estuvieran TODAS marcadas. Con una
    /// selección a medias, marcar es lo que espera quien acaba de seleccionar para aplicar; invertir
    /// cada casilla por separado dejaría el bloque igual de mezclado que estaba.
    /// </summary>
    public static bool DebeMarcar(IEnumerable<PreviewRow> filas) => filas.Any(f => !f.Apply);

    private List<PreviewRow> Objetivo() => Objetivo(_seleccion, SelectedRow);

    /// <summary>
    /// Marca o desmarca las seleccionadas. Con varias filas y la casilla en distinto estado, se
    /// marcan todas: es lo que espera quien acaba de seleccionar un bloque para aplicarlo. Solo se
    /// desmarcan cuando ya estaban todas marcadas.
    /// </summary>
    [RelayCommand]
    private void ToggleSelected()
    {
        var filas = Objetivo();
        if (filas.Count == 0) return;

        var marcar = DebeMarcar(filas);
        MarcarEnLote(filas, marcar);
        if (filas.Count > 1) Status = $"{filas.Count} {(marcar ? "marcadas" : "desmarcadas")}.";
    }

    [RelayCommand]
    private void MarkSelected() => MarcarSeleccion(true);

    [RelayCommand]
    private void UnmarkSelected() => MarcarSeleccion(false);

    private void MarcarSeleccion(bool valor)
    {
        var filas = Objetivo();
        if (filas.Count == 0) { Status = "Selecciona antes una o varias canciones."; return; }
        MarcarEnLote(filas, valor);
        Status = $"{filas.Count} {(valor ? "marcadas" : "desmarcadas")}.";
    }

    /// <summary>
    /// Quita las filas seleccionadas y DESCARTA esas canciones: no volverán a aparecer en
    /// Enriquecer. No se toca ningún archivo, y se recuperan desde Ajustes.
    /// </summary>
    [RelayCommand]
    private void RemoveSelected()
    {
        var filas = Objetivo();
        if (filas.Count == 0) { Status = "Selecciona antes una o varias canciones."; return; }

        _engine.IgnoreTracks(filas.Select(f => f.Result.FilePath));
        foreach (var f in filas) OlvidarDecision(_engine.Marks, f.Result.FilePath, f.Huella);
        _engine.Marks.Save();
        foreach (var f in filas) Rows.Remove(f);
        RowsView.Refresh();
        SetSelection(Array.Empty<PreviewRow>());

        _engine.Logger.Log($"Descartadas {filas.Count} (total descartadas: {_engine.Ignored.Count})");
        foreach (var f in filas) _engine.Logger.Detail($"    descartada '{f.Old}'");

        Status = filas.Count == 1
            ? $"Descartada «{filas[0].Old}». No volverá a aparecer (puedes recuperarlas en Ajustes)."
            : $"Descartadas {filas.Count} canciones. No volverán a aparecer (puedes recuperarlas en Ajustes).";
    }

    /// <summary>
    /// Cierra la tirada con las cifras que sirven para afinar el algoritmo: reparto por fuente y por
    /// desenlace, cómo cuadran las duraciones y qué casos fallaron. Además deja un CSV en "informes"
    /// para poder mirarlo en una hoja de cálculo.
    /// </summary>
    private void WriteAnalysisReport(List<ProcessResult> res)
    {
        if (res.Count == 0) return;
        var log = _engine.Logger;

        var found = res.Where(r => !r.Skip && r.Found).ToList();
        var noRes = res.Where(r => !r.Skip && !r.Found && !r.CleanOnly).ToList();
        var skipped = res.Where(r => r.Skip).ToList();

        log.Sum("── Resumen para afinar el algoritmo ─────────────");
        log.Sum($"   BeatTag {AppInfo.Version}   ({DateTime.Now:yyyy-MM-dd HH:mm})");
        log.Sum($"   Identificadas {found.Count} · sin resultado {noRes.Count} · saltadas como mezcla {skipped.Count}");

        // Reparto por fuente y por estrategia de búsqueda (qué variante acertó).
        foreach (var g in found.GroupBy(r => r.Source).OrderByDescending(g => g.Count()))
            log.Sum($"   fuente {g.Key,-14} {g.Count(),6}");

        // La vía es lo que dice cuánto aportan la IA local ("ia") y la huella ("acoustid"), que es
        // justo lo que hay que comparar entre tiradas. Va como resumen, no como detalle.
        foreach (var g in found.Where(r => r.Variant.Length > 0).GroupBy(r => r.Variant).OrderByDescending(g => g.Count()))
            log.Sum($"   via {g.Key,-16} {g.Count(),6}");

        // Confianza: dónde se concentra y cuántas quedan por debajo del umbral.
        var scores = found
            .Select(r => double.TryParse(r.Score, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v)).ToList();
        if (scores.Count > 0)
        {
            log.Sum($"   confianza: media {scores.Average():0.0} · <2 (dudosas) {scores.Count(v => v < 2)} · "
                  + $"2-9 {scores.Count(v => v >= 2 && v < 10)} · >=10 {scores.Count(v => v >= 10)}");
        }

        // Duración: la señal más fiable de "me han dado otra versión".
        var withDur = found.Where(r => r.DurLocal > 0 && int.TryParse(r.DurMatch, out var d) && d > 0)
                           .Select(r => Math.Abs(int.Parse(r.DurMatch) - r.DurLocal)).ToList();
        if (withDur.Count > 0)
            log.Sum($"   duración: ≤2s {withDur.Count(d => d <= 2)} · ≤5s {withDur.Count(d => d is > 2 and <= 5)} · "
                  + $"≤20s {withDur.Count(d => d is > 5 and <= 20)} · >20s {withDur.Count(d => d > 20)} (sospechosas)");

        // Versiones detectadas (remix/edit y quién las firma).
        var vers = found.Where(r => r.RemixKind.Length > 0).ToList();
        log.Sum($"   versiones: {vers.Count} detectadas · {vers.Count(r => r.Remixer.Length > 0)} con autor identificado");

        // Una sola línea con las cifras que se comparan entre tiradas, en formato fijo. Buscarla en
        // dos registros distintos basta para ver si un cambio ha mejorado o empeorado las cosas,
        // sin tener que recontar a mano.
        var dudosas = scores.Count(v => v < LowConfidence);
        var porIa = found.Count(r => r.Variant == "ia");
        var porHuella = found.Count(r => r.Variant == "acoustid");
        log.Sum($"COMPARA|v{AppInfo.Version}|revisadas={res.Count}|identificadas={found.Count}"
              + $"|sinresultado={noRes.Count}|dudosas={dudosas}|ia={porIa}|huella={porHuella}");

        // Lo más accionable: los casos que fallaron, para leerlos y sacar patrones.
        if (noRes.Count > 0)
        {
            log.Sum($"   — primeras {Math.Min(40, noRes.Count)} sin resultado (candidatas a mejorar):");
            foreach (var r in noRes.Take(40)) log.Sum($"       {r.Old}   [query: {r.Kw}]");
        }
        if (skipped.Count > 0)
        {
            log.Detail($"   — primeras {Math.Min(20, skipped.Count)} saltadas como mezcla:");
            foreach (var r in skipped.Take(20)) log.Detail($"       {r.Old}");
        }

        // CSV completo para hoja de cálculo.
        try
        {
            Directory.CreateDirectory(_engine.Paths.ReportsDir);
            var csv = Path.Combine(_engine.Paths.ReportsDir, $"analisis_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("archivo;estado;fuente;via;confianza;dur_local;dur_match;dur_delta;query;artista;titulo;remixer;tipo_version;genero;bpm;anio;propuesto");
            foreach (var r in res)
            {
                var dm = int.TryParse(r.DurMatch, out var d) ? d : 0;
                var delta = (r.DurLocal > 0 && dm > 0) ? Math.Abs(dm - r.DurLocal) : -1;
                var estado = r.Skip ? "MEZCLA" : r.Found ? "OK" : r.CleanOnly ? "LIMPIEZA" : "SIN-RESULTADO";
                sb.AppendLine(string.Join(";", new[]
                {
                    Q(r.Old), estado, Q(r.Source), Q(r.Variant), Q(r.Score.Replace('.', ',')),   // decimal ES para Excel
                    r.DurLocal.ToString(), dm.ToString(), delta.ToString(),
                    Q(r.Kw), Q(r.Artist), Q(r.Title), Q(r.Remixer), Q(r.RemixKind),
                    Q(r.Genre), Q(r.Bpm), Q(r.Year), Q(r.New),
                }));
            }
            File.WriteAllText(csv, sb.ToString(), System.Text.Encoding.UTF8);
            log.Sum($"   Informe para analizar: {csv}");
        }
        catch (Exception e) { log.Error("No se pudo escribir el informe CSV", e); }

        log.Sum("─────────────────────────────────────────────────");
    }

    /// <summary>Entrecomilla un campo del CSV (separador ';', como espera Excel en español).</summary>
    private static string Q(string? s) => TextUtils.CsvField(s);

    /// <summary>Se dispara al terminar un análisis, para que otras pestañas se refresquen.</summary>
    public event Action? AnalysisCompleted;

    /// <summary>Servicios del diálogo "Reanalizar…" (los mismos que usa No encontradas).</summary>
    public SearchServices SearchServicesFor(string filePath)
        => new(_engine.FindCandidatesAsync, _engine.ResolveLinkAsync,
               () => _engine.IdentifyByFingerprintAsync(filePath));

    /// <summary>
    /// Reanaliza UNA fila. Si se pasan términos, se buscan ESOS en vez de deducirlos del nombre del
    /// archivo. El resultado se guarda en la caché: la corrección persiste entre sesiones.
    /// La fila llega por parámetro a propósito: el diálogo previo es modal y la tabla puede perder
    /// la selección mientras está abierto (releer SelectedRow aquí dejaba el reanálisis sin efecto).
    /// </summary>
    public async Task ReanalyzeRowAsync(PreviewRow? row, string searchArtist = "", string searchTitle = "",
        string searchSource = "")
    {
        row ??= SelectedRow;
        // Antes se salía en silencio y parecía que "no funcionaba": ahora se dice por qué.
        if (row == null) { Status = "Selecciona antes una canción de la lista."; return; }
        if (IsBusy) { Status = "Hay un proceso en curso; espera a que termine para reanalizar."; return; }
        _engine.ReleaseAudio();
        IsBusy = true;
        var manual = searchArtist.Length > 0 || searchTitle.Length > 0;
        Status = manual
            ? $"Buscando «{searchArtist} - {searchTitle}»…"
            : "Reanalizando " + row.Old + "…";
        _engine.Logger.Head($"Reanalizar '{row.Old}'"
            + (manual ? $" · buscando '{searchArtist} - {searchTitle}'"
                      + (searchSource.Length > 0 ? $" solo en {searchSource}" : "") : ""));
        try
        {
            var opts = _engine.BuildOptions();
            opts.SearchArtist = searchArtist;
            opts.SearchTitle = searchTitle;
            opts.SearchSource = searchSource;
            var r = await Task.Run(() => _engine.Processor.ProcessAsync(row.Result.FilePath, isAcapella: false, opts));
            var huellaAntes = row.Huella;
            row.UpdateFrom(r);
            var encontrada = r.Found || r.CleanOnly;
            var (porDefecto, estadoInicial) = EstadoInicial(r);
            if (row.Huella != huellaAntes)
            {
                // La propuesta es otra: la casilla puesta en la anterior no vale para esta, igual que
                // al volver a abrir la aplicación. Vuelve a decidir el análisis.
                OlvidarDecision(_engine.Marks, row.Result.FilePath, huellaAntes);
                PonerSinAnotar(row, encontrada && porDefecto);
            }
            RowsView.Refresh();   // repinta la celda en la vista agrupada

            if (encontrada)
            {
                // Igual que al cargar la fila (AddRow): lo que solo encontró una fuente de rescate o
                // un nombre con huellas de una regla fallida no se queda marcado para aplicar. Antes
                // aquí salía «reanalizado» y la casilla seguía como estuviera.
                if (r.Revisar.Length > 0) DesmarcarParaRevisar(row, r.Revisar);
                else if (estadoInicial.Length > 0) row.RowStatus = estadoInicial;   // baja confianza
                else row.RowStatus = manual ? "✔ búsqueda manual" : "reanalizado";
                // La propuesta pasa a ser la buena de este archivo (misma firma que el análisis normal).
                _engine.Analysis.Set(row.Result.FilePath, _engine.BuildOptions().Signature(), r);
                _engine.Analysis.Save();
            }
            else row.RowStatus = r.Skip ? "saltado" : "sin resultado";

            Status = !(r.Found || r.CleanOnly)
                ? $"Sin resultado para «{searchArtist} - {searchTitle}». Prueba otra grafía."
                : r.Revisar.Length > 0
                    ? $"Reanalizado: {row.Old}. Queda desmarcada, pendiente de revisión: {r.Revisar}."
                    : "Reanalizado: " + row.Old;
        }
        catch (Exception e)
        {
            row.RowStatus = "⚠ " + e.Message;
            Status = "Error al reanalizar.";
            _engine.Logger.Error($"Error al reanalizar '{row.Old}'", e);
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Desmarca la fila y enseña por qué hay que revisarla. La decisión que hubiera era sobre la
    /// propuesta anterior, y desmarcar aquí no es una decisión del usuario: se olvida aquella y esto
    /// no se anota como suya.
    /// </summary>
    private void DesmarcarParaRevisar(PreviewRow row, string motivo)
    {
        PonerSinAnotar(row, false);
        OlvidarDecision(_engine.Marks, row.Result.FilePath, row.Huella);
        row.RowStatus = "⚠ revisar: " + motivo;
    }

    /// <summary>
    /// Pone en la lista, desmarcada y con su motivo, una propuesta que otra pestaña (No encontradas,
    /// Incompletas) ha encontrado pero no debe aplicar por su cuenta (<see cref="ProcessResult.Revisar"/>):
    /// se revisa aquí, con todas las demás. Devuelve false si no ha entrado en la lista (el archivo ya
    /// no está en la biblioteca, o cambia menos de lo que pide el filtro de cambios).
    /// </summary>
    public bool MostrarParaRevisar(ProcessResult r)
    {
        if (IsBusy) return false;
        var fila = Rows.FirstOrDefault(x => string.Equals(x.Result.FilePath, r.FilePath, StringComparison.OrdinalIgnoreCase));
        if (fila != null)
        {
            var huellaAntes = fila.Huella;
            fila.UpdateFrom(r);
            if (fila.Huella != huellaAntes) OlvidarDecision(_engine.Marks, r.FilePath, huellaAntes);
            if (r.Revisar.Length > 0) DesmarcarParaRevisar(fila, r.Revisar);
            RowsView.Refresh();
            return true;
        }
        var t = _engine.Library.Tracks.FirstOrDefault(x => string.Equals(x.FilePath, r.FilePath, StringComparison.OrdinalIgnoreCase));
        if (t == null) return false;
        OlvidarDecision(_engine.Marks, r.FilePath, HuellaPropuesta(r));   // AddRow la deja desmarcada con el motivo
        var dentro = AddRow(r, t);
        RowsView.Refresh();
        return dentro;
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.Result.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsBusy) { Status = "Espera a que termine el proceso en curso para escuchar."; return; }
        var path = SelectedRow?.Result.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    // --- F1: analizar una carpeta sin desmarcar las demás ---
    //
    // Visto en los registros de un usuario real: en cada sesión desmarcaba 42 de sus 43 carpetas para
    // trabajar solo «Descargas» (análisis de 33, 59, 98 y 114 canciones) y después las volvía a marcar
    // una a una. Desmarcar saca las canciones de TODAS las pestañas; para acotar un análisis basta con
    // decir qué carpeta. El ámbito vive solo en esta pestaña y no cambia ninguna casilla.

    /// <summary>
    /// Carpeta a la que se limita Enriquecer (análisis, tabla y aplicar), o null para todas las
    /// marcadas. No se guarda: al abrir la aplicación se vuelve a ver todo.
    /// </summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HayAmbito))] private AmbitoAnalisis? _ambito;

    public bool HayAmbito => Ambito != null;

    /// <summary>«Mostrando solo «Descargas» · 98 canciones», para la banda encima de la tabla.</summary>
    [ObservableProperty] private string _ambitoTexto = "";

    partial void OnAmbitoChanged(AmbitoAnalisis? value)
    {
        if (value == null) AmbitoTexto = "";
    }

    /// <summary>Comprueba la carpeta elegida contra la lista de carpetas (ver <see cref="PrepararAmbito(IEnumerable{FolderItem}, string)"/>).</summary>
    public PreparacionAmbito PrepararAmbito(string carpeta) => PrepararAmbito(Folders, carpeta);

    /// <summary>
    /// Analiza solo <paramref name="ambito"/>. Con <paramref name="marcarRaiz"/> (ya confirmado por
    /// el usuario) marca antes su carpeta de la lista, que estaba desmarcada: el análisis la escanea.
    /// </summary>
    public async Task AnalizarAmbitoAsync(AmbitoAnalisis ambito, bool marcarRaiz)
    {
        if (IsBusy) return;
        var raiz = marcarRaiz ? Folders.FirstOrDefault(f => !f.Enabled && Rutas.MismaCarpeta(f.Path, ambito.Raiz)) : null;
        Ambito = ambito;
        await RunAnalyzeAsync(force: false, marcar: raiz);
    }

    /// <summary>Vuelve a la vista de todas las carpetas marcadas, con lo que ya haya analizado.</summary>
    [RelayCommand]
    private void VerTodas()
    {
        if (IsBusy || Ambito == null) return;
        Ambito = null;
        LoadCached();
        Status = Rows.Count > 0
            ? $"Se muestran de nuevo las propuestas de todas las carpetas marcadas ({Rows.Count}). Pulsa «Analizar» para completar el resto."
            : "Se muestran de nuevo todas las carpetas marcadas. Pulsa «Analizar» para analizarlas.";
    }

    /// <summary>Si la carpeta del ámbito se ha quitado de la lista, se vuelve a ver todo.</summary>
    private void OlvidarAmbitoSiCaduco()
    {
        if (Ambito == null || AmbitoVigente(Folders, Ambito)) return;
        Ambito = null;
        LoadCached();
    }

    /// <summary>
    /// Qué carpeta de la lista contiene la elegida y si está marcada. Solo se aceptan carpetas que ya
    /// estén en la lista o dentro de alguna: analizar es trabajar sobre la biblioteca, y lo de fuera
    /// no aparece en ninguna otra pestaña.
    ///
    /// Si su carpeta está DESMARCADA no se analiza «por libre»: se pide marcarla. Analizarla sin
    /// escanearla dejaría en Enriquecer propuestas de archivos que ninguna otra pestaña conoce
    /// (Biblioteca, Editor, No encontradas), y tras aplicar no habría dónde revisarlos. Marcarla es
    /// visible, se explica antes y se puede deshacer quitando la marca; lo analizado se conserva.
    /// </summary>
    public static PreparacionAmbito PrepararAmbito(IEnumerable<FolderItem> carpetas, string elegida)
    {
        if (string.IsNullOrWhiteSpace(elegida)) return new PreparacionAmbito(null, false, "Elige una carpeta.");
        var candidatas = carpetas.Where(f => Rutas.MismaCarpeta(f.Path, elegida) || Rutas.EstaDentroDe(elegida, f.Path)).ToList();
        if (candidatas.Count == 0)
            return new PreparacionAmbito(null, false,
                $"«{NombreCarpeta(elegida)}» no está dentro de ninguna carpeta de la lista. Para analizarla, añádela antes con «Añadir carpeta…».");

        // Con carpetas solapadas (configuraciones de versiones anteriores), una marcada antes que una
        // desmarcada y la de fuera antes que la de dentro, que es la que el escaneo asigna.
        var raiz = candidatas.OrderByDescending(f => f.Enabled).ThenBy(f => f.Path.Length).First();
        var carpeta = Rutas.MismaCarpeta(raiz.Path, elegida)
            ? raiz.Path
            : elegida.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new PreparacionAmbito(new AmbitoAnalisis(carpeta, raiz.Path), !raiz.Enabled, "");
    }

    /// <summary>La carpeta del ámbito sigue en la lista y marcada.</summary>
    public static bool AmbitoVigente(IEnumerable<FolderItem> carpetas, AmbitoAnalisis ambito)
        => carpetas.Any(f => f.Enabled && Rutas.MismaCarpeta(f.Path, ambito.Raiz));

    /// <summary>Las canciones del ámbito (todas si no hay). Se mira la ruta, así vale también para una subcarpeta.</summary>
    public static List<Track> PistasDelAmbito(IEnumerable<Track> pistas, AmbitoAnalisis? ambito)
        => ambito == null ? pistas.ToList() : pistas.Where(t => Rutas.EstaDentroDe(t.FilePath, ambito.Carpeta)).ToList();

    /// <summary>
    /// Dentro de qué carpetas se puede podar la caché de análisis tras una pasada (E1): las que la
    /// pasada ha recorrido de verdad. Con un ámbito, SOLO esa carpeta: con una subcarpeta, la raíz de
    /// la lista borraría lo analizado del resto de la raíz. Sin canciones (un disco desconectado),
    /// ninguna.
    /// </summary>
    public static List<string> RaicesAPodar(IReadOnlyCollection<Track> pistas, AmbitoAnalisis? ambito)
    {
        if (pistas.Count == 0) return new List<string>();
        if (ambito != null) return new List<string> { ambito.Carpeta };
        return pistas.Select(t => t.Folder).Where(f => !string.IsNullOrEmpty(f))
                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Nombre corto del ámbito: «Descargas», o «Descargas\House» para una subcarpeta.</summary>
    public static string NombreAmbito(AmbitoAnalisis a)
    {
        var raiz = NombreCarpeta(a.Raiz);
        if (a.EsRaiz) return raiz;
        var relativa = Path.GetRelativePath(a.Raiz, a.Carpeta);
        return relativa.StartsWith("..", StringComparison.Ordinal) ? NombreCarpeta(a.Carpeta) : Path.Combine(raiz, relativa);
    }

    /// <summary>Texto de la banda del ámbito.</summary>
    public static string TextoAmbito(AmbitoAnalisis a, int canciones)
        => $"Mostrando solo «{NombreAmbito(a)}» · {(canciones == 1 ? "1 canción" : $"{canciones} canciones")}";

    /// <summary>Confirmación para marcar una carpeta desmarcada antes de analizarla.</summary>
    public static (string Titulo, string Cuerpo, string Nota, string Aceptar) ConfirmacionCarpetaDesmarcada(AmbitoAnalisis a)
    {
        var raiz = NombreCarpeta(a.Raiz);
        return ($"«{raiz}» está desmarcada",
                "Las carpetas desmarcadas no forman parte de la biblioteca: sus canciones no se escanean ni aparecen en ninguna pestaña. "
                + $"Para analizar «{NombreAmbito(a)}» hay que marcar «{raiz}». Se escaneará y el análisis se limitará a esa carpeta.",
                "Las demás carpetas se quedan como están. Puedes volver a desmarcarla cuando termines: lo analizado se conserva.",
                "Marcar y analizar");
    }

    /// <summary>Solo el nombre de una carpeta, sin la ruta entera.</summary>
    private static string NombreCarpeta(string ruta)
    {
        var limpia = ruta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nombre = Path.GetFileName(limpia);
        return nombre.Length > 0 ? nombre : limpia;
    }

    // --- E14/M14: historial de deshacer ---

    /// <summary>El modelo del diálogo «Historial de cambios», conectado a esta pestaña.</summary>
    public HistorialDeshacerViewModel CrearHistorial()
        => new(new HistorialDeshacer(_engine.Paths.UndoDir), DeshacerDelHistorialAsync, _engine.Undo.DarPorPerdidos);

    /// <summary>
    /// Deshace, desde el historial, los cambios elegidos de una ejecución (null: todos los
    /// pendientes), con el mismo tratamiento que «Deshacer última»: lo revertido deja de contar como
    /// aplicado, se relee en la biblioteca y sus propuestas salen de la lista. Fuera del hilo de la
    /// interfaz. Devuelve lo que se dice al terminar, o null si había otro proceso en curso.
    /// </summary>
    public async Task<string?> DeshacerDelHistorialAsync(EjecucionDeshacer ejecucion, IReadOnlyCollection<string>? huellas)
    {
        if (IsBusy || ejecucion.Manifiesto is not { } manifiesto) return null;
        _engine.ReleaseAudio();   // deshacer también renombra: el archivo no puede estar sonando
        IsBusy = true;
        var cuantos = huellas?.Count ?? ejecucion.Pendientes;
        Status = $"Deshaciendo {(cuantos == 1 ? "1 cambio" : $"{cuantos} cambios")} de «{ejecucion.TipoTexto}»…";
        _engine.Logger.Head($"Deshaciendo desde el historial: {Path.GetFileName(manifiesto)} ({ejecucion.TipoTexto}), "
                          + (huellas == null ? "todo lo pendiente" : $"{huellas.Count} cambio(s) elegidos") + "…");
        UndoResult res;
        var vuelven = 0;
        try
        {
            res = await Task.Run(() => _engine.DeshacerCambios(manifiesto, huellas));
            vuelven = await AsentarDeshacerAsync(res);
        }
        finally { IsBusy = false; }

        Status = ResumenDeshacer(res, vuelven, RetirarRevertidas(res),
                                 nada: "No quedaba nada pendiente en esa ejecución.",
                                 reintento: "se puede reintentar desde el historial.");
        return Status;
    }
}

/// <summary>Carpeta a la que se limita Enriquecer (F1).</summary>
/// <param name="Carpeta">La carpeta elegida: una de la lista o una subcarpeta suya.</param>
/// <param name="Raiz">La carpeta de la lista que la contiene (o ella misma).</param>
public sealed record AmbitoAnalisis(string Carpeta, string Raiz)
{
    /// <summary>Es una carpeta de la lista, no una subcarpeta.</summary>
    public bool EsRaiz => Rutas.MismaCarpeta(Carpeta, Raiz);
}

/// <summary>Resultado de comprobar la carpeta elegida para analizar (F1).</summary>
/// <param name="Ambito">El ámbito, o null si no se puede (ver <paramref name="Motivo"/>).</param>
/// <param name="RaizDesmarcada">Su carpeta de la lista está desmarcada: hay que confirmar que se marca.</param>
/// <param name="Motivo">Por qué no se puede, o "".</param>
public sealed record PreparacionAmbito(AmbitoAnalisis? Ambito, bool RaizDesmarcada, string Motivo);
