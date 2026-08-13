using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;

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

    /// <summary>En cristiano: qué le pasa a esta canción respecto a las demás.</summary>
    public string Estado =>
        Math.Abs(Gain) <= 1.0 ? "✔ Bien" :
        Gain > 3 ? "🔉 Muy floja" :
        Gain > 1 ? "🔉 Floja" :
        Gain < -3 ? "🔊 Muy alta" : "🔊 Alta";

    /// <summary>Qué habría que hacerle, en palabras y con la cifra al lado.</summary>
    public string Accion =>
        Math.Abs(Gain) <= 1.0 ? "—"
        : Gain > 0 ? $"Subir {Gain:0.0} dB" : $"Bajar {-Gain:0.0} dB";

    /// <summary>Si al aplicar esa ganancia el pico se pasaría de 0 dBFS (saturaría).</summary>
    public bool Satura => PeakDb + Gain > 0;

    /// <summary>Aviso sin jerga: por qué esta no se puede subir del todo.</summary>
    public string Aviso => Satura
        ? "No se puede subir tanto (distorsionaría)"
        : "";

    public IBrush StateBrush =>
        Math.Abs(Gain) <= 1.0 ? OkBrush :
        Gain < 0 ? AltaBrush : BajaBrush;
}

/// <summary>
/// Pestaña Volumen: mide la sonoridad real (EBU R128) de la biblioteca y dice cuánto se desvía
/// cada canción del objetivo. De momento SOLO MIDE: no se toca ningún archivo.
/// </summary>
public partial class LoudnessViewModel : ViewModelBase
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
        "Igualar mi música entre sí (recomendado)",
        "Nivel de club / sesión de DJ",
        "Nivel de plataformas (Spotify, YouTube…)",
        "Nivel de cine y televisión",
    };

    [ObservableProperty] private string _modo = "Igualar mi música entre sí (recomendado)";

    /// <summary>Nivel al que se compara todo. En el modo automático lo calcula la propia música.</summary>
    [ObservableProperty] private double _target = -14;

    /// <summary>Explicación del modo elegido, para que se entienda qué va a pasar.</summary>
    [ObservableProperty] private string _explicacionModo = "";

    private bool EsAutomatico => Modo.StartsWith("Igualar", StringComparison.Ordinal);

    partial void OnModoChanged(string value)
    {
        Target = value switch
        {
            var m when m.StartsWith("Nivel de club") => -11,
            var m when m.StartsWith("Nivel de plataformas") => -14,
            var m when m.StartsWith("Nivel de cine") => -23,
            _ => Rows.Count > 0 ? Mediana() : Target,   // automático: lo dice tu propia música
        };
        ExplicarModo();
        Recalcular();
    }

    private void ExplicarModo()
    {
        ExplicacionModo = EsAutomatico
            ? $"Se toma como referencia el volumen típico de tu propia música ({Target:0.0}), así que solo se corrigen las que se salen de la norma."
            : Modo.StartsWith("Nivel de club")
                ? "Nivel alto, pensado para sonar fuerte en un equipo de sala."
                : Modo.StartsWith("Nivel de plataformas")
                    ? "El nivel al que reproducen Spotify o YouTube. Más bajo que el habitual en música de baile."
                    : "Nivel de referencia de cine y televisión. Bastante más bajo que la música comercial.";
    }

    /// <summary>Volumen típico de la colección: la mediana aguanta bien los casos extremos.</summary>
    private double Mediana()
    {
        var orden = Rows.Select(r => r.Lufs).OrderBy(x => x).ToList();
        return orden.Count == 0 ? -14 : Math.Round(orden[orden.Count / 2], 1);
    }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _soloDesviadas;
    [ObservableProperty] private LoudnessRow? _selectedRow;
    [ObservableProperty] private string _status = "Pulsa Medir para conocer el volumen real de tu biblioteca.";
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
        if (_engine.Library.Folders.Count == 0) { Status = "Añade carpetas en Biblioteca."; return; }
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
                Status = $"Midiendo {p.done}/{p.total}…  {p.file}";
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
            Status = $"Medidas {Rows.Count} de {tracks.Count} en {TextUtils.FormatEta(sw.Elapsed.TotalSeconds)}. {Resumen}";
            _engine.Logger.Sum($"Volumen: {Rows.Count} medidas · {Resumen}");
        }
        catch (OperationCanceledException) { Status = $"Medición cancelada ({Rows.Count} medidas)."; }
        catch (Exception e)
        {
            Status = "Error al medir: " + e.Message;
            _engine.Logger.Error("Volumen: fallo al medir", e);
        }
        finally { IsBusy = false; _engine.Loudness.Save(); _cts?.Dispose(); _cts = null; }
    }

    /// <summary>Resumen en cristiano: cuántas están bien y cuántas se salen de verdad.</summary>
    private void Resumir()
    {
        if (Rows.Count == 0) { Resumen = ""; return; }
        var bien = Rows.Count(r => Math.Abs(r.Gain) <= 1.0);
        var flojas = Rows.Count(r => r.Gain > 1.0);
        var altas = Rows.Count(r => r.Gain < -1.0);
        var muyFuera = Rows.Count(r => Math.Abs(r.Gain) > 2.0);

        Resumen = muyFuera == 0
            ? $"Tu música ya suena pareja: las {Rows.Count} están en su sitio."
            : $"{bien} suenan bien · {flojas} se quedan flojas · {altas} destacan de más. "
              + $"Merece la pena ajustar {muyFuera}.";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsBusy) { Status = "Espera a que termine la medición."; return; }
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
