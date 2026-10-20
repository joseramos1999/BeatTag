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
    [InlineData("Heavy Hits Vol. 3", "es un record pool")]          // el pool con su volumen
    [InlineData("LatinBox Week 45", "es un record pool")]           // con su semana
    [InlineData("BRGS PRO QATAR 2022", "es un record pool")]
    [InlineData("Latin Box - Diciembre 2024", "es un record pool")] // con su mes
    public void P8_un_album_que_es_un_pool_o_un_usuario_es_publicidad_fuerte(string album, string motivo)
    {
        var porQue = LimpiezaPacks.PorQueAlbum(album, out var fuerte);
        Assert.NotNull(porQue);
        Assert.Contains(motivo, porQue);
        Assert.True(fuerte);
    }

    // Revisión de la tanda 3: PoolRe se escribió para nombres de archivo. En el Álbum, un álbum de
    // verdad puede llevar dentro el nombre de un pool; con más texto que el pool y su fecha, se
    // propone desmarcado y «Por qué» dice qué texto queda.
    [Theory]
    [InlineData("FIFA World Cup Qatar 2022", "FIFA World Cup")]
    [InlineData("Lo Mejor de Intensa", "Lo Mejor")]
    [InlineData("Latin Urbano Hits 2023", "Hits")]
    public void P8_un_album_con_el_nombre_de_un_pool_y_mas_texto_sale_desmarcado(string album, string resto)
    {
        var porQue = LimpiezaPacks.PorQueAlbum(album, out var fuerte);
        Assert.NotNull(porQue);
        Assert.False(fuerte);
        Assert.Contains($"pero también «{resto}»", porQue);
        Assert.Contains("Revísalo", porQue);
    }

    [Fact]
    public void P8_el_album_desmarcado_por_llevar_mas_texto_se_propone_sin_marcar()
    {
        var t = T(Rutas.De("lib", "a.mp3"), "Shakira", "Tema", "FIFA World Cup Qatar 2022");
        var p = Assert.Single(LimpiezaPacks.Proponer(new[] { t }));
        Assert.Equal(CampoPublicidad.Album, p.Campo);
        Assert.False(p.Marcada);
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
        Assert.False(bien.Fuerte);   // sin artistas conocidos que lo apoyen: se revisa a mano

        Assert.Null(Leido("Cancion Unica - Nuevo Artista.mp3", "Cancion Unica"));
        Assert.Null(Leido("Nuevo Artista - Cancion Unica.mp3", null));
    }

    // Revisión de la tanda 3: la vía del título es la más débil (en los mashups el orden del nombre
    // cambia) y salía marcada como las demás.
    [Fact]
    public void P8_el_artista_que_solo_confirma_el_titulo_sale_desmarcado_y_el_de_artistas_conocidos_marcado()
    {
        var debil = T(Rutas.De("pack", "Nuevo Artista - Cancion Unica.mp3"), "@editor", "Cancion Unica");
        var fuerte = T(Rutas.De("pack", "Khea, Cazzu - Loca.mp3"), "@editor", null);
        var p = LimpiezaPacks.Proponer(Conocidos("Khea", "Cazzu").Append(debil).Append(fuerte).ToList());

        var d = Assert.Single(p, x => x.Ruta == debil.FilePath);
        Assert.Equal("Nuevo Artista", d.Nuevo);
        Assert.False(d.Marcada);
        Assert.Contains("Revísalo", d.PorQue);
        var f = Assert.Single(p, x => x.Ruta == fuerte.FilePath);
        Assert.Equal("Khea, Cazzu", f.Nuevo);
        Assert.True(f.Marcada);
    }

    // Revisión de la tanda 3: los packs nombran en mayúsculas y copiar el nombre del archivo creaba
    // otra variante de un artista que la biblioteca ya escribe de otra forma.
    [Fact]
    public void P8_el_artista_se_escribe_como_lo_escribe_la_biblioteca()
    {
        var biblioteca = new[]
        {
            T(Rutas.De("lib", "1.mp3"), "Kevin Roldan", "A"), T(Rutas.De("lib", "2.mp3"), "Kevin Roldan", "B"),
            T(Rutas.De("lib", "3.mp3"), "KEVIN ROLDAN", "C"),
            T(Rutas.De("lib", "4.mp3"), "Bad Bunny", "D"), T(Rutas.De("lib", "5.mp3"), "Bad Bunny feat. Otro", "E"),
        };
        var pack = T(Rutas.De("pack", "KEVIN ROLDAN, BAD BUNNY - Tema.mp3"), "@editor", null);
        var indice = new IndiceArtistas(biblioteca.Append(pack));

        Assert.Equal("Kevin Roldan", indice.Grafia("KEVIN ROLDAN"));
        Assert.Equal("Desconocido Total", indice.Grafia("Desconocido Total"));   // no lo conoce: tal cual
        Assert.Equal("Kevin Roldan, Bad Bunny", indice.ComoEnLaBiblioteca("KEVIN ROLDAN, BAD BUNNY"));
        Assert.Equal("Kevin Roldan x Nuevo", indice.ComoEnLaBiblioteca("KEVIN ROLDAN x Nuevo"));   // separadores y desconocidos, igual

        var r = LimpiezaPacks.ArtistaDelNombre(pack, indice);
        Assert.Equal("Kevin Roldan, Bad Bunny", r!.Artista);
        Assert.True(r.Fuerte);
        Assert.Contains("«Kevin Roldan» ya es artista de 3 canciones", r.PorQue);
        Assert.Contains("como en el resto de tu biblioteca", r.PorQue);
    }

    // A igualdad de canciones, la grafía que no va toda en mayúsculas.
    [Fact]
    public void P8_a_igualdad_gana_la_grafia_que_no_va_en_mayusculas()
    {
        var indice = new IndiceArtistas(new[]
        {
            T(Rutas.De("lib", "1.mp3"), "OZUNA", "A"), T(Rutas.De("lib", "2.mp3"), "Ozuna", "B"),
        });
        Assert.Equal("Ozuna", indice.Grafia("ozuna"));
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
        Assert.Contains("1 canción: 1 álbum y 1 artista", cuerpo);
        Assert.Contains("⚠ 1 no salía marcada", nota);
        Assert.Contains("Duplicados y la Bandeja", nota);
        Assert.Contains("«Recargar etiqueta»", nota);
        Assert.Contains("comentario", nota);
        Assert.Contains("Recargar etiqueta", LimpiarPacksViewModel.AvisoTrasAplicar(0));
        Assert.DoesNotContain("Duplicados", LimpiarPacksViewModel.AvisoTrasAplicar(0));
        Assert.Contains("Duplicados", LimpiarPacksViewModel.AvisoTrasAplicar(3));
        Assert.Contains("el artista de 1 canción:", LimpiarPacksViewModel.AvisoTrasAplicar(1));
        Assert.Contains("el artista de 3 canciones:", LimpiarPacksViewModel.AvisoTrasAplicar(3));
    }

    [Fact]
    public void P8_los_textos_distinguen_singular_y_plural_y_cuentan_canciones()
    {
        var a = Rutas.De("p", "a.mp3");
        var una = new[] { new PropuestaPublicidad(a, CampoPublicidad.Album, "Latin Box", "", "pool", true) };
        Assert.Equal("1 álbum y 0 artistas con publicidad", LimpiarPacksViewModel.TextoResumen(una, 0));
        var varias = una.Append(new PropuestaPublicidad(Rutas.De("p", "b.mp3"), CampoPublicidad.Album, "@x", "", "u", true))
                        .Append(new PropuestaPublicidad(a, CampoPublicidad.Artista, "@x", "Ozuna", "u", true)).ToList();
        Assert.StartsWith("2 álbumes y 1 artista con publicidad · 1 canción con un artista «@usuario»", LimpiarPacksViewModel.TextoResumen(varias, 1));
        Assert.StartsWith("3 canciones con un artista «@usuario» o de un pool se quedan sin propuesta", LimpiarPacksViewModel.TextoSinPropuesta(3));

        var (cuerpo, _) = LimpiarPacksViewModel.Confirmacion(new[]
        {
            new PublicidadFila(new PropuestaPublicidad(a, CampoPublicidad.Album, "Latin Box", "", "pool", true), true),
            new PublicidadFila(new PropuestaPublicidad(Rutas.De("p", "b.mp3"), CampoPublicidad.Album, "@y", "", "u", true), true),
        }, alComentario: false);
        Assert.Equal("Se limpiarán las etiquetas de 2 canciones: 2 álbumes y 0 artistas.", cuerpo);
    }

    // Revisión de la tanda 3: en un mismo archivo, el artista «@usuario» marcado y el álbum «El
    // Cangri.com» desmarcado. Tras aplicar, el álbum desaparecía de la tabla sin haberse aplicado.
    [Fact]
    public void P8_tras_aplicar_se_quedan_las_filas_desmarcadas_aunque_su_archivo_se_escribiera()
    {
        var ruta = Rutas.De("p", "a.mp3");
        var artista = new PublicidadFila(new PropuestaPublicidad(ruta, CampoPublicidad.Artista, "@x", "Daddy Yankee", "u", true), true);
        var album = new PublicidadFila(new PropuestaPublicidad(ruta, CampoPublicidad.Album, "El Cangri.com", "", "web", false), true);
        var fallida = new PublicidadFila(new PropuestaPublicidad(Rutas.De("p", "b.mp3"), CampoPublicidad.Album, "Latin Box", "", "pool", true), true);
        var respetada = new PublicidadFila(new PropuestaPublicidad(Rutas.De("p", "c.mp3"), CampoPublicidad.Album, "Latin Box", "", "pool", true), true);
        var res = new EscrituraPorReglas.Resultado(new[] { ruta }, new[] { respetada.Ruta }, new[] { "b.mp3: en uso" }, "", false);

        var quedan = LimpiarPacksViewModel.QuedanTrasAplicar(new[] { artista, album, fallida, respetada }, new[] { artista, fallida, respetada }, res);

        // Sale lo aplicado y lo que cambió por otro lado; se quedan la desmarcada y la que falló.
        Assert.Equal(new[] { album, fallida }, quedan);
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
    // quedó en el archivo, o deshacer lo daría por editado a mano y no lo devolvería. Y en UNA sola
    // línea: con un registro corrector aparte, el historial contaba dos cambios por archivo.
    [Fact]
    public void Un_valor_con_barra_se_anota_como_quedo_en_una_sola_linea_y_tambien_se_deshace()
    {
        var paths = new AppPaths(Path.Combine(_dir, "datos"));
        var ruta = Mp3("a.mp3", artistas: new[] { "@editor" });
        using (var m = ManifiestoDeshacer.Abrir(paths.NuevoManifiesto(), out var error))
        {
            var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { ArtistaVisto = "@editor", ArtistaNuevo = "AC/DC" } },
                                                  m, error, null, null, null, CancellationToken.None, "prueba");
            Assert.Equal("", res.UndoErr);
            Assert.Equal(1, m!.Anotados);
        }

        new UndoEngine(paths).UndoLastRun();

        using var f = TagLib.File.Create(ruta);
        Assert.Equal(new[] { "@editor" }, f.Tag.Performers);
    }

    /// <summary>Un MP3 con su etiqueta ID3v2 en la versión pedida (2.3 parte los valores con «/» al leerlos).</summary>
    private string Mp3Id3v2(string nombre, byte version)
    {
        var ruta = Mp3(nombre, artistas: new[] { "@editor" });
        using var f = TagLib.File.Create(ruta);
        var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
        id3.Version = version;
        f.Save();
        return ruta;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Se_anota_de_entrada_lo_que_el_archivo_leera_despues(byte version)
    {
        var ruta = Mp3Id3v2("a.mp3", version);
        var historial = new HistorialQueFalla();

        var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { GeneroNuevo = "Hip-Hop/Rap" } },
                                              historial, "", null, null, null, CancellationToken.None, "prueba");

        Assert.Equal(new[] { ruta }, res.Escritos);
        var registro = Assert.Single(historial.Anotados);   // sin registro corrector
        string[] quedo;
        using (var f = TagLib.File.Create(ruta)) quedo = f.Tag.Genres;
        Assert.Equal(quedo, registro.Fields!["Genre"].NewArr);
    }

    [Fact]
    public void La_lista_releida_es_la_de_tagLib_en_cada_version()
    {
        Assert.Equal(new[] { "AC", "DC" }, EtiquetasPorReglas.ListaReleidaId3v2(3, "Artist", new[] { "AC/DC" }));
        Assert.Equal(new[] { "AC/DC" }, EtiquetasPorReglas.ListaReleidaId3v2(4, "Artist", new[] { "AC/DC" }));
        Assert.Equal(new[] { "Bad Bunny" }, EtiquetasPorReglas.ListaReleidaId3v2(3, "Artist", new[] { "Bad Bunny" }));
    }

    // El historial reconoce la herramienta (datos-4): sin Origen, una limpieza de packs (álbum,
    // artista, comentario) salía como «Edición de etiquetas», que es el Editor.
    [Fact]
    public void Escribir_anota_el_origen_y_el_historial_reconoce_la_herramienta()
    {
        var ruta = Mp3("a.mp3", "Latin Box", new[] { "@x" });
        var historial = new HistorialQueFalla();

        EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { AlbumVisto = "Latin Box", ArtistaVisto = "@x", ArtistaNuevo = "Ozuna" } },
                                    historial, "", null, null, null, CancellationToken.None, "prueba", TipoEjecucion.LimpiezaPacks);

        var registro = Assert.Single(historial.Anotados);
        Assert.Equal(nameof(TipoEjecucion.LimpiezaPacks), registro.Origen);
        var nodo = System.Text.Json.JsonSerializer.SerializeToNode(registro);
        Assert.Equal(TipoEjecucion.LimpiezaPacks, HistorialDeshacer.DeducirTipo(new[] { nodo }));
        Assert.Equal("Limpieza de publicidad de packs", HistorialDeshacer.Texto(TipoEjecucion.LimpiezaPacks));

        // Sin origen, el historial lo deduce de los campos, como en los manifiestos anteriores.
        registro.Origen = null;
        Assert.Equal(TipoEjecucion.EdicionEtiquetas, HistorialDeshacer.DeducirTipo(new[] { System.Text.Json.JsonSerializer.SerializeToNode(registro) }));
    }

    [Fact]
    public void El_historial_nombra_el_campo_isrc_en_mayusculas()
        => Assert.Equal("ISRC", HistorialDeshacer.NombreCampo(Tagging.IsrcField));

    // Un archivo enviado a la papelera desde otra tabla (o movido) se dice en español, sin el texto
    // en inglés de TagLib.
    [Fact]
    public void Escribir_un_archivo_que_ya_no_esta_lo_dice_en_espanol()
    {
        var ruta = Path.Combine(_dir, "no-esta.mp3");
        var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { AlbumVisto = "Latin Box" } },
                                              new HistorialQueFalla(), "", null, null, null, CancellationToken.None, "prueba");
        var fallo = Assert.Single(res.Fallos);
        Assert.Equal("no-esta.mp3: " + EscrituraPorReglas.YaNoEsta, fallo);
        Assert.Contains("1 no se pudo escribir (no-esta.mp3: ya no está", EscrituraPorReglas.Cola(res));
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
        Assert.Contains("1 se ha dejado como estaba porque cambió después de proponerla", EscrituraPorReglas.Cola(bien));
        Assert.Contains("«Deshacer última»", EscrituraPorReglas.Cola(bien));
        var mal = bien with { UndoErr = "disco lleno" };
        Assert.Contains("no se podrá deshacer", EscrituraPorReglas.Cola(mal));
        var varias = bien with { Respetados = new[] { "b", "c" }, Fallos = new[] { "x.mp3: en uso", "y.mp3: en uso" } };
        Assert.Contains("2 se han dejado como estaban", EscrituraPorReglas.Cola(varias));
        Assert.Contains("2 no se pudieron escribir (la primera, x.mp3: en uso)", EscrituraPorReglas.Cola(varias));

        Assert.Equal("1 canción", EscrituraPorReglas.Cuenta(1, "canción", "canciones"));
        Assert.Equal("0 canciones", EscrituraPorReglas.Cuenta(0, "canción", "canciones"));
        Assert.Equal($"{1500:N0} canciones", EscrituraPorReglas.Cuenta(1500, "canción", "canciones"));
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
            [Rutas.De("Musica", "Mezcla")] = "  ",   // sin género: excluida, no da regla
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
        Assert.True(arriba.Subcarpetas);
    }

    // Revisión de la tanda 3: en una biblioteca Género/Pack/… la lista solo enseñaba los packs, y la
    // carpeta del género había que añadirla a mano.
    private static List<Track> GeneroYPacks() => new()
    {
        T(Rutas.De("M", "Reggaeton", "Pack1", "1.mp3")), T(Rutas.De("M", "Reggaeton", "Pack1", "2.mp3"), genero: "Reggaeton"),
        T(Rutas.De("M", "Reggaeton", "Pack2", "1.mp3")), T(Rutas.De("M", "Reggaeton", "Pack2", "2.mp3"), genero: "Reggaeton"),
        T(Rutas.De("M", "Reggaeton", "Pack2", "3.mp3"), genero: "reguetón"),
        T(Rutas.De("M", "Salsa", "1.mp3")), T(Rutas.De("M", "Salsa", "2.mp3"), genero: "Salsa"),
        T(Rutas.De("M", "Salsa", "3.mp3"), genero: "Salsa"), T(Rutas.De("M", "Salsa", "4.mp3"), genero: "Salsa"),
    };

    [Fact]
    public void P9_la_lista_incluye_la_carpeta_que_agrupa_otras_con_sus_cifras()
    {
        var c = GeneroPorReglas.Carpetas(GeneroYPacks(), new[] { Rutas.De("M") });

        var reggaeton = Assert.Single(c, x => x.Ruta == Rutas.De("M", "Reggaeton"));
        Assert.Equal(new CarpetaSinGenero(Rutas.De("M", "Reggaeton"), 2, 5, "Reggaeton", 3, 3, Subcarpetas: true), reggaeton);
        Assert.Equal(reggaeton, c[0]);                                  // la que más cubre, arriba
        Assert.Contains(c, x => x.Ruta == Rutas.De("M", "Reggaeton", "Pack1") && !x.Subcarpetas);
        Assert.Contains(c, x => x.Ruta == Rutas.De("M", "Salsa"));
        // La carpeta de la biblioteca mezcla Reggaeton y Salsa: darle un género lo pondría en todo.
        Assert.DoesNotContain(c, x => x.Ruta == Rutas.De("M"));
        Assert.Equal(4, c.Count);

        // Sin las carpetas de la biblioteca no se sube por encima de las que tienen canciones.
        Assert.DoesNotContain(GeneroPorReglas.Carpetas(GeneroYPacks()), x => x.Ruta == Rutas.De("M", "Reggaeton"));
    }

    [Fact]
    public void P9_la_carpeta_de_la_biblioteca_sale_si_es_la_de_un_genero()
    {
        var soloReggaeton = GeneroYPacks().Where(t => t.FilePath.Contains("Reggaeton")).ToList();
        var c = GeneroPorReglas.Carpetas(soloReggaeton, new[] { Rutas.De("M", "Reggaeton") });
        Assert.Equal(Rutas.De("M", "Reggaeton"), c[0].Ruta);   // 3 de 3 con género llevan Reggaeton
    }

    // Una subcarpeta que mezcla estilos («WarmUp» dentro de «Reggaeton») se excluye de la regla de
    // su carpeta madre. Antes, dejar el género vacío borraba la regla y volvía a valer la de la madre.
    [Fact]
    public void P9_una_subcarpeta_excluida_no_recibe_la_regla_de_su_madre()
    {
        var mapa = new Dictionary<string, string>
        {
            [Rutas.De("M", "Reggaeton")] = "Reggaeton",
            [Rutas.De("M", "Reggaeton", "WarmUp")] = "",
            [Rutas.De("M", "Reggaeton", "WarmUp", "Solo Reggaeton")] = "Reggaeton",
        };
        Assert.Null(GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("M", "Reggaeton", "WarmUp", "a.mp3")));
        Assert.Null(GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("M", "Reggaeton", "WarmUp", "Otra", "a.mp3")));
        Assert.Equal("Reggaeton", GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("M", "Reggaeton", "WarmUp", "Solo Reggaeton", "a.mp3"))?.Genero);
        Assert.Equal("Reggaeton", GeneroPorReglas.ReglaDeCarpeta(mapa, Rutas.De("M", "Reggaeton", "Pack1", "a.mp3"))?.Genero);

        var canciones = new[] { T(Rutas.De("M", "Reggaeton", "WarmUp", "a.mp3"), "Nadie", "Tema"), T(Rutas.De("M", "Reggaeton", "Pack1", "b.mp3"), "Nadie", "Otro") };
        var p = Assert.Single(GeneroPorReglas.Proponer(canciones, mapa));
        Assert.Equal(canciones[1].FilePath, p.Ruta);
    }

    [Fact]
    public void P9_el_mapa_guarda_las_carpetas_excluidas()
    {
        var archivo = Path.Combine(_dir, MapaGenerosCarpeta.NombreArchivo);
        var madre = Rutas.De("M", "Reggaeton");
        var warmUp = Rutas.De("M", "Reggaeton", "WarmUp");
        var mapa = new MapaGenerosCarpeta(archivo);
        mapa.Poner(madre, "Reggaeton");
        Assert.True(mapa.Excluir(warmUp));
        Assert.False(mapa.Excluir(warmUp));   // ya lo estaba
        Assert.Equal("", mapa.Guardar());

        var leido = new MapaGenerosCarpeta(archivo);
        Assert.True(leido.Excluida(warmUp));
        Assert.False(leido.Excluida(madre));
        Assert.Equal(1, leido.ConGenero);
        Assert.Equal(2, leido.Reglas.Count);

        // Quitar la regla de una excluida la devuelve a la de su madre.
        Assert.True(leido.Poner(warmUp, ""));
        Assert.False(leido.Excluida(warmUp));
        Assert.Single(leido.Reglas);
    }

    [Fact]
    public void P9_la_fila_de_carpeta_dice_si_esta_excluida_y_si_cuenta_subcarpetas()
    {
        var datos = new CarpetaSinGenero(Rutas.De("M", "Reggaeton"), 2, 5, "Reggaeton", 3, 3, Subcarpetas: true);
        var fila = new CarpetaGeneroFila(datos, "", excluida: true);
        Assert.Equal("Excluida (sin regla de carpeta)", fila.Regla);
        Assert.Equal("Reggaeton (y subcarpetas)", fila.Nombre);
        fila.Excluida = false;
        Assert.Equal("Sin asignar", fila.Regla);
    }

    [Fact]
    public void P9_los_textos_distinguen_singular_y_plural()
    {
        Assert.Equal("1 carpeta con canciones sin género · 0 con regla", GeneroPorReglasViewModel.TextoCarpetas(1, 0, 0));
        Assert.Equal("3 carpetas con canciones sin género · 2 con regla · 1 excluida", GeneroPorReglasViewModel.TextoCarpetas(3, 2, 1));
        Assert.Equal("1 canción sin género · 1 por su carpeta · 0 por su artista (0 marcadas)", GeneroPorReglasViewModel.TextoResumen(1, 1, 0, 0));
        Assert.EndsWith("(1 marcada)", GeneroPorReglasViewModel.TextoResumen(4, 1, 3, 1));
        Assert.StartsWith("«WarmUp» queda excluida", GeneroPorReglasViewModel.TextoReglaCambiada(1, "WarmUp", "", excluir: true));
        Assert.StartsWith("2 carpetas quedan excluidas", GeneroPorReglasViewModel.TextoReglaCambiada(2, "WarmUp", "", excluir: true));
        Assert.Equal("Regla quitada de «Salsa».", GeneroPorReglasViewModel.TextoReglaCambiada(1, "Salsa", "", excluir: false));

        var t = T(Rutas.De("A", "a.mp3"), "Feid", "Tema");
        var una = new[] { new GeneroReglaFila(new PropuestaGeneroRegla(t.FilePath, "Reggaeton", ReglaGenero.Carpeta, "Carpeta", true), t) };
        Assert.Equal("Se pondrá género a 1 canción que no tiene: 1 por su carpeta y 0 por su artista.", GeneroPorReglasViewModel.Confirmacion(una).Cuerpo);
    }

    // Revisión de la tanda 3: el género corregido a mano en la tabla se escribía tal cual y creaba
    // otra variante («reguetón» junto a «Reggaeton»).
    [Fact]
    public void P9_el_genero_corregido_a_mano_se_escribe_como_en_la_biblioteca()
    {
        var t = T(Rutas.De("A", "a.mp3"), "Feid", "Tema");
        var u = T(Rutas.De("A", "b.mp3"), "Feid", "Otro");
        var corregida = new GeneroReglaFila(new PropuestaGeneroRegla(t.FilePath, "Latin", ReglaGenero.Artista, "Artista", true), t) { Genero = " reguetón " };
        var vaciada = new GeneroReglaFila(new PropuestaGeneroRegla(u.FilePath, "Latin", ReglaGenero.Artista, "Artista", true), u) { Genero = "  " };

        var cambio = Assert.Single(GeneroPorReglasViewModel.Cambios(new[] { corregida, vaciada }, new[] { "Reggaeton", "Salsa" }));
        Assert.Equal("Reggaeton", cambio.GeneroNuevo);
    }

    [Fact]
    public void P9_tras_aplicar_se_quedan_las_desmarcadas_y_las_que_fallaron()
    {
        GeneroReglaFila Fila(string archivo)
        {
            var t = T(Rutas.De("A", archivo), "Feid", "Tema");
            return new GeneroReglaFila(new PropuestaGeneroRegla(t.FilePath, "Reggaeton", ReglaGenero.Artista, "Artista", true), t);
        }
        var escrita = Fila("a.mp3");
        var desmarcada = Fila("b.mp3");
        var fallida = Fila("c.mp3");
        var res = new EscrituraPorReglas.Resultado(new[] { escrita.Ruta }, Array.Empty<string>(), new[] { "c.mp3: en uso" }, "", false);

        Assert.Equal(new[] { desmarcada, fallida },
                     GeneroPorReglasViewModel.QuedanTrasAplicar(new[] { escrita, desmarcada, fallida }, new[] { escrita, fallida }, res));
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
        Assert.Contains("⚠ 1 no salía marcada", nota);
        Assert.Contains("«Recargar etiqueta»", nota);

        var cambio = Assert.Single(GeneroPorReglasViewModel.Cambios(filas.Take(1), new[] { "Reggaeton" }));
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
