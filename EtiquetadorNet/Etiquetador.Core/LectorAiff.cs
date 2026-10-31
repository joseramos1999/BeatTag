namespace Etiquetador.Core;

// ---------------------------------------------------------------------------------------------
// AIFF-C: lo que se puede saber sin TagLib (revisión de M15, equipo catalogo)
// ---------------------------------------------------------------------------------------------
//
// POR QUÉ. TagLib# 2.x solo abre el AIFF clásico: con cualquier AIFF-C (el tipo de formulario
// «AIFC», que es lo que exportan QuickTime, Música o Core Audio en macOS) lanza «File form type is
// not AIFF». El escáner lo tragaba y el archivo entraba en la biblioteca sin artista, sin título y
// con duración 0, así que no contaba para Duplicados (que compara la duración) ni para Sustituir, y
// la calidad salía como «desconocida». Aplicar una propuesta fallaba con ese texto en inglés.
//
// Aquí se lee la cabecera (bloques COMM y SSND) y, si lo lleva, el bloque «ID3 » donde las
// aplicaciones guardan las etiquetas de un AIFF. Solo lectura: escribir en un AIFF-C sigue sin ser
// posible, y quien escribe lo dice con un texto claro (ver TextoNoSeEscribe).

/// <summary>Lo que dice la cabecera de un AIFF que hace falta para leer sus muestras.</summary>
/// <param name="Compresion">El tipo de AIFF-C («sowt», «twos»…), o «NONE» en un AIFF clásico.</param>
/// <param name="InicioDatos">Posición en el archivo de la primera muestra.</param>
/// <param name="LongitudDatos">Bytes de muestras.</param>
/// <param name="Tramas">Tramas (una muestra por canal) que declara el COMM.</param>
public readonly record struct CabeceraAiff(bool EsAifc, string Compresion, int Canales, int Bits, int Frecuencia,
                                           long InicioDatos, long LongitudDatos, long Tramas = 0)
{
    /// <summary>
    /// Las muestras van sin comprimir (en un orden de bytes u otro, enteras o en coma flotante): la
    /// duración sale de las tramas y la calidad, de la frecuencia, los bits y los canales. Con un tipo
    /// comprimido, lo que cuenta el COMM depende del códec y no se da por bueno.
    /// </summary>
    public bool SinComprimir => Compresion is "NONE" or "none" or "sowt" or "twos" or "in24" or "in32"
                                           or "fl32" or "FL32" or "fl64" or "FL64";
}

public static class LectorAiff
{
    /// <summary>Lo que se dice al intentar escribir las etiquetas de un AIFF-C.</summary>
    public const string TextoNoSeEscribe =
        "Este archivo es un AIFF-C (la variante de AIFF que exportan algunas aplicaciones de macOS) y BeatTag todavía no puede "
        + "escribir sus etiquetas, así que no se han tocado. Para etiquetarlo, conviértelo a AIFF o WAV sin comprimir con tu programa de audio.";

    /// <summary>¿Tiene extensión de AIFF (.aif, .aiff)?</summary>
    public static bool EsAiff(string ruta)
        => Path.GetExtension(ruta).ToLowerInvariant() is ".aif" or ".aiff";

    /// <summary>¿Es un AIFF-C (formulario «AIFC»)? Lee solo los 12 primeros bytes. Nunca lanza.</summary>
    public static bool EsAifc(string ruta)
    {
        try
        {
            using var fs = File.OpenRead(ruta);
            var b = new byte[12];
            return Leer(fs, b, 12) && Texto(b, 0) == "FORM" && Texto(b, 8) == "AIFC";
        }
        catch { return false; }
    }

