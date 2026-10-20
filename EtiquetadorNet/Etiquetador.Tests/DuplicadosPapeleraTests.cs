using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgo E9: enviar copias a la papelera desde Duplicados.
///
/// (a) Agrupando por artista y título, marcar a mano 30 copias y enviar otra desde el clic derecho
///     rehacía la tabla y las 30 marcas desaparecían.
/// (b) Agrupando por audio, las 120 enviadas seguían en la tabla y marcadas; repetir el botón daba
///     «0 enviadas, 120 con problemas».
///
/// Ahora lo enviado se quita de los grupos que ya se ven, sin volver a agrupar (por audio serían
/// millones de comparaciones), y las marcas del resto se conservan.
/// </summary>
public class DuplicadosPapeleraTests
{
    private static Track Pista(string ruta, int kbps = 320, int segundos = 200)
        => new() { FilePath = ruta, Folder = @"E:\Musica", Bitrate = kbps, DurationSeconds = segundos };

    private static DuplicateGroup Grupo(string titulo, params Track[] copias)
        => new(titulo, "Quevedo", titulo, copias);

    private static (List<DupRow> Filas, int ConPrioridad) Filas(IReadOnlyList<DuplicateGroup> grupos,
                                                                 IReadOnlySet<string>? marcadas = null,
                                                                 bool porAudio = false,
                                                                 IReadOnlySet<string>? recomendadasMarcadas = null)
        => DuplicatesViewModel.ConstruirFilas(grupos, porAudio,
            puntuar: t => t.Bitrate * 1000.0 + t.DurationSeconds,
            motivoDe: t => $"Mejor calidad ({t.Bitrate} kbps)",
            esPrioritaria: _ => false,
            marcadas, recomendadasMarcadas);

    private static HashSet<string> Rutas(params Track[] pistas)
        => pistas.Select(p => p.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Quitar_las_enviadas_conserva_el_resto_de_cada_grupo()
    {
        var a1 = Pista(@"E:\Musica\Quevedo - Columbia.mp3");
        var a2 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (1).mp3", kbps: 192);
        var a3 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (2).mp3", kbps: 128);
        var b1 = Pista(@"E:\Musica\Quevedo - Punto G.mp3");
        var b2 = Pista(@"E:\Musica\Pack\Quevedo - Punto G.mp3", kbps: 128);
        var grupos = new[] { Grupo("Columbia", a1, a2, a3), Grupo("Punto G", b1, b2) };

        var quedan = DuplicatesViewModel.SinBorradas(grupos, new[] { a3.FilePath.ToUpperInvariant(), b2.FilePath });

        // Columbia sigue siendo un duplicado (dos copias); Punto G ya no: queda una sola.
        var g = Assert.Single(quedan);
        Assert.Equal("Columbia", g.Title);
        Assert.Equal(new[] { a1, a2 }, g.Tracks);
    }

    [Fact]
    public void Sin_nada_enviado_los_grupos_no_cambian()
    {
        var grupos = new[] { Grupo("Columbia", Pista(@"E:\a.mp3"), Pista(@"E:\b.mp3")) };
        Assert.Same(grupos, DuplicatesViewModel.SinBorradas(grupos, Array.Empty<string>()));
    }

    // (a) Las marcas hechas a mano sobreviven al repintado tras enviar otra copia a la papelera.
    [Fact]
    public void Repintar_tras_la_papelera_conserva_las_marcas_hechas_a_mano()
    {
        var a1 = Pista(@"E:\Musica\Quevedo - Columbia.mp3");
        var a2 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (1).mp3", kbps: 192);
        var a3 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (2).mp3", kbps: 128);
        var b1 = Pista(@"E:\Musica\Quevedo - Punto G.mp3");
        var b2 = Pista(@"E:\Musica\Pack\Quevedo - Punto G.mp3", kbps: 128);
        var b3 = Pista(@"E:\Musica\Pack\Quevedo - Punto G (1).mp3", kbps: 96);
        var grupos = new[] { Grupo("Columbia", a1, a2, a3), Grupo("Punto G", b1, b2, b3) };

        // El usuario había marcado a mano a2 y b3, y envía b2 desde el clic derecho.
        var marcadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a2.FilePath, b3.FilePath };
        var (filas, _) = Filas(DuplicatesViewModel.SinBorradas(grupos, new[] { b2.FilePath }), marcadas);

        Assert.DoesNotContain(filas, f => f.FilePath == b2.FilePath);
        Assert.Equal(new[] { a2.FilePath, b3.FilePath }, filas.Where(f => f.Marcada).Select(f => f.FilePath));
        // La etiqueta del grupo cuenta las copias que quedan.
        Assert.Contains(filas, f => f.FilePath == b1.FilePath && f.Group.EndsWith("(2 copias)", StringComparison.Ordinal));
    }

