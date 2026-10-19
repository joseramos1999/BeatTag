using System.IO;

namespace Etiquetador.App.Services;

/// <summary>
/// Fecha de modificación y tamaño de un archivo en un momento dado: con lo que las cachés de audio
/// deciden si lo que guardaron sigue valiendo. Se toma JUSTO ANTES de que la aplicación escriba en el
/// archivo, para poder decir después «esta entrada valía antes de escribir; el audio no ha cambiado;
/// que siga valiendo» (hallazgos A2 y X4).
/// </summary>
public readonly record struct SelloArchivo(long M, long S, bool Existe)
{
    public static SelloArchivo De(string ruta)
    {
        try
        {
            var fi = new FileInfo(ruta);
            return fi.Exists ? new SelloArchivo(fi.LastWriteTimeUtc.Ticks, fi.Length, true) : default;
        }
        catch { return default; }
    }
}
