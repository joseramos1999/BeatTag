using Etiquetador.Core.Ai;

namespace Etiquetador.Tests;

/// <summary>
/// Renombrar con IA. Los nombres y las respuestas de la IA de estas pruebas son REALES: salen de
/// medir llama3.2 contra una biblioteca de 15.000 canciones antes de construir la herramienta.
/// </summary>
public class RenombradoIaTests
{
    private static (PropuestaNombre? P, string Motivo) Evaluar(string archivo, string artista, string titulo, string version,
                                                              double confianza = 0.9, bool mashup = false,
                                                              string tagA = "", string tagT = "")
        // La carpeta se monta con Path.Combine: escrita a mano como «C:\Musica\x.mp3», en macOS la
        // barra invertida no separa nada, el nombre del archivo sale entero y la prueba falla sin que
        // haya nada roto. Pasó en la CI de macOS.
        => RenombradoIa.Evaluar(Path.Combine(Path.GetTempPath(), "beattag-pruebas", archivo),
                                tagA, tagT, new AiParse(artista, titulo, version, mashup, confianza));

    // --- Qué se consulta ---

    [Theory]
    [InlineData("PALETA_X_FAINT.wav", true)]                                              // sin artista y con guiones bajos
    [InlineData("CUENTA REGREVISA 100 BPM - BRGS QATAR 2022.mp3", true)]                  // BPM y record pool
    [InlineData("El-Chuape_-Javi-Torres-Dale-Rollo-_Extended_.mp3", true)]               // palabras unidas con guiones
    [InlineData("Daddy Yankee - Gasolina (F3LY Intro Hype Dur.mp3", true)]               // paréntesis cortado
    [InlineData("Kabasaki, L0rna - LA SACAPUNTAS (F3LY Melodic Intro) [83 Bpm].mp3", true)]
    // Nombres ya limpios: la IA los devolvía igual. No se pregunta por ellos.
    [InlineData("Feid - Lady Mi Amor (Extended).mp3", false)]
    [InlineData("Bramsito, Niska - Criminel (Yaniss Remix).mp3", false)]
    [InlineData("ABBA - SOS.mp3", false)]
    public void Solo_se_consultan_los_nombres_sucios(string archivo, bool sucio)
        => Assert.Equal(sucio, RenombradoIa.PareceSucio(archivo));

    // --- Lo que se acepta ---

    [Fact]
    public void Un_nombre_sucio_bien_interpretado_se_propone()
    {
        var (p, _) = Evaluar("Mi Estrella (Rolando Rodriguez) 128 BPM - Los Yakis Ft. Ivan Narvi - 128bpm - DJTOOLSVIP.mp3",
                             "Los Yakis Ft. Ivan Narvi", "Mi Estrella (Rolando Rodriguez)", "128 BPM");

        Assert.NotNull(p);
        Assert.Equal("Los Yakis Ft. Ivan Narvi - Mi Estrella (Rolando Rodriguez)", p!.Propuesto);   // el BPM no es una versión
    }

    [Fact]
    public void Los_guiones_bajos_se_convierten_en_espacios()
    {
        var (p, _) = Evaluar("SAIKO - Ese_Soyy_Yooo.mp3", "SAIKO", "Ese_Soyy_Yooo", "");

        Assert.Equal("SAIKO - Ese Soyy Yooo", p!.Propuesto);
    }

    [Fact]
    public void La_basura_de_la_version_se_quita_y_se_conserva_el_descriptor()
    {
        var (p, _) = Evaluar("Peru_Make_Me_Feel_Good_LAM_Bootleg [chemist-.mp3", "LAM", "Peru Make Me Feel Good", "[chemist-music.com] Bootleg");

        Assert.Equal("LAM - Peru Make Me Feel Good (Bootleg)", p!.Propuesto);
    }

    // Respuesta real. «Latin Box» es un record pool y se quita, como hace la app con cualquier nombre.
    [Fact]
    public void Un_descriptor_colado_en_el_titulo_pasa_a_la_version()
    {
        var (p, _) = Evaluar("Calvin Harris, Clementine Douglas - Blessings (Latin Box Extended) 124bpm.mp3",
                             "Calvin Harris, Clementine Douglas", "Blessings (Latin Box Extended)", "");

        Assert.Equal("Calvin Harris, Clementine Douglas - Blessings (Extended)", p!.Propuesto);
    }

