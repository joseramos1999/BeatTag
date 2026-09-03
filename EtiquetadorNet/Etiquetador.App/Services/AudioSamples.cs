using System;
using System.IO;
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
    /// Abre el archivo como <see cref="WaveStream"/>: bytes PCM con posición (CurrentTime, Read).
    /// Solo fuera de Windows -allí el camino sigue siendo el códec del sistema-, para lo que
    /// necesite saltar a un punto del archivo, como la vista previa que empieza al 25 % del tema.
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
