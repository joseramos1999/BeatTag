using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Página Asistente IA: las herramientas que usan la IA local, más una que no la necesita.
///
/// Todas siguen la misma norma, que sale de medir el modelo antes de construir nada: la IA
/// PROPONE, las reglas y los datos reales COMPRUEBAN, y el usuario DECIDE. Nada de lo que diga el
/// modelo se escribe sin pasar por las tres cosas.
/// </summary>
public partial class AsistenteViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina
{
    internal readonly AppEngine Engine;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;

    /// <summary>Qué IA hay: modelo en uso, o por qué no hay.</summary>
    [ObservableProperty] private string _estadoIa = "Comprobando la IA local…";
    [ObservableProperty] private bool _iaDisponible;
    [ObservableProperty] private string _modelo = "";

    public BusquedaFraseViewModel Busqueda { get; }
    public FichasIaViewModel FichasIa { get; }
    public GenerosIaViewModel Generos { get; }
    public MezclaViewModel Mezcla { get; }
    public RenombrarIaViewModel Renombrar { get; }
    public CompletarNombresViewModel Completar { get; }

    /// <summary>Se ha creado una colección desde aquí: el shell la abre en Colecciones.</summary>
    public event Action<ColeccionInteligente>? ColeccionCreada;
    internal void AvisarColeccionCreada(ColeccionInteligente c) => ColeccionCreada?.Invoke(c);

    public AsistenteViewModel(AppEngine engine)
    {
        Engine = engine;
        Busqueda = new BusquedaFraseViewModel(this);
        FichasIa = new FichasIaViewModel(this);
        Generos = new GenerosIaViewModel(this);
        Mezcla = new MezclaViewModel(this);
        Renombrar = new RenombrarIaViewModel(this);
        Completar = new CompletarNombresViewModel(this);
    }

    /// <summary>Mira si Ollama responde y con qué modelo. Se llama al entrar en la página.</summary>
    [RelayCommand]
    public async Task ComprobarIaAsync()
    {
        if (!Engine.Config.UseAi)
        {
            IaDisponible = false;
            Modelo = "";
            EstadoIa = "La IA local está desactivada en Enriquecer. Las herramientas funcionan solo con reglas.";
            return;
        }
        Modelo = await Engine.Ai.ModeloEfectivoAsync(Engine.Config.AiModel);
        IaDisponible = Modelo.Length > 0;
        EstadoIa = IaDisponible
            ? $"IA local: {Modelo}"
            : "La IA local no responde (Ollama parado o sin modelos). Las herramientas funcionan solo con reglas; instálala o arráncala en Ajustes.";
        Mezcla.RellenarColecciones();
        FichasIa.RellenarAmbitos();
        Renombrar.RellenarAmbitos();
        Completar.RellenarAmbitos();
    }

    [RelayCommand]
    private void Cancelar() => _cts?.Cancel();

    /// <summary>
    /// Marca la página como ocupada mientras dura <paramref name="trabajo"/>, con cancelación y
    /// avance. Todas las herramientas pasan por aquí para que el bloqueo del resto de la aplicación
    /// y el botón Cancelar funcionen igual en todas.
    /// </summary>
    internal async Task TrabajarAsync(string que, Func<CancellationToken, Task> trabajo)
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        Status = que;
        try { await trabajo(_cts.Token); }
        catch (OperationCanceledException) { Status = "Cancelado."; }
        catch (Exception e)
        {
            Engine.Logger.Error("Asistente IA: " + que, e);
            Status = "Error: " + e.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Las fichas de toda la biblioteca de una vez, para no leerlas canción a canción.</summary>
    internal Dictionary<string, FichaDj> FichasActuales()
    {
        var d = new Dictionary<string, FichaDj>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Engine.Fichas.Rutas)
            if (Engine.Fichas.Obtener(r) is { } f) d[r] = f;
        return d;
    }

    internal bool BibliotecaLista(out string motivo)
    {
        motivo = Engine.Library.IsScanned ? "" : "Primero escanea la biblioteca.";
        return motivo.Length == 0;
    }
}

// =====================================================================================================
// 1. Buscar con una frase
// =====================================================================================================

/// <summary>Una condición en la lista de revisión, con su casilla.</summary>
public sealed partial class FiltroFila : ObservableObject
{
    public FiltroPropuesto Filtro { get; }
    private readonly Action _alCambiar;

    /// <param name="sinDatos">Nadie en la biblioteca tiene todavía ese dato en su ficha.</param>
    public FiltroFila(FiltroPropuesto filtro, bool sinDatos, Action alCambiar)
    {
        Filtro = filtro;
        _alCambiar = alCambiar;
        SinDatos = sinDatos;

        // Qué sale marcado, medido sobre una biblioteca real:
        //   · Lo leído con reglas, sí.
        //   · Lo que añade la IA, no. Lo que pasaba la validación era discutible («la noche» →
        //     energía alta; «para que baile la gente mayor» → alta con un modelo y apertura con otro).
        //   · Una condición de ficha que no tiene NADIE, no: dejaba la colección en cero canciones
        //     («Bad Bunny con mucha energía» no encontraba ningún tema de Bad Bunny).
        _aceptado = filtro.Valido && filtro.Origen == OrigenFiltro.Frase && !sinDatos;
    }

