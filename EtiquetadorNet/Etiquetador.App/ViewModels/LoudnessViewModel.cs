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
        : Satura ? "Sin margen suficiente: aumentarla produciría distorsión"
        : "";

    public IBrush StateBrush =>
        Math.Abs(Gain) <= 1.0 ? OkBrush :
        Gain < 0 ? AltaBrush : BajaBrush;
}

/// <summary>
/// Pestaña Volumen: mide la sonoridad real (EBU R128) de la biblioteca y dice cuánto se desvía
/// cada canción del objetivo. De momento SOLO MIDE: no se toca ningún archivo.
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
        Rows.Clear();
        foreach (var r in previas)
            Rows.Add(new LoudnessRow
            {
                FileName = r.FileName, Folder = r.Folder, FilePath = r.FilePath,
                Lufs = r.Lufs, PeakDb = r.PeakDb, Target = Target,
            });
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
            foreach (var t in tracks)
            {
                var m = _engine.Loudness.Get(t.FilePath);
                if (m is not { Ok: true } r) continue;
                Rows.Add(new LoudnessRow
                {
                    FileName = t.FileName, Folder = t.Folder, FilePath = t.FilePath,
                    Lufs = r.Lufs, PeakDb = r.PeakDb, Target = Target,
                });
            }

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

    /// <summary>Grabaciones que se corregirían con los criterios actuales.</summary>
    public List<LoudnessRow> ParaAjustar()
        => Rows.Where(r => r.Ajustable)   // un FLAC o un WAV se miden, pero no se tocan
               .Where(r => Math.Abs(r.Gain) > UmbralAjuste)
               .Where(r => PasosAplicables(r.Gain, r.PeakDb) != 0)
               .ToList();

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
    /// Ajusta el volumen de las grabaciones desviadas. Modifica los archivos, pero sin recodificar
    /// (no hay pérdida de calidad) y dejando manifiesto para poder deshacerlo.
    /// </summary>
    public async Task ApplyAsync()
    {
        if (IsBusy) return;
        var objetivo = ParaAjustar();
        if (objetivo.Count == 0)
        {
            // Que no haya nada que ajustar y que lo desviado no sea ajustable son dos cosas
            // distintas, y el usuario merece saber cuál de las dos le está pasando.
            var noMp3 = Rows.Count(r => !r.Ajustable && Math.Abs(r.Gain) > UmbralAjuste);
            Status = noMp3 > 0
                ? $"No hay ningún MP3 que ajustar. {noMp3} grabaciones están desviadas, pero no son MP3: se miden, no se ajustan."
                : "No hay ninguna grabación que requiera ajuste.";
            return;
        }

        IsBusy = true;
        _engine.ReleaseAudio();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Progress = 0;

        var manifiesto = Path.Combine(_engine.Paths.UndoDir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
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
        Rows.Clear();
        foreach (var r in previas)
        {
            var m = _engine.Loudness.Get(r.FilePath);
            var lufs = m is { Ok: true } v ? v.Lufs : r.Lufs;
            var pico = m is { Ok: true } v2 ? v2.PeakDb : r.PeakDb;
            Rows.Add(new LoudnessRow
            {
                FileName = r.FileName, Folder = r.Folder, FilePath = r.FilePath,
                Lufs = lufs, PeakDb = pico, Target = Target,
            });
        }
        AplicarFiltro();
        Resumir();
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
