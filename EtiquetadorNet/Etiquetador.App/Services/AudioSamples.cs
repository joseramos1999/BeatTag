using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NLayer.NAudioSupport;
using Etiquetador.Core;

namespace Etiquetador.App.Services;

/// <summary>
/// Abre un archivo de audio en cualquier sistema, para lo que haga falta con él: medir su
/// sonoridad (muestras float) o recortar un fragmento para reproducirlo (bytes PCM con posición).
///
/// El problema que resuelve: NAudio decodifica MP3 apoyándose en códecs de Windows (ACM/DMO y
/// MediaFoundation). Eso compila en macOS y revienta al ejecutarse, así que ni medir el volumen ni
/// escuchar un fragmento funcionarían allí.
///
/// La salida elegida es NLayer, un decodificador de MP3 escrito en C# puro que se enchufa a NAudio.
/// Frente a llamar a ffmpeg como proceso externo -que era la idea inicial- tiene dos ventajas que
/// pesan más que su menor cobertura de formatos: no hay nada que instalar (ffmpeg son unos 80 MB y
/// una instalación aparte en cada equipo), y la biblioteca a la que sirve esto es MP3 en su
/// práctica totalidad: 14.653 MP3 y 21 WAV en la del autor.
///
/// Reparto para medir (<see cref="Abrir"/>):
///   Windows        AudioFileReader, el códec del sistema. IGUAL QUE SIEMPRE, no se toca.
///   macOS  .mp3    NLayer, en C# puro.
///   macOS  .wav    WaveFileReader de NAudio, que ya era código gestionado.
///   los dos .aif(f) AiffFileReader de NAudio, también gestionado, y el lector propio para los AIFF-C
///                  de macOS (M15; ver <see cref="AbrirAiff"/>).
///   macOS  resto   no soportado, y se dice claramente.
/// Para recortar (<see cref="AbrirComoWaveStream"/>), MP3, WAV y AIFF igual en los dos sistemas, y el
/// resto, en Windows, con Media Foundation.
///
/// No se pierde nada respecto a antes: en macOS no funcionaba NADA, y en Windows sigue funcionando
/// exactamente lo mismo, con el mismo decodificador y por tanto los mismos números. Verificado no
/// solo por lectura: comparado contra 60 MP3 reales de la biblioteca del autor, las medidas salen
/// bit a bit idénticas a las de antes del cambio.
/// </summary>
public static class AudioSamples
{
    /// <summary>
    /// Abre el archivo como muestras float de -1 a 1, para medir su sonoridad. Devuelve un recurso
    /// que hay que liberar. Lanza excepción si el formato no se puede decodificar en este sistema;
    /// quien llama ya la trata.
    /// </summary>
    public static ISampleProvider Abrir(string path, out IDisposable recurso)
    {
        // En Windows NO se cambia nada. El decodificador del sistema es el que produjo las medidas
        // que el usuario ya tiene guardadas, y el margen de seguridad del ajuste de volumen está
        // calibrado contra ellas. Medido: entre los dos decodificadores el LUFS coincide (peor
        // diferencia 0,007 en 25 canciones reales), pero el pico puede moverse medio decibelio, y
        // eso basta para cambiar una decisión al filo. Esto es un port: en Windows, todo igual.
        // El AIFF va por el lector propio también en Windows (M15): AudioFileReader lo abre con el
        // mismo AiffFileReader de NAudio, pero rechaza los AIFF-C de macOS con un mensaje en inglés.
        // Para un AIFF sin comprimir las muestras salen idénticas por los dos caminos.
        if (OperatingSystem.IsWindows() && !EsAiff(path))
        {
            var win = new AudioFileReader(path);
            recurso = win;
            return win;
        }

        var stream = AbrirComoWaveStream(path);
        recurso = stream;
        return Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            ? new Recortado(stream.ToSampleProvider())
            : stream.ToSampleProvider();
    }

