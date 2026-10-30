using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Etiquetador.Core;

/// <summary>
/// Configuración persistente (equivale a Save-Config/Load-Config del .ps1).
/// Los secretos se guardan cifrados con DPAPI (propiedades *Enc); en memoria van en claro.
/// Nota: a diferencia del .ps1, aquí la clave de AcoustID también se cifra.
/// </summary>
public sealed class AppConfig
{
    // --- Carpetas ---
    public List<string> Folders { get; set; } = new();

    /// <summary>
    /// Ancho de las columnas que el usuario ha ajustado a mano, por tabla ("Enrich", "Library"…).
    /// Se recuerda entre sesiones para no tener que recolocarlas cada vez.
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = new();

    // --- Duplicados ---
    /// <summary>
    /// Carpetas cuyas copias se prefieren conservar. Sirve para separar la colección buena de las
    /// descargas sueltas: ante dos copias iguales, se marca como mejor la que está aquí.
    /// </summary>
    public List<string> PriorityFolders { get; set; } = new();

    /// <summary>Carpetas que no se tienen en cuenta al buscar duplicados (acapellas, packs…).</summary>
    public List<string> ExcludedDupFolders { get; set; } = new();

    /// <summary>
    /// Índice de cambio mínimo (0-10) para que una propuesta aparezca en Enriquecer. Por debajo se
    /// considera un retoque cosmético y se aparta, para no tapar lo que sí hay que revisar.
    /// </summary>
    public double MinChangeIndex { get; set; } = 3.0;

    /// <summary>Criterio para decidir qué copia es la mejor de cada grupo.</summary>
    public string DupKeepCriterion { get; set; } = "";

    /// <summary>
    /// Dejar fuera de Duplicados las ediciones para pinchar (intros, open shows, acapellas, mashups).
    /// No son copias sobrantes, y emparejadas con su original tapan los duplicados de verdad.
    /// </summary>
    public bool DupExcluirEdicionesDj { get; set; }
    /// <summary>Carpetas presentes pero desmarcadas (no se analizan).</summary>
    public List<string> DisabledFolders { get; set; } = new();

    // --- Credenciales (en claro en memoria; se serializan cifradas vía *Enc) ---
    [JsonIgnore] public string DiscogsToken { get; set; } = "";
    [JsonIgnore] public string SpotifySecret { get; set; } = "";
    [JsonIgnore] public string AcoustIdKey { get; set; } = "";

    /// <summary>Clave de AudD, el servicio que identifica una canción escuchando su audio.</summary>
    [JsonIgnore] public string AuddToken { get; set; } = "";
    public string SpotifyId { get; set; } = "";   // no secreto

    /// <summary>Modelo de la IA local (Ollama). No es un secreto: se guarda en claro.</summary>
    public string AiModel { get; set; } = "";

    /// <summary>Dirección de Ollama. Vacío = la de siempre (http://localhost:11434).</summary>
    public string AiHost { get; set; } = "";

    // --- Opciones de ejecución ---
    public bool Apply { get; set; }
    public bool Overwrite { get; set; }
    public bool SkipMixFolders { get; set; }
    public bool CleanOnly { get; set; }

    // --- Fuentes ---
    public bool UseSpotify { get; set; }
    public bool UseDiscogs { get; set; }
    public bool UseMusicBrainz { get; set; }
    public bool UseItunes { get; set; }
    public bool UseDeezer { get; set; } = true;
    public bool UseAcoustId { get; set; }
    public bool UseAi { get; set; }

    // --- Carátula ---
    public string CoverMode { get; set; } = "keep";   // keep | spotify | png
    public string CoverPath { get; set; } = "";

    // --- Campos a escribir ---
    public bool WriteTitle { get; set; } = true;
    public bool WriteAlbum { get; set; } = true;
    public bool WriteGenre { get; set; } = true;
    public bool WriteYear { get; set; } = true;
    public bool WriteArtist { get; set; } = true;
    public bool WriteBpm { get; set; } = true;

    /// <summary>
    /// Enriquecer no consulta las canciones cuyo nombre y tags ya coinciden y a las que no les falta
    /// nada de lo que se escribe (ver <see cref="Pipeline.YaCorrecta"/>).
    /// </summary>
    public bool SaltarYaCorrectas { get; set; } = true;

