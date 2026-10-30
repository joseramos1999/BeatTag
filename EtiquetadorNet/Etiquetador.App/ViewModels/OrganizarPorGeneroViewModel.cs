using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>Una carpeta de destino en la vista previa.</summary>
/// <param name="Nombre">Desde la carpeta de la biblioteca («Musica/Por género/Pop»).</param>
public sealed record FilaDestinoOrganizar(string Carpeta, string Nombre, int Canciones, bool YaExiste)
{
    /// <summary>«1 canción», «12 canciones».</summary>
    public string CancionesTexto => OrganizarPorGenero.TextoCanciones(Canciones);

    public string Estado => YaExiste ? "Ya existe" : "Nueva";
}

/// <summary>
/// Una carpeta de origen en la vista previa. Desmarcarla deja donde están todas sus canciones (las de
/// esa carpeta; sus subcarpetas son filas aparte) y vuelve a calcular el plan.
/// </summary>
public sealed partial class FilaOrigenOrganizar : ObservableObject
{
    private readonly Action<FilaOrigenOrganizar> _alCambiar;
    private bool _poniendo;

    public string Carpeta { get; }

    /// <summary>Desde la carpeta de la biblioteca («Musica/WarmUp»).</summary>
    public string Nombre { get; }

    [ObservableProperty] private int _total;
    [ObservableProperty] private int _seMueven;
    [ObservableProperty] private int _seQuedan;
    [ObservableProperty] private string _reparto = "";
    [ObservableProperty] private bool _incluir = true;

    public FilaOrigenOrganizar(CarpetaOrigenPlan origen, string nombre, Action<FilaOrigenOrganizar> alCambiar)
    {
        Carpeta = origen.Carpeta;
        Nombre = nombre;
        _alCambiar = alCambiar;
        Poner(origen);
    }

    /// <summary>Las cifras de un plan nuevo, sin que cuente como un cambio del usuario.</summary>
    internal void Poner(CarpetaOrigenPlan origen)
    {
        _poniendo = true;
        try
        {
            Total = origen.Total;
            SeMueven = origen.SeMueven;
            SeQuedan = origen.SeQuedan;
            Reparto = origen.Excluida ? "Desmarcada: se queda entera" : origen.Reparto;
            Incluir = !origen.Excluida;
        }
        finally { _poniendo = false; }
    }

    partial void OnIncluirChanged(bool value)
    {
        if (!_poniendo) _alCambiar(this);
    }
}