    /// <summary>
    /// Abre el archivo como <see cref="WaveStream"/>: bytes PCM con posición (CurrentTime, Read),
    /// para lo que necesite saltar a un punto del archivo en vez de leerlo entero.
    ///
    /// MP3 y WAV van por el mismo camino en los dos sistemas, y a diferencia de <see cref="Abrir"/>
    /// eso no es un problema: aquí no se MIDE nada. Lo que sale de aquí se reproduce o se manda a
    /// identificar, y para ambas cosas da igual qué decodificador lo produjo.
    ///
    /// El resto de formatos (FLAC, M4A, AAC, WMA…) se abren en Windows con el decodificador del
    /// sistema, igual que ya hacía la vista previa. Antes se rechazaban en TODOS los sistemas con
    /// «Fuera de Windows solo se pueden leer MP3 y WAV», también en Windows: «Comprobar audio» daba
    /// por fallida cada canción de una biblioteca en FLAC o M4A, y con el motor de pago activo cada
    /// una se contaba como consulta de pago aunque no se enviara nada.
    /// </summary>
    public static WaveStream AbrirComoWaveStream(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".mp3")
            // El decodificador gestionado va como parámetro: sin él, NAudio buscaría el códec del
            // sistema y en macOS no hay ninguno.
            return new Mp3FileReaderBase(path, wf => new Mp3FrameDecompressor(wf));

        if (ext == ".wav") return new WaveFileReader(path);

        // AIFF (M15, equipo catalogo_bandeja): el lector de NAudio es código gestionado y vale en los
        // dos sistemas. Media Foundation NO lo abre en Windows. MEDIDO con un AIFF de 16 bits:
        // 0xC00D36C4 («no se admite el tipo de secuencia de bytes»), y Comprobar audio lo daba por
        // «dañado o falta el códec».
        if (EsAiff(path)) return AbrirAiff(path);

        if (OperatingSystem.IsWindows()) return AbrirConMediaFoundation(path, ext);