    /// <summary>El último enlace de playlist consultado, para no tener que volver a pegarlo.</summary>
    public string UltimaPlaylist { get; set; } = "";

    // --- Varios ---
    public bool Cache { get; set; } = true;

    // --- Bandeja de entrada ---

    /// <summary>Carpeta donde se deja la música recién descargada, fuera de la biblioteca.</summary>
    public string BandejaCarpeta { get; set; } = "";

    /// <summary>Carpeta de la biblioteca a la que pasan las canciones preparadas.</summary>
    public string BandejaDestino { get; set; } = "";

    // --- Aspecto ---

    /// <summary>Tema: "sistema", "claro" u "oscuro". Por defecto el del sistema.</summary>
    public string Theme { get; set; } = "sistema";

    /// <summary>Filas de tabla más juntas, para que quepan más canciones en pantalla.</summary>
    public bool CompactRows { get; set; }

    // --- Ventana ---
    //
    // Se guarda para volver a abrirla como se dejó. Cero significa «nunca se ha guardado», y con
    // eso la ventana usa su tamaño de fábrica; las coordenadas usan int.MinValue por lo mismo,
    // porque 0,0 es una posición legítima (esquina superior izquierda).
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public bool WindowMaximized { get; set; }

    // --- Secretos serializados (cifrados). El getter cifra, el setter descifra. ---
    [JsonPropertyName("DiscogsTokenEnc")] public string DiscogsTokenEnc { get => Enc(DiscogsToken, nameof(DiscogsTokenEnc)); set => DiscogsToken = Dec(value, nameof(DiscogsTokenEnc)); }
    [JsonPropertyName("SpotifySecretEnc")] public string SpotifySecretEnc { get => Enc(SpotifySecret, nameof(SpotifySecretEnc)); set => SpotifySecret = Dec(value, nameof(SpotifySecretEnc)); }
    [JsonPropertyName("AcoustIdKeyEnc")] public string AcoustIdKeyEnc { get => Enc(AcoustIdKey, nameof(AcoustIdKeyEnc)); set => AcoustIdKey = Dec(value, nameof(AcoustIdKeyEnc)); }
    [JsonPropertyName("AuddTokenEnc")] public string AuddTokenEnc { get => Enc(AuddToken, nameof(AuddTokenEnc)); set => AuddToken = Dec(value, nameof(AuddTokenEnc)); }

    // Texto cifrado TAL CUAL venía en el archivo, de los secretos que aquí no se pudieron descifrar
    // (otro perfil de Windows, una configuración traída de un Mac, el Llavero bloqueado…).
    private readonly Dictionary<string, string> _ilegibles = new();

    /// <summary>
    /// true si hay credenciales guardadas que no se pudieron descifrar en este equipo y que el
    /// usuario no ha sustituido. Ya NO bloquea el guardado: al guardar se conserva su texto
    /// cifrado original (ver <see cref="CredencialesIlegibles"/>).
    /// </summary>
    [JsonIgnore] public bool SecretsUnreadable => CredencialesIlegibles.Count > 0;

    /// <summary>
    /// Nombres de las credenciales que no se pudieron descifrar y se conservan cifradas tal cual
    /// («SpotifySecretEnc»…). Para avisar en la interfaz.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> CredencialesIlegibles
        => _ilegibles.Keys.Where(k => Claro(k).Length == 0).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Descarta las credenciales ilegibles: el siguiente guardado ya no las conserva. Es la salida
    /// explícita cuando son de otro equipo y no se van a poder leer nunca aquí.
    /// </summary>
    public void OlvidarCredencialesIlegibles() => _ilegibles.Clear();

    /// <summary>
    /// Cifra para guardar. Si el secreto no se pudo descifrar al cargar y el usuario no ha puesto
    /// otro, se devuelve el cifrado ORIGINAL: re-cifrar el vacío perdería la credencial.
    ///
    /// Antes, con una sola credencial ilegible, Save se negaba a guardar NADA, y los guardados
    /// automáticos (carpetas, ancho de columnas, ventana) fallaban en silencio: con una
    /// configuración traída de otro equipo, el usuario añadía carpetas, cerraba y al reabrir habían
    /// desaparecido.
    /// </summary>
    private string Enc(string claro, string campo)
        => claro.Length == 0 && _ilegibles.TryGetValue(campo, out var original) ? original : Secretos.Protect(claro);

