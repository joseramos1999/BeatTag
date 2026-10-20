using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.App.Acciones;
using Etiquetador.Core;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Página Novedades (P5): quiénes son los artistas principales de la biblioteca, quién es cada uno en
/// Deezer entre sus homónimos, qué han publicado en el plazo y qué canciones trae cada lanzamiento. Las
/// respuestas de Deezer son JSON de ejemplo con la forma de las reales, sin red.
/// </summary>
public class NovedadesTests
{
    private static readonly DateTime Hoy = new(2026, 10, 20);

    private static Track Cancion(string artista, string titulo, string? archivo = null)
        => new() { FilePath = Rutas.Archivo(archivo ?? $"{artista} - {titulo}.mp3"), Folder = Rutas.Carpeta, Artist = artista, Title = titulo };

    private static ArtistaBiblioteca Artista(string nombre, params string[] titulos)
        => new(IndiceArtistasClave(nombre), nombre, titulos.Length,
               titulos.Select(ArtistasDeLaBiblioteca.ClaveTitulo).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal));

    private static string IndiceArtistasClave(string nombre) => Etiquetador.Core.Dj.IndiceArtistas.Clave(nombre);

    private static LanzamientoDeezer Lanzamiento(long id, string titulo, string fecha, TipoLanzamiento tipo = TipoLanzamiento.Single)
        => new(id, titulo, DateTime.Parse(fecha, System.Globalization.CultureInfo.InvariantCulture), tipo, $"https://www.deezer.com/album/{id}");

    private static PistaNovedad Pista(string titulo, LanzamientoDeezer l, string artista = "Bad Bunny", string isrc = "", int posicion = 1)
        => new(artista, titulo, 180, $"https://www.deezer.com/track/{l.Id}{posicion}", isrc, l, 1, posicion) { Por = artista };

    // =============================================================================================
    // Lectura de las respuestas de Deezer
    // =============================================================================================

    [Fact]
    public void Lee_los_artistas_de_la_busqueda_y_salta_los_que_no_traen_id_o_nombre()
    {
        var json = JsonNode.Parse("""
            {"data":[
              {"id":27,"name":"Bad Bunny","nb_album":120,"nb_fan":8045190},
              {"id":0,"name":"Sin id","nb_fan":5},
              {"id":31,"name":"  ","nb_fan":5},
              {"id":900,"name":"Bad Bunny","nb_album":3,"nb_fan":26}
            ],"total":4}
            """);

        var artistas = NovedadesDeezer.LeerArtistas(json);

        Assert.Equal(new long[] { 27, 900 }, artistas.Select(a => a.Id));
        Assert.Equal(8045190, artistas[0].Fans);
        Assert.Equal(120, artistas[0].Lanzamientos);
        Assert.Equal("https://www.deezer.com/artist/27", artistas[0].Enlace);
    }

    [Fact]
    public void Lee_los_lanzamientos_con_su_tipo_y_salta_los_que_no_tienen_fecha()
    {
        var json = JsonNode.Parse("""
            {"data":[
              {"id":1,"title":"DeBÍ TiRAR MáS FOTOS","release_date":"2025-01-05","record_type":"album","link":"https://www.deezer.com/album/1"},
              {"id":2,"title":"Un EP","release_date":"2026-10-01","record_type":"ep"},
              {"id":3,"title":"Monaco","release_date":"2023-10-13","record_type":"single"},
              {"id":4,"title":"Grandes éxitos","release_date":"2020-02-02","record_type":"compile"},
              {"id":5,"title":"Sin fecha","release_date":"0000-00-00","record_type":"single"},
              {"id":6,"title":"Raro","release_date":"2019-01-01","record_type":"loquesea"}
            ]}
            """);

        var l = NovedadesDeezer.LeerLanzamientos(json);

        Assert.Equal(new long[] { 1, 2, 3, 4, 6 }, l.Select(x => x.Id));
        Assert.Equal(new[] { TipoLanzamiento.Album, TipoLanzamiento.Ep, TipoLanzamiento.Single, TipoLanzamiento.Recopilatorio, TipoLanzamiento.Album },
                     l.Select(x => x.Tipo));
        Assert.Equal(new DateTime(2025, 1, 5), l[0].Fecha);
        Assert.Equal("https://www.deezer.com/album/2", l[1].Enlace);   // sin «link», el de siempre
    }

    [Fact]
    public void Lee_las_canciones_de_un_lanzamiento_con_su_isrc_y_su_posicion()
    {
        var l = Lanzamiento(5001, "DeBÍ TiRAR MáS FOTOS", "2026-10-10", TipoLanzamiento.Album);
        var json = JsonNode.Parse("""
            {"data":[
              {"id":11,"title":"NUEVAYoL","duration":183,"isrc":"qm6mz2600001","link":"https://www.deezer.com/track/11","track_position":1,"disk_number":1,"artist":{"name":"Bad Bunny"}},
              {"id":12,"title":"VOY A LLeVARTE PA PR","duration":157,"track_position":2,"artist":{"name":"Bad Bunny"}},
              {"id":13,"title":"","duration":100}
            ]}
            """);

        var p = NovedadesDeezer.LeerPistas(json, l);

        Assert.Equal(2, p.Count);
        Assert.Equal("QM6MZ2600001", p[0].Isrc);
        Assert.Equal(183, p[0].Duracion);
        Assert.Equal(1, p[1].Disco);   // sin «disk_number», el primero
        Assert.Equal(2, p[1].Posicion);
        Assert.Same(l, p[0].Lanzamiento);
    }

    [Fact]
    public void Lee_la_publicacion_de_una_grabacion_y_nada_si_deezer_da_error()
    {
        var bien = NovedadesDeezer.LeerPublicacion(JsonNode.Parse("""{"id":1,"album":{"title":"YHLQMDLG","release_date":"2020-02-29"}}"""));
        Assert.Equal((new DateTime(2020, 2, 29), "YHLQMDLG"), bien);
        Assert.Null(NovedadesDeezer.LeerPublicacion(JsonNode.Parse("""{"error":{"type":"DataException","message":"no data","code":800}}""")));
        Assert.Null(NovedadesDeezer.LeerArtista(JsonNode.Parse("""{"error":{"type":"DataException","message":"no data","code":800}}""")));
        Assert.Equal(27, NovedadesDeezer.LeerArtista(JsonNode.Parse("""{"id":27,"name":"Bad Bunny","nb_fan":10}"""))!.Id);
    }

    [Theory]
    [InlineData("https://www.deezer.com/es/artist/10583405", 10583405L)]
    [InlineData("deezer.com/artist/27?utm_source=x", 27L)]
    [InlineData("https://www.deezer.com/en-gb/artist/27/top_track", 27L)]
    [InlineData("  27  ", 27L)]
    [InlineData("https://www.deezer.com/album/27", null)]
    [InlineData("https://open.spotify.com/artist/27", null)]
    [InlineData("Bad Bunny", null)]
    [InlineData("0", null)]
    [InlineData("", null)]
    public void El_numero_de_artista_sale_del_enlace_de_deezer_o_del_numero_tal_cual(string texto, long? esperado)
        => Assert.Equal(esperado, NovedadesDeezer.IdDeEnlace(texto));

    // =============================================================================================
    // Homónimos
    // =============================================================================================

    // MEDIDO: «bad bunny» devuelve primero un «Bad Bunny» de 26 seguidores. Los que no se llaman igual
    // («Bad Bunny & Arcangel», «BAD BUNNY BAND») no son candidatos; acentos y mayúsculas no cuentan.
    [Fact]
    public void Solo_son_candidatos_los_que_se_llaman_igual_de_mas_a_menos_seguidores()
    {
        var a = Artista("Bad Bunny", "Monaco");
        var resultados = new[]
        {
            new ArtistaDeezer(900, "Bad Bunny", 26, 3),
            new ArtistaDeezer(27, "BAD BUNNY", 8_045_190, 120),
            new ArtistaDeezer(28, "Bad Bunny & Arcangel", 50_000, 2),
            new ArtistaDeezer(29, "BAD BUNNY BAND", 300, 1),
            new ArtistaDeezer(30, "Bád Bunny", 100, 1),
            new ArtistaDeezer(31, "Bad Bunny", 10, 1),
            new ArtistaDeezer(27, "Bad Bunny", 8_045_190, 120),   // repetido por buscar con dos nombres
        };

        var c = NovedadesDeezer.CandidatosMismoNombre(a, resultados, new ArtistAliases());

        Assert.Equal(new long[] { 27, 30, 900 }, c.Select(x => x.Id));   // como mucho 3
    }

    [Fact]
    public void Un_alias_registrado_tambien_es_candidato()
    {
        var alias = new ArtistAliases();   // de serie: Cruz Cafuné = Cruzzi
        var a = Artista("Cruz Cafuné", "Contando Lunares");

        Assert.Contains("Cruzzi", NovedadesDeezer.NombresDeBusqueda(a, alias));
        Assert.Single(NovedadesDeezer.CandidatosMismoNombre(a, new[] { new ArtistaDeezer(5, "Cruzzi", 1000, 10) }, alias));
    }

    [Fact]
    public void Gana_el_que_tiene_canciones_de_la_biblioteca_aunque_tenga_menos_seguidores()
    {
        var famoso = new ArtistaDeezer(1, "Manu", 900_000, 50);
        var bueno = new ArtistaDeezer(2, "Manu", 1_309, 30);

        var r = NovedadesDeezer.Elegir(new (ArtistaDeezer, int, IReadOnlyList<LanzamientoDeezer>?)[] { (famoso, 0, null), (bueno, 3, null) });

        Assert.Equal(2, r.Artista!.Id);
        Assert.Equal(EstadoArtistaDeezer.Confirmado, r.Estado);
        Assert.Equal(3, r.Coincidencias);
        Assert.Equal(2, r.Candidatos);
    }

    // MEDIDO: con una sola canción en común, «Manu» casaba con un homónimo brasileño. Hacen falta dos.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Con_menos_de_dos_canciones_en_comun_queda_sin_confirmar_y_se_toma_el_de_mas_seguidores(int coincidencias)
    {
        var r = NovedadesDeezer.Elegir(new (ArtistaDeezer, int, IReadOnlyList<LanzamientoDeezer>?)[]
        {
            (new ArtistaDeezer(1, "Kike", 34, 2), coincidencias, null),
            (new ArtistaDeezer(2, "Kike", 5_000, 9), coincidencias, null),
        });

        Assert.Equal(2, r.Artista!.Id);
        Assert.Equal(EstadoArtistaDeezer.Dudoso, r.Estado);
    }

    [Fact]
    public void Sin_candidatos_no_esta_en_deezer()
    {
        var r = NovedadesDeezer.Elegir(Array.Empty<(ArtistaDeezer, int, IReadOnlyList<LanzamientoDeezer>?)>());
        Assert.Null(r.Artista);
        Assert.Equal(EstadoArtistaDeezer.NoEncontrado, r.Estado);
    }

    [Fact]
    public void Las_coincidencias_cuentan_titulos_distintos_sin_la_version()
    {
        var biblioteca = Artista("Bad Bunny", "DtMF (Extended)", "Monaco", "Yo").Titulos;

        Assert.Equal(2, NovedadesDeezer.Coincidencias(new[] { "DtMF", "DTMF (Remix)", "Monaco", "Yo", "Otra" }, biblioteca));
        Assert.Equal("", ArtistasDeLaBiblioteca.ClaveTitulo("Yo"));   // títulos tan cortos no reconocen a nadie
        Assert.Equal(ArtistasDeLaBiblioteca.ClaveTitulo("DtMF"), ArtistasDeLaBiblioteca.ClaveTitulo("DtMF (Extended Mix)"));
    }

    // =============================================================================================
    // Qué es reciente y qué ya había salido
    // =============================================================================================

    [Fact]
    public void Reciente_es_desde_hace_N_dias_hasta_hoy_y_los_anunciados_para_despues_no_cuentan()
    {
        var l = new[]
        {
            Lanzamiento(1, "Justo en el límite", "2026-09-20"),
            Lanzamiento(2, "Un día antes", "2026-09-19"),
            Lanzamiento(3, "Hoy", "2026-10-20"),
            Lanzamiento(4, "Preestreno", "2026-11-15"),
        };

        Assert.Equal(new long[] { 1, 3 }, NovedadesDeezer.Recientes(l, Hoy, 30).Select(x => x.Id));
    }

    // Lo más habitual: el álbum recoge el single de hace meses. Se compara el título sin versión.
    [Fact]
    public void Una_cancion_que_ya_salio_como_single_antes_del_plazo_se_reconoce()
    {
        var album = Lanzamiento(10, "El Álbum", "2026-10-10", TipoLanzamiento.Album);
        var catalogo = new[]
        {
            album,
            Lanzamiento(11, "Monaco", "2024-03-01"),
            Lanzamiento(12, "Monaco (Remix)", "2023-10-13"),
            Lanzamiento(13, "Otra", "2026-10-01"),
            Lanzamiento(14, "Otra", "2026-09-25"),   // dentro del plazo: es parte de la misma novedad
        };
        var desde = NovedadesDeezer.Desde(Hoy, 30);

        Assert.Equal((new DateTime(2023, 10, 13), "Monaco (Remix)"), NovedadesDeezer.PublicadaEnElCatalogo(Pista("Monaco", album), catalogo, desde));
        Assert.Null(NovedadesDeezer.PublicadaEnElCatalogo(Pista("Otra", album), catalogo, desde));
        Assert.Null(NovedadesDeezer.PublicadaEnElCatalogo(Pista("El Álbum", album), catalogo, desde));   // él mismo no cuenta
    }

    [Theory]
    [InlineData("USUM71703861", 2017, 2017)]
    [InlineData("QMFMF9800001", 2026, 1998)]
    [InlineData("QM-6MZ-26-00001", 2026, 2026)]
    [InlineData("USUM7", 2026, 0)]
    [InlineData("", 2026, 0)]
    [InlineData("QMFMFAB00001", 2026, 0)]
    public void El_anio_del_isrc_va_en_sus_cifras_seis_y_siete(string isrc, int anioLanzamiento, int esperado)
        => Assert.Equal(esperado, NovedadesDeezer.AnioDeIsrc(isrc, anioLanzamiento));

    // El año del ISRC no basta para decir que es antigua (MEDIDO: solo 61 de 119 habían salido antes); solo decide a cuáles preguntar.
    [Fact]
    public void Se_pregunta_por_el_isrc_solo_si_es_de_dos_anios_antes_o_mas_y_no_se_sabe_ya()
    {
        var l = Lanzamiento(1, "Nuevo", "2026-10-01");
        Assert.True(NovedadesDeezer.MerecePreguntarPorIsrc(Pista("A", l, isrc: "QZDYA2400001")));
        Assert.False(NovedadesDeezer.MerecePreguntarPorIsrc(Pista("B", l, isrc: "QZDYA2500001")));
        Assert.False(NovedadesDeezer.MerecePreguntarPorIsrc(Pista("C", l, isrc: "")));
        Assert.False(NovedadesDeezer.MerecePreguntarPorIsrc(Pista("D", l, isrc: "QZDYA1800001") with { PublicadaAntes = new DateTime(2018, 1, 1) }));
    }

    [Fact]
    public void La_misma_cancion_en_el_single_y_en_el_album_sale_una_vez_con_la_primera_publicacion()
    {
        var single = Lanzamiento(1, "Bicho raro", "2026-09-25");
        var album = Lanzamiento(2, "El disco", "2026-10-16", TipoLanzamiento.Album);
        var pistas = new[]
        {
            Pista("Bicho raro", album, "Estopa", posicion: 3) with { Por = "Estopa" },
            Pista("Otra", album, "Estopa", posicion: 1) with { Por = "Estopa" },
            Pista("Bicho raro", single, "Estopa") with { Por = "Estopa, Rosalía", DeArtistaDudoso = true },
        };

        var u = NovedadesDeezer.Unir(pistas);

        Assert.Equal(2, u.Count);
        Assert.Equal("Otra", u[0].Titulo);                 // la más reciente primero
        Assert.Equal(1, u[1].Lanzamiento.Id);              // el single, que salió antes
        Assert.Equal("Estopa, Rosalía", u[1].Por);
        Assert.False(u[1].DeArtistaDudoso);                // la trae también un artista confirmado
    }

    // =============================================================================================
    // Los artistas de la biblioteca
    // =============================================================================================

    [Fact]
    public void Los_principales_cuentan_cada_artista_del_credito_y_apartan_pools_usuarios_y_ediciones()
    {
        var canciones = new List<Track>
        {
            Cancion("Ozuna, Daddy Yankee Ft Quevedo", "Uno"),
            Cancion("Ozuna", "Dos"),
            Cancion("Ozuna (Bayne Mashup)", "Tres"),
            Cancion("Daddy Yankee", "Gasolina"),
            Cancion("Quevedo", "Columbia"),
            Cancion("@liyo_dj98", "Algo"), Cancion("@liyo_dj98", "Otra"),
            Cancion("Latin Box", "Pack 1"), Cancion("Latin Box", "Pack 2"),
            Cancion("Various Artists", "X1"), Cancion("Various Artists", "X2"),
            Cancion("40 Mashups by Alex Gonzalez", "M1"), Cancion("40 Mashups by Alex Gonzalez", "M2"),
            Cancion("Solo Una", "Cancion"),
            // Sin artista en las etiquetas: se lee del nombre del archivo.
            new() { FilePath = Rutas.Archivo("Feid - Normal.mp3"), Folder = Rutas.Carpeta },
            Cancion("Feid", "Porfa"),
        };

        var p = ArtistasDeLaBiblioteca.Principales(canciones, 10, new ArtistAliases());

        Assert.Equal(new[] { "Ozuna", "Daddy Yankee", "Feid", "Quevedo" }, p.Select(a => a.Nombre));
        Assert.Equal(3, p[0].Canciones);
        Assert.Contains(ArtistasDeLaBiblioteca.ClaveTitulo("Normal"), p.Single(a => a.Nombre == "Feid").Titulos);
    }

    [Fact]
    public void Los_alias_suman_al_mismo_artista_y_los_excluidos_dejan_su_sitio()
    {
        var canciones = new List<Track>
        {
            Cancion("Cruz Cafuné", "Contando Lunares"), Cancion("Cruzzi", "Me Rindo"),
            Cancion("Bad Bunny", "A"), Cancion("Bad Bunny", "B"), Cancion("Bad Bunny", "C"),
            Cancion("Feid", "Normal"), Cancion("Feid", "Porfa"),
        };
        var alias = new ArtistAliases();

        var todos = ArtistasDeLaBiblioteca.Principales(canciones, 2, alias);
        Assert.Equal(new[] { "Bad Bunny", "Cruz Cafuné" }, todos.Select(a => a.Nombre));   // a igualdad, por clave
        Assert.Equal(2, todos[1].Canciones);

        var sinBb = ArtistasDeLaBiblioteca.Principales(canciones, 2, alias, excluido: k => k == IndiceArtistasClave("Bad Bunny"));
        Assert.Equal(new[] { "Cruz Cafuné", "Feid" }, sinBb.Select(a => a.Nombre));
    }

    // =============================================================================================
    // El almacén de correspondencias
    // =============================================================================================

    private sealed class Carpeta : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "etq-novedades-" + Guid.NewGuid().ToString("N"));
        public string Archivo => Path.Combine(Dir, AlmacenArtistasDeezer.NombreArchivo);
        public Carpeta() => Directory.CreateDirectory(Dir);
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { /* best-effort */ } }
    }

    private static ArtistaDeezerGuardado Guardado(string nombre, long id, EstadoArtistaDeezer estado, DateTime? fecha = null)
        => new() { Clave = IndiceArtistasClave(nombre), Nombre = nombre, DeezerId = id, DeezerNombre = nombre, Fans = 10, Estado = estado,
                   Coincidencias = estado == EstadoArtistaDeezer.Confirmado ? 2 : 0, Fecha = fecha ?? Hoy };

    [Fact]
    public void El_almacen_guarda_y_vuelve_a_leer_lo_averiguado_con_las_correcciones_y_exclusiones()
    {
        using var c = new Carpeta();
        var a = new AlmacenArtistasDeezer(c.Archivo);
        a.Poner(Guardado("Bad Bunny", 27, EstadoArtistaDeezer.Confirmado));
        a.Corregir(IndiceArtistasClave("Lennox"), "Lennox", new ArtistaDeezer(164, "Zion & Lennox", 2_000_000, 80), Hoy);
        a.Excluir(IndiceArtistasClave("4BEATS"), "4BEATS", true, Hoy);
        Assert.Equal("", a.Guardar());

        var b = new AlmacenArtistasDeezer(c.Archivo);

        Assert.Equal("", b.AvisoCarga);
        Assert.Equal(3, b.Count);
        Assert.Equal(27, b.Buscar(IndiceArtistasClave("Bad Bunny"))!.DeezerId);
        Assert.Equal(EstadoArtistaDeezer.Manual, b.Buscar(IndiceArtistasClave("Lennox"))!.Estado);
        Assert.True(b.EstaExcluido(IndiceArtistasClave("4BEATS")));
        Assert.Equal(new[] { "4BEATS" }, b.Excluidos().Select(x => x.Nombre));
    }

    [Fact]
    public void Una_busqueda_automatica_no_pisa_una_correccion_ni_quita_una_exclusion()
    {
        using var c = new Carpeta();
        var a = new AlmacenArtistasDeezer(c.Archivo);
        var clave = IndiceArtistasClave("Lennox");
        a.Corregir(clave, "Lennox", new ArtistaDeezer(164, "Zion & Lennox", 2_000_000, 80), Hoy);
        a.Excluir(clave, "Lennox", true, Hoy);

        a.Poner(Guardado("Lennox", 999, EstadoArtistaDeezer.Dudoso));

        var g = a.Buscar(clave)!;
        Assert.Equal(164, g.DeezerId);
        Assert.True(g.Excluido);
    }

    [Fact]
    public void Olvidar_quita_lo_averiguado_pero_conserva_la_exclusion()
    {
        using var c = new Carpeta();
        var a = new AlmacenArtistasDeezer(c.Archivo);
        a.Poner(Guardado("Bad Bunny", 27, EstadoArtistaDeezer.Confirmado));
        a.Poner(Guardado("Kike", 34, EstadoArtistaDeezer.Dudoso));
        a.Excluir(IndiceArtistasClave("Kike"), "Kike", true, Hoy);

        Assert.True(a.Olvidar(IndiceArtistasClave("Bad Bunny")));
        Assert.True(a.Olvidar(IndiceArtistasClave("Kike")));
        Assert.False(a.Olvidar(IndiceArtistasClave("Nadie")));

        Assert.Null(a.Buscar(IndiceArtistasClave("Bad Bunny")));
        var kike = a.Buscar(IndiceArtistasClave("Kike"))!;
        Assert.True(kike.Excluido);
        Assert.Equal(0, kike.DeezerId);
        Assert.True(AlmacenArtistasDeezer.HayQueIdentificar(kike, Hoy));   // en cuanto se vuelva a incluir
    }

    [Fact]
    public void Solo_se_vuelve_a_identificar_lo_nuevo_y_lo_dudoso_o_no_encontrado_de_hace_mas_de_30_dias()
    {
        Assert.True(AlmacenArtistasDeezer.HayQueIdentificar(null, Hoy));
        Assert.False(AlmacenArtistasDeezer.HayQueIdentificar(Guardado("A", 1, EstadoArtistaDeezer.Confirmado, Hoy.AddDays(-400)), Hoy));
        Assert.False(AlmacenArtistasDeezer.HayQueIdentificar(Guardado("A", 1, EstadoArtistaDeezer.Manual, Hoy.AddDays(-400)), Hoy));
        Assert.False(AlmacenArtistasDeezer.HayQueIdentificar(Guardado("A", 1, EstadoArtistaDeezer.Dudoso, Hoy.AddDays(-10)), Hoy));
        Assert.True(AlmacenArtistasDeezer.HayQueIdentificar(Guardado("A", 1, EstadoArtistaDeezer.Dudoso, Hoy.AddDays(-31)), Hoy));
        Assert.True(AlmacenArtistasDeezer.HayQueIdentificar(Guardado("A", 0, EstadoArtistaDeezer.NoEncontrado, Hoy.AddDays(-31)), Hoy));
    }

    // Son correcciones del usuario: un archivo dañado se aparta en vez de pisarse.
    [Fact]
    public void Un_archivo_ilegible_se_aparta_y_se_empieza_de_cero()
    {
        using var c = new Carpeta();
        File.WriteAllText(c.Archivo, "{ esto no es json");

        var a = new AlmacenArtistasDeezer(c.Archivo);

        Assert.Equal(0, a.Count);
        Assert.Contains("ilegible", a.AvisoCarga);
        Assert.Single(Directory.GetFiles(c.Dir, AlmacenArtistasDeezer.NombreArchivo + ".ilegible_*"));
        Assert.Equal("", a.Guardar());
    }

    // =============================================================================================
    // La búsqueda entera, contra una red de mentira
    // =============================================================================================

    private const string BusquedaBadBunny = """
        {"data":[{"id":900,"name":"Bad Bunny","nb_album":3,"nb_fan":26},{"id":27,"name":"Bad Bunny","nb_album":120,"nb_fan":8045190}]}
        """;

    // El de verdad: dos singles que están en la biblioteca, un álbum reciente y un preestreno.
    private const string LanzamientosBadBunny = """
        {"data":[
          {"id":5001,"title":"DeBÍ TiRAR MáS FOTOS","release_date":"2026-10-10","record_type":"album"},
          {"id":4001,"title":"Monaco","release_date":"2023-10-13","record_type":"single"},
          {"id":4002,"title":"Dakiti","release_date":"2020-10-30","record_type":"single"},
          {"id":5002,"title":"Futuro","release_date":"2026-11-15","record_type":"single"}
        ]}
        """;

    // El homónimo: también ha publicado algo este mes, que no debe salir.
    private const string LanzamientosHomonimo = """
        {"data":[{"id":7001,"title":"Bad Bunny Beats","release_date":"2026-10-01","record_type":"album"}]}
        """;

    private const string PistasAlbum = """
        {"data":[
          {"id":51,"title":"NUEVAYoL","duration":183,"isrc":"QM6MZ2600001","track_position":1,"disk_number":1,"link":"https://www.deezer.com/track/51","artist":{"name":"Bad Bunny"}},
          {"id":52,"title":"Monaco","duration":267,"isrc":"USA2P2300001","track_position":2,"disk_number":1,"link":"https://www.deezer.com/track/52","artist":{"name":"Bad Bunny"}},
          {"id":53,"title":"Baile Inolvidable","duration":367,"isrc":"QMFMF1800001","track_position":3,"disk_number":1,"link":"https://www.deezer.com/track/53","artist":{"name":"Bad Bunny"}}
        ]}
        """;

    private static MontajeApi Red(bool cache = true)
    {
        var m = new MontajeApi(cache: cache);
        m.Red.Responde("search/artist?q=Bad", BusquedaBadBunny)
             .Responde("search/artist?q=Estopa", """{"data":[{"id":77,"name":"Estopa","nb_album":40,"nb_fan":227212}]}""")
             .Responde("/artist/27/albums", LanzamientosBadBunny)
             .Responde("/artist/900/albums", LanzamientosHomonimo)
             .Responde("/artist/900/top", """{"data":[{"title":"Beat 1","title_short":"Beat 1"}]}""")
             // Los títulos de Estopa en la biblioteca no son de singles: se reconocen por sus canciones más escuchadas.
             .Responde("/artist/77/albums", """
                 {"data":[{"id":6001,"title":"Bicho raro","release_date":"2026-10-16","record_type":"single"},
                          {"id":6000,"title":"La Calle Es Tuya","release_date":"2004-03-08","record_type":"album"}]}
                 """)
             .Responde("/artist/77/top", """{"data":[{"title":"Como Camarón","title_short":"Como Camarón"},{"title":"Vino Tinto","title_short":"Vino Tinto"}]}""")
             .Responde("/album/5001/tracks", PistasAlbum)
             .Responde("/album/6001/tracks", """
                 {"data":[{"id":61,"title":"Bicho raro","duration":200,"isrc":"ES5022600836","track_position":1,"link":"https://www.deezer.com/track/61","artist":{"name":"Estopa"}}]}
                 """)
             .Responde("/track/isrc:QMFMF1800001", """{"id":53,"album":{"title":"Viejo","release_date":"2018-05-01"}}""");
        return m;
    }

    private static readonly ArtistaBiblioteca BadBunny = Artista("Bad Bunny", "Monaco", "Dakiti", "Titi Me Pregunto");
    private static readonly ArtistaBiblioteca Estopa = Artista("Estopa", "Como Camaron", "Vino Tinto");

    [Fact]
    public async Task Elige_al_artista_bueno_entre_homonimos_y_trae_sus_novedades_con_lo_ya_publicado()
    {
        using var m = Red();
        using var c = new Carpeta();
        var almacen = new AlmacenArtistasDeezer(c.Archivo);
        var guardados = new List<string>();

        var r = await new NovedadesDeezer(m.Api).BuscarAsync(new[] { BadBunny, Estopa }, almacen, 30, Hoy, null, CancellationToken.None,
                                                             guardados.Add);

        Assert.False(r.Cancelada);
        Assert.Equal(new[] { "Bad Bunny", "Estopa" }, r.Artistas.Select(a => a.Artista.Nombre));
        Assert.All(r.Artistas, a => Assert.False(a.FalloDeRed));
        Assert.Equal(27, almacen.Buscar(BadBunny.Clave)!.DeezerId);
        Assert.Equal(EstadoArtistaDeezer.Confirmado, almacen.Buscar(BadBunny.Clave)!.Estado);
        Assert.Equal(EstadoArtistaDeezer.Confirmado, almacen.Buscar(Estopa.Clave)!.Estado);   // por sus canciones más escuchadas
        Assert.Equal(2, r.Lanzamientos);   // el álbum y el single; ni el preestreno ni lo del homónimo

        Assert.Equal(new[] { "Bicho raro", "NUEVAYoL", "Monaco", "Baile Inolvidable" }, r.Pistas.Select(p => p.Titulo));
        var monaco = r.Pistas.Single(p => p.Titulo == "Monaco");
        Assert.Equal(new DateTime(2023, 10, 13), monaco.PublicadaAntes);   // el single de su catálogo
        var baile = r.Pistas.Single(p => p.Titulo == "Baile Inolvidable");
        Assert.Equal(new DateTime(2018, 5, 1), baile.PublicadaAntes);      // lo dice Deezer al preguntar por su ISRC
        Assert.Equal("Viejo", baile.PublicadaEn);
        Assert.True(r.Pistas.Single(p => p.Titulo == "NUEVAYoL").EsEstreno);
        Assert.All(r.Pistas, p => Assert.False(p.DeArtistaDudoso));
        Assert.Equal("Bad Bunny", monaco.Por);

        Assert.Equal(0, m.Red.Cuantas("/artist/27/top"));   // con dos singles en común ya estaba confirmado
        Assert.Equal(0, m.Red.Cuantas("/album/7001/tracks"));
        Assert.Equal(0, m.Red.Cuantas("/track/isrc:QM6MZ"));   // un ISRC del año no hace preguntar
        Assert.Contains("", guardados);
        Assert.True(File.Exists(c.Archivo));
    }

    // La caché de red dura 30 días: sin pedir el catálogo sin caché nunca saldría nada nuevo.
    [Fact]
    public async Task La_segunda_busqueda_no_vuelve_a_identificar_pero_si_vuelve_a_pedir_los_lanzamientos()
    {
        using var m = Red(cache: true);
        using var c = new Carpeta();
        var almacen = new AlmacenArtistasDeezer(c.Archivo);
        var dz = new NovedadesDeezer(m.Api);
        await dz.BuscarAsync(new[] { BadBunny }, almacen, 30, Hoy, null, CancellationToken.None);

        // Sin nadie que escuche los guardados: el almacén se guarda igual (antes no se guardaba nada).
        var otra = await dz.BuscarAsync(new[] { BadBunny }, new AlmacenArtistasDeezer(c.Archivo), 30, Hoy, null, CancellationToken.None);

        Assert.Equal(3, otra.Pistas.Count);
        Assert.Equal(1, m.Red.Cuantas("search/artist"));
        Assert.Equal(1, m.Red.Cuantas("/artist/900/albums"));   // el homónimo no se vuelve a comprobar
        Assert.Equal(2, m.Red.Cuantas("/artist/27/albums"));
        Assert.Equal(1, m.Red.Cuantas("/album/5001/tracks"));   // las canciones de un lanzamiento valen una semana
        Assert.Equal(1, m.Red.Cuantas("/track/isrc:"));
    }

    [Fact]
    public async Task Una_correccion_a_mano_se_respeta_y_no_se_busca_al_artista()
    {
        using var m = Red();
        using var c = new Carpeta();
        var almacen = new AlmacenArtistasDeezer(c.Archivo);
        almacen.Corregir(BadBunny.Clave, BadBunny.Nombre, new ArtistaDeezer(900, "Bad Bunny", 26, 3), Hoy);

        var r = await new NovedadesDeezer(m.Api).BuscarAsync(new[] { BadBunny }, almacen, 30, Hoy, null, CancellationToken.None);

        Assert.Equal(0, m.Red.Cuantas("search/artist"));
        Assert.Equal(900, r.Artistas[0].Deezer!.DeezerId);
        Assert.Equal(EstadoArtistaDeezer.Manual, r.Artistas[0].Deezer!.Estado);
        Assert.Equal(1, r.Lanzamientos);   // el del artista indicado, aunque no tenga canciones en común
        Assert.Equal(1, m.Red.Cuantas("/album/7001/tracks"));
    }

    [Fact]
    public async Task Sin_canciones_en_comun_queda_sin_confirmar_y_sus_canciones_van_marcadas()
    {
        using var m = new MontajeApi();
        m.Red.Responde("search/artist?q=Kike", """{"data":[{"id":34,"name":"Kike","nb_album":2,"nb_fan":34}]}""")
             .Responde("/artist/34/albums", """{"data":[{"id":8001,"title":"Brasil","release_date":"2026-10-12","record_type":"single"}]}""")
             .Responde("/artist/34/top", """{"data":[{"title":"Brasil"}]}""")
             .Responde("/album/8001/tracks", """{"data":[{"id":81,"title":"Brasil","duration":150,"track_position":1,"artist":{"name":"Kike"}}]}""");
        using var c = new Carpeta();
        var almacen = new AlmacenArtistasDeezer(c.Archivo);

        var r = await new NovedadesDeezer(m.Api).BuscarAsync(new[] { Artista("Kike", "Pa Ti", "Mi Gente") }, almacen, 30, Hoy, null,
                                                             CancellationToken.None);

        Assert.Equal(EstadoArtistaDeezer.Dudoso, almacen.Buscar(IndiceArtistasClave("Kike"))!.Estado);
        Assert.True(Assert.Single(r.Pistas).DeArtistaDudoso);
        Assert.Empty(PresentacionNovedades.Filtrar(r.Pistas, new FiltroNovedades()));
        Assert.Single(PresentacionNovedades.Filtrar(r.Pistas, new FiltroNovedades(IncluirSinConfirmar: true)));
    }

    // Si Deezer no responde, no se sabe nada de ese artista: no se guarda ni se da por no encontrado.
    [Fact]
    public async Task Si_deezer_no_responde_el_artista_queda_marcado_y_no_se_guarda_nada_de_el()
    {
        using var m = new MontajeApi();
        m.Red.Responde("search/artist?q=Bad", BusquedaBadBunny).SinConexion("/artist/");
        using var c = new Carpeta();
        var almacen = new AlmacenArtistasDeezer(c.Archivo);

        var r = await new NovedadesDeezer(m.Api).BuscarAsync(new[] { BadBunny }, almacen, 30, Hoy, null, CancellationToken.None);

        Assert.True(r.Artistas[0].FalloDeRed);
        Assert.Null(almacen.Buscar(BadBunny.Clave));
        Assert.Empty(r.Pistas);
        Assert.Contains("Deezer no respondió con 1 artista", PresentacionNovedades.Resumen(r, 30, 0));
    }

    [Fact]
    public async Task Cancelar_devuelve_lo_que_haya_sin_lanzar_excepcion()
    {
        using var m = Red();
        using var c = new Carpeta();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = await new NovedadesDeezer(m.Api).BuscarAsync(new[] { BadBunny, Estopa }, new AlmacenArtistasDeezer(c.Archivo), 30, Hoy,
                                                             null, cts.Token);

        Assert.True(r.Cancelada);
        Assert.Empty(r.Pistas);
        Assert.Contains("cancelada", PresentacionNovedades.Resumen(r, 30, 0));
    }

    [Fact]
    public async Task El_avance_va_por_fases_y_nunca_retrocede()
    {
        using var m = Red();
        using var c = new Carpeta();
        var avisos = new List<ProgresoNovedades>();
        var progreso = new ProgresoSincrono(avisos);

        await new NovedadesDeezer(m.Api).BuscarAsync(new[] { BadBunny, Estopa }, new AlmacenArtistasDeezer(c.Archivo), 30, Hoy, progreso,
                                                     CancellationToken.None);

        List<ProgresoNovedades> copia;
        lock (avisos) copia = avisos.ToList();
        Assert.Contains(copia, p => p.Fase == 1 && p.Hechos == 2 && p.Total == 2);
        Assert.Contains(copia, p => p.Fase == 2 && p.Hechos == 2 && p.Total == 2);
        Assert.Contains(copia, p => p.Fase == 3 && p.Total == 1);
        var avance = copia.OrderBy(p => p.Fase).ThenBy(p => p.Hechos).Select(PresentacionNovedades.Avance).ToList();
        Assert.Equal(avance.OrderBy(x => x), avance);
        Assert.InRange(avance[^1], 99.9, 100);
    }

    private sealed class ProgresoSincrono(List<ProgresoNovedades> lista) : IProgress<ProgresoNovedades>
    {
        public void Report(ProgresoNovedades value) { lock (lista) lista.Add(value); }
    }

    // =============================================================================================
    // Lo que se enseña
    // =============================================================================================

    [Fact]
    public void Los_filtros_de_tipo_y_estrenos_deciden_que_canciones_forman_la_lista()
    {
        var single = Lanzamiento(1, "S", "2026-10-01");
        var ep = Lanzamiento(2, "E", "2026-10-02", TipoLanzamiento.Ep);
        var album = Lanzamiento(3, "A", "2026-10-03", TipoLanzamiento.Album);
        var recop = Lanzamiento(4, "Grandes éxitos", "2026-10-04", TipoLanzamiento.Recopilatorio);
        var remaster = Lanzamiento(5, "Clásico (Remastered)", "2026-10-05", TipoLanzamiento.Album);
        var pistas = new[]
        {
            Pista("s1", single), Pista("e1", ep), Pista("a1", album),
            Pista("a2", album) with { PublicadaAntes = new DateTime(2025, 1, 1) },
            Pista("r1", recop), Pista("m1", remaster),
        };

        string Titulos(FiltroNovedades f) => string.Join(",", PresentacionNovedades.Filtrar(pistas, f).Select(p => p.Titulo));

        Assert.Equal("s1,e1,a1,a2,r1,m1", Titulos(new FiltroNovedades()));
        Assert.Equal("s1", Titulos(new FiltroNovedades(FiltroTipoLanzamiento.Singles)));
        Assert.Equal("e1", Titulos(new FiltroNovedades(FiltroTipoLanzamiento.Ep)));
        Assert.Equal("a1,a2,r1,m1", Titulos(new FiltroNovedades(FiltroTipoLanzamiento.Albumes)));
        Assert.Equal("s1,e1,a1,r1", Titulos(new FiltroNovedades(SoloEstrenos: true)));
    }

    [Fact]
    public void Las_canciones_pasan_a_filas_numeradas_con_su_enlace_y_duracion()
    {
        var l = Lanzamiento(1, "S", "2026-10-01");
        var filas = PresentacionNovedades.ComoFilas(new[] { Pista("Uno", l, posicion: 1), Pista("Dos", l, posicion: 2) });

        Assert.Equal(new[] { 1, 2 }, filas.Select(f => f.Position));
        Assert.Equal("Bad Bunny", filas[0].Artist);
        Assert.Equal("3:00", filas[0].DurText);
        Assert.Equal("https://www.deezer.com/track/12", filas[1].Link);
    }

    [Fact]
    public void El_origen_en_pendientes_dice_de_que_lanzamiento_sale()
    {
        Assert.Equal("Novedades: Bicho raro (single, 16/10/2026)", PresentacionNovedades.Origen(Pista("Bicho raro", Lanzamiento(1, "Bicho raro", "2026-10-16"))));
        Assert.Equal("Novedades: Lo Nuevo (EP, 01/10/2026)", PresentacionNovedades.Origen(Pista("x", Lanzamiento(2, "Lo Nuevo", "2026-10-01", TipoLanzamiento.Ep))));
        Assert.Equal("Novedades: Disco (álbum, 02/10/2026)", PresentacionNovedades.Origen(Pista("x", Lanzamiento(3, "Disco", "2026-10-02", TipoLanzamiento.Album))));
    }

    [Fact]
    public void El_aviso_dice_si_ya_salio_si_es_reedicion_version_alterada_o_de_artista_sin_confirmar()
    {
        var l = Lanzamiento(1, "Algo", "2026-10-01");
        Assert.Equal("", PresentacionNovedades.Aviso(Pista("Nueva", l)));
        Assert.Equal("Publicada antes, el 13/10/2023 en «Monaco»",
                     PresentacionNovedades.Aviso(Pista("Monaco", l) with { PublicadaAntes = new DateTime(2023, 10, 13), PublicadaEn = "Monaco" }));
        Assert.Equal("Reedición o remasterización", PresentacionNovedades.Aviso(Pista("Clásico (Remastered 2026)", l)));
        Assert.Equal("Versión acelerada, ralentizada, instrumental o similar · Artista sin confirmar en Deezer",
                     PresentacionNovedades.Aviso(Pista("Tema (Sped Up)", l) with { DeArtistaDudoso = true }));
    }

    // Casos reales de los lanzamientos de 90 días de 200 artistas.
    [Theory]
    [InlineData("pAPi wiTH tOKisCha (TXURK and MC Kaique SP REMIX) [SUPERSLOWED]", true)]
    [InlineData("pAPi wiTH tOKisCha (TXURK and MC Kaique SP REMIX) [ULTRASLOWED]", true)]
    [InlineData("pAPi wiTH tOKisCha [SPED UP]", true)]
    [InlineData("Ritmada de Halloween (Ultra Slow Remix)", true)]
    [InlineData("Ritmada de Halloween (Speed Remix)", true)]
    [InlineData("GOSTER TEKRAR (burakDoa Remix SLOW VERSION)", true)]
    [InlineData("DROGA DE DISEÑADOR (Instrumental)", true)]
    [InlineData("Slow Burn", false)]
    [InlineData("Speed", false)]
    [InlineData("NUEVAYoL", false)]
    public void Las_versiones_aceleradas_ralentizadas_e_instrumentales_se_reconocen(string titulo, bool alterada)
        => Assert.Equal(alterada, Pista(titulo, Lanzamiento(1, "Otro título", "2026-10-01")).EsVersionAlterada);

    [Fact]
    public void El_resumen_concuerda_en_numero()
    {
        ConsultaArtista Consulta(string n, EstadoArtistaDeezer e)
            => new(Artista(n, "x1x"), new ArtistaDeezerGuardado { Clave = n, Nombre = n, Estado = e }, 0, false);
        var uno = new ResultadoNovedades(new[] { Pista("a", Lanzamiento(1, "S", "2026-10-01")) },
                                         new[] { Consulta("A", EstadoArtistaDeezer.Confirmado) }, 1, 0, false, TimeSpan.Zero);
        var varios = new ResultadoNovedades(Array.Empty<PistaNovedad>(),
                                            new[] { Consulta("A", EstadoArtistaDeezer.Dudoso), Consulta("B", EstadoArtistaDeezer.Dudoso),
                                                    Consulta("C", EstadoArtistaDeezer.NoEncontrado) }, 0, 2, false, TimeSpan.Zero);

        Assert.Equal("1 artista consultado: 1 lanzamiento en los últimos 30 días, con 1 canción.", PresentacionNovedades.Resumen(uno, 30, 0));
        Assert.Equal("3 artistas consultados: ningún lanzamiento en los últimos 60 días. 2 artistas sin confirmar en Deezer (5 canciones que no se muestran). "
                   + "1 artista no está en Deezer con ese nombre. No se pudieron leer las canciones de 2 lanzamientos.",
                     PresentacionNovedades.Resumen(varios, 60, 5));
    }

    [Fact]
    public void Lo_que_se_dice_al_rellenar_la_tabla_explica_por_que_esta_vacia()
    {
        Assert.Equal("1 artista consultado: ningún lanzamiento en los últimos 30 días.",
                     PresentacionNovedades.TextoTabla(0, 0, "", "", "1 artista consultado: ningún lanzamiento en los últimos 30 días.", false));
        Assert.StartsWith("Ninguna canción pasa los filtros", PresentacionNovedades.TextoTabla(5, 0, "", "", "x", false));
        Assert.Equal("1 de 3 en la biblioteca (33 %). Faltan 2.", PresentacionNovedades.TextoTabla(3, 3, "1 de 3 en la biblioteca (33 %)", "Faltan 2.", "x", false));
        Assert.EndsWith("Búsqueda cancelada: es lo encontrado hasta ese momento.", PresentacionNovedades.TextoTabla(3, 3, "a", "b", "x", true));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    [InlineData(260, 300)]
    [InlineData(5000, 300)]
    [InlineData(0, 100)]
    [InlineData(-3, 100)]
    public void Un_numero_de_artistas_guardado_fuera_de_las_opciones_se_lleva_a_la_mas_cercana(int guardado, int esperado)
        => Assert.Equal(esperado, PresentacionNovedades.Normalizar(guardado, PresentacionNovedades.OpcionesArtistas, PresentacionNovedades.ArtistasPorDefecto));

    [Fact]
    public void La_configuracion_trae_de_fabrica_100_artistas_y_30_dias()
    {
        var cfg = new AppConfig();
        Assert.Equal(PresentacionNovedades.ArtistasPorDefecto, cfg.NovedadesArtistas);
        Assert.Equal(PresentacionNovedades.DiasPorDefecto, cfg.NovedadesDias);
        Assert.Contains(cfg.NovedadesArtistas, PresentacionNovedades.OpcionesArtistas);
        Assert.Contains(cfg.NovedadesDias, PresentacionNovedades.OpcionesDias);
    }

    [Fact]
    public void La_tabla_de_artistas_dice_lo_que_hay_ahora_en_el_almacen_y_anade_los_excluidos()
    {
        using var c = new Carpeta();
        var almacen = new AlmacenArtistasDeezer(c.Archivo);
        almacen.Poner(Guardado("Bad Bunny", 27, EstadoArtistaDeezer.Confirmado));
        almacen.Poner(Guardado("Lennox", 71, EstadoArtistaDeezer.Dudoso));
        almacen.Excluir(IndiceArtistasClave("4BEATS"), "4BEATS", true, Hoy);
        var consultas = new[]
        {
            new ConsultaArtista(Artista("Bad Bunny", "Monaco"), almacen.Buscar(IndiceArtistasClave("Bad Bunny")), 3, false),
            new ConsultaArtista(Artista("Lennox", "Algo"), almacen.Buscar(IndiceArtistasClave("Lennox")), 2, false),
        };
        // Después de la búsqueda, el usuario corrige a Lennox.
        almacen.Corregir(IndiceArtistasClave("Lennox"), "Lennox", new ArtistaDeezer(164, "Zion & Lennox", 2_000_000, 80), Hoy);

        var filas = PresentacionNovedades.FilasArtistas(consultas, almacen, new HashSet<string> { IndiceArtistasClave("Lennox") });

        Assert.Equal(new[] { "Bad Bunny", "Lennox", "4BEATS" }, filas.Select(f => f.Nombre));
        Assert.Equal("Confirmado (2 canciones en común)", filas[0].EstadoTexto);
        Assert.Equal("3", filas[0].RecientesTexto);
        Assert.Equal("Corregido a mano", filas[1].EstadoTexto);
        Assert.Equal("Zion & Lennox", filas[1].DeezerNombre);
        Assert.Equal("", filas[1].RecientesTexto);   // lo de la búsqueda era del artista anterior
        Assert.True(filas[2].Excluido);
        Assert.StartsWith("Excluido", filas[2].EstadoTexto);
    }

    [Fact]
    public void Al_corregir_un_artista_se_quitan_las_canciones_que_salieron_solo_por_el()
    {
        var l = Lanzamiento(1, "S", "2026-10-01");
        var pistas = new[]
        {
            Pista("Suya", l) with { Por = "Lennox" },
            Pista("De los dos", l) with { Por = "Zion, Lennox" },
            Pista("Ajena", l) with { Por = "Bad Bunny" },
        };

        var quedan = PresentacionNovedades.SinLasDe(pistas, "lennox");

        Assert.Equal(new[] { "De los dos", "Ajena" }, quedan.Select(p => p.Titulo));
        Assert.Equal("Zion", quedan[0].Por);
    }

    // =============================================================================================
    // La página
    // =============================================================================================

    private static string Fuente(params string[] tramos)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "MainWindow.axaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(tramos)));
    }

    // El mismo menú que Tendencias y Playlists, en el orden común (ver MenusCancionTests).
    [Fact]
    public void El_menu_de_la_tabla_de_novedades_sigue_el_orden_comun()
    {
        var texto = Fuente("Etiquetador.App", "Views", "NovedadesView.axaml");
        var bloque = Regex.Match(texto, @"<DataGrid\.ContextMenu>(.*?)</DataGrid\.ContextMenu>", RegexOptions.Singleline);
        Assert.True(bloque.Success);
        var rotulos = Regex.Matches(bloque.Groups[1].Value, @"<MenuItem\s+Header=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();

        foreach (var comun in new[] { AccionesCancion.TextoEscuchar, AccionesCancion.TextoEditar, AccionesCancion.TextoCompatibles,
                                      AccionesCancion.TextoVersiones, AccionesCancion.TextoCarpeta, "Añadir a pendientes", "Buscar en mi pool" })
            Assert.Contains(comun, rotulos);
        var puestos = rotulos.Select(AccionesCancion.Puesto).Where(p => p >= 0).ToList();
        Assert.Equal(puestos.OrderBy(p => p), puestos);
    }

    // La página dice lo que /artist/{id}/albums no trae: las colaboraciones en lanzamientos de otros.
    [Fact]
    public void La_pagina_avisa_de_que_no_salen_las_colaboraciones_en_lanzamientos_de_otros()
        => Assert.Contains("colaboraciones como invitado en lanzamientos de otros artistas normalmente no aparecen", Fuente("Etiquetador.App", "Views", "NovedadesView.axaml"));

    [Fact]
    public void La_pagina_esta_en_la_barra_lateral_con_su_vista()
    {
        var vm = Fuente("Etiquetador.App", "ViewModels", "MainViewModel.cs");
        var ventana = Fuente("Etiquetador.App", "Views", "MainWindow.axaml.cs");
        Assert.Matches(@"Indice = 20, Icono = ""[^""]+"", Nombre = ""Novedades""", vm);
        Assert.Matches(@"\[20\]\s*=\s*\(\(\) => new NovedadesView\(\)", ventana);
    }
}
