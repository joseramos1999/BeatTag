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
using Etiquetador.Core.Dj;
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

    /// <summary>Enlace a la canción en su plataforma (Spotify, Deezer), si lo trae la lista. Va a Pendientes.</summary>
    public string Enlace { get; init; } = "";

    /// <summary>
    /// «Nueva desde 12/10» si la lista se sigue y la canción no estaba la última vez que se revisó en
    /// un día anterior (ver <see cref="ListasSeguidas.Registrar"/>). Vacío si no.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsNueva))]
    private string _novedad = "";

    public bool EsNueva => Novedad.Length > 0;
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
        OlvidarListaAnterior();
        _cargado = null;
        RowsView.Refresh();
        Resumen = "";
        FechaLista = "";
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

    /// <summary>
    /// De cuándo es la lista que se está viendo. Las listas se guardan unas horas
    /// (<see cref="ChartsProvider.VigenciaLista"/>) y, sin conexión, se enseña la última guardada:
    /// sin decirlo, el usuario podía estar mirando la de hace días creyendo que era la de hoy.
    /// </summary>
    [ObservableProperty] private string _fechaLista = "";

    [RelayCommand]
    private Task LoadAsync() => CargarAsync(forzarRed: false);

    /// <summary>Vuelve a pedir la lista a la red aunque la guardada sea reciente.</summary>
    [RelayCommand]
    private Task ActualizarAsync() => CargarAsync(forzarRed: true);

    private async Task CargarAsync(bool forzarRed)
    {
        if (IsBusy) return;
        // Red de seguridad: si por lo que sea no se cargaron al abrir la pestaña, se cargan aquí.
        if (Countries.Count == 0) await EnsureCountriesAsync();
        if (SelectedCountry is not { } pais) { Status = "Selecciona antes un país."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var fuente = FuenteElegida;
        Status = forzarRed ? $"Descargando de nuevo el Top {ListSize} de {pais.Name}…" : $"Consultando el Top {ListSize} de {pais.Name}…";
        _engine.Logger.Head($"Tendencias: Top {ListSize} de {pais.Name}" + (forzarRed ? " (actualizar)" : ""));
        try
        {
            await AsegurarBibliotecaAsync();
            // Para saber si «Actualizar» trajo de verdad una lista nueva: si la red falla, se enseña la
            // guardada, y esa es de antes de este momento.
            var pedidaEn = DateTime.Now;
            var chart = await _engine.Charts.GetChartAsync(fuente, pais, ListSize, _cts.Token, forzarRed);
            // Con la caché desactivada no hay archivo del que sacar la fecha: la lista acaba de llegar.
            var descargada = _engine.Api.CacheOn ? _engine.Charts.FechaDeLaLista(fuente, pais, ListSize)
                           : chart.Count > 0 ? DateTime.Now : (DateTime?)null;
            // Sin filas no hay lista guardada que enseñar: no se dice que se enseña la de antes.
            var sinActualizar = forzarRed && chart.Count > 0 && NoSeActualizo(descargada, pedidaEn);
            if (sinActualizar) _engine.Logger.Detail($"Tendencias: no se pudo descargar de nuevo el Top de {pais.Name}; se enseña la lista guardada.");
            var fecha = TextoFechaLista(descargada, DateTime.Now, ChartsProvider.VigenciaLista, sinActualizar);
            var tengo = await CruzarAsync(chart, _cts.Token);
            // La fecha se pone a la vez que la tabla. Antes se ponía antes del cruce, y si se cancelaba
            // durante el cruce quedaba la fecha de la lista nueva junto a la tabla anterior.
            FechaLista = fecha;
            _cargado = (fuente, pais, ListSize);
            AnotarLectura(chart, descargada ?? DateTime.Now, completa: true, aviso: "");
            Status = (sinActualizar ? "No se ha podido descargar de nuevo: se enseña la lista guardada. " : "")
                   + $"Top {Rows.Count} de {pais.Name}. {Resumen}. Faltan {Rows.Count - tengo}."
                   + (Novedades.Length > 0 ? " " + Novedades : "");
            _engine.Logger.Sum($"Tendencias {pais.Name}: tienes {tengo} de {Rows.Count}");
        }
        catch (OperationCanceledException) { Status = "Consulta cancelada."; }
        catch (Exception e) { Status = "No se pudieron cargar las tendencias: " + e.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    /// <summary>
    /// «Lista descargada hoy a las 14:32.» y, si no está al día, por qué y cómo intentarlo otra vez:
    /// porque se pidió «Actualizar» y la red no respondió (<paramref name="actualizacionFallida"/>), o
    /// porque es más antigua que la vigencia. Vacío si no se sabe de cuándo es.
    ///
    /// Visto en la revisión: con «Actualizar» y sin red, si la guardada tenía menos de 6 horas se decía
    /// «Lista descargada hoy a las 14:32.» sin más, como si la actualización pedida hubiera ido bien.
    /// </summary>
    internal static string TextoFechaLista(DateTime? descargada, DateTime ahora, TimeSpan vigencia,
                                           bool actualizacionFallida = false)
    {
        if (descargada is not { } f) return "";
        var hora = f.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var cuando = f.Date == ahora.Date ? $"hoy a las {hora}"
                   : f.Date == ahora.Date.AddDays(-1) ? $"ayer a las {hora}"
                   : $"el {f.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)} a las {hora}";
        var texto = $"Lista descargada {cuando}.";
        if (actualizacionFallida)
            texto += " No se ha podido descargar de nuevo y se enseña la guardada: pulsa «Actualizar» para volver a intentarlo.";
        else if (ahora - f > vigencia)
            texto += " No se ha podido descargar una más reciente: pulsa «Actualizar» para volver a intentarlo.";
        return texto;
    }

    /// <summary>
    /// Margen para comparar la fecha del archivo guardado con la hora a la que se pidió la lista: los
    /// dos salen del mismo reloj, pero con resoluciones distintas.
    /// </summary>
    private static readonly TimeSpan MargenReloj = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Se pidió la lista a la red en <paramref name="pedidaEn"/> y lo que hay guardado es de antes (o no
    /// hay nada): la descarga falló y lo que se enseña es la copia anterior. Al descargar bien, la copia
    /// guardada se reescribe y su fecha es posterior a la petición.
    /// </summary>
    internal static bool NoSeActualizo(DateTime? descargada, DateTime pedidaEn)
        => descargada is not { } f || f < pedidaEn - MargenReloj;

    // --- Seguir el chart (P7) ---

    /// <summary>El chart que se está viendo: el que se cargó, no lo que haya ahora en los selectores.</summary>
    private (ChartSource Fuente, ChartCountry Pais, int Tamano)? _cargado;

    protected override ListaSeguida? DescribirLista()
        => _cargado is { } c ? DescribirChart(c.Fuente, c.Pais, c.Tamano) : null;

    /// <summary>Un chart como lista seguida: su fuente, su país y su tamaño son la lista.</summary>
    internal static ListaSeguida DescribirChart(ChartSource fuente, ChartCountry pais, int tamano) => new()
    {
        Tipo = TipoListaSeguida.Chart,
        Clave = ListasSeguidas.ClaveChart(fuente, pais.Code, pais.PlaylistId, tamano),
        Nombre = $"Top {tamano} de {pais.Name} ({fuente})",
        Fuente = fuente.ToString(), Pais = pais.Name, CodigoPais = pais.Code, ListaPais = pais.PlaylistId, Tamano = tamano,
    };
}
