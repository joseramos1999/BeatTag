using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Media;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Una línea del registro en pantalla. Se guarda el tipo, no solo el texto, para poder colorearla
/// y filtrarla: en una tirada larga pasan miles de líneas y sin distinguirlas no se ve nada.
/// </summary>
public sealed record LogLine(DateTime Time, string Message, LogKind Kind)
{
    public string Hora => Time.ToString("HH:mm:ss");

    public IBrush Color => Kind switch
    {
        LogKind.Err => new SolidColorBrush(Avalonia.Media.Color.FromRgb(0xDC, 0x26, 0x26)),   // rojo
        LogKind.No => new SolidColorBrush(Avalonia.Media.Color.FromRgb(0xD9, 0x77, 0x06)),    // ámbar
        LogKind.Ok => new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x16, 0xA3, 0x4A)),    // verde
        LogKind.Sum or LogKind.Head => new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x25, 0x63, 0xEB)), // azul
        LogKind.Dim => new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x9C, 0xA3, 0xAF)),   // gris
        _ => Brushes.Gray,
    };

    public FontWeight Peso => Kind is LogKind.Sum or LogKind.Head ? FontWeight.SemiBold : FontWeight.Normal;
}

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

    partial void OnAiModelChanged(string value) => OnPropertyChanged(nameof(AiModelNota));
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _testReport = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Registro en vivo (mensajes del motor: proveedores, IA, fpcalc, deshacer…).</summary>
    public ObservableCollection<LogLine> Log { get; } = new();

    /// <summary>Deja solo lo importante: errores, avisos y resúmenes.</summary>
    [ObservableProperty] private bool _soloImportante;

    /// <summary>Todo lo recibido, para poder rehacer la lista al cambiar el filtro.</summary>
    private readonly List<LogLine> _todoElLog = new();

    /// <summary>Ruta del archivo de registro de esta sesión, para saber dónde mirar.</summary>
    public string LogFilePath => _engine.Logger.LogFile ?? "(sin archivo)";

    private static bool EsImportante(LogKind k) => k is LogKind.Err or LogKind.No or LogKind.Sum or LogKind.Head;

    partial void OnSoloImportanteChanged(bool value)
    {
        Log.Clear();
        foreach (var l in _todoElLog.Where(l => !value || EsImportante(l.Kind))) Log.Add(l);
    }

    private void Anotar(LogEntry entry)
    {
        var linea = new LogLine(entry.Time, entry.Message, entry.Kind);
        _todoElLog.Add(linea);
        while (_todoElLog.Count > MaxLogLines) _todoElLog.RemoveAt(0);

        if (SoloImportante && !EsImportante(entry.Kind)) return;
        Log.Add(linea);
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

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
        _engine.Logger.OnLog += entry => Dispatcher.UIThread.Post(() => Anotar(entry));
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

    /// <summary>
    /// Se asegura de que Ollama esté respondiendo: si está instalado pero parado, lo arranca y
    /// espera. Sin esto, la comprobación diría "no está instalado" cuando en realidad solo estaba
    /// apagado, que es justo lo que más despista.
    /// </summary>
    private async Task<bool> AsegurarOllamaAsync()
    {
        if (await _engine.Ai.IsRunningAsync()) return true;
        if (!OllamaInstaller.EstaInstalado()) return false;

        AiStatus = "Ollama está instalado pero no en marcha. Arrancándolo…";
        if (!OllamaInstaller.Lanzar(m => _engine.Logger.Detail("  " + m))) return false;

        // Recién arrancado tarda unos segundos en aceptar peticiones.
        var listo = await _engine.Ai.WaitUntilRunningAsync(TimeSpan.FromSeconds(20));
        if (listo) _engine.Ai.Reset();   // pudo haberse desactivado sola por no encontrarlo antes
        return listo;
    }

    /// <summary>Busca Ollama en este equipo, arrancándolo si hace falta, y lista sus modelos.</summary>
    [RelayCommand]
    private async Task DetectAiAsync()
    {
        PushToConfig();
        AiBusy = true;
        try { await DetectarAsync(); }
        finally { AiBusy = false; }
    }

    private async Task DetectarAsync()
    {
        AiStatus = "Buscando…";
        if (!await AsegurarOllamaAsync())
        {
            AiStatus = OllamaInstaller.EstaInstalado()
                ? "Ollama está instalado pero no responde. Ábrelo una vez a mano y vuelve a pulsar «Detectar»."
                : "No se ha encontrado Ollama en este equipo. Puedes instalarlo con el botón «Instalar Ollama».";
            return;
        }

        var modelos = await _engine.Ai.ListModelsAsync();
        if (modelos == null)
        {
            AiStatus = "Ollama dejó de responder mientras se consultaban sus modelos.";
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

            if (await _engine.Ai.IsRunningAsync()) await DetectarAsync();
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
            await DetectarAsync();
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
        // Arrancar Ollama antes de la prueba: si no, informaría de que no está por estar apagado.
        await AsegurarOllamaAsync();
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

    /// <summary>
    /// Qué se borraría al limpiar la carpeta de datos, con su tamaño. Se calcula antes de preguntar
    /// para que la confirmación diga cifras concretas y no un "¿seguro?" a ciegas.
    /// </summary>
    public (string Resumen, long Bytes, List<string> Rutas) InspeccionarLimpieza()
    {
        var p = _engine.Paths;
        var rutas = new List<string>();
        var partes = new List<string>();
        long total = 0;

        void Mirar(string ruta, string nombre)
        {
            try
            {
                if (Directory.Exists(ruta))
                {
                    var ficheros = Directory.EnumerateFiles(ruta, "*", SearchOption.AllDirectories).ToList();
                    if (ficheros.Count == 0) return;
                    var bytes = ficheros.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                    partes.Add($"{nombre}: {ficheros.Count} archivos ({Tam(bytes)})");
                    total += bytes; rutas.Add(ruta);
                }
                else if (File.Exists(ruta))
                {
                    var bytes = new FileInfo(ruta).Length;
                    partes.Add($"{nombre} ({Tam(bytes)})");
                    total += bytes; rutas.Add(ruta);
                }
            }
            catch { }
        }

        Mirar(p.LogsDir, "Registros");
        Mirar(p.ReportsDir, "Informes CSV");
        Mirar(p.CacheDir, "Caché de red");
        Mirar(p.ScanCachePath, "Caché de escaneo");
        Mirar(p.AnalysisCachePath, "Caché de análisis");
        Mirar(p.LoudnessCachePath, "Mediciones de volumen");

        return (partes.Count == 0 ? "No hay nada que limpiar." : string.Join("\n", partes), total, rutas);
    }

    private static string Tam(long b)
        => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB"
         : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.#} MB"
         : $"{b / 1024.0:0.#} KB";

    /// <summary>
    /// Borra lo regenerable de la carpeta de datos. NO toca la configuración, las listas de artista
    /// ni los manifiestos de deshacer: esos son la única forma de revertir cambios ya aplicados a
    /// los archivos, y perderlos sería irreversible de verdad.
    /// </summary>
    public void LimpiarCarpetaDatos()
    {
        var (_, _, rutas) = InspeccionarLimpieza();
        int borrados = 0;
        foreach (var r in rutas)
        {
            try
            {
                if (Directory.Exists(r))
                {
                    foreach (var f in Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
                        try { File.Delete(f); borrados++; } catch { }
                    foreach (var d in Directory.EnumerateDirectories(r))
                        try { Directory.Delete(d, true); } catch { }
                }
                else if (File.Exists(r)) { File.Delete(r); borrados++; }
            }
            catch { }
        }

        // Las cachés viven además en memoria: sin esto seguirían sirviendo datos ya borrados.
        try { _engine.Library.ClearScanCache(); _engine.ClearAnalysisCache(); } catch { }

        Status = $"Carpeta de datos limpiada: {borrados} archivo(s). Se conservan ajustes, listas de artista y el historial de deshacer.";
    }

    /// <summary>Abre la carpeta de datos, para poder mirarla a mano.</summary>
    [RelayCommand]
    private async Task OpenDataFolderAsync()
    {
        await Shell.OpenFolderAsync(_engine.Paths.DataDir);
        Status = "Carpeta de datos: " + _engine.Paths.DataDir;
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