    public bool SinDatos { get; }

    [ObservableProperty] private bool _aceptado;
    partial void OnAceptadoChanged(bool value) => _alCambiar();

    public bool SePuedeUsar => Filtro.Valido;
    public string Campo => Filtro.NombreCampo;
    public string Valor => Filtro.Valor;
    public string Cita => Filtro.Cita.Length > 0 ? $"«{Filtro.Cita}»" : "";
    public string Origen => !Filtro.Valido ? "Descartado" : Filtro.Origen == OrigenFiltro.Frase ? "Leído en tu frase" : "Propuesto por la IA: revísalo";
    public string Motivo => Filtro.Descartado.Length > 0
        ? Filtro.Descartado
        : SinDatos ? "· Ninguna canción tiene este dato en su ficha todavía." : "";
    public bool EsIa => Filtro.Origen == OrigenFiltro.Ia;
}

public sealed partial class BusquedaFraseViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;

    public BusquedaFraseViewModel(AsistenteViewModel padre) => _p = padre;

    [ObservableProperty] private string _frase = "";
    [ObservableProperty] private string _nombreColeccion = "";
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _aviso = "";
    [ObservableProperty] private bool _hayFiltros;

    public ObservableCollection<FiltroFila> Filtros { get; } = new();
    public ObservableCollection<FichaRow> Vista { get; } = new();

    public IReadOnlyList<string> Ejemplos { get; } = new[]
    {
        "reggaeton suave para abrir una boda, sin palabrotas",
        "house vocal entre 120 y 124 bpm para el pico",
        "temas en inglés de los 80 para cerrar",
        "cumbia y merengue de los 90",
    };

    [RelayCommand]
    private void UsarEjemplo(string? ejemplo) { if (ejemplo != null) Frase = ejemplo; }

    [RelayCommand]
    private Task InterpretarAsync() => _p.TrabajarAsync("Leyendo la petición…", async ct =>
    {
        var frase = Frase.Trim();
        if (frase.Length == 0) { _p.Status = "Escribe qué buscas."; return; }
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var voc = await Task.Run(() => new VocabularioBiblioteca(_p.Engine.Library.Tracks.ToList()), ct);
        var reglas = BusquedaIa.DesdeFrase(frase, voc);
        var ia = new List<FiltroPropuesto>();
        var nota = "";

        if (_p.IaDisponible)
        {
            _p.Status = "Consultando a la IA…";
            var (json, error) = await _p.Engine.Ai.PedirJsonAsync(BusquedaIa.Sistema, frase, _p.Modelo, 512, ct: ct);
            if (error.Length > 0) nota = $"La IA no respondió ({error}); se usa solo lo leído en la frase.";
            else ia = BusquedaIa.DesdeIa(json, frase, voc);
        }
        else nota = "Sin IA: solo se usa lo que las reglas entienden de la frase.";

        var fichas = _p.FichasActuales().Values.ToList();
        bool SinDatos(string campo) => campo switch
        {
            "energia" => !fichas.Any(x => x.Energia > 0),
            "momento" => !fichas.Any(x => x.Momentos != MomentoSet.Ninguno),
            "voz" => !fichas.Any(x => x.Voz != TipoVoz.SinIndicar),
            "idioma" => !fichas.Any(x => x.Idioma.Trim().Length > 0),
            _ => false,
        };

        Filtros.Clear();
        foreach (var f in BusquedaIa.Combinar(reglas, ia)) Filtros.Add(new FiltroFila(f, SinDatos(f.Campo), Recalcular));
        HayFiltros = Filtros.Any(f => f.SePuedeUsar);
        NombreColeccion = Capitalizar(frase);
        Recalcular();

        var descartados = Filtros.Count(f => !f.SePuedeUsar);
        _p.Status = !HayFiltros
            ? "No se ha encontrado ninguna condición en la frase. Prueba a nombrar un género, un tempo o un momento de la sesión."
            : $"{Filtros.Count(f => f.SePuedeUsar)} condiciones" + (descartados > 0 ? $" · {descartados} propuestas de la IA descartadas" : "")
              + (nota.Length > 0 ? ". " + nota : ".");
    });

    private static string Capitalizar(string s)
    {
        var t = s.Length > 60 ? s[..60].TrimEnd() + "…" : s;
        return t.Length == 0 ? t : char.ToUpper(t[0]) + t[1..];
    }

    /// <summary>Vuelve a contar las canciones con las condiciones marcadas. Se llama al tocar una casilla.</summary>
    private void Recalcular()
    {
        Vista.Clear();
        var aceptados = Filtros.Where(f => f.Aceptado && f.SePuedeUsar).Select(f => f.Filtro).ToList();
        var c = BusquedaIa.AColeccion(aceptados, NombreColeccion);
        if (!FiltroColeccion.TieneCriterios(c)) { Resumen = ""; Aviso = ""; return; }

        var fichas = _p.FichasActuales();
        var encontradas = _p.Engine.Library.Tracks
            .Select(t => (t, f: fichas.GetValueOrDefault(t.FilePath)))
            .Where(x => FiltroColeccion.Cumple(c, x.t, x.f))
            .ToList();
        foreach (var (t, f) in encontradas.Take(300)) Vista.Add(new FichaRow(t, f));

        var segundos = encontradas.Sum(x => (long)x.t.DurationSeconds);
        Resumen = $"{encontradas.Count} canciones · {(int)TimeSpan.FromSeconds(segundos).TotalMinutes} min"
                  + (encontradas.Count > 300 ? " (se muestran las 300 primeras)" : "");

        // Lo que más sorprende: pedir energía o momento y que no salga nada porque nadie los ha
        // rellenado. Se dice cuántas canciones tienen ese dato, en vez de dejar una lista vacía sin más.
        var deFicha = aceptados.Where(f => BusquedaIa.DependeDeFicha(f.Campo)).Select(f => f.NombreCampo.ToLowerInvariant()).Distinct().ToList();
        Aviso = deFicha.Count == 0
            ? ""
            : $"{string.Join(", ", deFicha)} solo se encuentra en canciones con ficha DJ, y ahora hay {fichas.Count} con ficha. "
              + "Desmarca esas condiciones o rellena fichas (a mano o con «Proponer fichas»).";
    }

    [RelayCommand]
    private void CrearColeccion()
    {
        var aceptados = Filtros.Where(f => f.Aceptado && f.SePuedeUsar).Select(f => f.Filtro).ToList();
        var c = BusquedaIa.AColeccion(aceptados, NombreColeccion);
        if (!FiltroColeccion.TieneCriterios(c)) { _p.Status = "Marca al menos una condición."; return; }
        _p.AvisarColeccionCreada(c);
    }
}

