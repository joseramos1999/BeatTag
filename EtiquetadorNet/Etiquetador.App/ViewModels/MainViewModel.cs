using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Una entrada de la barra lateral: la pestaña, su nombre y si ahora mismo se puede entrar.
///
/// Lleva también CÓMO saber que está ocupada, y eso quita de encima un problema que existía: antes
/// la lista de nombres para el aviso de «proceso en curso» se escribía aparte y a mano, y su orden
/// tenía que coincidir con el de las pestañas del XAML. Ya se habían quedado tres fuera por ese
/// motivo. Ahora hay una sola lista y no puede desalinearse consigo misma.
/// </summary>
public sealed partial class PaginaNav : ObservableObject
{
    public int Indice { get; init; }
    public string Icono { get; init; } = "";
    public string Nombre { get; init; } = "";

    /// <summary>Está ejecutando algo largo. Null si esta página no tiene procesos propios (Ayuda).</summary>
    public Func<bool>? Ocupada { get; init; }

    /// <summary>Su ViewModel, para poder leerle el estado que se muestra abajo.</summary>
    public ViewModelBase? Vm { get; init; }

    [ObservableProperty] private bool _habilitada = true;
    [ObservableProperty] private bool _activa;
}

/// <summary>Un bloque de la barra lateral, con su título. Sin título si no lleva encabezado.</summary>
public sealed class GrupoNav
{
    public string Titulo { get; init; } = "";
    public bool TieneTitulo => Titulo.Length > 0;
    public IReadOnlyList<PaginaNav> Paginas { get; init; } = Array.Empty<PaginaNav>();
}

/// <summary>Shell de la app: agrupa las pestañas y comparte el motor (AppEngine).</summary>
public partial class MainViewModel : ViewModelBase
{
    // Índices de los TabItem de MainWindow.axaml.
    private const int LibraryTabIndex = 0;
    private const int EditorTabIndex = 2;
    private const int IdentifyTabIndex = 7;
    private const int TrendsTabIndex = 9;
    private const int SettingsTabIndex = 11;
    private const int HelpTabIndex = 12;

    public AppEngine Engine { get; }

