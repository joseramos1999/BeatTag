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
    private const int EditorTabIndex = 2;
    private const int TrendsTabIndex = 8;

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

    partial void OnBusyTabIndexChanged(int value) => OnPropertyChanged(nameof(Busy));

    // Mientras hay una operación larga, no se puede cambiar de pestaña (se vuelve a la ocupada).
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_reverting || !Busy || value == BusyTabIndex)
        {
            // Al entrar en Tendencias se cargan los países (una sola vez). Se hace aquí y no en la
            // vista porque el enganche al árbol visual no llegaba a dispararse con las pestañas.
            if (!_reverting && value == TrendsTabIndex) _ = Trends.EnsureCountriesAsync();
            return;
        }
        _reverting = true;
        SelectedTabIndex = BusyTabIndex;
        _reverting = false;
    }

    /// <summary>
    /// Habilita una pestaña solo si no hay ninguna operación en curso, o si es justo la que la
    /// está ejecutando (para poder seguir el avance y cancelarla). El parámetro es su índice.
    /// </summary>
    public static readonly IValueConverter TabEnabled = new PestanaHabilitada();

    private sealed class PestanaHabilitada : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var ocupada = value as int? ?? -1;
            if (ocupada < 0) return true;                       // nada en marcha: todo disponible
            return int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var propia)
                   && propia == ocupada;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