// =====================================================================================================
// 2. Proponer fichas
// =====================================================================================================

public sealed partial class PropuestaFichaFila : ObservableObject
{
    public PropuestaFicha Propuesta { get; }
    public Track Track { get; }

    public PropuestaFichaFila(PropuestaFicha p, Track t) { Propuesta = p; Track = t; }

    [ObservableProperty] private bool _aceptar = true;

    public string Artist => Track.Artist ?? "";
    public string Title => Track.Title ?? Track.FileName;
    public string Idioma => Propuesta.Idioma;
    public string Voz => Fichas.Nombre(Propuesta.Voz);
    public string Origen => Propuesta.Origen;
}

public sealed partial class FichasIaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    public const string TodaLaBiblioteca = "Toda la biblioteca";

    public FichasIaViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        RellenarAmbitos();
    }

    public ObservableCollection<string> Ambitos { get; } = new();
    [ObservableProperty] private string? _ambito = TodaLaBiblioteca;

    public IReadOnlyList<int> Limites { get; } = new[] { 50, 200, 1000, 5000 };
    [ObservableProperty] private int _limite = 200;

    public ObservableCollection<PropuestaFichaFila> Propuestas { get; } = new();
    [ObservableProperty] private string _resumen = "";

    public void RellenarAmbitos()
    {
        var actual = Ambito;
        Ambitos.Clear();
        Ambitos.Add(TodaLaBiblioteca);
        foreach (var c in _p.Engine.Colecciones.Todas) Ambitos.Add(c.Nombre);
        Ambito = Ambitos.Contains(actual ?? "") ? actual : TodaLaBiblioteca;
    }

    [RelayCommand]
    private Task ProponerAsync() => _p.TrabajarAsync("Buscando canciones sin idioma o sin voz…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var fichas = _p.FichasActuales();
        var coleccion = _p.Engine.Colecciones.Todas.FirstOrDefault(c => c.Nombre == Ambito);
        var candidatas = _p.Engine.Library.Tracks
            .Where(t => coleccion == null || FiltroColeccion.Cumple(coleccion, t, fichas.GetValueOrDefault(t.FilePath)))
            .Where(t => FichasIa.NecesitaPropuesta(fichas.GetValueOrDefault(t.FilePath)))
            .Take(Limite)
            .ToList();

        Propuestas.Clear();
        if (candidatas.Count == 0) { _p.Status = "Todas las canciones del ámbito ya tienen idioma y voz."; return; }

        var sinRespuesta = 0;
        string ultimoError = "";
        for (var i = 0; i < candidatas.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = candidatas[i];
            var f = fichas.GetValueOrDefault(t.FilePath);

            var p = FichasIa.PorNombre(t, f);
            if (p == null && _p.IaDisponible)
            {
                var (json, error) = await _p.Engine.Ai.PedirJsonAsync(
                    FichasIa.Sistema, FichasIa.Peticion(t), _p.Modelo, 32, FichasIa.ClaveCache(t), ct);
                if (error.Length > 0) { sinRespuesta++; ultimoError = error; }
                else p = FichasIa.Interpretar(t, f, json);
            }
            if (p is { HayAlgo: true }) Propuestas.Add(new PropuestaFichaFila(p, t));

            _p.Progress = 100.0 * (i + 1) / candidatas.Count;
            _p.Status = $"Proponiendo fichas… {i + 1} de {candidatas.Count}";
        }

        Resumen = $"{Propuestas.Count} propuestas para {candidatas.Count} canciones revisadas.";
        _p.Status = !_p.IaDisponible
            ? $"Sin IA solo se reconocen las que dicen «instrumental» en el nombre: {Propuestas.Count}."
            : sinRespuesta > 0
                ? $"{Resumen} {sinRespuesta} sin respuesta de la IA ({ultimoError})."
                : $"{Resumen} Revísalas antes de aceptar.";
    });

    [RelayCommand]
    private void MarcarTodas() { foreach (var p in Propuestas) p.Aceptar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var p in Propuestas) p.Aceptar = false; }

    [RelayCommand]
    private void Aceptar()
    {
        var elegidas = Propuestas.Where(p => p.Aceptar).ToList();
        if (elegidas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        var err = _p.Engine.GuardarFichas(elegidas.Select(p => (p.Track.FilePath, p.Propuesta.Cambios())));
        foreach (var p in elegidas) Propuestas.Remove(p);
        _p.Engine.AvisarFichasCambiadas();
        _p.Status = err.Length > 0
            ? $"⚠ No se pudieron guardar las fichas: {err}"
            : $"Fichas actualizadas en {elegidas.Count} canciones. Las desmarcadas siguen en la lista.";
    }
}

