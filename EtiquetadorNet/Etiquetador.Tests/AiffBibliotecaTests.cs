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

    // Volumen sí los mide, con el lector de AIFF de NAudio (el mismo que usaría AudioFileReader, pero
    // sin su mensaje en inglés ante un AIFF-C); el valor esperado es el de un seno a −20 dBFS en estéreo.
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

    // ---------------------------------------------------------------- revisión: AIFF-C

    /// <summary>
    /// Un AIFF-C con las muestras tal cual se le dan (ya en el orden de bytes de su tipo), con su FVER
    /// y un nombre de compresión, como los que escribe Core Audio en macOS.
    /// </summary>
    private static void WriteAifc(string path, string tipo, int bits, int canales, byte[] muestras)
    {
        var marcos = muestras.Length / ((bits + 7) / 8 * canales);
        using var cuerpo = new MemoryStream();
        void Texto(string s) => cuerpo.Write(Encoding.ASCII.GetBytes(s));
        void U32(int v) { cuerpo.WriteByte((byte)(v >> 24)); cuerpo.WriteByte((byte)(v >> 16)); cuerpo.WriteByte((byte)(v >> 8)); cuerpo.WriteByte((byte)v); }
        void U16(int v) { cuerpo.WriteByte((byte)(v >> 8)); cuerpo.WriteByte((byte)v); }

        Texto("AIFC");
        Texto("FVER"); U32(4); U32(unchecked((int)0xA2805140));
        Texto("COMM"); U32(18 + 4 + 2); U16(canales); U32(marcos); U16(bits);
        cuerpo.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 });   // 44100
        Texto(tipo); cuerpo.WriteByte(1); cuerpo.WriteByte((byte)'x');           // nombre: «x» (cadena Pascal, par)
        Texto("SSND"); U32(8 + muestras.Length); U32(0); U32(0); cuerpo.Write(muestras);
        if (muestras.Length % 2 == 1) cuerpo.WriteByte(0);

        using var fs = File.Create(path);
        fs.Write(Encoding.ASCII.GetBytes("FORM"));
        var largo = (int)cuerpo.Length;
        fs.Write(new[] { (byte)(largo >> 24), (byte)(largo >> 16), (byte)(largo >> 8), (byte)largo });
        cuerpo.Position = 0;
        cuerpo.CopyTo(fs);
    }

    private static byte[] LeerTodo(NAudio.Wave.WaveStream s)
    {
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    private static byte[] Rampa(int bytes) => Enumerable.Range(0, bytes).Select(i => (byte)(i * 7 + 1)).ToArray();

    // Lo que exporta macOS: «sowt» es el mismo PCM con los bytes en orden Intel. NAudio lo rechazaba
    // con «Compressed AIFC is not supported.», en inglés, en «Comprobar audio» y en la vista previa.
    [Fact]
    public void Un_aifc_sowt_se_lee_tal_cual()
    {
        var ruta = Path.Combine(_dir, "mac.aif");
        var datos = Rampa(44100 * 4);   // un segundo, 16 bits, estéreo
        WriteAifc(ruta, "sowt", 16, 2, datos);

        using var s = AudioSamples.AbrirComoWaveStream(ruta);

        Assert.Equal((44100, 16, 2), (s.WaveFormat.SampleRate, s.WaveFormat.BitsPerSample, s.WaveFormat.Channels));
        Assert.InRange(s.TotalTime.TotalSeconds, 0.99, 1.01);
        Assert.Equal(datos, LeerTodo(s));

        // Se puede saltar a mitad de tema (la vista previa empieza al 25 %), siempre a una trama entera.
        s.Position = 1001;
        Assert.Equal(1000, s.Position);
        var trozo = new byte[8];
        Assert.Equal(8, s.Read(trozo, 0, 8));
        Assert.Equal(datos.Skip(1000).Take(8), trozo);
    }

    // Los otros tipos sin pérdida de Apple guardan big-endian, como el AIFF clásico.
    [Theory]
    [InlineData("twos")]
    [InlineData("in24")]
    public void Un_aifc_big_endian_de_24_bits_se_lee_dando_la_vuelta_a_los_bytes(string tipo)
    {
        var ruta = Path.Combine(_dir, "mac24.aiff");
        WriteAifc(ruta, tipo, 24, 1, new byte[] { 1, 2, 3, 4, 5, 6 });

        using var s = AudioSamples.AbrirComoWaveStream(ruta);

        Assert.Equal(24, s.WaveFormat.BitsPerSample);
        Assert.Equal(new byte[] { 3, 2, 1, 6, 5, 4 }, LeerTodo(s));
    }

    [Fact]
    public void Un_aifc_de_8_bits_pasa_a_muestras_sin_signo()
    {
        var ruta = Path.Combine(_dir, "ocho.aif");
        WriteAifc(ruta, "sowt", 8, 1, new byte[] { 0x00, 0x7F, 0x80 });   // 0, máximo y mínimo con signo
        using var s = AudioSamples.AbrirComoWaveStream(ruta);
        Assert.Equal(new byte[] { 0x80, 0xFF, 0x00 }, LeerTodo(s));         // como los quiere WAV
    }

    [Fact]
    public void Un_aifc_en_coma_flotante_se_lee()
    {
        var ruta = Path.Combine(_dir, "float.aif");
        var medio = BitConverter.GetBytes(0.5f);
        Array.Reverse(medio);   // big-endian
        WriteAifc(ruta, "fl32", 32, 1, medio.Concat(medio).ToArray());

        using var s = AudioSamples.AbrirComoWaveStream(ruta);

        Assert.Equal(NAudio.Wave.WaveFormatEncoding.IeeeFloat, s.WaveFormat.Encoding);
        var muestras = new float[2];
        Assert.Equal(2, NAudio.Wave.WaveExtensionMethods.ToSampleProvider(s).Read(muestras, 0, 2));
        Assert.Equal(new[] { 0.5f, 0.5f }, muestras);
    }

    // Un AIFF-C sin compresión («NONE») sigue yendo por el lector de NAudio.
    [Fact]
    public void Un_aifc_sin_compresion_se_lee_con_el_lector_de_naudio()
    {
        var ruta = Path.Combine(_dir, "none.aif");
        WriteAifc(ruta, "NONE", 16, 1, new byte[] { 0x01, 0x02, 0x03, 0x04 });
        using var s = AudioSamples.AbrirComoWaveStream(ruta);
        Assert.IsType<NAudio.Wave.AiffFileReader>(s);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x04, 0x03 }, LeerTodo(s));
    }

    // Lo que no se puede leer se dice en un texto claro, en lugar del mensaje en inglés de NAudio.
    [Fact]
    public void Un_aifc_comprimido_se_rechaza_con_un_texto_claro()
    {
        var ruta = Path.Combine(_dir, "ulaw.aif");
        WriteAifc(ruta, "ulaw", 16, 1, new byte[] { 1, 2, 3, 4 });

        var e = Assert.Throws<NotSupportedException>(() => AudioSamples.AbrirComoWaveStream(ruta));

        Assert.Contains("«ulaw»", e.Message);
        Assert.Contains("conviértelo a AIFF o WAV sin comprimir", e.Message);
        Assert.Throws<NotSupportedException>(() => AudioSamples.Abrir(ruta, out _));   // también al medir
    }

    [Fact]
    public void Un_aiff_danado_se_rechaza_con_un_texto_claro()
    {
        var ruta = Path.Combine(_dir, "roto.aiff");
        File.WriteAllBytes(ruta, Encoding.ASCII.GetBytes("FORM").Concat(new byte[] { 0, 0, 0, 12 })
                                         .Concat(Encoding.ASCII.GetBytes("AIFFbasura")).ToArray());

        var e = Assert.Throws<NotSupportedException>(() => AudioSamples.AbrirComoWaveStream(ruta));

        Assert.StartsWith("No se ha podido leer este archivo AIFF", e.Message);
    }

    // Volumen y «Comprobar audio» también: medir y recortar un AIFF-C de macOS.
    [Fact]
    public void Un_aifc_sowt_se_mide_y_se_recorta()
    {
        var ruta = Path.Combine(_dir, "mac.aiff");
        WriteAifc(ruta, "sowt", 16, 2, Rampa(44100 * 4 * 2));

        var muestras = AudioSamples.Abrir(ruta, out var recurso);
        using (recurso)
        {
            Assert.Equal(44100, muestras.WaveFormat.SampleRate);
            Assert.Equal(1024, muestras.Read(new float[1024], 0, 1024));
        }

        var recorte = AudioSamples.EscribirRecorteWav(ruta, 0.25, 1);
        try
        {
            using var wav = new NAudio.Wave.WaveFileReader(recorte.Ruta);
            Assert.InRange(wav.TotalTime.TotalSeconds, 0.99, 1.01);
        }
        finally { try { File.Delete(recorte.Ruta); } catch { } }
    }

    // WaveOut no garantiza 24 bits con todos los controladores: la vista previa de Windows los pasa a 16.
    [Fact]
    public void Para_escuchar_un_aiff_de_24_bits_se_pasa_a_16()
    {
        var a24 = Path.Combine(_dir, "a24.aif");
        WriteAifc(a24, "sowt", 24, 2, Rampa(44100 * 6));
        var a16 = Path.Combine(_dir, "a16.aif");
        WriteSineAiff(a16, segundos: 1);

        using var s24 = AudioSamples.AbrirComoWaveStream(a24);
        using var s16 = AudioSamples.AbrirComoWaveStream(a16);

        var p24 = AudioSamples.ParaReproducir(s24);
        Assert.Equal((16, 2, 44100), (p24.WaveFormat.BitsPerSample, p24.WaveFormat.Channels, p24.WaveFormat.SampleRate));
        Assert.Same(s16, AudioSamples.ParaReproducir(s16));
    }

    // ---------------------------------------------------------------- revisión de la tanda 3: AIFF-C en la biblioteca

    /// <summary>Añade un bloque al final de un AIFF y pone al día el tamaño del FORM.</summary>
    private static void AnadirBloque(string path, string id, byte[] datos)
    {
        var todo = File.ReadAllBytes(path).ToList();
        todo.AddRange(Encoding.ASCII.GetBytes(id));
        todo.AddRange(new[] { (byte)(datos.Length >> 24), (byte)(datos.Length >> 16), (byte)(datos.Length >> 8), (byte)datos.Length });
        todo.AddRange(datos);
        if (datos.Length % 2 == 1) todo.Add(0);
        var largo = todo.Count - 8;
        todo[4] = (byte)(largo >> 24); todo[5] = (byte)(largo >> 16); todo[6] = (byte)(largo >> 8); todo[7] = (byte)largo;
        File.WriteAllBytes(path, todo.ToArray());
    }

    // TagLib no abre los AIFF-C («File form type is not AIFF»): entraban sin duración ni calidad, y
    // Duplicados y Sustituir no los tenían en cuenta. Ahora salen de la cabecera.
    [Fact]
    public void Un_aifc_entra_en_la_biblioteca_con_su_duracion_y_su_calidad()
    {
        var ruta = Path.Combine(_dir, "mac.aif");
        WriteAifc(ruta, "sowt", 16, 2, Rampa(44100 * 4 * 2));   // dos segundos
        Assert.ThrowsAny<Exception>(() => TagLib.File.Create(ruta));   // lo que hace TagLib con ellos

        var t = LibraryScanner.ReadTrack(ruta);

        Assert.Equal((2, 44100, 2, 1411), (t.DurationSeconds, t.SampleRate, t.Channels, t.Bitrate));
        Assert.Null(t.Title);
        Assert.Equal(QualityTier.Lossless, AudioQuality.Rate(t));
    }

    // Si lleva bloque ID3 (donde las aplicaciones guardan las etiquetas de un AIFF), también sus etiquetas.
    [Fact]
    public void Las_etiquetas_de_un_aifc_se_leen_de_su_bloque_id3()
    {
        var ruta = Path.Combine(_dir, "Fisher - Stop It.aif");
        WriteAifc(ruta, "sowt", 16, 2, Rampa(44100 * 4));
        var id3 = new TagLib.Id3v2.Tag { Title = "Stop It", Performers = new[] { "Fisher" }, Album = "Stop It EP", InitialKey = "8A", BeatsPerMinute = 126 };
        AnadirBloque(ruta, "ID3 ", id3.Render().Data);

        var t = LibraryScanner.ReadTrack(ruta);

        Assert.Equal(("Stop It", "Fisher", "Stop It EP", "8A", 126u, 1), (t.Title, t.Artist, t.Album, t.Key, t.Bpm, t.DurationSeconds));
    }

    // Un AIFF-C comprimido: lo que cuenta su cabecera depende del códec, así que solo se da por bueno
    // lo que no depende de él (frecuencia y canales).
    [Fact]
    public void De_un_aifc_comprimido_solo_se_fia_la_frecuencia_y_los_canales()
    {
        var ruta = Path.Combine(_dir, "ulaw.aif");
        WriteAifc(ruta, "ulaw", 16, 1, new byte[] { 1, 2, 3, 4 });

        var t = LibraryScanner.ReadTrack(ruta);

        Assert.Equal((0, 44100, 1, 0), (t.DurationSeconds, t.SampleRate, t.Channels, t.Bitrate));
    }

    // Quien ya lo tenía en la caché de escaneo, vacío, lo recupera sin tener que vaciarla.
    [Fact]
    public void Un_aifc_que_estaba_vacio_en_la_cache_se_vuelve_a_leer()
    {
        var ruta = Path.Combine(_dir, "mac.aiff");
        WriteAifc(ruta, "sowt", 16, 2, Rampa(44100 * 4));
        var fi = new FileInfo(ruta);
        var cacheFile = Path.Combine(_dir, "scan-cache.json");
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(new Dictionary<string, ScanCache.Entry>
        {
            [ruta] = new() { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length },
        }));

        var t = new ScanCache(cacheFile).Read(ruta);

        Assert.Equal((1, 44100), (t.DurationSeconds, t.SampleRate));
    }

    // Aplicar una propuesta a un AIFF-C decía «File form type is not AIFF». Ahora, en español y con
    // qué hacer, sin tocar el archivo.
    [Fact]
    public async Task Aplicar_a_un_aifc_lo_dice_con_un_texto_claro_y_no_lo_toca()
    {
        var ruta = Path.Combine(_dir, "mac.aif");
        WriteAifc(ruta, "sowt", 16, 2, Rampa(44100 * 4));
        var antes = File.ReadAllBytes(ruta);
        var info = new Etiquetador.Core.Pipeline.ProcessResult
        {
            FilePath = ruta, Old = "mac.aif", New = "Fisher - Stop It.aif", Artist = "Fisher", Title = "Stop It", Found = true,
        };

        var res = await new Etiquetador.Core.Pipeline.ApplyEngine().ApplyOneAsync(info, true, new Etiquetador.Core.Pipeline.FieldFlags(),
            "keep", null, Path.Combine(_dir, "run.jsonl"), Path.Combine(_dir, "done.log"));

        Assert.False(res.TagOk);
        Assert.Equal(LectorAiff.TextoNoSeEscribe, res.TagErr);
        Assert.Equal(ruta, res.FinalPath);   // tampoco se renombra
        Assert.Equal(antes, File.ReadAllBytes(ruta));
        Assert.False(File.Exists(Path.Combine(_dir, "run.jsonl")));
    }

    // Sustituir con una descarga AIFF-C no ofrece copiar las etiquetas de DJ (no se pueden leer ni
    // escribir), y si se intentara, se dice por qué sin tocar el archivo.
    [Fact]
    public void A_un_aifc_no_se_le_copian_etiquetas_de_dj()
    {
        var ruta = Path.Combine(_dir, "mac.aif");
        WriteAifc(ruta, "sowt", 16, 2, Rampa(44100 * 4));
        var antes = File.ReadAllBytes(ruta);

        Assert.True(Etiquetador.Core.Dj.SustitucionBandeja.QueCopiar(new Etiquetador.Core.Dj.EtiquetasDj("x", "8A", 124),
                                                                     Etiquetador.Core.Dj.SustitucionBandeja.LeerEtiquetasDj(ruta)).Vacias);
        var (copiadas, error) = Etiquetador.Core.Dj.SustitucionBandeja.CopiarEtiquetasDj(ruta, new Etiquetador.Core.Dj.EtiquetasDj("x", "8A", 0), _ => "");

        Assert.Equal((0, LectorAiff.TextoNoSeEscribe), (copiadas, error));
        Assert.Equal(antes, File.ReadAllBytes(ruta));
    }

    [Fact]
    public void Solo_un_aifc_se_reconoce_como_tal()
    {
        var aifc = Path.Combine(_dir, "a.aif");
        WriteAifc(aifc, "sowt", 16, 1, new byte[] { 1, 2 });
        var clasico = Path.Combine(_dir, "b.aiff");
        WriteSineAiff(clasico, segundos: 1);

        Assert.True(LectorAiff.EsAifc(aifc));
        Assert.False(LectorAiff.EsAifc(clasico));
        Assert.False(LectorAiff.EsAifc(Path.Combine(_dir, "no-existe.aif")));
        var c = LectorAiff.LeerPista(clasico);
        Assert.Equal((1, 1411), (c!.DurationSeconds, c.Bitrate));   // la misma cuenta que TagLib para uno clásico
        Assert.Equal(1411, LibraryScanner.ReadTrack(clasico).Bitrate);
    }

    [Fact]
    public void La_cabecera_de_un_aiff_clasico_se_lee()
    {
        var ruta = Path.Combine(_dir, "clasico.aiff");
        WriteSineAiff(ruta, segundos: 1);
        using var fs = File.OpenRead(ruta);

        var c = AudioSamples.LeerCabeceraAiff(fs);

        Assert.NotNull(c);
        Assert.Equal((false, "NONE", 2, 16, 44100, 54L, 44100L * 4), (c!.Value.EsAifc, c.Value.Compresion, c.Value.Canales,
                     c.Value.Bits, c.Value.Frecuencia, c.Value.InicioDatos, c.Value.LongitudDatos));
        Assert.Null(AudioSamples.LeerCabeceraAiff(new MemoryStream(Encoding.ASCII.GetBytes("RIFF0000WAVE"))));
    }
}
