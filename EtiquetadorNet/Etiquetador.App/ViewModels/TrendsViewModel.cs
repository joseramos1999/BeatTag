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
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>Una canción del chart cruzada con tu biblioteca.</summary>
public sealed partial class TrendRow : ObservableObject
{
    private static readonly IBrush TengoBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xA3, 0x4A));

    public int Position { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string DurText { get; init; } = "";

    /// <summary>Ruta del archivo de tu biblioteca, si la tienes.</summary>
    public string FilePath { get; init; } = "";
    public string FileName => FilePath.Length > 0 ? Path.GetFileName(FilePath) : "";
    public bool Tengo => FilePath.Length > 0;
    public string Estado => Tengo ? "En la biblioteca" : "No disponible";

    public IBrush StateBrush => Tengo ? TengoBrush : Brushes.Transparent;
}

/// <summary>
/// Pestaña Tendencias: qué suena ahora en cada país y cuánto de eso tienes ya.
/// Se usa Deezer porque sus listas son públicas y sin clave (Spotify cerró el acceso a sus
/// playlists editoriales, Top 50 incluido, en noviembre de 2024).
/// </summary>
public partial class TrendsViewModel : ViewModelBase
{
    private readonly AppEngine _engine;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ChartCountry> Countries { get; } = new();
    public ObservableCollection<TrendRow> Rows { get; } = new();

    /// <summary>Vista de la tabla (permite filtrar por "solo las que tengo").</summary>
    public Avalonia.Collections.DataGridCollectionView RowsView { get; }

    [ObservableProperty] private ChartCountry? _selectedCountry;
    [ObservableProperty] private TrendRow? _selectedRow;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _soloLasQueTengo;
    [ObservableProperty] private int _listSize = 50;
    [ObservableProperty] private string _status = "Selecciona un país y pulsa Ver tendencias.";
    [ObservableProperty] private string _resumen = "";

    public int[] ListSizes { get; } = { 20, 50, 100 };

    public TrendsViewModel(AppEngine engine)
    {
        _engine = engine;
        RowsView = new Avalonia.Collections.DataGridCollectionView(Rows);
    }

    partial void OnSoloLasQueTengoChanged(bool value)
    {
        RowsView.Filter = value ? o => o is TrendRow r && r.Tengo : null;
        RowsView.Refresh();
    }