// =====================================================================================================
// 3. Unificar géneros
// =====================================================================================================

public sealed partial class GeneroFila : ObservableObject
{
    public string Original { get; }
    public int Canciones { get; }
    public OrigenGenero OrigenValor { get; private set; }

    public GeneroFila(PropuestaGenero p)
    {
        Original = p.Original;
        Canciones = p.Canciones;
        OrigenValor = p.Origen;
        _propuesto = p.Propuesto;
        // Lo que dicen las reglas sale marcado; lo que propone la IA, no: hay que elegirlo a propósito.
        _aplicar = p.Origen is OrigenGenero.Regla or OrigenGenero.NoEsGenero;
    }

    [ObservableProperty] private string _propuesto;
    [ObservableProperty] private bool _aplicar;

    public bool Cambia => !string.Equals(Original, Propuesto, StringComparison.Ordinal);
    public string PropuestoTexto => Propuesto.Length == 0 ? "(quitar género)" : Propuesto;

    public string Origen => OrigenValor switch
    {
        OrigenGenero.Regla => "Regla",
        OrigenGenero.NoEsGenero => "No es un género",
        OrigenGenero.Ia => "IA",
        _ => "",
    };

    public void PonerIa(string destino)
    {
        OrigenValor = OrigenGenero.Ia;
        Propuesto = destino;
        OnPropertyChanged(nameof(Origen));
    }

    partial void OnPropuestoChanged(string value)
    {
        OnPropertyChanged(nameof(Cambia));
        OnPropertyChanged(nameof(PropuestoTexto));
    }
}

public sealed partial class GenerosIaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    public GenerosIaViewModel(AsistenteViewModel padre) => _p = padre;

    public ObservableCollection<GeneroFila> Filas { get; } = new();
    [ObservableProperty] private string _resumen = "";

    [RelayCommand]
    private Task AnalizarAsync() => _p.TrabajarAsync("Contando géneros…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var propuestas = GenerosIa.Contar(_p.Engine.Library.Tracks.ToList())
                                  .Select(x => GenerosIa.PorReglas(x.Original, x.Canciones)).ToList();
        var destinos = GenerosIa.Destinos(propuestas);
        var filas = propuestas.Select(p => new GeneroFila(p)).ToDictionary(f => f.Original, StringComparer.Ordinal);

        var preguntar = _p.IaDisponible ? GenerosIa.ParaIa(propuestas) : Array.Empty<string>();
        // La clave de caché incluye la lista de destinos: si la biblioteca cambia, la respuesta de antes
        // pudo elegir entre otras opciones y no vale.
        var huella = Huella(string.Join("|", destinos));

        // Las variantes que solo se distinguen en mayúsculas o espacios se preguntan UNA vez. Medido
        // en una biblioteca real: «RKT» salió como Reggaeton y «rkt» y «Rkt» como Rock. Con una sola
        // pregunta, al menos la respuesta es la misma para las tres y se revisa una vez.
        var grupos = preguntar.GroupBy(VocabularioBiblioteca.Clave, StringComparer.Ordinal)
                              .Where(g => g.Key.Length > 0).ToList();
        for (var i = 0; i < grupos.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var representante = grupos[i].OrderByDescending(r => filas[r].Canciones).First();
            var (json, error) = await _p.Engine.Ai.PedirJsonAsync(
                GenerosIa.Sistema, GenerosIa.Peticion(representante, destinos), _p.Modelo, 48,
                $"genero:{huella}:{grupos[i].Key}", ct);
            if (error.Length == 0 && GenerosIa.Interpretar(json, representante, destinos) is { } destino)
                foreach (var variante in grupos[i])
                    if (!string.Equals(variante.Trim(), destino, StringComparison.OrdinalIgnoreCase))
                        filas[variante].PonerIa(destino);
            _p.Progress = 100.0 * (i + 1) / grupos.Count;
            _p.Status = $"Consultando a la IA… {i + 1} de {grupos.Count} géneros";
        }

        Filas.Clear();
        foreach (var f in filas.Values.Where(f => f.Cambia).OrderByDescending(f => f.Canciones)) Filas.Add(f);

        var cambian = Filas.Sum(f => f.Canciones);
        Resumen = $"{propuestas.Count} géneros distintos · {Filas.Count} con propuesta ({cambian} canciones)";
        _p.Status = Filas.Count == 0
            ? "No hay nada que unificar."
            : $"{Resumen}. Las de la IA salen desmarcadas: revísalas antes de marcarlas.";
    });

    private static string Huella(string s)
    {
        // FNV-1a: estable entre ejecuciones, a diferencia de string.GetHashCode.
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }

    [RelayCommand]
    private void MarcarTodas() { foreach (var f in Filas) f.Aplicar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Filas) f.Aplicar = false; }

    public IReadOnlyList<GeneroFila> Marcadas => Filas.Where(f => f.Aplicar && f.Cambia).ToList();

    /// <summary>Escribe los géneros marcados. La confirmación la pide la vista.</summary>
    public Task AplicarAsync() => _p.TrabajarAsync("Escribiendo géneros…", async ct =>
    {
        var marcadas = Marcadas.ToDictionary(f => f.Original, f => f.Propuesto.Trim(), StringComparer.Ordinal);
        if (marcadas.Count == 0) { _p.Status = "No hay ningún género marcado."; return; }

        var cambios = _p.Engine.Library.Tracks
            .Where(t => t.Genre != null && marcadas.ContainsKey(t.Genre))
            .Select(t => (t.FilePath, marcadas[t.Genre!]))
            .ToList();

        _p.Engine.ReleaseAudio();
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => _p.Engine.AplicarGeneros(cambios, progreso, ct));

        await _p.Engine.Library.ScanAsync();   // los géneros nuevos, en todas las pestañas
        foreach (var f in Filas.Where(f => f.Aplicar).ToList()) Filas.Remove(f);

        var texto = $"Género actualizado en {res.Escritas} canciones";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no se pudieron escribir (detalle en el registro)";
        if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ El cambio NO quedó anotado y no se podrá deshacer: {res.UndoErr}"
            : res.Escritas > 0 ? ". Se puede deshacer desde Enriquecer." : ".";
        _p.Status = texto;
    });
}