    private string Dec(string? enc, string campo)
    {
        var (value, status) = Secretos.TryUnprotect(enc);
        if (status == UnprotectStatus.CryptoError && !string.IsNullOrEmpty(enc)) _ilegibles[campo] = enc;
        else _ilegibles.Remove(campo);
        return value;
    }

    private string Claro(string campo) => campo switch
    {
        nameof(DiscogsTokenEnc) => DiscogsToken,
        nameof(SpotifySecretEnc) => SpotifySecret,
        nameof(AcoustIdKeyEnc) => AcoustIdKey,
        nameof(AuddTokenEnc) => AuddToken,
        _ => "",
    };

    /// <summary>Nombre de la credencial tal como se ve en Ajustes.</summary>
    public static string NombreVisible(string campo) => campo switch
    {
        nameof(DiscogsTokenEnc) => "Discogs",
        nameof(SpotifySecretEnc) => "Spotify",
        nameof(AcoustIdKeyEnc) => "AcoustID",
        nameof(AuddTokenEnc) => "AudD",
        _ => campo,
    };

    /// <summary>
    /// "" si la configuración se leyó con normalidad; si no, qué pasó con el archivo (se apartó por
    /// ilegible, se descartaron ajustes que esta versión no entiende…). Para avisar en la interfaz.
    /// </summary>
    [JsonIgnore] public string AvisoCarga { get; private set; } = "";

    /// <summary>
    /// true si lo único que pasó al cargar fue que hay credenciales que no se pueden descifrar: en
    /// ese caso <see cref="AvisoCarga"/> es ese aviso, y Ajustes ya lo da aparte (y lo quita en cuanto
    /// se sustituyen). Explícito para no tener que reconocer el aviso por su texto: si el texto
    /// cambiaba, el aviso salía dos veces.
    /// </summary>
    [JsonIgnore] public bool AvisoCargaSoloCredenciales { get; private set; }

    /// <summary>
    /// config.net.json existe pero no se pudo leer NI apartar. Mientras sea true, Save no escribe:
    /// sustituiría por los valores de fábrica un archivo que quizá solo estaba bloqueado.
    /// </summary>
    [JsonIgnore] public bool GuardadoBloqueado { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // Al LEER se toleran las comas finales y los comentarios: es lo típico de una edición a mano
    // («"Cache": true,» antes del «}»), y antes eso solo bastaba para que el archivo entero se diera
    // por ilegible -y el rescate tampoco lo entendía, así que se perdían todos los ajustes-. Al
    // guardar se escribe JSON estricto, así que la siguiente vez ya sale limpio.
    private static readonly JsonSerializerOptions JsonLectura = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonDocumentOptions DocumentoTolerante = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static AppConfig Load(AppPaths paths) => Load(paths, out _);

    /// <summary>
    /// Lee la configuración. Si el archivo existe pero no se puede leer -un JSON editado a mano con
    /// un error, o un ajuste que cambió de tipo al volver a una versión anterior- ya no se
    /// devuelven los valores de fábrica sin más, que el siguiente guardado (al cerrar la ventana,
    /// o el automático de las carpetas) escribía encima: se perdían las 43 carpetas, las prioridades
    /// de Duplicados y las 4 credenciales cifradas. Ahora:
    ///   1. se rescata todo lo que se entienda, ajuste a ajuste, y solo se descarta lo que no;
    ///   2. se COPIA el archivo original como «config.net.json.ilegible_fecha», intacto;
    ///   3. solo entonces se escribe lo rescatado encima del original;
    ///   4. el motivo queda en <paramref name="error"/> y en <see cref="AvisoCarga"/>.
    /// Si no se puede ni leer ni copiar, <see cref="GuardadoBloqueado"/> impide escribir encima.
    ///
    /// El orden importa. Antes el original se MOVÍA a «.ilegible» y lo rescatado se quedaba solo en
    /// memoria hasta el primer guardado: si el proceso moría antes (el Stop-Process -Force de
    /// Compilar-BeatTag.ps1 -CerrarApp, por ejemplo), el siguiente arranque no encontraba
    /// config.net.json y empezaba con los valores de fábrica sin ningún aviso, con las 43 carpetas
    /// solo en el «.ilegible». Ahora en cualquier momento hay un config.net.json: el original (que
    /// el siguiente arranque volvería a rescatar) o el ya rescatado.
    /// </summary>
    public static AppConfig Load(AppPaths paths, out string error)
    {
        error = "";
        string json;
        try
        {
            if (!File.Exists(paths.ConfigPath)) return new AppConfig();
            json = File.ReadAllText(paths.ConfigPath, Encoding.UTF8);
        }
        catch (Exception e)
        {
            // No se ha podido mirar dentro: no hay motivo para creerlo dañado, pero tampoco se puede
            // guardar encima sin perderlo.
            error = $"No se pudo leer la configuración ({e.Message}); no se guardará ningún cambio en esta sesión para no sustituirla.";
            return new AppConfig { AvisoCarga = error, GuardadoBloqueado = true };
        }

        string motivo;
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonLectura);
            if (cfg != null)
            {
                // Se puede seguir, pero hay que decirlo: antes solo bloqueaba el guardado en silencio.
                cfg.AvisoCargaSoloCredenciales = cfg.SecretsUnreadable;
                if (cfg.SecretsUnreadable)
                    error = cfg.AvisoCarga =
                        $"Hay credenciales guardadas que no se pueden descifrar en este equipo ({string.Join(", ", cfg.CredencialesIlegibles.Select(NombreVisible))}); " +
                        "se conservan cifradas tal cual. Si son de otro equipo, vuelve a introducirlas en Ajustes.";
                return cfg;
            }
            motivo = "el archivo está vacío";
        }
        catch (Exception e)   // lo que sea del contenido: una configuración dañada no puede impedir arrancar
        {
            motivo = e.Message;
        }

