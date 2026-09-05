using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NLayer.NAudioSupport;

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
/// Reparto:
///   Windows        AudioFileReader, el códec del sistema. IGUAL QUE SIEMPRE, no se toca.
///   macOS  .mp3    NLayer, en C# puro.
///   macOS  .wav    WaveFileReader de NAudio, que ya era código gestionado.
///   macOS  resto   no soportado, y se dice claramente.
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
        if (OperatingSystem.IsWindows())
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
    /// Este camino es el mismo en los dos sistemas, y a diferencia de <see cref="Abrir"/> eso no es
    /// un problema: aquí no se MIDE nada. Lo que sale de aquí se reproduce o se manda a identificar,
    /// y para ambas cosas da igual qué decodificador lo produjo.
    /// </summary>
    public static WaveStream AbrirComoWaveStream(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".mp3")
            // El decodificador gestionado va como parámetro: sin él, NAudio buscaría el códec del
            // sistema y en macOS no hay ninguno.
            return new Mp3FileReaderBase(path, wf => new Mp3FrameDecompressor(wf));

        if (ext == ".wav") return new WaveFileReader(path);

        throw new NotSupportedException($"Fuera de Windows solo se pueden leer MP3 y WAV (este es {ext}).");
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
