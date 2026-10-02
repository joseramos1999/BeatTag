using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción del chart cruzada con tu biblioteca.</summary>
public sealed partial class TrendRow : ObservableObject
{
    private static readonly IBrush TengoBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A));

    public int Position { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>ID de Spotify, si el chart viene de ahi: con el se pide la duracion exacta.</summary>
    public string SpotifyId { get; init; } = "";
    /// <summary>
    /// Duracion. Es [ObservableProperty] porque en el chart de Spotify llega DESPUES: la tabla se
    /// pinta al momento con el orden y los nombres, y la ficha exacta de cada pista se va rellenando
    /// en segundo plano sin hacer esperar al usuario.
    /// </summary>
    [ObservableProperty] private string _durText = "";

    /// <summary>Ruta del archivo de tu biblioteca, si la tienes.</summary>
    public string FilePath { get; init; } = "";
    public string FileName => FilePath.Length > 0 ? Path.GetFileName(FilePath) : "";
    public bool Tengo => FilePath.Length > 0;
    public string Estado => Tengo ? "En la biblioteca" : "No disponible";

    /// <summary>
    /// Qué copia se ha elegido teniendo varias. Se enseña para que la preferencia no sea invisible:
    /// si sale «Remix» es que de ese tema no tienes ni la extendida ni la original.
    /// </summary>
    public string Version => Tengo ? PrioridadVersion.Nombre(PrioridadVersion.De(FileName)) : "";

    public IBrush StateBrush => Tengo ? TengoBrush : Brushes.Transparent;
}

/// <summary>
/// Pestaña Tendencias: qué suena ahora en cada país y cuánto de eso tienes ya.
///
/// La fuente por defecto es el chart de Spotify, que es el que se corresponde con lo que suena de
/// verdad. Su API ya no sirve las listas editoriales (ver ChartsProvider), así que el orden y los
/// IDs se leen del chart publicado por kworb y la ficha exacta de cada pista se le pide a Spotify
/// con esos IDs. Deezer queda como alternativa seleccionable.
///
/// El cruce con la biblioteca y las acciones son las de <see cref="ListaFrenteBibliotecaViewModel"/>,
/// compartidas con la pestaña Playlists.
/// </summary>
public partial class TrendsViewModel : ListaFrenteBibliotecaViewModel
{
    public ObservableCollection<ChartCountry> Countries { get; } = new();

    [ObservableProperty] private ChartCountry? _selectedCountry;

    /// <summary>
    /// Cierto solo mientras se descarga la lista de países. Es una precarga que arranca sola con
    /// la aplicación, así que no cuenta como proceso a efectos de bloquear el resto de pestañas.
    /// </summary>
    [ObservableProperty] private bool _loadingCountries;
    [ObservableProperty] private int _listSize = 50;

    public int[] ListSizes { get; } = { 20, 50, 100 };

    /// <summary>
    /// De dónde salen las listas. Spotify va primero porque es la que se corresponde con lo que
    /// suena de verdad; Deezer se queda como alternativa por si el otro camino falla.
    /// </summary>
    public string[] Fuentes { get; } = { "Spotify (el que marca lo que suena)", "Deezer" };

    [ObservableProperty] private string _fuente = "Spotify (el que marca lo que suena)";

    private ChartSource FuenteElegida
        => Fuente.StartsWith("Spotify", StringComparison.Ordinal) ? ChartSource.Spotify : ChartSource.Deezer;

    public override string NombreSugerido => $"Tendencias {SelectedCountry?.Name} {DateTime.Now:yyyy-MM-dd}";
    protected override string Etiqueta => "Tendencias";

    /// <summary>Al cambiar de fuente cambian los países disponibles: hay que rehacer el selector.</summary>
    partial void OnFuenteChanged(string value)
    {
        Countries.Clear();
        Rows.Clear();
        RowsView.Refresh();
        Resumen = "";
        Status = "Cargando países…";
        _ = EnsureCountriesAsync();
    }

    public TrendsViewModel(AppEngine engine) : base(engine)
    {
        Status = "Selecciona un país y pulsa Ver tendencias.";
    }

    /// <summary>Carga la lista de países la primera vez que se abre la pestaña.</summary>
    public async Task EnsureCountriesAsync()
    {
        if (Countries.Count > 0 || IsBusy) return;
        // Se marca ANTES que IsBusy: es una precarga de fondo que se lanza al arrancar la
        // aplicación, y no debe bloquear el resto de pestañas como sí hace un proceso del usuario.
        LoadingCountries = true;
        IsBusy = true;
        Status = "Cargando países…";
        _engine.Logger.Detail("Tendencias: pidiendo la lista de países…");
        try
        {
            var paises = await _engine.Charts.GetCountriesAsync(FuenteElegida);
            foreach (var p in paises) Countries.Add(p);
            // España por defecto; si no estuviera, el primero.
            SelectedCountry = Countries.FirstOrDefault(c => c.Name.Equals("Spain", StringComparison.OrdinalIgnoreCase))
                              ?? Countries.FirstOrDefault();
            Status = Countries.Count > 0
                ? $"{Countries.Count} países disponibles. Pulsa Ver tendencias."
                : "No se recibió ningún país. ¿Hay conexión a internet?";
            _engine.Logger.Sum($"Tendencias: {Countries.Count} países cargados.");
        }
        catch (Exception e)
        {
            Status = "No se pudieron cargar los países: " + e.Message;
            _engine.Logger.Error("Tendencias: fallo al cargar los países", e);
        }
        finally { IsBusy = false; LoadingCountries = false; }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        // Red de seguridad: si por lo que sea no se cargaron al abrir la pestaña, se cargan aquí.
        if (Countries.Count == 0) await EnsureCountriesAsync();
        if (SelectedCountry is not { } pais) { Status = "Selecciona antes un país."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Status = $"Consultando el Top {ListSize} de {pais.Name}…";
        _engine.Logger.Head($"Tendencias: Top {ListSize} de {pais.Name}");
        try
        {
            await AsegurarBibliotecaAsync();
            var chart = await _engine.Charts.GetChartAsync(FuenteElegida, pais, ListSize, _cts.Token);
            var tengo = Cruzar(chart);
            Status = $"Top {Rows.Count} de {pais.Name}. {Resumen}. Faltan {Rows.Count - tengo}.";
            _engine.Logger.Sum($"Tendencias {pais.Name}: tienes {tengo} de {Rows.Count}");
        }
        catch (OperationCanceledException) { Status = "Consulta cancelada."; }
        catch (Exception e) { Status = "No se pudieron cargar las tendencias: " + e.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }
}