    // --- Lo que se corrige ---

    // Respuesta real: el record pool como artista.
    [Fact]
    public void El_record_pool_no_es_el_artista()
    {
        var (p, _) = Evaluar("CUENTA REGREVISA 100 BPM - BRGS QATAR 2022.mp3", "BRGS", "Cuenta Regrevisa", "");

        Assert.Equal("Cuenta Regrevisa", p!.Propuesto);
        Assert.Contains(p.Avisos, a => a.Contains("record pool"));
    }

    // Respuesta real: «Porfa - Porfa». Con la errata «EXTENED» del nombre original.
    [Fact]
    public void El_titulo_repetido_como_artista_se_quita()
    {
        var (p, _) = Evaluar("PORFA ( EXTENED 91BPM).mp3", "Porfa", "Porfa", "Extended 91BPM");

        Assert.Equal("Porfa (Extended)", p!.Propuesto);
    }

    // --- Lo que se descarta ---

    // Respuesta real: «Spice Mash» es un editor, y la IA se inventó a las Spice Girls.
    [Fact]
    public void Un_artista_inventado_se_descarta()
    {
        var (p, motivo) = Evaluar("Stay Worry (Spice Mash) [128bpm].mp3", "Spice Girls", "Stay Worry", "Mash");

        Assert.Null(p);
        Assert.Contains("no están", motivo);
    }

    // Respuesta real: el título es uno de los artistas.
    [Fact]
    public void Un_titulo_que_es_uno_de_los_artistas_se_descarta()
    {
        var (p, _) = Evaluar("Bizarrap - Daddy Yankee_ Bzrp Music Sessions, Vol. 0_66.mp3", "Bizarrap, Daddy Yankee", "Daddy Yankee", "");

        Assert.Null(p);
    }

    // Respuesta real: título «_».
    [Fact]
    public void Un_titulo_sin_letras_se_descarta()
    {
        Assert.Null(Evaluar("Marc Benjamin x Marcus Santoro _ David Pietr.mp3", "Marc Benjamin, Marcus Santoro, David Pietr", "_", "", 0.9).P);
    }

    // Respuesta real: artista y título mezclados en el título.
    [Fact]
    public void Un_titulo_con_artista_dentro_se_descarta()
    {
        Assert.Null(Evaluar("Pedroh, Quevedo, El Bobe, Camin _ Ptazeta - .mp3",
                            "Pedroh, Quevedo", "Pedroh, Quevedo, El Bobe, Camin & Ptazeta - Ahora 2 vs Ella Remix", "Ella Remix").P);
    }

    [Fact]
    public void Con_poca_confianza_no_se_propone()
    {
        Assert.Null(Evaluar("MIX&NOISE - QUE EMPAPE! __ FREE DOWNLOAD!.mp3", "MIX&NOISE", "QUE EMPAPE!", "FREE DOWNLOAD!", 0.0).P);
    }

    [Fact]
    public void Un_mashup_se_deja_como_esta()
    {
        var (p, motivo) = Evaluar("Pelele x Desesperados (73-90Bpm) (Try It Mas.mp3", "Pelele, Desesperados", "Pelele x Desesperados", "Try It Mashup", mashup: true);

        Assert.Null(p);
        Assert.Contains("mashup", motivo);
    }

    // --- Fallos vistos en la medición de punta a punta, todos con respuestas reales ---

    // --- Segunda medición, con otra muestra: respuestas reales ---

    [Fact]
    public void Quien_solo_aparece_entre_parentesis_no_es_el_artista()
    {
        Assert.Null(Evaluar("Pepas (David Guetta Remix).mp3", "David Guetta", "Pepas", "Remix").P);
    }

    [Fact]
    public void Si_las_etiquetas_dicen_el_artista_si_vale()
    {
        var (p, _) = Evaluar("Heartless Boy (Niko Alvera Remix).mp3", "Yanis.S, Lea Mojica", "Heartless Boy", "Niko Alvera Remix",
                             tagA: "Yanis.S feat Lea Mojica");

        Assert.Equal("Yanis.S, Lea Mojica - Heartless Boy (Niko Alvera Remix)", p!.Propuesto);
    }

