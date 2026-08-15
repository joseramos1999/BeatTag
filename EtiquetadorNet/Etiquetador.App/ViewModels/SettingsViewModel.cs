using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

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
    [ObservableProperty] private string _aiHost = "";
    [ObservableProperty] private bool _aiBusy;
    [ObservableProperty] private double _aiProgress;
    [ObservableProperty] private bool _aiShowProgress;
    [ObservableProperty] private bool _cache = true;

    /// <summary>
    /// Lo que ofrece el desplegable: primero los modelos instalados, después los recomendados que
    /// aún no lo están. Va en UNA sola lista a propósito: con dos desplegables enlazados a la misma
    /// propiedad, elegir en uno un valor que el otro no tiene hace que este escriba null y borre la
    /// selección.
    /// </summary>
    public ObservableCollection<string> AiOpciones { get; } = new();

    /// <summary>Modelos instalados realmente (subconjunto de AiOpciones).</summary>
    private readonly HashSet<string> _instalados = new();

    // Recomendados, del más equilibrado al más liviano. El sufijo aclara que hay que descargarlos.
    private static readonly (string Modelo, string Nota)[] Sugeridos =
    {
        ("llama3.2",    "~2 GB · recomendado"),
        ("llama3.1:8b", "~4,7 GB · más preciso"),
        ("qwen2.5:3b",  "~1,9 GB · ligero"),
        ("gemma2:2b",   "~1,6 GB · el más liviano"),
    };

    // Ollama devuelve los modelos con etiqueta ("llama3.2:latest") mientras que los sugeridos se
    // escriben sin ella ("llama3.2"). Son el mismo modelo: sin esto aparecerían por duplicado en el
    // desplegable y uno ya descargado se anunciaría como no instalado.
    private static string SinEtiquetaLatest(string m)
        => m.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? m[..^":latest".Length] : m;

    private bool EstaInstalado(string modelo)
        => _instalados.Any(x => string.Equals(SinEtiquetaLatest(x), SinEtiquetaLatest(modelo),
                                              StringComparison.OrdinalIgnoreCase));

    /// <summary>Rehace la lista del desplegable conservando la elección actual si sigue siendo válida.</summary>
    private void RefrescarOpciones()
    {
        var elegido = AiModel ?? "";
        AiOpciones.Clear();
        foreach (var m in _instalados.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) AiOpciones.Add(m);
        foreach (var (m, _) in Sugeridos) if (!EstaInstalado(m)) AiOpciones.Add(m);
        // El valor guardado puede no estar ni instalado ni entre los sugeridos: se conserva igualmente.
        if (elegido.Length > 0 && !AiOpciones.Contains(elegido)) AiOpciones.Insert(0, elegido);
        AiModel = elegido;
    }

    /// <summary>Texto de ayuda del modelo seleccionado (si está instalado o cuánto ocupa descargarlo).</summary>
    public string AiModelNota
    {
        get
        {
            var m = (AiModel ?? "").Trim();
            if (m.Length == 0) return "";
            if (EstaInstalado(m)) return "Instalado y listo para usarse.";
            var s = Sugeridos.FirstOrDefault(x => x.Modelo == m);
            return s.Modelo != null
                ? $"No instalado ({s.Nota}). Pulsa «Descargar modelo»."
                : "No instalado. Pulsa «Descargar modelo».";
        }
    }

    partial void OnAiModelChanged(string? value) => OnPropertyChanged(nameof(AiModelNota));
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
        _aiHost = c.AiHost;
        _cache = c.Cache;
        RefrescarOpciones();

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
        c.AiHost = (AiHost ?? "").Trim();
        c.Cache = Cache;
        // La dirección se aplica en caliente: así "Detectar" y "Descargar" usan lo que hay en pantalla.
        _engine.Ai.Host = c.AiHost.Length > 0 ? c.AiHost : Core.Ai.OllamaClient.DefaultHost;
    }

    /// <summary>Busca Ollama en este equipo y lista los modelos instalados.</summary>
    [RelayCommand]
    private async Task DetectAiAsync()
    {
        PushToConfig();
        AiStatus = "Buscando…";
        var modelos = await _engine.Ai.ListModelsAsync();
        if (modelos == null)
        {
            AiStatus = "No se ha encontrado Ollama en este equipo. Puedes instalarlo con el botón «Instalar Ollama».";
            return;
        }
        if (modelos.Count == 0)
        {
            AiStatus = "Ollama está en marcha, pero no tiene ningún modelo. Descarga uno con «Descargar modelo».";
            return;
        }

        var previo = AiModel ?? "";
        _instalados.Clear();
        foreach (var m in modelos) _instalados.Add(m);
        // Conserva la elección anterior si sigue instalada; si no, la primera disponible.
        AiModel = previo.Length > 0 && EstaInstalado(previo) ? previo : modelos[0];
        RefrescarOpciones();
        AiStatus = $"Preparada. {modelos.Count} modelo(s) instalado(s).";
    }

    /// <summary>Instala Ollama con winget. Windows pedirá confirmación de administrador.</summary>
    [RelayCommand]
    private async Task InstallAiAsync()
    {
        if (!OllamaInstaller.HayWinget())
        {
            AiStatus = "Este Windows no tiene winget. Se abrirá la página oficial de descarga.";
            await Shell.OpenUrlAsync(OllamaInstaller.PaginaDescarga);
            return;
        }

        AiBusy = true;
        AiShowProgress = false;
        AiStatus = "Instalando Ollama… Windows pedirá confirmación de administrador.";
        try
        {
            var err = await OllamaInstaller.InstalarAsync(l => _engine.Logger.Detail("  winget: " + l));
            if (err.Length > 0)
            {
                AiStatus = "No se pudo instalar: " + err;
                return;
            }
            // Tras instalar, el servicio tarda un poco en levantar; se comprueba en vez de darlo por hecho.
            AiStatus = "Instalado. Comprobando que el servicio responde…";
            for (int i = 0; i < 10 && !await _engine.Ai.IsRunningAsync(); i++)
                await Task.Delay(1500);

            if (await _engine.Ai.IsRunningAsync()) await DetectAiAsync();
            else AiStatus = "Ollama se instaló, pero el servicio aún no responde. Ábrelo una vez y pulsa «Detectar».";
        }
        finally { AiBusy = false; }
    }

    /// <summary>Descarga el modelo seleccionado (son varios GB, con aviso de avance).</summary>
    [RelayCommand]
    private async Task PullModelAsync()
    {
        PushToConfig();
        var modelo = (AiModel ?? "").Trim();
        if (modelo.Length == 0) modelo = Core.Ai.OllamaClient.DefaultModel;

        if (!await _engine.Ai.IsRunningAsync())
        {
            AiStatus = "Ollama no responde. Instálalo o ábrelo antes de descargar un modelo.";
            return;
        }

        AiBusy = true;
        AiShowProgress = true;
        AiProgress = 0;
        AiStatus = $"Descargando «{modelo}»… Puede tardar: son varios GB.";
        try
        {
            var avance = new Progress<(string Estado, double Fraccion)>(p =>
            {
                AiProgress = p.Fraccion * 100;
                AiStatus = p.Fraccion > 0
                    ? $"Descargando «{modelo}»: {p.Estado} ({p.Fraccion:P0})"
                    : $"«{modelo}»: {p.Estado}";
            });
            var err = await _engine.Ai.PullModelAsync(modelo, avance);
            if (err.Length > 0) { AiStatus = "No se pudo descargar: " + err; return; }

            AiStatus = $"Modelo «{modelo}» listo.";
            await DetectAiAsync();
            AiModel = modelo;
        }
        finally { AiBusy = false; AiShowProgress = false; }
    }

    /// <summary>Abre el archivo de alias de artista (nombres distintos del mismo artista).</summary>
    [RelayCommand]
    private async Task OpenAliasesFileAsync()
    {
        // Basta con abrir la carpeta: el archivo se crea solo al arrancar y se edita con el bloc de notas.
        await Shell.OpenContainingFolderAsync(_engine.Paths.ArtistAliasesPath);
        Status = "Alias de artista: " + _engine.Paths.ArtistAliasesPath;
    }

    /// <summary>Abre el archivo de grafías especiales de artista.</summary>
    [RelayCommand]
    private async Task OpenExceptionsFileAsync()
    {
        await Shell.OpenContainingFolderAsync(_engine.Paths.ArtistExceptionsPath);
        Status = "Grafías de artista: " + _engine.Paths.ArtistExceptionsPath;
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
