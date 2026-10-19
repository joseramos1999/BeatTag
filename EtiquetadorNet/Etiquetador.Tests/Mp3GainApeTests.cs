using System.Globalization;
using System.Text;
using Etiquetador.Core.Analysis;
using static Etiquetador.Tests.Mp3GainSintetico;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgo A5: al ajustar el volumen, las etiquetas APE de MP3Gain y ReplayGain se quedaban
/// describiendo el audio de ANTES. Visto en una biblioteca real: 173 de 200 MP3 traen
/// REPLAYGAIN_TRACK_GAIN y MP3GAIN_MINMAX del MP3Gain clásico, y 166 también MP3GAIN_UNDO. Después
/// de que BeatTag bajara 3 dB, MP3Gain volvía a corregir lo ya corregido y su «Deshacer» devolvía
/// el volumen antiguo en vez del original.
///
/// Los valores de las pruebas son los de un MP3 real de la biblioteca (E:\Musica\COMERCIAL\Afro):
/// MINMAX «105,209», UNDO «+001,+001,N», TRACK_GAIN «-6.605000 dB», TRACK_PEAK «0.903952», con
/// cabecera APE, los datos de la canción detrás y el ID3v1 al final.
/// </summary>
public class Mp3GainApeTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static (string, byte[], uint)[] EtiquetaReal() => new[]
    {
        Texto("MP3GAIN_MINMAX", "105,209"),
        Texto("MP3GAIN_UNDO", "+001,+001,N"),
        Texto("REPLAYGAIN_TRACK_GAIN", "-6.605000 dB"),
        Texto("REPLAYGAIN_TRACK_PEAK", "0.903952"),
        Texto("Artist", "Burna Boy"),
        Texto("Title", "Last Last"),
        Texto("BPM", "100"),
    };

    // Mínimo y máximo de las tramas iguales a los de MINMAX, como en el archivo real.
    private static byte[] AudioReal() => Tramas(30, k => k == 0 ? 105 : k == 1 ? 209 : 160);

    private static void EnTemporal(Action<string> prueba)
    {
        var dir = Mp3Fixture.NewTempDir();
        try { prueba(Path.Combine(dir, "Burna Boy - Last Last.mp3")); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Bajar_3_dB_pone_al_dia_las_etiquetas_de_MP3Gain_y_ReplayGain()
    {
        EnTemporal(p =>
        {
            var audio = AudioReal();
            var id3v1 = Id3v1("Last Last");
            File.WriteAllBytes(p, [.. audio, .. Ape(true, EtiquetaReal()), .. id3v1]);

            var r = Mp3Gain.Apply(p, Mp3Gain.StepsFor(-3.0));
            Assert.True(r.Ok, r.Error);
            Assert.Equal(-2, r.Steps);

            var b = File.ReadAllBytes(p);
            var ape = LeerApe(b, out var conCabecera);
            Assert.True(conCabecera);

            // MP3Gain la había bajado 1 paso y BeatTag 2 más: para volver al original hay que subir 3.
            Assert.Equal("+003,+003,N", Valor(ape, "MP3GAIN_UNDO"));
            Assert.Equal("103,207", Valor(ape, "MP3GAIN_MINMAX"));
            // -6.605 + 2 · 1,505 = -3.595: ahora le falta menos para llegar al nivel de referencia.
            Assert.Equal("-3.595000 dB", Valor(ape, "REPLAYGAIN_TRACK_GAIN"));
            // 0.903952 · 2^(-2/4) = 0.639191: el pico baja con el audio.
            Assert.Equal("0.639191", Valor(ape, "REPLAYGAIN_TRACK_PEAK"));

            // Lo demás de la etiqueta, intacto y en su orden.
            Assert.Equal(new[] { "MP3GAIN_MINMAX", "MP3GAIN_UNDO", "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK", "Artist", "Title", "BPM" },
                         ape.Select(e => e.Clave));
            Assert.Equal("Burna Boy", Valor(ape, "Artist"));
            Assert.Equal("Last Last", Valor(ape, "Title"));
            Assert.Equal(id3v1, b[^128..]);

            // Y el audio, bajado de verdad.
            Assert.Equal(new[] { 103, 103, 103, 103 }, Ganancias(b, 0));
            Assert.Equal(new[] { 207, 207, 207, 207 }, Ganancias(b, LargoTrama));
            Assert.Equal(new[] { 158, 158, 158, 158 }, Ganancias(b, 2 * LargoTrama));
        });
    }

    // Otro lector de APE (TagLib#, el que usa toda la aplicación) tiene que entender la etiqueta
    // reescrita: si no, se habría estropeado para cualquier otro programa.
    [Fact]
    public void La_etiqueta_reescrita_la_entiende_otro_lector()
    {
        EnTemporal(p =>
        {
            File.WriteAllBytes(p, [.. AudioReal(), .. Ape(true, EtiquetaReal()), .. Id3v1("Last Last")]);
            Assert.True(Mp3Gain.Apply(p, -2).Ok);

            using var f = TagLib.File.Create(p);
            var ape = Assert.IsType<TagLib.Ape.Tag>(f.GetTag(TagLib.TagTypes.Ape, false));
            Assert.Equal("+003,+003,N", ape.GetItem("MP3GAIN_UNDO").ToString());
            Assert.Equal("-3.595000 dB", ape.GetItem("REPLAYGAIN_TRACK_GAIN").ToString());
            Assert.Equal("Last Last", ape.Title);
        });
    }

    // El deshacer de BeatTag llama a Apply con los pasos inversos: tiene que devolver las
    // etiquetas a como estaban, igual que devuelve el audio.
    [Fact]
    public void Deshacer_el_volumen_devuelve_las_etiquetas_como_estaban()
    {
        EnTemporal(p =>
        {
            var audio = AudioReal();
            var id3v1 = Id3v1("Last Last");
            File.WriteAllBytes(p, [.. audio, .. Ape(true, EtiquetaReal()), .. id3v1]);

            Assert.True(Mp3Gain.Apply(p, -2).Ok);
            Assert.True(Mp3Gain.Apply(p, +2).Ok);

            var b = File.ReadAllBytes(p);
            var ape = LeerApe(b, out _);
            Assert.Equal("+001,+001,N", Valor(ape, "MP3GAIN_UNDO"));
            Assert.Equal("105,209", Valor(ape, "MP3GAIN_MINMAX"));
            Assert.Equal("-6.605000 dB", Valor(ape, "REPLAYGAIN_TRACK_GAIN"));
            // El pico se guarda con seis decimales y al ir y volver puede moverse en el último,
            // igual que con el propio MP3Gain: 0.903952 → 0.639191 → 0.903953. Es una millonésima.
            var pico = double.Parse(Valor(ape, "REPLAYGAIN_TRACK_PEAK"), Inv);
            Assert.InRange(pico, 0.903952 - 1.5e-6, 0.903952 + 1.5e-6);

            Assert.Equal(audio, b[..audio.Length]);   // el audio, exactamente como estaba
            Assert.Equal(id3v1, b[^128..]);
        });
    }

    // Un MP3 que MP3Gain solo analizó (sin aplicar nada) trae MINMAX y ReplayGain pero no UNDO.
    // Tras el cambio de BeatTag hay que anotar el deshacer, donde lo pone MP3Gain; y al deshacer en
    // BeatTag el UNDO vuelve a cero y se quita. MP3Gain lo dejaría a «+000,+000,N» (en mp3gain.c,
    // changeGainAndTag pone siempre haveUndo); aquí se quita para que la etiqueta vuelva idéntica,
    // y MP3Gain trata la falta de UNDO como cero.
    [Fact]
    public void Si_MP3Gain_solo_lo_habia_analizado_se_anota_el_deshacer_y_luego_se_quita()
    {
        EnTemporal(p =>
        {
            File.WriteAllBytes(p, [.. Tramas(30, k => k == 0 ? 120 : k == 1 ? 201 : 160),
                                   .. Ape(true,
                                          Texto("MP3GAIN_MINMAX", "120,201"),
                                          Texto("REPLAYGAIN_TRACK_GAIN", "-4.130000 dB"),
                                          Texto("REPLAYGAIN_TRACK_PEAK", "0.812345"),
                                          Texto("Artist", "Burna Boy")),
                                   .. Id3v1("Last Last")]);

            Assert.True(Mp3Gain.Apply(p, -2).Ok);
            var ape = LeerApe(File.ReadAllBytes(p), out _);
            Assert.Equal(new[] { "MP3GAIN_MINMAX", "MP3GAIN_UNDO", "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK", "Artist" },
                         ape.Select(e => e.Clave));
            Assert.Equal("+002,+002,N", Valor(ape, "MP3GAIN_UNDO"));
            Assert.Equal("118,199", Valor(ape, "MP3GAIN_MINMAX"));
            Assert.Equal("-1.120000 dB", Valor(ape, "REPLAYGAIN_TRACK_GAIN"));

            Assert.True(Mp3Gain.Apply(p, +2).Ok);
            ape = LeerApe(File.ReadAllBytes(p), out _);
            Assert.Equal(new[] { "MP3GAIN_MINMAX", "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK", "Artist" },
                         ape.Select(e => e.Clave));
            Assert.Equal("120,201", Valor(ape, "MP3GAIN_MINMAX"));
            Assert.Equal("-4.130000 dB", Valor(ape, "REPLAYGAIN_TRACK_GAIN"));
        });
    }

    // Lo pedido expresamente: si el archivo no lleva APE, no se le crea una.
    [Fact]
    public void Sin_etiqueta_APE_no_se_crea_ninguna()
    {
        EnTemporal(p =>
        {
            var id3v1 = Id3v1("Last Last");
            File.WriteAllBytes(p, [.. AudioReal(), .. id3v1]);
            var largo = new FileInfo(p).Length;

            Assert.True(Mp3Gain.Apply(p, -2).Ok);

            var b = File.ReadAllBytes(p);
            Assert.Equal(largo, b.Length);
            Assert.DoesNotContain("APETAGEX", Encoding.ASCII.GetString(b));
            Assert.Equal(id3v1, b[^128..]);
        });
    }

    // Una APE sin datos de ganancia (solo artista, título…) no se reescribe: ni un byte cambia.
    [Fact]
    public void Una_etiqueta_APE_sin_datos_de_ganancia_queda_igual()
    {
        EnTemporal(p =>
        {
            var audio = AudioReal();
            byte[] cola = [.. Ape(true, Texto("Artist", "Burna Boy"), Texto("Title", "Last Last"), Texto("BPM", "100")),
                           .. Id3v1("Last Last")];
            File.WriteAllBytes(p, [.. audio, .. cola]);

            Assert.True(Mp3Gain.Apply(p, -2).Ok);

            var b = File.ReadAllBytes(p);
            Assert.Equal(cola, b[audio.Length..]);
        });
    }

    // La etiqueta de foobar2000 solo lleva ReplayGain. Se pone al día, pero no se le añade nada de
    // MP3Gain: ese programa no la ha tocado nunca.
    [Fact]
    public void Una_etiqueta_solo_de_ReplayGain_se_pone_al_dia_sin_anadir_nada_de_MP3Gain()
    {
        EnTemporal(p =>
        {
            File.WriteAllBytes(p, [.. AudioReal(),
                                   .. Ape(true, Texto("REPLAYGAIN_TRACK_GAIN", "-7.12 dB"), Texto("REPLAYGAIN_TRACK_PEAK", "0.988212"))]);

            Assert.True(Mp3Gain.Apply(p, +1).Ok);

            var ape = LeerApe(File.ReadAllBytes(p), out _);
            Assert.Equal(new[] { "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK" }, ape.Select(e => e.Clave));
            Assert.Equal("-8.625000 dB", Valor(ape, "REPLAYGAIN_TRACK_GAIN"));
            Assert.Equal("1.175189", Valor(ape, "REPLAYGAIN_TRACK_PEAK"));   // 0.988212 · 2^(1/4)
        });
    }

    // APE sin cabecera (solo pie), sin ID3v1 detrás y con las claves en minúsculas: el formato
    // permite las tres cosas y hay programas que las escriben así.
    [Fact]
    public void Tambien_entiende_la_etiqueta_sin_cabecera_y_con_claves_en_minusculas()
    {
        EnTemporal(p =>
        {
            File.WriteAllBytes(p, [.. AudioReal(),
                                   .. Ape(false,
                                          Texto("mp3gain_minmax", "105,209"),
                                          Texto("mp3gain_undo", "+001,+001,N"),
                                          Texto("replaygain_track_gain", "-6.605000 dB"),
                                          Texto("replaygain_album_gain", "-7.000000 dB"))]);

            Assert.True(Mp3Gain.Apply(p, +1).Ok);

            var ape = LeerApe(File.ReadAllBytes(p), out var conCabecera);
            Assert.False(conCabecera);
            Assert.Equal("106,210", Valor(ape, "mp3gain_minmax"));
            Assert.DoesNotContain(ape, e => e.Clave == "mp3gain_undo");   // +001 menos 1 paso = cero: se quita
            Assert.Equal("-8.110000 dB", Valor(ape, "replaygain_track_gain"));
            Assert.Equal("-8.505000 dB", Valor(ape, "replaygain_album_gain"));
        });
    }
}
