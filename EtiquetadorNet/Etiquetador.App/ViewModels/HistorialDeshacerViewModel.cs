using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

/// <summary>Una ejecución en la lista del historial.</summary>
public sealed class FilaEjecucion
{
    public FilaEjecucion(EjecucionDeshacer e) => E = e;

    public EjecucionDeshacer E { get; }
    public string Fecha => HistorialDeshacerViewModel.Cuando(E.Fecha);
    public string Tipo => E.TipoTexto;
    public int Cambios => E.Cambios;

    /// <summary>
    /// La columna «Archivos»: archivos distintos, no registros. Un archivo con su registro corrector
    /// (una lista que el archivo guardó de otra forma) contaba dos veces (hallazgo interfaz-9).
    /// </summary>
    public int Archivos => E.Archivos;

    public int Renombrados => E.Renombrados;
    public string Estado => E.EstadoTexto;
}

/// <summary>
/// Un archivo de la ejecución elegida, con su casilla para deshacerlo solo a él. Puede llevar más de
/// un registro: el corrector que se anota cuando el archivo guarda una lista de otra forma va con su
/// registro, y marcar la fila los deshace juntos (ver <see cref="HistorialDeshacerViewModel.Agrupar"/>).
/// </summary>
public sealed partial class FilaCambio : ObservableObject
{
    public FilaCambio(CambioDeshacer c) : this(new[] { c }) { }

    public FilaCambio(IReadOnlyList<CambioDeshacer> registros)
    {
        if (registros.Count == 0) throw new ArgumentException("Una fila necesita al menos un registro.", nameof(registros));
        Registros = registros;
        C = registros[0];
    }

    /// <summary>El primer registro del archivo: de él salen el nombre, la carpeta y el estado.</summary>
    public CambioDeshacer C { get; }

    /// <summary>Todos los registros de este archivo en la ejecución, en el orden del manifiesto.</summary>
    public IReadOnlyList<CambioDeshacer> Registros { get; }

    /// <summary>Lo que hay que pasar a deshacer para esta fila: las huellas de todos sus registros.</summary>
    public IEnumerable<string> Huellas => Registros.Select(r => r.Huella);

    [ObservableProperty] private bool _marcada;

    /// <summary>Solo lo pendiente (y legible) se puede marcar: lo demás ya no está por deshacer.</summary>
    public bool Seleccionable => C.Estado == EstadoCambio.Pendiente && C.Legible;

    public string Archivo => C.Archivo;

    /// <summary>El nombre que tenía antes, solo si la ejecución lo cambió.</summary>
    public string NombreAnterior => Registros.FirstOrDefault(r => r.Renombra) is { } r ? r.ArchivoAntes : "";

    /// <summary>Lo que cambió, sin repetir los campos que llevan sus dos registros.</summary>
    public string Cambios => Registros.Count == 1 ? C.CamposTexto
        : string.Join(", ", Registros.SelectMany(r => r.Campos).Distinct(StringComparer.Ordinal).Select(HistorialDeshacer.NombreCampo)
                                     .Concat(Registros.Any(r => r.Renombra) ? new[] { "nombre" } : Array.Empty<string>()));

    public string Carpeta => Path.GetDirectoryName(C.Despues) ?? "";

    public string Estado => C.Estado switch
    {
        EstadoCambio.Pendiente => "Pendiente",
        EstadoCambio.Deshecho => "Deshecho",
        _ => "Dado por perdido",
    };
}

/// <summary>Lo que se va a deshacer, ya comprobado contra las ejecuciones posteriores, con los textos para confirmarlo.</summary>
/// <param name="Huellas">Lo que se pasa a <see cref="UndoEngine.DeshacerCambios"/> (null: todo lo pendiente).</param>
/// <param name="Motivo">Si no hay nada que se pueda deshacer, por qué; si no, "".</param>
public sealed record PlanDeshacer(EjecucionDeshacer Ejecucion, IReadOnlyCollection<string>? Huellas, int Cuantos,
                                  string Titulo, string Cuerpo, string Nota, string Aceptar, string Motivo)
{
    public bool Posible => Motivo.Length == 0;
}

