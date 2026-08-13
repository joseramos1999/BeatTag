using System;
using System.Collections.ObjectModel;

using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;

namespace Etiquetador.App.ViewModels;

/// <summary>Pestaña Ajustes: claves de API (cifradas DPAPI), caché, prueba de conexión y registro (log).</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private const int MaxLogLines = 500;
    private readonly AppEngine _engine;

    [ObservableProperty] private string _spotifyId = "";
    [ObservableProperty] private string _spotifySecret = "";
    [ObservableProperty] private string _discogsToken = "";
    [ObservableProperty] private string _acoustIdKey = "";
    [ObservableProperty] private string _aiModel = "";
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private bool _cache = true;

    /// <summary>Modelos de IA local detectados en este equipo.</summary>
    public ObservableCollection<string> AiModels { get; } = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _testReport = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Registro en vivo (mensajes del motor: proveedores, IA, fpcalc, deshacer…).</summary>
    public ObservableCollection<string> Log { get; } = new();

    public SettingsViewModel(AppEngine engine)
    {
        _engine = engine;
        var c = engine.Config;
        _spotifyId = c.SpotifyId;
        _spotifySecret = c.SpotifySecret;
        _discogsToken = c.DiscogsToken;
        _acoustIdKey = c.AcoustIdKey;
        _aiModel = c.AiModel;
        _cache = c.Cache;
        if (_aiModel.Length > 0) AiModels.Add(_aiModel);

        // El Logger puede emitir desde hilos de fondo -> marshalizar a la UI.
        _engine.Logger.OnLog += entry =>
            Dispatcher.UIThread.Post(() =>
            {
                Log.Add($"{entry.Time:HH:mm:ss}  {entry.Message}");
                while (Log.Count > MaxLogLines) Log.RemoveAt(0);
            });
    }

    private void PushToConfig()
    {
        var c = _engine.Config;
        c.SpotifyId = SpotifyId.Trim();
        c.SpotifySecret = SpotifySecret.Trim();
        c.DiscogsToken = DiscogsToken.Trim();
        c.AcoustIdKey = AcoustIdKey.Trim();
        c.AiModel = (AiModel ?? "").Trim();
        c.Cache = Cache;
    }

    /// <summary>Busca Ollama en este equipo y lista los modelos instalados.</summary>
    [RelayCommand]
    private async Task DetectAiAsync()
    {
        AiStatus = "Buscando…";
        var modelos = await _engine.Ai.ListModelsAsync();
        if (modelos == null)
        {
            AiStatus = "No se ha encontrado Ollama. Instálalo desde ollama.com y déjalo en marcha.";
            return;
        }
        if (modelos.Count == 0)
        {
            AiStatus = $"Ollama está en marcha, pero sin modelos. Ejecuta: ollama pull {Core.Ai.OllamaClient.DefaultModel}";
            return;
        }

        var previo = AiModel;
        AiModels.Clear();
        foreach (var m in modelos) AiModels.Add(m);
        // Conserva la elección anterior si sigue instalada; si no, la primera disponible.
        AiModel = modelos.Contains(previo) ? previo : modelos[0];
        AiStatus = $"{modelos.Count} modelo(s) disponible(s).";
    }

    [RelayCommand]
    private void Save()
    {
        PushToConfig();
        Status = _engine.SaveConfig(out var error)
            ? "Ajustes guardados (claves cifradas con DPAPI)."
            : "⚠ NO se guardó (config anterior intacta): " + error;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        PushToConfig();
        IsBusy = true;
        Status = "Probando servicios…";
        TestReport = "";
        try { TestReport = await _engine.Tester.RunAsync(_engine.Config); Status = "Prueba terminada."; }
        catch (System.Exception e) { TestReport = "Error: " + e.Message; Status = "Falló la prueba."; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    /// <summary>Vacía la caché de respuestas de API y la caché de escaneo (se regeneran al usar la app).</summary>
    [RelayCommand]
    private void ClearCache()
    {
        int n = 0;
        try
        {
            var cacheDir = _engine.Paths.CacheDir;
            if (Directory.Exists(cacheDir))
            {
                foreach (var f in Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories))
                    try { File.Delete(f); n++; } catch { }
                foreach (var d in Directory.EnumerateDirectories(cacheDir))
                    try { Directory.Delete(d, true); } catch { }
            }
            _engine.Library.ClearScanCache();   // memoria + archivo (no basta con borrar el .json)
            _engine.ClearAnalysisCache();       // resultados de análisis guardados
            Status = $"Caché vaciada ({n} respuestas + escaneo + análisis). Se regenerará al usar la app.";
        }
        catch (Exception e) { Status = "No se pudo vaciar del todo la caché: " + e.Message; }
    }

    /// <summary>Abre la carpeta de logs de esta sesión.</summary>
    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        await Shell.OpenFolderAsync(_engine.Paths.LogsDir);
        Status = "Carpeta de registros: " + _engine.Paths.LogsDir;
    }

    /// <summary>Vuelve a tener en cuenta las canciones descartadas con "Quitar de la lista".</summary>
    [RelayCommand]
    private void RestoreIgnored()
    {
        var n = _engine.Ignored.Count;
        if (n == 0) { Status = "No hay ninguna canción descartada."; return; }
        _engine.ClearIgnored();
        Status = $"Recuperadas {n} canciones descartadas. Vuelve a analizar en Enriquecer para verlas.";
    }

    /// <summary>Olvida qué canciones se aplicaron ya, para que vuelvan a proponerse al analizar.</summary>
    [RelayCommand]
    private void ForgetApplied()
    {
        var n = _engine.Applied.Count;
        if (n == 0) { Status = "No hay ninguna canción marcada como aplicada."; return; }
        _engine.Applied.Clear();
        Status = $"Olvidadas {n} canciones aplicadas. Volverán a salir al analizar.";
    }
}
