namespace Etiquetador.Core.Analysis;

/// <summary>Resultado de ajustar el volumen de un MP3.</summary>
/// <param name="Ok">Se escribió el archivo.</param>
/// <param name="Steps">Pasos aplicados (cada uno vale 1,5 dB).</param>
/// <param name="Frames">Tramas modificadas.</param>
/// <param name="Error">Motivo si no se pudo.</param>
public readonly record struct Mp3GainResult(bool Ok, int Steps, int Frames, string Error, int Clamped = 0)
{
    public double Db => Steps * Mp3Gain.DbPerStep;
}

/// <summary>
/// Cambia el volumen de un MP3 SIN RECODIFICAR, como hace MP3Gain: cada trama lleva un campo
/// "global_gain" y basta con sumarle o restarle unidades. No se toca el audio comprimido, así que
/// no hay pérdida de calidad y el cambio es exactamente reversible (se aplica el inverso).
///
/// La contrapartida es que el paso mínimo es 1,5 dB: no se puede afinar más que eso.
/// </summary>
public static class Mp3Gain
{
    /// <summary>Cada unidad de global_gain equivale a 1,5 dB.</summary>
    public const double DbPerStep = 1.5;

    /// <summary>Pasos necesarios para acercarse a <paramref name="db"/> (redondeando al más cercano).</summary>
    public static int StepsFor(double db) => (int)Math.Round(db / DbPerStep, MidpointRounding.AwayFromZero);