    // (b) Por audio: las enviadas desaparecen de la tabla, así que repetir el envío ya no las vuelve
    // a intentar ni las cuenta como «con problemas».
    [Fact]
    public void Por_audio_las_enviadas_desaparecen_y_no_quedan_marcadas()
    {
        var original = Pista(@"E:\Musica\Bad Bunny - Titi Me Pregunto.mp3");
        var copia = Pista(@"E:\Descargas\Bad Bunny - Titi Me Pregunto.mp3", kbps: 128);
        var intro = Pista(@"E:\Musica\Bad Bunny - Titi Me Pregunto (Hype Intro).mp3");
        var copiaIntro = Pista(@"E:\Descargas\Bad Bunny - Titi Me Pregunto (Hype Intro).mp3", kbps: 128);
        var grupos = new[] { Grupo("Titi Me Pregunto", original, copia, intro, copiaIntro) };

        var marcadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { copia.FilePath, copiaIntro.FilePath };
        var enviadas = new[] { copia.FilePath, copiaIntro.FilePath };
        var (filas, _) = Filas(DuplicatesViewModel.SinBorradas(grupos, enviadas), marcadas, porAudio: true);

        Assert.Equal(2, filas.Count);
        Assert.DoesNotContain(filas, f => f.Marcada);
        // Cada versión conserva su ejemplar: el original y la intro siguen siendo «la mejor».
        Assert.All(filas, f => Assert.True(f.EsMejor));
    }

    // Recalcular a petición del usuario empieza sin marcas, como siempre.
    [Fact]
    public void Sin_marcas_que_conservar_nada_sale_marcado()
    {
        var grupos = new[] { Grupo("Columbia", Pista(@"E:\a.mp3"), Pista(@"E:\b.mp3", kbps: 128)) };
        var (filas, _) = Filas(grupos);
        Assert.DoesNotContain(filas, f => f.Marcada);
        Assert.Single(filas, f => f.EsMejor);
    }

    // ---------- Ningún grupo se queda sin copias por repintar (revisión de E9) ----------
    //
    // Visto en revisión: «Marcar sobrantes» en un grupo de 3 y enviar después a la papelera, con el
    // clic derecho, la copia recomendada. Las dos que quedaban seguían marcadas, una pasaba a ser la
    // recomendada y «Enviar marcadas» se llevaba la canción entera.

    [Fact]
    public void Enviar_la_recomendada_desmarca_la_que_pasa_a_serlo()
    {
        var a1 = Pista(@"E:\Musica\Quevedo - Columbia.mp3");
        var a2 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (1).mp3", kbps: 192);
        var a3 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (2).mp3", kbps: 128);
        var grupos = new[] { Grupo("Columbia", a1, a2, a3) };

        // «Marcar sobrantes» dejó marcadas a2 y a3; luego a1 va a la papelera desde el clic derecho.
        var (filas, _) = Filas(DuplicatesViewModel.SinBorradas(grupos, new[] { a1.FilePath }), Rutas(a2, a3));

        var nueva = Assert.Single(filas, f => f.EsMejor);
        Assert.Equal(a2.FilePath, nueva.FilePath);
        Assert.False(nueva.Marcada);
        Assert.Equal(new[] { a3.FilePath }, filas.Where(f => f.Marcada).Select(f => f.FilePath));
        Assert.Equal(0, DuplicatesViewModel.SinNingunaCopia(filas));
    }

