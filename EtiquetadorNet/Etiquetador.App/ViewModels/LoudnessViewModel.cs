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
    public string GainText => $"{Gain:+0.0;-0.0;0.0} dB";

    /// <summary>Si al aplicar esa ganancia el pico se pasaría de 0 dBFS (saturaría).</summary>
    public bool Satura => PeakDb + Gain > 0;
    public string Aviso => Satura ? $"⚠ saturaría {PeakDb + Gain:+0.0} dB" : "";

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

    /// <summary>Objetivos habituales: streaming (-14), algo más caliente para club, y el de radio.</summary>
    public double[] Targets { get; } = { -23, -16, -14, -12, -9 };

    [ObservableProperty] private double _target = -14;
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

    private void Resumir()
    {
        if (Rows.Count == 0) { Resumen = ""; return; }
        var media = Rows.Average(r => r.Lufs);
        var enRango = Rows.Count(r => Math.Abs(r.Gain) <= 1.0);
        var bajas = Rows.Count(r => r.Gain > 1.0);
        var altas = Rows.Count(r => r.Gain < -1.0);
        var saturarian = Rows.Count(r => r.Satura);
        Resumen = $"media {media:0.0} LUFS · {enRango} en su sitio · {bajas} bajas · {altas} altas"
                + (saturarian > 0 ? $" · {saturarian} saturarían al subirlas" : "");
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