    public LibraryViewModel Library { get; }
    public EnrichViewModel Enrich { get; }
    public EditorViewModel Editor { get; }
    public DuplicatesViewModel Duplicates { get; }
    public QualityViewModel Quality { get; }
    public IncompleteViewModel Incomplete { get; }
    public NotFoundViewModel NotFound { get; }
    public IdentifyViewModel Identify { get; }
    public StatsViewModel Stats { get; }
    public TrendsViewModel Trends { get; }
    public LoudnessViewModel Loudness { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>
    /// Pestaña que está ejecutando una operación larga, o -1 si no hay ninguna. Mientras vale
    /// distinto de -1 el resto de la aplicación queda bloqueada: no se puede cambiar de pestaña
    /// ni actuar sobre las demás, porque tocar archivos que otro proceso está usando falla.
    /// </summary>
    [ObservableProperty] private int _busyTabIndex = -1;

    /// <summary>Hay una operación larga en curso.</summary>
    public bool Busy => BusyTabIndex >= 0;

    /// <summary>Qué se está haciendo, para explicar al usuario por qué no puede tocar nada.</summary>
    [ObservableProperty] private string _busyWhat = "";

    /// <summary>
    /// La biblioteca está lista: hay carpetas y ya se han escaneado. Hasta entonces el resto de
    /// pestañas no tiene nada sobre lo que trabajar, así que se mantienen fuera de alcance para
    /// no mostrar tablas vacías que hacen pensar que la aplicación no funciona.
    /// </summary>
    public bool LibraryReady => Engine.Library.Folders.Count > 0 && Engine.Library.IsScanned;

    /// <summary>Hay que explicar por qué está casi todo en gris (no basta con deshabilitarlo).</summary>
    public bool ShowSetupHint => !Busy && !LibraryReady;

    /// <summary>Siguiente paso concreto, según si ya hay carpetas o todavía no.</summary>
    public string SetupHint => Engine.Library.Folders.Count == 0
        ? "Empieza por añadir tus carpetas de música en esta pestaña. El resto de la aplicación se activará en cuanto la biblioteca esté preparada."
        : "Pulsa «Escanear» para preparar la biblioteca. El resto de la aplicación se activará al terminar.";

    /// <summary>
    /// Pestañas disponibles, un bit por pestaña (bit N = pestaña N). Se calcula en un solo sitio
    /// para que las reglas de bloqueo no queden repartidas por el XAML.
    /// </summary>
    [ObservableProperty] private int _enabledTabs = -1;

    private bool _reverting;

    /// <summary>
    /// La barra lateral, por bloques. Trece destinos en una sola fila de pestañas no se leen: se
    /// agrupan por lo que uno viene a hacer -tener la música, etiquetarla, limpiarla, mirarla-, y
    /// Ajustes y Ayuda quedan aparte abajo porque no forman parte de ese recorrido.
    /// </summary>
    public IReadOnlyList<GrupoNav> Grupos { get; }

    /// <summary>Las mismas páginas en plano y ORDENADAS POR ÍNDICE, para consultarlas por número.</summary>
    public IReadOnlyList<PaginaNav> Paginas { get; }

    public MainViewModel() : this(new AppEngine()) { }

    public MainViewModel(AppEngine engine)
    {
        Engine = engine;
        Library = new LibraryViewModel(engine);
        Enrich = new EnrichViewModel(engine);
        Editor = new EditorViewModel(engine);
        Duplicates = new DuplicatesViewModel(engine);
        Quality = new QualityViewModel(engine);
        Incomplete = new IncompleteViewModel(engine);
        NotFound = new NotFoundViewModel(engine);
        Identify = new IdentifyViewModel(engine);
        Stats = new StatsViewModel(engine);
        Trends = new TrendsViewModel(engine);
        Loudness = new LoudnessViewModel(engine);
        Settings = new SettingsViewModel(engine);

        // El ÍNDICE de cada página es su hueco en el contenido de MainWindow.axaml; el ORDEN en que
        // aparecen aquí es el de la barra lateral, y no tiene por qué ser el mismo.
        Grupos = new GrupoNav[]
        {
            new()
            {
                Titulo = "TU MÚSICA",
                Paginas = new PaginaNav[]
                {
                    new() { Indice = 0,  Icono = "📚", Nombre = "Biblioteca", Vm = Library, Ocupada = () => Library.IsBusy },
                    new() { Indice = 2,  Icono = "✏",  Nombre = "Editor",     Vm = Editor, Ocupada = () => Editor.IsBusy },
                },
            },
            new()
            {
                Titulo = "ETIQUETAR",
                Paginas = new PaginaNav[]
                {
                    new() { Indice = 1,  Icono = "✨", Nombre = "Enriquecer",      Vm = Enrich, Ocupada = () => Enrich.IsBusy },
                    new() { Indice = 6,  Icono = "❓", Nombre = "No encontradas",  Vm = NotFound, Ocupada = () => NotFound.IsBusy },
                    new() { Indice = 7,  Icono = "🎤", Nombre = "Comprobar audio", Vm = Identify, Ocupada = () => Identify.IsBusy },
                },
            },
            new()
            {
                Titulo = "LIMPIAR",
                Paginas = new PaginaNav[]
                {
                    new() { Indice = 3,  Icono = "👥", Nombre = "Duplicados",  Vm = Duplicates, Ocupada = () => Duplicates.IsBusy },
                    new() { Indice = 4,  Icono = "🎧", Nombre = "Calidad",     Vm = Quality, Ocupada = () => Quality.IsBusy },
                    new() { Indice = 5,  Icono = "⚠",  Nombre = "Incompletas", Vm = Incomplete, Ocupada = () => Incomplete.IsBusy },
                    new() { Indice = 10, Icono = "🔊", Nombre = "Volumen",     Vm = Loudness, Ocupada = () => Loudness.IsBusy },
                },
            },
            new()
            {
                Titulo = "EXPLORAR",
                Paginas = new PaginaNav[]
                {
                    new() { Indice = 8,  Icono = "📊", Nombre = "Estadísticas", Vm = Stats, Ocupada = () => Stats.IsBusy },
                    // La carga de países no cuenta: es una precarga de fondo, no un proceso del usuario.
                    new() { Indice = 9,  Icono = "🌍", Nombre = "Tendencias", Vm = Trends,
                            Ocupada = () => Trends.IsBusy && !Trends.LoadingCountries },
                },
            },
            new()
            {
                // Sin título: no son parte del recorrido de trabajo, van al pie.
                Paginas = new PaginaNav[]
                {
                    // En Ajustes cuenta también la IA: instalar Ollama o descargar un modelo tarda mucho.
                    new() { Indice = 11, Icono = "⚙", Nombre = "Ajustes", Vm = Settings,
                            Ocupada = () => Settings.IsBusy || Settings.AiBusy },
                    // Ayuda no ejecuta nada: no tiene forma de estar ocupada.
                    new() { Indice = 12, Icono = "ℹ", Nombre = "Ayuda" },
                },
            },
        };

        Paginas = Grupos.SelectMany(g => g.Paginas).OrderBy(p => p.Indice).ToList();

        // Bloqueo global: seguir el estado "ocupado" de todas las pestañas con operación larga.
        foreach (ViewModelBase vm in new ViewModelBase[]
                 { Library, Enrich, Editor, Duplicates, Quality, Incomplete, NotFound, Identify, Stats, Trends, Loudness, Settings })
            vm.PropertyChanged += OnChildChanged;

        // La biblioteca avisa al escanearse y al cambiar las carpetas: es lo que abre el resto de
        // pestañas. Folders es una ObservableCollection, así que también hay que seguirla: quitar
        // todas las carpetas debe volver a bloquear.
        Engine.Library.Changed += RecomputeTabs;
        Engine.Library.Folders.CollectionChanged += (_, _) => RecomputeTabs();
        RecomputeTabs();

        // La página de arranque no pasa por OnSelectedTabIndexChanged (el valor no ha CAMBIADO),
        // así que su estado y su marca de activa se ponen aquí a mano.
        foreach (var p in Paginas) p.Activa = p.Indice == SelectedTabIndex;
        SeguirEstadoDeLaPagina();

        // "Editar esta canción" desde otras pestañas: la selecciona en el Editor y salta a esa pestaña.
        engine.EditRequested += path =>
        {
            Editor.SelectByPath(path);
            SelectedTabIndex = EditorTabIndex;
        };

        // Al terminar un análisis en Enriquecer, las no identificadas pasan a su pestaña.
        Enrich.AnalysisCompleted += () => NotFound.LoadFromCache();

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        if (Engine.Library.Folders.Count > 0) await Library.ScanAsync();   // rápido gracias a la caché de escaneo
        Enrich.LoadCached();      // rellena la previsualización con lo que ya haya analizado
        NotFound.LoadFromCache(); // y las que no se identificaron, a su pestaña
        _ = Trends.EnsureCountriesAsync();   // la lista de paises, lista para cuando abras Tendencias
        _ = ArrancarIaLocalAsync();          // que Ollama este listo cuando haga falta
    }

    /// <summary>
    /// Deja Ollama en marcha desde el principio si está instalado y la IA local está activada.
    /// Arrancarlo tarda unos segundos y cargar el modelo la primera vez bastante más, así que
    /// hacerlo ahora evita esa espera justo cuando el análisis lo necesita.
    ///
    /// Solo si la IA está activada: levantar un servicio que consume memoria a quien la tiene
    /// desactivada sería tomarse una libertad que no toca.
    /// </summary>
    private async Task ArrancarIaLocalAsync()
    {
        try
        {
            if (!Engine.Config.UseAi) return;
            if (await Engine.Ai.IsRunningAsync()) return;
            if (!OllamaInstaller.EstaInstalado()) return;

            Engine.Logger.Detail("IA local: Ollama está instalado pero parado; arrancándolo…");
            if (!OllamaInstaller.Lanzar(m => Engine.Logger.Detail("  " + m))) return;

            if (await Engine.Ai.WaitUntilRunningAsync(TimeSpan.FromSeconds(30)))
            {
                Engine.Ai.Reset();
                Engine.Logger.Detail("IA local: Ollama preparado.");
            }
            else Engine.Logger.Detail("IA local: Ollama no respondió a tiempo; se intentará al usarla.");
        }
        catch { /* que la IA no arranque nunca debe impedir abrir la aplicación */ }
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsBusy" or "AiBusy" or "LoadingCountries") RecomputeBusy();
    }

    private void RecomputeBusy()
    {
        foreach (var p in Paginas)
        {
            if (p.Ocupada?.Invoke() != true) continue;
            BusyWhat = p.Nombre;
            BusyTabIndex = p.Indice;
            return;
        }
        BusyWhat = "";
        BusyTabIndex = -1;
    }

    /// <summary>Nombre de la página abierta, para la barra de estado y el título de la ventana.</summary>
    public string PaginaActual => Paginas.FirstOrDefault(p => p.Indice == SelectedTabIndex)?.Nombre ?? "";

    /// <summary>Salta a una página desde la barra lateral.</summary>
    [RelayCommand]
    private void IrA(PaginaNav? pagina)
    {
        if (pagina != null) SelectedTabIndex = pagina.Indice;
    }

    // --- Barra de estado del pie ---
    //
    // Cada pestaña contaba lo que hacía donde le venía bien: a media altura en unas, bajo los
    // botones en otras. Buscar dónde te está hablando la aplicación no debería ser parte del
    // trabajo, así que el estado de la página abierta sale siempre en el mismo sitio: abajo.

    /// <summary>Lo que dice la página abierta.</summary>
    [ObservableProperty] private string _estadoPagina = "";

    /// <summary>Avance de la página abierta, de 0 a 100.</summary>
    [ObservableProperty] private double _progresoPagina;

    /// <summary>Hay algo que enseñar en la barra de avance (solo si trabaja y sabe por dónde va).</summary>
    [ObservableProperty] private bool _hayBarraProgreso;

    private INotifyPropertyChanged? _paginaSeguida;

    private ViewModelBase? VmActual => Paginas.FirstOrDefault(p => p.Indice == SelectedTabIndex)?.Vm;

    /// <summary>
    /// Escucha a la página abierta y deja de escuchar a la anterior. Sin lo segundo, cambiar de
    /// pestaña iría acumulando suscripciones y el pie acabaría mostrando el estado de la que no es.
    /// </summary>
    private void SeguirEstadoDeLaPagina()
    {
        if (_paginaSeguida != null) _paginaSeguida.PropertyChanged -= OnEstadoPaginaCambiado;
        _paginaSeguida = VmActual;
        if (_paginaSeguida != null) _paginaSeguida.PropertyChanged += OnEstadoPaginaCambiado;
        RefrescarEstadoPagina();
    }

    private void OnEstadoPaginaCambiado(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Status" or "Progress" or "IsBusy") RefrescarEstadoPagina();
    }

    private void RefrescarEstadoPagina()
    {
        var vm = VmActual;
        EstadoPagina = vm is IEstadoPagina estado ? estado.Status : "";

        // La barra solo aparece si la página está trabajando Y sabe decir por dónde va. Una barra
        // parada en cero mientras algo ocurre es peor que no tener barra.
        var trabajando = vm is IEstadoPagina e2 && e2.IsBusy;
        HayBarraProgreso = trabajando && vm is IProgresoPagina;
        ProgresoPagina = vm is IProgresoPagina p ? p.Progress : 0;
    }

    partial void OnBusyTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Busy));
        RecomputeTabs();
    }

    /// <summary>Decide qué pestañas quedan disponibles. Único sitio donde vive esa regla.</summary>
    private void RecomputeTabs()
    {
        OnPropertyChanged(nameof(LibraryReady));
        OnPropertyChanged(nameof(ShowSetupHint));
        OnPropertyChanged(nameof(SetupHint));

        if (BusyTabIndex >= 0)
            // Proceso en curso: solo la que lo ejecuta, para ver el avance y poder cancelar.
            EnabledTabs = 1 << BusyTabIndex;
        else if (!LibraryReady)
            // Sin biblioteca: solo lo necesario para prepararla y entender cómo empezar.
            EnabledTabs = (1 << LibraryTabIndex) | (1 << SettingsTabIndex) | (1 << HelpTabIndex);
        else
            EnabledTabs = ~0;

        // La barra lateral lee de aquí: la decisión sigue viviendo en un solo sitio.
        foreach (var p in Paginas) p.Habilitada = (EnabledTabs & (1 << p.Indice)) != 0;

        // Si la pestaña abierta acaba de quedarse fuera, devolver a una que sí esté disponible.
        if ((EnabledTabs & (1 << SelectedTabIndex)) == 0)
        {
            _reverting = true;
            SelectedTabIndex = BusyTabIndex >= 0 ? BusyTabIndex : LibraryTabIndex;
            _reverting = false;
        }
    }

    // No se puede saltar a una pestaña que está fuera de alcance (por proceso en curso o por no
    // haber biblioteca todavía). Los TabItem ya salen deshabilitados; esto cubre además los saltos
    // hechos desde código, como "Editar esta canción".
    partial void OnSelectedTabIndexChanged(int value)
    {
        // La barra lateral marca la activa por aquí, venga el cambio de un clic o de código
        // ("Editar esta canción" salta al Editor desde otra pestaña).
        foreach (var p in Paginas) p.Activa = p.Indice == value;
        OnPropertyChanged(nameof(PaginaActual));
        SeguirEstadoDeLaPagina();

        if (_reverting || (EnabledTabs & (1 << value)) != 0)
        {
            // Al entrar en Tendencias se cargan los países (una sola vez). Se hace aquí y no en la
            // vista porque el enganche al árbol visual no llegaba a dispararse con las pestañas.
            if (!_reverting && value == TrendsTabIndex) _ = Trends.EnsureCountriesAsync();

            // Al entrar en Comprobar audio se muestra lo que ya se comprobó en su día. No consulta
            // nada ni gasta nada: solo lee lo guardado. Se hace aquí y no en el constructor para no
            // recorrer la biblioteca entera al arrancar por una pestaña que quizá no se abra.
            if (!_reverting && value == IdentifyTabIndex && !Identify.IsBusy) _ = Identify.RecomponerAsync();
            return;
        }
        _reverting = true;
        SelectedTabIndex = BusyTabIndex >= 0 ? BusyTabIndex : LibraryTabIndex;
        _reverting = false;
    }

    /// <summary>
    /// Comprueba el bit de esta pestaña en <see cref="EnabledTabs"/>. El parámetro es su índice.
    /// Toda la decisión vive en RecomputeTabs; esto solo la consulta.
    /// </summary>
    public static readonly IValueConverter TabEnabled = new PestanaHabilitada();

    /// <summary>
    /// ¿Es esta la página abierta? El parámetro es su índice.
    ///
    /// Con esto, las trece vistas viven a la vez en la ventana y solo se muestra una. Se hace así,
    /// y no creando la vista al entrar, porque cada pestaña guarda estado que no está en su
    /// ViewModel -la posición de la tabla, la selección, qué grupos hay desplegados-, y volver a
    /// crearla lo perdería en cada salto.
    /// </summary>
    public static readonly IValueConverter EsIndice = new PaginaVisible();

    private sealed class PaginaVisible : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int actual
               && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var propio)
               && actual == propio;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class PestanaHabilitada : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var mascara = value as int? ?? 0;
            // Ante un parámetro ausente o ilegible, bloquear: nunca dejar accesible por error una
            // pestaña que podría tocar archivos en uso.
            if (!int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var propia)
                || propia is < 0 or > 31) return false;
            return (mascara & (1 << propia)) != 0;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
