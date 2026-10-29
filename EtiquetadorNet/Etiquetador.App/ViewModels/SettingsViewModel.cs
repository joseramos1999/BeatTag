using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Media;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Views;
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
public partial class SettingsViewModel : ViewModelBase, IEstadoPagina
{
    private const int MaxLogLines = 500;
    private readonly AppEngine _engine;

    [ObservableProperty] private string _spotifyId = "";
    [ObservableProperty] private string _spotifySecret = "";
    [ObservableProperty] private string _discogsToken = "";
    [ObservableProperty] private string _acoustIdKey = "";
    [ObservableProperty] private string _auddToken = "";
    [ObservableProperty] private string _aiModel = "";
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private string _aiHost = "";

    /// <summary>
    /// «Detectar» en curso: es breve (como mucho, lo que tarda Ollama en arrancar) y mientras tanto
    /// la aplicación se bloquea como con cualquier proceso.
    /// </summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IaOcupada))] private bool _aiBusy;

    /// <summary>
    /// Instalando Ollama o descargando un modelo. NO bloquea la aplicación: no toca ningún archivo
    /// del usuario, y «llama3.1:8b» son unos 4,7 GB (unos 20 minutos a 30 Mbit/s) en los que antes
    /// BeatTag quedaba inutilizable y sin forma de cancelar.
    /// </summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IaOcupada))] private bool _iaEnSegundoPlano;

    /// <summary>Hay algo de la IA en marcha: los botones de la sección se desactivan.</summary>
    public bool IaOcupada => AiBusy || IaEnSegundoPlano;

    private CancellationTokenSource? _ctsIa;

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
    // desplegable y uno ya descargado se anunciaría como no instalado. La regla es la de OllamaClient,
    // la misma con la que el análisis decide si el modelo elegido está descargado.
    private bool EstaInstalado(string modelo) => Core.Ai.OllamaClient.EstaInstalado(modelo, _instalados);

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

    // --- Aspecto ---

    public string[] Temas { get; } = { "Como el sistema", "Claro", "Oscuro" };

    [ObservableProperty] private string _tema = "Como el sistema";
    [ObservableProperty] private bool _tablasCompactas;

    /// <summary>Lo que se guarda en el archivo, a partir de lo que se lee en pantalla.</summary>
    private static string ClaveTema(string? mostrado) => mostrado switch
    {
        "Claro" => Apariencia.Claro,
        "Oscuro" => Apariencia.Oscuro,
        _ => Apariencia.Sistema,
    };

    private static string TemaMostrado(string? clave) => clave switch
    {
        Apariencia.Claro => "Claro",
        Apariencia.Oscuro => "Oscuro",
        _ => "Como el sistema",
    };

    // Se aplican en caliente y se guardan al momento: son ajustes que se eligen mirando el
    // resultado, así que obligar a pulsar Guardar para verlos sería absurdo.
    partial void OnTemaChanged(string value)
    {
        _engine.Config.Theme = ClaveTema(value);
        Apariencia.AplicarTema(_engine.Config.Theme);
        _engine.SaveConfig();
    }

    partial void OnTablasCompactasChanged(bool value)
    {
        _engine.Config.CompactRows = value;
        Apariencia.AplicarDensidad(value);
        _engine.SaveConfig();
    }

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
        _auddToken = c.AuddToken;
        _aiModel = c.AiModel;
        _aiHost = c.AiHost;
        _cache = c.Cache;
        // Con el campo y no con la propiedad: asignar la propiedad dispararía el guardado nada más
        // abrir la aplicación, escribiendo la configuración sin que nadie haya cambiado nada.
        _tema = TemaMostrado(c.Theme);
        _tablasCompactas = c.CompactRows;
        RefrescarOpciones();
        ActualizarAvisosConfiguracion();

        // El Logger puede emitir desde hilos de fondo -> marshalizar a la UI.
        _engine.Logger.OnLog += entry => Dispatcher.UIThread.Post(() => Anotar(entry));