    /// <summary>Carga la lista de países la primera vez que se abre la pestaña.</summary>
    public async Task EnsureCountriesAsync()
    {
        if (Countries.Count > 0 || IsBusy) return;
        IsBusy = true;
        Status = "Cargando países…";
        _engine.Logger.Detail("Tendencias: pidiendo la lista de países…");
        try
        {
            var paises = await _engine.Charts.GetCountriesAsync();
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
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        // Red de seguridad: si por lo que sea no se cargaron al abrir la pestaña, se cargan aquí.
        if (Countries.Count == 0) await EnsureCountriesAsync();
        if (SelectedCountry is not { } pais) { Status = "Selecciona antes un país."; return; }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Status = $"Consultando el Top {ListSize} de {pais.Name}…";
        _engine.Logger.Head($"Tendencias: Top {ListSize} de {pais.Name}");
        try
        {
            if (!_engine.Library.IsScanned && _engine.Library.Folders.Count > 0)
            {
                Status = "Escaneando biblioteca…";
                await _engine.Library.ScanAsync();
            }

            var chart = await _engine.Charts.GetChartAsync(pais.PlaylistId, ListSize, _cts.Token);
            var indice = BuildLibraryIndex();

            Rows.Clear();
            foreach (var t in chart)
            {
                indice.TryGetValue(Clave(t.Artist, t.Title), out var ruta);
                Rows.Add(new TrendRow
                {
                    Position = t.Position, Artist = t.Artist, Title = t.Title,
                    DurText = t.DurText, FilePath = ruta ?? "",
                });
            }

            RowsView.Refresh();
            var tengo = Rows.Count(r => r.Tengo);
            Resumen = $"{tengo} de {Rows.Count} disponibles en la biblioteca ({(Rows.Count == 0 ? 0 : tengo * 100 / Rows.Count)}%)";
            Status = $"Top {Rows.Count} de {pais.Name}. {Resumen}. Faltan {Rows.Count - tengo}.";
            _engine.Logger.Sum($"Tendencias {pais.Name}: tienes {tengo} de {Rows.Count}");
        }
        catch (OperationCanceledException) { Status = "Consulta cancelada."; }
        catch (Exception e) { Status = "No se pudieron cargar las tendencias: " + e.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    /// <summary>
    /// Índice de la biblioteca por artista+título normalizados. Se indexa tanto por los tags como
    /// por el nombre del archivo, porque en material de DJ los tags no siempre están.
    /// </summary>
    private Dictionary<string, string> BuildLibraryIndex()
    {
        var idx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in _engine.Library.Tracks)
        {
            if (!string.IsNullOrEmpty(t.Artist) && !string.IsNullOrEmpty(t.Title))
                idx.TryAdd(Clave(t.Artist, t.Title), t.FilePath);

            var pr = Core.Pipeline.FileNameParser.Parse(t.FileName);
            if (pr.FnArtist.Length > 0 && pr.QTitle.Length > 0)
                idx.TryAdd(Clave(pr.FnArtist, pr.QTitle), t.FilePath);
        }
        return idx;
    }

    /// <summary>Clave de comparación: artista principal + título, sin adornos ni versiones.</summary>
    private static string Clave(string artist, string title)
    {
        var a = TextUtils.Nk(PrimerArtista(artist));
        var t = TextUtils.Nk(Descriptors.CleanKeywords(title));
        return a + "|" + t;
    }

    private static string PrimerArtista(string s)
        => System.Text.RegularExpressions.Regex.Split(s ?? "", @"(?i)\s*(?:,| x | vs\.?| feat\.?| ft\.?|&)\s*")
                 .FirstOrDefault()?.Trim() ?? "";

    /// <summary>Copia a una carpeta las canciones del chart que ya tienes (no las mueve).</summary>
    public async Task CopyToFolderAsync(string destino)
    {
        if (IsBusy) return;
        var tengo = Rows.Where(r => r.Tengo).ToList();
        if (tengo.Count == 0) { Status = "Ninguna de esta lista está en la biblioteca."; return; }

        IsBusy = true;
        _engine.ReleaseAudio();
        Status = $"Copiando {tengo.Count} canciones…";
        try
        {
            var (copiadas, saltadas, errores) = await Task.Run(() =>
            {
                int ok = 0, skip = 0, err = 0;
                Directory.CreateDirectory(destino);
                foreach (var r in tengo)
                {
                    try
                    {
                        // El número de posición delante mantiene el orden del chart en el explorador.
                        var nombre = $"{r.Position:00} - {Path.GetFileName(r.FilePath)}";
                        var d = Path.Combine(destino, nombre);
                        if (File.Exists(d)) { skip++; continue; }
                        File.Copy(r.FilePath, d);   // COPIA: tu biblioteca no se toca
                        ok++;
                    }
                    catch { err++; }
                }
                return (ok, skip, err);
            });

            Status = $"Copiadas {copiadas} en «{Path.GetFileName(destino)}»"
                   + (saltadas > 0 ? $" · {saltadas} ya estaban" : "")
                   + (errores > 0 ? $" · {errores} con error" : "")
                   + ". La biblioteca original no se modifica.";
            _engine.Logger.Sum($"Tendencias: copiadas {copiadas} a {destino}");
        }
        catch (Exception e) { Status = "No se pudo copiar: " + e.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsBusy) { Status = "Espera a que termine el proceso en curso."; return; }
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) { Status = "Esa grabación no está en la biblioteca."; return; }
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) { Status = "Esa grabación no está en la biblioteca."; return; }
        await Shell.OpenContainingFolderAsync(path);
    }
}