    // Si queda otra copia sin marcar, las marcas del usuario se respetan tal cual: el grupo no se
    // queda vacío y es él quien ha decidido cuál conservar.
    [Fact]
    public void Si_queda_una_copia_sin_marcar_no_se_desmarca_nada()
    {
        var a1 = Pista(@"E:\Musica\Quevedo - Columbia.mp3");
        var a2 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (1).mp3", kbps: 256);
        var a3 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (2).mp3", kbps: 192);
        var a4 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (3).mp3", kbps: 128);
        var grupos = new[] { Grupo("Columbia", a1, a2, a3, a4) };

        var (filas, _) = Filas(DuplicatesViewModel.SinBorradas(grupos, new[] { a1.FilePath }), Rutas(a2, a4));

        Assert.Equal(new[] { a2.FilePath, a4.FilePath }, filas.Where(f => f.Marcada).Select(f => f.FilePath));
        Assert.Equal(0, DuplicatesViewModel.SinNingunaCopia(filas));
    }

    // Una recomendada que el usuario ya había marcado se marcó a sabiendas: sigue marcada, y el
    // diálogo de la papelera avisa de que la canción desaparecería.
    [Fact]
    public void Una_recomendada_marcada_a_sabiendas_sigue_marcada_y_el_dialogo_lo_avisa()
    {
        var a1 = Pista(@"E:\Musica\Quevedo - Columbia.mp3");
        var a2 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (1).mp3", kbps: 192);
        var b1 = Pista(@"E:\Musica\Quevedo - Punto G.mp3");
        var b2 = Pista(@"E:\Musica\Pack\Quevedo - Punto G.mp3", kbps: 128);
        var b3 = Pista(@"E:\Musica\Pack\Quevedo - Punto G (1).mp3", kbps: 96);
        var grupos = new[] { Grupo("Columbia", a1, a2), Grupo("Punto G", b1, b2, b3) };

        // Todo Columbia marcado, a1 incluida como recomendada; se envía b3 desde el clic derecho.
        var (filas, _) = Filas(DuplicatesViewModel.SinBorradas(grupos, new[] { b3.FilePath }),
                               Rutas(a1, a2), recomendadasMarcadas: Rutas(a1));

        Assert.Equal(new[] { a1.FilePath, a2.FilePath }, filas.Where(f => f.Marcada).Select(f => f.FilePath));
        Assert.Equal(1, DuplicatesViewModel.SinNingunaCopia(filas));
        Assert.Equal("Afecta a 1 grupo(s) de duplicados. Atención: 1 de ellas es la copia recomendada de su grupo. "
                     + "Hay 1 canción con TODAS sus copias marcadas: desaparecería de la biblioteca.",
                     DuplicatesViewModel.CuerpoConfirmarPapelera(filas));
    }

    // Por audio, cada versión conserva su ejemplar: se protege la versión, no solo el grupo.
    [Fact]
    public void Por_audio_se_protege_cada_version()
    {
        var original = Pista(@"E:\Musica\Bad Bunny - Titi Me Pregunto.mp3");
        var copia = Pista(@"E:\Descargas\Bad Bunny - Titi Me Pregunto.mp3", kbps: 128);
        var intro = Pista(@"E:\Musica\Bad Bunny - Titi Me Pregunto (Hype Intro).mp3");
        var copiaIntro = Pista(@"E:\Descargas\Bad Bunny - Titi Me Pregunto (Hype Intro).mp3", kbps: 128);
        var grupos = new[] { Grupo("Titi Me Pregunto", original, copia, intro, copiaIntro) };

        // «Marcar sobrantes» marcó una copia de cada versión; luego se envía el original.
        var (filas, _) = Filas(DuplicatesViewModel.SinBorradas(grupos, new[] { original.FilePath }),
                               Rutas(copia, copiaIntro), porAudio: true);

        Assert.False(filas.Single(f => f.FilePath == copia.FilePath).Marcada);       // única de su versión
        Assert.True(filas.Single(f => f.FilePath == copiaIntro.FilePath).Marcada);   // la intro sigue teniendo la suya
        Assert.Equal(0, DuplicatesViewModel.SinNingunaCopia(filas));
    }

