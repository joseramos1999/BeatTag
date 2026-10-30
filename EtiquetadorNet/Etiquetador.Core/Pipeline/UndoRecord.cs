using System.Collections.Generic;

namespace Etiquetador.Core.Pipeline;

/// <summary>Tipo de campo de tag (para saber cómo comparar/restaurar).</summary>
public enum FieldKind { Str, Arr, Num }

/// <summary>
/// Cambio de un campo de tag: guarda el valor ANTERIOR y el valor que la app ESCRIBIÓ.
/// Al deshacer, solo se restaura el anterior si el actual sigue siendo el escrito por la app.
/// </summary>
public sealed class FieldChange
{
    public FieldKind Kind { get; set; }
    public string? OldStr { get; set; }
    public string? NewStr { get; set; }
    public List<string>? OldArr { get; set; }
    public List<string>? NewArr { get; set; }
    public uint OldNum { get; set; }
    public uint NewNum { get; set; }

    public static FieldChange Str(string? oldVal, string? newVal)
        => new() { Kind = FieldKind.Str, OldStr = oldVal, NewStr = newVal };

    public static FieldChange Arr(IEnumerable<string> oldVal, IEnumerable<string> newVal)
        => new() { Kind = FieldKind.Arr, OldArr = new List<string>(oldVal), NewArr = new List<string>(newVal) };

    public static FieldChange Num(uint oldVal, uint newVal)
        => new() { Kind = FieldKind.Num, OldNum = oldVal, NewNum = newVal };

    /// <summary>
    /// Sustitución de la carátula frontal (contrato «Cover», se anota con la clave
    /// <see cref="UndoEngine.CoverField"/>): <paramref name="copiaAnterior"/> es la ruta absoluta de
    /// la copia de la carátula anterior en <see cref="AppPaths.PortadasDir"/>, o "" si el archivo no
    /// tenía. Deshacer la vuelve a poner como frontal, o quita la frontal si no había.
    ///
    /// <paramref name="puesta"/> son los bytes de la carátula que se pone: su SHA-1 va en NewStr
    /// («sustituida:&lt;sha1&gt;») para que deshacer no pise una carátula cambiada a mano después.
    /// Sin ella se anota la marca sola, como en la 1.9.x, y deshacer no puede comprobarlo.
    /// </summary>
    public static FieldChange Portada(string? copiaAnterior, byte[]? puesta = null)
        => Str(copiaAnterior ?? "", UndoEngine.MarcaCover(puesta));
}

/// <summary>Una línea del manifiesto reversible: rutas absolutas + cambios por campo.</summary>
public sealed class UndoRecord
{
    public string OrigPath { get; set; } = "";   // ruta absoluta original
    public string FinalPath { get; set; } = "";  // ruta absoluta tras renombrar (o = OrigPath)
    public bool Renamed { get; set; }
    public Dictionary<string, FieldChange>? Fields { get; set; }

    /// <summary>
    /// true si este cambio dio la canción por aplicada (aplicadas.json): lo anotan Enriquecer y No
    /// encontradas al aplicar una propuesta. Deshacer solo devuelve a «pendientes» lo que lleva esta
    /// marca.
    ///
    /// Visto en revisión: «Deshacer última» es un único botón para todo (volumen, Unificar géneros,
    /// fichas al comentario, BPM y clave de rekordbox, Renombrar con IA…), y deshacer un ajuste de
    /// volumen sacaba de aplicadas.json las 640 canciones aplicadas antes en Enriquecer, sin forma de
    /// recuperarlas: volvían a consultarse y las aplicadas con la propuesta editada reaparecían
    /// marcadas.
    ///
    /// Se escribe SIEMPRE, también cuando es false, a propósito: así un registro sin el campo es de un
    /// manifiesto anterior a esta marca (ver <see cref="UndoEngine.DioPorAplicada"/>). Quien escribe
    /// el manifiesto serializando un UndoRecord con las opciones por defecto la lleva sin hacer nada.
    /// </summary>
    public bool Aplicada { get; set; }

    // --- Historial de deshacer (E14/M14) ---

    /// <summary>
    /// Qué operación escribió este registro, con el nombre de un <see cref="TipoEjecucion"/>
    /// («Volumen», «Generos»…). Es OPCIONAL: los manifiestos anteriores no lo llevan, y el historial
    /// deduce el tipo de lo que guarda cada registro (<see cref="HistorialDeshacer.DeducirTipo"/>).
    /// Solo hace falta donde esa deducción no distingue dos operaciones con los mismos campos (el
    /// Editor y Unificar géneros escriben los dos solo «Genre»).
    ///
    /// Sin valor no se escribe, así que los manifiestos de quien no lo pone quedan exactamente como
    /// antes.
    ///
    /// Lo pone cada escritor con <c>nameof(TipoEjecucion.X)</c>: aplicar propuestas (ApplyEngine, según
    /// quien llama), el Editor, Volumen, Unificar géneros, Renombrar con IA y Completar nombres, las
    /// fichas al comentario, la importación de rekordbox, «Pasar a la biblioteca» de la Bandeja
    /// (Traslado), las dos herramientas de reglas del Asistente (EscrituraPorReglas: LimpiezaPacks y
    /// GeneroPorReglas), «Sustituir» de la Bandeja (Sustitucion: el traslado y las etiquetas
    /// copiadas) y «Organizar por género» (OrganizadorPorGenero: OrganizarPorGenero, solo traslados).
    /// Un escritor nuevo debe ponerlo también: sin él, el historial deduce el tipo por los campos y
    /// puede confundirlo con otra herramienta.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Origen { get; set; }

    /// <summary>
    /// Solo en los registros de la pestaña Volumen: true si el ajuste quedó anotado en la ganancia
    /// aplicada de la medida del archivo (la «G» de sonoridad.json y volumen-aplicado.json). Al
    /// deshacerlo, solo los pasos de los registros con esta marca se restan de esa ganancia; los demás
    /// solo desplazan la medida.
    ///
    /// POR QUÉ. BeatTag 1.10 ajustaba sin anotar la ganancia. Al deshacer uno de esos ajustes con una
    /// versión posterior, sus pasos se sumaban a una ganancia que nunca los había contado: un archivo
    /// devuelto a su nivel original quedaba con +3 dB «aplicados», su nivel original calculado pasaba
    /// a ser el ya ajustado, y el siguiente «Ajustar» automático partía de una referencia equivocada.
    ///
    /// Sin valor no se escribe: los manifiestos de antes y los de las demás herramientas quedan igual.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? GananciaAnotada { get; set; }

    /// <summary>
    /// Carpetas que creó la ejecución para llevar ahí este archivo, de la más alta a la más profunda
    /// («…/Por género» y «…/Por género/Pop»). Las lleva el PRIMER registro de la ejecución que movió un
    /// archivo a cada una. Al deshacer cualquier parte de la ejecución, al terminar se quitan las que
    /// hayan quedado vacías de las que contienen el destino de lo deshecho, de la más profunda a la más
    /// alta, cuente quien las cuente (ver UndoEngine.QuitarCarpetasCreadas).
    ///
    /// POR QUÉ. Organizar por género crea unas 181 carpetas en una biblioteca real; deshacerlo sin esto
    /// las dejaba vacías. Se miran las de toda la ejecución, y no solo las del registro que las lleva,
    /// porque se puede deshacer por partes desde el historial y en cualquier orden (revisión 1).
    ///
    /// Sin valor no se escribe: los manifiestos de antes y los de las demás herramientas quedan igual.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CarpetasCreadas { get; set; }
}