    /// <summary>
    /// Lee la cabecera de un AIFF o AIFF-C, o null si no lo es o le falta el COMM o el SSND. Solo
    /// recorre las cabeceras de los bloques, sin leer el audio.
    /// </summary>
    public static CabeceraAiff? LeerCabecera(Stream s)
    {
        try
        {
            var b = new byte[22];
            if (!Leer(s, b, 12) || Texto(b, 0) != "FORM") return null;
            var tipo = Texto(b, 8);
            if (tipo is not ("AIFF" or "AIFC")) return null;
            var aifc = tipo == "AIFC";

            string? compresion = null;
            int canales = 0, bits = 0, frecuencia = 0;
            long inicio = -1, longitud = 0, tramas = 0;
            while (Leer(s, b, 8))
            {
                var id = Texto(b, 0);
                long largo = Be32(b, 4);
                var datos = s.Position;
                if (id == "COMM" && largo >= 18 && Leer(s, b, (int)Math.Min(22, largo)))
                {
                    canales = Be16(b, 0);
                    tramas = Be32(b, 2);
                    bits = Be16(b, 6);
                    var hz = Extendido(b, 8);
                    frecuencia = hz is >= 1 and <= 1_000_000 ? (int)Math.Round(hz) : 0;   // fuera de eso, cabecera dañada
                    compresion = aifc && largo >= 22 ? Texto(b, 18) : "NONE";
                }
                else if (id == "SSND" && largo >= 8 && Leer(s, b, 8))
                {
                    var desplazamiento = Be32(b, 0);
                    inicio = datos + 8 + desplazamiento;
                    longitud = Math.Max(0, Math.Min(largo - 8 - desplazamiento, s.Length - inicio));
                }
                if (compresion != null && inicio >= 0) break;
                s.Position = datos + largo + (largo & 1);   // los bloques de longitud impar llevan un byte de relleno
            }
            if (compresion == null || inicio < 0 || canales <= 0 || bits <= 0 || frecuencia <= 0) return null;
            return new CabeceraAiff(aifc, compresion, canales, bits, frecuencia, inicio, longitud, tramas);
        }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Las etiquetas del bloque «ID3 » de un AIFF, o null si no lo lleva o no se entiende. Recorre
    /// las cabeceras de los bloques (el ID3 suele ir detrás del audio, que se salta sin leerlo).
    /// </summary>
    internal static TagLib.Id3v2.Tag? LeerId3(Stream s)
    {
        try
        {
            s.Position = 0;
            var b = new byte[12];
            if (!Leer(s, b, 12) || Texto(b, 0) != "FORM") return null;
            while (Leer(s, b, 8))
            {
                var id = Texto(b, 0);
                long largo = Be32(b, 4);
                var datos = s.Position;
                // Una etiqueta de más de 16 MB no es una etiqueta: el archivo está dañado.
                if (id is "ID3 " or "id3 " && largo is > 10 and <= 16 * 1024 * 1024)
                {
                    var bytes = new byte[largo];
                    if (!Leer(s, bytes, (int)largo)) return null;
                    return new TagLib.Id3v2.Tag(new TagLib.ByteVector(bytes));
                }
                s.Position = datos + largo + (largo & 1);
                if (s.Position >= s.Length) break;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Lo que se puede saber de un AIFF sin TagLib: la duración, la frecuencia, los canales y la
    /// calidad de la cabecera, y las etiquetas del bloque ID3 si lo lleva. Null si no es un AIFF
    /// legible. Nunca lanza. Es lo que usa el escáner cuando TagLib no puede abrir el archivo.
    /// </summary>
    public static Track? LeerPista(string ruta)
    {
        try
        {
            using var fs = File.OpenRead(ruta);
            if (LeerCabecera(fs) is not { } c) return null;
            var t = new Track { FilePath = ruta, SampleRate = c.Frecuencia, Channels = c.Canales };
            if (c.SinComprimir)
            {
                // Como TagLib: los segundos enteros, sin redondear hacia arriba.
                if (c.Tramas > 0) t.DurationSeconds = (int)(c.Tramas / (double)c.Frecuencia);
                var bitsMuestra = (c.Bits + 7) / 8 * 8;
                t.Bitrate = (int)Math.Round(c.Frecuencia * (double)bitsMuestra * c.Canales / 1000);
            }
            if (LeerId3(fs) is { } id3)
            {
                t.Title = Vacio(id3.Title);
                t.Artist = Vacio(id3.JoinedPerformers);
                t.Album = Vacio(id3.Album);
                t.Genre = Vacio(id3.JoinedGenres);
                t.Year = id3.Year;
                t.Bpm = BpmEtiqueta.Leer(id3);
                t.Key = Vacio(id3.InitialKey);
            }
            return t;
        }
        catch { return null; }

        static string? Vacio(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>La frecuencia de muestreo, que AIFF guarda como número de coma flotante de 80 bits (IEEE 754 extendido).</summary>
    private static double Extendido(byte[] b, int i)
    {
        var exponente = ((b[i] & 0x7F) << 8) | b[i + 1];
        ulong mantisa = 0;
        for (var k = 0; k < 8; k++) mantisa = (mantisa << 8) | b[i + 2 + k];
        if (exponente == 0 && mantisa == 0) return 0;
        var valor = mantisa * Math.Pow(2, exponente - 16383 - 63);
        return (b[i] & 0x80) != 0 ? -valor : valor;
    }

    private static bool Leer(Stream s, byte[] b, int n)
    {
        var leidos = 0;
        while (leidos < n)
        {
            var r = s.Read(b, leidos, n - leidos);
            if (r == 0) return false;
            leidos += r;
        }
        return true;
    }

    private static string Texto(byte[] b, int i) => System.Text.Encoding.ASCII.GetString(b, i, 4);
    private static int Be16(byte[] b, int i) => (short)((b[i] << 8) | b[i + 1]);
    private static long Be32(byte[] b, int i) => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
}