        var descartados = new List<string>();
        var rescatada = Rescatar(json, descartados);

        // 1) Copia intacta del original. Sin ella no se escribe nada encima.
        var apartado = ArchivoDatos.CopiarApartado(paths.ConfigPath);

        var cfgFinal = rescatada ?? new AppConfig();
        if (apartado.Length == 0)
        {
            cfgFinal.GuardadoBloqueado = true;
            error = $"No se pudo leer la configuración ({motivo}) ni apartarla; no se guardará ningún cambio en esta sesión para no sustituirla.";
            cfgFinal.AvisoCarga = error;
            return cfgFinal;
        }

        if (rescatada != null && descartados.Count > 0)
        {
            error = $"La configuración tenía ajustes que esta versión no entiende ({string.Join(", ", descartados)}); " +
                    $"se han dejado con su valor de fábrica y el resto se conserva. El archivo original se ha guardado como {Path.GetFileName(apartado)}.";
        }
        else if (rescatada != null)
        {
            error = $"La configuración no se pudo leer a la primera ({motivo}), pero se han recuperado todos los ajustes. " +
                    $"El archivo original se ha guardado como {Path.GetFileName(apartado)}.";
        }
        else
        {
            error = $"No se pudo leer la configuración ({motivo}); se empieza con los valores de fábrica. " +
                    $"El archivo original se ha guardado como {Path.GetFileName(apartado)}.";
        }

        // 2) Y solo ahora, lo rescatado encima del original (escritura atómica). Si no se puede, el
        //    original sigue en su sitio y el siguiente guardado lo intentará otra vez: la copia ya
        //    está hecha, así que no se pierde nada.
        if (!cfgFinal.Save(paths, out var errGuardar))
            error += " No se pudo guardar todavía la configuración corregida: " + errGuardar;

