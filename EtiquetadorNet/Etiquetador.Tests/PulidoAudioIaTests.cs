using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Dj;
using static Etiquetador.Tests.Mp3GainSintetico;

namespace Etiquetador.Tests;

/// <summary>
/// Tanda 2, equipo «pulido_nucleo»: los detalles que dejaron los revisores de la tanda 1 en audio
/// (volumen, huellas, letra explícita) y en la IA local y las colecciones.
/// </summary>
public class PulidoAudioIaTests
{
    // ------------------------------------------------------------------ A5 · pie APE corrupto

    // Un pie «APETAGEX» con un tamaño corrupto y sin cabecera ponía el final del audio en mitad de
    // la canción: Leer devolvía null, pero el recorrido se paraba ahí y Apply ajustaba solo una parte.
    // Un salto de volumen a mitad de tema, sin aviso.
    [Fact]
    public void Un_pie_APE_corrupto_no_corta_el_ajuste_a_mitad_de_cancion()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Burna Boy - Last Last.mp3");
            var audio = Tramas(30, _ => 160);
            // Pie APE sin cabecera cuyo tamaño apunta a mitad de la trama 15.
            var pie = new byte[32];
            Encoding.ASCII.GetBytes("APETAGEX").CopyTo(pie, 0);
            BinaryPrimitives.WriteInt32LittleEndian(pie.AsSpan(8), 2000);
            BinaryPrimitives.WriteInt32LittleEndian(pie.AsSpan(12), 32 + 15 * LargoTrama + 100);
            BinaryPrimitives.WriteInt32LittleEndian(pie.AsSpan(16), 1);
            File.WriteAllBytes(p, [.. audio, .. pie, .. Id3v1("Last Last")]);

            var r = Mp3Gain.Apply(p, 2);

            Assert.True(r.Ok, r.Error);
            Assert.Equal(30, r.Frames);
            var b = File.ReadAllBytes(p);
            for (var k = 0; k < 30; k++)
                Assert.All(Ganancias(b, k * LargoTrama), g => Assert.Equal(162, g));
            Assert.Equal(pie, b[audio.Length..(audio.Length + 32)]);   // el pie, intacto
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // A1 (la guarda del umbral): la prueba se ha rehecho en su sitio, FingerprintDuplicatesTests.
    // Muestrear_por_contenido_no_junta_canciones_distintas.

    // ------------------------------------------------------------------ A12 · «Clean By Dj»

