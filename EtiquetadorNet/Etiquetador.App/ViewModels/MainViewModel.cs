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
    /// <summary>Alguna operación larga en curso: se bloquea el cambio de pestaña.</summary>
    [ObservableProperty] private bool _busy;

    private int _busyTabIndex;
    private bool _reverting;

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

        // Bloqueo de pestaña: seguir el estado "ocupado" de cada pestaña con operación larga.
        foreach (ViewModelBase vm in new ViewModelBase[] { Library, Enrich, Duplicates, Quality, Incomplete, NotFound, Stats, Trends, Loudness })
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
        if (e.PropertyName == "IsBusy") RecomputeBusy();
    }

    private void RecomputeBusy()
    {
        if (Library.IsBusy) { Busy = true; _busyTabIndex = 0; }
        else if (Enrich.IsBusy) { Busy = true; _busyTabIndex = 1; }
        else if (Duplicates.IsBusy) { Busy = true; _busyTabIndex = 3; }
        else if (Quality.IsBusy) { Busy = true; _busyTabIndex = 4; }
        else if (Incomplete.IsBusy) { Busy = true; _busyTabIndex = 5; }
        else if (NotFound.IsBusy) { Busy = true; _busyTabIndex = 6; }
        else if (Stats.IsBusy) { Busy = true; _busyTabIndex = 7; }
        else if (Loudness.IsBusy) { Busy = true; _busyTabIndex = 9; }
        else Busy = false;
    }

    // Mientras hay una operación larga, no se puede cambiar de pestaña (se vuelve a la ocupada).
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_reverting || !Busy || value == _busyTabIndex)
        {
            // Al entrar en Tendencias se cargan los países (una sola vez). Se hace aquí y no en la
            // vista porque el enganche al árbol visual no llegaba a dispararse con las pestañas.
            if (!_reverting && value == TrendsTabIndex) _ = Trends.EnsureCountriesAsync();
            return;
        }
        _reverting = true;
        SelectedTabIndex = _busyTabIndex;
        _reverting = false;
    }
}
