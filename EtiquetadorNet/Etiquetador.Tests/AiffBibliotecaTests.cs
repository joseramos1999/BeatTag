using System.Text;
using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// M15: los AIFF entran en la biblioteca. Que se leen, se etiquetan, se renombran y se deshacen ya lo
/// prueba LibraryScannerTests; aquí, lo que hacen con ellos el resto de piezas: Volumen los mide y no
/// los ajusta, la vista previa y «Comprobar audio» los abren (Media Foundation no sabe), y quien ya
/// tenía la caché de escaneo no tiene que releer la biblioteca para que aparezcan.
/// </summary>
public class AiffBibliotecaTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>
    /// Un AIFF con un seno de 440 Hz con pico a −20 dBFS en los dos canales: con silencio no hay nada
    /// que medir.
    /// </summary>
    private static void WriteSineAiff(string path, int segundos = 3)
    {
        const int canales = 2, bits = 16, frecuencia = 44100;
        var marcos = frecuencia * segundos;
        var datos = marcos * canales * (bits / 8);
        using var ms = new MemoryStream();
        void Texto(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void U32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        Texto("FORM"); U32(4 + (8 + 18) + (8 + 8 + datos)); Texto("AIFF");
        Texto("COMM"); U32(18); U16(canales); U32(marcos); U16(bits);
        ms.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 });   // 44100 en coma flotante extendida
        Texto("SSND"); U32(8 + datos); U32(0); U32(0);
        for (var i = 0; i < marcos; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 440 * i / frecuencia) * 3277);   // 0,1 de fondo de escala
            for (var c = 0; c < canales; c++) U16(v);
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    // Volumen solo ajusta MP3: el recorrido de tramas podría dar con una secuencia que imite la marca
    // de sincronismo dentro del audio de un AIFF y reescribir encima. Con un archivo de verdad.
    [Theory]
    [InlineData("tema.aiff")]
    [InlineData("tema.aif")]
    public void El_ajuste_de_volumen_no_toca_un_aiff(string nombre)
    {
        var ruta = Path.Combine(_dir, nombre);
        WriteSineAiff(ruta, segundos: 1);
        var antes = File.ReadAllBytes(ruta);

        var r = Mp3Gain.Apply(ruta, 2);

        Assert.False(r.Ok);
        Assert.Equal(Mp3Gain.MotivoNoAjustable, r.Error);
        Assert.Equal(antes, File.ReadAllBytes(ruta));
    }

    // Volumen sí los mide. En Windows, con el mismo decodificador de siempre (AudioFileReader, que
    // trae lector de AIFF); el valor esperado es el de un seno a −20 dBFS en estéreo.
    [WindowsFact]
    public void Volumen_mide_la_sonoridad_de_un_aiff()
    {
        foreach (var nombre in new[] { "tema.aiff", "tema.aif" })
        {
            var ruta = Path.Combine(_dir, nombre);
            WriteSineAiff(ruta);
            var r = new LoudnessScanner(Path.Combine(_dir, "sonoridad.json")).Measure(ruta, force: true);

            Assert.NotNull(r);
            Assert.InRange(r!.Value.Lufs, -22.0, -19.0);
            Assert.InRange(r.Value.PeakDb, -20.5, -19.5);
        }
    }

    // La vista previa en macOS y «Comprobar audio» en los dos sistemas recortan con
    // AbrirComoWaveStream. Antes, en Windows, iba a Media Foundation, que no abre AIFF (MEDIDO:
    // 0xC00D36C4), y el aviso decía que el archivo estaba dañado o faltaba el códec.
    [Theory]
    [InlineData("tema.aiff")]
    [InlineData("TEMA.AIF")]
    public void Se_abre_para_escuchar_y_recortar_en_cualquier_sistema(string nombre)
    {
        var ruta = Path.Combine(_dir, nombre);
        WriteSineAiff(ruta);
        Assert.True(AudioSamples.EsAiff(ruta));

        using (var s = AudioSamples.AbrirComoWaveStream(ruta))
            Assert.InRange(s.TotalTime.TotalSeconds, 2.9, 3.1);

        var recorte = AudioSamples.EscribirRecorteWav(ruta, 0.25, 1);
        try
        {
            using var wav = new NAudio.Wave.WaveFileReader(recorte.Ruta);
            Assert.InRange(wav.TotalTime.TotalSeconds, 0.9, 1.05);
            Assert.InRange(recorte.SegundosOriginal, 2.9, 3.1);
        }
        finally { try { File.Delete(recorte.Ruta); } catch { } }
    }

    [Theory]
    [InlineData("tema.mp3", false)]
    [InlineData("tema.wav", false)]
    [InlineData("tema.aifc", false)]   // AIFF comprimido: no lo lee el lector de NAudio
    [InlineData("tema.aif", true)]
    public void Solo_los_aiff_van_por_el_lector_de_aiff(string nombre, bool esperado)
        => Assert.Equal(esperado, AudioSamples.EsAiff(nombre));

    // Quien ya tenía la caché de escaneo de una versión anterior no relee la biblioteca: lo que no ha
    // cambiado sale de la caché (aquí, con un título que solo está en ella) y solo se leen los AIFF.
    [Fact]
    public async Task Los_aiff_aparecen_sin_releer_lo_que_ya_estaba_en_la_cache()
    {
        var musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        var mp3 = Path.Combine(musica, "Fisher - Losing It.mp3");
        var aiff = Path.Combine(musica, "Fisher - Stop It.aiff");
        Mp3Fixture.WriteMinMp3(mp3);
        LibraryScannerTests.WriteMinAiff(aiff);
        using (var f = TagLib.File.Create(aiff)) { f.Tag.Title = "Stop It"; f.Save(); }

        // La caché como la dejó una versión que no conocía los AIFF: solo el MP3.
        var fi = new FileInfo(mp3);
        var cacheFile = Path.Combine(_dir, "scan-cache.json");
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(new Dictionary<string, ScanCache.Entry>
        {
            [mp3] = new() { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length, Ti = "Título que solo está en la caché" },
        }));

        var store = new LibraryStore(new AppConfig { Folders = new() { musica } }, () => { }, new ScanCache(cacheFile));
        await store.ScanAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, store.Tracks.Count);
        Assert.Equal("Título que solo está en la caché", store.Tracks.Single(t => t.FilePath == mp3).Title);
        Assert.Equal("Stop It", store.Tracks.Single(t => t.FilePath == aiff).Title);
    }
}