    private static Explicitness Letra(string archivo, string titulo = "")
        => ExplicitDetector.Detect(new Track { FilePath = @"E:\Musica\" + archivo, Title = titulo });

    [Theory]
    [InlineData("Diabla en Bellakera (Extended) - Clean By Dj.mp3", Explicitness.Clean)]   // el de explicit_eval.txt
    [InlineData("Tusa (Dirty Intro by DJ Nano).mp3", Explicitness.Explicit)]
    [InlineData("Gasolina (Clean Mashup).mp3", Explicitness.Clean)]
    [InlineData("Provenza (Versión Limpia de DJ Pepe).mp3", Explicitness.Clean)]
    [InlineData("Gasolina (Dirty Sou Mashup).mp3", Explicitness.Unknown)]                  // Dirty Sou es un DJ
    [InlineData("Rockabye (Clean Bandit Remix).mp3", Explicitness.Unknown)]               // Clean Bandit, un grupo
    [InlineData("Clean Bandit - Rockabye.mp3", Explicitness.Unknown)]
    public void El_marcador_admite_quien_firma_la_version(string archivo, Explicitness esperado)
        => Assert.Equal(esperado, Letra(archivo));

    // ------------------------------------------------------------------ I12 · tildes diacríticas

    // La IA reordena: alinear por orden ponía la tilde en el «Se» equivocado.
    [Fact]
    public void Si_la_ia_reordena_las_palabras_la_tilde_sigue_a_su_contexto()
        => Assert.Equal("Se Fue Ya Sé", AiSuggestion.RestaurarAcentos("Se Fue Ya Se", "Ya_Sé_Que_Se_Fue"));

    // Al completar un nombre, la cola de la etiqueta trae «El» (artículo) y el nombre solo tenía «Él».
    [Fact]
    public void Un_el_de_mas_en_la_propuesta_no_se_lleva_la_tilde()
        => Assert.Equal("Él Me Mintió (Remix De El Alfa)",
                        AiSuggestion.RestaurarAcentos("Él Me Mintió (Remix De El Alfa)", "Él Me Mintió (Remix De"));

    [Fact]
    public void Completar_desde_la_etiqueta_no_le_pone_tilde_al_articulo()
    {
        var r = NombreCortado.DesdeEtiquetas(@"E:\Musica\Él Me Mintió (Remix De.mp3", "", "Él Me Mintió (Remix De El Alfa)");
        Assert.NotNull(r);
        Assert.DoesNotContain("Él Alfa", r);
        Assert.Contains("El Alfa", r);
    }

    // Lo de siempre sigue igual: una palabra que solo aparece con tilde y no es de las diacríticas.
    [Fact]
    public void Una_palabra_sin_ambiguedad_sigue_recuperando_su_tilde_en_todas_partes()
        => Assert.Equal("Rosalía - Despechá (DESPECHÁ Intro)",
                        AiSuggestion.RestaurarAcentos("Rosalia - Despecha (DESPECHA Intro)", "Rosalía - Despechá (Intr"));

    // ------------------------------------------------------------------ I1 · tonalidad y BPM en la versión

    // Compose envuelve la versión en paréntesis, y la limpieza de tonalidad y BPM estaba anclada al
    // final del texto: el «)» impedía quitarlos.
    [Fact]
    public void Completar_con_la_ia_quita_la_tonalidad_y_el_bpm_de_la_version()
        => Assert.Equal("Karol G - Provenza (Intro Clean)",
                        NombreCortado.DesdeIa(Path.Combine("Musica", "Karol G - Provenza (Intro Clea.mp3"),"Karol G", "Provenza", "Intro Clean 6A 95"));

    // ------------------------------------------------------------------ I9 · qué cuenta para la pausa

    private sealed class OllamaFalso : HttpMessageHandler
    {
        public HttpStatusCode Codigo = HttpStatusCode.BadRequest;
        public int Generaciones;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Interlocked.Increment(ref Generaciones);
            return Task.FromResult(new HttpResponseMessage(Codigo)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = "invalid options" }), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 4)]            // depende de la petición: no pausa
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]    // el servidor falla: tras tres, pausa
    [InlineData(HttpStatusCode.NotFound, 3)]              // el modelo no está: también
    public async Task Solo_los_fallos_ajenos_a_la_peticion_pausan_la_ia(HttpStatusCode codigo, int llegan)
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var ollama = new OllamaFalso { Codigo = codigo };
            var ia = new OllamaClient(new ApiClient(new AppPaths(dir)), http: new HttpClient(ollama));

            for (var i = 0; i < 4; i++)
            {
                var (json, error) = await ia.PedirJsonAsync("sistema", $"petición {i}", "llama3.2");
                Assert.Null(json);
                Assert.NotEqual("", error);
            }
            Assert.Equal(llegan, ollama.Generaciones);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ I11 · varios géneros, varios valores

    [Theory]
    [InlineData("Hip Hop; Latino", new[] { "Hip Hop", "Latino" })]
    [InlineData("Dance/Electrónica", new[] { "Dance/Electrónica" })]   // un valor con barra sigue siendo uno
    [InlineData("Reggaeton", new[] { "Reggaeton" })]
    [InlineData("", new string[0])]
    public void Una_lista_de_generos_se_escribe_como_varios_valores(string propuesto, string[] valores)
        => Assert.Equal(valores, GenerosIa.ValoresParaEscribir(propuesto));

    // Efecto a sabiendas: un segmento suelto sigue la misma regla que el género solo.
    [Fact]
    public void Dance_Electronic_se_unifica_como_Electronic_suelto()
    {
        var p = GenerosIa.PorReglas("Dance/Electronic", 40);
        Assert.Equal("Dance/Electrónica", p.Propuesto);
        Assert.Equal(OrigenGenero.Regla, p.Origen);
    }

    // ------------------------------------------------------------------ I2 · un estilo dentro de otro

    private static bool Entra(string pedido, string generoDelTema)
        => FiltroColeccion.Cumple(new ColeccionInteligente { Genero = pedido },
            new Track { Artist = "Artista", Title = "Tema", Genre = generoDelTema, FilePath = $@"C:\Musica\Artista - Tema ({generoDelTema}).mp3" }, null);

    [Theory]
    [InlineData("pop", "Electropop", true)]        // un canónico de GenreNormalizer que dejó de entrar
    [InlineData("house", "Housemusic", true)]
    [InlineData("dance", "Eurodance", false)]      // sigue fuera, como pidió el hallazgo
    [InlineData("electro", "Electronica", false)]  // otro género
    [InlineData("rap", "Trap", false)]
    public void Los_compuestos_de_una_palabra_entran_solo_si_son_ese_estilo(string pedido, string genero, bool entra)
        => Assert.Equal(entra, Entra(pedido, genero));
}