    [Fact]
    public void Un_remix_entre_corchetes_no_se_pierde()
    {
        var (p, _) = Evaluar("Omar Montes, Khaled - Si Tu Te Vas [Remix] 128bpm.mp3", "Omar Montes, Khaled", "Si Tu Te Vas", "[Remix]");

        Assert.Equal("Omar Montes, Khaled - Si Tu Te Vas (Remix)", p!.Propuesto);
    }

    [Fact]
    public void Se_avisa_si_desaparece_un_descriptor()
    {
        var (p, _) = Evaluar("DENNIS, Emilia - MOTINHA 2.0 REMIX (F3LY Melodic Intro) [135 Bpm].mp3", "DENNIS, Emilia", "MOTINHA 2.0", "F3LY Melodic Intro");

        Assert.Contains(p!.Avisos, a => a.Contains("REMIX", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void La_tonalidad_no_forma_parte_del_titulo()
    {
        var (p, _) = Evaluar("(7A) GLOW UP - Lewis Potter, Reddy (Coro Live Edit 100Bpm).mp3", "Lewis Potter, Reddy", "(7A) GLOW UP", "Coro Live Edit");

        Assert.Equal("Lewis Potter, Reddy - GLOW UP (Coro Live Edit)", p!.Propuesto);
    }

    [Fact]
    public void Un_trozo_cortado_del_nombre_no_se_usa_como_version()
    {
        var (p, _) = Evaluar("Edward Maya ft Chimbala - Stereo love (Jose .mp3", "Edward Maya ft Chimbala", "Stereo Love", "Jose");

        Assert.Equal("Edward Maya ft Chimbala - Stereo Love", p!.Propuesto);
    }

    [Fact]
    public void Un_trozo_cortado_que_la_ia_completa_si_vale()
    {
        var (p, _) = Evaluar("Feid - REMIX EXCLUSIVO (Albert González Remi.mp3", "Feid", "REMIX EXCLUSIVO", "Albert Gonzalez Remix");

        Assert.Equal("Feid - REMIX EXCLUSIVO (Albert Gonzalez Remix)", p!.Propuesto);
    }

    [Fact]
    public void Un_numero_suelto_en_la_version_es_un_bpm()
    {
        var (p, _) = Evaluar("Alberto Gambino - Purpurina  Extended Latino  160.mp3", "Alberto Gambino", "Purpurina", "Extended Latino 160");

        Assert.Equal("Alberto Gambino - Purpurina (Extended Latino)", p!.Propuesto);
    }

    [Fact]
    public void La_cancion_de_antes_de_la_x_tampoco_puede_ser_la_version()
    {
        Assert.Null(Evaluar("MMC x Ven Conmigo - Dalex, Lenny Tavarez Ft. Sergio Blanco.mp3",
                            "Dalex, Lenny Tavarez Ft. Sergio Blanco", "Ven Conmigo", "MMC").P);
    }

    // --- Tercera medición, con otra muestra: respuestas reales ---

    [Fact]
    public void El_bpm_entre_corchetes_no_se_queda_en_el_titulo()
    {
        var (p, _) = Evaluar("Franco El Gorila X Yandel - Sexo Seguro (F3LY Aca Out) [97 Bpm].mp3",
                             "Franco El Gorila X Yandel", "Sexo Seguro [97BPM]", "F3LY Aca Out");

        Assert.Equal("Franco El Gorila X Yandel - Sexo Seguro (F3LY Aca Out)", p!.Propuesto);
    }

    [Fact]
    public void El_nombre_de_un_pack_no_es_un_artista()
    {
        Assert.Null(Evaluar("LOS DIOZES.mp3", "40 mashups by Alex Gonzalez, 4BEATS", "Los Dioses", "",
                            tagA: "40 mashups by Alex Gonzalez, 4BEATS").P);
    }

    [Fact]
    public void Se_pierde_una_cancion_aunque_la_x_este_en_la_parte_del_artista()
    {
        Assert.Null(Evaluar("Mañana x Girl - Ozuna, Myke Towers (Paul Mor.mp3", "Ozuna, Myke Towers", "Mañana", "").P);
    }

    [Fact]
    public void Otros_simbolos_de_x_tambien_juntan_canciones()
    {
        Assert.Null(Evaluar("No Te Canses ✘ El Funeral... Daddy Yankee [ .mp3", "Daddy Yankee", "No Te Canses", "El Funeral").P);
    }

    [Fact]
    public void Se_avisa_si_el_artista_sale_de_las_etiquetas()
    {
        var (p, _) = Evaluar("Poblado Remix X Culo (TRY IT RE-EDIT) 100bpm.mp3", "Try It", "Poblado Remix X Culo", "TRY IT RE-EDIT", tagA: "Try It");

        // «Try It» sí aparece en el nombre (dentro del paréntesis) y las etiquetas lo repiten: aquí la
        // regla del paréntesis no puede decidir, así que lo que queda es avisar o descartar.
        Assert.True(p == null || p.Avisos.Count > 0);
    }

    [Fact]
    public void Intensa_music_es_un_record_pool_entero()
    {
        var (p, _) = Evaluar("Carnaval In Spain (Intensa Music Remix) 128bpm.mp3", "Kilian Dominguez & Javi Slink", "Carnaval In Spain",
                             "Intensa Music Remix", tagA: "Kilian Dominguez & Javi Slink");

        Assert.Equal("Kilian Dominguez & Javi Slink - Carnaval In Spain (Remix)", p!.Propuesto);
    }

    // Visto en la app: la versión se duplicaba al mover el descriptor que la IA dejó en el título.
    [Fact]
    public void Mover_el_descriptor_del_titulo_no_duplica_la_version()
    {
        var (p, _) = Evaluar("La Manta - Dembow Rip (Oscar Alegre Hype Intro) (100-118 Bpm).mp3",
                             "La Manta", "Dembow Rip (Oscar Alegre Hype Intro)", "Hype Intro");

        Assert.Equal("La Manta - Dembow Rip (Oscar Alegre Hype Intro)", p!.Propuesto);
    }

    [Fact]
    public void Los_nombres_con_mashup_no_se_consultan()
    {
        Assert.False(RenombradoIa.PareceSucio("Vista Al Mar x Punto G (Try It Mashup).mp3"));
    }

    [Fact]
    public void Un_mashup_deshecho_se_descarta()
    {
        Assert.Null(Evaluar("Dile a El x Normal - Rauw Alejandro, Feid (P.mp3", "Rauw Alejandro, Feid", "Dile a El", "Normal").P);
        Assert.Null(Evaluar("Wisin y Yandel x J.Balvin - Pam Pam x Mora-1.mp3", "Wisin y Yandel x J.Balvin", "Pam Pam", "Hype Intro").P);
    }

    [Fact]
    public void Un_mashup_que_conserva_las_dos_canciones_se_acepta()
    {
        var (p, _) = Evaluar("Clarent x Eminem - LOVE x Lose Yourself (Latin Box Open Show).mp3", "Clarent, Eminem", "LOVE x Lose Yourself", "Latin Box Open Show");

        Assert.Equal("Clarent, Eminem - LOVE x Lose Yourself (Open Show)", p!.Propuesto);
    }

    [Fact]
    public void Los_parentesis_descuadrados_se_descartan()
    {
        Assert.Null(Evaluar("Tranky Funky x Baila Baila Baila (E. Rodriguez Private Edit 110).mp3",
                            "Trueno, Ozuna", "Tranky Funky x Baila", "Baila Baila (E. Rodriguez Private Edit").P);
    }

    [Fact]
    public void Una_version_dentro_del_artista_se_descarta()
    {
        Assert.Null(Evaluar("Dile - La Nueva Escuela (Antonio Guevara Live Edit 125Bpm).mp3",
                            "La Nueva Escuela (Antonio Guevara Live Edit 125Bpm)", "Dile", "Dile").P);
    }

    [Fact]
    public void Un_titulo_que_repite_al_artista_se_descarta()
    {
        Assert.Null(Evaluar("Rafa Pabon DJ TAO Turreo Sessions #11 (Liyo .mp3", "Rafa Pabon", "Rafa Pabon DJ Turreo Sessions #11", "Liyo").P);
    }

    // La IA dijo «F3LY Intro Edit»; el nombre ya decía «F3LY Melodic Intro». Se queda el del nombre.
    [Fact]
    public void Si_la_version_propuesta_no_vale_se_conserva_la_del_nombre()
    {
        var (p, _) = Evaluar("Daddy Yankee - Rompe (F3LY Melodic Intro) [88 Bpm].mp3", "Daddy Yankee", "Rompe", "F3LY Intro Edit");

        Assert.Equal("Daddy Yankee - Rompe (F3LY Melodic Intro)", p!.Propuesto);
    }

    // --- Renombrar el archivo de verdad ---

    [Fact]
    public void Renombra_conservando_la_extension()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var origen = Path.Combine(dir, "SAIKO - Ese_Soyy_Yooo.mp3");
            Mp3Fixture.WriteMinMp3(origen);

            var t = RenombradoIa.RenombrarArchivo(origen, "SAIKO - Ese Soyy Yooo");

            Assert.True(t.Ok, t.Error);
            Assert.False(File.Exists(origen));
            Assert.True(File.Exists(Path.Combine(dir, "SAIKO - Ese Soyy Yooo.mp3")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si el nombre ya existe no se inventa «(2)»: probablemente es un duplicado y hay que verlo.
    [Fact]
    public void Si_el_nombre_ya_existe_no_renombra_ni_sobrescribe()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var origen = Path.Combine(dir, "Rompe 88bpm.mp3");
            var existente = Path.Combine(dir, "Daddy Yankee - Rompe.mp3");
            Mp3Fixture.WriteMinMp3(origen, frames: 10);
            Mp3Fixture.WriteMinMp3(existente, frames: 40);
            var tamano = new FileInfo(existente).Length;

            var t = RenombradoIa.RenombrarArchivo(origen, "Daddy Yankee - Rompe");

            Assert.False(t.Ok);
            Assert.Contains("duplicado", t.Error);
            Assert.True(File.Exists(origen));
            Assert.Equal(tamano, new FileInfo(existente).Length);
            Assert.Empty(Directory.GetFiles(dir, "*(2)*"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Un_nombre_con_ruta_no_saca_el_archivo_de_su_carpeta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var origen = Path.Combine(dir, "tema.mp3");
            Mp3Fixture.WriteMinMp3(origen);

            var t = RenombradoIa.RenombrarArchivo(origen, @"..\fuera");

            Assert.True(File.Exists(origen) || File.Exists(Path.Combine(dir, t.Destino)));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "fuera.mp3")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Si_ya_se_llama_asi_no_hay_propuesta()
    {
        Assert.Null(Evaluar("Feid - Lady Mi Amor (Extended).mp3", "Feid", "Lady Mi Amor", "Extended").P);
    }

    // --- Lo que se avisa ---

    // Respuesta real: invierte artista y título. No se puede saber con reglas cuál es cuál, porque
    // también hay nombres que vienen al revés; se propone pero con aviso.
    [Fact]
    public void Se_avisa_si_artista_y_titulo_quedan_al_reves()
    {
        var (p, _) = Evaluar("Power 74 - DOKE (DAVIDMUSIC Remix 128Bpm).mp3", "DOKE", "Power 74", "DAVIDMUSIC Remix 128Bpm");

        Assert.NotNull(p);
        Assert.Equal("DOKE - Power 74 (DAVIDMUSIC Remix)", p!.Propuesto);
        Assert.Contains(p.Avisos, a => a.Contains("al revés"));
    }

    // Respuesta real: «Sold3k» desaparece del nombre.
    [Fact]
    public void Se_avisa_de_lo_que_desaparece_del_nombre()
    {
        var (p, _) = Evaluar("Sold3k_-Joan-Roca-Te-He-Echado-de-Menos-_Extended_.mp3", "Joan Roca", "Te He Echado de Menos", "Extended");

        Assert.NotNull(p);
        Assert.Contains(p!.Avisos, a => a.Contains("Sold3k"));
    }

    [Fact]
    public void Una_version_que_no_estaba_en_el_nombre_se_ignora()
    {
        var (p, _) = Evaluar("Abraham_Mateo_-_BAI-LALA.mp3", "Abraham Mateo", "BAI-LALA", "Radio Edit");

        Assert.Equal("Abraham Mateo - BAI-LALA", p!.Propuesto);
        Assert.Contains(p.Avisos, a => a.Contains("Radio Edit"));
    }
}