    // Dos grabaciones distintas con el mismo nombre dan la misma etiqueta de grupo: se cuentan por
    // grupo, no por etiqueta.
    [Fact]
    public void El_dialogo_cuenta_los_grupos_aunque_tengan_la_misma_etiqueta()
    {
        var a1 = Pista(@"E:\Musica\Quevedo - Columbia.mp3");
        var a2 = Pista(@"E:\Pack\Quevedo - Columbia.mp3", kbps: 128);
        var c1 = Pista(@"E:\Directos\Quevedo - Columbia.mp3");
        var c2 = Pista(@"E:\Directos\Pack\Quevedo - Columbia.mp3", kbps: 128);
        var grupos = new[] { Grupo("Columbia", a1, a2), Grupo("Columbia", c1, c2) };
        var (filas, _) = Filas(grupos, Rutas(a1, a2, c1, c2), recomendadasMarcadas: Rutas(a1, c1));

        Assert.Equal(filas[0].Group, filas[2].Group);
        Assert.Equal(2, DuplicatesViewModel.SinNingunaCopia(filas));
        Assert.StartsWith("Afecta a 2 grupo(s) de duplicados.", DuplicatesViewModel.CuerpoConfirmarPapelera(filas));
        Assert.Contains("Hay 2 canciones con TODAS sus copias marcadas", DuplicatesViewModel.CuerpoConfirmarPapelera(filas));
    }

    [Fact]
    public void Sin_nada_raro_el_dialogo_solo_dice_cuantos_grupos()
    {
        var a1 = Pista(@"E:\Musica\Quevedo - Columbia.mp3");
        var a2 = Pista(@"E:\Musica\Pack\Quevedo - Columbia (1).mp3", kbps: 192);
        var (filas, _) = Filas(new[] { Grupo("Columbia", a1, a2) }, Rutas(a2));
        Assert.Equal("Afecta a 1 grupo(s) de duplicados.", DuplicatesViewModel.CuerpoConfirmarPapelera(filas));
    }

    [Fact]
    public void El_estado_dice_cuantas_se_han_desmarcado()
    {
        Assert.Equal("", DuplicatesViewModel.NotaDesmarcadas(0));
        Assert.StartsWith(" Se ha desmarcado 1 copia:", DuplicatesViewModel.NotaDesmarcadas(1));
        Assert.StartsWith(" Se han desmarcado 3 copias:", DuplicatesViewModel.NotaDesmarcadas(3));
    }

    // ---------- Lo que se le dice al usuario ----------

    [Fact]
    public void Todas_enviadas_lo_dice_sin_mas()
        => Assert.Equal("120 copias enviadas a la papelera.",
                        DuplicatesViewModel.ResumenPapelera(120, Array.Empty<(string, string)>()));

    // Si la papelera se niega por un motivo común (por ejemplo, una unidad sin papelera, donde
    // «enviar» sería borrar para siempre), ese motivo es lo que hay que leer, no un recuento.
    [Fact]
    public void Si_todas_fallan_por_lo_mismo_se_dice_el_motivo()
    {
        const string motivo = "La unidad E: no tiene papelera; enviarlo ahí lo borraría para siempre.";
        var fallos = Enumerable.Range(0, 120).Select(i => ($"copia {i}.mp3", motivo)).ToList();

        Assert.Equal("No se ha enviado ninguna copia a la papelera: " + motivo,
                     DuplicatesViewModel.ResumenPapelera(0, fallos));
        Assert.Equal("3 copias enviadas a la papelera · 120 no se han enviado: " + motivo,
                     DuplicatesViewModel.ResumenPapelera(3, fallos));
    }

    [Fact]
    public void Con_motivos_distintos_se_dice_el_primero()
    {
        var fallos = new List<(string, string)>
        {
            ("a.mp3", "El archivo está en uso por otro programa."),
            ("b.mp3", "Acceso denegado."),
        };
        Assert.Equal("5 enviadas, 2 con problemas. La primera: a.mp3: El archivo está en uso por otro programa.",
                     DuplicatesViewModel.ResumenPapelera(5, fallos));
    }

    [Fact]
    public void Un_fallo_sin_motivo_no_deja_el_mensaje_cortado()
        => Assert.EndsWith("motivo desconocido.",
                           DuplicatesViewModel.ResumenPapelera(0, new List<(string, string)> { ("a.mp3", "") }));
}
