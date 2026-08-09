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

    /// <summary>Tipo de versión detectada ("Remix", "Bootleg", "Edit"…). Vacío = original.</summary>
    public string RemixKind { get; set; } = "";
    public int DurLocal { get; set; }
    public string DurMatch { get; set; } = "";
    public string FieldSrc { get; set; } = "";
}
