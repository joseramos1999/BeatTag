using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción comprobada: lo que dice el archivo frente a lo que resultó ser el audio.</summary>
public sealed partial class IdentifyRow : ObservableObject
{
    private static readonly IBrush RojoBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xDC, 0x26, 0x26));
    private static readonly IBrush VerdeBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A));
    private static readonly IBrush GrisBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0x9C, 0xA3, 0xAF));

    public string FileName { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";

    /// <summary>Null mientras no se haya consultado esta canción.</summary>
    public VeredictoId? Veredicto { get; init; }

    /// <summary>Explicación en lenguaje llano de por qué está como está.</summary>
    public string Motivo { get; init; } = "";

    /// <summary>Lo que el audio resultó ser, para poder corregir el archivo a partir de ahí.</summary>
    public string AudioArtist { get; init; } = "";
    public string AudioTitle { get; init; } = "";

    /// <summary>
    /// Qué motor la reconoció. Se enseña porque es el dato que dice si merece la pena pagar: si
    /// casi todo lo resuelve el gratuito, el de pago sobra.
    /// </summary>
    public string Motor { get; init; } = "";

    /// <summary>
    /// El usuario ya miró este aviso y dijo que la canción está bien. Deja de contar como
    /// discrepancia: el rojo tiene que significar «esto está sin resolver», y un aviso ya revisado
    /// no lo está.
    /// </summary>
    public bool Descartada { get; init; }

    /// <summary>Lo identificado, en una sola celda.</summary>
    public string Audio => AudioArtist.Length > 0 && AudioTitle.Length > 0
        ? $"{AudioArtist} – {AudioTitle}"
        : AudioTitle.Length > 0 ? AudioTitle : AudioArtist;

    /// <summary>La única fila que pide una acción. Es lo que se pinta en rojo.</summary>
    public bool NoCoincide => Veredicto == VeredictoId.Difiere && !Descartada;

    public string Estado => Descartada ? "Descartada" : Veredicto switch
    {
        VeredictoId.Difiere => "No coincide",
        VeredictoId.Coincide => "Correcta",
        VeredictoId.SinIdentificar => "No reconocida",
        VeredictoId.Excluida => "No se comprueba",
        _ => "Sin comprobar",
    };

    public IBrush StateBrush => Descartada ? GrisBrush : Veredicto switch
    {
        VeredictoId.Difiere => RojoBrush,
        VeredictoId.Coincide => VerdeBrush,
        _ => GrisBrush,
    };
}