// =====================================================================================================
// 5. Renombrar con IA
// =====================================================================================================

public sealed partial class NombreFila : ObservableObject
{
    public string Ruta { get; }
    public string Actual { get; }
    public string Carpeta { get; }
    public IReadOnlyList<string> ListaAvisos { get; }

    public NombreFila(PropuestaNombre p)
    {
        Ruta = p.Ruta;
        Actual = p.Actual;
        Carpeta = Path.GetFileName(Path.GetDirectoryName(p.Ruta) ?? "");
        ListaAvisos = p.Avisos;
        _propuesto = p.Propuesto;
    }

    /// <summary>Editable: si la propuesta está casi bien, se corrige aquí antes de aceptarla.</summary>
    [ObservableProperty] private string _propuesto;

    /// <summary>Todo sale desmarcado: medido, la propuesta es buena en unas 3 de cada 4.</summary>
    [ObservableProperty] private bool _aceptar;

    public bool HayAvisos => ListaAvisos.Count > 0;
    public string Avisos => string.Join(" ", ListaAvisos.Select(a => "⚠ " + a));
}

public sealed partial class RenombrarIaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    public const string TodaLaBiblioteca = "Toda la biblioteca";

    public RenombrarIaViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        FilasView = new Avalonia.Collections.DataGridCollectionView(Filas);
        RellenarAmbitos();
    }

    public ObservableCollection<string> Ambitos { get; } = new();
    [ObservableProperty] private string? _ambito = TodaLaBiblioteca;

    public IReadOnlyList<int> Limites { get; } = new[] { 50, 200, 1000, 5000 };
    [ObservableProperty] private int _limite = 200;

    public ObservableCollection<NombreFila> Filas { get; } = new();
    public Avalonia.Collections.DataGridCollectionView FilasView { get; }

    [ObservableProperty] private bool _soloSinAvisos;
    [ObservableProperty] private string _resumen = "";

    partial void OnSoloSinAvisosChanged(bool value)
    {
        FilasView.Filter = value ? o => o is NombreFila f && !f.HayAvisos : null;
        FilasView.Refresh();
    }

    public void RellenarAmbitos()
    {
        var actual = Ambito;
        Ambitos.Clear();
        Ambitos.Add(TodaLaBiblioteca);
        foreach (var c in _p.Engine.Colecciones.Todas) Ambitos.Add(c.Nombre);
        Ambito = Ambitos.Contains(actual ?? "") ? actual : TodaLaBiblioteca;
    }

    [RelayCommand]
    private Task ProponerAsync() => _p.TrabajarAsync("Buscando nombres sucios…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }
        if (!_p.IaDisponible) { _p.Status = "Esta herramienta necesita la IA local. Arráncala o instálala en Ajustes."; return; }

        var fichas = _p.FichasActuales();
        var coleccion = _p.Engine.Colecciones.Todas.FirstOrDefault(c => c.Nombre == Ambito);
        var candidatas = _p.Engine.Library.Tracks
            .Where(t => coleccion == null || FiltroColeccion.Cumple(coleccion, t, fichas.GetValueOrDefault(t.FilePath)))
            .Where(t => RenombradoIa.PareceSucio(t.FilePath))
            .Take(Limite)
            .ToList();

        Filas.Clear();
        if (candidatas.Count == 0) { Resumen = ""; _p.Status = "No hay nombres sucios en el ámbito elegido."; return; }

        _p.Engine.Ai.Reset();   // por si se desactivó sola en una tirada anterior
        var descartes = new Dictionary<string, int>();
        for (var i = 0; i < candidatas.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = candidatas[i];
            var ia = await _p.Engine.Ai.ParseAsync(Path.GetFileNameWithoutExtension(t.FilePath), t.Artist ?? "", t.Title ?? "", _p.Modelo, ct);
            var (propuesta, por) = RenombradoIa.Evaluar(t.FilePath, t.Artist ?? "", t.Title ?? "", ia);
            if (propuesta != null) Filas.Add(new NombreFila(propuesta));
            else descartes[por] = descartes.GetValueOrDefault(por) + 1;

            _p.Progress = 100.0 * (i + 1) / candidatas.Count;
            _p.Status = $"Consultando a la IA… {i + 1} de {candidatas.Count}";
        }

        var principales = string.Join(" · ", descartes.OrderByDescending(d => d.Value).Take(3).Select(d => $"{d.Value} {d.Key.TrimEnd('.').ToLowerInvariant()}"));
        Resumen = $"{Filas.Count} propuestas de {candidatas.Count} nombres sucios · {Filas.Count(f => f.HayAvisos)} con avisos";
        _p.Status = Filas.Count == 0
            ? $"Ninguna propuesta pasó las comprobaciones. {principales}"
            : $"{Resumen}. Descartadas: {principales}.";
    });

    [RelayCommand]
    private void MarcarTodas() { foreach (var f in FilasView.Cast<NombreFila>()) f.Aceptar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Filas) f.Aceptar = false; }

    public IReadOnlyList<NombreFila> Marcadas => Filas.Where(f => f.Aceptar && f.Propuesto.Trim().Length > 0).ToList();

    /// <summary>Renombra las marcadas. La confirmación la pide la vista.</summary>
    public Task RenombrarAsync() => _p.TrabajarAsync("Renombrando…", async ct =>
    {
        var marcadas = Marcadas;
        if (marcadas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        _p.Engine.ReleaseAudio();
        var cambios = marcadas.Select(f => (f.Ruta, f.Propuesto.Trim())).ToList();
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => _p.Engine.RenombrarArchivos(cambios, progreso, ct));

        var hechos = new HashSet<string>(res.Hechos.Select(h => h.Origen), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Filas.Where(f => hechos.Contains(f.Ruta)).ToList()) Filas.Remove(f);
        if (res.Hechos.Count > 0) await _p.Engine.Library.ScanAsync();
        if (res.Hechos.Count > 0) _p.Engine.AvisarFichasCambiadas();

        var texto = $"{res.Hechos.Count} archivos renombrados";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no ({res.Fallos[0]})";
        if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ El cambio NO quedó anotado y no se podrá deshacer: {res.UndoErr}"
            : res.Hechos.Count > 0 ? ". Se puede deshacer desde Enriquecer; si usas rekordbox, repara la colección en Ajustes." : ".";
        _p.Status = texto;
    });
}

