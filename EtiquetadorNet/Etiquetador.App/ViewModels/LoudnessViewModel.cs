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
    public double PeakDb { get; init; }

    /// <summary>Objetivo con el que se calculó la ganancia.</summary>
    public double Target { get; init; }

    public string LufsText => $"{Lufs:0.0}";
    public string PeakText => $"{PeakDb:0.0}";

    /// <summary>Cuánto hay que subir (+) o bajar (−) para llegar al objetivo.</summary>
    public double Gain => Target - Lufs;

    /// <summary>Situación de la grabación respecto al nivel de referencia.</summary>
    public string Estado =>
        Math.Abs(Gain) <= 1.0 ? "Correcto" :
        Gain > 3 ? "Muy bajo" :
        Gain > 1 ? "Bajo" :
        Gain < -3 ? "Muy alto" : "Alto";

    /// <summary>Corrección que necesitaría para alcanzar el nivel de referencia.</summary>
    public string Accion =>
        Math.Abs(Gain) <= 1.0 ? "—"
        : Gain > 0 ? $"Aumentar {Gain:0.0} dB" : $"Reducir {-Gain:0.0} dB";

    /// <summary>Si al aplicar esa ganancia el pico se pasaría de 0 dBFS (saturaría).</summary>
    public bool Satura => PeakDb + Gain > 0;

    /// <summary>
    /// El ajuste sin recodificar solo existe para MP3: consiste en retocar un campo de cada trama.
    /// Un FLAC o un WAV se miden igual de bien, pero cambiarles el volumen exigiría reescribir el
    /// audio, y eso ni es reversible ni se hace a espaldas del usuario. Se miden y se dejan.
    /// </summary>
    public bool Ajustable => Mp3Gain.EsAjustable(FilePath);

    /// <summary>Limitación aplicable a esta grabación, expresada sin tecnicismos.</summary>
    public string Aviso =>
        !Ajustable ? "Se mide, pero no se ajusta: solo los MP3 admiten el cambio sin pérdida"
        : Exclusion == ExclusionVolumen.Carpeta ? "Excluida del ajuste: su carpeta está en las exclusiones"
        : Exclusion == ExclusionVolumen.Cancion ? "Excluida del ajuste"
        : Satura ? "Sin margen suficiente: aumentarla produciría distorsión"
        : "";

    // --- Qué se ajusta (E15, equipo «volumen_copias», tanda 4) ---

    /// <summary>
    /// Requiere ajuste y se puede ajustar: MP3, desviada más del umbral y con algún paso que aplicar
    /// (una que solo podría subir y no tiene margen se queda fuera). Es el criterio de siempre.
    /// </summary>
    public bool Candidata => Ajustable && Math.Abs(Gain) > LoudnessViewModel.UmbralAjuste
                             && LoudnessViewModel.PasosAplicables(Gain, PeakDb) != 0;

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
        Math.Abs(Gain) <= 1.0 ? OkBrush :
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

    public ObservableCollection<LoudnessRow> Rows { get; } = new();
    public DataGridCollectionView RowsView { get; }

    /// <summary>
    /// Modos pensados para que no haya que saber qué es un LUFS. El primero es el recomendado
    /// porque se adapta SOLO a la colección de cada uno: no impone un número de fuera.
    /// </summary>
    public string[] Modos { get; } =
    {
        "Nivel medio de la biblioteca (recomendado)",
        "Nivel de club",
        "Nivel de plataformas de streaming",
        "Nivel de cine y televisión",
    };

    [ObservableProperty] private string _modo = "Nivel medio de la biblioteca (recomendado)";

    /// <summary>Nivel al que se compara todo. En el modo automático lo calcula la propia música.</summary>
    [ObservableProperty] private double _target = -14;

    /// <summary>Explicación del modo elegido, para que se entienda qué va a pasar.</summary>
    [ObservableProperty] private string _explicacionModo = "";

    private bool EsAutomatico => Modo.StartsWith("Nivel medio", StringComparison.Ordinal);

    partial void OnModoChanged(string value)
    {
        Target = value switch
        {
            var m when m.StartsWith("Nivel de club") => -11,
            var m when m.StartsWith("Nivel de plataformas") => -14,
            var m when m.StartsWith("Nivel de cine") => -23,
            _ => Rows.Count > 0 ? Mediana() : Target,   // el propio contenido fija la referencia
        };
        ExplicarModo();
        Recalcular();
    }

    private void ExplicarModo()
    {
        ExplicacionModo = EsAutomatico
            ? $"La referencia se calcula a partir de la propia biblioteca ({Target:0.0} LUFS). Se señalan únicamente las grabaciones que se apartan de ese nivel."
            : Modo.StartsWith("Nivel de club")
                ? "Nivel elevado, adecuado para equipos de sala."
                : Modo.StartsWith("Nivel de plataformas")
                    ? "Nivel de reproducción de Spotify y YouTube. Inferior al habitual en música de baile."
                    : "Nivel de referencia en cine y televisión. Considerablemente inferior al de la música comercial.";
    }

    /// <summary>Nivel representativo de la colección: la mediana no se distorsiona con los extremos.</summary>
    private double Mediana()
    {
        var orden = Rows.Select(r => r.Lufs).OrderBy(x => x).ToList();
        return orden.Count == 0 ? -14 : Math.Round(orden[orden.Count / 2], 1);
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
    }

    partial void OnSoloDesviadasChanged(bool value) => AplicarFiltro();
    partial void OnTargetChanged(double value) => Recalcular();

    private void AplicarFiltro()
    {
        RowsView.Filter = SoloDesviadas ? o => o is LoudnessRow r && Math.Abs(r.Gain) > 1.0 : null;
        RowsView.Refresh();
    }

    /// <summary>Al cambiar el objetivo no hay que volver a medir: solo se recalcula la desviación.</summary>
    private void Recalcular()
    {
        if (Rows.Count == 0) return;
        var previas = Rows.ToList();
        var excl = Exclusiones();
        Rows.Clear();
        foreach (var r in previas)
            Rows.Add(Fila(r.FileName, r.Folder, r.FilePath, r.Lufs, r.PeakDb, excl));
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var prog = new Progress<(int done, int total, string file)>(p =>
            {
                Progress = p.total == 0 ? 0 : p.done * 100.0 / p.total;
                Status = $"Analizando {p.done} de {p.total}…  {p.file}";
            });

            await _engine.Loudness.MeasureManyAsync(rutas, force, prog, _cts.Token);

            Rows.Clear();
            var excl = Exclusiones();
            foreach (var t in tracks)
            {
                var m = _engine.Loudness.Get(t.FilePath);
                if (m is not { Ok: true } r) continue;
                Rows.Add(Fila(t.FileName, t.Folder, t.FilePath, r.Lufs, r.PeakDb, excl));
            }
            RefrescarListaExclusiones();   // ya se sabe qué canciones excluidas siguen en la biblioteca

            // En el modo automático la referencia sale de la propia música, así que hasta ahora no
            // se podía saber: se calcula al terminar de medir y se rehace la tabla con ella.
            if (EsAutomatico && Rows.Count > 0)
            {
                Target = Mediana();
                ExplicarModo();
                Recalcular();
            }
            AplicarFiltro();
            Resumir();
            Progress = 100;
            Status = $"Analizadas {Rows.Count} de {tracks.Count} en {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}. {Resumen}";
            _engine.Logger.Sum($"Volumen: {Rows.Count} medidas · {Resumen}");
        }
        catch (OperationCanceledException) { Status = $"Análisis cancelado ({Rows.Count} grabaciones analizadas)."; }
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
        var correctas = Rows.Count(r => Math.Abs(r.Gain) <= 1.0);
        var bajas = Rows.Count(r => r.Gain > 1.0);
        var altas = Rows.Count(r => r.Gain < -1.0);
        var requierenAjuste = Rows.Count(r => Math.Abs(r.Gain) > 2.0);

        Resumen = requierenAjuste == 0
            ? $"El nivel es uniforme en las {Rows.Count} grabaciones analizadas."
            : $"{correctas} con el nivel correcto · {bajas} por debajo · {altas} por encima. "
              + $"{requierenAjuste} requieren ajuste.";
    }

    /// <summary>A partir de esta desviación (dB) merece la pena corregir; por debajo no se nota.</summary>
    public const double UmbralAjuste = 2.0;

    /// <summary>Margen que se deja sin usar para no llegar justo al límite de distorsión.</summary>
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
    public static bool AumentoParcial(LoudnessRow r)
        => r.Gain > 0 && r.Gain - PasosAplicables(r.Gain, r.PeakDb) * Mp3Gain.DbPerStep > Mp3Gain.DbPerStep / 2;

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

        int hechas = 0, parciales = 0, fallidas = 0, i = 0;
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

                var pasos = PasosAplicables(fila.Gain, fila.PeakDb);
                if (pasos == 0) continue;

                var sello = SelloArchivo.De(fila.FilePath);   // la huella y la identificación siguen valiendo
                var res = await Task.Run(() => Mp3Gain.Apply(fila.FilePath, pasos), ct);
                if (res.Ok) _engine.VolumenAjustado(fila.FilePath, sello);
                if (!res.Ok)
                {
                    fallidas++;
                    _engine.Logger.Err($"Volumen: no se pudo ajustar '{fila.FileName}': {res.Error}");
                    continue;
                }

                hechas++;
                _marcasAMano.Remove(fila.FilePath);   // ya ajustada: su casilla vuelve a la regla de siempre
                if (AumentoParcial(fila)) parciales++;

                // El ajuste se anuncia como reversible. Si el historial no se puede escribir, esa
                // promesa deja de ser cierta a partir de aquí, así que se para: seguir tocando
                // archivos que ya no se podrían devolver a su sitio no es decisión de la máquina.
                if (!EscribirManifiesto(manifiesto, fila.FilePath, pasos))
                {
                    sinHistorial = true;
                    break;
                }

                // El archivo ha cambiado: se actualiza la medida sin volver a analizarlo.
                _engine.Loudness.Update(fila.FilePath, fila.Lufs + res.Db, fila.PeakDb + res.Db);
                _engine.Logger.Detail($"    {fila.FileName}: {res.Db:+0.0;-0.0} dB ({res.Frames} tramas)");
            }

            _engine.Loudness.Save();
            RecargarDesdeCache();

            Status = $"Ajustadas {hechas} de {objetivo.Count}"
                   + (parciales > 0 ? $" · {parciales} solo en parte (sin margen para más)" : "")
                   + (fallidas > 0 ? $" · {fallidas} sin cambios" : "")
                   + (sinHistorial
                        ? ". ⚠ SE DETUVO: no se pudo escribir el historial para deshacer, y la última canción ajustada NO se puede revertir automáticamente. Revisa que la carpeta de datos tenga permiso de escritura."
                        : $". Se puede deshacer desde Enriquecer. {Resumen}");
            if (sinHistorial) _engine.Logger.Err($"Volumen: detenido tras {hechas} por no poder escribir el manifiesto de deshacer");
            _engine.Logger.Sum($"Volumen: ajustadas {hechas}, parciales {parciales}, fallidas {fallidas}");
        }
        catch (OperationCanceledException) { Status = $"Ajuste cancelado ({hechas} aplicadas)."; }
        catch (Exception e)
        {
            Status = "Error durante el ajuste: " + e.Message;
            _engine.Logger.Error("Volumen: fallo al ajustar", e);
        }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
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

    /// <summary>Rehace la tabla con las medidas actualizadas tras el ajuste.</summary>
    private void RecargarDesdeCache()
    {
        var previas = Rows.ToList();
        var excl = Exclusiones();
        Rows.Clear();
        foreach (var r in previas)
        {
            var m = _engine.Loudness.Get(r.FilePath);
            var lufs = m is { Ok: true } v ? v.Lufs : r.Lufs;
            var pico = m is { Ok: true } v2 ? v2.PeakDb : r.PeakDb;
            Rows.Add(Fila(r.FileName, r.Folder, r.FilePath, lufs, pico, excl));
        }
        AplicarFiltro();
        Resumir();
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
    private LoudnessRow Fila(string nombre, string carpeta, string ruta, double lufs, double pico,
                             (List<string> Carpetas, HashSet<string> Canciones) excl)
    {
        var r = new LoudnessRow
        {
            FileName = nombre, Folder = carpeta, FilePath = ruta, Lufs = lufs, PeakDb = pico, Target = Target,
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
        return nuevas;
    }

    /// <summary>Guarda la configuración tras cambiar las exclusiones; "" si fue bien, o el aviso para el estado.</summary>
    private string GuardarExclusiones()
        => _engine.SaveConfig(out var error) ? "" : " ⚠ No se pudo guardar en la configuración: " + error;

    /// <summary>«Excluir esta carpeta del ajuste»: la carpeta de cada canción elegida, con sus subcarpetas.</summary>
    public void ExcluirCarpetas(IReadOnlyCollection<LoudnessRow> filas)
    {
        if (IsBusy) { Status = "Espera a que termine la operación en curso."; return; }
        var carpetas = filas.Select(f => Path.GetDirectoryName(f.FilePath) ?? "")
                            .Where(d => d.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
        if (carpetas.Count == 0) { Status = "Elige antes una canción de la tabla."; return; }

        var (lista, anadidas) = AjusteVolumenSeleccion.AnadirCarpetas(_engine.Config.VolumenCarpetasExcluidas, carpetas);
        if (anadidas == 0) { Status = AjusteVolumenSeleccion.TextoCarpetasExcluidas(carpetas, 0, 0); return; }
        _engine.Config.VolumenCarpetasExcluidas = lista;
        var aviso = GuardarExclusiones();
        var excluidas = AplicarExclusiones();
        Status = AjusteVolumenSeleccion.TextoCarpetasExcluidas(carpetas, anadidas, excluidas) + aviso;
        _engine.Logger.Detail($"Volumen: excluida{(anadidas == 1 ? " 1 carpeta" : $"s {anadidas} carpetas")} del ajuste: {string.Join(" · ", carpetas)}");
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
            Status = AjusteVolumenSeleccion.TextoCancionesExcluidas(filas.Count, anadidas) + aviso;
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
        Status = (fila.EsCarpeta
                    ? $"Quitada la exclusión de la carpeta «{nombre}»: sus grabaciones vuelven a marcarse si requieren ajuste."
                    : $"Quitada la exclusión de «{nombre}»: vuelve a marcarse si requiere ajuste.") + aviso;
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
