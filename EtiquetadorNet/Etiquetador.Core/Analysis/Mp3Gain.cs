using System.Buffers.Binary;
using System.Globalization;
using System.Text;

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

    /// <summary>
    /// Solo se puede tocar un MP3, y hay que comprobarlo ANTES de mirar el contenido.
    ///
    /// El recorrido de tramas busca la marca de sincronismo byte a byte por todo el archivo, así
    /// que en un FLAC, un WAV o un M4A puede dar con una secuencia que la imite por casualidad y
    /// creerse que ha encontrado audio MP3. Si eso pasara al aplicar, se reescribirían bytes en
    /// mitad de un archivo que no es un MP3: se corrompe la grabación del usuario. Con la
    /// extensión delante, ese camino no existe.
    /// </summary>
    public static bool EsAjustable(string path)
        => Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Motivo, en claro, por el que un archivo no se puede ajustar (o "" si sí se puede).</summary>
    public const string MotivoNoAjustable = "solo se puede ajustar el volumen de los MP3 sin recodificar";

    /// <summary>Pasos necesarios para acercarse a <paramref name="db"/> (redondeando al más cercano).</summary>
    public static int StepsFor(double db) => (int)Math.Round(db / DbPerStep, MidpointRounding.AwayFromZero);

    /// <summary>Comprueba que el archivo se puede procesar y cuenta sus tramas, sin modificar nada.</summary>
    public static Mp3GainResult Analyze(string path)
    {
        if (!EsAjustable(path)) return new Mp3GainResult(false, 0, 0, MotivoNoAjustable);
        try
        {
            var bytes = File.ReadAllBytes(path);
            EtiquetaApe.Leer(bytes, out var finAudio);
            var n = Recorrer(bytes, finAudio, 0, out var error, ensayo: true, out _);
            return n > 0
                ? new Mp3GainResult(true, 0, n, "")
                : new Mp3GainResult(false, 0, 0, error.Length > 0 ? error : "no se encontraron tramas MP3");
        }
        catch (Exception e) { return new Mp3GainResult(false, 0, 0, e.Message); }
    }

    /// <summary>
    /// Aplica el cambio de volumen. Escribe primero en un temporal y luego sustituye, para que un
    /// fallo a medias no deje el archivo del usuario a medio escribir.
    ///
    /// También pone al día las etiquetas APE de MP3Gain y ReplayGain, si el archivo las trae. El
    /// deshacer de volumen llama a este mismo método con los pasos inversos, así que las deja
    /// como estaban sin hacer nada aparte.
    /// </summary>
    public static Mp3GainResult Apply(string path, int steps)
    {
        if (!EsAjustable(path)) return new Mp3GainResult(false, 0, 0, MotivoNoAjustable);
        if (steps == 0) return new Mp3GainResult(true, 0, 0, "");
        var tmp = path + ".beattag.tmp";
        try
        {
            var bytes = File.ReadAllBytes(path);

            // Las etiquetas del final (la APEv2 de MP3Gain y el ID3v1) no son audio: el recorrido
            // se para antes de ellas, para no tomar ninguno de sus bytes por una trama.
            var ape = EtiquetaApe.Leer(bytes, out var finAudio);

            // 1) Ensayo: se comprueba que el archivo se recorre entero y CUÁNTAS tramas se saldrían
            //    de rango. Si son demasiadas, el cambio deformaría la canción y no se hace.
            var n = Recorrer(bytes, finAudio, steps, out var error, ensayo: true, out var recortadas);
            if (n <= 0) return new Mp3GainResult(false, 0, 0, error.Length > 0 ? error : "no se encontraron tramas MP3");

            // Un puñado de tramas al límite son silencios digitales (recortarlas no se oye).
            // Si afecta a más del 1 %, es que el cambio pedido es demasiado grande para este archivo.
            if (recortadas > Math.Max(4, n / 100))
                return new Mp3GainResult(false, 0, n,
                    $"el cambio es demasiado grande para este archivo ({recortadas} tramas se saldrían de rango)", recortadas);

            // 2) De verdad.
            n = Recorrer(bytes, finAudio, steps, out error, ensayo: false, out recortadas);

            // 3) Las etiquetas de MP3Gain y ReplayGain describen la ganancia que tenía el audio
            //    ANTES de este cambio. Visto en una biblioteca real: 173 de 200 MP3 traen
            //    REPLAYGAIN_TRACK_GAIN y MP3GAIN_MINMAX escritos por el MP3Gain clásico, y 166
            //    también MP3GAIN_UNDO. Sin ponerlas al día, MP3Gain volvía a corregir lo ya
            //    corregido y su «Deshacer» devolvía el volumen antiguo, no el original.
            var salida = ape != null && ape.AjustarGanancia(steps) ? ape.Sustituir(bytes) : bytes;

            // 4) Temporal y sustitución. Se vuelca a disco ANTES de sustituir: si se va la luz justo
            //    después, NTFS puede haber guardado el renombrado y no los datos, y la canción
            //    quedaría con contenido basura sin nada que la recupere.
            var creado = File.GetCreationTimeUtc(path);
            var atributos = File.GetAttributes(path);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(salida, 0, salida.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            ConservarFechaYAtributos(path, creado, atributos);
            return new Mp3GainResult(true, steps, n, "", recortadas);
        }
        catch (Exception e) { return new Mp3GainResult(false, 0, 0, e.Message); }
        finally
        {
            // Si la sustitución falla (la canción está abierta en Rekordbox, Serato o VirtualDJ, es
            // de solo lectura o el disco se llenó), el temporal no puede quedarse junto a la canción:
            // es una copia entera de ~10 MB que nadie ve, porque «.tmp» no es una extensión de
            // audio. Solo se borra si el original sigue en su sitio, por si acaso fuera la única copia.
            try { if (File.Exists(tmp) && File.Exists(path)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// El archivo nuevo nace con la fecha de creación de hoy. Se le devuelve la del original, que es
    /// la que se ve como «fecha de creación» al ordenar la carpeta, y sus atributos visibles.
    /// Si no se puede, no pasa nada: el audio ya está bien escrito.
    /// </summary>
    private static void ConservarFechaYAtributos(string path, DateTime creadoUtc, FileAttributes antes)
    {
        try { File.SetCreationTimeUtc(path, creadoUtc); } catch { }
        try
        {
            const FileAttributes Conservables = FileAttributes.Hidden | FileAttributes.System | FileAttributes.NotContentIndexed;
            var ahora = File.GetAttributes(path);
            var deseados = (ahora & ~Conservables) | (antes & Conservables);
            if (deseados != ahora) File.SetAttributes(path, deseados);
        }
        catch { }
    }

    /// <summary>Lo que dice la cabecera de una trama MPEG de capa III.</summary>
    private readonly record struct Cabecera(int VersionId, int RateIdx, bool Mpeg1, bool Crc, bool Mono, int Largo);

    /// <summary>
    /// ¿Hay en <paramref name="i"/> una cabecera de trama de capa III con sentido? Solo mira los
    /// cuatro bytes de la cabecera; la confirmación con la trama siguiente la hace quien llama.
    /// </summary>
    private static bool LeerCabecera(byte[] b, int i, out Cabecera c)
    {
        c = default;
        // Sincronismo de trama: 11 bits a 1.
        if (i < 0 || i + 4 > b.Length || b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) return false;

        var versionId = (b[i + 1] >> 3) & 0x03;   // 0=MPEG2.5, 2=MPEG2, 3=MPEG1
        var layer = (b[i + 1] >> 1) & 0x03;       // 1 = Layer III
        var protection = (b[i + 1] & 0x01) == 0;  // 0 => lleva CRC
        var bitrateIdx = (b[i + 2] >> 4) & 0x0F;
        var rateIdx = (b[i + 2] >> 2) & 0x03;
        var padding = (b[i + 2] >> 1) & 0x01;
        var mode = (b[i + 3] >> 6) & 0x03;        // 3 = mono

        if (versionId == 1 || layer != 1 || bitrateIdx is 0 or 15 || rateIdx == 3) return false;

        var mpeg1 = versionId == 3;
        var bitrate = Bitrate(mpeg1, bitrateIdx);
        var sampleRate = SampleRate(versionId, rateIdx);
        if (bitrate == 0 || sampleRate == 0) return false;

        var largo = mpeg1
            ? 144 * bitrate * 1000 / sampleRate + padding
            : 72 * bitrate * 1000 / sampleRate + padding;
        if (largo < 8) return false;

        c = new Cabecera(versionId, rateIdx, mpeg1, protection, mode == 3, largo);
        return true;
    }

    /// <summary>
    /// Tras una cabecera encontrada buscando byte a byte, ¿empieza en <paramref name="j"/> lo que
    /// tendría que venir detrás? Otra trama de la misma versión y frecuencia, el final del audio o
    /// una etiqueta. Es como resincronizan mpg123 y ffmpeg.
    /// </summary>
    private static bool SiguienteCuadra(byte[] b, int j, int finAudio, Cabecera actual)
    {
        if (j == finAudio) return true;
        if (j > finAudio) return false;
        if (LeerCabecera(b, j, out var siguiente))
            return siguiente.VersionId == actual.VersionId && siguiente.RateIdx == actual.RateIdx;
        // Etiquetas del final que no se hubieran reconocido al buscar dónde acaba el audio.
        return EmpiezaPor(b, j, "TAG") || EmpiezaPor(b, j, "APETAGEX") || EmpiezaPor(b, j, "LYRICS");
    }

    private static bool EmpiezaPor(byte[] b, int pos, string marca)
    {
        if (pos < 0 || pos + marca.Length > b.Length) return false;
        for (var k = 0; k < marca.Length; k++)
            if (b[pos + k] != marca[k]) return false;
        return true;
    }

    /// <summary>
    /// Recorre las tramas del MP3 sumando <paramref name="steps"/> a cada global_gain, desde el
    /// principio del audio hasta <paramref name="finAudio"/> (donde empiezan las etiquetas del
    /// final). Devuelve cuántas tramas se procesaron; <paramref name="error"/> se rellena si alguna
    /// se saldría de rango (solo en modo ensayo).
    /// </summary>
    private static int Recorrer(byte[] b, int finAudio, int steps, out string error, bool ensayo, out int recortadas)
    {
        error = "";
        recortadas = 0;
        var i = SaltarId3(b);
        var tramas = 0;

        // Dónde tendría que empezar la siguiente trama si se va en sincronía. Al principio no se
        // sabe, así que también la primera cabecera necesita confirmación.
        var esperada = -1;

        while (i + 4 <= finAudio)
        {
            if (!LeerCabecera(b, i, out var cab)) { i++; continue; }

            // Cabecera encontrada buscando byte a byte (al principio, o tras una trama dañada o un
            // segundo ID3v2 con la carátula dentro): un 0xFF 0xFB cualquiera la imita. Solo se acepta
            // si detrás viene otra trama coherente; si no, se escribiría «steps» en un byte de la
            // carátula o de los datos de audio, y eso es un chasquido o una imagen rota.
            if (i != esperada && !SiguienteCuadra(b, i + cab.Largo, finAudio, cab)) { i++; continue; }

            var largo = cab.Largo;
            if (i + largo > finAudio) break;

            // El "side info" empieza tras la cabecera (y tras el CRC si lo hay).
            var si = i + 4 + (cab.Crc ? 2 : 0);
            var mpeg1 = cab.Mpeg1;
            var mono = cab.Mono;
            var canales = mono ? 1 : 2;
            var granulos = mpeg1 ? 2 : 1;

            // La PRIMERA trama suele ser la cabecera Xing/Info/VBRI: son metadatos (duración,
            // índice de búsqueda), no audio, y va toda a cero. No se toca ni cuenta como problema.
            var sideLen = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
            if (EsTramaDeMetadatos(b, i, si, sideLen)) { i += largo; esperada = i; continue; }

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
            esperada = i;
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

    /// <summary>
    /// La etiqueta APEv2 que el MP3Gain clásico deja al final del MP3, delante del ID3v1. En una
    /// biblioteca real va así: cabecera, MP3GAIN_MINMAX «000,208», MP3GAIN_UNDO «+002,+002,N»,
    /// REPLAYGAIN_TRACK_GAIN «-5.520000 dB», REPLAYGAIN_TRACK_PEAK «0.738516», luego los datos de
    /// la canción (Artist, Title, BPM…) y el pie.
    ///
    /// Se ponen al día los valores igual que lo hace MP3Gain cuando él mismo cambia la ganancia:
    ///   REPLAYGAIN_TRACK/ALBUM_GAIN   −= pasos · 1,505 dB
    ///   REPLAYGAIN_TRACK/ALBUM_PEAK   ×= 2^(pasos/4)
    ///   MP3GAIN_MINMAX y _ALBUM_MINMAX += pasos en el mínimo y el máximo (entre 0 y 255)
    ///   MP3GAIN_UNDO                  −= pasos en los dos canales: guarda lo que hay que aplicar
    ///                                 para volver al original, así que si se baja, el deshacer
    ///                                 sube. Al llegar a cero se quita, como hace MP3Gain.
    /// MEDIDO en la biblioteca (seis MP3 de E:\Musica\COMERCIAL\Afro):
    ///   · Los REPLAYGAIN_TRACK_GAIN que dejó MP3Gain tras aplicar 1, 2 o 3 pasos («-6.395000»,
    ///     «-5.520000», «-5.345000») salen de una medida de dos decimales más 1,505 dB por paso. Con
    ///     5·log10(2) = 1,50515 dB habrían quedado cifras como «-5.519700».
    ///   · El UNDO resta: con «+002» y «-5.520000 dB», la medida original era -8,53 dB (bajado 2
    ///     pasos), y así con los seis (-7,90, -8,11, -9,86, -9,80…), lo normal en música comercial
    ///     actual. Si sumara, saldrían -2,51 o -0,83 dB, imposibles en temas de hoy.
    ///
    /// Si el archivo no trae etiqueta APE, no se crea ninguna. Todo lo demás de la etiqueta (otros
    /// elementos, su orden, cabecera y pie) se reescribe byte a byte como estaba.
    /// </summary>
    private sealed class EtiquetaApe
    {
        private const uint TieneCabecera = 0x80000000;
        private const uint EsCabecera = 0x20000000;
        private const int Bloque = 32;

        /// <summary>Lo que mueve MP3Gain las ganancias de ReplayGain por cada paso.</summary>
        private const double DbPorPasoMp3Gain = 1.505;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class Elemento
        {
            public required byte[] Clave { get; init; }
            public required uint Flags { get; init; }
            public required byte[] Valor { get; set; }

            public string Nombre => Encoding.ASCII.GetString(Clave).ToUpperInvariant();

            /// <summary>Elemento de texto (UTF-8). Los binarios y los enlaces no se tocan.</summary>
            public bool EsTexto => ((Flags >> 1) & 0x03) == 0;

            public string Texto => Encoding.UTF8.GetString(Valor);
        }

        private readonly int _inicio;          // primer byte de la etiqueta (su cabecera, si la lleva)
        private readonly int _fin;             // byte siguiente al pie: el ID3v1, o el final del archivo
        private readonly byte[]? _cabecera;    // los 32 bytes tal cual estaban
        private readonly byte[] _pie;
        private readonly List<Elemento> _elementos;

        private EtiquetaApe(int inicio, int fin, byte[]? cabecera, byte[] pie, List<Elemento> elementos)
        {
            _inicio = inicio; _fin = fin; _cabecera = cabecera; _pie = pie; _elementos = elementos;
        }

        /// <summary>
        /// Busca la etiqueta APEv2 al final (tras el audio, delante del ID3v1 si lo hay).
        /// <paramref name="finAudio"/> sale con el byte donde acaba el audio: el inicio de la APE,
        /// o el del ID3v1, o el final del archivo. Devuelve null si no hay APE o no se entiende
        /// entera; en ese caso no se toca.
        /// </summary>
        public static EtiquetaApe? Leer(byte[] b, out int finAudio)
        {
            var fin = b.Length;
            if (fin >= 128 && b[fin - 128] == 'T' && b[fin - 127] == 'A' && b[fin - 126] == 'G') fin -= 128;
            finAudio = fin;
            if (fin < Bloque || !EmpiezaPor(b, fin - Bloque, "APETAGEX")) return null;

            var pie = fin - Bloque;
            var tam = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pie + 12));     // elementos + pie
            var cuantos = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pie + 16));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pie + 20));
            if ((flags & EsCabecera) != 0 || tam < Bloque || tam > (uint)fin) return null;

            var primero = fin - (int)tam;
            var conCabecera = (flags & TieneCabecera) != 0;
            var inicio = conCabecera ? primero - Bloque : primero;
            if (inicio < 0 || (conCabecera && !EmpiezaPor(b, inicio, "APETAGEX"))) return null;
            finAudio = inicio;

            var elementos = new List<Elemento>();
            var p = primero;
            for (var k = 0u; k < cuantos; k++)
            {
                if (p + 8 > pie) return null;
                var largo = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
                var fl = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
                var finClave = Array.IndexOf(b, (byte)0, p + 8, pie - (p + 8));
                if (finClave <= p + 8 || largo > (uint)(pie - finClave - 1)) return null;
                elementos.Add(new Elemento
                {
                    Clave = b[(p + 8)..finClave],
                    Flags = fl,
                    Valor = b[(finClave + 1)..(finClave + 1 + (int)largo)],
                });
                p = finClave + 1 + (int)largo;
            }
            if (p != pie) return null;   // sobran o faltan bytes: algo no cuadra y mejor no tocarla

            return new EtiquetaApe(inicio, fin, conCabecera ? b[inicio..(inicio + Bloque)] : null,
                                   b[pie..fin], elementos);
        }

        /// <summary>
        /// Pone al día las etiquetas de ganancia tras subir o bajar <paramref name="pasos"/>.
        /// Devuelve true si cambió algo y hay que reescribir la etiqueta.
        /// </summary>
        public bool AjustarGanancia(int pasos)
        {
            var cambio = false;
            var deMp3Gain = false;
            Elemento? deshacer = null;
            Elemento? minMax = null;

            foreach (var e in _elementos)
            {
                if (!e.EsTexto) continue;
                var nombre = e.Nombre;
                if (nombre.StartsWith("MP3GAIN_", StringComparison.Ordinal)) deMp3Gain = true;
                if (nombre == "MP3GAIN_UNDO") { deshacer = e; continue; }
                if (nombre == "MP3GAIN_MINMAX") minMax = e;

                var nuevo = nombre switch
                {
                    "REPLAYGAIN_TRACK_GAIN" or "REPLAYGAIN_ALBUM_GAIN" => NuevaGanancia(e.Texto, pasos),
                    "REPLAYGAIN_TRACK_PEAK" or "REPLAYGAIN_ALBUM_PEAK" => NuevoPico(e.Texto, pasos),
                    "MP3GAIN_MINMAX" or "MP3GAIN_ALBUM_MINMAX" => NuevoMinMax(e.Texto, pasos),
                    _ => null,
                };
                // Un valor que no se entiende se deja como está: mejor viejo que inventado.
                if (nuevo != null && nuevo != e.Texto) { e.Valor = Encoding.UTF8.GetBytes(nuevo); cambio = true; }
            }

            // El deshacer de MP3Gain solo se lleva en etiquetas que ya son suyas. A una etiqueta que
            // solo trae ReplayGain (la de foobar2000, por ejemplo) no se le añade nada de MP3Gain.
            if (!deMp3Gain) return cambio;

            int izq = 0, der = 0;
            var modo = 'N';
            if (deshacer != null && !LeerDeshacer(deshacer.Texto, out izq, out der, out modo)) return cambio;
            izq -= pasos;
            der -= pasos;

            if (izq == 0 && der == 0)
            {
                // Vuelve a estar como lo dejó el original: MP3Gain quita el elemento.
                if (deshacer != null) { _elementos.Remove(deshacer); cambio = true; }
                return cambio;
            }

            var texto = Encoding.UTF8.GetBytes(
                $"{izq.ToString("+000;-000;+000", Inv)},{der.ToString("+000;-000;+000", Inv)},{modo}");
            if (deshacer != null) deshacer.Valor = texto;
            else
            {
                // Mismo orden que escribe MP3Gain: justo detrás de MP3GAIN_MINMAX.
                var donde = minMax != null ? _elementos.IndexOf(minMax) + 1 : 0;
                _elementos.Insert(donde, new Elemento { Clave = Encoding.ASCII.GetBytes("MP3GAIN_UNDO"), Flags = 0, Valor = texto });
            }
            return true;
        }

        /// <summary>«-5.520000 dB» → 2 pasos más abajo → «-2.510000 dB» (el formato de MP3Gain).</summary>
        private static string? NuevaGanancia(string texto, int pasos)
        {
            var t = texto.Trim();
            if (t.EndsWith("dB", StringComparison.OrdinalIgnoreCase)) t = t[..^2].TrimEnd();
            if (!double.TryParse(t, NumberStyles.Float, Inv, out var db)) return null;
            db -= pasos * DbPorPasoMp3Gain;
            return db.ToString("+0.000000;-0.000000;+0.000000", Inv) + " dB";
        }

        /// <summary>El pico sube o baja con el audio: 2^(1/4) por paso, «0.738516».</summary>
        private static string? NuevoPico(string texto, int pasos)
        {
            if (!double.TryParse(texto.Trim(), NumberStyles.Float, Inv, out var pico) || pico < 0) return null;
            pico *= Math.Pow(2.0, pasos / 4.0);
            return pico.ToString("0.000000", Inv);
        }

        /// <summary>«105,209» → 2 pasos más abajo → «103,207». global_gain no sale de 0…255.</summary>
        private static string? NuevoMinMax(string texto, int pasos)
        {
            var partes = texto.Split(',');
            if (partes.Length != 2
                || !int.TryParse(partes[0].Trim(), NumberStyles.Integer, Inv, out var min)
                || !int.TryParse(partes[1].Trim(), NumberStyles.Integer, Inv, out var max)) return null;
            min = Math.Clamp(min + pasos, 0, 255);
            max = Math.Clamp(max + pasos, 0, 255);
            return $"{min.ToString("000", Inv)},{max.ToString("000", Inv)}";
        }

        /// <summary>«+002,+002,N»: pasos para volver al original en cada canal y si se aplicó «envolviendo».</summary>
        private static bool LeerDeshacer(string texto, out int izq, out int der, out char modo)
        {
            izq = der = 0;
            modo = 'N';
            var partes = texto.Split(',');
            if (partes.Length < 2
                || !int.TryParse(partes[0].Trim(), NumberStyles.Integer, Inv, out izq)
                || !int.TryParse(partes[1].Trim(), NumberStyles.Integer, Inv, out der)) return false;
            if (partes.Length > 2 && partes[2].Trim().Length > 0) modo = partes[2].Trim()[0];
            return true;
        }

        /// <summary>El archivo con esta etiqueta en lugar de la que tenía. El audio y el ID3v1, igual.</summary>
        public byte[] Sustituir(byte[] b)
        {
            var etiqueta = Escribir();
            var cola = b.Length - _fin;
            var r = new byte[_inicio + etiqueta.Length + cola];
            Buffer.BlockCopy(b, 0, r, 0, _inicio);
            Buffer.BlockCopy(etiqueta, 0, r, _inicio, etiqueta.Length);
            Buffer.BlockCopy(b, _fin, r, _inicio + etiqueta.Length, cola);
            return r;
        }

        private byte[] Escribir()
        {
            using var cuerpo = new MemoryStream();
            Span<byte> num = stackalloc byte[4];
            foreach (var e in _elementos)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(num, (uint)e.Valor.Length); cuerpo.Write(num);
                BinaryPrimitives.WriteUInt32LittleEndian(num, e.Flags); cuerpo.Write(num);
                cuerpo.Write(e.Clave);
                cuerpo.WriteByte(0);
                cuerpo.Write(e.Valor);
            }

            var tam = (uint)(cuerpo.Length + Bloque);
            using var salida = new MemoryStream();
            if (_cabecera != null) salida.Write(ConTamaño(_cabecera, tam));
            cuerpo.Position = 0;
            cuerpo.CopyTo(salida);
            salida.Write(ConTamaño(_pie, tam));
            return salida.ToArray();
        }

        /// <summary>Cabecera o pie tal como estaban, con el tamaño y el número de elementos al día.</summary>
        private byte[] ConTamaño(byte[] original, uint tam)
        {
            var r = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), tam);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(16), (uint)_elementos.Count);
            return r;
        }
    }
}
