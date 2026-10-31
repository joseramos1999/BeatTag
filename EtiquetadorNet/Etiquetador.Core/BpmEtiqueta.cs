using System.Globalization;

namespace Etiquetador.Core;

/// <summary>
/// El BPM de una etiqueta, leído sin depender de la configuración regional del sistema.
///
/// POR QUÉ. TagLib lee el texto de TBPM (ID3v2) y el del campo BPM de APE con double.TryParse en la
/// cultura del sistema, y en la española el punto separa los miles: «102.000000», como lo guardan
/// algunos programas, salía como 102.000.000 BPM, y «102.5» como 1025. MEDIDO en una biblioteca real:
/// 2 archivos con TBPM «102.000000». Aquí el texto se lee con la cultura invariable, se admite también
/// la coma decimal («102,5», igual que en el Editor) y lo que pasa de <see cref="Maximo"/> se descarta.
/// </summary>
public static class BpmEtiqueta
{
    /// <summary>El BPM más alto que se da por bueno. Lo de encima no es un tempo, sino un texto mal leído.</summary>
    public const uint Maximo = 999;

    /// <summary>
    /// El número de un texto de BPM: «102», «102.5», «102,5» o «102.000000». Sin signo, sin separador
    /// de miles y sin unidades. false si no es un número o pasa de <see cref="Maximo"/>.
    /// </summary>
    public static bool TryLeer(string? texto, out double valor)
    {
        valor = 0;
        var t = (texto ?? "").Trim();
        if (t.Length == 0) return false;
        if (!double.TryParse(t.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v > Maximo)
            return false;
        valor = v;
        return true;
    }

    /// <summary>El BPM entero de un texto, redondeado como en el Editor (102,5 da 103). 0 si no hay uno válido.</summary>
    public static uint DeTexto(string? texto)
        => TryLeer(texto, out var v) ? (uint)Math.Round(v, MidpointRounding.AwayFromZero) : 0;

    /// <summary>
    /// El BPM de la etiqueta de un archivo; 0 si no tiene uno válido. Recorre las etiquetas en el mismo
    /// orden que TagLib y se queda con el primer BPM válido. De las que guardan el BPM como texto
    /// (ID3v2, APE, Xiph, WMA) lee el texto tal cual está en el archivo.
    /// </summary>
    public static uint Leer(TagLib.Tag? tag)
    {
        switch (tag)
        {
            case null:
                return 0;
            case TagLib.CombinedTag combinada:   // MP3 (ID3v2, APE, ID3v1), FLAC, WAV…
                foreach (var t in combinada.Tags)
                    if (Leer(t) is var bpm and > 0) return bpm;
                return 0;
            case TagLib.Ogg.GroupedComment ogg:  // OGG y Opus: un comentario por flujo
                foreach (var c in ogg.Comments)
                    if (Leer(c) is var bpm and > 0) return bpm;
                return 0;
            case TagLib.Id3v2.Tag id3:           // también el de los AIFF
                return DeTexto(id3.GetTextAsString("TBPM"));
            case TagLib.Ape.Tag ape:
                return DeTexto(ape.GetItem("BPM")?.ToString());
            case TagLib.Ogg.XiphComment xiph:    // TagLib mira TEMPO y, si no está, BPM
                return DeTexto(xiph.GetFirstField("TEMPO") ?? xiph.GetFirstField("BPM"));
            case TagLib.Asf.Tag asf:
                // Como número (DWORD) no tiene texto, y ese TagLib lo lee bien.
                var texto = asf.GetDescriptorString("WM/BeatsPerMinute");
                return string.IsNullOrEmpty(texto) ? Acotado(asf.BeatsPerMinute) : DeTexto(texto);
            default:                             // MP4 (un entero), INFO de WAV…
                return Acotado(tag.BeatsPerMinute);
        }

        static uint Acotado(uint bpm) => bpm <= Maximo ? bpm : 0;
    }
}