        _ = ActualizarRenombradosAsync();
    }

    private void PushToConfig()
    {
        var c = _engine.Config;
        c.SpotifyId = SpotifyId.Trim();
        c.SpotifySecret = SpotifySecret.Trim();
        c.DiscogsToken = DiscogsToken.Trim();
        c.AcoustIdKey = AcoustIdKey.Trim();
        c.AuddToken = (AuddToken ?? "").Trim();
        c.AiModel = (AiModel ?? "").Trim();
        c.AiHost = (AiHost ?? "").Trim();
        c.Cache = Cache;
        // La dirección se aplica en caliente: así "Detectar" y "Descargar" usan lo que hay en pantalla.
        _engine.Ai.Host = c.AiHost.Length > 0 ? c.AiHost : Core.Ai.OllamaClient.DefaultHost;
        // Una credencial ilegible que se acaba de volver a escribir ya no lo es: sin esto, el aviso
        // amarillo seguía hasta reiniciar aunque la clave de Spotify o Discogs ya fuese la nueva.
        ActualizarAvisosConfiguracion();
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
        if (IaOcupada) return;
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
        AiModel = previo.Length > 0 && EstaInstalado(previo) ? previo : Core.Ai.OllamaClient.Automatico(modelos);
        RefrescarOpciones();
        AiStatus = modelos.Count == 1 ? "Preparada. 1 modelo instalado." : $"Preparada. {modelos.Count} modelos instalados.";
    }

    /// <summary>
    /// Instala Ollama con el gestor de paquetes del sistema (winget o Homebrew). En segundo plano y
    /// cancelable: el resto de la aplicación sigue disponible mientras tanto.
    /// </summary>
    [RelayCommand]
    private async Task InstallAiAsync()
    {
        if (IaOcupada) return;
        if (!OllamaInstaller.HayGestorDePaquetes())
        {
            AiStatus = $"Este equipo no tiene {OllamaInstaller.GestorDePaquetes}. Se abrirá la página oficial de descarga.";
            await Shell.OpenUrlAsync(OllamaInstaller.PaginaDescarga);
            return;
        }

        IaEnSegundoPlano = true;
        AiShowProgress = false;
        _ctsIa = new CancellationTokenSource();
        var ct = _ctsIa.Token;
        AiStatus = OperatingSystem.IsWindows()
            ? "Instalando Ollama… Windows pedirá confirmación de administrador. Puedes seguir usando la aplicación."
            : "Instalando Ollama con Homebrew… Puedes seguir usando la aplicación.";
        try
        {
            var err = await OllamaInstaller.InstalarAsync(l => _engine.Logger.Detail($"  {OllamaInstaller.GestorDePaquetes}: {l}"), ct);
            ct.ThrowIfCancellationRequested();
            if (err.Length > 0)
            {
                AiStatus = "No se pudo instalar: " + err;
                return;
            }
            // Tras instalar, el servicio tarda un poco en levantar; se comprueba en vez de darlo por hecho.
            AiStatus = "Instalado. Comprobando que el servicio responde…";
            for (int i = 0; i < 10 && !await _engine.Ai.IsRunningAsync(ct); i++)
                await Task.Delay(1500, ct);

            if (await _engine.Ai.IsRunningAsync(ct)) await DetectarAsync();
            else AiStatus = "Ollama se instaló, pero el servicio aún no responde. Ábrelo una vez y pulsa «Detectar».";
        }
        catch (OperationCanceledException)
        {
            // La confirmación de administrador abre el instalador como otro proceso con permisos
            // elevados, que esta aplicación no puede detener: hay que decirlo.
            AiStatus = "Instalación cancelada. Si el instalador ya había empezado, puede terminar por su cuenta: pulsa «Detectar» para comprobarlo.";
        }
        finally
        {
            IaEnSegundoPlano = false;
            _ctsIa.Dispose(); _ctsIa = null;
        }
    }

    /// <summary>
    /// Sin ninguna línea de avance durante este tiempo, la descarga se da por colgada. Ollama envía
    /// avance varias veces por segundo mientras descarga; el tramo mudo más largo es la comprobación
    /// final del archivo. Antes, una conexión colgada sin cerrar el socket dejaba la lectura
    /// esperando para siempre, y la única salida era cerrar la aplicación.
    /// </summary>
    public static readonly TimeSpan DescargaSinAvance = TimeSpan.FromMinutes(5);

    /// <summary>¿Lleva la descarga demasiado tiempo sin avanzar?</summary>
    public static bool DescargaParada(DateTime ultimoAvance, DateTime ahora) => ahora - ultimoAvance >= DescargaSinAvance;

    /// <summary>
    /// Descarga el modelo seleccionado (son varios GB, con aviso de avance). En segundo plano y
    /// cancelable, y se detiene sola si deja de avanzar.
    /// </summary>
    [RelayCommand]
    private async Task PullModelAsync()
    {
        if (IaOcupada) return;
        PushToConfig();
        var modelo = (AiModel ?? "").Trim();
        if (modelo.Length == 0) modelo = Core.Ai.OllamaClient.DefaultModel;

        if (!await _engine.Ai.IsRunningAsync())
        {
            AiStatus = "Ollama no responde. Instálalo o ábrelo antes de descargar un modelo.";
            return;
        }

        IaEnSegundoPlano = true;
        AiShowProgress = true;
        AiProgress = 0;
        AiStatus = $"Descargando «{modelo}»… Puede tardar: son varios GB. Puedes seguir usando la aplicación.";
        var cts = _ctsIa = new CancellationTokenSource();
        var ultimoAvance = DateTime.UtcNow;
        var parada = false;
        var terminada = false;   // un avance que llegue tarde no puede pisar el mensaje final
        // El avance llega por el hilo de la interfaz (Progress<T>), igual que el reloj que lo vigila.
        var vigilante = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        vigilante.Tick += (_, _) =>
        {
            if (!DescargaParada(ultimoAvance, DateTime.UtcNow)) return;
            parada = true;
            cts.Cancel();
        };
        try
        {
            var avance = new Progress<(string Estado, double Fraccion)>(p =>
            {
                ultimoAvance = DateTime.UtcNow;
                if (terminada) return;
                AiProgress = p.Fraccion * 100;
                AiStatus = p.Fraccion > 0
                    ? $"Descargando «{modelo}»: {p.Estado} ({p.Fraccion:P0})"
                    : $"«{modelo}»: {p.Estado}";
            });
            vigilante.Start();
            var err = await _engine.Ai.PullModelAsync(modelo, avance, cts.Token);
            terminada = true;
            vigilante.Stop();
            if (cts.IsCancellationRequested)
            {
                AiStatus = parada
                    ? $"La descarga de «{modelo}» lleva {DescargaSinAvance.TotalMinutes:0} minutos sin avanzar y se ha detenido. Comprueba la conexión y vuelve a pulsar «Descargar modelo»."
                    : $"Descarga de «{modelo}» cancelada.";
                return;
            }
            if (err.Length > 0) { AiStatus = "No se pudo descargar: " + err; return; }

            AiStatus = $"Modelo «{modelo}» listo.";
            await DetectarAsync();
            AiModel = modelo;
        }
        finally
        {
            vigilante.Stop();
            IaEnSegundoPlano = false; AiShowProgress = false;
            cts.Dispose(); _ctsIa = null;
        }
    }

    /// <summary>Cancela la instalación de Ollama o la descarga del modelo en curso.</summary>
    [RelayCommand]
    private void CancelarIa()
    {
        if (_ctsIa == null) return;
        AiStatus = "Cancelando…";
        _ctsIa.Cancel();
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

    // --- Vaciar caché (E11) ---
    //
    // Antes borraba sin preguntar, y en el hilo de la interfaz: con la carpeta cache de una
    // biblioteca real (256 subcarpetas de ~260-280 archivos, unos 69.000) la ventana se quedaba
    // congelada, y después el siguiente escaneo releía las etiquetas de toda la biblioteca (el primer
    // escaneo medido tardó 340 s) y el siguiente análisis volvía a consultarlo todo en red e IA.
    // Estaba al lado de «Recuperar descartadas», así que bastaba un clic equivocado.

    /// <summary>Qué se llevaría «Vaciar caché», con cifras, para decirlo antes de borrar.</summary>
    public sealed record ContenidoCache(int Respuestas, long Bytes, int Analisis, bool HayEscaneo)
    {
        public bool Vacia => Respuestas == 0 && Analisis == 0 && !HayEscaneo;
    }

    /// <summary>Mira la caché en segundo plano: contar decenas de miles de archivos lleva un rato.</summary>
    public async Task<ContenidoCache> InspeccionarCacheAsync()
    {
        var dir = _engine.Paths.CacheDir;
        var (n, bytes) = await Task.Run(() => ContarArchivos(dir));
        var escaneo = File.Exists(_engine.Paths.ScanCachePath);
        return new ContenidoCache(n, bytes, _engine.Analysis.Count, escaneo);
    }

    /// <summary>Archivos y bytes que hay dentro de una carpeta (0 si no existe).</summary>
    public static (int Archivos, long Bytes) ContarArchivos(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        int n = 0; long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            n++;
            try { bytes += new FileInfo(f).Length; } catch { }
        }
        return (n, bytes);
    }

    /// <summary>Texto de la confirmación de «Vaciar caché»: qué se borra y qué cuesta después.</summary>
    public static (string Cuerpo, string Nota) TextosVaciarCache(ContenidoCache c)
    {
        var partes = new List<string>();
        if (c.Respuestas > 0) partes.Add($"• Respuestas guardadas de los catálogos: {c.Respuestas:N0} archivos ({Tam(c.Bytes)}).");
        if (c.HayEscaneo) partes.Add("• Caché de escaneo: lo leído de las etiquetas de toda la biblioteca.");
        if (c.Analisis > 0) partes.Add($"• Resultados de análisis guardados: {c.Analisis:N0} canciones, con las propuestas pendientes de Enriquecer y No encontradas.");
        return ("Se va a borrar:\n" + string.Join("\n", partes),
                "No se tocan tus archivos de música, ni los ajustes, ni el historial para deshacer. Pero el próximo "
              + "escaneo volverá a leer las etiquetas de toda la biblioteca y el próximo análisis volverá a consultar "
              + "cada canción en los catálogos y en la IA local: en una biblioteca grande, eso lleva bastante tiempo.");
    }

    /// <summary>
    /// «Vaciar caché» entero: mira qué hay, pregunta con <paramref name="confirmar"/> y vacía. La
    /// página queda ocupada desde el clic, también durante el recuento (unos segundos con ~69.000
    /// archivos) y la confirmación: ocupada, ninguna otra pestaña puede estar analizando y leyendo o
    /// escribiendo estas mismas cachés, y un doble clic no abre dos confirmaciones
    /// (ver <see cref="TareaExclusiva"/>).
    /// </summary>
    public Task VaciarCacheAsync(Func<ContenidoCache, Task<bool>> confirmar)
        => TareaExclusiva.EjecutarAsync(() => IsBusy, ocupada => IsBusy = ocupada, async () =>
        {
            Status = "Mirando qué hay en la caché…";
            var contenido = await InspeccionarCacheAsync();
            if (contenido.Vacia) { Status = "La caché ya está vacía."; return; }
            Status = "";
            if (!await confirmar(contenido)) return;
            await VaciarAsync();
        });

    /// <summary>Vacía las cachés ya confirmado, sin congelar la ventana y diciendo por dónde va.</summary>
    private async Task VaciarAsync()
    {
        Status = "Vaciando la caché…";
        try
        {
            var dir = _engine.Paths.CacheDir;
            var avance = new Progress<int>(n => Status = $"Vaciando la caché… {n:N0} archivos borrados.");
            var (borrados, fallidos) = await Task.Run(() => BorrarContenido(dir, avance));
            _engine.Library.ClearScanCache();   // memoria + archivo (no basta con borrar el .json)
            _engine.ClearAnalysisCache();       // resultados de análisis guardados
            Status = $"Caché vaciada ({borrados:N0} respuestas, escaneo y análisis). Se regenerará al usar la aplicación."
                   + (fallidos > 0 ? $" {fallidos:N0} archivos no se pudieron borrar (estaban en uso)." : "");
            _engine.Logger.Log($"Caché vaciada: {borrados} respuestas borradas, {fallidos} sin poder borrar.");
        }
        catch (Exception e) { Status = "No se pudo vaciar del todo la caché: " + e.Message; }
    }

    /// <summary>
    /// Borra todo lo que hay DENTRO de una carpeta (la carpeta se queda). Cuenta lo borrado y lo que
    /// no se pudo borrar, e informa del avance cada 500 archivos.
    /// </summary>
    public static (int Borrados, int Fallidos) BorrarContenido(string dir, IProgress<int>? avance = null)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        int borrados = 0, fallidos = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            try { File.Delete(f); borrados++; } catch { fallidos++; }
            if (borrados > 0 && borrados % 500 == 0) avance?.Report(borrados);
        }
        foreach (var d in Directory.EnumerateDirectories(dir).ToList())
            try { Directory.Delete(d, true); } catch { }
        return (borrados, fallidos);
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

        // NO se listan a propósito, aunque sean cachés: las huellas acústicas cuestan horas de
        // cálculo y las identificaciones por audio cuestan DINERO, una consulta de pago cada una.
        // «Limpiar» está para recuperar espacio de lo que se regenera solo; eso no se regenera solo.
        // Cada una tiene su propio botón donde toca, avisando de lo que se pierde.

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
        // Las mediciones de volumen también. Sin esto, la pestaña seguía usándolas y el siguiente
        // guardado (al medir, al ajustar, al renombrar una canción…) volvía a escribir el archivo
        // entero: el borrado se deshacía o no según lo que se hiciera antes de cerrar. La ganancia
        // que ya se ha aplicado se conserva (ver LoudnessScanner.Clear).
        if (rutas.Contains(_engine.Paths.LoudnessCachePath, StringComparer.OrdinalIgnoreCase))
            try { _engine.Loudness.Clear(); } catch { }

        Status = $"Carpeta de datos limpiada: {borrados} {(borrados == 1 ? "archivo" : "archivos")}. Se conservan ajustes, listas de artista y el historial de deshacer.";
    }

    /// <summary>
    /// Cuántos renombrados de BeatTag siguen vigentes en el historial. Se calcula en segundo plano
    /// (lee todos los manifiestos y mira los archivos) al abrir Ajustes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoRenombradosAnotados))]
    private int _renombradosAnotados;

    /// <summary>
    /// «1 renombrado anotado por BeatTag» / «N renombrados anotados por BeatTag». Antes decía «N
    /// renombrados que se pueden reparar» («1 renombrados»), y la página rekordbox usa «se pueden
    /// reparar» para otra cifra (las entradas rotas reparables de la colección abierta).
    /// </summary>
    public string TextoRenombradosAnotados => TextoRenombrados(RenombradosAnotados);

    internal static string TextoRenombrados(int n)
        => n == 1 ? "1 renombrado anotado por BeatTag" : $"{n:N0} renombrados anotados por BeatTag";

    public async Task ActualizarRenombradosAsync()
    {
        var dir = _engine.Paths.UndoDir;
        try { RenombradosAnotados = await Task.Run(() => RenombradosReparables(dir)); }
        catch (Exception e) { _engine.Logger.Error("No se pudieron contar los renombrados anotados", e); }
    }

    /// <summary>
    /// Renombrados que hoy tiene sentido reparar: el archivo está en la ruta nueva y ya no en la vieja,
    /// y la ruta vieja no es un nombre que solo existió hasta que se deshizo.
    ///
    /// Visto en una biblioteca real: run_20260920_230254.deshecho.jsonl tiene 12 renombrados que se
    /// deshicieron esa misma noche, y el contador los sumaba. El mapa de RekordboxRelocator los
    /// incluye a propósito como vueltas B→A (las fichas y una Location que llegara a apuntar a B las
    /// necesitan), y en esas vueltas A existe y B no, así que mirar solo si existen los archivos los
    /// seguía contando: hay que saber además que B fue un nombre deshecho.
    /// </summary>
    public static int RenombradosReparables(string undoDir)
    {
        var mapa = RekordboxRelocator.ReadRenames(undoDir);
        if (mapa.Count == 0) return 0;
        var deshechos = NombresDeshechos(undoDir);
        return mapa.Count(kv => !deshechos.Contains(kv.Key) && File.Exists(kv.Value) && !File.Exists(kv.Key));
    }

    /// <summary>
    /// Rutas que BeatTag llegó a dar a un archivo y que se deshicieron (el destino de cada renombrado
    /// de los manifiestos archivados «.deshecho»).
    /// </summary>
    public static HashSet<string> NombresDeshechos(string undoDir)
    {
        var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(undoDir)) return res;
        foreach (var mf in Directory.EnumerateFiles(undoDir, "run_*.jsonl"))
        {
            if (!Path.GetFileName(mf).Contains(".deshecho", StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<string> lineas;
            try { lineas = File.ReadAllLines(mf); } catch { continue; }
            foreach (var linea in lineas)
            {
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(linea) is not System.Text.Json.Nodes.JsonObject o) continue;
                    // Formato actual (OrigPath/FinalPath/Renamed) y el antiguo (new/orig/renamed).
                    var destino = (string?)(o["FinalPath"] ?? o["new"]) ?? "";
                    var renombrado = (o["Renamed"] ?? o["renamed"]) is System.Text.Json.Nodes.JsonValue v
                                     && v.TryGetValue<bool>(out var b) && b;
                    if (renombrado && destino.Length > 0) res.Add(destino);
                }
                catch { /* línea ilegible: no cuenta */ }
            }
        }
        return res;
    }

    /// <summary>
    /// La reparación de la colección de rekordbox se ha trasladado a la página rekordbox (equipo
    /// «rekordbox», tanda 4), que además de los renombrados de BeatTag encuentra los archivos movidos o
    /// renombrados fuera de BeatTag y no bloquea la ventana mientras copia el XML. Ajustes conserva el
    /// acceso, con el recuento de renombrados anotados, para quien la busque donde estaba; los avisos de
    /// la aplicación ya remiten a la página (<see cref="RekordboxViewModel.DondeSeRepara"/>).
    /// </summary>
    public event Action? AbrirRekordboxPedido;

    [RelayCommand(CanExecute = nameof(RekordboxDisponible))]
    private void AbrirRekordbox() => AbrirRekordboxPedido?.Invoke();

    /// <summary>
    /// La página rekordbox se puede abrir ahora. La pone el shell al decidir qué pestañas están
    /// disponibles: sin biblioteca preparada, o con un proceso en marcha, el salto se deshacía y se
    /// acababa en Biblioteca (o no pasaba nada) sin explicar por qué.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AbrirRekordboxCommand))]
    [NotifyPropertyChangedFor(nameof(AyudaAbrirRekordbox))]
    private bool _rekordboxDisponible = true;

    /// <summary>Por qué no está disponible la página rekordbox ("" si lo está).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AyudaAbrirRekordbox))]
    private string _motivoSinRekordbox = "";

    /// <summary>El rótulo emergente del botón, también con el botón desactivado.</summary>
    public string AyudaAbrirRekordbox => RekordboxDisponible || MotivoSinRekordbox.Length == 0
        ? "Abre la página rekordbox, donde se comprueba y se repara la colección exportada."
        : MotivoSinRekordbox;

    /// <summary>Lo llama el shell cada vez que cambia qué pestañas están disponibles.</summary>
    public void PonerDisponibilidadRekordbox(bool disponible, bool hayProceso)
    {
        RekordboxDisponible = disponible;
        MotivoSinRekordbox = disponible ? ""
            : hayProceso ? "La página rekordbox estará disponible cuando termine el proceso en marcha."
            : "La página rekordbox estará disponible cuando la biblioteca esté preparada: añade tus carpetas de música en Biblioteca y pulsa «Escanear».";
    }

    // --- Avisos de la configuración (T9) ---
    //
    // Si config.net.json no se pudo leer, o trae credenciales cifradas en otro equipo, el motor lo
    // sabe desde el arranque (AppConfig.AvisoCarga), pero hasta ahora solo iba al registro: se
    // perdían ajustes o no se guardaba nada, y en pantalla nadie lo decía.

    /// <summary>Lo que pasó al leer config.net.json, o "" si se leyó con normalidad.</summary>
    [ObservableProperty] private string _avisoConfiguracion = "";

    /// <summary>Qué credenciales no se pueden descifrar aquí, o "" si ninguna.</summary>
    [ObservableProperty] private string _avisoCredenciales = "";

    /// <summary>
    /// Los dos avisos, por separado: el del archivo se queda toda la sesión; el de las credenciales
    /// desaparece en cuanto se sustituyen (al guardar o probar las claves: lo recalcula PushToConfig)
    /// o se olvidan. Cuando el único problema al cargar fueron las credenciales
    /// (<see cref="AppConfig.AvisoCargaSoloCredenciales"/>), el aviso de carga es ese mismo y no se repite.
    /// </summary>
    public static (string Archivo, string Credenciales) AvisosConfiguracion(AppConfig cfg)
    {
        var ilegibles = cfg.CredencialesIlegibles;
        var credenciales = ilegibles.Count == 0 ? ""
            : $"No se pueden descifrar en este equipo las credenciales de {NombresCredenciales(ilegibles)}: "
            + "probablemente se guardaron en otro equipo o con otro usuario. Se conservan cifradas tal cual; "
            + "vuelve a introducirlas en «Claves de API» o pulsa «Olvidar credenciales ilegibles».";
        return (cfg.AvisoCargaSoloCredenciales ? "" : cfg.AvisoCarga, credenciales);
    }

    private static string NombresCredenciales(IEnumerable<string> campos)
        => string.Join(", ", campos.Select(AppConfig.NombreVisible));

    private void ActualizarAvisosConfiguracion()
        => (AvisoConfiguracion, AvisoCredenciales) = AvisosConfiguracion(_engine.Config);

    /// <summary>Nombres de las credenciales ilegibles, para la confirmación.</summary>
    public string CredencialesIlegibles => NombresCredenciales(_engine.Config.CredencialesIlegibles);

    /// <summary>Descarta las credenciales que no se pueden descifrar aquí (ya confirmado en la vista).</summary>
    public void OlvidarCredencialesIlegibles()
    {
        var nombres = CredencialesIlegibles;
        if (nombres.Length == 0) { ActualizarAvisosConfiguracion(); return; }
        _engine.Config.OlvidarCredencialesIlegibles();
        var ok = _engine.SaveConfig(out var error);
        ActualizarAvisosConfiguracion();
        Status = ok
            ? $"Olvidadas las credenciales ilegibles ({nombres}). Si las necesitas, vuelve a introducirlas en «Claves de API»."
            : $"Se olvidaron las credenciales ilegibles ({nombres}), pero la configuración no se pudo guardar: {error}";
        _engine.Logger.Log($"Credenciales ilegibles olvidadas: {nombres}");
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

    /// <summary>
    /// Vuelve a tener en cuenta canciones descartadas con "Quitar de la lista", ELIGIENDO cuáles.
    ///
    /// Antes era todo o nada: quien había descartado trescientas y quería una sola de vuelta tenía
    /// que devolverlas todas y volver a descartar las demás.
    /// </summary>
    [RelayCommand]
    private async Task RestoreIgnoredAsync()
    {
        if (_engine.Ignored.Count == 0) { Status = "No hay ninguna canción descartada."; return; }
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        { Status = "No se pudo abrir la ventana."; return; }

        var elegidas = await RestoreIgnoredDialog.AskAsync(owner, _engine.Ignored.Items);
        if (elegidas == null) return;                       // cancelado
        if (elegidas.Count == 0) { Status = "No marcaste ninguna."; return; }

        _engine.RestoreIgnored(elegidas);
        Status = $"Recuperadas {elegidas.Count} canciones (quedan {_engine.Ignored.Count} descartadas). "
               + "Vuelve a analizar en Enriquecer para verlas.";
        _engine.Logger.Log($"Recuperadas {elegidas.Count} canciones descartadas (quedan {_engine.Ignored.Count})");
    }

    /// <summary>Cuántas canciones constan como ya aplicadas (para la confirmación de «Olvidar aplicadas»).</summary>
    public int AplicadasCount => _engine.Applied.Count;

    /// <summary>
    /// Olvida qué canciones se aplicaron ya, para que vuelvan a proponerse al analizar. Se llama ya
    /// confirmado desde la vista: no se puede deshacer y afecta a miles de canciones de golpe.
    /// </summary>
    public void OlvidarAplicadas()
    {
        var n = _engine.Applied.Count;
        if (n == 0) { Status = "No hay ninguna canción marcada como aplicada."; return; }
        _engine.Applied.Clear();
        Status = $"Olvidadas {n} canciones aplicadas. Volverán a salir al analizar.";
        _engine.Logger.Log($"Olvidadas {n} canciones aplicadas.");
    }

    // =================================================================================================
    // Pendientes: dirección de búsqueda del pool (equipo «listas», tanda 3)
    // =================================================================================================

    /// <summary>
    /// Plantilla de «Buscar en mi pool» (Pendientes). Se pone en la configuración al escribirla y se
    /// guarda al salir de la casilla, con «Probar» o con «Guardar»: guardar con cada tecla volvería a
    /// cifrar y escribir toda la configuración por cada letra.
    /// </summary>
    public string PoolBusquedaUrl
    {
        get => _engine.Config.PoolBusquedaUrl;
        set
        {
            var v = (value ?? "").Trim();
            if (v == _engine.Config.PoolBusquedaUrl) return;
            _poolBusquedaGuardada ??= _engine.Config.PoolBusquedaUrl;   // la que había en el archivo
            _engine.Config.PoolBusquedaUrl = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvisoPoolBusqueda));
        }
    }

    /// <summary>Por qué la plantilla escrita no sirve, o vacío.</summary>
    public string AvisoPoolBusqueda => BusquedaEnPool.Validar(PoolBusquedaUrl);

    public string EjemploPoolBusqueda => BusquedaEnPool.Ejemplo;

    /// <summary>La plantilla tal como está guardada en el archivo; null mientras no se haya tocado.</summary>
    private string? _poolBusquedaGuardada;

    /// <summary>Guarda la plantilla si ha cambiado desde la última vez.</summary>
    public void GuardarPoolBusqueda()
    {
        if (_poolBusquedaGuardada == null || PoolBusquedaUrl == _poolBusquedaGuardada) return;
        if (_engine.SaveConfig(out var error))
        {
            _poolBusquedaGuardada = PoolBusquedaUrl;
            Status = PoolBusquedaUrl.Length == 0 ? "Búsqueda del pool borrada." : "Búsqueda del pool guardada.";
        }
        else Status = "⚠ NO se guardó la búsqueda del pool: " + error;
    }

    /// <summary>Abre la búsqueda de una canción de ejemplo, para comprobar que la dirección lleva al sitio correcto.</summary>
    [RelayCommand]
    private async Task ProbarPoolBusquedaAsync()
    {
        var url = BusquedaEnPool.Url(PoolBusquedaUrl, "Daft Punk", "One More Time", out var motivo);
        if (url.Length == 0) { Status = motivo; return; }
        GuardarPoolBusqueda();
        await Shell.OpenUrlAsync(url);
        Status = "Abierta en el navegador la búsqueda de «Daft Punk One More Time». Si sale la página de resultados de tu pool, la dirección es correcta.";
    }

    // --- Catálogo: ISRC (equipo catalogo_bandeja, C14) ---
    //
    // Como el tema y las tablas compactas, se guarda al momento: es una casilla que no depende de
    // las claves, y obligar a pulsar Guardar para que cuente en la próxima aplicación sería un tropiezo.

    /// <summary>Escribir el ISRC del catálogo al aplicar propuestas (desactivado por defecto).</summary>
    public bool EscribirIsrc
    {
        get => _engine.Config.WriteIsrc;
        set
        {
            if (_engine.Config.WriteIsrc == value) return;
            _engine.Config.WriteIsrc = value;
            Status = _engine.SaveConfig(out var error)
                ? (value ? "Se escribirá el ISRC al aplicar propuestas." : "Ya no se escribirá el ISRC al aplicar propuestas.")
                : "⚠ NO se guardó (config anterior intacta): " + error;
            OnPropertyChanged();
        }
    }
}
