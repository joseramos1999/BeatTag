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

    [Fact]
    public void Un_genero_generico_se_sustituye_y_se_deshace()
    {
        var paths = new AppPaths(Path.Combine(_dir, "datos"));
        var ruta = Mp3("a.mp3", "Latin");
        using (var m = ManifiestoDeshacer.Abrir(paths.NuevoManifiesto(), out var error))
        {
            var res = EscrituraPorReglas.Escribir(new[] { new CambioEtiquetas(ruta) { GeneroNuevo = "Reggaeton", GeneroVisto = "Latin" } },
                                                  m, error, null, null, null, CancellationToken.None, "prueba", TipoEjecucion.GeneroPorReglas);
            Assert.Equal(new[] { ruta }, res.Escritos);
            Assert.Equal(1, m!.Anotados);
        }
        using (var f = TagLib.File.Create(ruta)) Assert.Equal(new[] { "Reggaeton" }, f.Tag.Genres);

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

    private sealed class Historial : IHistorialDeshacer
    {
        private readonly List<UndoRecord> _lista;
        public Historial(List<UndoRecord> lista) => _lista = lista;
        public string Anotar(UndoRecord registro) { _lista.Add(registro); return ""; }
    }
}
