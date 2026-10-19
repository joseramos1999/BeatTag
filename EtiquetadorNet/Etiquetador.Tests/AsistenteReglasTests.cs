using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Las dos herramientas del Asistente que funcionan solo con reglas: «Limpiar la publicidad de los
/// packs» (P8) y «Género por carpeta y por artista» (P9). Qué proponen, qué sale marcado, que no
/// escriben lo que el usuario cambió después, que lo escrito se puede deshacer y que, si el historial
/// falla, no se toca nada más.
/// </summary>
public class AsistenteReglasTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Track T(string ruta, string? artista = null, string? titulo = null, string? album = null, string? genero = null)
        => new() { FilePath = ruta, Artist = artista, Title = titulo, Album = album, Genre = genero };

    /// <summary>Canciones que dan a conocer artistas al índice (cada uno en dos, el mínimo).</summary>
    private static IEnumerable<Track> Conocidos(params string[] artistas)
        => artistas.SelectMany((a, i) => new[]
        {
            T(Rutas.De("lib", $"conocido{i}a.mp3"), a, "Tema A"),
            T(Rutas.De("lib", $"conocido{i}b.mp3"), a, "Tema B"),
        });

    private string Mp3(string nombre, string? album = null, string[]? artistas = null, string? comentario = null, string[]? generos = null)
    {
        var ruta = Path.Combine(_dir, nombre);
        Mp3Fixture.WriteMinMp3(ruta);
        using var f = TagLib.File.Create(ruta);
        if (album != null) f.Tag.Album = album;
        if (artistas != null) f.Tag.Performers = artistas;
        if (comentario != null) f.Tag.Comment = comentario;
        if (generos != null) f.Tag.Genres = generos;
        f.Save();
        return ruta;
    }

    /// <summary>Un historial que deja de poder escribirse en la anotación número <c>fallaEn</c>.</summary>
    private sealed class HistorialQueFalla : IHistorialDeshacer
    {
        private readonly int _fallaEn;
        public readonly List<UndoRecord> Anotados = new();
        private int _llamadas;
        public HistorialQueFalla(int fallaEn = int.MaxValue) => _fallaEn = fallaEn;

        public string Anotar(UndoRecord registro)
        {
            if (++_llamadas >= _fallaEn) return "No hay espacio suficiente en el disco.";
            Anotados.Add(registro);
            return "";
        }
    }

    // =================================================================================================
    // P8 · Qué es publicidad
    // =================================================================================================

    [Theory]
    [InlineData("Latin Box 15 12 2025", "«Latin Box» es un record pool")]
    [InlineData("UNLIMITED LATIN", "«UNLIMITED LATIN» es un record pool")]
    [InlineData("Dj City Latino 06 07 2023", "es un record pool")]
    [InlineData("@tryitmusic", "usuario de redes")]
    [InlineData("  @Rodrigmzz @Paquillocastrodj", "usuario de redes")]
    public void P8_un_album_que_es_un_pool_o_un_usuario_es_publicidad_fuerte(string album, string motivo)
    {
        var porQue = LimpiezaPacks.PorQueAlbum(album, out var fuerte);
        Assert.NotNull(porQue);
        Assert.Contains(motivo, porQue);
        Assert.True(fuerte);
    }

    // Visto en una biblioteca real: «El Cangri.com» (Daddy Yankee) y «souljaboytellem.com» (Soulja Boy)
    // son álbumes de verdad. Una web se propone, pero desmarcada.
    [Theory]
    [InlineData("El Cangri.com")]
    [InlineData("www.rmxvip.com")]
    [InlineData("LatinRemixes.com 05 04 2023")]
    public void P8_un_album_que_parece_una_web_se_propone_pero_no_es_fuerte(string album)
    {
        Assert.NotNull(LimpiezaPacks.PorQueAlbum(album, out var fuerte));
        Assert.False(fuerte);
    }

    // «Try It» no está en PoolRe a propósito: es un editor que firma sus ediciones, no un pool.
    [Theory]
    [InlineData("Try It Mashups Vol. 3")]
    [InlineData("Barrio Fino")]
    [InlineData("")]
    [InlineData(null)]
    public void P8_un_album_normal_no_se_toca(string? album)
        => Assert.Null(LimpiezaPacks.PorQueAlbum(album, out _));

    [Theory]
    [InlineData("@liyo_dj98", true)]
    [InlineData("@liyo_dj98 @roseemdj", true)]
    [InlineData("KEVIN ROLDAN (Unlimited Latin Live Edit 84Bpm)", true)]
    [InlineData("Bad Bunny", false)]
    [InlineData("Try It", false)]
    [InlineData("", false)]
    public void P8_artista_que_es_publicidad(string artista, bool esperado)
        => Assert.Equal(esperado, LimpiezaPacks.ArtistaEsPublicidad(artista));

    // =================================================================================================
    // P8 · El artista del nombre del archivo, solo si es fiable
    // =================================================================================================

    private static LimpiezaPacks.ArtistaLeido? Leido(string archivo, string? titulo, params string[] conocidos)
    {
        var t = T(Rutas.De("pack", archivo), "@editor", titulo);
        var indice = new IndiceArtistas(Conocidos(conocidos).Append(t));
        return LimpiezaPacks.ArtistaDelNombre(t, indice);
    }

    [Fact]
    public void P8_artistas_delante_del_guion_que_la_biblioteca_conoce()
    {
        var r = Leido("Khea, Cazzu - Loca x Firework (Eneko Rivas Transition 116-126Bpm).mp3", null, "Khea", "Cazzu");
        Assert.NotNull(r);
        Assert.Equal("Khea, Cazzu", r!.Artista);
        Assert.Contains("«Khea» ya es artista de 2 canciones", r.PorQue);
    }

    // Los mashups van a menudo «Títulos - Artistas». Tomar siempre lo de delante pondría los títulos.
    [Fact]
    public void P8_en_un_mashup_los_artistas_pueden_ir_detras_del_guion()
    {
        var r = Leido("La Ocasion x Heroes - De La Ghetto, Arcangel.mp3", null, "De La Ghetto", "Arcangel");
        Assert.NotNull(r);
        Assert.Equal("De La Ghetto, Arcangel", r!.Artista);
        Assert.Contains("detrás del guion", r.PorQue);
    }

    // Detrás del guion está el final del nombre, que es lo que cortan los pools: «… & B» no es un artista.
    [Fact]
    public void P8_un_nombre_cortado_por_detras_no_da_artista()
        => Assert.Null(Leido("Helicoptero x Las 12 - Farruko, Ana Mena & B.mp3", null, "Farruko", "Ana Mena"));

    [Fact]
    public void P8_si_ningun_lado_son_artistas_conocidos_no_se_propone_nada()
        => Assert.Null(Leido("La Prendo x Superman Sin Capa - Bad Gyal x E.mp3", null, "Ozuna"));

    [Fact]
    public void P8_sin_guion_no_se_sabe_cual_es_el_artista()
        => Assert.Null(Leido("Hasta El Amanecer x Yandel 150 (Try It Mashup).mp3", null, "Yandel"));

    // Un artista que no está en ninguna otra canción vale si el título de las etiquetas confirma el
    // orden «Artista - Título», y no si confirma el contrario.
    [Fact]
    public void P8_el_titulo_de_las_etiquetas_confirma_el_orden()
    {
        var bien = Leido("Nuevo Artista - Cancion Unica.mp3", "Cancion Unica");
        Assert.Equal("Nuevo Artista", bien?.Artista);
        Assert.Contains("título de las etiquetas", bien!.PorQue);

        Assert.Null(Leido("Cancion Unica - Nuevo Artista.mp3", "Cancion Unica"));
        Assert.Null(Leido("Nuevo Artista - Cancion Unica.mp3", null));
    }

    // Visto en una biblioteca real: varias canciones tenían «Remix» como artista, y «REMIX» pasaba por
    // un artista conocido.
    [Fact]
    public void P8_un_credito_que_solo_dice_la_version_no_es_un_artista()
        => Assert.Null(Leido("Caile x Xclusivo - REMIX.mp3", null, "Remix"));

    [Fact]
    public void P8_con_una_sola_cancion_un_artista_no_cuenta_como_conocido()
    {
        var unica = T(Rutas.De("lib", "x.mp3"), "Gatubela x Rompe", "Algo");
        var indice = new IndiceArtistas(new[] { unica });
        Assert.False(indice.Conoce("Gatubela"));
        Assert.Equal(1, indice.Canciones("Gatubela"));
    }

    [Fact]
    public void P8_la_x_final_de_un_nombre_no_se_quita()
    {
        var r = Leido("Lil Nas X - Old Town Road.mp3", null, "Lil Nas X");
        Assert.Equal("Lil Nas X", r?.Artista);
    }

    // Dos de tres nombres conocidos bastarían delante del guion, pero «Quevedo Remix» delata que el
    // texto lleva la versión: escribirlo así en el Artista no sería limpiarlo.
    [Fact]
    public void P8_la_version_sin_parentesis_delata_que_no_es_solo_el_artista()
        => Assert.Null(Leido("Bad Bunny, Feid, Quevedo Remix - Tema Nuevo.mp3", null, "Bad Bunny", "Feid"));

    [Fact]
    public void P8_el_indice_parte_los_creditos_y_quita_la_version()
    {
        Assert.Equal(new[] { "Ozuna", "Daddy Yankee", "Quevedo" },
                     IndiceArtistas.Partir("Ozuna, Daddy Yankee Ft Quevedo (Bayne Mashup 92Bpm)"));
        Assert.Equal(new[] { "Bad Bunny", "Feid" }, IndiceArtistas.Partir("Bad Bunny x Feid"));
        Assert.Equal(new[] { "Lil Nas X" }, IndiceArtistas.Partir("Lil Nas X"));
    }

    // =================================================================================================
    // P8 · Las propuestas
    // =================================================================================================

    [Fact]
    public void P8_proponer_saca_una_fila_por_etiqueta_y_marca_solo_lo_fuerte()
    {
        var canciones = Conocidos("De La Ghetto", "Arcangel").Concat(new[]
        {
            T(Rutas.De("pack", "La Ocasion x Heroes - De La Ghetto, Arcangel.mp3"), "@mykeroldan", null, "Latin Box 15 12 2025"),
            T(Rutas.De("pack", "Gasolina.mp3"), "Daddy Yankee", "Gasolina", "El Cangri.com"),
            T(Rutas.De("pack", "La Prendo x Superman - Bad Gyal x E.mp3"), "@mykeroldan", null, "Barrio Fino"),
        }).ToList();

        var p = LimpiezaPacks.Proponer(canciones);

        var album = Assert.Single(p, x => x.Campo == CampoPublicidad.Album && x.Actual == "Latin Box 15 12 2025");
        Assert.True(album.Marcada);
        Assert.Equal("", album.Nuevo);
        var web = Assert.Single(p, x => x.Actual == "El Cangri.com");
        Assert.False(web.Marcada);
        var artista = Assert.Single(p, x => x.Campo == CampoPublicidad.Artista);
        Assert.Equal("De La Ghetto, Arcangel", artista.Nuevo);
        Assert.True(artista.Marcada);
        Assert.Contains("usuario de redes", artista.PorQue);
        Assert.DoesNotContain(p, x => x.Actual == "Barrio Fino");
        Assert.Equal(1, LimpiezaPacks.ArtistasSinPropuesta(canciones, p));   // «La Prendo x Superman…»
    }

    // =================================================================================================
    // P8 · El comentario
    // =================================================================================================

    [Fact]
    public void P8_el_pack_se_anade_al_final_del_comentario_sin_repetirse()
    {
        var bloque = LimpiezaPacks.BloquePack("Latin Box 15 12 2025");
        Assert.Equal("[Pack: Latin Box 15 12 2025]", bloque);
        Assert.Equal(bloque, LimpiezaPacks.ComentarioCon(null, bloque));
        Assert.Equal("8A - Energy 6 " + bloque, LimpiezaPacks.ComentarioCon("8A - Energy 6", bloque));
        Assert.Equal("8A - Energy 6 " + bloque, LimpiezaPacks.ComentarioCon("8A - Energy 6 " + bloque, bloque));
        Assert.Equal("[Pack: Raro (1)]", LimpiezaPacks.BloquePack("Raro (1]"));   // un «]» cerraría el bloque antes de tiempo
    }

    // La ficha de DJ se vuelca como «[DJ: …]» al final y se busca ahí: el pack va delante, y un
    // volcado posterior de la ficha lo conserva.
    [Fact]
    public void P8_el_pack_va_delante_de_la_ficha_de_dj_y_la_ficha_lo_conserva()
    {
        var bloque = LimpiezaPacks.BloquePack("Latin Box");
        var con = LimpiezaPacks.ComentarioCon("mi nota [DJ: E7 · Pico]", bloque);
        Assert.Equal("mi nota [Pack: Latin Box] [DJ: E7 · Pico]", con);

        var revolcado = Fichas.ComentarioCombinado(con, new FichaDj { Energia = 5 });
        Assert.Contains("[Pack: Latin Box]", revolcado);
        Assert.EndsWith("[DJ: E5]", revolcado);
    }

    // =================================================================================================
    // P8 · Los cambios por archivo y los textos
    // =================================================================================================

    [Fact]
    public void P8_el_album_y_el_artista_de_un_mismo_archivo_van_en_un_solo_cambio()
    {
        var ruta = Rutas.De("pack", "a.mp3");
        var filas = new[]
        {
            new PublicidadFila(new PropuestaPublicidad(ruta, CampoPublicidad.Album, "Latin Box", "", "pool", true), true),
            new PublicidadFila(new PropuestaPublicidad(ruta, CampoPublicidad.Artista, "@x", "Bad Bunny", "usuario", true), true),
            new PublicidadFila(new PropuestaPublicidad(Rutas.De("pack", "b.mp3"), CampoPublicidad.Album, "@y", "", "usuario", true), true),
        };

        var cambios = LimpiarPacksViewModel.Cambios(filas, alComentario: true);

        Assert.Equal(2, cambios.Count);
        var a = cambios.Single(c => c.Ruta == ruta);
        Assert.Equal("Latin Box", a.AlbumVisto);
        Assert.Equal("", a.AlbumNuevo);
        Assert.Equal("@x", a.ArtistaVisto);
        Assert.Equal("Bad Bunny", a.ArtistaNuevo);
        Assert.True(a.GuardarEnComentario);
    }

    [Fact]
    public void P8_un_artista_vaciado_a_mano_no_se_aplica_y_lo_que_se_quita_sigue_la_opcion()
    {
        var fila = new PublicidadFila(new PropuestaPublicidad(Rutas.De("p", "a.mp3"), CampoPublicidad.Artista, "@liyo_dj98", "Bad Bunny", "u", true), true);
        Assert.Equal("Al comentario: [Editor: @liyo_dj98]", fila.LoQueSeQuita);
        fila.AlComentario = false;
        Assert.Equal("Se borra", fila.LoQueSeQuita);
        var pool = new PublicidadFila(new PropuestaPublicidad(Rutas.De("p", "b.mp3"), CampoPublicidad.Artista, "K-Narias (Unlimited Latin Live Edit)", "K-Narias", "p", true), true);
        Assert.StartsWith("Se borra", pool.LoQueSeQuita);
    }

    [Fact]
    public void P8_la_confirmacion_avisa_de_lo_dudoso_de_los_duplicados_y_de_rekordbox()
    {
        var filas = new[]
        {
            new PublicidadFila(new PropuestaPublicidad(Rutas.De("p", "a.mp3"), CampoPublicidad.Album, "El Cangri.com", "", "web", false), true),
            new PublicidadFila(new PropuestaPublicidad(Rutas.De("p", "a.mp3"), CampoPublicidad.Artista, "@x", "Daddy Yankee", "u", true), true),
        };
        var (cuerpo, nota) = LimpiarPacksViewModel.Confirmacion(filas, alComentario: true);
        Assert.Contains("1 canciones: 1 álbumes y 1 artistas", cuerpo);
        Assert.Contains("⚠ 1 no salían marcadas", nota);
        Assert.Contains("Duplicados y la Bandeja", nota);
        Assert.Contains("«Recargar etiqueta»", nota);
        Assert.Contains("comentario", nota);
        Assert.Contains("Recargar etiqueta", LimpiarPacksViewModel.AvisoTrasAplicar(0));
        Assert.DoesNotContain("Duplicados", LimpiarPacksViewModel.AvisoTrasAplicar(0));
        Assert.Contains("Duplicados", LimpiarPacksViewModel.AvisoTrasAplicar(3));
    }

    // =================================================================================================
    // Escribir: cálculo, respeto de lo editado después, deshacer e historial
    // =================================================================================================

    [Fact]
    public void Escribir_limpia_el_album_y_el_artista_y_guarda_lo_quitado_en_el_comentario()
    {
        var ruta = Mp3("a.mp3", "Latin Box 15 12 2025", new[] { "@liyo_dj98" }, "8A - Energy 6");
        var historial = new HistorialQueFalla();
        var cambio = new CambioEtiquetas(ruta)
        {
            AlbumVisto = "Latin Box 15 12 2025", ArtistaVisto = "@liyo_dj98", ArtistaNuevo = "Rauw Alejandro, Farruko", GuardarEnComentario = true,
        };

        var res = EscrituraPorReglas.Escribir(new[] { cambio }, historial, "", null, null, null, CancellationToken.None, "prueba");

        Assert.Equal(new[] { ruta }, res.Escritos);
        using (var f = TagLib.File.Create(ruta))
        {
            Assert.True(string.IsNullOrEmpty(f.Tag.Album));
            Assert.Equal(new[] { "Rauw Alejandro, Farruko" }, f.Tag.Performers);   // un solo valor, como en el nombre
            Assert.Equal("8A - Energy 6 [Pack: Latin Box 15 12 2025] [Editor: @liyo_dj98]", f.Tag.Comment);
        }
        var registro = Assert.Single(historial.Anotados);
        Assert.Equal(new[] { "Album", "Artist", "Comment" }, registro.Fields!.Keys.OrderBy(k => k));
        Assert.False(registro.Aplicada);   // deshacerlo no devuelve canciones a «pendientes»
    }

    [Fact]
    public void Escribir_borrando_no_toca_el_comentario()
    {
        var ruta = Mp3("a.mp3", "@tryitmusic", null, "mi nota");
        var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { AlbumVisto = "@tryitmusic" } },
                                              new HistorialQueFalla(), "", null, null, null, CancellationToken.None, "prueba");
        Assert.Single(res.Escritos);
        using var f = TagLib.File.Create(ruta);
        Assert.True(string.IsNullOrEmpty(f.Tag.Album));
        Assert.Equal("mi nota", f.Tag.Comment);
    }

    // Lo que el usuario cambió después de proponer (en el Editor, en rekordbox…) se respeta.
    [Fact]
    public void Escribir_respeta_lo_que_cambio_despues_de_proponer()
    {
        var ruta = Mp3("a.mp3", "Mi album de verdad", new[] { "Bad Bunny" });
        var historial = new HistorialQueFalla();
        var cambio = new CambioEtiquetas(ruta) { AlbumVisto = "Latin Box", ArtistaVisto = "@x", ArtistaNuevo = "Otro" };

        var res = EscrituraPorReglas.Escribir(new[] { cambio }, historial, "", null, null, null, CancellationToken.None, "prueba");

        Assert.Empty(res.Escritos);
        Assert.Equal(new[] { ruta }, res.Respetados);
        Assert.Empty(historial.Anotados);
        using var f = TagLib.File.Create(ruta);
        Assert.Equal("Mi album de verdad", f.Tag.Album);
        Assert.Equal(new[] { "Bad Bunny" }, f.Tag.Performers);
    }

    [Fact]
    public void Escribir_nunca_pisa_un_genero_y_escribe_las_listas_como_varios_valores()
    {
        var con = Mp3("con.mp3", generos: new[] { "Salsa" });
        var sin = Mp3("sin.mp3");
        var res = EscrituraPorReglas.Escribir(new[]
            {
                new CambioEtiquetas(con) { GeneroNuevo = "Reggaeton" },
                new CambioEtiquetas(sin) { GeneroNuevo = "Hip Hop; Latino" },
            }, new HistorialQueFalla(), "", null, null, null, CancellationToken.None, "prueba");

        Assert.Equal(new[] { sin }, res.Escritos);
        Assert.Equal(new[] { con }, res.Respetados);
        using (var f = TagLib.File.Create(con)) Assert.Equal(new[] { "Salsa" }, f.Tag.Genres);
        using (var f = TagLib.File.Create(sin)) Assert.Equal(new[] { "Hip Hop", "Latino" }, f.Tag.Genres);
    }

    [Fact]
    public void Escribir_se_para_en_cuanto_el_historial_falla_y_no_toca_el_resto()
    {
        var a = Mp3("a.mp3", "Latin Box"); var b = Mp3("b.mp3", "Latin Box"); var c = Mp3("c.mp3", "Latin Box");
        var cambios = new[] { a, b, c }.Select(r => new CambioEtiquetas(r) { AlbumVisto = "Latin Box" }).ToList();

        var res = EscrituraPorReglas.Escribir(cambios, new HistorialQueFalla(fallaEn: 2), "", null, null, null, CancellationToken.None, "prueba");

        Assert.Equal(new[] { a }, res.Escritos);
        Assert.True(res.Cancelado);
        Assert.Contains("«b.mp3»", res.Detenido);
        Assert.Equal("", res.UndoErr);
        using (var f = TagLib.File.Create(b)) Assert.Equal("Latin Box", f.Tag.Album);
        using (var f = TagLib.File.Create(c)) Assert.Equal("Latin Box", f.Tag.Album);
    }

    [Fact]
    public void Escribir_sin_historial_no_toca_ningun_archivo()
    {
        var a = Mp3("a.mp3", "Latin Box");
        var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(a) { AlbumVisto = "Latin Box" } }, null, "Acceso denegado.",
                                              null, null, null, CancellationToken.None, "prueba");
        Assert.Empty(res.Escritos);
        Assert.StartsWith("No se ha tocado ningún archivo", res.Detenido);
        using var f = TagLib.File.Create(a);
        Assert.Equal("Latin Box", f.Tag.Album);
    }

    // Solo cambian etiquetas: las cachés de audio reciben el sello de ANTES de escribir.
    [Fact]
    public void Escribir_avisa_a_las_caches_con_el_sello_de_antes()
    {
        var a = Mp3("a.mp3", "Latin Box");
        var antes = SelloArchivo.De(a);
        var avisos = new List<(string De, string A, SelloArchivo Sello)>();
        EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(a) { AlbumVisto = "Latin Box" } }, new HistorialQueFalla(), "",
                                    (de, para, s) => avisos.Add((de, para, s)), null, null, CancellationToken.None, "prueba");
        var aviso = Assert.Single(avisos);
        Assert.Equal(a, aviso.De);
        Assert.Equal(a, aviso.A);
        Assert.Equal(antes, aviso.Sello);
    }

    // De principio a fin, con el manifiesto y el UndoEngine de verdad: «Deshacer última» lo devuelve todo.
    [Fact]
    public void Lo_escrito_se_deshace_con_deshacer_ultima()
    {
        var paths = new AppPaths(Path.Combine(_dir, "datos"));
        var ruta = Mp3("a.mp3", "Latin Box 15 12 2025", new[] { "@liyo_dj98" }, "8A - Energy 6", Array.Empty<string>());
        var cambio = new CambioEtiquetas(ruta)
        {
            AlbumVisto = "Latin Box 15 12 2025", ArtistaVisto = "@liyo_dj98", ArtistaNuevo = "Bad Bunny", GeneroNuevo = "Reggaeton", GuardarEnComentario = true,
        };
        using (var m = ManifiestoDeshacer.Abrir(paths.NuevoManifiesto(), out var error))
            EscrituraPorReglas.Escribir(new[] { cambio }, m, error, null, null, null, CancellationToken.None, "prueba");

        var u = new UndoEngine(paths).UndoLastRun();

        Assert.Equal(1, u.Reverted);
        Assert.Empty(u.AplicadasRevertidas);
        using var f = TagLib.File.Create(ruta);
        Assert.Equal("Latin Box 15 12 2025", f.Tag.Album);
        Assert.Equal(new[] { "@liyo_dj98" }, f.Tag.Performers);
        Assert.Equal("8A - Energy 6", f.Tag.Comment);
        Assert.Empty(f.Tag.Genres);
    }

    // MEDIDO con TagLib#: en ID3v2.3 «AC/DC» se lee como «AC» y «DC». Lo anotado tiene que ser lo que
    // quedó en el archivo, o deshacer lo daría por editado a mano y no lo devolvería.
    [Fact]
    public void Un_valor_con_barra_se_anota_como_quedo_y_tambien_se_deshace()
    {
        var paths = new AppPaths(Path.Combine(_dir, "datos"));
        var ruta = Mp3("a.mp3", artistas: new[] { "@editor" });
        using (var m = ManifiestoDeshacer.Abrir(paths.NuevoManifiesto(), out var error))
        {
            var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { ArtistaVisto = "@editor", ArtistaNuevo = "AC/DC" } },
                                                  m, error, null, null, null, CancellationToken.None, "prueba");
            Assert.Equal("", res.UndoErr);
            string[] quedo;
            using (var f0 = TagLib.File.Create(ruta)) quedo = f0.Tag.Performers;
            // Si el archivo lo partió, hay un segundo registro con lo que de verdad quedó.
            Assert.Equal(quedo.SequenceEqual(new[] { "AC/DC" }) ? 1 : 2, m!.Anotados);
        }

        new UndoEngine(paths).UndoLastRun();

        using var f = TagLib.File.Create(ruta);
        Assert.Equal(new[] { "@editor" }, f.Tag.Performers);
    }

    [Fact]
    public void Corregidos_solo_devuelve_las_listas_que_no_coinciden()
    {
        var escritos = new Dictionary<string, FieldChange>
        {
            ["Artist"] = FieldChange.Arr(new[] { "@x" }, new[] { "AC/DC" }),
            ["Genre"] = FieldChange.Arr(Array.Empty<string>(), new[] { "Rock" }),
            ["Album"] = FieldChange.Str("Latin Box", ""),
        };
        Assert.True(EtiquetasPorReglas.HayQueReleer(escritos));
        var tag = new TagLib.Id3v2.Tag { Performers = new[] { "AC", "DC" }, Genres = new[] { "Rock" } };

        var corregidos = EtiquetasPorReglas.Corregidos(tag, escritos);

        var artista = Assert.Single(corregidos);
        Assert.Equal("Artist", artista.Key);
        Assert.Equal(new[] { "@x" }, artista.Value.OldArr);
        Assert.Equal(new[] { "AC", "DC" }, artista.Value.NewArr);
        Assert.False(EtiquetasPorReglas.HayQueReleer(new Dictionary<string, FieldChange> { ["Artist"] = FieldChange.Arr(new[] { "a" }, new[] { "Bad Bunny" }) }));
    }

    [Fact]
    public void La_cola_del_estado_dice_si_se_puede_deshacer()
    {
        var bien = new EscrituraPorReglas.Resultado(new[] { "a" }, new[] { "b" }, Array.Empty<string>(), "", false);
        Assert.Contains("1 se han dejado como estaban", EscrituraPorReglas.Cola(bien));
        Assert.Contains("«Deshacer última»", EscrituraPorReglas.Cola(bien));
        var mal = bien with { UndoErr = "disco lleno" };
        Assert.Contains("no se podrá deshacer", EscrituraPorReglas.Cola(mal));
    }

    // =================================================================================================
    // P9 · Normalizar y contar
    // =================================================================================================

    [Theory]
    [InlineData("Hip-Hop", "Hip Hop")]
    [InlineData("reguetón", "Reggaeton")]
    [InlineData("Reggaeton; Latino", "Reggaeton")]
    [InlineData("  Rumba ", "Rumba")]
    [InlineData("@luigi.beltran", null)]
    [InlineData("Desconocido", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void P9_el_genero_se_cuenta_normalizado(string? genero, string? esperado)
        => Assert.Equal(esperado, GeneroPorReglas.Normalizado(genero));

    [Fact]
    public void P9_el_artista_principal_es_el_primero_del_credito()
    {
        Assert.Equal("ozuna", GeneroPorReglas.ClaveArtista("Ozuna, Daddy Yankee Ft Quevedo (Bayne Mashup 92Bpm)"));
        Assert.Equal("beyonce", GeneroPorReglas.ClaveArtista("Beyoncé"));
        Assert.Equal("", GeneroPorReglas.ClaveArtista("@liyo_dj98"));
        Assert.Equal("", GeneroPorReglas.ClaveArtista(null));
    }

    [Theory]
    [InlineData(2, 2, false, false)]   // dos pueden ser casualidad
    [InlineData(3, 3, true, false)]
    [InlineData(7, 10, true, false)]   // 70 %: más del doble que el resto
    [InlineData(6, 10, false, false)]
    [InlineData(4, 5, true, false)]    // 80 %, pero no casi unánime
    [InlineData(5, 5, true, true)]
    [InlineData(9, 10, true, true)]
    [InlineData(4, 4, true, false)]    // unánime, pero pocas
    public void P9_umbrales_del_artista(int con, int total, bool propone, bool fuerte)
        => Assert.Equal((propone, fuerte), GeneroPorReglas.Fuerza(con, total));

    [Fact]
    public void P9_los_generos_de_la_biblioteca_juntan_variantes_y_se_ordenan_por_uso()
    {
        var canciones = new[]
        {
            T(Rutas.De("a", "1.mp3"), genero: "Reggaeton"), T(Rutas.De("a", "2.mp3"), genero: "reguetón"),
            T(Rutas.De("a", "3.mp3"), genero: "Reggaeton"), T(Rutas.De("a", "4.mp3"), genero: "Salsa"),
            T(Rutas.De("a", "5.mp3"), genero: "@usuario"), T(Rutas.De("a", "6.mp3")),
        };
        var g = GeneroPorReglas.GenerosDeLaBiblioteca(canciones);
        Assert.Equal(new[] { ("Reggaeton", 3), ("Salsa", 1) }, g);
    }

    [Theory]
    [InlineData("reguetón", "Reggaeton")]
    [InlineData("REGGAETON", "Reggaeton")]
    [InlineData("hip-hop", "Hip Hop")]        // conocido por el normalizador, aunque la biblioteca no lo tenga
    [InlineData("Verbena", "Verbena")]        // nuevo: tal cual
    [InlineData("Pop/Rock", "Pop/Rock")]      // una lista escrita a propósito no se recorta
    [InlineData("  ", "")]
    public void P9_lo_escrito_en_el_mapa_se_escribe_como_la_biblioteca(string tecleado, string esperado)
        => Assert.Equal(esperado, GeneroPorReglas.ComoEnLaBiblioteca(tecleado, new[] { "Reggaeton", "Salsa" }));

    // =================================================================================================
    // P9 · Carpetas
    // =================================================================================================

    [Fact]
    public void P9_la_regla_de_carpeta_vale_para_las_subcarpetas_y_manda_la_mas_concreta()
    {
        var mapa = new Dictionary<string, string>
        {
            [Rutas.De("Musica", "Flamenco")] = "Flamenco",
            [Rutas.De("Musica", "Flamenco", "Rumbas")] = "Rumba",
            [Rutas.De("Musica", "Mezcla")] = "  ",   // sin género: no cuenta
        };
        Assert.Equal(("Rumba", Rutas.De("Musica", "Flamenco", "Rumbas")),
                     GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("Musica", "Flamenco", "Rumbas", "Peret", "a.mp3")));
        Assert.Equal("Flamenco", GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("Musica", "Flamenco", "Bulerias", "b.mp3"))?.Genero);
        Assert.Null(GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("Musica", "Flamenco2", "c.mp3")));   // no es una subcarpeta
        Assert.Null(GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("Musica", "Mezcla", "d.mp3")));
    }

    [Fact]
    public void P9_las_carpetas_se_ordenan_por_canciones_sin_genero_con_la_pista_de_lo_que_llevan_las_demas()
    {
        var canciones = new[]
        {
            T(Rutas.De("M", "Reggaeton", "1.mp3")), T(Rutas.De("M", "Reggaeton", "2.mp3")),
            T(Rutas.De("M", "Reggaeton", "3.mp3"), genero: "Reggaeton"), T(Rutas.De("M", "Reggaeton", "4.mp3"), genero: "reguetón"),
            T(Rutas.De("M", "Reggaeton", "5.mp3"), genero: "Latin"),
            T(Rutas.De("M", "Salsa", "1.mp3")), T(Rutas.De("M", "Salsa", "2.mp3"), genero: "Salsa"),
            T(Rutas.De("M", "Completa", "1.mp3"), genero: "Pop"),
        };

        var c = GeneroPorReglas.Carpetas(canciones);

        Assert.Equal(2, c.Count);   // la que ya tiene todo no sale
        Assert.Equal(new CarpetaSinGenero(Rutas.De("M", "Reggaeton"), 2, 5, "Reggaeton", 2, 3), c[0]);
        Assert.Equal(Rutas.De("M", "Salsa"), c[1].Ruta);

        var arriba = GeneroPorReglas.ConSubcarpetas(canciones, Rutas.De("M"));
        Assert.Equal(3, arriba.SinGenero);
        Assert.Equal(8, arriba.Total);
    }

    // =================================================================================================
    // P9 · Las propuestas
    // =================================================================================================

    private static IEnumerable<Track> DelArtista(string artista, string carpeta, params string[] generos)
        => generos.Select((g, i) => T(Rutas.De(carpeta, $"{artista} {i}.mp3"), artista, $"Tema {i}", genero: g));

    [Fact]
    public void P9_la_carpeta_manda_sobre_el_artista_y_sale_marcada()
    {
        var canciones = DelArtista("Peret", "Otros", "Rumba", "Rumba", "Rumba", "Rumba", "Rumba")
            .Append(T(Rutas.De("Verbena", "Peret - Borriquito.mp3"), "Peret", "Borriquito"))
            .ToList();
        var mapa = new Dictionary<string, string> { [Rutas.De("Verbena")] = "Verbena" };

        var p = Assert.Single(GeneroPorReglas.Proponer(canciones, mapa));

        Assert.Equal("Verbena", p.Genero);
        Assert.Equal(ReglaGenero.Carpeta, p.Regla);
        Assert.True(p.Marcada);
        Assert.Equal("Carpeta «Verbena» → Verbena", p.PorQue);
    }

    [Fact]
    public void P9_el_artista_propone_su_genero_dominante_y_solo_marca_lo_fuerte()
    {
        var canciones = DelArtista("Bad Bunny", "A", "Reggaeton", "Reggaeton", "reguetón", "Reggaeton", "Reggaeton", "Reggaeton",
                                   "Reggaeton", "Reggaeton", "Reggaeton", "Trap")
            .Concat(DelArtista("Feid", "A", "Reggaeton", "Reggaeton", "Reggaeton", "Pop"))
            .Concat(DelArtista("Rosalia", "A", "Flamenco", "Pop", "Reggaeton", "Flamenco"))
            .Append(T(Rutas.De("B", "bb.mp3"), "Bad Bunny x Feid", "Nuevo"))
            .Append(T(Rutas.De("B", "feid.mp3"), "Feid", "Nuevo"))
            .Append(T(Rutas.De("B", "rosalia.mp3"), "Rosalia", "Nuevo"))
            .Append(T(Rutas.De("B", "nadie.mp3"), "Nadie", "Nuevo"))
            .ToList();

        var p = GeneroPorReglas.Proponer(canciones, new Dictionary<string, string>());

        var bb = Assert.Single(p, x => x.Ruta.EndsWith("bb.mp3"));
        Assert.Equal("Reggaeton", bb.Genero);   // «reguetón» cuenta con «Reggaeton»
        Assert.Equal("Artista «Bad Bunny»: 9 de 10 canciones con Reggaeton", bb.PorQue);
        Assert.True(bb.Marcada);
        var feid = Assert.Single(p, x => x.Ruta.EndsWith("feid.mp3"));
        Assert.Equal("Reggaeton", feid.Genero);
        Assert.False(feid.Marcada);              // 3 de 4: se propone, pero hay que revisarlo
        Assert.DoesNotContain(p, x => x.Ruta.EndsWith("rosalia.mp3"));   // 2 de 4: sin dominio claro
        Assert.DoesNotContain(p, x => x.Ruta.EndsWith("nadie.mp3"));
        Assert.All(p, x => Assert.Equal(ReglaGenero.Artista, x.Regla));
    }

    [Fact]
    public void P9_nunca_propone_para_una_cancion_con_genero_ni_cuenta_lo_que_no_es_genero()
    {
        var canciones = DelArtista("X", "A", "@usuario", "@usuario", "@usuario", "Desconocido")
            .Append(T(Rutas.De("A", "con.mp3"), "X", "Con", genero: "Salsa"))
            .Append(T(Rutas.De("A", "sin.mp3"), "X", "Sin"))
            .ToList();
        var mapa = new Dictionary<string, string>();
        Assert.Empty(GeneroPorReglas.Proponer(canciones, mapa));
    }

    [Fact]
    public void P9_avisa_de_las_variantes_sin_unificar()
    {
        var canciones = new[] { T(Rutas.De("a", "1.mp3"), genero: "Hip-Hop"), T(Rutas.De("a", "2.mp3"), genero: "Salsa") };
        Assert.Equal(1, GeneroPorReglas.VariantesSinUnificar(canciones));
    }

    [Fact]
    public void P9_la_confirmacion_cuenta_por_regla_y_avisa_de_las_que_no_salian_marcadas()
    {
        var t = T(Rutas.De("A", "a.mp3"), "Feid", "Tema");
        var filas = new[]
        {
            new GeneroReglaFila(new PropuestaGeneroRegla(t.FilePath, "Reggaeton", ReglaGenero.Artista, "Artista", false), t),
            new GeneroReglaFila(new PropuestaGeneroRegla(t.FilePath, "Rumba", ReglaGenero.Carpeta, "Carpeta", true), t),
        };
        var (cuerpo, nota) = GeneroPorReglasViewModel.Confirmacion(filas);
        Assert.Contains("2 canciones que no tienen: 1 por su carpeta y 1 por su artista", cuerpo);
        Assert.Contains("⚠ 1 no salían marcadas", nota);
        Assert.Contains("«Recargar etiqueta»", nota);

        var cambio = Assert.Single(GeneroPorReglasViewModel.Cambios(filas.Take(1)));
        Assert.Equal("Reggaeton", cambio.GeneroNuevo);
        Assert.Null(cambio.AlbumVisto);
        Assert.Null(cambio.ArtistaNuevo);
    }

    // =================================================================================================
    // P9 · El mapa guardado
    // =================================================================================================

    [Fact]
    public void P9_el_mapa_se_guarda_y_se_vuelve_a_leer()
    {
        var archivo = Path.Combine(_dir, MapaGenerosCarpeta.NombreArchivo);
        var carpeta = Rutas.De("Musica", "Flamenco Rumbas");
        var mapa = new MapaGenerosCarpeta(archivo);
        Assert.Empty(mapa.Reglas);
        Assert.True(mapa.Poner(carpeta, " Rumba "));
        Assert.False(mapa.Poner(carpeta, "Rumba"));   // igual: no cambia nada
        Assert.True(mapa.Poner(Rutas.De("Musica", "Mezcla"), "Pop"));
        Assert.True(mapa.Poner(Rutas.De("Musica", "Mezcla"), ""));   // vacío: quita la regla
        Assert.Equal("", mapa.Guardar());

        var otra = new MapaGenerosCarpeta(archivo);
        Assert.Equal("", otra.AvisoCarga);
        Assert.Equal("Rumba", otra.De(carpeta.ToUpperInvariant()));   // la ruta no distingue mayúsculas
        Assert.Single(otra.Reglas);
    }

    [Fact]
    public void P9_un_mapa_ilegible_se_aparta_en_vez_de_pisarse()
    {
        var archivo = Path.Combine(_dir, MapaGenerosCarpeta.NombreArchivo);
        File.WriteAllText(archivo, "{ esto no es JSON");

        var mapa = new MapaGenerosCarpeta(archivo);

        Assert.Empty(mapa.Reglas);
        Assert.Contains("apartado", mapa.AvisoCarga);
        Assert.Single(Directory.GetFiles(_dir, MapaGenerosCarpeta.NombreArchivo + ".ilegible_*"));
        Assert.False(mapa.SoloLectura);
    }
}
