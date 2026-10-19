namespace Etiquetador.Core.Pipeline;

/// <summary>Qué campos de tag está permitido escribir (casillas de la UI).</summary>
public sealed class FieldFlags
{
    public bool Title { get; set; } = true;
    public bool Artist { get; set; } = true;
    public bool Album { get; set; } = true;
    public bool Genre { get; set; } = true;
    public bool Year { get; set; } = true;
    public bool Bpm { get; set; } = true;

    /// <summary>
    /// Escribir el ISRC (C14). Desactivado salvo que el usuario lo active en Ajustes: es un dato que
    /// casi nadie mira y que tocaría miles de archivos al aplicar.
    /// </summary>
    public bool Isrc { get; set; }

    public static FieldFlags All => new();
}

/// <summary>
/// Resultado de procesar un archivo (equivale al PSCustomObject de Process-File).
/// Lo consume la fase de escritura (ApplyEngine) y la previsualización/UI.
/// </summary>
public sealed class ProcessResult
{
    public string FilePath { get; init; } = "";
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Year { get; set; } = "";
    public string Genre { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public string Bpm { get; set; } = "";

    /// <summary>
    /// El BPM propuesto cuando sale del catálogo (no del nombre del archivo); vacío si no. Sirve para
    /// no pisar con él el BPM que el archivo ya tiene, ni con «Sobrescribir» (ver Tagging). Si el
    /// usuario cambia el BPM en la tabla, deja de coincidir y se escribe el suyo.
    /// </summary>
    public string BpmCatalogo { get; set; } = "";
    public bool Found { get; set; }
    public bool GenreOnly { get; set; }
    public string Source { get; set; } = "-";
    public string SpDiag { get; set; } = "off";
    public string Kw { get; set; } = "";
    public bool CleanOnly { get; set; }
    public bool Skip { get; set; }
    public string Variant { get; set; } = "";
    public string Score { get; set; } = "";

    /// <summary>Quién firma la versión ("Tiesto"), vacío si es el tema original o no se pudo saber.</summary>
    public string Remixer { get; set; } = "";

    /// <summary>
    /// Lo que se escribe en el tag Remixer (TPE4 en ID3, REMIXER en Vorbis y MP4): solo la persona
    /// que firma la versión, ya comprobada. Vacío = no se toca ese tag.
    ///
    /// Va aparte de <see cref="Remixer"/> por la caché de análisis: las versiones anteriores guardaban
    /// en Remixer el texto en bruto, sin pasar por RemixParser.Autor y también cuando se había
    /// descartado por ser «el propio título» («Intro, Alex Torres Intro», «Look At Me Now», «Tik
    /// Tok»…). La firma de la caché no cambia al actualizar, así que esas filas se reutilizan tal cual
    /// y, si el tag saliera de Remixer, se escribirían en miles de archivos sin reanalizarlos. Lo que
    /// guardó una versión anterior no trae este campo, se lee como "" y no escribe nada.
    /// </summary>
    public string RemixerTag { get; set; } = "";

    /// <summary>Tipo de versión detectada ("Remix", "Bootleg", "Edit"…). Vacío = original.</summary>
    public string RemixKind { get; set; } = "";
    /// <summary>
    /// Lo que la IA local propuso cuando NINGÚN catálogo confirmó la canción. No se escribe nunca
    /// por su cuenta: viaja hasta la pestaña "No encontradas" para enseñárselo al usuario como
    /// sugerencia de un clic. Vacío si no hubo propuesta o si sí quedó verificada (entonces los
    /// datos buenos son los del catálogo, que están en Artist/Title).
    /// </summary>
    public string AiArtist { get; set; } = "";
    public string AiTitle { get; set; } = "";
    public string AiVersion { get; set; } = "";

    public int DurLocal { get; set; }
    public string DurMatch { get; set; } = "";
    /// <summary>
    /// Por que se propuso esto, en claro: que hizo ganar a la coincidencia y como la corrigen los
    /// tags que el archivo ya traia. Se ensena en la tabla para poder juzgar una propuesta dudosa
    /// sin abrir el registro. Vacio si no hubo coincidencia de catalogo.
    /// </summary>
    public string Why { get; set; } = "";

    /// <summary>
    /// Por qué esta propuesta no debe aplicarse sin mirarla, o "" si no hay motivo. La rellenan
    /// FileProcessor (lo que solo identificó una fuente de rescate) y <see cref="NombrePropuesto"/>
    /// (huellas de una regla que ha fallado: guion colgando, título vacío…). La interfaz desmarca la
    /// fila y enseña el motivo; el motivo va también al principio de <see cref="Why"/>.
    /// </summary>
    public string Revisar { get; set; } = "";

    public string FieldSrc { get; set; } = "";

    /// <summary>
    /// Versión de las reglas con que se obtuvo este resultado: la pone FileProcessor. 0 = lo guardó
    /// en la caché una versión anterior, sin las comprobaciones de ahora (motivo para revisar, BPM del
    /// nombre). <see cref="ResultadoAnterior"/> lo pone al día al leerlo de la caché.
    ///
    /// No lleva valor inicial a propósito: al leer de la caché un resultado que no trae el campo, se
    /// queda el valor por defecto, y eso es justo lo que lo distingue de uno nuevo.
    /// </summary>
    public int Reglas { get; set; }

    /// <summary>
    /// Versión actual de las reglas de análisis (ver <see cref="Reglas"/>). 2: los catálogos ya no
    /// dan la instrumental o la acapella por la canción (C2), y lo guardado antes que la proponga
    /// sin que el archivo la nombre sale para revisar (ver <see cref="ResultadoAnterior"/>).
    /// </summary>
    public const int ReglasActuales = 2;

    /// <summary>
    /// Peticiones a los catálogos que se quedaron sin respuesta mientras se analizaba ESTE archivo
    /// (sin conexión, tiempo agotado, 5xx, cuota…; ver ApiClient.FallosDeRed). Si no se encontró
    /// nada y esto es mayor que cero, «no encontrada» no es un dato: AnalysisCache no lo guarda,
    /// y el siguiente análisis vuelve a preguntar. No se guarda en la caché.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int FallosDeRed { get; set; }

    /// <summary>¿Un «no encontrada» que puede deberse a la red y no al catálogo?</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IncompletoPorLaRed => !Found && !Skip && !CleanOnly && FallosDeRed > 0;

    // ---- ISRC y letra del catálogo (C14) ----
    //
    // Compatibilidad con la caché de análisis: lo que guardó una versión anterior no trae estos
    // campos, se leen como "" y «sin dato», no se escribe nada y no se invalida ninguna entrada. La
    // firma de la caché no cambia: solo se rellenan al volver a analizar una canción.

    /// <summary>
    /// El ISRC de la grabación que identificó Deezer, o "". Solo cuando el archivo ES esa grabación:
    /// sin versiones que el título del catálogo no nombre (una extended, un remix o una intro de DJ
    /// tienen otro código, o ninguno) y con la misma letra (la versión clean tiene el suyo). Ver
    /// FileProcessor.IsrcDelArchivo.
    /// </summary>
    public string Isrc { get; set; } = "";

    /// <summary>
    /// A qué propuesta pertenece <see cref="Isrc"/>: artista y título normalizados en el momento del
    /// análisis. Si el usuario los cambia en la tabla -porque la coincidencia era otra canción- el ISRC
    /// deja de valer y no se escribe (igual que <see cref="BpmCatalogo"/>). Ver <see cref="IsrcVigente"/>.
    /// </summary>
    public string IsrcDe { get; set; } = "";

    /// <summary>
    /// Lo que dice Deezer de la letra de la pista que identificó, o «sin dato». Es el dato del catálogo
    /// sin mezclar con el nombre: quien lo use lo junta con <see cref="Analysis.ExplicitDetector.Combinar"/>.
    /// Se guarda como texto («Explicit») para que la caché no dependa del orden del enumerado.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Analysis.Explicitness>))]
    public Analysis.Explicitness Explicit { get; set; }

    /// <summary>La clave con la que se ata el ISRC a la propuesta (artista|título normalizados).</summary>
    public static string ClaveIsrc(string? artista, string? titulo) => TextUtils.Nk(artista) + "|" + TextUtils.Nk(titulo);

    /// <summary>El ISRC que se puede escribir ahora: "" si no hay o si la propuesta se ha editado a mano.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string IsrcVigente => Isrc.Length > 0 && IsrcDe == ClaveIsrc(Artist, Title) ? Isrc : "";
}