/// <summary>
/// Pestaña «Comprobar audio»: escucha cada canción y dice si es de verdad lo que su nombre afirma.
///
/// Responde a una pregunta que ninguna otra pestaña responde. Enriquecer parte del NOMBRE del
/// archivo y busca ese nombre en los catálogos: si el nombre está mal, el resultado estará mal con
/// mucha seguridad y toda la coherencia del mundo. Aquí se parte del AUDIO, que es el único dato
/// que no miente, y se contrasta contra lo que el archivo dice ser.
///
/// Solo se señala en rojo lo que NO cuadra. Lo demás -correcto, no reconocido, no comprobable- se
/// deja a la vista pero apagado: la lista es útil en la medida en que el rojo signifique siempre lo
/// mismo y siempre haya que hacer algo con él. Para revisar de un tirón está «Mostrar solo las que
/// no coinciden», que deja en pantalla exactamente esas.
///
/// El motor de pago GASTA DINERO: cada canción que llega hasta él es una consulta que se cobra. De
/// ahí que se cuente por adelantado el techo, que la respuesta se guarde para siempre y que nada se
/// consulte dos veces.
/// </summary>
public partial class IdentifyViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Las filas calculadas. Lista normal y no colección observable a propósito: la tabla se rehace
    /// entera de golpe, así que avisar de cada alta por separado solo cuesta tiempo.
    /// </summary>
    private List<IdentifyRow> _filas = new();

    /// <summary>Lo que ve la rejilla. Se reemplaza al recomponer, en un solo aviso.</summary>
    [ObservableProperty] private DataGridCollectionView _rowsView = ConstruirVista(new List<IdentifyRow>());

    /// <summary>
    /// Se está rehaciendo la tabla. Mueve el aviso de «cargando» que tapa la rejilla: entrar en
    /// esta pestaña con una biblioteca grande tarda un momento, y sin decirlo parece que la
    /// aplicación se ha colgado.
    /// </summary>
    [ObservableProperty] private bool _cargando;

    /// <summary>
    /// Los recálculos se ponen en COLA, no se descartan. Descartar el segundo parecía más barato,
    /// pero dejaba la tabla mostrando el estado anterior: si al descartar un aviso ya había un
    /// recálculo en vuelo, el nuevo se tiraba y el aviso descartado seguía en pantalla.
    /// </summary>
    private readonly SemaphoreSlim _turnoRecomponer = new(1, 1);

    [ObservableProperty] private IdentifyRow? _selectedRow;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _soloDiferencias;
    [ObservableProperty] private string _busqueda = "";
    [ObservableProperty] private string _filtroInfo = "";
    [ObservableProperty] private string _seleccionadasInfo = "";
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _status =
        "Pulsa Comprobar para averiguar, escuchando el audio, si cada canción es lo que su nombre dice.";

    /// <summary>
    /// Permiso explícito para usar el motor de PAGO con lo que el gratuito no reconozca. Viene
    /// apagado: una pestaña no enciende sola algo que cuesta dinero.
    /// </summary>
    [ObservableProperty] private bool _usarPago;

    /// <summary>
    /// Tope de consultas de pago por pasada. Trescientas por defecto, que es justo lo que AudD
    /// regala al registrarse: con el valor de fábrica es imposible gastar sin haberlo subido a mano.
    /// </summary>
    [ObservableProperty] private int _maxConsultasPago = 300;

    private IReadOnlyList<IdentifyRow> _seleccion = Array.Empty<IdentifyRow>();

    public IdentifyViewModel(AppEngine engine)
    {
        _engine = engine;
        // Solo se rehace sola si YA se está usando esta pestaña. Recorrer la biblioteca entera cada
        // vez que otra pestaña la reescanea es trabajo tirado: mientras la tabla esté vacía no hay
        // nada que refrescar, y se rellena al entrar aquí (lo hace el shell).
        _engine.Library.Changed += () => { if (!IsBusy && _filas.Count > 0) _ = RecomponerAsync(); };
    }

    // --- Filtros de la tabla ---

    partial void OnBusquedaChanged(string value) => AplicarFiltro();
    partial void OnSoloDiferenciasChanged(bool value) => AplicarFiltro();

    private void AplicarFiltro()
    {
        var texto = Busqueda.Trim();
        RowsView.Filter = texto.Length == 0 && !SoloDiferencias
            ? null
            : o => o is IdentifyRow r
                   && (!SoloDiferencias || r.NoCoincide)
                   && BusquedaTexto.Coincide(texto, r.FileName, r.Audio, r.Folder);
        RowsView.Refresh();
        FiltroInfo = RowsView.Count == _filas.Count ? "" : $"{RowsView.Count} de {_filas.Count}";
    }

    /// <summary>Filas seleccionadas: el DataGrid de Avalonia no permite enlazar SelectedItems.</summary>
    public void SetSelection(IEnumerable<IdentifyRow> filas)
    {
        _seleccion = filas.ToList();
        SeleccionadasInfo = _seleccion.Count > 1 ? $"{_seleccion.Count} seleccionadas" : "";
    }

    private List<IdentifyRow> Objetivo()
        => _seleccion.Count > 0 ? _seleccion.ToList()
         : SelectedRow != null ? new List<IdentifyRow> { SelectedRow }
         : new List<IdentifyRow>();

    // --- Lo que hay que saber ANTES de gastar ---

    /// <summary>
    /// Canciones que se enviarían a comprobar: las que no están excluidas y no tienen ya respuesta
    /// guardada. Es lo que se le enseña al usuario antes de empezar, porque es lo que va a pagar.
    /// </summary>
    public List<string> PorConsultar(bool force)
        => _engine.Library.Tracks
            .Select(t => t.FilePath)
            .Where(p => !Identificacion.SeExcluye(p, out _))
            .Where(p => force || !_engine.Identificacion.Consultado(p, UsarPago))
            .ToList();

    /// <summary>Hay motor gratuito: AcoustID solo necesita una clave, que no cuesta nada.</summary>
    public bool HayGratuito => _engine.Config.AcoustIdKey.Trim().Length > 0;

    /// <summary>Hay motor de pago disponible (otra cosa es que el usuario lo active).</summary>
    public bool HayPago => _engine.Config.AuddToken.Trim().Length > 0;

    /// <summary>
    /// El motor de pago va a hacer algo de verdad. Con el tope a cero está activado pero no puede
    /// gastar ni una consulta, así que no cuenta: dejarlo contar permitía confirmar una pasada que
    /// el escáner rechazaba después por no tener ningún motor con el que trabajar.
    /// </summary>
    public bool PagoEfectivo => HayPago && UsarPago && MaxConsultasPago > 0;

    /// <summary>Se puede comprobar algo. Sin ninguna de las dos claves no hay nada que hacer aquí.</summary>
    public bool HayAlgunMotor => HayGratuito || PagoEfectivo;

    partial void OnUsarPagoChanged(bool value)
    {
        Avisar();
        // Activar el de pago abre de nuevo las que el gratuito había dado por perdidas.
        if (_filas.Count > 0) _ = RecomponerAsync();
    }

    partial void OnMaxConsultasPagoChanged(int value) => Avisar();

    private void Avisar()
    {
        OnPropertyChanged(nameof(PagoEfectivo));
        OnPropertyChanged(nameof(HayAlgunMotor));
        OnPropertyChanged(nameof(MotoresInfo));
    }

    /// <summary>Qué se va a usar en la próxima pasada, dicho en una línea.</summary>
    public string MotoresInfo =>
        !HayGratuito && !HayPago ? "Sin claves configuradas: hacen falta AcoustID (gratuita) o AudD (de pago), en Ajustes."
        : !HayGratuito && HayPago && UsarPago && MaxConsultasPago <= 0
            ? "El máximo de consultas de pago está en 0, así que AudD no puede consultar nada y no hay motor gratuito. Sube el máximo o añade la clave de AcoustID en Ajustes."
        : HayGratuito && PagoEfectivo ? $"Se usará AcoustID (gratis) y, para lo que no reconozca, AudD (máximo {MaxConsultasPago} consultas de pago)."
        : HayGratuito && HayPago && UsarPago ? "Se usará solo AcoustID: el máximo de consultas de pago está en 0, así que AudD no llegará a consultar nada."
        : HayGratuito ? "Se usará solo AcoustID, que es gratuito. No se gastará nada."
        : PagoEfectivo ? $"Solo AudD, que es de pago (máximo {MaxConsultasPago} consultas). Añade la clave gratuita de AcoustID en Ajustes para no gastar de más."
        : "Tienes clave de AudD pero el motor de pago está desactivado, y falta la de AcoustID. Actívalo o añade la clave gratuita.";

    // --- Comprobación ---

    [RelayCommand]
    private Task CheckAsync() => RunAsync(force: false);

    [RelayCommand]
    private Task RecheckAllAsync() => RunAsync(force: true);

    /// <param name="soloEstas">
    /// Comprobar únicamente estas rutas en vez de la biblioteca entera. Es lo que usa el
    /// «reanalizar» del clic derecho, que actúa sobre lo seleccionado.
    /// </param>
    /// <param name="otraParte">Enviar un fragmento distinto al de la vez anterior.</param>
    public async Task RunAsync(bool force, IReadOnlyList<string>? soloEstas = null, bool otraParte = false)
    {
        if (IsBusy) return;
        if (_engine.Library.Folders.Count == 0)
        {
            Status = "No hay carpetas configuradas. Añádelas en la pestaña Biblioteca.";
            return;
        }
        if (!HayAlgunMotor) { Status = MotoresInfo; return; }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        Progress = 0;
        _engine.ReleaseAudio();   // el reproductor mantiene el archivo abierto y estorba al recorte

        try
        {
            if (!_engine.Library.IsScanned) { Status = "Escaneando biblioteca…"; await _engine.Library.ScanAsync(); }

            var rutas = soloEstas ?? PorConsultar(force);
            if (rutas.Count == 0)
            {
                await RecomponerAsync();
                Status = $"No hay nada nuevo que comprobar. {Resumen}";
                return;
            }

            var crono = System.Diagnostics.Stopwatch.StartNew();
            var prog = new Progress<(int hechas, int total, string archivo)>(p =>
            {
                Progress = p.total == 0 ? 0 : p.hechas * 100.0 / p.total;
                Status = $"Comprobando {p.hechas} de {p.total}…  {p.archivo}";
            });

            var opciones = new IdentificationScanner.Opciones(
                _engine.Config.AcoustIdKey.Trim(), _engine.Config.AuddToken.Trim(),
                UsarPago && HayPago, MaxConsultasPago);

            var r = await _engine.Identificacion.ScanAsync(rutas, opciones, force, prog, _cts.Token, otraParte);

            await RecomponerAsync();
            // El informe describe una pasada sobre la biblioteca. Reanalizar cuatro filas desde el
            // clic derecho no lo es, y escribir un CSV entero por eso solo sería ruido.
            if (soloEstas == null) Informe(r);
            Progress = 100;

            if (soloEstas != null)
            {
                var reconocidas = r.PorGratis + r.PorPago;
                Status = r.Cancelada
                    ? $"Reanálisis cancelado. Lo ya consultado queda guardado."
                    : $"Reanalizadas {rutas.Count} con otro fragmento: {reconocidas} reconocidas"
                      + (r.Fallidas > 0 ? $" · {r.Fallidas} sin respuesta" : "")
                      + (r.Error.Length > 0 ? $" · {r.Error}" : "") + ".";
                return;
            }

            Status = r.Cancelada
                // Cancelar no es terminar. Anunciar «Comprobadas N…» tras pulsar Cancelar hacía
                // creer que la pasada había llegado al final.
                ? $"Comprobación CANCELADA tras {r.Consultadas} canciones. Lo ya consultado queda "
                  + $"guardado y no se volverá a pagar. {Resumen}"
                : r.Error.Length > 0
                // Un error de cuenta no es «no se reconoció nada»: hay que decir exactamente qué pasó,
                // o el usuario concluirá que el servicio no conoce su música.
                ? $"Comprobación detenida: {r.Error}. Lo respondido hasta ahí queda guardado."
                : $"Comprobadas {r.Consultadas} en {TextUtils.FormatEta(crono.Elapsed.TotalSeconds)} · "
                  + $"{r.PorGratis} las identificó AcoustID sin coste"
                  + (r.PorPago > 0 ? $" y {r.PorPago} AudD ({r.ConsultasDePago} consultas de pago)" : "")
                  + (r.Fallidas > 0 ? $" · {r.Fallidas} no se pudieron comprobar" : "")
                  + $". {Resumen}";
        }
        catch (OperationCanceledException)
        {
            // También se informa al cancelar: esas consultas ya se han pagado, así que lo hecho
            // hasta el corte tiene que quedar por escrito igual que una pasada completa.
            await RecomponerAsync();
            Informe(new IdentificationScanner.Resumen(0, 0, 0, 0, 0, "", Cancelada: true));
            Status = $"Comprobación cancelada. Lo ya consultado queda guardado. {Resumen}";
        }
        catch (Exception e)
        {
            Status = "Error durante la comprobación: " + e.Message;
            _engine.Logger.Error("Comprobar audio: fallo", e);
        }
        finally { IsBusy = false; _engine.Identificacion.Save(); _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Rehace la tabla con lo que hay guardado. No consulta nada: se puede llamar siempre, y es lo
    /// que hace que al abrir la pestaña ya se vea el resultado de la última vez.
    /// </summary>
    public async Task RecomponerAsync()
    {
        await _turnoRecomponer.WaitAsync().ConfigureAwait(true);
        Cargando = true;
        try
        {
            var tracks = _engine.Library.Tracks.ToList();
            var pago = UsarPago;

            // FUERA del hilo de la interfaz. Son catorce mil archivos y a cada uno se le parte el
            // nombre y se le pasan varias expresiones regulares: segundos de trabajo. Hacerlo aquí
            // dentro congelaba la ventana al entrar en la pestaña, y con la ventana congelada un
            // aviso de «cargando» ni siquiera llega a dibujarse, que es la trampa de este arreglo.
            //
            // En paralelo y conservando el orden: son cuentas puras sobre datos que solo se leen.
            var filas = await Task.Run(() => tracks.AsParallel().AsOrdered().Select(t => Fila(t, pago)).ToList());

            _filas = filas;

            // La vista se rehace ENTERA de una vez en lugar de ir añadiendo fila a fila. Con una
            // colección observable, catorce mil altas son catorce mil avisos a la rejilla agrupada,
            // y eso costaba más que el propio cálculo.
            RowsView = ConstruirVista(_filas);

            Resumir();
            AplicarFiltro();
        }
        finally { Cargando = false; _turnoRecomponer.Release(); }
    }

    /// <summary>Veredicto de una canción a partir de lo guardado. Puro: no toca red ni interfaz.</summary>
    private IdentifyRow Fila(Track t, bool usarPago)
    {
        if (Identificacion.SeExcluye(t.FilePath, out var motivoExcluida))
            return new IdentifyRow
            {
                FileName = t.FileName, Folder = t.Folder, FilePath = t.FilePath,
                Veredicto = VeredictoId.Excluida,
                Motivo = motivoExcluida,
            };

        if (!_engine.Identificacion.Consultado(t.FilePath, usarPago))
            return new IdentifyRow
            {
                FileName = t.FileName, Folder = t.Folder, FilePath = t.FilePath,
                Veredicto = null,
                Motivo = _engine.Identificacion.TieneRespuesta(t.FilePath)
                    ? "el motor gratuito no la reconoce; queda pendiente del de pago"
                    : "todavía no se ha escuchado",
            };

        var id = _engine.Identificacion.Get(t.FilePath);
        var v = Identificacion.Comparar(t.FileName, t.Artist, t.Title, id);
        return new IdentifyRow
        {
            FileName = t.FileName, Folder = t.Folder, FilePath = t.FilePath,
            Veredicto = v.Estado,
            Motivo = v.Motivo,
            AudioArtist = id?.Artist ?? "",
            AudioTitle = id?.Title ?? "",
            Motor = id?.Fuente ?? "",
            Descartada = _engine.AudioAceptadas.Contains(t.FilePath),
        };
    }

    private static DataGridCollectionView ConstruirVista(IList<IdentifyRow> filas)
    {
        var v = new DataGridCollectionView(filas);
        v.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(IdentifyRow.Folder)));
        return v;
    }

    /// <summary>
    /// Deja en el registro el resultado de la pasada, y en un CSV la tabla entera.
    ///
    /// Es la parte que permite comprobar que esto funciona bien de verdad. El registro por canción
    /// que escribe el escáner cuenta lo que RESPONDIÓ el servicio; aquí se anota el VEREDICTO, que
    /// es otra cosa: la comparación entre lo que dijo el audio y lo que dice el archivo. Sin esto,
    /// revisar si la pestaña acierta obligaría a ir fila por fila por la pantalla.
    ///
    /// Se listan las discrepancias porque son lo accionable, y también unas cuantas correctas: para
    /// juzgar si el criterio es bueno hay que poder ver los aciertos, no solo los avisos.
    /// </summary>
    private void Informe(IdentificationScanner.Resumen r)
    {
        var log = _engine.Logger;
        var malas = _filas.Where(f => f.NoCoincide).ToList();
        var buenas = _filas.Count(f => f.Veredicto == VeredictoId.Coincide);
        var desconocidas = _filas.Count(f => f.Veredicto == VeredictoId.SinIdentificar);
        var excluidas = _filas.Count(f => f.Veredicto == VeredictoId.Excluida);
        var sinComprobar = _filas.Count(f => f.Veredicto == null);

        // Reparto por motor sobre la tabla ENTERA, no solo sobre esta pasada: es el número que
        // responde a la pregunta que importa, «¿hace falta pagar?». Si el gratuito resuelve la
        // mayoría, el de pago sobra.
        var porGratisTotal = _filas.Count(f => f.Motor == "AcoustID");
        var porPagoTotal = _filas.Count(f => f.Motor == "AudD");

        log.Sum("─────────────────────────────────────────────────");
        log.Sum($"AUDIO|v{AppInfo.Version}|comprobadas={r.Consultadas}|correctas={buenas}"
              + $"|discrepancias={malas.Count}|noreconocidas={desconocidas}"
              + $"|excluidas={excluidas}|sincomprobar={sinComprobar}|fallidas={r.Fallidas}");
        log.Sum($"AUDIO-MOTORES|acoustid={porGratisTotal}|audd={porPagoTotal}"
              + $"|nuevas_gratis={r.PorGratis}|nuevas_pago={r.PorPago}"
              + $"|consultas_de_pago={r.ConsultasDePago}");

        // «consultas_de_pago» es el gasto real de esta pasada: es el número que debe cuadrar con el
        // panel de AudD. Lo que resolvió AcoustID no cuesta nada por muchas que sean.
        if (r.Cancelada) log.Log("   PASADA CANCELADA por el usuario; queda biblioteca sin comprobar.", LogKind.No);
        if (r.Error.Length > 0) log.Err($"   PASADA DETENIDA: {r.Error}");

        if (malas.Count > 0)
        {
            log.Sum($"   — las {Math.Min(60, malas.Count)} primeras que NO coinciden:");
            foreach (var f in malas.Take(60)) log.Log($"       {f.FileName}   ->   {f.Audio}", LogKind.No);
        }
        else if (buenas > 0) log.Ok("   — ninguna discrepancia.");

        // Una muestra de aciertos: sirve para ver si el criterio es demasiado blando.
        var muestra = _filas.Where(f => f.Veredicto == VeredictoId.Coincide).Take(15).ToList();
        if (muestra.Count > 0)
        {
            log.Detail($"   — muestra de {muestra.Count} dadas por correctas (para revisar el criterio):");
            foreach (var f in muestra) log.Detail($"       {f.FileName}   ->   {f.Audio}");
        }

        try
        {
            Directory.CreateDirectory(_engine.Paths.ReportsDir);
            var csv = Path.Combine(_engine.Paths.ReportsDir, $"audio_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("archivo;carpeta;estado;motor;audio_artista;audio_titulo;explicacion");
            foreach (var f in _filas)
                sb.AppendLine(string.Join(";", new[]
                {
                    TextUtils.CsvField(f.FileName), TextUtils.CsvField(f.Folder), TextUtils.CsvField(f.Estado),
                    TextUtils.CsvField(f.Motor),
                    TextUtils.CsvField(f.AudioArtist), TextUtils.CsvField(f.AudioTitle), TextUtils.CsvField(f.Motivo),
                }));
            File.WriteAllText(csv, sb.ToString(), System.Text.Encoding.UTF8);
            log.Sum($"   Informe para analizar: {csv}");
        }
        catch (Exception e) { log.Error("No se pudo escribir el informe CSV de la comprobación", e); }

        log.Sum("─────────────────────────────────────────────────");
    }

    private void Resumir()
    {
        var comprobadas = _filas.Count(r => r.Veredicto is VeredictoId.Coincide or VeredictoId.Difiere
                                                      or VeredictoId.SinIdentificar);
        if (comprobadas == 0) { Resumen = ""; return; }

        var mal = _filas.Count(r => r.NoCoincide);
        var bien = _filas.Count(r => r.Veredicto == VeredictoId.Coincide);
        var desconocidas = _filas.Count(r => r.Veredicto == VeredictoId.SinIdentificar);
        var descartadas = _filas.Count(r => r.Descartada);

        // Las descartadas se cuentan aparte, no se suman a las correctas: son avisos que el usuario
        // revisó y dio por falsos, y saber cuántos hay dice si el criterio está avisando de más.
        var revisadas = descartadas > 0 ? $" · {descartadas} descartadas tras revisarlas" : "";

        Resumen = mal == 0
            ? $"Ninguna discrepancia pendiente entre {bien} canciones comprobadas."
              + (desconocidas > 0 ? $" Otras {desconocidas} no las reconoce el servicio." : "")
              + revisadas
            : $"{mal} no coinciden con lo que dice su nombre · {bien} correctas"
              + (desconocidas > 0 ? $" · {desconocidas} no reconocidas" : "") + revisadas + ".";
    }

    // --- Acciones sobre la tabla ---

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsBusy) { Status = "Hay una comprobación en curso."; return; }
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        await Shell.OpenContainingFolderAsync(path);
    }

    /// <summary>
    /// Lleva la canción al Editor para corregirla a mano. A propósito NO se escribe nada de forma
    /// automática: lo que dice el audio es una pista muy buena, pero decidir que el archivo estaba
    /// mal etiquetado y reescribirlo sin que nadie lo mire es justo el tipo de automatismo que
    /// estropea una biblioteca entera de una pasada.
    /// </summary>
    [RelayCommand]
    private void EditTrack()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        _engine.RequestEdit(path);
    }

    /// <summary>Olvida las respuestas de las filas elegidas para poder volver a preguntarlas.</summary>
    [RelayCommand]
    private async Task ForgetSelectedAsync()
    {
        var filas = Objetivo();
        if (filas.Count == 0) { Status = "Selecciona antes alguna canción de la lista."; return; }
        Status = $"{filas.Count} volverán a comprobarse en la próxima pasada (cada una gastará una consulta).";
        _engine.Identificacion.Olvidar(filas.Select(f => f.FilePath));
        await RecomponerAsync();
    }

    /// <summary>
    /// Da por bueno el aviso: la canción es correcta y el que se equivocó fue el servicio.
    ///
    /// No se toca el archivo ni se borra la respuesta guardada; solo deja de contar como
    /// discrepancia. Es lo que hace que la lista converja: cada falso aviso revisado desaparece del
    /// rojo para siempre, y lo que queda en rojo es siempre lo que aún no se ha mirado.
    /// </summary>
    [RelayCommand]
    private async Task DiscardSelectedAsync()
    {
        var filas = Objetivo();
        if (filas.Count == 0) { Status = "Selecciona antes alguna canción de la lista."; return; }

        foreach (var f in filas) _engine.AudioAceptadas.Add(f.FilePath);
        _engine.AudioAceptadas.Save();
        _engine.Logger.Detail($"Comprobar audio: {filas.Count} aviso(s) descartado(s) por el usuario.");

        Status = filas.Count == 1
            ? $"«{filas[0].FileName}» dada por buena: deja de contar como discrepancia."
            : $"{filas.Count} dadas por buenas: dejan de contar como discrepancias.";
        await RecomponerAsync();
    }

    /// <summary>Vuelve a tener en cuenta avisos descartados, por si se descartó alguno por error.</summary>
    [RelayCommand]
    private async Task UndiscardSelectedAsync()
    {
        var filas = Objetivo().Where(f => f.Descartada).ToList();
        if (filas.Count == 0) { Status = "Ninguna de las seleccionadas estaba descartada."; return; }

        foreach (var f in filas) _engine.AudioAceptadas.Remove(f.FilePath);
        _engine.AudioAceptadas.Save();
        Status = $"{filas.Count} vuelven a contar como discrepancia.";
        await RecomponerAsync();
    }

    /// <summary>
    /// Vuelve a preguntar por las seleccionadas enviando OTRO fragmento del tema.
    ///
    /// Es la respuesta correcta a la mayoría de los avisos falsos: que un fragmento no se reconozca
    /// -o se reconozca mal- suele significar que cayó en un break, en un solo de percusión o en un
    /// trozo hablado, no que la canción sea otra. Cada intento prueba un punto distinto, y se
    /// recuerda cuál se usó para no repetir el mismo trozo.
    /// </summary>
    [RelayCommand]
    private async Task ReanalyzeOtherPartAsync()
    {
        var filas = Objetivo();
        if (filas.Count == 0) { Status = "Selecciona antes alguna canción de la lista."; return; }
        await RunAsync(force: true, soloEstas: filas.Select(f => f.FilePath).ToList(), otraParte: true);
    }
}
