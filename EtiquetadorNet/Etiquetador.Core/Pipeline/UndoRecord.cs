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
}