// =====================================================================================================
// 6. Completar nombres cortados
// =====================================================================================================

public sealed partial class CortadoFila : ObservableObject
{
    public Completado Completado { get; }
    public CortadoFila(Completado c)
    {
        Completado = c;
        _propuesto = c.Propuesto;
        // Lo que sale de las ETIQUETAS del propio archivo se marca: no lo ha inventado nadie, ya
        // estaba dentro del archivo. Lo de la IA, no.
        _aceptar = c.Origen == OrigenCompletado.Etiquetas;
    }

    [ObservableProperty] private string _propuesto;
    [ObservableProperty] private bool _aceptar;

    public string Ruta => Completado.Ruta;
    public string Actual => Completado.Actual;
    public string Detalle => Completado.Detalle;
    public string Origen => Completado.Origen switch
    {
        OrigenCompletado.Etiquetas => "Etiquetas del archivo",
        OrigenCompletado.IaConfirmada => "IA, confirmada en el catálogo",
        _ => "IA sin confirmar",
    };
}

public sealed partial class CompletarNombresViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    public const string TodaLaBiblioteca = "Toda la biblioteca";

    public CompletarNombresViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        RellenarAmbitos();
    }

    public ObservableCollection<string> Ambitos { get; } = new();
    [ObservableProperty] private string? _ambito = TodaLaBiblioteca;

    public IReadOnlyList<int> Limites { get; } = new[] { 50, 200, 1000, 5000 };
    [ObservableProperty] private int _limite = 1000;

    /// <summary>Consultar a la IA los que no se arreglan con sus etiquetas. Cuesta ~0,5 s por canción.</summary>
    [ObservableProperty] private bool _usarIa = true;

    public ObservableCollection<CortadoFila> Filas { get; } = new();
    [ObservableProperty] private string _resumen = "";

    public void RellenarAmbitos()
    {
        var actual = Ambito;
        Ambitos.Clear();
        Ambitos.Add(TodaLaBiblioteca);
        foreach (var c in _p.Engine.Colecciones.Todas) Ambitos.Add(c.Nombre);
        Ambito = Ambitos.Contains(actual ?? "") ? actual : TodaLaBiblioteca;
    }

    [RelayCommand]
    private Task BuscarAsync() => _p.TrabajarAsync("Buscando nombres cortados…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var fichas = _p.FichasActuales();
        var coleccion = _p.Engine.Colecciones.Todas.FirstOrDefault(c => c.Nombre == Ambito);
        var cortados = _p.Engine.Library.Tracks
            .Where(t => coleccion == null || FiltroColeccion.Cumple(coleccion, t, fichas.GetValueOrDefault(t.FilePath)))
            .Where(t => NombreCortado.Parece(t.FilePath, t.Artist ?? "", t.Title ?? ""))
            .Take(Limite)
            .ToList();

        Filas.Clear();
        if (cortados.Count == 0) { Resumen = ""; _p.Status = "No hay nombres cortados en el ámbito elegido."; return; }

        // 1) Las etiquetas del propio archivo. Ni IA ni red: es lo que ya está dentro del archivo.
        var paraIa = new List<Track>();
        foreach (var t in cortados)
        {
            var conTags = NombreCortado.DesdeEtiquetas(t.FilePath, t.Artist ?? "", t.Title ?? "");
            if (conTags != null)
                Filas.Add(new CortadoFila(new Completado(t.FilePath, Path.GetFileNameWithoutExtension(t.FilePath),
                                                         conTags, OrigenCompletado.Etiquetas, "Estaba en las etiquetas del archivo")));
            else paraIa.Add(t);
        }
        var conEtiquetas = Filas.Count;

        // 2) Los que llegaron sin etiquetas útiles: la IA, y su propuesta se contrasta con el catálogo.
        var conIa = 0;
        if (UsarIa && _p.IaDisponible && paraIa.Count > 0)
        {
            _p.Engine.Ai.Reset();
            for (var i = 0; i < paraIa.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = paraIa[i];
                var ia = await _p.Engine.Ai.ParseAsync(Path.GetFileNameWithoutExtension(t.FilePath),
                                                       t.Artist ?? "", t.Title ?? "", _p.Modelo, ct);
                _p.Progress = 100.0 * (i + 1) / paraIa.Count;
                _p.Status = $"Consultando a la IA… {i + 1} de {paraIa.Count}";
                if (ia == null) continue;

                var completo = NombreCortado.DesdeIa(t.FilePath, ia.Artist, ia.Title, ia.Version);
                if (completo == null) continue;

                var (origen, detalle) = await ConfirmarAsync(ia.Artist, ia.Title, ct);
                Filas.Add(new CortadoFila(new Completado(t.FilePath, Path.GetFileNameWithoutExtension(t.FilePath),
                                                         completo, origen, detalle)));
                conIa++;
            }
        }

        Resumen = $"{cortados.Count} nombres cortados · {conEtiquetas} se completan con sus etiquetas · {conIa} con la IA";
        _p.Status = !UsarIa || !_p.IaDisponible
            ? $"{Resumen}. Sin IA solo se usan las etiquetas; los demás se quedan como están."
            : $"{Resumen}. Lo que sale de las etiquetas va marcado; lo de la IA, no.";
    });

    /// <summary>
    /// ¿Existe esa canción en el catálogo? No decide el nombre —lo cortado suele ser el editor, que
    /// ningún catálogo conoce— pero sí dice si la IA está hablando de una canción real.
    /// </summary>
    private async Task<(OrigenCompletado, string)> ConfirmarAsync(string artista, string titulo, CancellationToken ct)
    {
        if (!_p.Engine.Config.UseDeezer || titulo.Trim().Length == 0)
            return (OrigenCompletado.IaSinConfirmar, "La IA lo completó; no se ha contrastado con ningún catálogo");
        try
        {
            var hit = await _p.Engine.Deezer.SearchAsync(artista, titulo, false, false, 0, false, ct);
            return hit != null
                ? (OrigenCompletado.IaConfirmada, $"Deezer: {hit.Artist} - {hit.Title}")
                : (OrigenCompletado.IaSinConfirmar, "La IA lo completó; Deezer no encuentra esa canción");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (OrigenCompletado.IaSinConfirmar, "La IA lo completó; no se pudo consultar el catálogo"); }
    }

    [RelayCommand]
    private void MarcarTodas() { foreach (var f in Filas) f.Aceptar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Filas) f.Aceptar = false; }

    public IReadOnlyList<CortadoFila> Marcadas => Filas.Where(f => f.Aceptar && f.Propuesto.Trim().Length > 0).ToList();

    /// <summary>Renombra las marcadas. La confirmación la pide la vista.</summary>
    public Task RenombrarAsync() => _p.TrabajarAsync("Completando nombres…", async ct =>
    {
        var marcadas = Marcadas;
        if (marcadas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        _p.Engine.ReleaseAudio();
        var cambios = marcadas.Select(f => (f.Ruta, f.Propuesto.Trim())).ToList();
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => _p.Engine.RenombrarArchivos(cambios, progreso, ct));

        var hechos = new HashSet<string>(res.Hechos.Select(h => h.Origen), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Filas.Where(f => hechos.Contains(f.Ruta)).ToList()) Filas.Remove(f);
        if (res.Hechos.Count > 0)
        {
            await _p.Engine.Library.ScanAsync();
            _p.Engine.AvisarFichasCambiadas();
        }

        var texto = $"{res.Hechos.Count} nombres completados";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no ({res.Fallos[0]})";
        if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ El cambio NO quedó anotado y no se podrá deshacer: {res.UndoErr}"
            : res.Hechos.Count > 0 ? ". Se puede deshacer desde Enriquecer; si usas rekordbox, repara la colección en Ajustes." : ".";
        _p.Status = texto;
    });
}

