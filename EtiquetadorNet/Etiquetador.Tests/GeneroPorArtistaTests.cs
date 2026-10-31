using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Copias;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// «Género por carpeta y artista» ampliado con el género por artista: el dominante sin contar los
/// géneros genéricos (A3), el mapa artista → género del usuario (A4), qué se propone y qué sale
/// marcado según el género que ya tiene cada canción (A5) y que solo se sustituye lo que sigue como
/// se vio al proponer, con deshacer (A6). Los casos son los de una biblioteca real.
/// </summary>
public class GeneroPorArtistaTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Una canción, con el título también en el nombre del archivo (como en una biblioteca de verdad).</summary>
    private static Track T(string artista, string titulo, string? genero, string carpeta = "Musica", string? album = null)
        => new()
        {
            FilePath = Rutas.De("M", carpeta, $"{artista} - {titulo}.mp3"), Artist = artista, Title = titulo, Album = album, Genre = genero,
        };

    /// <summary>Canciones de relleno de un artista con esos géneros (cada uno, tantas veces como diga).</summary>
    private static IEnumerable<Track> DelArtista(string artista, params (string? Genero, int Veces)[] generos)
    {
        var i = 0;
        foreach (var (genero, veces) in generos)
            for (var n = 0; n < veces; n++, i++)
                yield return T(artista, $"Relleno {i}", genero, "Relleno");
    }

    private static readonly Dictionary<string, string> SinCarpetas = new();

    private static List<PropuestaGeneroRegla> Proponer(IReadOnlyList<Track> canciones, Dictionary<string, string>? artistas = null,
                                                       AlcanceGenero alcance = AlcanceGenero.Todas, Dictionary<string, string>? carpetas = null)
        => GeneroPorReglas.Proponer(canciones, new OpcionesGeneroReglas(carpetas ?? SinCarpetas, artistas ?? new Dictionary<string, string>(), alcance));

    private static PropuestaGeneroRegla? De(IEnumerable<PropuestaGeneroRegla> p, Track t) => p.SingleOrDefault(x => x.Ruta == t.FilePath);

    // =================================================================================================
    // A3 · El dominante del artista sin los géneros genéricos
    // =================================================================================================

    [Fact]
    public void Los_generos_genericos_no_cuentan_para_el_dominante()
    {
        var nueva = T("Feid", "Nueva", null);
        var canciones = DelArtista("Feid", ("Reggaeton", 6), ("Latin", 3), ("Trap", 1)).Append(nueva).ToList();

        var d = GeneroPorReglas.GenerosPorArtista(canciones)["feid"];
        Assert.Equal(("Reggaeton", 6, 7, 3), d);   // antes, 6 de 10: un 60 %, sin propuesta

        var p = De(Proponer(canciones, alcance: AlcanceGenero.SinGenero), nueva);
        Assert.NotNull(p);
        Assert.Equal("Reggaeton", p!.Genero);
        Assert.False(p.Marcada);                    // 86 %: propone, pero no es fuerte
        Assert.Equal("Artista «Feid»: 6 de 7 canciones con Reggaeton (sin contar géneros genéricos)", p.PorQue);
    }

    [Fact]
    public void Un_artista_con_solo_generos_genericos_no_propone_nada()
    {
        var nueva = T("DJ Generico", "Nueva", null);
        var canciones = DelArtista("DJ Generico", ("Electro", 6)).Append(nueva).ToList();
        Assert.Empty(Proponer(canciones));
    }

    // =================================================================================================
    // A4 · El mapa artista → género y el resumen de cada artista
    // =================================================================================================

    [Fact]
    public void El_mapa_de_artistas_se_guarda_y_se_vuelve_a_leer()
    {
        var archivo = Path.Combine(_dir, MapaGenerosArtista.NombreArchivo);
        var mapa = new MapaGenerosArtista(archivo);
        Assert.Empty(mapa.Reglas);
        Assert.True(mapa.Poner("ANUEL AA", "ANUEL AA", " Reggaeton "));
        Assert.False(mapa.Poner("Anuel AA", "Anuel AA", "Reggaeton"));   // la misma clave y el mismo género
        Assert.True(mapa.Poner("Pablo Alborán", "Pablo Alborán", "Pop"));
        Assert.True(mapa.Excluir("Bad Bunny", "Bad Bunny"));
        Assert.False(mapa.Excluir("bad bunny", "bad bunny"));
        Assert.Equal("", mapa.Guardar());

        var leido = new MapaGenerosArtista(archivo);
        Assert.Equal("", leido.AvisoCarga);
        Assert.Equal("Reggaeton", leido.De("Anuel AA")?.Genero);
        Assert.Equal("Pop", leido.De("PABLO ALBORAN")?.Genero);   // sin mayúsculas ni acentos
        Assert.Equal("", leido.De("Bad Bunny")?.Genero);          // excluido
        Assert.Null(leido.De("Ozuna"));
        Assert.Equal(2, leido.ConGenero);
        Assert.Equal(1, leido.Excluidos);
        Assert.Equal(new Dictionary<string, string> { ["anuel aa"] = "Reggaeton", ["pablo alboran"] = "Pop", ["bad bunny"] = "" },
                     leido.Generos());

        Assert.True(leido.Quitar("bad bunny"));
        Assert.True(leido.Poner("Pablo Alborán", "Pablo Alborán", ""));   // vacío: quita la regla
        Assert.Single(leido.Reglas);
    }

    [Fact]
    public void La_clave_es_el_artista_principal()
    {
        Assert.Equal(MapaGenerosArtista.Clave("Antonio Orozco"), MapaGenerosArtista.Clave("Antonio Orozco; Pablo Alboran"));
        Assert.Equal("anuel aa", MapaGenerosArtista.Clave("ANUEL AA"));
        Assert.Equal(MapaGenerosArtista.Clave("Anuel AA"), MapaGenerosArtista.Clave("Anuel AA x Ozuna"));
        // La «x» pegada a un signo no separa artistas.
        Assert.Equal("dj x treme", MapaGenerosArtista.Clave("DJ X-Treme"));
    }

    // Revisión: la clave no se volvía a pasar por el artista principal sin estropearse. «dj x treme»
    // tiene una «x» suelta, que separa artistas: la regla se guardaba como «dj» y no valía para nadie.
    [Theory]
    [InlineData("DJ X-Treme", "dj x treme")]
    [InlineData("Mr.X Band", "mr x band")]
    public void La_regla_de_un_artista_vale_despues_de_guardarla_y_volver_a_leerla(string artista, string clave)
    {
        var vacia = T(artista, "Nueva", null);
        var canciones = DelArtista(artista, ("Techno", 1)).Append(vacia).ToList();
        var fila = new ArtistaGeneroFila(GeneroPorReglas.ResumenArtistas(canciones).Single());
        Assert.Equal(clave, fila.Clave);
        var archivo = Path.Combine(_dir, MapaGenerosArtista.NombreArchivo);
        var mapa = new MapaGenerosArtista(archivo);

        Assert.Single(GeneroPorReglasViewModel.Aplicar(mapa, new[] { fila }, GeneroPorReglasViewModel.AccionArtista.Poner, _ => "Hardstyle"));
        Assert.Equal(new[] { clave }, mapa.Reglas.Keys);
        Assert.Equal("", mapa.Guardar());

        var leido = new MapaGenerosArtista(archivo);
        Assert.Equal(new[] { clave }, leido.Reglas.Keys);
        Assert.Equal(new EntradaGeneroArtista(artista, "Hardstyle"), leido.De(artista));
        var p = De(Proponer(canciones, leido.Generos()), vacia);
        Assert.Equal(("Hardstyle", ReglaGenero.MapaArtista, true), (p?.Genero, p?.Regla, p?.Marcada));

        // Excluir y quitar también van por la clave de la fila.
        Assert.Single(GeneroPorReglasViewModel.Aplicar(leido, new[] { fila }, GeneroPorReglasViewModel.AccionArtista.Excluir, _ => ""));
        Assert.Equal("", leido.Reglas[clave].Genero);
        Assert.Single(GeneroPorReglasViewModel.Aplicar(leido, new[] { fila }, GeneroPorReglasViewModel.AccionArtista.Quitar, _ => ""));
        Assert.Empty(leido.Reglas);
    }

    // Lo que ha escrito alguien a mano con el nombre como clave sigue valiendo, como antes.
    [Fact]
    public void Un_mapa_de_artistas_escrito_a_mano_con_el_nombre_sigue_valiendo()
    {
        var archivo = Path.Combine(_dir, MapaGenerosArtista.NombreArchivo);
        File.WriteAllText(archivo, """
            {"Anuel AA x Ozuna":{"Nombre":"Anuel AA","Genero":"Reggaeton"},"dj x treme":{"Nombre":"DJ X-Treme","Genero":"Hardstyle"}}
            """);
        var leido = new MapaGenerosArtista(archivo);
        Assert.Equal(new Dictionary<string, string> { ["anuel aa"] = "Reggaeton", ["dj x treme"] = "Hardstyle" }, leido.Generos());
    }

    [Fact]
    public void Un_mapa_de_artistas_ilegible_se_aparta_en_vez_de_pisarse()
    {
        var archivo = Path.Combine(_dir, MapaGenerosArtista.NombreArchivo);
        File.WriteAllText(archivo, "{ esto no es JSON");

        var mapa = new MapaGenerosArtista(archivo);

        Assert.Empty(mapa.Reglas);
        Assert.Contains("apartado", mapa.AvisoCarga);
        Assert.Single(Directory.GetFiles(_dir, MapaGenerosArtista.NombreArchivo + ".ilegible_*"));
        Assert.False(mapa.SoloLectura);
    }

    // En macOS los bloqueos de archivo son orientativos, por eso solo se prueba en Windows.
    [WindowsFact]
    public void Un_mapa_de_artistas_bloqueado_al_abrir_no_se_pisa_al_guardar()
    {
        var archivo = Path.Combine(_dir, MapaGenerosArtista.NombreArchivo);
        var primero = new MapaGenerosArtista(archivo);
        primero.Poner("Anuel AA", "Anuel AA", "Reggaeton");
        Assert.Equal("", primero.Guardar());
        var antes = File.ReadAllBytes(archivo);

        MapaGenerosArtista mapa;
        using (new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.None))
            mapa = new MapaGenerosArtista(archivo);

        Assert.True(mapa.SoloLectura);
        Assert.NotEqual("", mapa.AvisoCarga);
        mapa.Poner("Ozuna", "Ozuna", "Reggaeton");
        Assert.NotEqual("", mapa.Guardar());
        Assert.Equal(antes, File.ReadAllBytes(archivo));
    }

    [Fact]
    public void El_resumen_de_un_artista_muestra_su_reparto_y_solo_sugiere_con_dominio_claro()
    {
        var canciones = DelArtista("Anuel AA", ("Reggaeton", 71), ("Trap", 32), ("Latin", 10), (null, 2))
            .Concat(DelArtista("ANUEL AA", ("Reggaeton", 1)))
            .Concat(DelArtista("Ozuna", ("Reggaeton", 9), ("Latin", 4), ("Pop", 1)))
            .ToList();

        var r = GeneroPorReglas.ResumenArtistas(canciones);

        var anuel = r[0];
        Assert.Equal(("anuel aa", "Anuel AA", 116, 2), (anuel.Clave, anuel.Nombre, anuel.Canciones, anuel.SinGenero));
        Assert.Equal("Reggaeton 72 · Trap 32 · Latin 10", GeneroPorReglas.TextoReparto(anuel.Reparto));
        Assert.True(anuel.Reparto.Single(x => x.Genero == "Latin").Generico);
        Assert.Equal("", anuel.Sugerencia);   // 72 de 104: un 69 %, no llega al 70 %
        Assert.Equal((72, 104), (anuel.Con, anuel.Total));

        var ozuna = r[1];
        Assert.Equal("Reggaeton", ozuna.Sugerencia);   // 9 de 10 sin contar Latin: fuerte
        Assert.True(ozuna.Fuerte);
    }

    [Fact]
    public void La_copia_de_seguridad_describe_el_mapa_de_artistas()
        => Assert.Equal("Reglas de género por artista",
                        CopiaSeguridad.Descripcion(MapaGenerosArtista.NombreArchivo, new AppPaths(Path.Combine(_dir, "datos"))));

    // =================================================================================================
    // A5 · Qué se propone, con el mapa del usuario
    // =================================================================================================

    [Fact]
    public void Anuel_AA_con_tu_regla_Reggaeton()
    {
        var latin = T("Anuel AA", "47", "Latin");
        var extended = T("Anuel AA", "Portate Bonito (Extended)", "Latin");
        var trap = T("Anuel AA", "Headshot (Extended)", "Trap");
        var mashup = T("Anuel AA", "Tu No Lo Amas x Yeezy (Gery Mashup)", "Trap");
        var moombah = T("Anuel AA", "Mas Rica Que Ayer (Sandunga Moombahton Remix)", "Reggaeton");
        var vacia = T("Anuel AA", "Sin Genero", null);
        var mismo = T("Anuel AA", "China", "Reggaeton");
        var canciones = new List<Track> { latin, extended, trap, mashup, moombah, vacia, mismo };

        var p = Proponer(canciones, new Dictionary<string, string> { ["anuel aa"] = "Reggaeton" });

        var a = De(p, latin)!;
        Assert.Equal(("Reggaeton", ReglaGenero.MapaArtista, true, CambioGenero.SustituirGenerico, "Latin"), (a.Genero, a.Regla, a.Marcada, a.Cambio, a.GeneroActual));
        Assert.Equal("Tu regla: «Anuel AA» → Reggaeton · sustituye «Latin», un género genérico", a.PorQue);
        Assert.True(De(p, extended)!.Marcada);   // Extended no protege
        var b = De(p, trap)!;
        Assert.Equal((false, CambioGenero.Revisar), (b.Marcada, b.Cambio));
        Assert.EndsWith("para revisar: la canción tiene Trap", b.PorQue);
        Assert.Null(De(p, mashup));               // un mashup con otro estilo conserva el suyo
        var c = De(p, moombah)!;
        Assert.Equal(("Moombahton", ReglaGenero.Version, true), (c.Genero, c.Regla, c.Marcada));
        Assert.Equal("Versión «Moombahton Remix» → Moombahton · sustituye «Reggaeton»", c.PorQue);
        Assert.Equal((true, CambioGenero.Rellenar), (De(p, vacia)!.Marcada, De(p, vacia)!.Cambio));
        Assert.Null(De(p, mismo));
    }

    [Fact]
    public void David_Guetta_con_tu_regla_EDM()
    {
        var electro = T("David Guetta", "Whos That Chick", "Electro");
        var titanium = T("David Guetta", "Titanium (feat. Sia) (Extended Mix)", "Electro House");
        var remix = T("David Guetta", "When Love Takes Over (Norman Doray & Arno Cost Remix)", "House");
        var soul = T("David Guetta", "Cuentale (Extended)", "Soul");
        var mashup = T("David Guetta", "Hey Mama x Bendecido (MRJ Mashup)", "Trap");

        var p = Proponer(new List<Track> { electro, titanium, remix, soul, mashup }, new Dictionary<string, string> { ["david guetta"] = "EDM" });

        Assert.Equal(("EDM", true), (De(p, electro)!.Genero, De(p, electro)!.Marcada));
        Assert.Null(De(p, titanium));   // Electro House y EDM: la misma familia, se conserva el detalle
        Assert.Null(De(p, remix));
        Assert.False(De(p, soul)!.Marcada);
        Assert.Null(De(p, mashup));
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Pablo_Alboran_con_tu_regla_Pop_no_toca_lo_que_no_es_suyo()
    {
        var tabu = T("Pablo Alborán", "Tabú", "Pop");
        var colaboracion = T("Antony Z; Pablo Alborán", "Besos Dorados", "Merengue");
        Assert.Empty(Proponer(new List<Track> { tabu, colaboracion }, new Dictionary<string, string> { ["pablo alboran"] = "Pop" }));
    }

    // =================================================================================================
    // A5 · La regla automática, sin mapa
    // =================================================================================================

    [Fact]
    public void La_regla_automatica_solo_marca_lo_que_no_tiene_estilo()
    {
        var dile = T("Don Omar", "Dile", "Pop");
        var wisin = T("Wisin", "3G (Remix)", "Hip Hop");
        var bonita = T("Jowell & Randy", "Bonita (ADRUKID Remix)", "Tech House");
        var zapatillas = T("El Canto del Loco", "Zapatillas x Better Off Alone (Taron Mashup)", "Techno");
        var g63 = T("Sfera Ebbasta", "G63", "Trap");
        var slomo = T("Chanel", "SloMo", "Dance-Pop");
        var luces = T("Feid", "Luces De Tecno (Extended)", "Latin");
        var lista = T("Feid", "Hey Mor", "Electro; Dance; Rap");
        var tego = T("Tego Calderón", "Pa Que Retocen Vs Metele Sazon", "Mashup");
        var canciones = DelArtista("Don Omar", ("Reggaeton", 88), ("Pop", 35))
            .Concat(DelArtista("Wisin", ("Reggaeton", 8)))
            .Concat(DelArtista("Jowell & Randy", ("Reggaeton", 5)))
            .Concat(DelArtista("El Canto del Loco", ("Pop", 5)))
            .Concat(DelArtista("Sfera Ebbasta", ("Hip Hop", 5)))
            .Concat(DelArtista("Chanel", ("Bachata", 5), ("Pop", 1)))
            .Concat(DelArtista("Feid", ("Reggaeton", 5), ("Latin", 2)))
            .Concat(DelArtista("Tego Calderón", ("Reggaeton", 3)))
            .Concat(new[] { dile, wisin, bonita, zapatillas, g63, slomo, luces, lista, tego })
            .ToList();

        var calculo = GeneroPorReglas.Calcular(canciones, new OpcionesGeneroReglas(SinCarpetas, new Dictionary<string, string>()));
        var p = calculo.Propuestas;

        var d = De(p, dile)!;   // 88 de 124: propone, pero la canción tiene otro estilo
        Assert.Equal(("Reggaeton", false, CambioGenero.Revisar), (d.Genero, d.Marcada, d.Cambio));
        Assert.False(De(p, wisin)!.Marcada);   // un «(Remix)» sin autor no protege
        Assert.Null(De(p, bonita));            // remix firmado
        Assert.Null(De(p, zapatillas));        // mashup
        Assert.Null(De(p, g63));               // Trap es de la familia del Hip Hop
        Assert.False(De(p, slomo)!.Marcada);   // Bachata 5 de 7 frente a Dance-Pop
        var l = De(p, luces)!;
        Assert.True(l.Marcada);
        Assert.Equal("Artista «Feid»: 5 de 5 canciones con Reggaeton (sin contar géneros genéricos) · sustituye «Latin», un género genérico", l.PorQue);
        Assert.Null(De(p, lista));             // las listas son cosa de «Unificar géneros»
        Assert.Equal(1, calculo.Listas);
        var t = De(p, tego)!;                  // un mashup con un género genérico: se propone, desmarcado
        Assert.Equal((false, CambioGenero.SustituirGenerico), (t.Marcada, t.Cambio));
        Assert.EndsWith("mashup o remix firmado: comprueba a qué tema corresponde", t.PorQue);
        Assert.Equal(2, calculo.Protegidas);   // Bonita y Zapatillas
    }

    // =================================================================================================
    // A5 · Alcance y orden de las reglas
    // =================================================================================================

    [Fact]
    public void Solo_sin_genero_es_lo_de_antes()
    {
        var latin = T("Feid", "Tema", "Latin");
        var vacia = T("Feid", "Vacia", null);
        var canciones = DelArtista("Feid", ("Reggaeton", 5)).Append(latin).Append(vacia).ToList();

        var p = Proponer(canciones, alcance: AlcanceGenero.SinGenero);
        Assert.Null(De(p, latin));
        Assert.NotNull(De(p, vacia));

        var conGenericos = Proponer(canciones, alcance: AlcanceGenero.SinGeneroYGenericos);
        Assert.NotNull(De(conGenericos, latin));
        var pop = T("Feid", "Pop", "Pop");
        Assert.Null(De(Proponer(canciones.Append(pop).ToList(), alcance: AlcanceGenero.SinGeneroYGenericos), pop));
    }

    [Fact]
    public void La_regla_de_carpeta_sobre_un_mashup_sin_genero_sigue_marcada_y_manda_sobre_tu_regla()
    {
        var mashup = T("Anuel AA", "Tu No Lo Amas x Yeezy (Gery Mashup)", null, "Mashups Trap");
        var otra = T("Anuel AA", "Otra", null, "Mashups Trap");
        var carpetas = new Dictionary<string, string> { [Rutas.De("M", "Mashups Trap")] = "Trap" };

        var p = Proponer(new List<Track> { mashup, otra }, new Dictionary<string, string> { ["anuel aa"] = "Reggaeton" }, carpetas: carpetas);

        Assert.All(p, x => Assert.Equal(("Trap", ReglaGenero.Carpeta, true), (x.Genero, x.Regla, x.Marcada)));
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void Un_artista_excluido_no_recibe_la_regla_automatica()
    {
        var vacia = T("Bad Bunny", "Nueva", null);
        var canciones = DelArtista("Bad Bunny", ("Reggaeton", 9)).Append(vacia).ToList();
        Assert.NotNull(De(Proponer(canciones), vacia));
        Assert.Null(De(Proponer(canciones, new Dictionary<string, string> { ["bad bunny"] = "" }), vacia));
    }

    [Fact]
    public void Tu_regla_manda_sobre_la_automatica_y_se_marca_sin_ser_fuerte()
    {
        var vacia = T("Rosalía", "Nueva", null);
        var canciones = DelArtista("Rosalía", ("Flamenco", 4), ("Pop", 1)).Append(vacia).ToList();
        Assert.False(De(Proponer(canciones), vacia)!.Marcada);   // 4 de 5: propone, pero no es fuerte
        var p = De(Proponer(canciones, new Dictionary<string, string> { ["rosalia"] = "Pop" }), vacia)!;
        Assert.Equal(("Pop", ReglaGenero.MapaArtista, true), (p.Genero, p.Regla, p.Marcada));
    }

    [Fact]
    public void Proponer_se_puede_cancelar()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => GeneroPorReglas.Calcular(
            new List<Track> { T("Feid", "Tema", null) }, new OpcionesGeneroReglas(SinCarpetas, new Dictionary<string, string>()), cts.Token));
    }

    [Fact]
    public void El_estilo_de_la_version_ambiguo_o_solo_en_el_album_sale_desmarcado()
    {
        var afro = T("Burna Boy", "Ye (Afro Edit)", null);
        var album = T("Romeo Santos", "Propuesta Indecente", null, album: "Bachata Hits (Bachata Version)");
        var familia = T("Fisher", "Losing It (Afro House Remix)", "Tech House");

        var p = Proponer(new List<Track> { afro, album, familia });

        Assert.Equal(("Afro", false), (De(p, afro)!.Genero, De(p, afro)!.Marcada));
        var a = De(p, album)!;
        Assert.False(a.Marcada);
        Assert.StartsWith("Álbum «Bachata Hits (Bachata Version)» → Bachata: puede describir el pack", a.PorQue);
        Assert.False(De(p, familia)!.Marcada);   // Afro House sobre Tech House: la misma familia
    }

    // Revisión 3: «(Tribal Mix)» pisaba marcado un Tech House o un House: «tribal» nombra varios estilos
    // (el tribal house, el tribal guarachero), como «Afro» y «Electro».
    [Fact]
    public void Tribal_suelto_sale_desmarcado_para_revisarlo()
    {
        var techHouse = T("Pepe", "Saturday Night (Tribal Mix)", "Tech House");
        var house = T("Pepe", "Sunday Morning (Tribal Mix)", "House");

        var p = Proponer(new List<Track> { techHouse, house });

        foreach (var t in new[] { techHouse, house })
        {
            var x = De(p, t)!;
            Assert.Equal(("Tribal", false), (x.Genero, x.Marcada));
            Assert.Contains("«Tribal» puede nombrar varios estilos: revísalo", x.PorQue);
        }
    }

    // Visto al validar en una biblioteca real: «(Afro House)» proponía cambiar «Afrohouse» por «Afro House».
    [Fact]
    public void El_mismo_genero_escrito_junto_o_separado_no_se_propone()
    {
        var junto = T("Hugel", "Cafe Con Ron (Afro House)", "Afrohouse");
        var hipHop = T("Eminem", "Lose Yourself", "HipHop");
        var canciones = DelArtista("Eminem", ("Hip Hop", 5)).Append(junto).Append(hipHop).ToList();
        var p = Proponer(canciones);
        Assert.Null(De(p, junto));
        Assert.Null(De(p, hipHop));
    }

    // Revisión: «(Disco Mix)» es la versión de 12" de cualquier tema de los 80, y «DJ Mambo» un
    // remixer. La regla de la versión los tomaba por estilos y proponía, MARCADO, pisar el género.
    [Fact]
    public void La_version_de_12_pulgadas_y_un_remixer_que_se_llama_como_un_estilo_no_pisan_el_genero()
    {
        var disco = T("Bronski Beat", "Smalltown Boy (Disco Mix)", "Synth-Pop");
        var version = T("Soft Cell", "Tainted Love (Disco Version)", "Pop");
        var mambo = T("Daddy Yankee", "Gasolina (DJ Mambo Remix)", "Reggaeton");
        var vacia = T("Alphaville", "Big In Japan (Disco Mix)", null);
        Assert.Empty(Proponer(new List<Track> { disco, version, mambo, vacia }));
    }

    // Revisión (ronda 2): con el título «Gasolina (DJ Mambo Remix)» no se proponía nada, pero entonces
    // se miraba el nombre del archivo, «Daddy Yankee - Gasolina DJ Mambo Remix», sin paréntesis, y salía
    // Mambo MARCADO sobre Reggaeton. Igual con «(Extended DJ Mambo Remix)» o «(DJ Mambo & DJ Trap Remix)».
    [Fact]
    public void Un_remixer_que_se_llama_como_un_estilo_no_pisa_el_genero_tampoco_desde_el_nombre_del_archivo()
    {
        static Track Con(string titulo, string archivo)
            => new() { FilePath = Rutas.De("M", "Musica", archivo + ".mp3"), Artist = "Daddy Yankee", Title = titulo, Genre = "Reggaeton" };
        var canciones = new List<Track>
        {
            Con("Gasolina (DJ Mambo Remix)", "Daddy Yankee - Gasolina DJ Mambo Remix"),
            Con("Gasolina", "Daddy Yankee - Gasolina DJ Mambo Remix"),
            Con("Gasolina", "01 - Daddy Yankee - Gasolina (Extended DJ Mambo Remix)"),
            Con("Gasolina (Intro DJ Mambo Remix)", "Daddy Yankee - Gasolina (Intro DJ Mambo Remix)"),
            Con("Gasolina (DJ Mambo & DJ Trap Remix)", "Daddy Yankee - Gasolina (DJ Mambo & DJ Trap Remix)"),
            // Revisión 3: «Artista - Título» en el título, el número de pista o el álbum en el nombre del
            // archivo, «b2b» y los separadores sin espacio.
            Con("Daddy Yankee - Gasolina DJ Mambo Remix", "Daddy Yankee - Gasolina DJ Mambo Remix"),
            Con("Daddy Yankee - Gasolina MC Trap Edit", "Daddy Yankee - Gasolina MC Trap Edit"),
            Con("Gasolina (DJ Mambo Remix)", "Daddy Yankee - Barrio Fino - 01 - Gasolina DJ Mambo Remix"),
            Con("Gasolina (DJ Mambo Remix)", "Daddy Yankee - 01 - Gasolina DJ Mambo Remix"),
            Con("Gasolina", "Daddy Yankee - 02 - Gasolina DJ Mambo Remix"),
            Con("Tema (DJ Pepe b2b DJ Trap Remix)", "Daddy Yankee - Tema (DJ Pepe b2b DJ Trap Remix)"),
            Con("Tema (DJ Pepe B2B MC Salsa Edit)", "Daddy Yankee - Tema (DJ Pepe B2B MC Salsa Edit)"),
            Con("Tema (DJ Pepe&DJ Mambo Remix)", "Daddy Yankee - Tema (DJ Pepe&DJ Mambo Remix)"),
        };
        Assert.Empty(Proponer(canciones));

        // El estilo que cierra el nombre del remixer, detrás del título, sí manda.
        var gramage = Con("Bad Boy", "Daddy Yankee - Bad Boy - Alex Gramage DJ Mambo Remix");
        var p = De(Proponer(new List<Track> { gramage }), gramage);
        Assert.Equal(("Mambo", ReglaGenero.Version, true), (p?.Genero, p?.Regla, p?.Marcada));
    }

    // Revisión: la Ayuda, la descripción de la pestaña, el desplegable «Proponer para», el consejo del
    // estado y el README decían que con otro estilo la propuesta sale SIEMPRE desmarcada. La que
    // indica la propia versión sale marcada (es lo que pide un DJ: el estilo de la versión manda), y
    // la de la misma familia sí se propone, desmarcada.
    [Fact]
    public void Los_textos_dicen_que_la_version_que_indica_su_estilo_sale_marcada_aunque_la_cancion_tenga_otro()
    {
        var bachata = T("Romeo Santos", "Dividimos (Bachata Version)", "Reggaeton");
        var familia = T("Fisher", "Losing It (Afro House Remix)", "Tech House");
        var dile = T("Don Omar", "Dile", "Pop");
        var p = Proponer(DelArtista("Don Omar", ("Reggaeton", 9)).Concat(new[] { bachata, familia, dile }).ToList());
        Assert.Equal(("Bachata", true, CambioGenero.Revisar), (De(p, bachata)!.Genero, De(p, bachata)!.Marcada, De(p, bachata)!.Cambio));
        Assert.Equal(("Afro House", false), (De(p, familia)!.Genero, De(p, familia)!.Marcada));
        Assert.Equal(("Reggaeton", false), (De(p, dile)!.Genero, De(p, dile)!.Marcada));

        static string Plano(string s) => Regex.Replace(WebUtility.HtmlDecode(s), @"\s+", " ");
        string Atributo(string archivo, string patron)
        {
            var m = Regex.Match(File.ReadAllText(RevisionUnionTests.Fuente("Etiquetador.App", "Views", archivo)), patron);
            Assert.True(m.Success, $"no se encontró {patron} en {archivo}");
            return Plano(m.Groups[1].Value);
        }
        var textos = new Dictionary<string, string>
        {
            ["Ayuda"] = Atributo("HelpView.axaml", @"Text=""(\s*· «Proponer para»[^""]*)"""),
            ["descripción de la pestaña"] = Atributo("AsistenteView.axaml", @"Text=""(Pone o corrige el género con reglas[^""]*)"""),
            ["«Proponer para»"] = Atributo("AsistenteView.axaml", @"ToolTip.Tip=""(Con «Todas»[^""]*)"""),
            ["consejo"] = GeneroPorReglasViewModel.Consejo(3, 5),
            ["README"] = Plano(File.ReadAllText(RevisionUnionTests.Fuente("..", "README.md"))),
        };
        foreach (var (donde, texto) in textos)
        {
            Assert.True(texto.Contains("salvo cuando la propia versión indica su estilo", StringComparison.Ordinal),
                        $"{donde}: no dice que la propuesta de la versión sale marcada sobre otro estilo");
            Assert.DoesNotContain("y si es de la misma familia (Trap y Hip Hop, Tech House y EDM) no se propone nada", texto);
            // Revisión (ronda 2): «Los mashups y los remixes firmados conservan su género» contradecía
            // lo anterior: la versión que indica su estilo («(Sandunga Moombahton Remix)») lo cambia, y
            // marcado. Solo lo conservan frente a las reglas de carpeta y de artista.
            Assert.DoesNotContain("Los mashups y los remixes firmados conservan su género.", texto);
            foreach (var frase in Regex.Split(texto, @"(?<=\.)\s+").Where(f => f.Contains("remixes firmados", StringComparison.Ordinal)))
                Assert.True(frase.Contains("reglas de carpeta y de artista", StringComparison.OrdinalIgnoreCase),
                            $"{donde}: «{frase}» no dice que los remixes firmados solo conservan su género frente a las reglas de carpeta y de artista");
        }
        // Revisión (ronda 2): la opción inicial de «Proponer para» decía «(las de otro estilo, para
        // revisar)», y la de la versión que indica su estilo sale marcada.
        var todas = GeneroPorReglasViewModel.TextosAlcance[(int)AlcanceGenero.Todas];
        Assert.DoesNotContain("para revisar", todas, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Todas", todas, StringComparison.Ordinal);
        // La regla automática del artista solo marca las canciones sin género cuando es fuerte.
        Assert.Contains("solo cuando es fuerte", textos["descripción de la pestaña"]);
        Assert.Contains("la versión propone el suyo desmarcado", textos["Ayuda"]);
    }

    // =================================================================================================
    // A6 · Escribir solo lo que sigue como se vio al proponer
    // =================================================================================================

    private string Mp3(string nombre, params string[] generos)
    {
        var ruta = Path.Combine(_dir, nombre);
        Mp3Fixture.WriteMinMp3(ruta);
        using var f = TagLib.File.Create(ruta);
        f.Tag.Genres = generos;
        f.Save();
        return ruta;
    }

    // Por el mismo camino que «Aplicar las marcadas»: EscribirConHistorial con el tipo de la herramienta.
    [Fact]
    public void Un_genero_generico_se_sustituye_y_se_deshace()
    {
        var motor = MotorDePrueba.Crear(Path.Combine(_dir, "datos"), new Logger());
        var paths = motor.Paths;
        var ruta = Mp3("a.mp3", "Latin");
        var res = EscrituraPorReglas.EscribirConHistorial(motor, new[] { new CambioEtiquetas(ruta) { GeneroNuevo = "Reggaeton", GeneroVisto = "Latin" } },
                                                          null, CancellationToken.None, "prueba", GeneroPorReglasViewModel.TipoEnElHistorial);
        Assert.Equal(new[] { ruta }, res.Escritos);
        using (var f = TagLib.File.Create(ruta)) Assert.Equal(new[] { "Reggaeton" }, f.Tag.Genres);

        // Una línea en el manifiesto, con el origen de la herramienta: sin él, el historial la
        // deduciría de los campos y la llamaría «Géneros», como a «Unificar géneros».
        var lineas = File.ReadAllLines(Assert.Single(Directory.GetFiles(paths.UndoDir, "*.jsonl"))).Where(l => l.Trim().Length > 0).ToList();
        var registro = JsonNode.Parse(Assert.Single(lineas))!;
        Assert.Equal(nameof(TipoEjecucion.GeneroPorReglas), (string?)registro[nameof(UndoRecord.Origen)]);
        var ejecucion = Assert.Single(new HistorialDeshacer(paths.UndoDir).Listar());
        Assert.Equal(TipoEjecucion.GeneroPorReglas, ejecucion.Tipo);
        Assert.Equal("Género por carpeta y artista", ejecucion.TipoTexto);

        var u = new UndoEngine(paths).UndoLastRun();

        Assert.Equal(1, u.Reverted);
        using (var f = TagLib.File.Create(ruta)) Assert.Equal(new[] { "Latin" }, f.Tag.Genres);
    }

    [Fact]
    public void Lo_cambiado_despues_de_proponer_se_respeta()
    {
        var ruta = Mp3("a.mp3", "Pop");   // al proponer tenía «Latin»; después se cambió a mano
        var historial = new List<UndoRecord>();
        var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { GeneroNuevo = "Reggaeton", GeneroVisto = "Latin" } },
                                              new Historial(historial), "", null, null, null, CancellationToken.None, "prueba");
        Assert.Empty(res.Escritos);
        Assert.Equal(new[] { ruta }, res.Respetados);
        Assert.Empty(historial);
        using var f = TagLib.File.Create(ruta);
        Assert.Equal(new[] { "Pop" }, f.Tag.Genres);
    }

    [Fact]
    public void Sin_lo_visto_sigue_sin_pisar_un_genero()
    {
        var ruta = Mp3("a.mp3", "Salsa");
        var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { GeneroNuevo = "Reggaeton" } },
                                              new Historial(new List<UndoRecord>()), "", null, null, null, CancellationToken.None, "prueba");
        Assert.Empty(res.Escritos);
        using var f = TagLib.File.Create(ruta);
        Assert.Equal(new[] { "Salsa" }, f.Tag.Genres);
    }

    // Con una barra en ID3v2.3 el valor vuelve partido: se anota como quedará, en una sola línea.
    [Fact]
    public void Un_valor_con_barra_sustituido_queda_en_una_sola_linea()
    {
        var ruta = Mp3("a.mp3", "Latin");
        using (var f = TagLib.File.Create(ruta))
        {
            ((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true)).Version = 3;
            f.Save();
        }
        var historial = new List<UndoRecord>();
        var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { GeneroNuevo = "Hip-Hop/Rap", GeneroVisto = "Latin" } },
                                              new Historial(historial), "", null, null, null, CancellationToken.None, "prueba");
        Assert.Equal(new[] { ruta }, res.Escritos);
        var registro = Assert.Single(historial);
        Assert.Equal(new[] { "Latin" }, registro.Fields!["Genre"].OldArr);
    }

    // =================================================================================================
    // A7 · La herramienta: textos, confirmación, instantánea y el mapa desde la tabla
    // =================================================================================================

    private static GeneroReglaFila Fila(string titulo, string? actual, CambioGenero cambio, bool marcada = true, string genero = "Reggaeton")
    {
        var t = T("Feid", titulo, actual);
        return new GeneroReglaFila(new PropuestaGeneroRegla(t.FilePath, genero, ReglaGenero.Artista, "Artista", marcada, actual ?? "", cambio), t);
    }

    [Fact]
    public void La_confirmacion_cuenta_por_tipo_de_cambio()
    {
        var filas = new[]
        {
            Fila("a", null, CambioGenero.Rellenar),
            Fila("b", "Latin", CambioGenero.SustituirGenerico), Fila("c", "Dance", CambioGenero.SustituirGenerico),
            Fila("d", "Trap", CambioGenero.Revisar, marcada: false),
        };
        var (cuerpo, nota) = GeneroPorReglasViewModel.Confirmacion(filas);
        Assert.Equal("Se cambiará el género de 4 canciones: 1 sin género, 2 con un género genérico y 1 con otro estilo.", cuerpo);
        Assert.StartsWith("⚠ 1 no salía marcada", nota);
        Assert.Contains("Solo se escribe en las canciones que sigan como estaban al proponer; lo que hayas cambiado después se respeta.", nota);

        Assert.Equal("Se cambiará el género de 2 canciones: 2 con un género genérico.", GeneroPorReglasViewModel.Confirmacion(filas[1..3]).Cuerpo);
        Assert.Equal("Se cambiará el género de 2 canciones: 1 sin género y 1 con un género genérico.", GeneroPorReglasViewModel.Confirmacion(filas[..2]).Cuerpo);
    }

    [Fact]
    public void Se_escribe_con_lo_que_tenia_al_proponer()
    {
        var vacia = Fila("a", null, CambioGenero.Rellenar);
        var latin = Fila("b", "Latin", CambioGenero.SustituirGenerico);
        latin.Track.Genre = "Pop";   // la biblioteca ya dice otra cosa: manda lo visto al proponer

        var cambios = GeneroPorReglasViewModel.Cambios(new[] { vacia, latin }, new[] { "Reggaeton" });

        Assert.Null(cambios[0].GeneroVisto);         // sin género: solo si sigue sin ninguno, como antes
        Assert.Equal("Latin", cambios[1].GeneroVisto);
        Assert.All(cambios, c => Assert.Equal("Reggaeton", c.GeneroNuevo));
    }

    [Fact]
    public void Los_textos_del_resumen_distinguen_singular_y_plural()
    {
        Assert.Equal("1 artista · 0 con tu regla", GeneroPorReglasViewModel.TextoArtistas(1, 0, 0));
        Assert.Equal($"{2767:N0} artistas · 3 con tu regla · 1 excluido", GeneroPorReglasViewModel.TextoArtistas(2767, 3, 1));
        Assert.EndsWith("· 2 excluidos", GeneroPorReglasViewModel.TextoArtistas(10, 0, 2));
        Assert.Equal("", GeneroPorReglasViewModel.TextoListas(0));
        Assert.Contains("1 canción tiene varios géneros y no se toca", GeneroPorReglasViewModel.TextoListas(1));
        Assert.Contains("3 canciones tienen varios géneros y no se tocan", GeneroPorReglasViewModel.TextoListas(3));
        Assert.Equal($"{2640:N0} canciones sin género · 314 propuestas marcadas · 890 para revisar · 250 protegidas (mashups y remixes firmados)",
                     GeneroPorReglasViewModel.TextoResumen(2640, 314, 890, 250));
    }

    private static ArtistaGeneroFila Artista(string nombre, string sugerencia)
        => new(new ResumenArtista(MapaGenerosArtista.Clave(nombre), nombre, 10, 1, Array.Empty<RepartoGenero>(), sugerencia, 9, 10, true));

    [Fact]
    public void Usar_la_sugerencia_no_pone_nada_a_los_que_no_tienen()
    {
        var mapa = new MapaGenerosArtista(Path.Combine(_dir, MapaGenerosArtista.NombreArchivo));
        var ozuna = Artista("Ozuna", "Reggaeton");
        var guetta = Artista("David Guetta", "");

        var cambiados = GeneroPorReglasViewModel.Aplicar(mapa, new[] { ozuna, guetta }, GeneroPorReglasViewModel.AccionArtista.Sugerencia,
                                                         f => f.Datos.Sugerencia);

        Assert.Equal(new[] { ozuna }, cambiados);
        Assert.Equal("Reggaeton", ozuna.Genero);
        Assert.Equal("Sin asignar", guetta.Regla);
        Assert.Null(mapa.De("David Guetta"));
        Assert.Equal("Reggaeton", mapa.De("Ozuna")?.Genero);
        Assert.Equal("Ninguno de esos artistas tiene sugerencia: sus canciones no muestran un estilo claro. Elige tú su género.",
                     GeneroPorReglasViewModel.TextoSinCambios(new[] { guetta, Artista("Pitbull", "") }, GeneroPorReglasViewModel.AccionArtista.Sugerencia));
    }

    [Fact]
    public void Excluir_y_quitar_dicen_lo_que_ha_pasado()
    {
        var mapa = new MapaGenerosArtista(Path.Combine(_dir, MapaGenerosArtista.NombreArchivo));
        var anuel = Artista("Anuel AA", "");
        var bunny = Artista("Bad Bunny", "Reggaeton");

        var excluidos = GeneroPorReglasViewModel.Aplicar(mapa, new[] { anuel, bunny }, GeneroPorReglasViewModel.AccionArtista.Excluir, _ => "");
        Assert.Equal(2, excluidos.Count);
        Assert.Equal("Excluido de la regla automática", anuel.Regla);
        Assert.Equal(2, mapa.Excluidos);
        Assert.StartsWith("«Anuel AA» queda excluido de la regla automática",
                          GeneroPorReglasViewModel.TextoReglaArtista(1, "Anuel AA", "", GeneroPorReglasViewModel.AccionArtista.Excluir));
        Assert.StartsWith("2 artistas quedan excluidos de la regla automática",
                          GeneroPorReglasViewModel.TextoReglaArtista(2, "Anuel AA", "", GeneroPorReglasViewModel.AccionArtista.Excluir));
        Assert.Equal("Esos artistas ya estaban excluidos.",
                     GeneroPorReglasViewModel.TextoSinCambios(new[] { anuel, bunny }, GeneroPorReglasViewModel.AccionArtista.Excluir));

        var quitados = GeneroPorReglasViewModel.Aplicar(mapa, new[] { anuel }, GeneroPorReglasViewModel.AccionArtista.Quitar, _ => "");
        Assert.Single(quitados);
        Assert.Equal("Sin asignar", anuel.Regla);
        Assert.Equal("Regla quitada de «Anuel AA»: vuelve a valer la regla automática.",
                     GeneroPorReglasViewModel.TextoReglaArtista(1, "Anuel AA", "", GeneroPorReglasViewModel.AccionArtista.Quitar));
        Assert.Equal("Ese artista no tenía regla.",
                     GeneroPorReglasViewModel.TextoSinCambios(new[] { anuel }, GeneroPorReglasViewModel.AccionArtista.Quitar));
        Assert.Equal("3 artistas → Reggaeton.",
                     GeneroPorReglasViewModel.TextoReglaArtista(3, "Anuel AA", "Reggaeton", GeneroPorReglasViewModel.AccionArtista.Poner));
    }

    [Fact]
    public void Al_volver_a_proponer_se_conserva_lo_marcado_y_lo_corregido_y_salen_primero_las_marcadas()
    {
        var a = T("Feid", "A", "Latin");
        var b = T("Feid", "B", "Trap");
        var propuestas = new[]
        {
            new PropuestaGeneroRegla(b.FilePath, "Reggaeton", ReglaGenero.Artista, "x", false, "Trap", CambioGenero.Revisar),
            new PropuestaGeneroRegla(a.FilePath, "Reggaeton", ReglaGenero.Artista, "x", true, "Latin", CambioGenero.SustituirGenerico),
        };
        var porRuta = new Dictionary<string, Track> { [a.FilePath] = a, [b.FilePath] = b };
        var primera = GeneroPorReglasViewModel.Filas(propuestas, porRuta, Array.Empty<GeneroReglaFila>());
        Assert.Equal(new[] { a.FilePath, b.FilePath }, primera.Select(f => f.Ruta));   // la marcada, arriba
        primera[0].Aplicar = false;
        primera[1].Aplicar = true;
        primera[1].Genero = "Trap Latino";

        var segunda = GeneroPorReglasViewModel.Filas(propuestas, porRuta, primera);

        Assert.False(segunda[0].Aplicar);
        Assert.True(segunda[1].Aplicar);
        Assert.Equal("Trap Latino", segunda[1].Genero);
        Assert.Equal(("Latin", "Sustituir genérico", "Artista"), (segunda[0].Ahora, segunda[0].Cambio, segunda[0].Regla));
    }

    // =================================================================================================
    // A11 · Deezer, solo para lo que las reglas no cubren y solo con subgéneros concretos
    // =================================================================================================

    private static (int Id, string Nombre)[] G(params (int, string)[] generos) => generos;

    [Fact]
    public void Deezer_solo_traduce_los_subgeneros_concretos()
    {
        Assert.Equal("Reggaeton", GeneroDeezer.Traducir(G((122, "Reggaeton"), (197, "Latino"))));
        Assert.Equal("Reggaeton", GeneroDeezer.Traducir(G((197, "Latino"), (122, "Reggaeton"))));   // el concreto manda sobre el padre
        Assert.Equal("Cumbia", GeneroDeezer.Traducir(G((71, "Cumbia"), (197, "Latino"))));
        Assert.Equal("Flamenco", GeneroDeezer.Traducir(G((36, "Flamenco"))));
        Assert.Equal("Salsa", GeneroDeezer.Traducir(G((169, "Salsa"))));
        Assert.Null(GeneroDeezer.Traducir(G((116, "Rap/Hip Hop"), (197, "Latino"))));
        Assert.Null(GeneroDeezer.Traducir(G((197, "Latino"))));
        Assert.Null(GeneroDeezer.Traducir(G((132, "Pop"), (138, "Pop latino"))));
        Assert.Null(GeneroDeezer.Traducir(G((85, "Alternativo"))));
        Assert.Null(GeneroDeezer.Traducir(G((113, "Dance"), (106, "Electro"))));
        Assert.Null(GeneroDeezer.Traducir(G()));
    }

    // El nombre llega en el idioma de la IP: se traduce por el identificador.
    [Fact]
    public void Deezer_da_lo_mismo_en_cualquier_idioma()
    {
        Assert.Equal(GeneroDeezer.Traducir(G((122, "Reggaeton"), (197, "Latino"))), GeneroDeezer.Traducir(G((122, "Reggaetón"), (197, "Latin Music"))));
        Assert.Equal(GeneroDeezer.Traducir(G((197, "Latino"))), GeneroDeezer.Traducir(G((197, "Latin Music"))));
    }

    private const string BusquedaDeezer = """
        {"data":[
          {"id":1,"title":"Bichota","duration":178,"artist":{"name":"KAROL G"},"album":{"title":"KG0516","id":77,"cover_big":"x"}},
          {"id":2,"title":"Tusa","duration":200,"artist":{"name":"KAROL G"},"album":{"title":"KG0516","id":77,"cover_big":"x"}}]}
        """;

    private const string AlbumDeezer = """{"genres":{"data":[{"id":122,"name":"Reggaeton"},{"id":197,"name":"Latino"}]}}""";

    // Revisión: cada canción pedía también la ficha de la pista (track/{id}), que aquí no se usa: unas
    // 565 peticiones de más con 565 candidatas. Ahora son la búsqueda y, si es nuevo, el álbum.
    [Fact]
    public async Task Deezer_pide_cada_album_una_vez_no_pide_la_ficha_de_la_pista_y_propone_desmarcado()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/search", BusquedaDeezer).Responde("api.deezer.com/album/77", AlbumDeezer);
        var canciones = new List<Track> { T("KAROL G", "Bichota", null), T("KAROL G", "Tusa", null) };

        var r = await GeneroDeezer.BuscarAsync(canciones, new Etiquetador.Core.Providers.DeezerProvider(m.Api), null, CancellationToken.None);

        var p = r.Propuestas;
        Assert.Equal(2, p.Count);
        Assert.All(p, x => Assert.Equal(("Reggaeton", ReglaGenero.Deezer, false), (x.Genero, x.Regla, x.Marcada)));
        Assert.StartsWith("Deezer: álbum «KG0516» (Reggaeton, Latino) → Reggaeton", p[0].PorQue);
        Assert.Equal((2, 2, false), (r.Buscadas, r.Encontradas, r.Cancelada));
        Assert.Equal(2, m.Red.Cuantas("api.deezer.com/search"));
        Assert.Equal(1, m.Red.Cuantas("api.deezer.com/album/"));
        Assert.Equal(0, m.Red.Cuantas("api.deezer.com/track/"));
    }

    [Fact]
    public async Task Deezer_distingue_lo_que_no_encuentra_de_lo_que_solo_trae_generos_amplios()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/search", """{"data":[]}""");
        var canciones = new List<Track> { T("KAROL G", "Bichota", null), T("KAROL G", "Tusa", null) };

        var r = await GeneroDeezer.BuscarAsync(canciones, new Etiquetador.Core.Providers.DeezerProvider(m.Api), null, CancellationToken.None);

        Assert.Equal((0, 2, 0, false), (r.Propuestas.Count, r.Buscadas, r.Encontradas, r.Cancelada));
        Assert.Equal("Buscadas en Deezer 2 canciones: Deezer no ha encontrado ninguna.", GeneroPorReglasViewModel.TextoDeezer(r, 2));
    }

    [Fact]
    public async Task Deezer_no_busca_los_mashups()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/search", BusquedaDeezer).Responde("api.deezer.com/album/77", AlbumDeezer);
        var mashup = T("KAROL G", "Bichota x Tusa (Gery Mashup)", null);

        Assert.Empty(GeneroDeezer.Candidatas(new[] { mashup }, Array.Empty<string>()));
        var r = await GeneroDeezer.BuscarAsync(new[] { mashup }, new Etiquetador.Core.Providers.DeezerProvider(m.Api), null, CancellationToken.None);
        Assert.Empty(r.Propuestas);
        Assert.Equal(0, r.Buscadas);
        Assert.Equal(0, m.Red.Cuantas("api.deezer.com"));
    }

    [Fact]
    public void Deezer_solo_busca_las_que_siguen_sin_genero_ni_propuesta()
    {
        var sin = T("KAROL G", "Bichota", null);
        var conPropuesta = T("KAROL G", "Tusa", null);
        var conGenero = T("KAROL G", "Provenza", "Reggaeton");
        Assert.Equal(new[] { sin }, GeneroDeezer.Candidatas(new[] { sin, conPropuesta, conGenero }, new[] { conPropuesta.FilePath }));
    }

    // Revisión: cancelar lanzaba la excepción y se perdía todo lo calculado (cancelar al 90 % no dejaba
    // ninguna propuesta). Ahora se para entre una canción y la siguiente y devuelve lo que lleva.
    [Fact]
    public async Task Deezer_cancelado_entre_una_cancion_y_la_siguiente_conserva_lo_encontrado()
    {
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com/search", BusquedaDeezer).Responde("api.deezer.com/album/77", AlbumDeezer);
        using var cts = new CancellationTokenSource();
        var canciones = new List<Track> { T("KAROL G", "Bichota", null), T("KAROL G", "Tusa", null) };

        var r = await GeneroDeezer.BuscarAsync(canciones, new Etiquetador.Core.Providers.DeezerProvider(m.Api), new EnLinea(_ => cts.Cancel()), cts.Token);

        Assert.Equal(1, m.Red.Cuantas("api.deezer.com/search"));
        Assert.Equal(canciones[0].FilePath, Assert.Single(r.Propuestas).Ruta);
        Assert.Equal((1, 1, true), (r.Buscadas, r.Encontradas, r.Cancelada));
        Assert.Equal("Búsqueda en Deezer cancelada a medias, tras 1 de 2 canciones: 1 propuesta con un subgénero concreto del álbum, desmarcada para que la revises.",
                     GeneroPorReglasViewModel.TextoDeezer(r, 2));
    }

    // Y también si se cancela mientras espera la respuesta de Deezer.
    [Fact]
    public async Task Deezer_cancelado_a_mitad_de_una_peticion_conserva_lo_encontrado()
    {
        using var m = new MontajeApi();
        using var cts = new CancellationTokenSource();
        m.Red.Cuando("api.deezer.com/search", async (n, req, ct) =>
        {
            if (n == 1) return RedFalsa.Respuesta(req, BusquedaDeezer);
            cts.Cancel();   // la segunda no contesta: se cancela mientras espera
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        }).Responde("api.deezer.com/album/77", AlbumDeezer);
        var canciones = new List<Track> { T("KAROL G", "Bichota", null), T("KAROL G", "Tusa", null), T("KAROL G", "Provenza", null) };

        var r = await GeneroDeezer.BuscarAsync(canciones, new Etiquetador.Core.Providers.DeezerProvider(m.Api), null, cts.Token);

        Assert.Equal(canciones[0].FilePath, Assert.Single(r.Propuestas).Ruta);
        Assert.Equal((1, 1, true), (r.Buscadas, r.Encontradas, r.Cancelada));
        Assert.Equal(2, m.Red.Cuantas("api.deezer.com/search"));
    }

    // Revisión (ronda 2): si se cancelaba mientras pedía el álbum, la canción contaba como encontrada
    // pero no como buscada, y el estado decía «tras 1 de 3 canciones: … Deezer ha encontrado 2».
    [Fact]
    public async Task Deezer_cancelado_mientras_pide_el_album_no_cuenta_la_cancion_como_encontrada()
    {
        static string Busqueda(string titulo, int album) => $$$"""
            {"data":[{"id":{{{album}}},"title":"{{{titulo}}}","duration":178,"artist":{"name":"KAROL G"},"album":{"title":"Album {{{album}}}","id":{{{album}}},"cover_big":"x"}}]}
            """;
        using var m = new MontajeApi();
        using var cts = new CancellationTokenSource();
        m.Red.Cuando("api.deezer.com/search", (n, req, _) => Task.FromResult(RedFalsa.Respuesta(req, n == 1 ? Busqueda("Bichota", 77) : Busqueda("Tusa", 88))))
             .Responde("api.deezer.com/album/77", """{"genres":{"data":[{"id":197,"name":"Latino"}]}}""")
             .Cuando("api.deezer.com/album/88", async (_, _, ct) =>
             {
                 cts.Cancel();   // se cancela mientras espera el álbum de la segunda canción
                 await Task.Delay(Timeout.Infinite, ct);
                 throw new InvalidOperationException("inalcanzable");
             });
        var canciones = new List<Track> { T("KAROL G", "Bichota", null), T("KAROL G", "Tusa", null), T("KAROL G", "Provenza", null) };

        var r = await GeneroDeezer.BuscarAsync(canciones, new Etiquetador.Core.Providers.DeezerProvider(m.Api), null, cts.Token);

        Assert.Equal(1, m.Red.Cuantas("api.deezer.com/album/88"));
        Assert.Empty(r.Propuestas);
        Assert.Equal((1, 1, true), (r.Buscadas, r.Encontradas, r.Cancelada));
        Assert.True(r.Encontradas <= r.Buscadas);
        Assert.Equal("Búsqueda en Deezer cancelada a medias, tras 1 de 3 canciones: ninguna propuesta: Deezer ha encontrado 1, pero su álbum solo trae géneros amplios, que no distinguen estilos.",
                     GeneroPorReglasViewModel.TextoDeezer(r, 3));
    }

    // Revisión: con 1 canción decía «Buscadas en Deezer 1 canción», y sin propuestas siempre culpaba a
    // los géneros de los álbumes, aunque Deezer no hubiera encontrado las canciones.
    [Fact]
    public void Los_textos_de_Deezer_distinguen_singular_y_plural()
    {
        static BusquedaGeneroDeezer B(int propuestas, int buscadas, int encontradas, bool cancelada = false)
            => new(Enumerable.Range(0, propuestas)
                       .Select(i => new PropuestaGeneroRegla($"r{i}", "Reggaeton", ReglaGenero.Deezer, "x", false)).ToList(),
                   buscadas, encontradas, cancelada);

        Assert.Equal("Buscada en Deezer 1 canción: 1 propuesta con un subgénero concreto del álbum, desmarcada para que la revises.",
                     GeneroPorReglasViewModel.TextoDeezer(B(1, 1, 1), 1));
        Assert.Equal("Buscada en Deezer 1 canción: Deezer no la ha encontrado.", GeneroPorReglasViewModel.TextoDeezer(B(0, 1, 0), 1));
        Assert.Equal("Buscada en Deezer 1 canción: ninguna propuesta: Deezer ha encontrado 1, pero su álbum solo trae géneros amplios, que no distinguen estilos.",
                     GeneroPorReglasViewModel.TextoDeezer(B(0, 1, 1), 1));
        Assert.Equal("Buscadas en Deezer 3 canciones: ninguna propuesta: Deezer ha encontrado 2, pero sus álbumes solo traen géneros amplios, que no distinguen estilos.",
                     GeneroPorReglasViewModel.TextoDeezer(B(0, 3, 2), 3));
        Assert.Equal("Buscadas en Deezer 3 canciones: Deezer no ha encontrado ninguna.", GeneroPorReglasViewModel.TextoDeezer(B(0, 3, 0), 3));
        Assert.EndsWith("2 propuestas con un subgénero concreto del álbum, desmarcadas para que las revises.", GeneroPorReglasViewModel.TextoDeezer(B(2, 3, 3), 3));
        Assert.Equal("Búsqueda en Deezer cancelada a medias, tras 0 de 1 canción: ninguna propuesta.", GeneroPorReglasViewModel.TextoDeezer(B(0, 0, 0, true), 1));
        Assert.Equal("Deezer", GeneroReglaFila.TextoRegla(ReglaGenero.Deezer));
    }

    /// <summary>Un avance que se ejecuta en el momento (Progress lo manda al contexto de sincronización).</summary>
    private sealed class EnLinea : IProgress<(int Hechas, int Total)>
    {
        private readonly Action<(int, int)> _a;
        public EnLinea(Action<(int, int)> a) => _a = a;
        public void Report((int Hechas, int Total) valor) => _a(valor);
    }

    private sealed class Historial : IHistorialDeshacer
    {
        private readonly List<UndoRecord> _lista;
        public Historial(List<UndoRecord> lista) => _lista = lista;
        public string Anotar(UndoRecord registro) { _lista.Add(registro); return ""; }
    }
}
