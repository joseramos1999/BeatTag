using System;
using System.Globalization;
using Avalonia.Data.Converters;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Etiquetador.App.Services;

namespace Etiquetador.App.ViewModels;

/// <summary>Shell de la app: agrupa las pestañas y comparte el motor (AppEngine).</summary>
public partial class MainViewModel : ViewModelBase
{
    // Índices de los TabItem de MainWindow.axaml.
    private const int LibraryTabIndex = 0;
    private const int EditorTabIndex = 2;
    private const int TrendsTabIndex = 8;
    private const int SettingsTabIndex = 10;
    private const int HelpTabIndex = 11;

    public AppEngine Engine { get; }

    public LibraryViewModel Library { get; }
    public EnrichViewModel Enrich { get; }
    public EditorViewModel Editor { get; }
    public DuplicatesViewModel Duplicates { get; }
    public QualityViewModel Quality { get; }
    public IncompleteViewModel Incomplete { get; }
    public NotFoundViewModel NotFound { get; }
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

    // Cada entrada dice si esa pestaña está ocupada y cómo se llama. El ÍNDICE de la lista debe
    // coincidir con el orden de los TabItem de MainWindow.axaml.
    // Va como lista y no como una cadena de "if" a propósito: la versión anterior se escribía a
    // mano y se habían quedado fuera Editor, Tendencias y Ajustes, de modo que sus procesos no
    // bloqueaban nada.
    private (Func<bool> Ocupada, string Nombre)[] _pestanas = Array.Empty<(Func<bool>, string)>();

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
        Stats = new StatsViewModel(engine);
        Trends = new TrendsViewModel(engine);
        Loudness = new LoudnessViewModel(engine);
        Settings = new SettingsViewModel(engine);

        // El orden debe coincidir con los TabItem de MainWindow.axaml. Ayuda (la última) no tiene
        // proceso propio, así que no aparece.
        _pestanas = new (Func<bool>, string)[]
        {
            (() => Library.IsBusy,    "Biblioteca"),
            (() => Enrich.IsBusy,     "Enriquecer"),
            (() => Editor.IsBusy,     "Editor"),
            (() => Duplicates.IsBusy, "Duplicados"),
            (() => Quality.IsBusy,    "Calidad"),
            (() => Incomplete.IsBusy, "Incompletas"),
            (() => NotFound.IsBusy,   "No encontradas"),
            (() => Stats.IsBusy,      "Estadísticas"),
            // La carga de países no cuenta: es una precarga de fondo, no un proceso del usuario.
            (() => Trends.IsBusy && !Trends.LoadingCountries, "Tendencias"),
            (() => Loudness.IsBusy,   "Volumen"),
            // En Ajustes cuenta también la IA: instalar Ollama o descargar un modelo tarda mucho.
            (() => Settings.IsBusy || Settings.AiBusy, "Ajustes"),
        };

        // Bloqueo global: seguir el estado "ocupado" de todas las pestañas con operación larga.
        foreach (ViewModelBase vm in new ViewModelBase[]
                 { Library, Enrich, Editor, Duplicates, Quality, Incomplete, NotFound, Stats, Trends, Loudness, Settings })
            vm.PropertyChanged += OnChildChanged;

        // La biblioteca avisa al escanearse y al cambiar las carpetas: es lo que abre el resto de
        // pestañas. Folders es una ObservableCollection, así que también hay que seguirla: quitar
        // todas las carpetas debe volver a bloquear.
        Engine.Library.Changed += RecomputeTabs;
        Engine.Library.Folders.CollectionChanged += (_, _) => RecomputeTabs();
        RecomputeTabs();

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
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsBusy" or "AiBusy" or "LoadingCountries") RecomputeBusy();
    }

    private void RecomputeBusy()
    {
        for (int i = 0; i < _pestanas.Length; i++)
        {
            if (!_pestanas[i].Ocupada()) continue;
            BusyWhat = _pestanas[i].Nombre;
            BusyTabIndex = i;
            return;
        }
        BusyWhat = "";
        BusyTabIndex = -1;
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
        if (_reverting || (EnabledTabs & (1 << value)) != 0)
        {
            // Al entrar en Tendencias se cargan los países (una sola vez). Se hace aquí y no en la
            // vista porque el enganche al árbol visual no llegaba a dispararse con las pestañas.
            if (!_reverting && value == TrendsTabIndex) _ = Trends.EnsureCountriesAsync();
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
