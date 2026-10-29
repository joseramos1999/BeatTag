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
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción medida, con lo que habría que subirla o bajarla.</summary>
public sealed partial class LoudnessRow : ObservableObject
{
    private static readonly IBrush AltaBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xDC, 0x26, 0x26));  // rojo
    private static readonly IBrush BajaBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B));  // ámbar
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A));    // verde

    public string FileName { get; init; } = "";
    public string Folder { get; init; } = "";
    public string FilePath { get; init; } = "";
    public double Lufs { get; init; }

    /// <summary>Pico de muestra (dBFS).</summary>
    public double PeakDb { get; init; }

    /// <summary>
    /// Pico real (dBTP, ITU-R BS.1770), o null si aún no se ha medido: las medidas guardadas por
    /// versiones anteriores solo tienen el pico de muestra. Se mide al analizar archivos nuevos y,
    /// en los ya medidos, solo en los que se van a aumentar.
    /// </summary>
    public double? TruePeakDb { get; init; }

    /// <summary>
    /// El pico con el que se decide cuánto se puede subir: el real si se conoce y, si no, el de
    /// muestra. Nunca por debajo del de muestra.
    /// </summary>
    public double PicoDecision => TruePeakDb is { } t ? Math.Max(t, PeakDb) : PeakDb;

    /// <summary>
    /// Ganancia que BeatTag le ha aplicado desde que se midió su audio original (dB; 0 si ninguna).
    /// La referencia automática se calcula con el nivel original, Lufs menos esta ganancia.
    /// </summary>
    public double GananciaAplicada { get; init; }

    /// <summary>
    /// Se bajó con el pico recortado: el pico guardado es el tope y el real se desconoce hasta volver
    /// a medirlo (ver LoudnessScanner.TrasAjustar). Antes de dar por hecho que no admite aumento, se mide.
    /// </summary>
    internal bool PicoDesconocidoTrasBajar => TruePeakDb == null && GananciaAplicada < 0 && PeakDb >= LoudnessScanner.PicoRecortado;

    /// <summary>Objetivo con el que se calculó la ganancia.</summary>
    public double Target { get; init; }

    /// <summary>
    /// La medida de la fila ya no vale para el archivo: ha cambiado desde que se midió (un deshacer,
    /// otro programa) y la fila conserva los valores de antes. No entra en el ajuste hasta volver a
    /// analizarla: con el nivel y el pico de antes, una subida podía saturar.
    /// </summary>
    public bool MedidaCaducada { get; init; }

    public string LufsText => $"{Lufs:0.0}";
    public string PeakText => $"{PicoDecision:0.0}";

    /// <summary>Cuánto hay que subir (+) o bajar (−) para llegar al objetivo.</summary>
    public double Gain => Target - Lufs;

    /// <summary>
    /// Dentro de la banda «Correcto»: a <see cref="LoudnessViewModel.UmbralAjuste"/> o menos de la
    /// referencia. Es el mismo umbral que decide qué se ajusta, así que una fila «Correcto» nunca
    /// sale marcada para ajustar.
    /// </summary>
    private bool EnNivel => Math.Abs(Gain) <= LoudnessViewModel.UmbralAjuste;

    /// <summary>Situación de la grabación respecto al nivel de referencia.</summary>
    public string Estado =>
        MedidaCaducada ? "Por medir" :
        EnNivel ? "Correcto" :
        Gain > 3 ? "Muy bajo" :
        Gain > 0 ? "Bajo" :
        Gain < -3 ? "Muy alto" : "Alto";

    /// <summary>Corrección que necesitaría para alcanzar el nivel de referencia.</summary>
    public string Accion =>
        MedidaCaducada || EnNivel ? "—"
        : Gain > 0 ? $"Aumentar {Gain:0.0} dB" : $"Reducir {-Gain:0.0} dB";

    /// <summary>Si al aplicar esa ganancia el pico se pasaría de 0 dBFS (saturaría).</summary>
    public bool Satura => PicoDecision + Gain > 0;

    /// <summary>
    /// El ajuste sin recodificar solo existe para MP3: consiste en retocar un campo de cada trama.
    /// Un FLAC o un WAV se miden igual de bien, pero cambiarles el volumen exigiría reescribir el
    /// audio, y eso ni es reversible ni se hace a espaldas del usuario. Se miden y se dejan.
    /// </summary>
    public bool Ajustable => Mp3Gain.EsAjustable(FilePath);

    /// <summary>
    /// Limitación aplicable a esta grabación, expresada sin tecnicismos. La falta de margen solo se
    /// señala en las que requieren aumento, con el mismo criterio que la confirmación: antes salía
    /// también en filas «Correcto» con el pico alto, que nadie iba a tocar.
    /// </summary>
    public string Aviso =>
        MedidaCaducada ? "Ha cambiado desde que se midió: pulsa Analizar para volver a medirla"
        : !Ajustable ? "Se mide, pero no se ajusta: solo los MP3 admiten el cambio sin pérdida"
        : Exclusion == ExclusionVolumen.Carpeta ? "Excluida del ajuste: su carpeta está en las exclusiones"
        : Exclusion == ExclusionVolumen.Cancion ? "Excluida del ajuste"
        : Gain > LoudnessViewModel.UmbralAjuste && PicoDesconocidoTrasBajar
            ? "Falta medir su pico real para saber si admite aumento: pulsa Analizar"
        : Gain > LoudnessViewModel.UmbralAjuste && LoudnessViewModel.AumentoParcial(this)
            ? (LoudnessViewModel.PasosAplicables(Gain, PicoDecision) == 0
                ? "Sin margen suficiente: aumentarla produciría distorsión"
                : "Sin margen suficiente: solo admite aumentarla en parte")
        : "";

    // --- Qué se ajusta (E15, equipo «volumen_copias», tanda 4) ---

    /// <summary>
    /// Requiere ajuste y se puede ajustar: MP3, desviada más del umbral y con algún paso que aplicar
    /// (una que solo podría subir y no tiene margen se queda fuera). Es el criterio de siempre, con
    /// una medida que siga valiendo para el archivo.
    /// </summary>
    public bool Candidata => !MedidaCaducada && Ajustable && Math.Abs(Gain) > LoudnessViewModel.UmbralAjuste
                             && LoudnessViewModel.PasosAplicables(Gain, PicoDecision) != 0;

    /// <summary>Excluida del ajuste por su carpeta o por ella misma (se mide igual).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Aviso), nameof(PuedeMarcarse))]
    private ExclusionVolumen _exclusion;

    /// <summary>La casilla «Ajustar» se puede tocar: requiere ajuste y no está excluida.</summary>
    public bool PuedeMarcarse => Candidata && Exclusion == ExclusionVolumen.Ninguna;

    /// <summary>Casilla «Ajustar»: entra en «Ajustar volumen». Marcada de entrada en las que lo requieren.</summary>
    [ObservableProperty] private bool _ajustar;

    /// <summary>El usuario ha cambiado la casilla (no el código al montar la tabla).</summary>
    internal Action<LoudnessRow>? AlMarcarAMano { get; set; }

    private bool _marcandoDesdeElCodigo;

    partial void OnAjustarChanged(bool value)
    {
        if (!_marcandoDesdeElCodigo) AlMarcarAMano?.Invoke(this);
    }

    /// <summary>Pone la casilla sin contarlo como un cambio hecho a mano.</summary>
    internal void Marcar(bool valor)
    {
        _marcandoDesdeElCodigo = true;
        try { Ajustar = valor; }
        finally { _marcandoDesdeElCodigo = false; }
    }

    public IBrush StateBrush =>
        MedidaCaducada ? Brushes.Transparent :
        EnNivel ? OkBrush :
        Gain < 0 ? AltaBrush : BajaBrush;
}