        cfgFinal.AvisoCarga = error;
        return cfgFinal;
    }

    /// <summary>
    /// Lee el JSON quitando, uno a uno, los ajustes que no se dejan leer (el que señala la
    /// excepción), hasta que el resto se lee. null si ni siquiera es un objeto JSON.
    /// </summary>
    private static AppConfig? Rescatar(string json, List<string> descartados)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(json, null, DocumentoTolerante) as JsonObject; }
        catch { return null; }
        if (obj == null) return null;

        // Como mucho tantos intentos como ajustes: cada vuelta quita uno.
        for (var intento = 0; intento <= obj.Count; intento++)
        {
            try { return obj.Deserialize<AppConfig>(JsonLectura); }
            catch (JsonException e)
            {
                var ajuste = AjusteDe(e.Path);
                if (ajuste == null || !obj.Remove(ajuste)) return null;
                descartados.Add(ajuste);
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Primer tramo de una ruta JSON: «$.Folders[3]» → «Folders», «$['Mi ajuste']» → «Mi ajuste».</summary>
    private static string? AjusteDe(string? ruta)
    {
        if (string.IsNullOrEmpty(ruta) || !ruta.StartsWith('$')) return null;
        var s = ruta[1..];
        if (s.StartsWith("['", StringComparison.Ordinal))
        {
            var fin = s.IndexOf("']", StringComparison.Ordinal);
            return fin > 2 ? s[2..fin] : null;
        }
        if (!s.StartsWith('.')) return null;
        s = s[1..];
        var corte = s.IndexOfAny(new[] { '.', '[' });
        var nombre = corte < 0 ? s : s[..corte];
        return nombre.Length > 0 ? nombre : null;
    }

    /// <summary>
    /// Solo el aspecto (tema y filas compactas), para pintar la pantalla de carga con el tema
    /// elegido. No descifra nada, no aparta nada y no avisa: eso lo hace <see cref="Load(AppPaths, out string)"/>
    /// cuando arranca el motor.
    ///
    /// Antes se usaba Load entero, en el hilo de interfaz y antes de la pantalla de carga: en macOS
    /// los setters de las credenciales llaman al Llavero, que puede quedarse esperando a que el
    /// usuario lo desbloquee; y un archivo ilegible se apartaría aquí, con el aviso tirado, y el
    /// motor ya no se enteraría.
    /// </summary>
    public static AppConfig LeerAspecto(AppPaths paths)
    {
        var cfg = new AppConfig();
        try
        {
            if (!File.Exists(paths.ConfigPath)) return cfg;
            using var doc = JsonDocument.Parse(File.ReadAllText(paths.ConfigPath, Encoding.UTF8), DocumentoTolerante);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return cfg;
            if (doc.RootElement.TryGetProperty(nameof(Theme), out var tema) && tema.ValueKind == JsonValueKind.String)
                cfg.Theme = tema.GetString() ?? cfg.Theme;
            if (doc.RootElement.TryGetProperty(nameof(CompactRows), out var compactas) && compactas.ValueKind is JsonValueKind.True or JsonValueKind.False)
                cfg.CompactRows = compactas.GetBoolean();
        }
        catch { /* el aspecto de fábrica; el motor dirá luego qué le pasa al archivo */ }
        return cfg;
    }

    /// <summary>
    /// Guarda de forma segura: 1) cifra TODO en memoria (si falla, no toca el archivo anterior);
    /// 2) escribe a un temporal y lo intercambia de forma atómica. Devuelve false + error si falla.
    /// Las credenciales que no se pudieron descifrar se guardan con su cifrado original.
    /// </summary>
    public bool Save(AppPaths paths, out string error)
    {
        error = "";
        if (GuardadoBloqueado)
        {
            error = "config.net.json no se pudo leer al arrancar; no se guarda para no sustituirlo por los valores de fábrica. " +
                    "Cierra la aplicación y revisa el archivo.";
            return false;
        }
        string json;
        try { json = JsonSerializer.Serialize(this, JsonOpts); }   // cifra los secretos aquí
        catch (Exception e) { error = "No se pudieron cifrar las credenciales: " + e.Message; return false; }

        var tmp = paths.ConfigPath + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.DataDir);
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(paths.ConfigPath)) File.Replace(tmp, paths.ConfigPath, null);   // intercambio atómico
            else File.Move(tmp, paths.ConfigPath);
            return true;
        }
        catch (Exception e)
        {
            error = "No se pudo guardar la configuración: " + e.Message;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    // --- Pendientes: «Buscar en mi pool» (equipo «listas», tanda 3) ---

    /// <summary>
    /// Dirección de búsqueda del pool de descargas del usuario, con «{q}» donde va lo que se busca
    /// («https://…/search?q={q}»). Vacía mientras no se configure. BeatTag solo abre esa dirección en
    /// el navegador: no inicia sesión en el pool ni lee nada de él.
    /// </summary>
    public string PoolBusquedaUrl { get; set; } = "";

    // --- Asistente: «Limpiar la publicidad de los packs» (equipo «asistente», tanda 3, P8) ---

    /// <summary>
    /// Qué se hace con el pack o el usuario que se quitan del Álbum y del Artista: true = se guardan en
    /// el comentario («[Pack: Latin Box 15 12 2025]»), false = se borran. Por defecto se guardan: para
    /// algunos DJ la fecha del pack sirve para ordenar por pack.
    /// </summary>
    public bool PacksAlComentario { get; set; } = true;

    // --- Catálogo: ISRC (equipo catalogo_bandeja, C14) ---

    /// <summary>
    /// Escribir el código ISRC que da Deezer al aplicar una propuesta. Desactivado por defecto: casi
    /// ningún programa de DJ lo enseña, y activarlo de fábrica añadiría un cambio a miles de archivos
    /// al aplicar. Solo rellena el campo si está vacío, y solo cuando el archivo es la grabación del
    /// catálogo (ver FileProcessor.IsrcDelArchivo).
    /// </summary>
    public bool WriteIsrc { get; set; }

    // --- Volumen: exclusiones del ajuste (equipo «volumen_copias», tanda 4, E15) ---

    /// <summary>
    /// Carpetas cuyas canciones no se ajustan en Volumen (ni las de sus subcarpetas): acapellas,
    /// intros o ediciones que el DJ quiere más bajas a propósito. Se siguen midiendo y se ven en la
    /// tabla; solo quedan fuera del ajuste. Antes, la única forma de protegerlas era desmarcar la
    /// carpeta en Biblioteca, que la sacaba de todas las pestañas.
    /// </summary>
    public List<string> VolumenCarpetasExcluidas { get; set; } = new();

    /// <summary>Canciones sueltas que no se ajustan en Volumen, por su ruta.</summary>
    public List<string> VolumenCancionesExcluidas { get; set; } = new();

    // --- rekordbox: cruce y exportación (equipo «rekordbox», tanda 4, P3 y P11) ---

    /// <summary>
    /// La última colección exportada de rekordbox que se abrió en la página rekordbox, para volver a
    /// cruzarla al abrir la página sin tener que buscarla. Solo se lee: BeatTag nunca la modifica.
    /// </summary>
    public string RekordboxXml { get; set; } = "";

    /// <summary>
    /// Dónde se guardó la última exportación de listas para rekordbox. rekordbox lee siempre el mismo
    /// archivo (Preferencias → Avanzado → rekordbox xml), así que volver a exportar al mismo sitio basta
    /// para que vea las listas al día.
    /// </summary>
    public string RekordboxExportacion { get; set; } = "";

    // --- Novedades de tus artistas (equipo «novedades», tanda 4, P5) ---

    /// <summary>
    /// Cuántos de los artistas con más canciones de la biblioteca se consultan en Novedades (50, 100,
    /// 200 o 300; ver Providers.PresentacionNovedades.OpcionesArtistas). Un valor fuera de esas opciones
    /// se lleva a la más cercana al abrir la página.
    /// </summary>
    public int NovedadesArtistas { get; set; } = 100;

    /// <summary>De cuántos días atrás son los lanzamientos que se enseñan en Novedades (30, 60 o 90).</summary>
    public int NovedadesDias { get; set; } = 30;

    // --- Organizar por género (B8) ---

    /// <summary>
    /// Dónde se crean las carpetas de los géneros: «porgenero» (una carpeta «Por género» dentro de cada
    /// carpeta de la biblioteca, lo recomendado), «raiz» (directamente en cada carpeta de la biblioteca)
    /// o «carpeta» (en <see cref="OrganizarCarpeta"/>). Un valor desconocido cuenta como «porgenero».
    /// </summary>
    public string OrganizarDestino { get; set; } = "porgenero";

    /// <summary>Con <see cref="OrganizarDestino"/> «carpeta», cuál: tiene que estar dentro de la biblioteca.</summary>
    public string OrganizarCarpeta { get; set; } = "";

    /// <summary>Dentro de cada género, las subcarpetas de origen.</summary>
    public bool OrganizarConservarEstructura { get; set; }

    /// <summary>Las canciones sin género van a «Sin género» (sin marcar, se quedan donde están).</summary>
    public bool OrganizarSinGenero { get; set; }

    /// <summary>Los mashups, las transiciones, las acapellas y los instrumentales se quedan donde están.</summary>
    public bool OrganizarDejarHerramientas { get; set; } = true;

    /// <summary>Los géneros con menos canciones que esto van a «Otros géneros»; 0, sin agrupar.</summary>
    public int OrganizarAgruparMenosDe { get; set; }
}