/// <summary>
/// Diálogo «Historial de cambios» (E14/M14): las ejecuciones anotadas para deshacer, con su fecha,
/// tipo, número de archivos y estado; el detalle de la elegida con una casilla por archivo; y
/// deshacer la ejecución entera o solo las canciones marcadas.
///
/// Leer el historial, comprobar las ejecuciones posteriores y deshacer van fuera del hilo de la
/// interfaz (MEDIDO en una biblioteca real: 177 manifiestos, 15 MB). Deshacer lo hace quien abre el
/// diálogo (Enriquecer), para que lo revertido reciba el mismo trato que con «Deshacer última».
/// </summary>
public sealed partial class HistorialDeshacerViewModel : ObservableObject
{
    private readonly HistorialDeshacer _historial;
    private readonly Func<EjecucionDeshacer, IReadOnlyCollection<string>?, Task<string?>> _deshacer;
    private readonly Func<string, PendientesDescartados>? _darPorPerdidos;
    private IReadOnlyList<EjecucionDeshacer> _todas = Array.Empty<EjecucionDeshacer>();

    /// <param name="deshacer">Deshace los cambios elegidos (null: todos) y dice cómo ha ido; null si no se pudo empezar.</param>
    /// <param name="darPorPerdidos">Saca de la cola los pendientes de un manifiesto sin tocar archivos (<see cref="UndoEngine.DarPorPerdidos"/>).</param>
    public HistorialDeshacerViewModel(HistorialDeshacer historial,
                                      Func<EjecucionDeshacer, IReadOnlyCollection<string>?, Task<string?>> deshacer,
                                      Func<string, PendientesDescartados>? darPorPerdidos = null)
    {
        _historial = historial;
        _deshacer = deshacer;
        _darPorPerdidos = darPorPerdidos;
    }

    public ObservableCollection<FilaEjecucion> Ejecuciones { get; } = new();