// =====================================================================================================
// 4. Ordenar para mezclar
// =====================================================================================================

public sealed class PasoFila
{
    public PasoMezcla Paso { get; }
    public PasoFila(PasoMezcla p) => Paso = p;

    public int Posicion => Paso.Posicion;
    public string Artist => Paso.Track.Artist ?? "";
    public string Title => Paso.Track.Title ?? Paso.Track.FileName;
    public string Bpm => Paso.Track.Bpm > 0 ? Paso.Track.Bpm.ToString() : "";
    public string Camelot => Paso.Track.KeyCamelot;
    public string Energia => Paso.Ficha?.Energia is > 0 ? Paso.Ficha.Energia.ToString() : "";
    public string Transicion => Paso.Transicion;
    public bool Choca => Paso.Posicion > 1 && Paso.Tono == EncajeTono.Choque;
    public string FilePath => Paso.Track.FilePath;
}

public sealed partial class MezclaViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;
    public const int Maximo = 1000;

    public MezclaViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        RellenarColecciones();
    }

    public ObservableCollection<string> Colecciones { get; } = new();
    [ObservableProperty] private string? _coleccion;
    public ObservableCollection<PasoFila> Pasos { get; } = new();
    [ObservableProperty] private string _resumen = "";

    public void RellenarColecciones()
    {
        var actual = Coleccion;
        Colecciones.Clear();
        foreach (var c in _p.Engine.Colecciones.Todas) Colecciones.Add(c.Nombre);
        Coleccion = Colecciones.Contains(actual ?? "") ? actual : Colecciones.FirstOrDefault();
    }

    [RelayCommand]
    private Task OrdenarAsync() => _p.TrabajarAsync("Ordenando…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }
        var c = _p.Engine.Colecciones.Todas.FirstOrDefault(x => x.Nombre == Coleccion);
        if (c == null) { _p.Status = "Elige una colección. Se crean en Colecciones o con «Buscar con una frase»."; return; }

        var fichas = _p.FichasActuales();
        var canciones = _p.Engine.Library.Tracks
            .Select(t => (t, fichas.GetValueOrDefault(t.FilePath)))
            .Where(x => FiltroColeccion.Cumple(c, x.t, x.Item2))
            .ToList();
        if (canciones.Count == 0) { Pasos.Clear(); Resumen = ""; _p.Status = "La colección está vacía."; return; }
        if (canciones.Count > Maximo)
        {
            _p.Status = $"La colección tiene {canciones.Count} canciones; para ordenar una sesión el máximo es {Maximo}. Afina sus condiciones.";
            return;
        }

        var orden = await Task.Run(() => OrdenMezcla.Ordenar(canciones.Select(x => (x.t, x.Item2)).ToList()), ct);
        Pasos.Clear();
        foreach (var paso in orden) Pasos.Add(new PasoFila(paso));

        var transiciones = Math.Max(0, orden.Count - 1);
        int N(EncajeTono e) => orden.Skip(1).Count(x => x.Tono == e);
        Resumen = $"{orden.Count} canciones · {N(EncajeTono.Igual) + N(EncajeTono.Compatible)} de {transiciones} transiciones armónicas"
                  + $" · {N(EncajeTono.Choque)} choques de tono · {N(EncajeTono.Desconocido)} sin tonalidad";
        _p.Status = N(EncajeTono.Desconocido) > transiciones / 2
            ? Resumen + ". Muchas canciones no tienen tonalidad en las etiquetas: impórtala de rekordbox en Biblioteca."
            : Resumen + ".";
    });

    public void ExportarM3u(string destino)
    {
        if (Pasos.Count == 0) { _p.Status = "Ordena antes una colección."; return; }
        var err = PlaylistWriter.Write(destino, Pasos.Select(p =>
            new PlaylistItem(p.FilePath, p.Artist, p.Title, p.Paso.Track.DurationSeconds)));
        _p.Status = err.Length > 0 ? "No se pudo guardar la lista: " + err : $"Lista guardada en ese orden: {Path.GetFileName(destino)}";
    }
}
