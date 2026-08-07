using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Etiquetador.App.Services;

namespace Etiquetador.App.ViewModels;

/// <summary>Shell de la app: agrupa las pestañas y comparte el motor (AppEngine).</summary>
public partial class MainViewModel : ViewModelBase
{
    private const int EditorTabIndex = 2;

    public AppEngine Engine { get; }

    public LibraryViewModel Library { get; }
    public EnrichViewModel Enrich { get; }
    public EditorViewModel Editor { get; }
    public DuplicatesViewModel Duplicates { get; }
    public QualityViewModel Quality { get; }
    public IncompleteViewModel Incomplete { get; }
    public NotFoundViewModel NotFound { get; }
    public StatsViewModel Stats { get; }
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
        Settings = new SettingsViewModel(engine);

        // Bloqueo de pestaña: seguir el estado "ocupado" de cada pestaña con operación larga.
        foreach (ViewModelBase vm in new ViewModelBase[] { Library, Enrich, Duplicates, Quality, Incomplete, NotFound, Stats })
            vm.PropertyChanged += OnChildChanged;

        // "Editar esta canción" desde otras pestañas: la selecciona en el Editor y salta a esa pestaña.
        engine.EditRequested += path =>
        {
            Editor.SelectByPath(path);
            SelectedTabIndex = EditorTabIndex;
        };

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        if (Engine.Library.Folders.Count > 0) await Library.ScanAsync();   // rápido gracias a la caché de escaneo
        Enrich.LoadCached();   // rellena la previsualización con lo que ya haya analizado
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
        else Busy = false;
    }

    // Mientras hay una operación larga, no se puede cambiar de pestaña (se vuelve a la ocupada).
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_reverting || !Busy || value == _busyTabIndex) return;
        _reverting = true;
        SelectedTabIndex = _busyTabIndex;
        _reverting = false;
    }
}