    /// <summary>
    /// Los archivos de la ejecución elegida. Se sustituye la colección entera en vez de añadirlos uno
    /// a uno: MEDIDO en una biblioteca real, la importación de rekordbox tiene 14.948 registros, y
    /// cada alta suelta es un aviso a la tabla en el hilo de la interfaz.
    /// </summary>
    [ObservableProperty] private ObservableCollection<FilaCambio> _cambios = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeDeshacerEjecucion), nameof(PuedeDarPorPerdidos))]
    private FilaEjecucion? _seleccionada;

    /// <summary>
    /// Enseñar también lo ya deshecho o dado por perdido. Por defecto no: en una biblioteca con años
    /// de uso son la mayoría, y lo que se viene a buscar aquí es lo que aún se puede deshacer.
    /// </summary>
    [ObservableProperty] private bool _mostrarDeshechas;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeDeshacerEjecucion), nameof(PuedeDeshacerMarcadas), nameof(PuedeDarPorPerdidos))]
    private bool _ocupado;

    [ObservableProperty] private string _estado = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeDeshacerMarcadas))]
    private int _marcadas;

    public bool PuedeDeshacerEjecucion => !Ocupado && Seleccionada?.E.TienePendientes == true;
    public bool PuedeDeshacerMarcadas => !Ocupado && Marcadas > 0;
    public bool PuedeDarPorPerdidos => PuedeDeshacerEjecucion && _darPorPerdidos != null;

    partial void OnMostrarDeshechasChanged(bool value) => Filtrar(Seleccionada?.E.Id);

    /// <summary>
    /// Las lecturas del detalle que estén en marcha. Preparar, deshacer y dar por perdidos las
    /// esperan antes de empezar. POR QUÉ: elegir una ejecución lanza la lectura de su manifiesto
    /// sin esperarla, y visto en revisión, «Dar por perdidos» justo después fallaba de vez en cuando
    /// por uso compartido del archivo («The process cannot access the file…»); deshaciendo, el
    /// manifiesto no se habría podido reescribir tras revertir. Se encadenan (WhenAll) para no
    /// olvidar una lectura anterior si se eligen varias seguidas.
    /// </summary>
    private Task _cargaCambios = Task.CompletedTask;

    partial void OnSeleccionadaChanged(FilaEjecucion? value) => _cargaCambios = Task.WhenAll(_cargaCambios, CargarCambiosAsync(value));

    /// <summary>Lee (o relee) el historial y deja elegida <paramref name="elegir"/> si sigue en la lista.</summary>
    public async Task CargarAsync(string? elegir = null)
    {
        Ocupado = true;
        try
        {
            _todas = await Task.Run(_historial.Listar);
            Filtrar(elegir);
            if (Estado.Length == 0 || elegir == null) Estado = ResumenLista(_todas.Count, _todas.Count(e => e.TienePendientes));
        }
        catch (Exception e) { Estado = "No se ha podido leer el historial: " + e.Message; }
        finally { Ocupado = false; }
    }

    private void Filtrar(string? elegir)
    {
        Ejecuciones.Clear();
        foreach (var e in _todas.Where(e => MostrarDeshechas || e.TienePendientes)) Ejecuciones.Add(new FilaEjecucion(e));
        Seleccionada = Ejecuciones.FirstOrDefault(f => string.Equals(f.E.Id, elegir, StringComparison.OrdinalIgnoreCase))
                       ?? Ejecuciones.FirstOrDefault();
    }

    private async Task CargarCambiosAsync(FilaEjecucion? fila)
    {
        foreach (var c in Cambios) c.PropertyChanged -= AlCambiarMarca;
        Cambios = new ObservableCollection<FilaCambio>();
        Marcadas = 0;
        if (fila == null) return;
        List<FilaCambio> filas;
        try { filas = await Task.Run(() => Agrupar(_historial.Cambios(fila.E))); }
        catch (Exception e) { Estado = "No se han podido leer los cambios de esa ejecución: " + e.Message; return; }
        if (!ReferenceEquals(fila, Seleccionada)) return;   // se eligió otra mientras se leía
        foreach (var f in filas) f.PropertyChanged += AlCambiarMarca;
        Cambios = new ObservableCollection<FilaCambio>(filas);
    }

    /// <summary>
    /// Una fila por archivo y estado, en el orden del manifiesto. POR QUÉ (hallazgo interfaz-9):
    /// cuando un archivo guarda una lista de otra forma (en ID3v2.3, «Hip-Hop/Rap» se relee como dos
    /// géneros), quien escribe anota un segundo registro con lo que de verdad quedó, y el historial
    /// enseñaba ese archivo dos veces. Marcar solo la primera fila no devolvía nada: su valor ya no
    /// coincide con el del archivo y se daba por cambiado a mano. Ahora van en una fila y se deshacen
    /// a la vez.
    /// </summary>
    public static List<FilaCambio> Agrupar(IEnumerable<CambioDeshacer> cambios)
    {
        var grupos = new Dictionary<(EstadoCambio, string), List<CambioDeshacer>>();
        var orden = new List<List<CambioDeshacer>>();
        foreach (var c in cambios)
        {
            var clave = (c.Estado, HistorialDeshacer.ClaveArchivo(c).ToUpperInvariant());
            if (!grupos.TryGetValue(clave, out var g))
            {
                grupos[clave] = g = new List<CambioDeshacer>();
                orden.Add(g);
            }
            g.Add(c);
        }
        return orden.Select(g => new FilaCambio(g)).ToList();
    }

    /// <summary>El estado al abrir el historial, en singular o plural.</summary>
    public static string ResumenLista(int ejecuciones, int conPendientes)
        => ejecuciones == 0
            ? "Todavía no hay ningún cambio anotado."
            : $"{(ejecuciones == 1 ? "1 ejecución anotada" : $"{ejecuciones} ejecuciones anotadas")} · "
              + $"{conPendientes} con cambios que se pueden deshacer.";

    private bool _marcandoEnLote;

    private void AlCambiarMarca(object? sender, PropertyChangedEventArgs e)
    {
        if (_marcandoEnLote || e.PropertyName != nameof(FilaCambio.Marcada) || sender is not FilaCambio f || !f.Seleccionable) return;
        Marcadas += f.Marcada ? 1 : -1;   // sin recorrer la lista: con miles de filas, cada casilla la recorrería entera
    }

    [RelayCommand]
    private void MarcarTodas() => Marcar(true);

    [RelayCommand]
    private void DesmarcarTodas() => Marcar(false);

    /// <summary>Marca o desmarca todo lo pendiente y recuenta una sola vez.</summary>
    private void Marcar(bool valor)
    {
        _marcandoEnLote = true;
        try { foreach (var c in Cambios.Where(c => c.Seleccionable)) c.Marcada = valor; }
        finally { _marcandoEnLote = false; }
        Marcadas = Cambios.Count(c => c.Marcada && c.Seleccionable);
    }

    /// <summary>
    /// Comprueba qué se puede deshacer (la ejecución entera o solo las marcadas) mirando las
    /// ejecuciones posteriores, y prepara los textos de la confirmación. null si no hay nada elegido.
    /// </summary>
    public async Task<PlanDeshacer?> PrepararAsync(bool soloMarcadas)
    {
        await _cargaCambios;
        var fila = Seleccionada;
        if (fila == null || !fila.E.TienePendientes) { Estado = "Elige una ejecución con cambios pendientes."; return null; }
        IReadOnlyCollection<string>? huellas = null;
        if (soloMarcadas)
        {
            huellas = Cambios.Where(c => c.Marcada && c.Seleccionable).SelectMany(c => c.Huellas).ToList();
            if (huellas.Count == 0) { Estado = "Marca antes alguna canción pendiente."; return null; }
        }
        Ocupado = true;
        Estado = "Comprobando si alguna ejecución posterior tocó los mismos archivos…";
        try
        {
            var todas = _todas;
            var analisis = await Task.Run(() => _historial.Analizar(fila.E, huellas, todas));
            Estado = "";
            return Planificar(analisis, soloMarcadas);
        }
        catch (Exception e) { Estado = "No se ha podido comprobar el historial: " + e.Message; return null; }
        finally { Ocupado = false; }
    }

    /// <summary>Deshace lo planificado (ya confirmado) y vuelve a leer el historial.</summary>
    public async Task EjecutarAsync(PlanDeshacer plan)
    {
        if (!plan.Posible) { Estado = plan.Motivo; return; }
        Ocupado = true;
        Estado = plan.Cuantos == 1 ? "Deshaciendo 1 cambio…" : $"Deshaciendo {plan.Cuantos} cambios…";
        try
        {
            await _cargaCambios;
            var resultado = await _deshacer(plan.Ejecucion, plan.Huellas);
            Estado = resultado ?? "Hay otro proceso en curso en Enriquecer; espera a que termine.";
        }
        catch (Exception e) { Estado = "No se ha podido deshacer: " + e.Message; }
        finally { Ocupado = false; }
        await CargarAsync(plan.Ejecucion.Id);
    }

    /// <summary>
    /// Da por perdidos los pendientes de la ejecución elegida (ya confirmado): se archivan sin tocar
    /// ningún archivo y la ejecución deja de estar por deshacer.
    ///
    /// POR QUÉ AQUÍ. El aviso de un deshacer atascado (un archivo enviado a la papelera hace que
    /// «Deshacer última» vuelva siempre a esa ejecución) decía que se podían dar por perdidos, pero
    /// ninguna pantalla lo ofrecía: la única salida era borrar el manifiesto a mano.
    /// </summary>
    public async Task DarPorPerdidosAsync()
    {
        var fila = Seleccionada;
        if (_darPorPerdidos == null || fila?.E.Manifiesto is not { } manifiesto) return;
        Ocupado = true;
        try
        {
            await _cargaCambios;
            var r = await Task.Run(() => _darPorPerdidos(manifiesto));
            Estado = r.Ok
                ? $"{(r.Registros == 1 ? "1 cambio pendiente" : $"{r.Registros} cambios pendientes")} de {Nombre(fila.E)} "
                  + $"{(r.Registros == 1 ? "queda" : "quedan")} dado{(r.Registros == 1 ? "" : "s")} por perdido{(r.Registros == 1 ? "" : "s")}. No se ha tocado ningún archivo."
                : $"No se han podido dar por perdidos: {r.Error}";
        }
        catch (Exception e) { Estado = "No se han podido dar por perdidos: " + e.Message; }
        finally { Ocupado = false; }
        await CargarAsync(fila.E.Id);
    }

    /// <summary>Confirmación para dar por perdidos los pendientes de una ejecución.</summary>
    public static (string Titulo, string Cuerpo, string Nota, string Aceptar) ConfirmacionPerdidos(EjecucionDeshacer e)
    {
        var n = e.Pendientes;
        return ($"Dar por perdidos los cambios pendientes de {Nombre(e)}",
                $"{(n == 1 ? "1 cambio pendiente deja" : $"{n} cambios pendientes dejan")} de estar por deshacer. No se toca ningún archivo. "
                + "Sirve para los archivos que ya no van a volver (borrados o enviados a la papelera), que impiden que «Deshacer última» llegue a las ejecuciones anteriores.",
                "Si el archivo está en un disco desconectado, conéctalo y deshaz en lugar de darlo por perdido. Los renombrados de esta ejecución se siguen teniendo en cuenta al reparar la colección de rekordbox.",
                "Dar por perdidos");
    }

    // --- Textos ---

    /// <summary>Fecha de una ejecución en la lista: «09/10/2026 16:03».</summary>
    public static string Cuando(DateTime f) => f.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Nombre(EjecucionDeshacer e) => $"«{e.TipoTexto}» del {Cuando(e.Fecha)}";

    private static string Archivos(int n) => n == 1 ? "1 archivo" : $"{n} archivos";

    /// <summary>
    /// Cuántos archivos distintos hay en esos cambios. Los textos hablan de archivos, y un archivo
    /// puede llevar dos registros (el corrector de una lista; ver <see cref="Agrupar"/>).
    /// </summary>
    internal static int Distintos(IEnumerable<CambioDeshacer> cambios)
        => cambios.Select(HistorialDeshacer.ClaveArchivo).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static string Lista(IEnumerable<EjecucionDeshacer> ejecuciones)
    {
        var l = ejecuciones.ToList();
        var nombres = l.Take(3).Select(Nombre).ToList();
        var texto = nombres.Count switch
        {
            1 => nombres[0],
            2 => $"{nombres[0]} y {nombres[1]}",
            _ => string.Join(", ", nombres.Take(nombres.Count - 1)) + " y " + nombres[^1],
        };
        return l.Count > 3 ? texto + $" (y {l.Count - 3} más)" : texto;
    }

    /// <summary>
    /// Por qué hay archivos que no se pueden deshacer todavía, o "". Tres casos: una ejecución
    /// posterior lo renombró o lo movió (ya no está con el nombre que le dejó esta), otro archivo
    /// ocupa ahora ese nombre, u otro archivo ocupa el nombre que tenía antes (al que volvería). En
    /// los tres, lo coherente es deshacer antes la posterior.
    ///
    /// Salvo si lo que lo impide se dio por perdido: eso ya no se puede deshacer, y decir «deshaz
    /// antes esa ejecución» dejaba al usuario sin salida (visto en revisión). Entonces se explica
    /// que este cambio solo se puede dar por perdido también.
    /// </summary>
    public static string TextoBloqueados(IReadOnlyList<BloqueoDeshacer> bloqueados)
    {
        static string Parte(List<BloqueoDeshacer> grupo, bool perdido, Func<int, string, string> porque)
        {
            if (grupo.Count == 0) return "";
            var n = Distintos(grupo.Select(b => b.Cambio));
            var uno = n == 1;
            var posteriores = grupo.Select(b => b.Posterior).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                                   .Select(g => g.First()).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
            var cuales = $"{(uno ? "1 archivo no se puede" : $"{n} archivos no se pueden")} deshacer";
            if (!perdido)
                return $"{cuales} todavía: {porque(n, Lista(posteriores))}. "
                     + $"Para deshacer{(uno ? "lo" : "los")}, deshaz antes {(posteriores.Count == 1 ? "esa ejecución" : "esas ejecuciones")}.";
            return $"{cuales}: {porque(n, Lista(posteriores))}, pero "
                 + (uno ? "ese cambio se dio por perdido y ya no se puede deshacer. " : "esos cambios se dieron por perdidos y ya no se pueden deshacer. ")
                 + (uno ? "Este archivo solo se puede dar por perdido también" : "Estos archivos solo se pueden dar por perdidos también")
                 + ": usa «Dar por perdidos…» cuando no quede nada más que deshacer en esta ejecución.";
        }
        static string Renombrados(int n, string cual)
            => n == 1 ? $"después lo renombró o lo movió {cual}" : $"después los renombró o los movió {cual}";
        static string Ocupados(int n, string cual)
            => n == 1 ? $"otro archivo ocupa ahora el nombre que le dejó esta ejecución (lo llevó ahí {cual})"
                      : $"otros archivos ocupan ahora los nombres que les dejó esta ejecución (los llevó ahí {cual})";
        static string Anteriores(int n, string cual)
            => n == 1 ? $"otro archivo ocupa ahora el nombre que tenía antes, al que volvería (lo llevó ahí {cual})"
                      : $"otros archivos ocupan ahora los nombres que tenían antes, a los que volverían (los llevó ahí {cual})";

        var partes = new List<string>();
        foreach (var perdido in new[] { false, true })
        {
            var grupo = bloqueados.Where(b => b.Perdido == perdido).ToList();
            partes.Add(Parte(grupo.Where(b => !b.OtroArchivo).ToList(), perdido, Renombrados));
            partes.Add(Parte(grupo.Where(b => b.OtroArchivo && !b.NombreAnterior).ToList(), perdido, Ocupados));
            partes.Add(Parte(grupo.Where(b => b.OtroArchivo && b.NombreAnterior).ToList(), perdido, Anteriores));
        }
        return string.Join(" ", partes.Where(t => t.Length > 0));
    }

    /// <summary>
    /// Lo que no puede volver a su nombre anterior porque ya hay otro archivo con ese nombre en su
    /// carpeta y el historial no dice de dónde salió (<see cref="AnalisisDeshacer.NombreOcupado"/>), o "".
    /// </summary>
    public static string TextoNombreOcupado(IReadOnlyCollection<CambioDeshacer> ocupados)
    {
        var n = Distintos(ocupados);
        if (n == 0) return "";
        return n == 1
            ? "1 archivo no puede volver a su nombre anterior porque ya hay otro archivo con ese nombre en su carpeta: se queda pendiente. "
              + "Cambia de nombre o aparta ese otro archivo y vuelve a intentarlo."
            : $"{n} archivos no pueden volver a su nombre anterior porque ya hay otros archivos con esos nombres en sus carpetas: se quedan pendientes. "
              + "Cambia de nombre o aparta esos otros archivos y vuelve a intentarlo.";
    }

    /// <summary>
    /// Los textos de la confirmación, o el motivo para no deshacer nada. Lo bloqueado no se deshace
    /// nunca (se queda pendiente); lo que una posterior volvió a tocar se deshace, avisando antes de
    /// qué se conserva. Los números cuentan archivos, no registros.
    /// </summary>
    public static PlanDeshacer Planificar(AnalisisDeshacer a, bool soloMarcadas)
    {
        var e = a.Ejecucion;
        var bloqueo = TextoBloqueados(a.Bloqueados);
        var ocupado = TextoNombreOcupado(a.NombreOcupado);

        if (a.ADeshacer.Count == 0)
        {
            var motivo = a.Bloqueados.Count + a.NombreOcupado.Count > 0
                ? string.Join(" ", new[] { bloqueo, ocupado }.Where(t => t.Length > 0))
                : "No queda nada pendiente que deshacer en esta ejecución.";
            return new PlanDeshacer(e, null, 0, "", "", "", "", motivo);
        }

        // Archivos, no registros: un archivo con su registro corrector es uno solo (interfaz-9).
        var cuantos = Distintos(a.ADeshacer);
        var renombrados = a.ADeshacer.Where(c => c.Renombra).ToList();
        var renombran = Distintos(renombrados);
        var titulo = soloMarcadas
            ? $"Deshacer {(cuantos == 1 ? "1 canción" : $"{cuantos} canciones")} de {Nombre(e)}"
            : $"Deshacer {Nombre(e)}";
        var cuerpo = $"{(cuantos == 1 ? "1 archivo volverá" : $"{cuantos} archivos volverán")} a como estaba{(cuantos == 1 ? "" : "n")} antes de esa ejecución"
                   + (renombran > 0 ? $"; {renombran} {QueRecupera(renombrados, renombran == 1)}." : ".");

        if (bloqueo.Length > 0) cuerpo += "\n\n" + bloqueo + " Se quedan pendientes.";
        if (ocupado.Length > 0) cuerpo += "\n\n" + ocupado;

        if (a.Solapes.Count > 0)
        {
            var s = Distintos(a.Solapes.Select(x => x.Cambio));
            var compartidos = a.Solapes.Where(x => x.CamposCompartidos.Count > 0).ToList();
            var campos = compartidos.SelectMany(x => x.CamposCompartidos).Distinct().Select(HistorialDeshacer.NombreCampo);
            var posteriores = a.Solapes.SelectMany(x => x.Posteriores).GroupBy(p => p.Id).Select(g => g.First())
                                       .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
            cuerpo += $"\n\n{(s == 1 ? "1 de estos archivos se volvió" : $"{s} de estos archivos se volvieron")} a cambiar después en {Lista(posteriores)}.";
            if (compartidos.Count > 0)
                cuerpo += $" Lo que cambiaron las dos ({string.Join(", ", campos)}) se queda como lo dejó la posterior en {Archivos(Distintos(compartidos.Select(x => x.Cambio)))}.";
            cuerpo += TextoPierdeDeshacer(a.Solapes.Where(x => x.PierdeDeshacer).ToList());
            cuerpo += " Si vas a deshacer las dos, empieza por la más reciente.";
        }

        if (a.NoEncontrados.Count > 0)
        {
            var m = Distintos(a.NoEncontrados);
            cuerpo += $"\n\n{(m == 1 ? "1 archivo no aparece" : $"{m} archivos no aparecen")} en el disco, ni con el nombre que les dejó la ejecución ni con el anterior: se quedará{(m == 1 ? "" : "n")} pendiente{(m == 1 ? "" : "s")}.";
        }

        var nota = "Un campo que hayas cambiado después a mano no se toca. Lo que se deshaga deja de contar como aplicado y vuelve a proponerse al analizar."
                 + (renombran > 0 ? $" Si usas rekordbox, repara después la colección {RekordboxViewModel.DondeSeRepara}." : "");

        // La ejecución entera y sin nada apartado va como «todo»: así también se archivan las
        // líneas que no se dejan leer, igual que con «Deshacer última».
        var huellas = !soloMarcadas && a.Bloqueados.Count == 0 && a.NombreOcupado.Count == 0 ? null : a.Huellas;
        return new PlanDeshacer(e, huellas, cuantos, titulo, cuerpo, nota, $"Deshacer ({cuantos})", "");
    }

    /// <summary>Qué recuperan los archivos que la ejecución renombró o movió (ver <see cref="QueRecupera"/>).</summary>
    private enum Vuelta { Nombre, Carpeta, NombreYCarpeta, NombreOCarpeta }

    private static Vuelta VueltaDe(IReadOnlyCollection<CambioDeshacer> renombrados)
        => renombrados.All(c => c.CambiaCarpeta && !c.CambiaNombre) ? Vuelta.Carpeta
         : renombrados.All(c => !c.CambiaCarpeta) ? Vuelta.Nombre
         : renombrados.All(c => c.CambiaCarpeta && c.CambiaNombre) ? Vuelta.NombreYCarpeta
         : Vuelta.NombreOCarpeta;

    /// <summary>
    /// Lo que recuperan al deshacer los archivos que la ejecución renombró o movió: «recupera su nombre
    /// anterior», «vuelven a su carpeta anterior» o, si se mezclan, «recuperan su nombre o su carpeta
    /// anterior». Revisión 3: Organizar por género mueve miles de archivos sin cambiarles el nombre, y la
    /// confirmación decía que recuperaban su nombre.
    /// </summary>
    private static string QueRecupera(IReadOnlyCollection<CambioDeshacer> renombrados, bool uno) => VueltaDe(renombrados) switch
    {
        Vuelta.Carpeta => uno ? "vuelve a su carpeta anterior" : "vuelven a su carpeta anterior",
        Vuelta.Nombre => uno ? "recupera su nombre anterior" : "recuperan su nombre anterior",
        Vuelta.NombreYCarpeta => uno ? "recupera su nombre y su carpeta anteriores" : "recuperan su nombre y su carpeta anteriores",
        _ => uno ? "recupera su nombre o su carpeta anterior" : "recuperan su nombre o su carpeta anterior",
    };

    /// <summary>
    /// Qué pasa con las ejecuciones posteriores cuando esta devuelve un archivo a su nombre o a su
    /// carpeta anterior, o "" (<see cref="SolapeDeshacer.PierdeDeshacer"/>).
    ///
    /// POR QUÉ ASÍ (hallazgo datos-1). Antes la posterior se quedaba apuntando al nombre que le dejó
    /// esta ejecución: su registro salía como no encontrado y, si después aparecía OTRO archivo con
    /// ese nombre (la copia antigua recuperada de la papelera tras «Sustituir»), «Deshacer última» le
    /// devolvía las etiquetas de la descarga o le quitaba un volumen que nunca se le subió. Ahora
    /// UndoEngine pone al día el historial de la posterior para que siga al archivo hasta su nombre
    /// anterior, y eso es lo que se promete. Si ese historial no se deja escribir en ese momento, el
    /// archivo no se toca y se queda pendiente, con un aviso que dice cómo seguir.
    /// </summary>
    public static string TextoPierdeDeshacer(IReadOnlyList<SolapeDeshacer> pierden)
    {
        var n = Distintos(pierden.Select(x => x.Cambio));
        if (n == 0) return "";
        var uno = n == 1;
        var posteriores = pierden.SelectMany(x => x.Posteriores).Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var unaPosterior = posteriores == 1;
        var vuelta = VueltaDe(pierden.Select(x => x.Cambio).ToList());
        var aDonde = vuelta switch
        {
            Vuelta.Carpeta => "a su carpeta anterior",
            Vuelta.Nombre => "a su nombre anterior",
            Vuelta.NombreYCarpeta => "a su nombre y su carpeta anteriores",
            _ => "a su nombre o su carpeta anterior",
        };
        return $" {(uno ? "1 archivo vuelve" : $"{n} archivos vuelven")} {aDonde}, y el historial se pone al día para que "
             + (unaPosterior ? "la ejecución posterior " : "las ejecuciones posteriores ")
             + $"{(uno ? "lo" : "los")} {(unaPosterior ? "encuentre" : "encuentren")} {(vuelta == Vuelta.Nombre ? "con ese nombre" : "ahí")}: "
             + $"lo que {(unaPosterior ? "cambió" : "cambiaron")} en {(uno ? "él" : "ellos")} se queda y se podrá deshacer después.";
    }
}