/// <summary>
/// «Organizar por género» (página propia, en TU MÚSICA): cada canción pasa a una carpeta con el nombre
/// de su género, sin tocar sus etiquetas.
///
/// POR QUÉ UNA PÁGINA PROPIA Y NO UNA HERRAMIENTA DEL ASISTENTE. Mueve archivos de toda la biblioteca:
/// tiene que bloquear las demás páginas mientras dura, como la Bandeja, y necesita su vista previa y sus
/// avisos. El Asistente reúne herramientas que proponen cambios de etiquetas, a menudo con la IA.
///
/// La vista previa es obligatoria: MEDIDO en una biblioteca real, la etiqueta solo coincidía con el
/// género que nombra su carpeta en el 37 % de las canciones, y cada carpeta de origen se repartiría
/// entre 24 géneros de media. Se ve adónde va cada parte, se puede desmarcar cada carpeta de origen, y
/// no se mueve nada hasta confirmar. Antes de calcularla se escanea la biblioteca: en la misma
/// biblioteca la caché tenía 249 entradas de archivos que ya no existían y 297 audios sin escanear.
/// </summary>
public sealed partial class OrganizarPorGeneroViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina, IPaginaVisible
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    // La biblioteca con que se calculó la vista previa, leída en el hilo de la interfaz al calcularla:
    // desmarcar una carpeta o cambiar una opción vuelve a planear sobre ella, sin escanear otra vez.
    private IReadOnlyList<Track>? _canciones;
    private IReadOnlyList<string> _raices = Array.Empty<string>();
    private int _variantes, _sinGenero;
    // Los puntos de montaje, una vez por vista previa: con una unidad de red caída, preguntar por ellos
    // puede tardar segundos, y replanificar tras cambiar una opción tiene que ser inmediato.
    private IReadOnlyList<string>? _puntos;
    private readonly HashSet<string> _excluidos = new(ComparadorRutas.Instancia);
    private PlanOrganizar? _plan;
    private OpcionesOrganizar? _opcionesDelPlan;
    private int _version;

    // Cuántas veces ha avisado la biblioteca de un cambio y cuántas llevaba al leer la que usa la
    // vista previa. Solo «Calcular la vista previa» vuelve a leerla: replanificar tras cambiar una
    // opción o una casilla usa la misma lectura, así que NO puede dar por buena una vista previa
    // desfasada (revisión 1: una canción que pasó de Pop a Rock en otra pestaña acababa en «Pop»).
    private int _cambiosBiblioteca;
    private int _cambiosAlLeer = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeOrganizar), nameof(Libre))]
    private bool _isBusy;

    [ObservableProperty] private string _status;
    [ObservableProperty] private double _progress;

    /// <summary>Solo mover tiene avance que dar; escanear y calcular, no.</summary>
    [ObservableProperty] private bool _conAvance;

    bool IProgresoPagina.ConAvance => ConAvance;

    // --- Opciones (se guardan en la configuración) ---

    /// <summary>
    /// Dónde se crean las carpetas. Los tres botones de opción se enlazan con <see cref="DestinoPorGenero"/>,
    /// <see cref="DestinoRaiz"/> y <see cref="DestinoCarpeta"/>, que solo cuentan cuando se marcan: al
    /// pasar de uno a otro, el grupo desmarca el anterior en un orden que no se puede dar por sabido.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinoPorGenero), nameof(DestinoRaiz), nameof(DestinoCarpeta))]
    private DestinoOrganizar _destino;

    public bool DestinoPorGenero
    {
        get => Destino == DestinoOrganizar.PorGeneroEnCadaRaiz;
        set { if (value) Destino = DestinoOrganizar.PorGeneroEnCadaRaiz; }
    }

    public bool DestinoRaiz
    {
        get => Destino == DestinoOrganizar.EnLaRaiz;
        set { if (value) Destino = DestinoOrganizar.EnLaRaiz; }
    }

    public bool DestinoCarpeta
    {
        get => Destino == DestinoOrganizar.Carpeta;
        set { if (value) Destino = DestinoOrganizar.Carpeta; }
    }

    [ObservableProperty] private string _carpetaDestino;
    [ObservableProperty] private bool _conservarEstructura;
    [ObservableProperty] private bool _moverSinGenero;
    [ObservableProperty] private bool _dejarHerramientas;
    [ObservableProperty] private bool _agrupar;
    [ObservableProperty] private int _minimoGrupo;

    // --- Vista previa ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeOrganizar))]
    private bool _hayPlan;

    /// <summary>Se está volviendo a calcular el plan tras cambiar una opción o una casilla.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeOrganizar))]
    private bool _calculando;

    /// <summary>El plan que se ve ya no corresponde a la biblioteca o a las opciones: hay que calcularlo de nuevo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeOrganizar))]
    private bool _desfasado;

    [ObservableProperty] private string _resumen = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayAviso))]
    private string _aviso = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayRecomendacion))]
    private string _recomendacion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayResultado))]
    private string _resultado = "";

    [ObservableProperty] private string _tituloDestinos = "Carpetas de destino";
    [ObservableProperty] private string _tituloOrigenes = "Por carpeta de origen";
    [ObservableProperty] private string _tituloOmitidas = "Se omiten";

    // Se sustituyen enteras con un solo aviso a la tabla: «Se omiten» puede tener miles de filas, y
    // añadirlas una a una, en el hilo de la interfaz, avisaba a la tabla miles de veces.
    private readonly ListaEnBloque<FilaDestinoOrganizar> _destinos = new();
    private readonly ListaEnBloque<FilaOrigenOrganizar> _origenes = new();
    private readonly ListaEnBloque<OmisionOrganizar> _omitidas = new();

    public ObservableCollection<FilaDestinoOrganizar> Destinos => _destinos;
    public ObservableCollection<FilaOrigenOrganizar> Origenes => _origenes;
    public ObservableCollection<OmisionOrganizar> Omitidas => _omitidas;

    public bool HayAviso => Aviso.Length > 0;
    public bool HayRecomendacion => Recomendacion.Length > 0;
    public bool HayResultado => Resultado.Length > 0;
    public bool Libre => !IsBusy;

    /// <summary>Hay un plan al día con algo que mover.</summary>
    public bool PuedeOrganizar => HayPlan && !Desfasado && !Calculando && !IsBusy && (_plan?.Movimientos.Count ?? 0) > 0;

    /// <summary>El plan que se ve (null si no hay ninguno al día).</summary>
    public PlanOrganizar? Plan => HayPlan ? _plan : null;

    /// <summary>«Abrir rekordbox», tras organizar: lo atiende el shell, que lleva a esa página.</summary>
    public event Action? AbrirRekordboxPedido;

    public OrganizarPorGeneroViewModel(AppEngine engine)
    {
        _engine = engine;
        var c = engine.Config;
        _destino = c.OrganizarDestino switch
        {
            "raiz" => DestinoOrganizar.EnLaRaiz,
            "carpeta" => DestinoOrganizar.Carpeta,
            _ => DestinoOrganizar.PorGeneroEnCadaRaiz,
        };
        _carpetaDestino = c.OrganizarCarpeta ?? "";
        _conservarEstructura = c.OrganizarConservarEstructura;
        _moverSinGenero = c.OrganizarSinGenero;
        _dejarHerramientas = c.OrganizarDejarHerramientas;
        _agrupar = c.OrganizarAgruparMenosDe > 0;
        _minimoGrupo = c.OrganizarAgruparMenosDe > 0 ? c.OrganizarAgruparMenosDe : OrganizarPorGenero.AgruparPorDefecto;
        _status = "Pulsa «Calcular la vista previa» para ver adónde iría cada canción. No se mueve nada hasta que confirmes.";
        engine.Library.Changed += AlCambiarLaBiblioteca;
    }

    // --- Opciones ---

    partial void OnDestinoChanged(DestinoOrganizar value) => OpcionCambiada();
    partial void OnCarpetaDestinoChanged(string value) { if (Destino == DestinoOrganizar.Carpeta) OpcionCambiada(); }
    partial void OnConservarEstructuraChanged(bool value) => OpcionCambiada();
    partial void OnMoverSinGeneroChanged(bool value) => OpcionCambiada();
    partial void OnDejarHerramientasChanged(bool value) => OpcionCambiada();
    partial void OnAgruparChanged(bool value) => OpcionCambiada();
    partial void OnMinimoGrupoChanged(int value) { if (Agrupar) OpcionCambiada(); }

    /// <summary>Se guarda la opción y, si hay una vista previa, se vuelve a calcular con ella.</summary>
    private void OpcionCambiada()
    {
        var c = _engine.Config;
        c.OrganizarDestino = Destino switch
        {
            DestinoOrganizar.EnLaRaiz => "raiz",
            DestinoOrganizar.Carpeta => "carpeta",
            _ => "porgenero",
        };
        c.OrganizarCarpeta = CarpetaDestino ?? "";
        c.OrganizarConservarEstructura = ConservarEstructura;
        c.OrganizarSinGenero = MoverSinGenero;
        c.OrganizarDejarHerramientas = DejarHerramientas;
        c.OrganizarAgruparMenosDe = Agrupar ? Math.Max(2, MinimoGrupo) : 0;
        _engine.SaveConfig();
        if (_canciones != null && !IsBusy) _ = ReplanificarAsync();
    }

    /// <summary>
    /// La carpeta elegida para «En otra carpeta de la biblioteca…». Devuelve "" si vale (y queda
    /// elegida), o por qué no (ver <see cref="OrganizarPorGenero.ValidarDestino"/>), que queda en el estado.
    /// </summary>
    public string ElegirCarpeta(string carpeta)
    {
        var motivo = OrganizarPorGenero.ValidarDestino(carpeta, _engine.Library.EnabledPaths(), CarpetasDesmarcadas(), CarpetasConAjustes());
        if (motivo.Length > 0)
        {
            Status = motivo;
            return motivo;
        }
        CarpetaDestino = carpeta;
        Destino = DestinoOrganizar.Carpeta;
        return "";
    }

    private List<string> CarpetasDesmarcadas() => _engine.Library.Folders.Where(f => !f.Enabled).Select(f => f.Path).ToList();

    /// <summary>Las opciones tal como están, o null (con el motivo) si la carpeta de destino no vale.</summary>
    internal OpcionesOrganizar? Opciones(out string motivo)
    {
        motivo = "";
        var destino = Destino;
        if (destino == DestinoOrganizar.Carpeta)
        {
            motivo = OrganizarPorGenero.ValidarDestino(CarpetaDestino ?? "", _engine.Library.EnabledPaths(), CarpetasDesmarcadas(), CarpetasConAjustes());
            if (motivo.Length > 0) return null;
        }
        return new OpcionesOrganizar
        {
            Destino = destino,
            CarpetaDestino = destino == DestinoOrganizar.Carpeta ? CarpetaDestino ?? "" : "",
            ConservarEstructura = ConservarEstructura,
            MoverSinGenero = MoverSinGenero,
            DejarHerramientas = DejarHerramientas,
            AgruparMenosDe = Agrupar ? Math.Max(2, MinimoGrupo) : 0,
            CarpetasConAjustes = CarpetasConAjustes(),
            OrigenesExcluidos = _excluidos.ToList(),
        };
    }

    /// <summary>
    /// Las carpetas con ajustes propios, tal como están ahora en la configuración: fuera de ellas (o al
    /// entrar en ellas) una canción perdería o ganaría la exclusión de Duplicados o de Volumen, o la prioridad.
    /// </summary>
    private List<string> CarpetasConAjustes()
    {
        var c = _engine.Config;
        return c.ExcludedDupFolders.Concat(c.PriorityFolders).Concat(c.VolumenCarpetasExcluidas)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(ComparadorRutas.Instancia).ToList();
    }

    // --- Vista previa ---

    /// <summary>
    /// «Calcular la vista previa»: escanea la biblioteca y calcula el plan, todo fuera del hilo de la
    /// interfaz. Mientras tanto, el resto de la aplicación queda bloqueado: la biblioteca no puede
    /// cambiar debajo del plan.
    /// </summary>
    [RelayCommand]
    private async Task CalcularAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ConAvance = false;
        Progress = 0;
        Resultado = "";
        // La vista previa anterior deja de verse: si este cálculo se cancela o falla, no puede quedar
        // a la vista como si correspondiera a la biblioteca que se acaba de leer.
        LimpiarPlan();
        _version++;           // y un recálculo que estuviera en marcha ya no se enseña
        Calculando = false;
        Status = "Escaneando la biblioteca…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            // El escaneo de la biblioteca no se puede parar a mitad (es de todas las páginas): al
            // cancelar se deja de esperarlo, se suelta el bloqueo y el escaneo termina en segundo plano.
            await _engine.Library.ScanAsync().WaitAsync(ct);
            // Primero cuántos cambios lleva y después la lectura: un aviso que llegara entre las dos
            // desfasaría la vista previa sin motivo, nunca al revés.
            _cambiosAlLeer = Volatile.Read(ref _cambiosBiblioteca);
            var canciones = _engine.Library.Tracks.ToList();
            _raices = _engine.Library.EnabledPaths();
            Status = "Calculando la vista previa…";
            (_variantes, _sinGenero, _puntos) = await Task.Run(() => (GeneroPorReglas.VariantesSinUnificar(canciones), canciones.Count(GeneroPorReglas.SinGenero),
                                                                      Rutas.PuntosDeMontaje()), ct);
            _canciones = canciones;
            if (!await PlanearAsync(ct)) return;
            Status = Resumen;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LimpiarPlan();
            Status = "Cancelado: no se ha calculado la vista previa."
                   + (_engine.Library.IsScanning ? " La biblioteca termina de leerse en segundo plano." : "");
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Organizar por género: no se pudo calcular la vista previa", e);
            Status = "No se ha podido calcular la vista previa: " + e.Message;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Vuelve a planear sobre la misma biblioteca tras cambiar una opción o una casilla, en segundo plano.</summary>
    private async Task ReplanificarAsync()
    {
        try
        {
            if (await PlanearAsync()) Status = Resumen;
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Organizar por género: no se pudo recalcular la vista previa", e);
            Status = "No se ha podido recalcular la vista previa: " + e.Message;
        }
    }

    /// <summary>Calcula el plan con las opciones de ahora y lo enseña. false si no se pudo, o si otro más reciente lo ha sustituido.</summary>
    private async Task<bool> PlanearAsync(CancellationToken ct = default)
    {
        if (_canciones is not { } canciones) return false;
        var op = Opciones(out var motivo);
        if (op == null)
        {
            if (HayPlan) Desfasado = true;
            Aviso = motivo;
            Status = motivo;
            return false;
        }
        var version = ++_version;
        var raices = _raices;
        Calculando = true;
        try
        {
            var puntos = _puntos ?? Array.Empty<string>();
            var plan = await Task.Run(() => OrganizarPorGenero.Planear(canciones, raices, op, new DiscoReal(puntos), ct), ct);
            if (version != _version) return false;
            Mostrar(plan, op);
            return true;
        }
        finally
        {
            if (version == _version) Calculando = false;
        }
    }

    private void Mostrar(PlanOrganizar plan, OpcionesOrganizar op)
    {
        _plan = plan;
        _opcionesDelPlan = op;
        HayPlan = true;
        // Un plan nuevo con las opciones de ahora, pero sobre la biblioteca que se leyó al calcular: si
        // desde entonces ha cambiado, sigue desfasado.
        var desfase = MotivoDesfase();
        Desfasado = desfase.Length > 0;
        Aviso = desfase;
        Resumen = OrganizarPorGenero.Resumen(plan);
        Recomendacion = _variantes > 0 || _sinGenero > 0 ? Recomendar : "";
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(PuedeOrganizar));

        _destinos.Reemplazar(plan.PorDestino.Select(d => new FilaDestinoOrganizar(d.Carpeta, NombreEnLaBiblioteca(d.Carpeta), d.Canciones, d.YaExiste)));

        // Las filas de origen se ponen al día en su sitio si son las mismas carpetas: así no se pierde la
        // posición de la tabla al desmarcar una.
        var porCarpeta = Origenes.ToDictionary(o => o.Carpeta, ComparadorRutas.Instancia);
        if (porCarpeta.Count == plan.PorOrigen.Count && plan.PorOrigen.All(o => porCarpeta.ContainsKey(o.Carpeta)))
            foreach (var o in plan.PorOrigen) porCarpeta[o.Carpeta].Poner(o);
        else _origenes.Reemplazar(plan.PorOrigen.Select(o => new FilaOrigenOrganizar(o, NombreEnLaBiblioteca(o.Carpeta), OrigenCambiado)));

        _omitidas.Reemplazar(plan.Omitidas);

        TituloDestinos = $"Carpetas de destino ({N(plan.Carpetas)})";
        TituloOrigenes = $"Por carpeta de origen ({N(plan.PorOrigen.Count)})";
        TituloOmitidas = $"Se omiten ({N(plan.Omitidas.Count)})";
    }

    private void OrigenCambiado(FilaOrigenOrganizar fila)
    {
        if (fila.Incluir) _excluidos.Remove(fila.Carpeta);
        else _excluidos.Add(fila.Carpeta);
        if (_canciones != null && !IsBusy) _ = ReplanificarAsync();
    }

    /// <summary>Una carpeta desde la de la biblioteca que la contiene («Musica/Por género/Pop»), o entera si no está en ninguna.</summary>
    private string NombreEnLaBiblioteca(string carpeta)
    {
        var raiz = _raices.Where(r => Rutas.MismaCarpeta(carpeta, r) || Rutas.EstaDentroDe(carpeta, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (raiz == null) return carpeta;
        var limpia = raiz.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nombre = GeneroPorReglas.NombreCarpeta(raiz);
        var debajo = carpeta.Length > limpia.Length ? carpeta[limpia.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "";
        return debajo.Length == 0 ? nombre : Path.Combine(nombre, debajo);
    }

    /// <summary>
    /// La biblioteca ha cambiado por otro lado (otra página, el escaneo de fondo, una carpeta
    /// desmarcada): el plan ya no vale hasta volver a calcularlo. Se cuenta también mientras se
    /// calcula o se mueve: lo que llegue después de leer la biblioteca desfasa lo calculado.
    /// </summary>
    private void AlCambiarLaBiblioteca()
    {
        Interlocked.Increment(ref _cambiosBiblioteca);
        if (IsBusy || !HayPlan) return;
        Desfasado = true;
        Aviso = AvisoBibliotecaCambiada;
    }

    /// <summary>
    /// Por qué la vista previa que se ve ya no corresponde a lo que hay ahora, o "" si sigue al día:
    /// la biblioteca ha cambiado desde que se leyó, se han marcado o desmarcado carpetas de la
    /// biblioteca, o han cambiado las carpetas con ajustes propios de Duplicados o de Volumen (eso no
    /// avisa a nadie: se compara con las que usó el plan).
    /// </summary>
    private string MotivoDesfase()
    {
        if (Volatile.Read(ref _cambiosBiblioteca) != _cambiosAlLeer) return AvisoBibliotecaCambiada;
        if (!MismasCarpetas(_raices, _engine.Library.EnabledPaths())) return AvisoCarpetasCambiadas;
        if (_opcionesDelPlan != null && !MismasCarpetas(_opcionesDelPlan.CarpetasConAjustes, CarpetasConAjustes())) return AvisoAjustesCambiados;
        return "";
    }

    private static bool MismasCarpetas(IEnumerable<string> a, IEnumerable<string> b)
        => new HashSet<string>(a.Select(c => c.TrimEnd('\\', '/')), ComparadorRutas.Instancia)
               .SetEquals(b.Select(c => c.TrimEnd('\\', '/')));

    /// <summary>
    /// Mira si la vista previa sigue al día (antes de pedir la confirmación, al volver a la página y
    /// antes de mover) y, si no, la marca como desfasada con el motivo. Devuelve si se puede organizar.
    /// </summary>
    public bool ComprobarVistaPrevia()
    {
        if (!HayPlan) return false;
        var desfase = MotivoDesfase();
        if (desfase.Length > 0)
        {
            Desfasado = true;
            Aviso = desfase;
        }
        return PuedeOrganizar;
    }

    /// <summary>Por qué no se puede organizar ahora, para decirlo en el estado (nunca se calla).</summary>
    private string PorQueNoSePuedeOrganizar()
        => !HayPlan ? "Calcula la vista previa antes de organizar."
         : Aviso.Length > 0 ? Aviso
         : Calculando ? "La vista previa se está recalculando: espera a que termine y vuelve a pulsar «Organizar»."
         : IsBusy ? "Hay un proceso en marcha: espera a que termine."
         : "La vista previa no tiene ninguna canción que mover.";

    /// <summary>La página se ve o deja de verse: al volver a ella se mira si la vista previa sigue al día.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value && !IsBusy) ComprobarVistaPrevia();
        }
    }

    private bool _visible;

    private void LimpiarPlan()
    {
        _plan = null;
        _opcionesDelPlan = null;
        _canciones = null;
        HayPlan = false;
        Desfasado = false;
        Aviso = "";
        Resumen = "";
        Recomendacion = "";
        _destinos.Reemplazar(Array.Empty<FilaDestinoOrganizar>());
        _origenes.Reemplazar(Array.Empty<FilaOrigenOrganizar>());
        _omitidas.Reemplazar(Array.Empty<OmisionOrganizar>());
        TituloDestinos = "Carpetas de destino";
        TituloOrigenes = "Por carpeta de origen";
        TituloOmitidas = "Se omiten";
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(PuedeOrganizar));
    }

    // --- Organizar ---

    /// <summary>
    /// Mueve lo que dice la vista previa (la vista ya ha pedido confirmación), fuera del hilo de la
    /// interfaz y con el resto de la aplicación bloqueado. Después vuelve a escanear la biblioteca, que
    /// es rápido porque lo leído ya ha seguido a cada archivo, y deja el resumen.
    /// </summary>
    public async Task OrganizarAsync()
    {
        // Entre abrir la confirmación y pulsar «Organizar» la vista previa puede haberse desfasado (por
        // ejemplo, termina la comprobación de la biblioteca en segundo plano): no se mueve nada y se
        // dice por qué.
        if (!ComprobarVistaPrevia() || _plan is not { } plan)
        {
            Status = PorQueNoSePuedeOrganizar();
            return;
        }
        IsBusy = true;
        ConAvance = true;
        Progress = 0;
        Resultado = "";
        Status = plan.Movimientos.Count == 1 ? "Moviendo 1 canción…" : $"Moviendo {N(plan.Movimientos.Count)} canciones…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progreso = new Progress<double>(p => Progress = p);
        ResultadoOrganizar? res = null;
        try
        {
            _engine.ReleaseAudio();   // el archivo que suena no se puede mover
            _engine.Logger.Head($"Organizar por género: {plan.Movimientos.Count} traslado(s) previstos.");
            res = await Task.Run(() => _engine.OrganizarPorGenero(plan, progreso, ct));
            ConAvance = false;
            if (res.Movidas > 0)
            {
                Status = "Volviendo a leer la biblioteca…";
                await _engine.Library.ScanAsync();
            }
            var carpetas = res.Hechos.Select(h => Path.GetDirectoryName(h.Destino) ?? "").Distinct(ComparadorRutas.Instancia).Count();
            Resultado = TextoResultado(res, carpetas);
            Status = Resultado;
        }
        catch (Exception e)
        {
            _engine.Logger.Error("Error al organizar por género", e);
            Status = "Error al organizar por género: " + e.Message;
        }
        finally
        {
            IsBusy = false;
            ConAvance = false;
            Progress = 0;
            _cts.Dispose();
            _cts = null;
            // Lo calculado ya no vale: los archivos han cambiado de sitio.
            if (res != null) LimpiarPlan();
        }
    }

    /// <summary>Para lo que esté en marcha entre un archivo y el siguiente (también al cerrar la aplicación).</summary>
    public void Cancelar()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    private void CancelarProceso() => Cancelar();

    [RelayCommand]
    private void AbrirRekordbox() => AbrirRekordboxPedido?.Invoke();

    // --- Textos ---

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private static string N(int n) => n.ToString("N0", Es);

    public const string AvisoBibliotecaCambiada = "La biblioteca ha cambiado desde la vista previa: vuelve a calcularla antes de organizar.";

    public const string AvisoCarpetasCambiadas =
        "Las carpetas marcadas de la biblioteca han cambiado desde la vista previa: vuelve a calcularla antes de organizar.";

    public const string AvisoAjustesCambiados =
        "Las carpetas con ajustes propios de Duplicados o de Volumen han cambiado desde la vista previa: vuelve a calcularla antes de organizar.";

    /// <summary>La recomendación de la vista previa si hay géneros sin unificar o canciones sin género.</summary>
    public const string Recomendar =
        "Antes de organizar, pasa «Unificar géneros» y «Género por carpeta y artista» del Asistente: las carpetas se crean con el género que tenga cada canción.";

    public const string AvisoRekordbox =
        "rekordbox guarda los cue points ligados a la ruta: después, abre la página rekordbox y repara las rutas en «Rutas rotas», o reubica las pistas desde el propio rekordbox, porque el nombre de cada archivo no cambia.";

    public const string AvisoOtrosProgramas =
        "Serato, Traktor, Engine DJ y VirtualDJ no se actualizan solos: usa su opción para localizar archivos movidos.";

    public const string AvisoListas = "Las listas M3U8 y XML exportadas antes conservan las rutas anteriores: vuelve a exportarlas.";

    public const string AvisoEnriquecer = "Si hay propuestas pendientes en Enriquecer, vuelve a analizar: se calcularon con la ruta anterior de cada archivo.";

    public const string AvisoDeshacer = "Se puede deshacer desde Enriquecer («Deshacer última») o desde el historial de cambios.";

    /// <summary>
    /// El historial no deshace un cambio de un archivo que una ejecución posterior movió (deshacer lo
    /// buscaría donde ya no está): con miles de canciones movidas, deshacer un ajuste de volumen de ayer
    /// exigiría deshacer antes la organización entera. Se avisa antes de mover (revisión 1).
    /// </summary>
    public const string AvisoEjecucionesAnteriores =
        "Las ejecuciones anteriores que sigan pendientes en el historial (por ejemplo, un ajuste de volumen) solo se podrán deshacer en las canciones movidas después de deshacer esta organización.";

    /// <summary>El título, el cuerpo y la nota del diálogo de confirmación.</summary>
    public static (string Titulo, string Cuerpo, string Nota) TextosConfirmacion(PlanOrganizar plan)
    {
        var nuevas = plan.CarpetasNuevas;
        var conEllas = plan.Movimientos.Count == 1 ? "Con ella" : "Con ellas";
        var cuerpo = OrganizarPorGenero.TextoMovimientos(plan.Movimientos.Count, plan.Carpetas)
                   + (nuevas == 0 ? "" : nuevas == 1 ? " (1 se crea)" : $" ({N(nuevas)} se crean)")
                   + ". " + (plan.Asociados == 0 ? ""
                             : plan.Asociados == 1 ? $"{conEllas} va 1 archivo asociado por el nombre (.asd de Ableton Live, .cue o .lrc). "
                             : $"{conEllas} van {N(plan.Asociados)} archivos asociados por el nombre (.asd de Ableton Live, .cue o .lrc). ")
                   + (plan.Omitidas.Count == 0 ? "" : "Las demás canciones se quedan donde están. ")
                   + "Ningún archivo se sobrescribe ni se renombra, y las etiquetas no se tocan.";
        var nota = string.Join(" ", AvisoRekordbox, AvisoOtrosProgramas, AvisoListas, AvisoEnriquecer, AvisoDeshacer, AvisoEjecucionesAnteriores);
        return ("Organizar por género", cuerpo, nota);
    }

    /// <summary>Lo que se dice al terminar.</summary>
    /// <param name="carpetas">A cuántas carpetas distintas han ido las canciones movidas.</param>
    public static string TextoResultado(ResultadoOrganizar res, int carpetas)
    {
        var texto = res.Movidas switch
        {
            0 => "No se ha movido ninguna canción",
            1 => "Se ha movido 1 canción a 1 carpeta",
            _ => $"Se han movido {N(res.Movidas)} canciones a " + (carpetas == 1 ? "1 carpeta" : $"{N(carpetas)} carpetas"),
        };
        if (res.Cancelado && res.Detenido.Length == 0) texto = "Cancelado. " + texto + "; el resto se queda donde estaba";
        texto += ".";
        if (res.Asociados > 0)
            texto += res.Asociados == 1
                ? " También se ha movido 1 archivo asociado (.asd, .cue o .lrc) con su canción."
                : $" También se han movido {N(res.Asociados)} archivos asociados (.asd, .cue o .lrc) con sus canciones.";
        if (res.Fallos.Count > 0)
            texto += res.Fallos.Count == 1
                ? $" 1 no se ha podido mover ({res.Fallos[0]})."
                : $" {N(res.Fallos.Count)} no se han podido mover (por ejemplo, {res.Fallos[0]}).";
        if (res.Detenido.Length > 0) texto += " " + res.Detenido;
        if (res.UndoErr.Length > 0) texto += " ⚠ " + res.UndoErr;
        if (res.Movidas > 0)
            texto += " " + AvisoDeshacer + " Si usas rekordbox, repara las rutas en la página rekordbox («Rutas rotas»).";
        return texto;
    }
}
