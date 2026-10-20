using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Copias;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Copia de seguridad y traslado del trabajo manual (T15, equipo «volumen_copias», tanda 4). El caso
/// real: un DJ que pasa de Windows a macOS, o que reinstala, y quiere llevarse fichas, lo aplicado y
/// descartado, las identificaciones pagadas… sin las claves (cifradas, solo sirven en su equipo) y con
/// las rutas de la música en su sitio nuevo («E:\Musica» → «/Volumes/USB/Musica»).
/// </summary>
public class CopiaSeguridadTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly DateTime Fecha = new(2026, 10, 17, 15, 56, 0);

    private AppPaths Datos(string nombre)
    {
        var p = new AppPaths(Path.Combine(_dir, nombre));
        Directory.CreateDirectory(p.DataDir);
        return p;
    }

    private static void Escribir(string ruta, string texto, bool bom = false)
        => File.WriteAllText(ruta, texto, new UTF8Encoding(bom));

    private static JsonNode Json(string ruta) => JsonNode.Parse(File.ReadAllText(ruta, Encoding.UTF8))!;

    /// <summary>Un equipo con Windows y la música en E:\Musica\COMERCIAL: lo que hay en su carpeta de datos.</summary>
    private AppPaths EquipoWindows()
    {
        var p = Datos("windows");
        Escribir(p.ConfigPath, """
            {
              "Folders": ["E:\\Musica\\COMERCIAL\\House", "E:\\Musica\\COMERCIAL\\Techno"],
              "BandejaCarpeta": "E:\\Descargas\\Bandeja",
              "Theme": "oscuro",
              "SpotifyId": "id-de-la-copia",
              "SpotifySecretEnc": "CIFRADO-EN-WINDOWS",
              "DiscogsTokenEnc": "OTRO-CIFRADO",
              "WindowWidth": 1600,
              "WindowMaximized": true
            }
            """);
        Escribir(p.FichasDjPath, """{"E:\\Musica\\COMERCIAL\\House\\Tema.mp3":{"Energia":7}}""");
        // IgnoreList escribe con marca de orden de bytes.
        Escribir(p.AppliedPath, """["E:\\Musica\\COMERCIAL\\Techno\\Pista.mp3"]""", bom: true);
        Escribir(p.IdentificacionCachePath, """{"E:\\Musica\\COMERCIAL\\House\\Tema.mp3":{"M":638000000000000000,"S":4096,"A":"Artista","T":"Tema"}}""");
        Escribir(AlmacenSustituciones.RutaEn(p.DataDir), "[]");
        // Un archivo de datos que esta versión no conoce: entra igual (no se queda fuera por olvido).
        Escribir(Path.Combine(p.DataDir, "novedades-artistas.json"), """{"E:\\Musica\\COMERCIAL":"x"}""");
        // Cachés y el config de la versión PowerShell: no entran.
        Escribir(p.FingerprintCachePath, "{}");
        Escribir(p.ScanCachePath, "{}");
        Escribir(p.AnalysisCachePath, "{}");
        Escribir(p.LoudnessCachePath, "{}");
        Escribir(Path.Combine(p.DataDir, "config.json"), """{"SpotifySecret":"en-claro"}""");
        return p;
    }

    private static Dictionary<string, string> Entradas(string zip)
    {
        using var a = ZipFile.OpenRead(zip);
        return a.Entries.ToDictionary(e => e.FullName, e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); });
    }

    // ------------------------------------------------------------------ hacer la copia

    [Fact]
    public void La_copia_lleva_el_trabajo_y_deja_fuera_las_caches_las_claves_y_la_ventana()
    {
        var p = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");

        var r = CopiaSeguridad.Crear(p, zip, "manual", Fecha);

        Assert.True(r.Ok, r.Error);
        var e = Entradas(zip);
        Assert.Contains(CopiaSeguridad.NombreManifiesto, e.Keys);
        foreach (var n in new[] { "config.net.json", "fichas-dj.json", "aplicadas.json", "identificacion.json", "bandeja-sustituidas.json", "novedades-artistas.json" })
            Assert.Contains(n, e.Keys);
        foreach (var n in new[] { "huellas.json", "scan-cache.json", "analysis-cache.json", "sonoridad.json", "config.json" })
            Assert.DoesNotContain(n, e.Keys);
        Assert.Equal(6, r.Archivos);

        var cfg = JsonNode.Parse(e["config.net.json"])!.AsObject();
        Assert.False(cfg.ContainsKey("SpotifySecretEnc"));
        Assert.False(cfg.ContainsKey("DiscogsTokenEnc"));
        Assert.False(cfg.ContainsKey("WindowWidth"));
        Assert.False(cfg.ContainsKey("WindowMaximized"));
        Assert.Equal("oscuro", (string?)cfg["Theme"]);
        Assert.Equal("id-de-la-copia", (string?)cfg["SpotifyId"]);   // no es secreto
        Assert.DoesNotContain("CIFRADO", string.Concat(e.Values));
        Assert.DoesNotContain("en-claro", string.Concat(e.Values));
    }

    [Fact]
    public void Una_configuracion_que_no_se_entiende_no_viaja_porque_podria_llevar_claves()
    {
        var p = Datos("rota");
        Escribir(p.ConfigPath, "{ \"SpotifySecretEnc\": \"abc\", ");   // cortada
        Escribir(p.FichasDjPath, "{}");

        var r = CopiaSeguridad.Crear(p, Path.Combine(_dir, "c.zip"), "manual", Fecha);

        Assert.True(r.Ok);
        Assert.Equal(1, r.Archivos);
        Assert.Contains(r.Avisos, a => a.StartsWith("config.net.json", StringComparison.Ordinal));
        Assert.DoesNotContain("config.net.json", Entradas(r.Ruta).Keys);
    }

    [Fact]
    public void Un_json_desconocido_demasiado_grande_se_trata_como_cache()
    {
        var p = Datos("grande");
        Escribir(p.FichasDjPath, "{}");
        using (var fs = File.Create(Path.Combine(p.DataDir, "otra-cache.json"))) fs.SetLength(CopiaSeguridad.TopeOtros + 1);

        var nombres = CopiaSeguridad.ArchivosAIncluir(p).Select(Path.GetFileName).ToList();

        Assert.Contains("fichas-dj.json", nombres);
        Assert.DoesNotContain("otra-cache.json", nombres);
    }

    // Los archivos que la Ayuda pide copiar (AyudaTests) son los que la copia lleva siempre. El
    // diario de sustituciones en curso no: solo sirve en el equipo donde se cortó la sustitución.
    [Fact]
    public void Los_archivos_de_trabajo_conocidos_son_los_que_la_Ayuda_pide_copiar()
    {
        var p = new AppPaths(Path.Combine(_dir, "no-se-crea"));
        var nombres = p.ArchivosDeTrabajo().Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var n in new[]
                 {
                     Path.GetFileName(p.FichasDjPath), Path.GetFileName(p.ColeccionesPath), Path.GetFileName(p.BandejaPath),
                     AlmacenPendientes.NombreArchivo, AlmacenListasSeguidas.NombreArchivo, MapaGenerosCarpeta.NombreArchivo,
                     Path.GetFileName(AlmacenSustituciones.RutaEn("datos")), "descartadas.json", "aplicadas.json",
                     "marcas-aplicar.json", "ArtistAliases.json", "ArtistsExceptions.json", "identificacion.json", "config.net.json",
                 })
            Assert.Contains(n, nombres);
        Assert.DoesNotContain(p.ArchivosDeTrabajo(), r => p.FueraDeLaCopia().Contains(r));
    }

    // ------------------------------------------------------------------ copia automática

    [Fact]
    public void La_copia_automatica_no_se_repite_si_nada_ha_cambiado_y_conserva_las_cinco_ultimas()
    {
        var p = Datos("auto");
        Assert.Null(CopiaSeguridad.CopiaAutomatica(p, Fecha));   // sin datos todavía, nada que copiar

        Escribir(p.FichasDjPath, "{}");
        var primera = CopiaSeguridad.CopiaAutomatica(p, Fecha);
        Assert.NotNull(primera);
        Assert.True(primera!.Ok);
        // Abrir y cerrar sin tocar nada no hace otra: cinco arranques así no echan fuera la buena.
        Assert.Null(CopiaSeguridad.CopiaAutomatica(p, Fecha.AddMinutes(1)));

        for (var i = 1; i <= 7; i++)
        {
            Escribir(p.FichasDjPath, new JsonObject { [$@"D:\M\{i}.mp3"] = new JsonObject { ["Energia"] = i } }.ToJsonString());
            Assert.True(CopiaSeguridad.CopiaAutomatica(p, Fecha.AddHours(i))!.Ok);
        }

        var copias = CopiaSeguridad.Copias(CopiaSeguridad.CarpetaCopias(p), CopiaSeguridad.PrefijoAutomatica);
        Assert.Equal(CopiaSeguridad.CopiasQueSeConservan, copias.Count);
        Assert.EndsWith($"auto_{Fecha.AddHours(7):yyyyMMdd_HHmmss}.zip", copias[^1]);
        Assert.EndsWith($"auto_{Fecha.AddHours(3):yyyyMMdd_HHmmss}.zip", copias[0]);
    }

    [Fact]
    public void Los_temporales_de_una_copia_cortada_se_limpian_y_los_recientes_no()
    {
        var p = Datos("tmp");
        var carpeta = CopiaSeguridad.CarpetaCopias(p);
        Directory.CreateDirectory(carpeta);
        var viejo = Path.Combine(carpeta, "auto_20261001_100000.zip.tmp");
        var reciente = Path.Combine(carpeta, "auto_20261017_155500.zip.tmp");
        File.WriteAllText(viejo, "x");
        File.WriteAllText(reciente, "x");
        File.SetLastWriteTime(viejo, Fecha.AddDays(-16));
        File.SetLastWriteTime(reciente, Fecha.AddMinutes(-1));

        CopiaSeguridad.LimpiarTemporales(carpeta, Fecha);

        Assert.False(File.Exists(viejo));
        Assert.True(File.Exists(reciente));
    }

    // ------------------------------------------------------------------ leer una copia

    [Fact]
    public void Leer_y_la_vista_previa_dicen_que_se_sustituye_que_se_anade_y_que_se_conserva()
    {
        var origen = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");
        Assert.True(CopiaSeguridad.Crear(origen, zip, "manual", Fecha).Ok);

        var aqui = Datos("aqui");
        Escribir(aqui.FichasDjPath, "{}");
        Escribir(aqui.ColeccionesPath, "[]");   // no está en la copia

        var c = CopiaSeguridad.Leer(zip, aqui, out var error);
        Assert.NotNull(c);
        Assert.Equal("", error);
        Assert.Equal("manual", c!.Manifiesto.Tipo);
        Assert.Equal(CopiaSeguridad.Sistema(), c.Manifiesto.Sistema);

        var filas = CopiaSeguridad.Vista(c, aqui);
        Assert.Equal("Sustituye al actual", filas.Single(f => f.Nombre == "fichas-dj.json").Accion);
        Assert.Equal("Se añade", filas.Single(f => f.Nombre == "aplicadas.json").Accion);
        Assert.Equal("No está en la copia: se conserva el actual", filas.Single(f => f.Nombre == "colecciones.json").Accion);
        Assert.Equal("Canciones ya aplicadas en Enriquecer", filas.Single(f => f.Nombre == "aplicadas.json").Descripcion);
        Assert.Equal("No existe", filas.Single(f => f.Nombre == "aplicadas.json").ActualTexto);
    }

    [Fact]
    public void Un_zip_cualquiera_no_se_restaura()
    {
        var p = Datos("x");
        var ajeno = Path.Combine(_dir, "ajeno.zip");
        using (var a = ZipFile.Open(ajeno, ZipArchiveMode.Create))
            using (var w = new StreamWriter(a.CreateEntry("fichas-dj.json").Open())) w.Write("{}");
        Assert.Null(CopiaSeguridad.Leer(ajeno, p, out var e1));
        Assert.Equal("El archivo no es una copia de seguridad de BeatTag.", e1);

        var noZip = Path.Combine(_dir, "no.zip");
        File.WriteAllText(noZip, "esto no es un zip");
        Assert.Null(CopiaSeguridad.Leer(noZip, p, out var e2));
        Assert.Equal("El archivo está dañado o no es un ZIP.", e2);
    }

    [Fact]
    public void Una_copia_de_una_version_posterior_no_se_restaura()
    {
        var p = Datos("x");
        var zip = Path.Combine(_dir, "futura.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(a.CreateEntry(CopiaSeguridad.NombreManifiesto).Open()))
                w.Write($$"""{"Formato":{{CopiaSeguridad.FormatoActual + 1}},"Aplicacion":"BeatTag","Version":"9.0"}""");
            using (var w = new StreamWriter(a.CreateEntry("fichas-dj.json").Open())) w.Write("{}");
        }
        Assert.Null(CopiaSeguridad.Leer(zip, p, out var error));
        Assert.Contains("versión más reciente de BeatTag (9.0)", error);
    }

    // Una copia manipulada no puede escribir fuera de la carpeta de datos ni colar una caché o algo
    // que no sea un archivo de datos.
    [Fact]
    public void Las_entradas_que_no_son_archivos_de_datos_sueltos_se_descartan()
    {
        var p = Datos("x");
        var zip = Path.Combine(_dir, "manipulada.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(a.CreateEntry(CopiaSeguridad.NombreManifiesto).Open()))
                w.Write("""{"Formato":1,"Aplicacion":"BeatTag"}""");
            foreach (var n in new[] { "fichas-dj.json", "../fuera.json", "sub/dentro.json", "..\\fuera2.json", "programa.exe", "huellas.json", ".oculto.json" })
                using (var w = new StreamWriter(a.CreateEntry(n).Open())) w.Write("{}");
        }

        var c = CopiaSeguridad.Leer(zip, p, out _);

        Assert.NotNull(c);
        Assert.Equal(new[] { "fichas-dj.json" }, c!.Archivos.Keys);
        Assert.Equal(6, c.Descartados.Count);
    }

    // ------------------------------------------------------------------ rutas y raíces

    [Fact]
    public void Una_ruta_de_Windows_pasa_a_macOS_con_su_separador()
    {
        var mapa = new MapaRaices(new[] { new CambioRaiz(@"E:\Musica", "/Volumes/USB/Musica") });

        Assert.Equal("/Volumes/USB/Musica/House/Tema.mp3", mapa.Traducir(@"E:\Musica\House\Tema.mp3"));
        Assert.Equal("/Volumes/USB/Musica/House/Tema.mp3", mapa.Traducir("e:/MUSICA/House/Tema.mp3"));   // mayúsculas y barras
        Assert.Equal("/Volumes/USB/Musica", mapa.Traducir(@"E:\Musica\"));
        Assert.Null(mapa.Traducir(@"E:\Musica 2\Tema.mp3"));   // empieza igual, pero es otra carpeta
        Assert.Null(mapa.Traducir(@"C:\Users\dj\Tema.mp3"));
        Assert.Null(mapa.Traducir("Daft Punk - One More Time"));
    }

    [Fact]
    public void De_macOS_a_Windows_los_acentos_descompuestos_se_componen()
    {
        var nfdCarpeta = "Mu\u0301sica";
        var mapa = new MapaRaices(new[] { new CambioRaiz("/Volumes/USB/" + nfdCarpeta, @"D:\Música") });

        var res = mapa.Traducir("/Volumes/USB/" + nfdCarpeta + "/Cancio\u0301n.mp3");

        Assert.Equal(@"D:\Música\Canción.mp3", res);
        Assert.True(res!.IsNormalized(NormalizationForm.FormC));
    }

    // En macOS, un archivo copiado desde HFS+ puede tener el nombre descompuesto: se usa la forma que
    // tiene de verdad en el disco, o la ficha no casaría con su canción.
    [Fact]
    public void Hacia_macOS_se_elige_la_forma_de_los_acentos_que_hay_en_el_disco()
    {
        var mapa = new MapaRaices(new[] { new CambioRaiz(@"D:\Musica", "/Volumes/USB/Musica") });
        var nfd = "/Volumes/USB/Musica/Cancio\u0301n.mp3";

        Assert.Equal(nfd, mapa.Traducir(@"D:\Musica\Canción.mp3", existe: r => r == nfd));
        Assert.Equal("/Volumes/USB/Musica/Canción.mp3", mapa.Traducir(@"D:\Musica\Canción.mp3", existe: _ => false));
    }

    [Fact]
    public void La_raiz_mas_concreta_manda_y_las_que_no_cambian_nada_se_ignoran()
    {
        var mapa = new MapaRaices(new[]
        {
            new CambioRaiz(@"D:\Musica", "/Volumes/A/Musica"),
            new CambioRaiz(@"D:\Musica\Acapellas", "/Volumes/B/Acapellas"),
            new CambioRaiz(@"E:\Igual", @"e:\igual\"),   // la misma carpeta
            new CambioRaiz("/", "/Volumes/C"),          // «/» no es una raíz
            new CambioRaiz(@"F:\Vacia", ""),            // sin cambios
        });

        Assert.Equal(2, mapa.Count);
        Assert.Equal("/Volumes/B/Acapellas/a.mp3", mapa.Traducir(@"D:\Musica\Acapellas\a.mp3"));
        Assert.Equal("/Volumes/A/Musica/House/b.mp3", mapa.Traducir(@"D:\Musica\House\b.mp3"));
        Assert.True(new MapaRaices(new[] { new CambioRaiz(@"E:\Igual", @"E:\Igual") }).Vacio);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(@"D:\Musica", "")]
    [InlineData("/Volumes/USB/Musica", "")]
    [InlineData(@"\\nas\musica\dj", "")]
    [InlineData("Musica", "«Musica» no es una ruta completa de carpeta.")]
    [InlineData("/", "«/» no puede ser la ubicación de la música")]
    public void Validar_pide_rutas_completas(string destino, string empiezaPor)
    {
        var error = MapaRaices.Validar(new[] { new CambioRaiz(@"E:\Musica", destino) });
        if (empiezaPor.Length == 0) Assert.Equal("", error);
        else Assert.StartsWith(empiezaPor, error);
    }

    [Fact]
    public void Reescribir_cambia_claves_y_valores_conserva_el_orden_y_cuenta_las_repetidas()
    {
        var nodo = JsonNode.Parse("""
            {
              "E:\\M\\a.mp3": {"Destino": "E:\\M\\b.mp3", "Nota": "E:\\M sin barra no es ruta de un archivo"},
              "texto": ["E:\\M\\c.mp3", "no es ruta"],
              "F:\\M\\a.mp3": 2
            }
            """);
        var mapa = new MapaRaices(new[] { new CambioRaiz(@"E:\M", @"F:\M") });

        var r = RutasJson.Reescribir(nodo, s => mapa.Traducir(s));

        var o = r.Raiz!.AsObject();
        Assert.Equal(new[] { @"F:\M\a.mp3", "texto" }, o.Select(x => x.Key));   // la segunda F:\M\a.mp3 coincide y se pierde
        Assert.Equal(1, r.Repetidas);
        Assert.Equal(@"F:\M\b.mp3", (string?)o[@"F:\M\a.mp3"]!["Destino"]);
        Assert.Equal(@"F:\M\c.mp3", (string?)o["texto"]![0]);
        Assert.Equal("no es ruta", (string?)o["texto"]![1]);
        Assert.Equal(3, r.Cambiadas);
    }

    [Fact]
    public void Resellar_acepta_el_redondeo_del_disco_nuevo_pero_no_un_archivo_retocado()
    {
        const long m = 638000000000000000;
        JsonNode Nodo() => new JsonObject { [@"D:\M\a.mp3"] = new JsonObject { ["M"] = m, ["S"] = 4096, ["A"] = "x" } };
        long Fecha(JsonNode n) => (long)n[@"D:\M\a.mp3"]!["M"]!;

        var redondeada = Nodo();
        Assert.Equal(1, RutasJson.Resellar(redondeada, _ => (m + TimeSpan.FromSeconds(1.5).Ticks, 4096)));
        Assert.Equal(m + TimeSpan.FromSeconds(1.5).Ticks, Fecha(redondeada));

        var lejos = Nodo();
        Assert.Equal(0, RutasJson.Resellar(lejos, _ => (m + TimeSpan.FromSeconds(3).Ticks, 4096)));
        Assert.Equal(m, Fecha(lejos));

        var otroTamano = Nodo();
        Assert.Equal(0, RutasJson.Resellar(otroTamano, _ => (m + 1, 4097)));

        Assert.Equal(0, RutasJson.Resellar(Nodo(), _ => null));   // el archivo no está en este equipo
    }

    // MEDIDO en la biblioteca real (ver «mediciones» de la tanda 4): las carpetas de una misma carpeta
    // de primer nivel se resumen en su antepasado común, para no pedir una confirmación por carpeta.
    [Fact]
    public void Las_carpetas_de_la_copia_se_agrupan_en_pocas_raices()
    {
        var carpetas = Enumerable.Range(1, 43).Select(i => $@"E:\Musica\COMERCIAL\Carpeta {i}")
                                 .Append(@"E:\Descargas\Bandeja").ToList();
        var rutas = new[] { @"E:\Musica\COMERCIAL\Carpeta 3\a.mp3", @"C:\Users\dj\Downloads\MUSICA\b.mp3" };

        var raices = CopiaSeguridad.AgruparRaices(carpetas, rutas).Select(r => r.Texto()).ToList();

        Assert.Equal(new[] { @"E:\Musica\COMERCIAL", @"E:\Descargas\Bandeja", @"C:\Users\dj\Downloads\MUSICA" }, raices);

        var mac = CopiaSeguridad.AgruparRaices(new[] { "/Volumes/USB/Musica/House", "/Volumes/USB/Musica/Techno", "/Volumes/Otro/Edits" }, Array.Empty<string>())
                                .Select(r => r.Texto()).ToList();
        Assert.Equal(new[] { "/Volumes/USB/Musica", "/Volumes/Otro/Edits" }, mac);
    }

    [Fact]
    public void Se_propone_donde_esta_ahora_cada_raiz()
    {
        var carpetas = new[] { @"E:\Musica\COMERCIAL\House", @"E:\Musica\COMERCIAL\Techno", @"E:\Edits", @"E:\Acapellas", @"G:\Perdida" };
        var rutas = new[] { @"E:\Musica\COMERCIAL\House\a.mp3", @"E:\Musica\COMERCIAL\Techno\b.mp3", @"E:\Edits\c.mp3", @"G:\Perdida\d.mp3" };
        var existen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"E:\Acapellas", "/Volumes/USB/Edits" };

        var res = CopiaSeguridad.ProponerRaices(carpetas, rutas, existen.Contains,
                                                carpetasActuales: new[] { "/Volumes/USB/COMERCIAL/House" },
                                                volumenes: new[] { "/Volumes/USB", "/Users/dj" });

        var comercial = res.Single(r => r.Origen == @"E:\Musica\COMERCIAL");
        Assert.Equal("/Volumes/USB/COMERCIAL", comercial.Propuesta);   // por la biblioteca de aquí
        Assert.Equal(2, comercial.Rutas);
        Assert.Contains("/Volumes/USB/COMERCIAL/House", comercial.Motivo);
        Assert.Equal("/Volumes/USB/Edits", res.Single(r => r.Origen == @"E:\Edits").Propuesta);   // por los discos
        Assert.Equal(@"E:\Acapellas", res.Single(r => r.Origen == @"E:\Acapellas").Propuesta);   // existe aquí
        var perdida = res.Single(r => r.Origen == @"G:\Perdida");
        Assert.Equal("", perdida.Propuesta);
        Assert.StartsWith("No se ha encontrado", perdida.Motivo);
    }

    [Fact]
    public void Si_solo_cambia_la_letra_de_la_unidad_se_encuentra_en_la_nueva()
    {
        var res = CopiaSeguridad.ProponerRaices(new[] { @"E:\Musica" }, new[] { @"E:\Musica\a.mp3" },
                                                r => string.Equals(r, @"F:\Musica", StringComparison.OrdinalIgnoreCase),
                                                Array.Empty<string>(), new[] { @"F:\" });
        Assert.Equal(@"F:\Musica", res.Single().Propuesta);
    }

    // ------------------------------------------------------------------ restaurar

    /// <summary>Un Mac con BeatTag recién puesto, sus claves ya introducidas y la ventana a su gusto.</summary>
    private AppPaths EquipoMac()
    {
        var p = Datos("mac");
        Escribir(p.ConfigPath, """
            {
              "Folders": [],
              "Theme": "claro",
              "SpotifyId": "id-del-mac",
              "SpotifySecretEnc": "CIFRADO-EN-EL-MAC",
              "WindowWidth": 1280
            }
            """);
        Escribir(p.FichasDjPath, """{"/Users/dj/Music/vieja.mp3":{"Energia":3}}""");
        Escribir(p.ColeccionesPath, "[]");
        return p;
    }

    [Fact]
    public void Restaurar_en_otro_sistema_reescribe_las_rutas_conserva_las_claves_de_aqui_y_guarda_antes_lo_que_habia()
    {
        var win = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");
        Assert.True(CopiaSeguridad.Crear(win, zip, "manual", Fecha).Ok);
        var mac = EquipoMac();

        var c = CopiaSeguridad.Leer(zip, mac, out _)!;
        var mapa = new MapaRaices(new[]
        {
            new CambioRaiz(@"E:\Musica\COMERCIAL", "/Volumes/USB/COMERCIAL"),
            new CambioRaiz(@"E:\Descargas\Bandeja", "/Users/dj/Downloads/Bandeja"),
        });
        var prep = CopiaSeguridad.PrepararRestauracion(c, mac, mapa, existe: null, Fecha.AddDays(1));
        Assert.True(prep.Ok, prep.Error);
        Assert.Equal(6, prep.Archivos);
        Assert.Equal(7, prep.RutasReescritas);   // 3 de la configuración, 1 + 1 + 1 de los datos y la del archivo desconocido
        Assert.NotNull(CopiaSeguridad.Pendiente(mac));

        // Nada cambia hasta el próximo arranque.
        Assert.Contains("vieja.mp3", File.ReadAllText(mac.FichasDjPath));

        var r = CopiaSeguridad.AplicarRestauracionPendiente(mac, Fecha.AddDays(2));

        Assert.NotNull(r);
        Assert.True(r!.Ok, r.Texto);
        Assert.Equal(6, r.Restaurados);
        Assert.Null(CopiaSeguridad.Pendiente(mac));
        Assert.False(Directory.Exists(CopiaSeguridad.CarpetaRestauracion(mac)));

        var cfg = Json(mac.ConfigPath).AsObject();
        Assert.Equal("oscuro", (string?)cfg["Theme"]);                         // de la copia
        Assert.Equal("CIFRADO-EN-EL-MAC", (string?)cfg["SpotifySecretEnc"]);   // de aquí
        Assert.Equal("id-del-mac", (string?)cfg["SpotifyId"]);                 // va en pareja con su secreto
        Assert.False(cfg.ContainsKey("DiscogsTokenEnc"));
        Assert.Equal(1280, (int)cfg["WindowWidth"]!);
        Assert.False(cfg.ContainsKey("WindowMaximized"));
        Assert.Equal(new[] { "/Volumes/USB/COMERCIAL/House", "/Volumes/USB/COMERCIAL/Techno" },
                     cfg["Folders"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("/Users/dj/Downloads/Bandeja", (string?)cfg["BandejaCarpeta"]);

        Assert.Equal(7, (int)Json(mac.FichasDjPath)["/Volumes/USB/COMERCIAL/House/Tema.mp3"]!["Energia"]!);
        Assert.Equal("/Volumes/USB/COMERCIAL/Techno/Pista.mp3", (string?)Json(mac.AppliedPath)[0]);   // con BOM en la copia
        Assert.NotNull(Json(mac.IdentificacionCachePath)["/Volumes/USB/COMERCIAL/House/Tema.mp3"]);
        Assert.True(File.Exists(mac.ColeccionesPath));   // no estaba en la copia: se conserva

        // Lo que había, guardado antes de sustituir y con su vieja ficha.
        var previa = CopiaSeguridad.Copias(CopiaSeguridad.CarpetaCopias(mac), CopiaSeguridad.PrefijoAntesDeRestaurar).Single();
        Assert.Equal(previa, r.CopiaPrevia);
        Assert.Contains("vieja.mp3", Entradas(previa)["fichas-dj.json"]);
        Assert.Contains(Path.GetFileName(previa), r.Texto);
        Assert.StartsWith("Se ha restaurado la copia de seguridad del 17/10/2026 15:56 (6 archivos).", r.Texto);
    }

    // MEDIDO al restaurar los datos reales sobre una configuración con el secreto de Spotify y sin su
    // ID: SpotifyId quedaba a null. El ID va en pareja con el secreto de aquí; sin él, vacío.
    [Fact]
    public void El_ID_de_Spotify_sigue_a_su_secreto_y_nunca_queda_a_null()
    {
        var aqui = Path.Combine(_dir, "config-aqui.json");
        var copia = Encoding.UTF8.GetBytes("""{"SpotifyId":"id-de-la-copia","Theme":"oscuro"}""");

        Escribir(aqui, """{"SpotifySecretEnc":"CIFRADO"}""");
        var conSecreto = JsonNode.Parse(CopiaSeguridad.ConDatosDelEquipo(copia, aqui))!;
        Assert.Equal("", (string?)conSecreto["SpotifyId"]);
        Assert.Equal("CIFRADO", (string?)conSecreto["SpotifySecretEnc"]);

        Escribir(aqui, """{"SpotifyId":"id-de-aqui","DiscogsTokenEnc":"X"}""");
        var sinSecreto = JsonNode.Parse(CopiaSeguridad.ConDatosDelEquipo(copia, aqui))!;
        Assert.Equal("id-de-la-copia", (string?)sinSecreto["SpotifyId"]);   // aquí no hay secreto que emparejar
        Assert.Equal("X", (string?)sinSecreto["DiscogsTokenEnc"]);
    }

    [Fact]
    public void En_un_equipo_sin_datos_se_restaura_sin_copia_previa_y_sin_decir_que_la_hay()
    {
        var win = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");
        Assert.True(CopiaSeguridad.Crear(win, zip, "manual", Fecha).Ok);
        var nuevo = Datos("nuevo");
        var c = CopiaSeguridad.Leer(zip, nuevo, out _)!;
        Assert.True(CopiaSeguridad.PrepararRestauracion(c, nuevo, new MapaRaices(Array.Empty<CambioRaiz>()), null, Fecha).Ok);

        var r = CopiaSeguridad.AplicarRestauracionPendiente(nuevo, Fecha)!;

        Assert.True(r.Ok, r.Texto);
        Assert.Equal("", r.CopiaPrevia);
        Assert.DoesNotContain("se ha guardado en", r.Texto);
        Assert.Empty(CopiaSeguridad.Copias(CopiaSeguridad.CarpetaCopias(nuevo), CopiaSeguridad.PrefijoAntesDeRestaurar));
        Assert.Contains(@"E:\Musica\COMERCIAL\House\Tema.mp3", Json(nuevo.FichasDjPath).AsObject().Select(x => x.Key));
    }

    [Fact]
    public void Una_configuracion_ilegible_se_aparta_antes_de_sustituirla()
    {
        var win = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");
        Assert.True(CopiaSeguridad.Crear(win, zip, "manual", Fecha).Ok);
        var aqui = Datos("ilegible");
        Escribir(aqui.ConfigPath, "{ cortada");
        var c = CopiaSeguridad.Leer(zip, aqui, out _)!;
        Assert.True(CopiaSeguridad.PrepararRestauracion(c, aqui, new MapaRaices(Array.Empty<CambioRaiz>()), null, Fecha).Ok);

        var r = CopiaSeguridad.AplicarRestauracionPendiente(aqui, Fecha)!;

        Assert.True(r.Ok, r.Texto);
        var apartada = Directory.GetFiles(aqui.DataDir, "config.net.json.ilegible_*").Single();
        Assert.Equal("{ cortada", File.ReadAllText(apartada));
        Assert.Equal("oscuro", (string?)Json(aqui.ConfigPath)["Theme"]);
    }

    [Fact]
    public void Sin_poder_leer_lo_actual_no_se_sustituye_nada()
    {
        var win = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");
        Assert.True(CopiaSeguridad.Crear(win, zip, "manual", Fecha).Ok);
        var mac = EquipoMac();
        var c = CopiaSeguridad.Leer(zip, mac, out _)!;
        Assert.True(CopiaSeguridad.PrepararRestauracion(c, mac, new MapaRaices(Array.Empty<CambioRaiz>()), null, Fecha).Ok);
        var antes = File.ReadAllText(mac.FichasDjPath);

        ResultadoRestauracion r;
        using (new FileStream(mac.ColeccionesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // otro programa lo tiene abierto
            r = CopiaSeguridad.AplicarRestauracionPendiente(mac, Fecha)!;

        Assert.False(r.Ok);
        Assert.StartsWith("No se ha restaurado la copia de seguridad", r.Texto);
        Assert.Equal(antes, File.ReadAllText(mac.FichasDjPath));
        Assert.Equal(0, r.Restaurados);
    }

    [Fact]
    public void Una_preparacion_a_medias_se_descarta_con_aviso()
    {
        var p = Datos("medias");
        Escribir(p.FichasDjPath, """{"a":1}""");
        var dir = CopiaSeguridad.CarpetaRestauracion(p);
        Directory.CreateDirectory(dir);
        Escribir(Path.Combine(dir, "fichas-dj.json"), "{}");   // sin restauracion.json: no terminó

        var r = CopiaSeguridad.AplicarRestauracionPendiente(p, Fecha)!;

        Assert.False(r.Ok);
        Assert.StartsWith("Había una restauración de copia de seguridad a medio preparar", r.Texto);
        Assert.False(Directory.Exists(dir));
        Assert.Equal("""{"a":1}""", File.ReadAllText(p.FichasDjPath));
        Assert.Null(CopiaSeguridad.AplicarRestauracionPendiente(p, Fecha));   // y no se repite
    }

    [Fact]
    public void Preparar_pone_al_dia_la_fecha_de_las_identificaciones_si_el_disco_la_redondea()
    {
        var win = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");
        Assert.True(CopiaSeguridad.Crear(win, zip, "manual", Fecha).Ok);
        var mac = EquipoMac();
        var c = CopiaSeguridad.Leer(zip, mac, out _)!;
        var mapa = new MapaRaices(new[] { new CambioRaiz(@"E:\Musica\COMERCIAL", "/Volumes/USB/COMERCIAL") });
        var redondeada = 638000000000000000 + TimeSpan.FromMilliseconds(1999).Ticks;

        var prep = CopiaSeguridad.PrepararRestauracion(c, mac, mapa, null, Fecha,
            sello: r => r == "/Volumes/USB/COMERCIAL/House/Tema.mp3" ? (redondeada, 4096) : null);
        Assert.Equal(1, prep.Resellados);
        CopiaSeguridad.AplicarRestauracionPendiente(mac, Fecha);

        Assert.Equal(redondeada, (long)Json(mac.IdentificacionCachePath)["/Volumes/USB/COMERCIAL/House/Tema.mp3"]!["M"]!);
    }

    [Fact]
    public void Cancelar_la_restauracion_no_toca_los_datos()
    {
        var win = EquipoWindows();
        var zip = Path.Combine(_dir, "copia.zip");
        Assert.True(CopiaSeguridad.Crear(win, zip, "manual", Fecha).Ok);
        var mac = EquipoMac();
        var antes = File.ReadAllText(mac.FichasDjPath);
        CopiaSeguridad.PrepararRestauracion(CopiaSeguridad.Leer(zip, mac, out _)!, mac, new MapaRaices(Array.Empty<CambioRaiz>()), null, Fecha);

        Assert.True(CopiaSeguridad.CancelarPendiente(mac));

        Assert.Null(CopiaSeguridad.AplicarRestauracionPendiente(mac, Fecha));
        Assert.Equal(antes, File.ReadAllText(mac.FichasDjPath));
        Assert.False(CopiaSeguridad.CancelarPendiente(mac));
    }

    // ------------------------------------------------------------------ textos

    [Fact]
    public void Los_textos_concuerdan_en_numero()
    {
        Assert.Equal("812 bytes", TextosCopia.Tamano(812));
        Assert.Equal("1,5 KB", TextosCopia.Tamano(1536));
        Assert.Equal("1,7 MB", TextosCopia.Tamano(1_719_340 + 60_000));

        Assert.StartsWith("Todavía no hay ninguna copia automática", TextosCopia.CopiasAutomaticas(Array.Empty<DateTime>()));
        Assert.Equal("Copias automáticas: 1 guardada, del 17/10/2026 15:56.", TextosCopia.CopiasAutomaticas(new[] { Fecha }));
        Assert.Equal("Copias automáticas: 2 guardadas; la última, del 18/10/2026 15:56.", TextosCopia.CopiasAutomaticas(new[] { Fecha, Fecha.AddDays(1) }));

        var una = new FilaRestauracion("fichas-dj.json", "Fichas de DJ", 10, Fecha, 20, Fecha, true);
        var nueva = new FilaRestauracion("aplicadas.json", "", 10, Fecha, null, null, true);
        var fuera = new FilaRestauracion("colecciones.json", "", 0, null, 5, Fecha, false);

        var (t1, c1, _) = TextosCopia.Confirmacion(new[] { una }, 0, 0);
        Assert.Equal("Restaurar 1 archivo de la copia de seguridad", t1);
        Assert.Equal("1 archivo de datos sustituye al actual.", c1);

        var (t2, c2, _) = TextosCopia.Confirmacion(new[] { una, una, nueva, fuera }, 1, 1);
        Assert.Equal("Restaurar 3 archivos de la copia de seguridad", t2);
        Assert.Equal("2 archivos de datos sustituyen a los actuales y se añade 1 que no existe ahora. "
                   + "Se conserva tal cual 1 archivo que no está en la copia. Cambia la ubicación de 1 carpeta de música: se reescribe 1 ruta.", c2);

        var (_, c3, _) = TextosCopia.Confirmacion(new[] { nueva, nueva }, 2, 15241);
        Assert.Equal("Se añaden 2 que no existen ahora. Cambia la ubicación de 2 carpetas de música: se reescriben 15.241 rutas.", c3);

        Assert.Equal("Restauración preparada (1 archivo). Se completará la próxima vez que se abra BeatTag.",
                     TextosCopia.Preparada(new ResultadoPreparacion(1, 0, Array.Empty<string>(), "")));
        Assert.Equal("Restauración preparada (14 archivos, 1 ruta reescrita). Se completará la próxima vez que se abra BeatTag.",
                     TextosCopia.Preparada(new ResultadoPreparacion(14, 1, Array.Empty<string>(), "")));

        Assert.Equal("1 ruta guardada", TextosCopia.Rutas(1));
        Assert.Equal("2.597 rutas guardadas", TextosCopia.Rutas(2597));

        var hecha = TextosCopia.CopiaHecha(new ResultadoCopia(@"D:\copias\BeatTag-copia.zip", 1, 2048, "", Array.Empty<string>()));
        Assert.StartsWith("Copia de seguridad guardada en «BeatTag-copia.zip»: 1 archivo, 2 KB.", hecha);
    }

    [Fact]
    public void La_descripcion_de_una_copia_dice_que_tipo_es_y_donde_se_hizo()
    {
        var m = new ManifiestoCopia { Fecha = Fecha, Sistema = "Windows", Tipo = "automatica", Version = "1.9.2" };
        Assert.Equal("Copia automática del 17/10/2026 15:56, hecha en Windows con BeatTag 1.9.2.", TextosCopia.Describir(m));
        Assert.StartsWith("Copia hecha antes de restaurar otra", TextosCopia.Describir(new ManifiestoCopia { Fecha = Fecha, Tipo = "antes-de-restaurar" }));
    }
}
