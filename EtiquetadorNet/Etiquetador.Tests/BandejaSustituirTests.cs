using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// I14: «Sustituir» la copia de la biblioteca cuando la descarga es mejor, y X15: qué cuenta como
/// aviso de duplicado al descartar. Lo que se prueba es que solo se proponga cuando la descarga es
/// de verdad la misma grabación y de mejor calidad, que la ficha de DJ no se pierda ni herede lo que
/// no debe, y que ningún fallo a mitad deje la biblioteca sin la canción sin decirlo.
/// </summary>
public class BandejaSustituirTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Ruta(params string[] partes) => Path.Combine(new[] { _dir }.Concat(partes).ToArray());

    private static Track Tema(string ruta, int kbps, int segundos = 200, string artista = "Fisher", string titulo = "Losing It") => new()
    {
        FilePath = ruta, Artist = artista, Title = titulo, Bitrate = kbps, DurationSeconds = segundos,
    };

    private static PropuestaSustitucion? Proponer(Track nueva, params Track[] biblioteca)
        => SustitucionBandeja.Proponer(nueva, new IndiceCanciones(biblioteca));

    // ---------------------------------------------------------------- X15 · avisos de duplicado

    [Theory]
    [InlineData(TipoAviso.YaEnBiblioteca, true)]
    [InlineData(TipoAviso.RepetidaEnBandeja, true)]
    [InlineData(TipoAviso.OtraVersion, false)]   // una extended de algo que ya tienes no es un duplicado
    [InlineData(TipoAviso.CalidadBaja, false)]
    [InlineData(TipoAviso.Incompleta, false)]
    [InlineData(TipoAviso.NombreOcupado, false)]
    public void Solo_la_copia_exacta_o_repetida_cuenta_como_duplicado(TipoAviso tipo, bool esperado)
        => Assert.Equal(esperado, RevisionBandeja.EsAvisoDeDuplicado(tipo));

    [Fact]
    public void El_dialogo_de_descartar_avisa_de_las_que_no_son_duplicados()
    {
        Assert.Equal("Las 25 ya están en la biblioteca o repetidas en la bandeja.", RevisionBandeja.CuerpoDescartar(25, 25));
        Assert.Equal("La descarga ya está en la biblioteca o repetida en la bandeja.", RevisionBandeja.CuerpoDescartar(1, 1));
        Assert.Equal("24 ya están en la biblioteca o repetidas en la bandeja. ⚠ 1 no tiene aviso de duplicado: no consta que ya la tengas.",
                     RevisionBandeja.CuerpoDescartar(25, 24));
        Assert.Equal("⚠ Ninguna de las 3 tiene aviso de duplicado: no consta que ya las tengas.", RevisionBandeja.CuerpoDescartar(3, 0));
        Assert.StartsWith("⚠ Esta descarga no tiene aviso", RevisionBandeja.CuerpoDescartar(1, 0));
    }

    // Una descarga mejor que la copia de la biblioteca también sale con «Ya en la biblioteca»: el
    // diálogo recuerda que esa conviene usarla para sustituir, no tirarla.
    [Fact]
    public void El_dialogo_de_descartar_avisa_de_las_que_mejoran_la_biblioteca()
    {
        Assert.EndsWith("⚠ 1 es de mejor calidad que su copia de la biblioteca: puedes sustituir esa copia por ella en lugar de descartarla.",
                        RevisionBandeja.CuerpoDescartar(25, 25, mejores: 1));
        Assert.Contains("⚠ 3 son de mejor calidad", RevisionBandeja.CuerpoDescartar(25, 25, mejores: 3));
        Assert.DoesNotContain("mejor calidad", RevisionBandeja.CuerpoDescartar(25, 25));
    }

    // ---------------------------------------------------------------- I14 · cuándo se propone

    // El caso del hallazgo: descargas en 320 kbps un tema que tienes a 128.
    [Fact]
    public void Se_propone_cuando_la_descarga_tiene_mas_bitrate()
    {
        var vieja = Tema(Ruta("Musica", "Fisher - Losing It.mp3"), 128);
        var p = Proponer(Tema(Ruta("Bandeja", "Fisher - Losing It.mp3"), 320), vieja);

        Assert.NotNull(p);
        Assert.Same(vieja, p!.Vieja);
        Assert.Equal("320 kbps frente a 128 kbps", p.Motivo);
    }

    [Fact]
    public void Sin_perdida_frente_a_con_perdida_tambien()
    {
        var p = Proponer(Tema(Ruta("Bandeja", "Fisher - Losing It.aiff"), 1411), Tema(Ruta("Musica", "Fisher - Losing It.mp3"), 320));
        Assert.Equal("sin pérdida (AIFF) frente a 320 kbps", p?.Motivo);
    }

    // Un VBR V0 ronda los 245 kbps de media y no es peor que un CBR 256: hace falta un escalón entero.
    [Theory]
    [InlineData(256, 245)]
    [InlineData(320, 320)]
    [InlineData(192, 256)]
    public void Sin_un_escalon_de_bitrate_no_es_mejor(int nueva, int vieja)
        => Assert.Null(Proponer(Tema(Ruta("Bandeja", "a.mp3"), nueva), Tema(Ruta("Musica", "b.mp3"), vieja)));

    [Fact]
    public void Entre_dos_sin_perdida_no_se_propone()
        => Assert.Null(Proponer(Tema(Ruta("Bandeja", "a.flac"), 900), Tema(Ruta("Musica", "b.wav"), 1411)));

    [Fact]
    public void Con_perdida_frente_a_sin_perdida_no_se_propone()
        => Assert.Null(Proponer(Tema(Ruta("Bandeja", "a.mp3"), 320), Tema(Ruta("Musica", "b.flac"), 900)));

    // Una intro de DJ o una extended llevan las mismas etiquetas y duran bastante más.
    [Fact]
    public void Con_otra_duracion_no_es_la_misma_grabacion()
    {
        Assert.Null(Proponer(Tema(Ruta("Bandeja", "a.mp3"), 320, segundos: 260), Tema(Ruta("Musica", "b.mp3"), 128, segundos: 200)));
        Assert.NotNull(Proponer(Tema(Ruta("Bandeja", "a.mp3"), 320, segundos: 204), Tema(Ruta("Musica", "b.mp3"), 128, segundos: 200)));
    }

    [Fact]
    public void Sin_duracion_conocida_no_se_puede_comprobar()
        => Assert.Null(Proponer(Tema(Ruta("Bandeja", "a.mp3"), 320, segundos: 0), Tema(Ruta("Musica", "b.mp3"), 128)));

    // Con dos copias en la biblioteca no hay forma de saber cuál sustituir: eso es cosa de Duplicados.
    [Fact]
    public void Con_dos_copias_en_la_biblioteca_no_se_propone()
        => Assert.Null(Proponer(Tema(Ruta("Bandeja", "a.mp3"), 320),
                                Tema(Ruta("Musica", "b.mp3"), 128), Tema(Ruta("Musica", "Pack", "c.mp3"), 128)));

    // Los pools publican la versión clean y la explícita con el mismo artista y título.
    [Fact]
    public void Una_version_clean_no_sustituye_a_la_que_no_lo_es()
        => Assert.Null(Proponer(Tema(Ruta("Bandeja", "Fisher - Losing It (Clean).mp3"), 320), Tema(Ruta("Musica", "Fisher - Losing It.mp3"), 128)));

    [Fact]
    public void Otra_cancion_no_se_propone()
        => Assert.Null(Proponer(Tema(Ruta("Bandeja", "a.mp3"), 320, titulo: "Stop It"), Tema(Ruta("Musica", "b.mp3"), 128)));

    // ---------------------------------------------------------------- I14 · destino y textos

    [Fact]
    public void El_destino_es_la_carpeta_y_el_nombre_de_la_vieja_con_la_extension_de_la_nueva()
    {
        var vieja = Ruta("Musica", "House", "Fisher - Losing It.mp3");
        Assert.Equal(vieja, SustitucionBandeja.Destino(vieja, Ruta("Bandeja", "fisher_losing_it_320.mp3")));
        Assert.Equal(vieja, SustitucionBandeja.Destino(vieja, Ruta("Bandeja", "x.MP3")));   // misma ruta: rekordbox la sigue encontrando
        Assert.Equal(Ruta("Musica", "House", "Fisher - Losing It.aiff"), SustitucionBandeja.Destino(vieja, Ruta("Bandeja", "x.aiff")));
    }

    [Fact]
    public void Avisa_de_lo_que_pasa_con_rekordbox()
    {
        var vieja = Ruta("Musica", "a.mp3");
        Assert.Contains("La ruta no cambia", SustitucionBandeja.TextoRekordbox(vieja, vieja));
        var otra = SustitucionBandeja.TextoRekordbox(vieja, Ruta("Musica", "a.aiff"));
        Assert.Contains("MP3 → AIFF", otra);
        Assert.Contains("por perdida", otra);
    }

    [Fact]
    public void La_confirmacion_nombra_los_dos_archivos_y_su_calidad()
    {
        var (cuerpo, nota) = SustitucionBandeja.TextosConfirmacion(
            Tema(Ruta("Bandeja", "fisher 320.mp3"), 320), Tema(Ruta("Musica", "Fisher - Losing It.mp3"), 128));

        Assert.Contains("«fisher 320.mp3» (320 kbps)", cuerpo);
        Assert.Contains("«Fisher - Losing It.mp3» (128 kbps)", cuerpo);
        Assert.Contains("como «Fisher - Losing It.mp3»", cuerpo);
        Assert.Contains("rekordbox", nota);
        Assert.Contains("papelera", nota);
    }

    // ---------------------------------------------------------------- I14 · la ficha de DJ

    [Fact]
    public void La_ficha_fusiona_lo_de_las_dos_y_manda_lo_de_la_bandeja()
    {
        var vieja = new FichaDj
        {
            Energia = 7, Momentos = MomentoSet.Pico, Voz = TipoVoz.Vocal, Idioma = "EN",
            Ambiente = new() { "oscura" }, Etiquetas = new() { "boda", "verano" }, ArmaSecreta = true,
        };
        var nueva = new FichaDj { Energia = 9, Momentos = MomentoSet.Subida, Etiquetas = new() { "Boda", "club" } };

        var f = SustitucionBandeja.FusionarFichas(vieja, nueva)!;

        Assert.Equal(9, f.Energia);                                   // lo de la bandeja manda
        Assert.Equal(MomentoSet.Pico | MomentoSet.Subida, f.Momentos);
        Assert.Equal(TipoVoz.Vocal, f.Voz);                           // lo que la nueva no dice, de la vieja
        Assert.Equal("EN", f.Idioma);
        Assert.Equal(new[] { "oscura" }, f.Ambiente);
        Assert.Equal(new[] { "boda", "verano", "club" }, f.Etiquetas);   // sin repetir «Boda»
        Assert.True(f.ArmaSecreta);
    }

    // «Limpia» se decide escuchando: los pools publican la clean y la explícita con el mismo nombre.
    [Fact]
    public void La_letra_limpia_no_se_hereda_y_la_explicita_si()
    {
        Assert.Equal(TipoLetra.SinIndicar,
                     SustitucionBandeja.FusionarFichas(new FichaDj { Energia = 5, Letra = TipoLetra.Limpia }, null)!.Letra);
        Assert.Equal(TipoLetra.Explicita,
                     SustitucionBandeja.FusionarFichas(new FichaDj { Letra = TipoLetra.Explicita }, null)!.Letra);
        Assert.Equal(TipoLetra.Limpia,
                     SustitucionBandeja.FusionarFichas(new FichaDj { Letra = TipoLetra.Explicita }, new FichaDj { Letra = TipoLetra.Limpia })!.Letra);
    }

    [Fact]
    public void Sin_fichas_no_queda_ninguna()
    {
        Assert.Null(SustitucionBandeja.FusionarFichas(null, null));
        Assert.Null(SustitucionBandeja.FusionarFichas(new FichaDj { Letra = TipoLetra.Limpia }, null));   // solo quedaba lo que no se hereda
    }

    [Fact]
    public void La_ficha_acaba_en_el_destino_y_no_quedan_huerfanas()
    {
        var almacen = new AlmacenFichas(Ruta("fichas.json"));
        var vieja = Ruta("Musica", "Fisher - Losing It.mp3");
        var nueva = Ruta("Bandeja", "fisher 320.aiff");
        var destino = SustitucionBandeja.Destino(vieja, nueva);
        almacen.Poner(vieja, new FichaDj { Energia = 7 });
        almacen.Poner(nueva, new FichaDj { Etiquetas = new() { "boda" } });

        SustitucionBandeja.LlevarFicha(almacen, vieja, nueva, destino);

        var f = almacen.Obtener(destino);
        Assert.Equal(7, f?.Energia);
        Assert.Equal(new[] { "boda" }, f?.Etiquetas);
        Assert.Null(almacen.Obtener(vieja));
        Assert.Null(almacen.Obtener(nueva));
        Assert.Equal(1, almacen.Count);
    }

    // ---------------------------------------------------------------- I14 · en disco

    /// <summary>Una «papelera» de prueba: mueve el archivo a una carpeta aparte.</summary>
    private (bool, string) APapelera(string ruta)
    {
        var papelera = Directory.CreateDirectory(Ruta("Papelera")).FullName;
        File.Move(ruta, Path.Combine(papelera, Path.GetFileName(ruta)));
        return (true, "");
    }

    private (string Nueva, string Vieja, string Manifiesto) Preparar(string extNueva = ".mp3")
    {
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "fisher 320" + extNueva);
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica", "House")).FullName, "Fisher - Losing It.mp3");
        File.WriteAllText(nueva, "nueva");
        File.WriteAllText(vieja, "vieja");
        return (nueva, vieja, Ruta("deshacer", "run_20261019_120000.jsonl"));
    }

    private static Func<UndoRecord, string> AnotarEn(string manifiesto) => r =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifiesto)!);
        File.AppendAllText(manifiesto, JsonSerializer.Serialize(r) + "\n");
        return "";
    };

    private int Temporales() => Directory.GetFiles(Ruta("Musica", "House"), "*.tmp").Length;

    [Fact]
    public void Sustituir_deja_la_nueva_en_el_lugar_de_la_vieja_y_se_puede_deshacer()
    {
        var (nueva, vieja, man) = Preparar();

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, AnotarEn(man));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(vieja, r.Destino);
        Assert.Equal("nueva", File.ReadAllText(vieja));                          // la descarga, con el nombre de la vieja
        Assert.False(File.Exists(nueva));
        Assert.Equal("vieja", File.ReadAllText(Ruta("Papelera", "Fisher - Losing It.mp3")));   // la vieja, recuperable
        Assert.Equal(0, Temporales());

        var rec = JsonSerializer.Deserialize<UndoRecord>(Assert.Single(File.ReadAllLines(man)))!;
        Assert.Equal(nueva, rec.OrigPath);
        Assert.Equal(vieja, rec.FinalPath);
        Assert.True(rec.Renamed);
        Assert.False(rec.Aplicada);   // deshacerlo no saca nada de aplicadas.json

        // Deshacer devuelve la descarga a la bandeja; la vieja se recupera desde la papelera.
        var undo = new UndoEngine(new AppPaths(_dir)).UndoLastRun(man);
        Assert.Equal(1, undo.Reverted);
        Assert.Equal("nueva", File.ReadAllText(nueva));
        Assert.False(File.Exists(vieja));
    }

    [Fact]
    public void Con_otra_extension_la_nueva_conserva_la_suya()
    {
        var (nueva, vieja, man) = Preparar(".aiff");

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, AnotarEn(man));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Path.ChangeExtension(vieja, ".aiff"), r.Destino);
        Assert.Equal("nueva", File.ReadAllText(r.Destino));
        Assert.False(File.Exists(vieja));
    }

    // Una unidad sin papelera (memoria USB, red): enviar sería borrar para siempre, así que no se toca nada.
    [Fact]
    public void Si_la_papelera_se_niega_no_cambia_nada()
    {
        var (nueva, vieja, man) = Preparar();

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, _ => (false, "Está en una unidad extraíble (F:)."), AnotarEn(man));

        Assert.False(r.Ok);
        Assert.False(r.ViejaEnPapelera);
        Assert.Contains("unidad extraíble", r.Error);
        Assert.Contains("No se ha cambiado nada", r.Error);
        Assert.Equal("nueva", File.ReadAllText(nueva));
        Assert.Equal("vieja", File.ReadAllText(vieja));
        Assert.False(File.Exists(man));   // nada anotado: no hay nada que deshacer
        Assert.Equal(0, Temporales());
    }

    // Lo que no se puede anotar no se deja hecho: la descarga vuelve a la bandeja y se dice dónde está la vieja.
    [Fact]
    public void Si_no_se_puede_anotar_la_descarga_vuelve_a_la_bandeja()
    {
        var (nueva, vieja, _) = Preparar();

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, _ => "disco lleno");

        Assert.False(r.Ok);
        Assert.True(r.ViejaEnPapelera);
        Assert.Contains("disco lleno", r.Error);
        Assert.Contains("papelera", r.Error);
        Assert.Equal("nueva", File.ReadAllText(nueva));
        Assert.False(File.Exists(vieja));
        Assert.Equal(0, Temporales());
    }

    // Nunca se sobrescribe: con otra extensión, el destino puede estar ocupado por otro archivo.
    [Fact]
    public void Con_el_destino_ocupado_no_se_sobrescribe()
    {
        var (nueva, vieja, man) = Preparar(".aiff");
        File.WriteAllText(Path.ChangeExtension(vieja, ".aiff"), "otra");
        var llamadas = 0;

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, p => { llamadas++; return APapelera(p); }, AnotarEn(man));

        Assert.False(r.Ok);
        Assert.Equal(0, llamadas);
        Assert.Equal("otra", File.ReadAllText(Path.ChangeExtension(vieja, ".aiff")));
        Assert.Equal("vieja", File.ReadAllText(vieja));
        Assert.Equal("nueva", File.ReadAllText(nueva));
    }

    [Fact]
    public void Si_falta_algun_archivo_no_se_hace_nada()
    {
        var (nueva, vieja, man) = Preparar();
        File.Delete(vieja);
        var r = SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, AnotarEn(man));
        Assert.False(r.Ok);
        Assert.True(File.Exists(nueva));
        Assert.False(File.Exists(man));
    }
}