    /// <summary>Comprueba que el archivo se puede procesar y cuenta sus tramas, sin modificar nada.</summary>
    public static Mp3GainResult Analyze(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var n = Recorrer(bytes, 0, out var error, ensayo: true, out _);
            return n > 0
                ? new Mp3GainResult(true, 0, n, "")
                : new Mp3GainResult(false, 0, 0, error.Length > 0 ? error : "no se encontraron tramas MP3");
        }
        catch (Exception e) { return new Mp3GainResult(false, 0, 0, e.Message); }
    }

    /// <summary>
    /// Aplica el cambio de volumen. Escribe primero en un temporal y luego sustituye, para que un
    /// fallo a medias no deje el archivo del usuario a medio escribir.
    /// </summary>
    public static Mp3GainResult Apply(string path, int steps)
    {
        if (steps == 0) return new Mp3GainResult(true, 0, 0, "");
        try
        {
            var bytes = File.ReadAllBytes(path);

            // 1) Ensayo: se comprueba que el archivo se recorre entero y CUÁNTAS tramas se saldrían
            //    de rango. Si son demasiadas, el cambio deformaría la canción y no se hace.
            var n = Recorrer(bytes, steps, out var error, ensayo: true, out var recortadas);
            if (n <= 0) return new Mp3GainResult(false, 0, 0, error.Length > 0 ? error : "no se encontraron tramas MP3");

            // Un puñado de tramas al límite son silencios digitales (recortarlas no se oye).
            // Si afecta a más del 1 %, es que el cambio pedido es demasiado grande para este archivo.
            if (recortadas > Math.Max(4, n / 100))
                return new Mp3GainResult(false, 0, n,
                    $"el cambio es demasiado grande para este archivo ({recortadas} tramas se saldrían de rango)", recortadas);

            // 2) De verdad.
            n = Recorrer(bytes, steps, out error, ensayo: false, out recortadas);

            var tmp = path + ".beattag.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return new Mp3GainResult(true, steps, n, "", recortadas);
        }
        catch (Exception e) { return new Mp3GainResult(false, 0, 0, e.Message); }
    }

    /// <summary>
    /// Recorre las tramas del MP3 sumando <paramref name="steps"/> a cada global_gain.
    /// Devuelve cuántas tramas se procesaron; <paramref name="error"/> se rellena si alguna
    /// se saldría de rango (solo en modo ensayo).
    /// </summary>
    private static int Recorrer(byte[] b, int steps, out string error, bool ensayo, out int recortadas)
    {
        error = "";
        recortadas = 0;
        var i = SaltarId3(b);
        var tramas = 0;

        while (i + 4 <= b.Length)
        {
            // Sincronismo de trama: 11 bits a 1.
            if (b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) { i++; continue; }

            var versionId = (b[i + 1] >> 3) & 0x03;   // 0=MPEG2.5, 2=MPEG2, 3=MPEG1
            var layer = (b[i + 1] >> 1) & 0x03;       // 1 = Layer III
            var protection = (b[i + 1] & 0x01) == 0;  // 0 => lleva CRC
            var bitrateIdx = (b[i + 2] >> 4) & 0x0F;
            var rateIdx = (b[i + 2] >> 2) & 0x03;
            var padding = (b[i + 2] >> 1) & 0x01;
            var mode = (b[i + 3] >> 6) & 0x03;        // 3 = mono

            if (versionId == 1 || layer != 1 || bitrateIdx is 0 or 15 || rateIdx == 3) { i++; continue; }

            var mpeg1 = versionId == 3;
            var bitrate = Bitrate(mpeg1, bitrateIdx);
            var sampleRate = SampleRate(versionId, rateIdx);
            if (bitrate == 0 || sampleRate == 0) { i++; continue; }

            var largo = mpeg1
                ? 144 * bitrate * 1000 / sampleRate + padding
                : 72 * bitrate * 1000 / sampleRate + padding;
            if (largo < 8 || i + largo > b.Length) break;

            // El "side info" empieza tras la cabecera (y tras el CRC si lo hay).
            var si = i + 4 + (protection ? 2 : 0);
            var mono = mode == 3;
            var canales = mono ? 1 : 2;
            var granulos = mpeg1 ? 2 : 1;

            // La PRIMERA trama suele ser la cabecera Xing/Info/VBRI: son metadatos (duración,
            // índice de búsqueda), no audio, y va toda a cero. No se toca ni cuenta como problema.
            var sideLen = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
            if (EsTramaDeMetadatos(b, i, si, sideLen)) { i += largo; continue; }

            // Bits que ocupan main_data_begin + private_bits (+ scfsi en MPEG1) antes de los bloques.
            int cabeceraBits = mpeg1
                ? (mono ? 9 + 5 + 4 : 9 + 3 + 8)
                : (mono ? 8 + 1 : 8 + 2);
            int bloqueBits = mpeg1 ? 59 : 63;   // tamaño del bloque de cada gránulo/canal
            const int antesDelGain = 12 + 9;    // part2_3_length + big_values

            for (int g = 0; g < granulos; g++)
            {
                for (int c = 0; c < canales; c++)
                {
                    var bit = cabeceraBits + (g * canales + c) * bloqueBits + antesDelGain;
                    var pos = si + bit / 8;
                    if (pos + 1 >= b.Length) { error = "trama incompleta"; return tramas; }

                    var actual = LeerByteEnBit(b, si, bit);
                    var nuevo = actual + steps;
                    if (nuevo is < 0 or > 255)
                    {
                        recortadas++;
                        nuevo = Math.Clamp(nuevo, 0, 255);
                    }
                    if (!ensayo) EscribirByteEnBit(b, si, bit, (byte)nuevo);
                }
            }

            tramas++;
            i += largo;
        }
        return tramas;
    }

    /// <summary>
    /// ¿Es la trama de metadatos VBR (Xing/Info/VBRI)? Va justo tras el "side info" (o en i+36
    /// para VBRI) y no contiene audio, así que no debe tocarse.
    /// </summary>
    private static bool EsTramaDeMetadatos(byte[] b, int frameStart, int si, int sideLen)
    {
        if (si + sideLen + 4 <= b.Length)
        {
            var t = b[si + sideLen];
            if ((t == 'X' && b[si + sideLen + 1] == 'i' && b[si + sideLen + 2] == 'n' && b[si + sideLen + 3] == 'g') ||
                (t == 'I' && b[si + sideLen + 1] == 'n' && b[si + sideLen + 2] == 'f' && b[si + sideLen + 3] == 'o'))
                return true;
        }
        if (frameStart + 40 <= b.Length &&
            b[frameStart + 36] == 'V' && b[frameStart + 37] == 'B' &&
            b[frameStart + 38] == 'R' && b[frameStart + 39] == 'I')
            return true;
        return false;
    }

    /// <summary>Lee 8 bits a partir de un desplazamiento en bits (puede quedar a caballo entre bytes).</summary>
    private static int LeerByteEnBit(byte[] b, int baseByte, int bitOffset)
    {
        var pos = baseByte + bitOffset / 8;
        var shift = bitOffset % 8;
        if (shift == 0) return b[pos];
        return ((b[pos] << shift) | (b[pos + 1] >> (8 - shift))) & 0xFF;
    }

    private static void EscribirByteEnBit(byte[] b, int baseByte, int bitOffset, byte value)
    {
        var pos = baseByte + bitOffset / 8;
        var shift = bitOffset % 8;
        if (shift == 0) { b[pos] = value; return; }

        // Parte alta en el primer byte y parte baja en el siguiente.
        var maskHi = (byte)(0xFF << (8 - shift));          // bits que se conservan del primero
        b[pos] = (byte)((b[pos] & maskHi) | (value >> shift));
        var maskLo = (byte)(0xFF >> shift);                // bits que se conservan del segundo
        b[pos + 1] = (byte)((b[pos + 1] & maskLo) | ((value << (8 - shift)) & 0xFF));
    }

    /// <summary>Se salta la etiqueta ID3v2 del principio, si la hay.</summary>
    private static int SaltarId3(byte[] b)
    {
        if (b.Length < 10 || b[0] != 'I' || b[1] != 'D' || b[2] != '3') return 0;
        // Tamaño sincroseguro: 7 bits útiles por byte.
        var size = (b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F);
        var fin = 10 + size;
        return fin < b.Length ? fin : 0;
    }

    private static readonly int[] BitratesV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    private static readonly int[] BitratesV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

    private static int Bitrate(bool mpeg1, int idx) => (mpeg1 ? BitratesV1 : BitratesV2)[idx];

    private static int SampleRate(int versionId, int idx)
    {
        int[] baseRates = { 44100, 48000, 32000 };
        var r = baseRates[idx];
        return versionId switch
        {
            3 => r,        // MPEG1
            2 => r / 2,    // MPEG2
            0 => r / 4,    // MPEG2.5
            _ => 0,
        };
    }
}