/// <summary>
/// Pestaña Volumen: mide la sonoridad real (EBU R128) de la biblioteca y dice cuánto se desvía
/// cada canción del objetivo. Medir no toca ningún archivo; «Ajustar volumen» corrige los MP3 que el
/// usuario deja marcados (o los seleccionados), salvo los excluidos, y se puede deshacer.
/// </summary>
public partial class LoudnessViewModel : ViewModelBase, IEstadoPagina, IProgresoPagina
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Las filas, que se rehacen enteras con un solo aviso a la tabla (ver <see cref="ListaEnBloque{T}"/>).
    /// Cambiar el nivel, excluir o quitar una exclusión pueden mover la referencia y rehacer la tabla:
    /// antes se vaciaba y se volvía a llenar fila a fila, y con 20.000 filas agrupadas por carpeta eso
    /// bloqueaba la pestaña casi un segundo (0,7-1,1 s medidos sin dibujar la tabla).
    /// </summary>
    private readonly ListaEnBloque<LoudnessRow> _filas = new();

    public ObservableCollection<LoudnessRow> Rows => _filas;
    public DataGridCollectionView RowsView { get; }

    /// <summary>
    /// Modos pensados para que no haya que saber qué es un LUFS. El primero es el recomendado
    /// porque se adapta SOLO a la colección de cada uno: no impone un número de fuera.
    /// </summary>
    public string[] Modos { get; } =
    {
        ModoAutomatico,
        "Nivel de club",
        "Nivel de plataformas de streaming",
        "Nivel de cine y televisión",
    };

    /// <summary>
    /// El modo recomendado. Se llamaba «Nivel medio de la biblioteca», pero la referencia ya no es la
    /// mediana: es el nivel que alcanza sin distorsión casi toda la colección (ver
    /// <see cref="ReferenciaVolumen"/>).
    /// </summary>
    internal const string ModoAutomatico = "Nivel común de la biblioteca (recomendado)";

    [ObservableProperty] private string _modo = ModoAutomatico;

    /// <summary>Nivel al que se compara todo. En el modo automático lo calcula la propia música.</summary>
    [ObservableProperty] private double _target = -14;

    /// <summary>Explicación del modo elegido, para que se entienda qué va a pasar.</summary>
    [ObservableProperty] private string _explicacionModo = "";

    /// <summary>La última referencia automática calculada, con su mediana y su motivo, para explicarla.</summary>
    private ResultadoReferencia? _referenciaCalculada;

    /// <summary>Alguna de las grabaciones que cuentan para la referencia ya la ha ajustado BeatTag.</summary>
    private bool _hayAjustadas;

    private bool EsAutomatico => Modo.StartsWith("Nivel común", StringComparison.Ordinal);

    partial void OnModoChanged(string value)
    {
        if (EsAutomatico)
        {
            // El propio contenido fija la referencia; sin nada medido aún, se queda como estaba.
            if (Rows.Count > 0) ActualizarReferenciaAutomatica();
        }
        else
        {
            Target = value switch
            {
                var m when m.StartsWith("Nivel de club") => -11,
                var m when m.StartsWith("Nivel de plataformas") => -14,
                _ => -23,
            };
        }
        // Si la referencia cambia, OnTargetChanged ya rehace la tabla.
        ExplicarModo();
        AvisarPicoRealPendiente();
    }

    private void ExplicarModo()
    {
        ExplicacionModo = EsAutomatico
            ? TextoReferenciaAutomatica(Rows.Count > 0 ? _referenciaCalculada : null, _hayAjustadas)
            : Modo.StartsWith("Nivel de club")
                ? "Nivel elevado, adecuado para equipos de sala."
                : Modo.StartsWith("Nivel de plataformas")
                    ? "Nivel de reproducción de Spotify y YouTube. Inferior al habitual en música de baile."
                    : "Nivel de referencia en cine y televisión. Considerablemente inferior al de la música comercial.";
    }

    /// <summary>
    /// Qué es la referencia automática y por qué ha quedado ahí. Cuando queda por debajo del nivel
    /// medio se dan los dos valores y el motivo, para que no parezca un error que no coincidan; cuando
    /// el límite de <see cref="ReferenciaVolumen.DescensoMaximo"/> dB o una de las salvaguardas la
    /// dejan en otro sitio, se dice cuál. Si ya hay grabaciones ajustadas, el nivel medio es el que
    /// tenían antes de ajustarlas, que es con el que se calcula.
    /// </summary>
    internal static string TextoReferenciaAutomatica(ResultadoReferencia? resultado, bool hayAjustadas = false)
    {
        if (resultado is not { } x || x.Motivo == MotivoReferencia.SinMedidas)
            return "La referencia se calcula a partir de tus grabaciones al analizarlas: el nivel más alto que alcanza "
                 + $"sin distorsión el {PorcentajeAlcance} % de ellas, sin superar su nivel medio.";

        var r = x.Referencia;
        var m = x.Mediana;
        var nivelMedio = hayAjustadas ? "nivel medio que tenían tus grabaciones antes de ajustarlas" : "nivel medio de tus grabaciones";
        return x.Motivo switch
        {
            MotivoReferencia.Alcance =>
                $"Referencia: {r:0.0} LUFS, el nivel más alto que alcanzan sin distorsión el {PorcentajeAlcance} % de tus "
                + $"grabaciones ({(hayAjustadas ? "nivel medio antes de ajustarlas" : "nivel medio")}: {m:0.0} LUFS). "
                + "Las más bajas no admiten aumento, así que se igualan reduciendo las demás.",
            MotivoReferencia.Tope =>
                $"Referencia: {r:0.0} LUFS, {ReferenciaVolumen.DescensoMaximo:0} dB por debajo del {nivelMedio} ({m:0.0} LUFS): "
                + "es lo máximo que se reduce de forma automática. "
                + $"Más del {100 - PorcentajeAlcance} % de tus grabaciones no alcanza ese nivel sin distorsión y se quedará por debajo; "
                + "para igualarlas también, elige un nivel fijo más bajo.",
            MotivoReferencia.PocasGrabaciones =>
                $"Referencia: {r:0.0} LUFS, el {nivelMedio}. Con menos de {ReferenciaVolumen.MinimoGrabaciones} grabaciones, "
                + "las que no alcanzan ese nivel sin distorsión se quedan por debajo en vez de reducir las demás.",
            MotivoReferencia.PocasAjustables =>
                $"Referencia: {r:0.0} LUFS, el {nivelMedio}. La mayoría no son MP3 y no se pueden reducir, "
                + "así que las que no alcanzan ese nivel sin distorsión se quedan por debajo.",
            _ => $"Referencia: {r:0.0} LUFS, el {nivelMedio}.",
        };
    }

    private static int PorcentajeAlcance => (int)Math.Round((1 - ReferenciaVolumen.PercentilAlcance) * 100);

    /// <summary>Las filas que cuentan para la referencia automática: las no excluidas (o todas, si lo están todas).</summary>
    private static List<LoudnessRow> CuentanParaLaReferencia(IReadOnlyCollection<LoudnessRow> filas)
    {
        var cuentan = filas.Where(r => r.Exclusion == ExclusionVolumen.Ninguna).ToList();
        return cuentan.Count == 0 ? filas.ToList() : cuentan;
    }

    /// <summary>
    /// Referencia del modo automático (ver <see cref="ReferenciaVolumen"/>), sin contar las excluidas:
    /// las acapellas o intros que el DJ quiere bajas a propósito no pueden arrastrar hacia abajo la
    /// referencia de todas las demás. Si todo está excluido, se calcula con todo (solo se muestra).
    /// Va con el nivel original de cada grabación (el actual menos lo que BeatTag ya le ha aplicado),
    /// para que ajustar no mueva la referencia.
    /// </summary>
    internal static ResultadoReferencia ReferenciaAutomatica(IReadOnlyCollection<LoudnessRow> filas)
        => ReferenciaVolumen.Automatica(
            CuentanParaLaReferencia(filas)
                .Select(r => new FilaReferencia(r.Lufs, r.PicoDecision, r.Ajustable, r.GananciaAplicada)).ToList(),
            MargenSeguridad, Mp3Gain.DbPerStep);

    /// <summary>
    /// Recalcula la referencia automática con lo que hay en la tabla. Ordenar unas miles de medidas ya
    /// guardadas lleva milisegundos: se puede hacer en el hilo de la interfaz.
    /// </summary>
    private void ActualizarReferenciaAutomatica()
    {
        var resultado = ReferenciaAutomatica(Rows);
        _referenciaCalculada = resultado;
        _hayAjustadas = CuentanParaLaReferencia(Rows).Any(r => r.GananciaAplicada != 0);
        Target = resultado.Referencia;   // si cambia, OnTargetChanged rehace la tabla
        ExplicarModo();
    }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _soloDesviadas;
    [ObservableProperty] private LoudnessRow? _selectedRow;
    [ObservableProperty] private string _status = "Pulsa Analizar para comprobar la uniformidad de volumen de la biblioteca.";
    [ObservableProperty] private string _resumen = "";

    public LoudnessViewModel(AppEngine engine)
    {
        _engine = engine;
        RowsView = new DataGridCollectionView(Rows);
        RowsView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(LoudnessRow.Folder)));
        RefrescarListaExclusiones();
        RecontarMarcadas();
        ExplicarModo();
    }

    partial void OnSoloDesviadasChanged(bool value) => AplicarFiltro();
    partial void OnTargetChanged(double value) => Recalcular();

    private void AplicarFiltro()
    {
        RowsView.Filter = SoloDesviadas ? o => o is LoudnessRow r && Math.Abs(r.Gain) > UmbralAjuste : null;
        RowsView.Refresh();
    }

    /// <summary>Al cambiar el objetivo no hay que volver a medir: solo se recalcula la desviación.</summary>
    private void Recalcular()
    {
        if (Rows.Count == 0) return;
        var excl = Exclusiones();
        _filas.Reemplazar(Rows.Select(r => Fila(r.FileName, r.Folder, r.FilePath, r.Lufs, r.PeakDb, r.TruePeakDb,
                                                r.GananciaAplicada, excl, r.MedidaCaducada)).ToList());
        AplicarFiltro();
        Resumir();
    }

    [RelayCommand]
    private Task MeasureAsync() => RunAsync(force: false);

    [RelayCommand]
    private Task RemeasureAllAsync() => RunAsync(force: true);

    private async Task RunAsync(bool force)
    {
        if (IsBusy) return;
        if (_engine.Library.Folders.Count == 0) { Status = "No hay carpetas configuradas. Añádelas en la pestaña Biblioteca."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Progress = 0;
        _engine.ReleaseAudio();   // no conviene medir mientras suena algo
        try
        {
            if (!_engine.Library.IsScanned) { Status = "Escaneando biblioteca…"; await _engine.Library.ScanAsync(); }
            var tracks = _engine.Library.Tracks.ToList();
            var rutas = tracks.Select(t => t.FilePath).ToList();
            _engine.Logger.Head($"Volumen: midiendo {rutas.Count} canciones (objetivo {Target:0.0} LUFS)");

            // Los mono medidos antes del mono doble se vuelven a medir; solo ellos (el resto de
            // medidas guardadas sigue valiendo).
            var mono = force ? null : _engine.Loudness.MonoPorVolverAMedir(tracks.Select(t => (t.FilePath, t.Channels)));
            if (mono is { Count: > 0 })
                _engine.Logger.Detail($"Volumen: {mono.Count} grabaciones mono se vuelven a medir como se escuchan (mono doble)");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var prog = new Progress<(int done, int total, string file)>(p =>
            {
                Progress = p.total == 0 ? 0 : p.done * 100.0 / p.total;
                Status = $"Analizando {p.done} de {p.total}…  {p.file}";
            });

            await _engine.Loudness.MeasureManyAsync(rutas, force, prog, _cts.Token, mono);

            var excl = Exclusiones();
            var filas = new List<LoudnessRow>(tracks.Count);
            foreach (var t in tracks)
            {
                var m = _engine.Loudness.Get(t.FilePath);
                if (m is not { Ok: true } r) continue;
                filas.Add(Fila(t.FileName, t.Folder, t.FilePath, r.Lufs, r.PeakDb, r.TruePeakDb, r.GananciaAplicada, excl));
            }
            _filas.Reemplazar(filas);
            RefrescarListaExclusiones();   // ya se sabe qué canciones excluidas siguen en la biblioteca

            // En el modo automático la referencia sale de la propia música, así que hasta ahora no
            // se podía saber: se calcula al terminar de medir (si cambia, se rehace la tabla con ella).
            if (EsAutomatico && Rows.Count > 0) ActualizarReferenciaAutomatica();
            else ExplicarModo();

            // Lo que se aumentaría necesita su pico real antes de marcarse; se mide solo en esas.
            await ComprobarPicoRealAsync(_cts.Token);

            AplicarFiltro();
            Resumir();
            Progress = 100;
            Status = $"Analizadas {Rows.Count} de {tracks.Count} en {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}. {Resumen}"
                   + (mono is { Count: > 0 } ? " " + AjusteVolumenSeleccion.TextoMonoMedidas(mono.Count) : "");
            _engine.Logger.Sum($"Volumen: {Rows.Count} medidas · referencia {Target:0.0} LUFS · {Resumen}");
        }
        catch (OperationCanceledException)
        {
            Status = Rows.Count == 1 ? "Análisis cancelado (1 grabación analizada)." : $"Análisis cancelado ({Rows.Count} grabaciones analizadas).";
        }
        catch (Exception e)
        {
            Status = "Error durante el análisis: " + e.Message;
            _engine.Logger.Error("Volumen: fallo al medir", e);
        }
        finally { IsBusy = false; _engine.Loudness.Save(); _cts?.Dispose(); _cts = null; }
    }

    /// <summary>Resumen del estado de la biblioteca, sin tecnicismos.</summary>
    private void Resumir()
    {
        RecontarMarcadas();
        if (Rows.Count == 0) { Resumen = ""; return; }
        var correctas = Rows.Count(r => Math.Abs(r.Gain) <= UmbralAjuste);
        var bajas = Rows.Count(r => r.Gain > UmbralAjuste);
        var altas = Rows.Count(r => r.Gain < -UmbralAjuste);
        var requierenAjuste = Rows.Count(r => Math.Abs(r.Gain) > UmbralAjuste);
        Resumen = AjusteVolumenSeleccion.TextoResumen(Rows.Count, correctas, bajas, altas, requierenAjuste);
    }

    /// <summary>
    /// A partir de esta desviación (dB) se ajusta, y es también el límite de la banda «Correcto».
    /// Con pasos de 1,5 dB, ajustar una desviación d deja |d − 1,5|, así que a partir de 0,75 dB
    /// ajustar siempre mejora. Se queda en 1 dB porque es, aproximadamente, la menor diferencia que se
    /// percibe entre dos canciones seguidas, coincide con la banda «Correcto» de la tabla y deja 0,25 dB
    /// de holgura frente al residuo máximo de 0,75: una grabación ya ajustada no vuelve a tocarse si la
    /// referencia se mueve una o dos décimas. Antes era 2 dB y dejaba sin tocar a las que se desviaban
    /// entre 1 y 2 dB (en una biblioteca real, una de cada cuatro).
    /// </summary>
    public const double UmbralAjuste = 1.0;

    /// <summary>
    /// Margen que se deja sin usar para no llegar justo al límite de distorsión: las subidas no dejan
    /// el pico por encima de −0,5 dBTP. Se aplica sobre el pico REAL cuando se conoce; el de muestra
    /// se quedaba hasta 2 dB corto y un margen fijo mayor sobre él desperdiciaba tres de cada cuatro
    /// pasos de subida.
    /// </summary>
    internal const double MargenSeguridad = 0.5;

    /// <summary>
    /// Grabaciones que se corregirían con «Ajustar volumen»: las marcadas en la columna «Ajustar»
    /// entre las que lo requieren (MP3, desviadas más del umbral, con margen) y no están excluidas.
    /// </summary>
    public List<LoudnessRow> ParaAjustar() => AjusteVolumenSeleccion.Elegir(Rows).Ajustar.ToList();

    /// <summary>
    /// Lo que entraría en el ajuste: lo marcado (<paramref name="seleccion"/> null) o lo seleccionado,
    /// con lo que queda fuera y por qué (ver <see cref="AjusteVolumenSeleccion.Elegir"/>).
    /// </summary>
    public SeleccionAjuste Elegir(IReadOnlyCollection<LoudnessRow>? seleccion = null)
        => AjusteVolumenSeleccion.Elegir(Rows, seleccion);

    /// <summary>Desviadas que no son MP3: se miden, pero no se pueden ajustar sin recodificar.</summary>
    public int DesviadasNoMp3 => Rows.Count(r => !r.Ajustable && Math.Abs(r.Gain) > UmbralAjuste);

    /// <summary>
    /// Pasos de MP3Gain (1,5 dB cada uno) que de verdad se pueden aplicar. Bajar siempre cabe, y se
    /// redondea al paso más cercano. Subir está limitado por el pico: se aplica lo máximo que no
    /// llegue a distorsionar (mejor mejorar algo que no tocar), y ese tope se redondea HACIA ABAJO.
    ///
    /// Antes se redondeaba al más cercano también al subir, y el redondeo puede pasarse hasta
    /// 0,75 dB, más que los 0,5 de margen. Ejemplo: canción a −16 LUFS con objetivo
    /// −11 (+5 dB deseados) y pico de −2,8 dBFS. Caben 2,3 dB; redondeando salían 2 pasos (+3,0 dB)
    /// y el pico acababa en +0,2 dBFS, recortado al decodificar. Ahora sale 1 paso (+1,5 dB) y el
    /// pico queda en −1,3.
    ///
    /// El tope se aplica aunque la ganancia deseada quepa: con +2,3 deseados y un pico de −2,9 caben
    /// 2,4 dB, pero el paso más cercano (+3,0) tampoco cabe.
    /// </summary>
    internal static int PasosAplicables(double ganancia, double picoDb)
    {
        var pasos = Mp3Gain.StepsFor(ganancia);
        if (ganancia <= 0) return pasos;
        var cabe = Math.Max(0, -MargenSeguridad - picoDb);   // lo que cabe antes de tocar techo
        var tope = (int)Math.Floor(cabe / Mp3Gain.DbPerStep + 1e-9);
        return Math.Min(pasos, tope);
    }

    /// <summary>
    /// El aumento se queda corto de lo deseado por falta de margen (más de medio paso por debajo).
    /// Lo usan el aviso previo y el resumen final, para que digan lo mismo.
    /// </summary>
    public static bool AumentoParcial(LoudnessRow r) => EsParcial(r.Gain, PasosAplicables(r.Gain, r.PicoDecision));

    /// <summary>Con <paramref name="pasos"/> aplicados, el aumento deseado se queda a más de medio paso.</summary>
    internal static bool EsParcial(double ganancia, int pasos)
        => ganancia > 0 && ganancia - pasos * Mp3Gain.DbPerStep > Mp3Gain.DbPerStep / 2;

    /// <summary>Lo que se va a aplicar a una grabación, con la medida de la que se partió.</summary>
    /// <param name="Pasos">Pasos de 1,5 dB (0: no se toca).</param>
    /// <param name="Lufs">
    /// Nivel del archivo antes del ajuste: el de su medida vigente, que no tiene por qué ser el de la
    /// fila (ver <see cref="Decidir"/>).
    /// </param>
    /// <param name="PicoMuestra">Pico de muestra antes del ajuste.</param>
    /// <param name="PicoReal">Pico real antes del ajuste (null si no se conoce).</param>
    /// <param name="Medida">Hubo que medir el archivo en ese momento.</param>
    /// <param name="NoSePudoMedir">
    /// Hacía falta medirlo y no se pudo (movido, renombrado, borrado o ilegible): no se toca, y cuenta
    /// como un fallo, no como falta de margen.
    /// </param>
    internal readonly record struct DecisionAjuste(int Pasos, double Lufs, double PicoMuestra, double? PicoReal, bool Medida,
                                                   bool NoSePudoMedir = false);

    /// <summary>
    /// Última barrera antes de escribir. Decide con la medida del archivo TAL COMO ESTÁ AHORA, no con
    /// la de la fila: la guardada (<paramref name="guardada"/>) si sigue valiendo para su fecha y su
    /// tamaño, y si no, una nueva (<paramref name="medir"/>, que decodifica el archivo: hay que
    /// llamarlo fuera del hilo de la interfaz). Ninguna subida se aplica sin el pico real: si no se
    /// conoce, se mide en ese momento. Si hace falta medir y no se puede, no se toca.
    ///
    /// POR QUÉ. Antes solo se medía si la fila no tenía pico real, y la fila podía no ser ya la del
    /// archivo: las filas se rehacen con sus valores al cambiar el nivel, y el deshacer de Enriquecer
    /// cambia el volumen sin que la pestaña se entere. Caso visto en revisión: un MP3 a −10,0 LUFS y
    /// −0,3 dBTP se baja 2 pasos (la fila queda en −13,0 y −3,3), «Deshacer última» lo devuelve a
    /// −10,0 y, sin volver a analizar, se elige «Nivel de club». Con la fila se aplicaba +1 paso: el
    /// archivo acababa a −8,5 LUFS y +1,2 dBTP, recortado al reproducir. Con la medida vigente está a
    /// 1 dB de la referencia y no se toca.
    /// </summary>
    internal static DecisionAjuste Decidir(LoudnessRow fila, Func<string, LoudnessResult?> guardada, Func<string, LoudnessResult?> medir)
    {
        var medida = false;
        if (guardada(fila.FilePath) is not { Ok: true } m)
        {
            if (medir(fila.FilePath) is not { Ok: true } nueva) return NoSePudoMedir(fila);
            m = nueva;
            medida = true;
        }

        // Una subida sin pico real: se mide (una medida recién hecha ya lo trae).
        if (fila.Target - m.Lufs > UmbralAjuste && m.TruePeakDb == null)
        {
            if (medida || medir(fila.FilePath) is not { Ok: true, TruePeakDb: not null } conPico) return NoSePudoMedir(fila);
            m = conPico;
            medida = true;
        }
        return new DecisionAjuste(PasosHasta(fila.Target, m), m.Lufs, m.PeakDb, m.TruePeakDb, medida);
    }

    private static DecisionAjuste NoSePudoMedir(LoudnessRow fila)
        => new(0, fila.Lufs, fila.PeakDb, fila.TruePeakDb, Medida: true, NoSePudoMedir: true);

    /// <summary>
    /// Pasos para llevar una medida a <paramref name="referencia"/>: ninguno si ya está dentro de la
    /// banda «Correcto» (<see cref="UmbralAjuste"/>), y las subidas limitadas por el pico, el real si
    /// se conoce (ver <see cref="PasosAplicables"/>).
    /// </summary>
    private static int PasosHasta(double referencia, LoudnessResult m)
    {
        var ganancia = referencia - m.Lufs;
        if (Math.Abs(ganancia) <= UmbralAjuste) return 0;
        var pico = m.TruePeakDb is { } t ? Math.Max(t, m.PeakDb) : m.PeakDb;
        return PasosAplicables(ganancia, pico);
    }

    /// <summary>
    /// Grabaciones que se aumentarían (MP3, no excluidas, desviadas más del umbral) y cuyo pico real
    /// aún no se conoce: las que tienen algún paso según el pico de muestra, y las que BeatTag bajó con
    /// el pico recortado (el pico guardado es el tope, pero el de verdad se desconoce y puede dejar
    /// recuperar parte de lo bajado). Son las únicas que hay que volver a medir para decidir con
    /// seguridad: el resto de medidas guardadas sigue valiendo. Una grabación con el pico recortado
    /// que nunca se ha ajustado no entra: su pico real no puede ser menor que el de muestra, así que
    /// medirlo no cambiaría nada.
    /// </summary>
    internal static List<LoudnessRow> SinPicoReal(IEnumerable<LoudnessRow> filas)
        => filas.Where(r => r.TruePeakDb == null && r.Ajustable && r.Exclusion == ExclusionVolumen.Ninguna
                            && r.Gain > UmbralAjuste
                            && (PasosAplicables(r.Gain, r.PeakDb) > 0 || r.PicoDesconocidoTrasBajar))
                .ToList();

    /// <summary>
    /// Mide el pico real de lo que se aumentaría y aún no lo tiene, y rehace la tabla con él. Va en
    /// segundo plano y respeta la cancelación. Con el pico real puede bajar la referencia automática;
    /// por eso se repite una vez (al bajar la referencia no aparecen subidas nuevas, pero no cuesta
    /// nada comprobarlo).
    /// </summary>
    private async Task ComprobarPicoRealAsync(CancellationToken ct)
    {
        for (var ronda = 0; ronda < 2; ronda++)
        {
            var faltan = SinPicoReal(Rows);
            if (faltan.Count == 0) return;
            var texto = AjusteVolumenSeleccion.TextoComprobandoPicoReal(faltan.Count);
            Status = texto;
            _engine.Logger.Detail($"Volumen: midiendo el pico real de {faltan.Count} grabaciones que se aumentarían");
            var prog = new Progress<(int done, int total, string file)>(p =>
            {
                Progress = p.total == 0 ? 0 : p.done * 100.0 / p.total;
                Status = $"{texto}  {p.done} de {p.total}";
            });
            await _engine.Loudness.MeasureManyAsync(faltan.Select(r => r.FilePath).ToList(), force: true, prog, ct);
            RecargarDesdeCache(recalcularReferencia: true, await MedidasGuardadasAsync());
        }
    }

    /// <summary>
    /// Tras cambiar el nivel de referencia o las exclusiones puede haber grabaciones que se
    /// aumentarían sin pico real conocido. NO se miden en ese momento: elegir «Nivel de club» lanzaba
    /// sin avisar la decodificación de cientos de canciones con todos los núcleos menos uno, y Analizar
    /// y Ajustar quedaban desactivados mientras duraba. Se dice cuántas son; su pico real se mide al
    /// ajustarlas (última barrera, ver <see cref="Decidir"/>) o al pulsar Analizar. Devuelve el aviso
    /// ("" si no hay ninguna), para añadirlo al mensaje de quien llama.
    /// </summary>
    private string PicoRealPendiente()
        => IsBusy || Rows.Count == 0 ? "" : AjusteVolumenSeleccion.TextoPicoRealPendiente(SinPicoReal(Rows).Count);

    /// <summary>Tras cambiar el nivel de referencia: el aviso de <see cref="PicoRealPendiente"/>, si lo hay.</summary>
    private void AvisarPicoRealPendiente()
    {
        var aviso = PicoRealPendiente();
        if (aviso.Length > 0) Status = aviso;
    }

    /// <summary>El mensaje de una exclusión con el aviso de <see cref="PicoRealPendiente"/> detrás, si lo hay.</summary>
    private string ConPicoRealPendiente(string mensaje)
    {
        var aviso = PicoRealPendiente();
        return aviso.Length == 0 ? mensaje : mensaje + " " + aviso;
    }

    /// <summary>
    /// Ajusta el volumen de las grabaciones elegidas (por defecto, las marcadas). Modifica los
    /// archivos, pero sin recodificar (no hay pérdida de calidad) y dejando manifiesto para poder
    /// deshacerlo. Quien llama ya ha confirmado con el usuario exactamente esta lista.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<LoudnessRow>? elegidas = null)
    {
        if (IsBusy) return;
        var objetivo = (elegidas ?? ParaAjustar()).ToList();
        if (objetivo.Count == 0)
        {
            // Que no haya nada que ajustar, que se haya desmarcado o excluido todo y que lo desviado
            // no sea ajustable son cosas distintas, y el usuario merece saber cuál le está pasando.
            Status = AjusteVolumenSeleccion.TextoNadaQueAjustar(Elegir(), porSeleccion: false, DesviadasNoMp3);
            return;
        }

        IsBusy = true;
        _engine.ReleaseAudio();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Progress = 0;

        // El nombre del manifiesto lo da AppPaths: antes se componía aquí a mano, y dos ajustes en el
        // mismo segundo acababan mezclados en un solo manifiesto.
        var manifiesto = _engine.Paths.NuevoManifiesto();
        _engine.Logger.Head($"Volumen: ajustando {objetivo.Count} grabaciones (referencia {Target:0.0} LUFS)");

        int hechas = 0, parciales = 0, fallidas = 0, sinMargen = 0, enNivel = 0, sinLeer = 0, i = 0;
        var sinHistorial = false;
        try
        {
            Directory.CreateDirectory(_engine.Paths.UndoDir);
            foreach (var fila in objetivo)
            {
                if (ct.IsCancellationRequested) break;
                i++;
                Progress = i * 100.0 / objetivo.Count;
                Status = $"Ajustando {i} de {objetivo.Count}…  {fila.FileName}";

                // Con la medida vigente del archivo; si hay que medirlo (ha cambiado, o va a subir sin
                // pico real conocido) se decodifica fuera de la interfaz. Cancelar a mitad de esa
                // medida es como cancelar entre dos canciones.
                DecisionAjuste decision;
                try
                {
                    decision = await Task.Run(() => Decidir(fila, _engine.Loudness.Get,
                                                            p => _engine.Loudness.Measure(p, force: true, ct)), ct);
                }
                catch (OperationCanceledException) { break; }
                if (decision.NoSePudoMedir)
                {
                    sinLeer++;
                    _engine.Logger.Err($"Volumen: no se pudo leer '{fila.FileName}' para medirla antes de ajustarla; se deja como estaba");
                    continue;
                }
                var pasos = decision.Pasos;
                var ganancia = fila.Target - decision.Lufs;
                if (pasos == 0)
                {
                    if (Math.Abs(ganancia) <= UmbralAjuste) enNivel++;   // había cambiado y ya no lo requiere
                    else if (ganancia > 0) sinMargen++;
                    continue;
                }

                var sello = SelloArchivo.De(fila.FilePath);   // la huella y la identificación siguen valiendo
                var res = await Task.Run(() => Mp3Gain.Apply(fila.FilePath, pasos), ct);
                if (!res.Ok)
                {
                    fallidas++;
                    _engine.Logger.Err($"Volumen: no se pudo ajustar '{fila.FileName}': {res.Error}");
                    continue;
                }

                // El archivo ya ha cambiado, pase lo que pase después con el historial: la medida se
                // pone al día ahora, sin volver a analizarlo (con el pico en el tope y sin pico real si
                // estaba recortado, ver LoudnessScanner.TrasAjustar). Antes iba detrás del manifiesto,
                // y si este fallaba la fila volvía con el nivel de antes, marcada: un segundo «Ajustar»
                // le aplicaba los mismos pasos otra vez, y la ganancia no quedaba anotada.
                _engine.VolumenAjustado(fila.FilePath, sello);
                _engine.Loudness.Ajustado(fila.FilePath, decision.Lufs, decision.PicoMuestra, decision.PicoReal, res.Db);
                hechas++;
                _marcasAMano.Remove(fila.FilePath);   // ya ajustada: su casilla vuelve a la regla de siempre
                if (EsParcial(ganancia, pasos)) parciales++;

                // El ajuste se anuncia como reversible. Si el historial no se puede escribir, esa
                // promesa deja de ser cierta a partir de aquí, así que se para: seguir tocando
                // archivos que ya no se podrían devolver a su sitio no es decisión de la máquina.
                if (!EscribirManifiesto(manifiesto, fila.FilePath, pasos))
                {
                    sinHistorial = true;
                    break;
                }
                _engine.Logger.Detail($"    {fila.FileName}: {res.Db:+0.0;-0.0} dB ({res.Frames} tramas)");
            }

            _engine.Loudness.Save();
            RecargarDesdeCache(recalcularReferencia: false, await MedidasGuardadasAsync());

            Status = $"Ajustadas {hechas} de {objetivo.Count}"
                   + (parciales > 0 ? $" · {parciales} solo en parte (sin margen para más)" : "")
                   + AjusteVolumenSeleccion.TextoSinMargen(sinMargen)
                   + AjusteVolumenSeleccion.TextoYaEnNivel(enNivel)
                   + AjusteVolumenSeleccion.TextoSinLeer(sinLeer)
                   + AjusteVolumenSeleccion.TextoFallidas(fallidas)
                   + (sinHistorial
                        ? ". ⚠ SE DETUVO: no se pudo escribir el historial para deshacer, y la última canción ajustada NO se puede revertir automáticamente. Revisa que la carpeta de datos tenga permiso de escritura."
                        : $". Se puede deshacer desde Enriquecer. {Resumen}");
            if (sinHistorial) _engine.Logger.Err($"Volumen: detenido tras {hechas} por no poder escribir el manifiesto de deshacer");
            _engine.Logger.Sum($"Volumen: ajustadas {hechas}, parciales {parciales}, sin margen por el pico real {sinMargen}, "
                             + $"ya en nivel {enNivel}, sin poder leerlas {sinLeer}, fallidas {fallidas}");
        }
        catch (OperationCanceledException)
        {
            // Lo ya ajustado tiene que verse con su nivel nuevo: con la medida anterior en la tabla,
            // un segundo «Ajustar» volvería a cambiarlo.
            GuardarYRecargarTrasInterrupcion(hechas);
            Status = hechas == 1 ? "Ajuste cancelado (1 aplicada)." : $"Ajuste cancelado ({hechas} aplicadas).";
        }
        catch (Exception e)
        {
            GuardarYRecargarTrasInterrupcion(hechas);
            Status = "Error durante el ajuste: " + e.Message;
            _engine.Logger.Error("Volumen: fallo al ajustar", e);
        }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    private void GuardarYRecargarTrasInterrupcion(int hechas)
    {
        if (hechas == 0) return;
        try
        {
            _engine.Loudness.Save();
            RecargarDesdeCache(recalcularReferencia: false);
        }
        catch (Exception e) { _engine.Logger.Error("Volumen: no se pudo rehacer la tabla tras interrumpir el ajuste", e); }
    }

    /// <summary>
    /// Anota el cambio con el mismo formato que usa Enriquecer, para poder revertirlo. Devuelve
    /// false si no se pudo anotar: sin esa línea el ajuste ya NO se puede deshacer, y quien llama
    /// tiene que enterarse en vez de seguir modificando archivos con una promesa que no se cumple.
    /// </summary>
    private bool EscribirManifiesto(string archivo, string ruta, int pasos)
    {
        try
        {
            // Se guardan como diferencia (Nuevo − Anterior) para que deshacer aplique el inverso.
            var cambio = pasos >= 0
                ? FieldChange.Num(0, (uint)pasos)
                : FieldChange.Num((uint)(-pasos), 0);

            var rec = new UndoRecord
            {
                OrigPath = ruta,
                FinalPath = ruta,
                Renamed = false,
                Fields = new Dictionary<string, FieldChange> { [UndoEngine.VolumeField] = cambio },
                Origen = nameof(TipoEjecucion.Volumen),
            };

            File.AppendAllText(archivo,
                System.Text.Json.JsonSerializer.Serialize(rec) + Environment.NewLine,
                System.Text.Encoding.UTF8);
            return true;
        }
        catch (Exception e) { _engine.Logger.Error("Volumen: no se pudo anotar el manifiesto", e); return false; }
    }

    /// <summary>
    /// Rehace la tabla con las medidas guardadas: tras el ajuste, o tras medir el pico real. En el
    /// segundo caso, en el modo automático, también la referencia (el alcance de cada grabación
    /// depende de su pico). Tras ajustar NO se recalcula: la referencia con la que se ajustó sigue
    /// siendo la de la tabla hasta el siguiente análisis.
    ///
    /// Una fila cuya medida ya no vale (el archivo ha cambiado desde que se midió) se queda con sus
    /// valores, pero como pendiente de medir y fuera del ajuste (ver <see cref="LoudnessRow.MedidaCaducada"/>):
    /// antes se rehacía como si siguieran siendo los del archivo, y salía marcada para ajustar.
    /// </summary>
    /// <param name="recalcularReferencia">Recalcular también la referencia automática.</param>
    /// <param name="medidas">Las medidas guardadas de las filas, ya leídas (ver <see cref="MedidasGuardadasAsync"/>); si falta, se leen aquí.</param>
    private void RecargarDesdeCache(bool recalcularReferencia, IReadOnlyDictionary<string, LoudnessResult?>? medidas = null)
    {
        var excl = Exclusiones();
        var filas = new List<LoudnessRow>(Rows.Count);
        foreach (var r in Rows)
        {
            var m = medidas != null && medidas.TryGetValue(r.FilePath, out var leida) ? leida : _engine.Loudness.Get(r.FilePath);
            filas.Add(m is { Ok: true } v
                ? Fila(r.FileName, r.Folder, r.FilePath, v.Lufs, v.PeakDb, v.TruePeakDb, v.GananciaAplicada, excl)
                : Fila(r.FileName, r.Folder, r.FilePath, r.Lufs, r.PeakDb, r.TruePeakDb, r.GananciaAplicada, excl, caducada: true));
        }
        _filas.Reemplazar(filas);
        if (recalcularReferencia && EsAutomatico && Rows.Count > 0) ActualizarReferenciaAutomatica();
        else if (EsAutomatico && Rows.Count > 0)
        {
            // La referencia se queda, pero la explicación ya puede hablar del nivel de antes de ajustar.
            _hayAjustadas = CuentanParaLaReferencia(Rows).Any(r => r.GananciaAplicada != 0);
            ExplicarModo();
        }
        AplicarFiltro();
        Resumir();
    }

    /// <summary>
    /// Las medidas guardadas de todas las filas, leídas fuera del hilo de la interfaz: cada una
    /// comprueba la fecha y el tamaño del archivo, y con 20.000 filas son 0,2-0,3 s (medido en una
    /// biblioteca real) que antes bloqueaban la pestaña al terminar de medir el pico real y al ajustar.
    /// </summary>
    private async Task<Dictionary<string, LoudnessResult?>> MedidasGuardadasAsync()
    {
        var rutas = Rows.Select(r => r.FilePath).ToList();
        return await Task.Run(() =>
        {
            var medidas = new Dictionary<string, LoudnessResult?>(rutas.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var ruta in rutas) medidas.TryAdd(ruta, _engine.Loudness.Get(ruta));
            return medidas;
        });
    }

    // =============================================================================================
    // Qué se ajusta: casilla por fila, selección y exclusiones (E15, equipo «volumen_copias», tanda 4)
    // =============================================================================================

    /// <summary>
    /// Casillas que el usuario ha cambiado a mano en esta sesión, por ruta. Cambiar el nivel de
    /// referencia rehace la tabla entera, y sin esto volvería a marcar lo que se acaba de desmarcar.
    /// </summary>
    private readonly Dictionary<string, bool> _marcasAMano = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las exclusiones guardadas, para la lista visible con su botón «Quitar».</summary>
    public ObservableCollection<ExclusionVolumenFila> ExclusionesGuardadas { get; } = new();

    /// <summary>«Exclusiones del ajuste: 2 carpetas y 1 canción».</summary>
    [ObservableProperty] private string _tituloExclusiones = "";

    /// <summary>«143 grabaciones marcadas para ajustar».</summary>
    [ObservableProperty] private string _textoMarcadas = "";

    /// <summary>Las exclusiones de la configuración, listas para mirar muchas filas seguidas.</summary>
    private (List<string> Carpetas, HashSet<string> Canciones) Exclusiones()
        => (_engine.Config.VolumenCarpetasExcluidas.ToList(),
            new HashSet<string>(_engine.Config.VolumenCancionesExcluidas, StringComparer.OrdinalIgnoreCase));

    /// <summary>Una fila nueva, con su exclusión y su casilla como tocan.</summary>
    /// <param name="caducada">Sus valores ya no son los del archivo (ver <see cref="LoudnessRow.MedidaCaducada"/>).</param>
    private LoudnessRow Fila(string nombre, string carpeta, string ruta, double lufs, double pico, double? picoReal,
                             double aplicada, (List<string> Carpetas, HashSet<string> Canciones) excl, bool caducada = false)
    {
        var r = new LoudnessRow
        {
            FileName = nombre, Folder = carpeta, FilePath = ruta, Lufs = lufs, PeakDb = pico, TruePeakDb = picoReal,
            GananciaAplicada = aplicada, Target = Target, MedidaCaducada = caducada,
            Exclusion = AjusteVolumenSeleccion.De(ruta, excl.Carpetas, excl.Canciones),
        };
        r.Marcar(AjusteVolumenSeleccion.MarcaInicial(r.PuedeMarcarse, _marcasAMano.TryGetValue(ruta, out var m) ? m : null));
        r.AlMarcarAMano = AlMarcarAMano;
        return r;
    }

    private void AlMarcarAMano(LoudnessRow r)
    {
        _marcasAMano[r.FilePath] = r.Ajustar;
        RecontarMarcadas();
    }

    private void RecontarMarcadas()
        => TextoMarcadas = Rows.Count == 0 ? "" : AjusteVolumenSeleccion.TextoMarcadas(Rows.Count(r => r.Ajustar && r.PuedeMarcarse));

    /// <summary>Rehace la lista visible de exclusiones a partir de la configuración.</summary>
    private void RefrescarListaExclusiones()
    {
        var cfg = _engine.Config;
        // Si una canción excluida sigue en la biblioteca solo se sabe con la biblioteca leída; sin
        // ella no se dice nada en vez de dar por perdidas todas.
        HashSet<string>? enBiblioteca = null;
        if (cfg.VolumenCancionesExcluidas.Count > 0 && _engine.Library.IsScanned)
            enBiblioteca = new HashSet<string>(_engine.Library.Tracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);

        ExclusionesGuardadas.Clear();
        foreach (var c in cfg.VolumenCarpetasExcluidas.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            ExclusionesGuardadas.Add(new ExclusionVolumenFila { Ruta = c, EsCarpeta = true });
        foreach (var c in cfg.VolumenCancionesExcluidas.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            ExclusionesGuardadas.Add(new ExclusionVolumenFila
            {
                Ruta = c, EsCarpeta = false, FueraDeLaBiblioteca = enBiblioteca != null && !enBiblioteca.Contains(c),
            });
        TituloExclusiones = AjusteVolumenSeleccion.TituloExclusiones(cfg.VolumenCarpetasExcluidas.Count, cfg.VolumenCancionesExcluidas.Count);
    }

    /// <summary>
    /// Vuelve a mirar la exclusión de cada fila tras cambiar la lista, sin rehacer la tabla. Devuelve
    /// cuántas de las que requerían ajuste han quedado excluidas.
    /// </summary>
    private int AplicarExclusiones()
    {
        var excl = Exclusiones();
        var nuevas = 0;
        foreach (var r in Rows)
        {
            var antes = r.Exclusion;
            var ahora = AjusteVolumenSeleccion.De(r.FilePath, excl.Carpetas, excl.Canciones);
            if (antes == ahora) continue;
            r.Exclusion = ahora;
            if (antes == ExclusionVolumen.Ninguna && r.Candidata) nuevas++;
            r.Marcar(AjusteVolumenSeleccion.MarcaInicial(r.PuedeMarcarse, _marcasAMano.TryGetValue(r.FilePath, out var m) ? m : null));
        }
        RefrescarListaExclusiones();
        RecontarMarcadas();

        // Las excluidas no cuentan para la referencia automática: excluir o quitar una exclusión puede
        // moverla (si cambia, OnTargetChanged rehace la tabla). Lo que pase a aumentarse sin pico real
        // medido no se mide aquí: quien llama lo añade a su mensaje (ver PicoRealPendiente).
        if (EsAutomatico && Rows.Count > 0) ActualizarReferenciaAutomatica();
        return nuevas;
    }

    /// <summary>Guarda la configuración tras cambiar las exclusiones; "" si fue bien, o el aviso para el estado.</summary>
    private string GuardarExclusiones()
        => _engine.SaveConfig(out var error) ? "" : " ⚠ No se pudo guardar en la configuración: " + error;

    /// <summary>«Excluir esta carpeta del ajuste»: la carpeta de cada canción elegida, con sus subcarpetas.</summary>
    public void ExcluirCarpetas(IReadOnlyCollection<LoudnessRow> filas)
    {
        if (IsBusy) { Status = "Espera a que termine la operación en curso."; return; }
        var carpetas = AjusteVolumenSeleccion.CarpetasDe(filas);
        if (carpetas.Count == 0) { Status = "Elige antes una canción de la tabla."; return; }

        var (lista, nuevas) = AjusteVolumenSeleccion.AnadirCarpetasConNombre(_engine.Config.VolumenCarpetasExcluidas, carpetas);
        if (nuevas.Count == 0) { Status = AjusteVolumenSeleccion.TextoCarpetasExcluidas(carpetas, 0, 0); return; }
        _engine.Config.VolumenCarpetasExcluidas = lista;
        var aviso = GuardarExclusiones();
        var excluidas = AplicarExclusiones();
        // Se nombran y se cuentan las que han entrado de verdad en la lista, no las pedidas.
        Status = ConPicoRealPendiente(AjusteVolumenSeleccion.TextoCarpetasExcluidas(nuevas, nuevas.Count, excluidas) + aviso);
        _engine.Logger.Detail($"Volumen: excluida{(nuevas.Count == 1 ? " 1 carpeta" : $"s {nuevas.Count} carpetas")} del ajuste: {string.Join(" · ", nuevas)}");
    }

    /// <summary>«Excluir del ajuste»: estas canciones, por su ruta.</summary>
    public void ExcluirCanciones(IReadOnlyCollection<LoudnessRow> filas)
    {
        if (IsBusy) { Status = "Espera a que termine la operación en curso."; return; }
        if (filas.Count == 0) { Status = "Elige antes una canción de la tabla."; return; }
        var (lista, anadidas) = AjusteVolumenSeleccion.AnadirCanciones(_engine.Config.VolumenCancionesExcluidas, filas.Select(f => f.FilePath));
        if (anadidas > 0)
        {
            _engine.Config.VolumenCancionesExcluidas = lista;
            var aviso = GuardarExclusiones();
            AplicarExclusiones();
            Status = ConPicoRealPendiente(AjusteVolumenSeleccion.TextoCancionesExcluidas(filas.Count, anadidas) + aviso);
        }
        else Status = AjusteVolumenSeleccion.TextoCancionesExcluidas(filas.Count, 0);
    }

    /// <summary>Quita una exclusión de la lista: sus canciones vuelven a marcarse si requieren ajuste.</summary>
    [RelayCommand]
    private void QuitarExclusion(ExclusionVolumenFila? fila)
    {
        if (fila == null) return;
        if (IsBusy) { Status = "Espera a que termine la operación en curso."; return; }
        var cfg = _engine.Config;
        var quitadas = fila.EsCarpeta
            ? cfg.VolumenCarpetasExcluidas.RemoveAll(c => Rutas.MismaCarpeta(c, fila.Ruta))
            : cfg.VolumenCancionesExcluidas.RemoveAll(c => string.Equals(c, fila.Ruta, StringComparison.OrdinalIgnoreCase));
        if (quitadas == 0) { RefrescarListaExclusiones(); return; }
        var aviso = GuardarExclusiones();
        AplicarExclusiones();
        var nombre = fila.EsCarpeta ? Path.GetFileName(fila.Ruta.TrimEnd('\\', '/')) : Path.GetFileName(fila.Ruta);
        Status = ConPicoRealPendiente((fila.EsCarpeta
                    ? $"Quitada la exclusión de la carpeta «{nombre}»: sus grabaciones vuelven a marcarse si requieren ajuste."
                    : $"Quitada la exclusión de «{nombre}»: vuelve a marcarse si requiere ajuste.") + aviso);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsBusy) { Status = "Hay un análisis en curso."; return; }
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
}