        throw new NotSupportedException($"En este sistema solo se pueden leer archivos MP3, WAV y AIFF (este es {ext}).");
    }

    /// <summary>¿Es un AIFF (.aif o .aiff)? Se abre con el lector de NAudio en los dos sistemas (M15).</summary>
    internal static bool EsAiff(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".aif" or ".aiff";

    // ---- AIFF-C (revisión de M15, equipo catalogo_bandeja) ----
    //
    // El AiffFileReader de NAudio solo admite el AIFF clásico y el AIFF-C sin compresión («NONE»).
    // Con cualquier otro tipo lanza «Compressed AIFC is not supported.», en inglés, y ese texto llegaba
    // tal cual a «Comprobar audio» y a la vista previa. Los .aif que exporta macOS (QuickTime, Música,
    // Core Audio) suelen ser AIFF-C «sowt»: no es compresión, es el mismo PCM con los bytes en el orden
    // de los procesadores Intel. Se leen aquí, con los otros tipos sin pérdida de Apple; el resto
    // (ulaw, ima4…, compresión con pérdida, raros en música para DJ) se rechaza con un texto claro.

    /// <summary>
    /// Lee la cabecera de un AIFF o AIFF-C, o null si no lo es o le falta el COMM o el SSND. La lectura
    /// está en Core (<see cref="LectorAiff"/>) porque el escáner también la usa: TagLib no abre los AIFF-C.
    /// </summary>
    internal static CabeceraAiff? LeerCabeceraAiff(Stream s) => LectorAiff.LeerCabecera(s);

    /// <summary>
    /// Abre un AIFF. El clásico y el AIFF-C sin compresión, con el lector de NAudio; los AIFF-C sin
    /// pérdida de Apple («sowt», «twos», «in24», «in32», «fl32»), con <see cref="LectorAifc"/>. Lo que
    /// no se puede leer se dice en un texto claro, como con Media Foundation.
    /// </summary>
    internal static WaveStream AbrirAiff(string path)
    {
        CabeceraAiff? cabecera;
        using (var fs = File.OpenRead(path)) cabecera = LeerCabeceraAiff(fs);

        if (cabecera is { EsAifc: true } c && !c.Compresion.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            var formato = FormatoAifc(c);
            if (formato == null) throw new NotSupportedException(TextoAifcNoAdmitido(c.Compresion));
            return new LectorAifc(path, c, formato.Value.Formato, formato.Value.Invertir);
        }

        try { return new AiffFileReader(path); }
        catch (Exception e) when (e is not FileNotFoundException and not DirectoryNotFoundException and not UnauthorizedAccessException)
        {
            throw new NotSupportedException(
                $"No se ha podido leer este archivo AIFF: está dañado o su formato no se admite ({e.Message.Trim()}).", e);
        }
    }

    /// <summary>
    /// El formato de las muestras de un AIFF-C que se sabe leer, y si hay que darles la vuelta a los
    /// bytes (AIFF guarda big-endian; WAV y NAudio esperan little-endian). Null si no se sabe.
    /// </summary>
    internal static (WaveFormat Formato, bool Invertir)? FormatoAifc(CabeceraAiff c)
    {
        // El tamaño de cada muestra se redondea al byte, como lo guarda AIFF (20 bits ocupan 3 bytes,
        // alineados a la izquierda, así que se pueden leer como 24).
        var bits = (c.Bits + 7) / 8 * 8;
        switch (c.Compresion)
        {
            case "sowt" when bits is 8 or 16 or 24 or 32:
                return (new WaveFormat(c.Frecuencia, bits, c.Canales), false);
            case "twos" or "in24" or "in32" when bits is 8 or 16 or 24 or 32:
                return (new WaveFormat(c.Frecuencia, bits, c.Canales), true);
            case "fl32" or "FL32" when bits == 32:
                return (WaveFormat.CreateIeeeFloatWaveFormat(c.Frecuencia, c.Canales), true);
            default:
                return null;
        }
    }

    internal static string TextoAifcNoAdmitido(string compresion)
        => $"Este AIFF está comprimido con «{compresion.Trim()}», un tipo que no se puede leer. Se leen el AIFF sin comprimir y "
         + "los AIFF-C sin pérdida de Apple; conviértelo a AIFF o WAV sin comprimir con tu programa de audio.";

    /// <summary>
    /// Lee las muestras de un AIFF-C sin pérdida directamente del bloque SSND, dando la vuelta a los
    /// bytes cuando hace falta. Admite saltar a cualquier punto (la vista previa empieza al 25 %).
    /// </summary>
    internal sealed class LectorAifc : WaveStream
    {
        private readonly FileStream _fs;
        private readonly long _inicio;
        private readonly long _longitud;
        private readonly int _bytesMuestra;
        private readonly bool _invertir;
        private readonly bool _ochoBits;
        private long _posicion;

        public LectorAifc(string path, CabeceraAiff c, WaveFormat formato, bool invertir)
        {
            _fs = File.OpenRead(path);
            WaveFormat = formato;
            _inicio = c.InicioDatos;
            _bytesMuestra = Math.Max(1, formato.BitsPerSample / 8);
            // Solo muestras enteras y tramas completas: un archivo cortado no puede desalinear los canales.
            _longitud = c.LongitudDatos - c.LongitudDatos % Math.Max(1, formato.BlockAlign);
            _invertir = invertir && _bytesMuestra > 1;
            // AIFF guarda 8 bits con signo y WAV sin signo.
            _ochoBits = formato.Encoding == WaveFormatEncoding.Pcm && formato.BitsPerSample == 8;
        }

        public override WaveFormat WaveFormat { get; }
        public override long Length => _longitud;

        public override long Position
        {
            get => _posicion;
            set
            {
                var v = Math.Clamp(value, 0, _longitud);
                _posicion = v - v % Math.Max(1, WaveFormat.BlockAlign);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count - count % _bytesMuestra, _longitud - _posicion);
            if (n <= 0) return 0;
            _fs.Position = _inicio + _posicion;
            var leidos = 0;
            while (leidos < n)
            {
                var r = _fs.Read(buffer, offset + leidos, n - leidos);
                if (r == 0) break;
                leidos += r;
            }
            leidos -= leidos % _bytesMuestra;
            if (_invertir)
                for (var i = offset; i < offset + leidos; i += _bytesMuestra)
                    Array.Reverse(buffer, i, _bytesMuestra);
            if (_ochoBits)
                for (var i = offset; i < offset + leidos; i++) buffer[i] ^= 0x80;
            _posicion += leidos;
            return leidos;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fs.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Lo que se le da al reproductor de Windows (WaveOut). Un AIFF de 24 o 32 bits, o en coma
    /// flotante, se pasa a 16 bits: WaveOut no garantiza esos formatos con todos los controladores,
    /// y para escuchar si es la canción buena sobra con 16. Lo demás, tal cual.
    /// </summary>
    internal static IWaveProvider ParaReproducir(WaveStream s)
    {
        var f = s.WaveFormat;
        var tal = f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample <= 16;
        return tal ? s : new NAudio.Wave.SampleProviders.SampleToWaveProvider16(s.ToSampleProvider());
    }

    /// <summary>
    /// El decodificador del sistema de Windows (Media Foundation), con un mensaje que se entienda
    /// si no sabe abrir el archivo: el error de origen es un código COM que no dice nada.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WaveStream AbrirConMediaFoundation(string path, string ext)
    {
        try { return new MediaFoundationReader(path); }
        catch (Exception e) when (e is not FileNotFoundException and not DirectoryNotFoundException and not UnauthorizedAccessException)
        {
            throw new NotSupportedException(
                $"Windows no ha podido decodificar este archivo {ext}: está dañado o falta el códec en este equipo ({e.Message.Trim()}).", e);
        }
    }

    /// <summary>
    /// Un recorte ya escrito en disco, con la duración del tema del que salió.
    ///
    /// La duración del ORIGINAL va aquí porque quien identifica la necesita y ya no la tiene: al
    /// recibir solo la ruta de un WAV de 120 segundos, no hay forma de saber que venía de un tema
    /// de cinco minutos. Y AcoustID espera exactamente eso —comprobado ejecutando fpcalc: el campo
    /// «duration» que emite es siempre el del archivo entero, aunque solo analice los primeros
    /// segundos—, así que mandar la del recorte sería describir mal lo que se envía.
    /// </summary>
    public readonly record struct Recorte(string Ruta, double SegundosOriginal);

    /// <summary>
    /// Escribe un fragmento del audio a un WAV temporal. Quien llama es responsable de borrarlo.
    ///
    /// Lo usan dos cosas distintas y por eso vive aquí: la vista previa de macOS, que necesita un
    /// archivo porque «afplay» no sabe empezar a mitad de tema, y la identificación por audio, que
    /// tiene que enviar unos segundos a un servicio. Reunirlo evita que existan dos recortes con
    /// reglas ligeramente distintas.
    ///
    /// <paramref name="segundos"/> a null significa hasta el final del archivo.
    /// </summary>
    public static Recorte EscribirRecorteWav(string path, double inicioFraccion, double? segundos,
                                             CancellationToken ct = default)
    {
        using var stream = AbrirComoWaveStream(path);
        var duracionOriginal = stream.TotalTime.TotalSeconds;
        try { stream.CurrentTime = TimeSpan.FromSeconds(duracionOriginal * inicioFraccion); } catch { }

        // Cuánto se deja escribir. Sin tope se copia el resto del archivo, que es lo que quiere la
        // vista previa; con tope, los bytes exactos de los segundos pedidos.
        var tope = segundos is double s
            ? (long)(s * stream.WaveFormat.AverageBytesPerSecond)
            : long.MaxValue;

        var tmp = Path.Combine(Path.GetTempPath(), "beattag_recorte_" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using var writer = new WaveFileWriter(tmp, stream.WaveFormat);
            var buf = new byte[Math.Max(4096, stream.WaveFormat.AverageBytesPerSecond)];   // ~1s por lectura
            long escritos = 0;
            int n;
            while (escritos < tope && (n = stream.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var cabe = (int)Math.Min(n, tope - escritos);
                writer.Write(buf, 0, cabe);
                escritos += cabe;
            }
        }
        catch
        {
            // Un recorte a medias no le sirve a nadie y encima queda ocupando disco.
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return new Recorte(tmp, duracionOriginal);
    }

    /// <summary>
    /// Recorta las muestras a ±1, como hacía el camino anterior.
    ///
    /// No es un capricho: el decodificador de Windows pasa por 16 bits y recorta ahí; NLayer
    /// entrega el float tal cual, y un MP3 muy alto puede salirse de la escala. Medido sobre 25
    /// canciones reales, el LUFS coincide (peor diferencia 0,067) pero el PICO no: donde el viejo
    /// decía -0,00 dB, el nuevo llegaba a +3,7.
    ///
    /// El pico sin recortar es más fiel a lo que hay dentro del archivo, pero cambiar eso ahora
    /// tendría dos efectos malos: las 6.629 medidas ya guardadas quedarían mezcladas con otras de
    /// significado distinto, y el margen de seguridad del ajuste de volumen está calibrado contra
    /// los números de antes. Esto es un port: tiene que dar lo mismo en Windows y en macOS, no
    /// cambiar lo que se mide. Si algún día interesa el pico real, será una decisión aparte.
    /// </summary>
    private sealed class Recortado : ISampleProvider
    {
        private readonly ISampleProvider _origen;
        public Recortado(ISampleProvider origen) => _origen = origen;
        public WaveFormat WaveFormat => _origen.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var n = _origen.Read(buffer, offset, count);
            for (var i = offset; i < offset + n; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            return n;
        }
    }
}
