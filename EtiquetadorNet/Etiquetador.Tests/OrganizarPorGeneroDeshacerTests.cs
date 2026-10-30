using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Organizar por género con archivos de verdad, en una carpeta temporal: el motor de traslado (B7), el
/// manifiesto por lotes (B5), deshacer que recrea el origen y quita las carpetas creadas (B3), el tipo
/// del historial (B4), lo que sigue a cada archivo movido (B6) y la reparación de rekordbox (B10). Sin
/// procesos hijo: el vigilante de la CI corta a 400.
/// </summary>
public class OrganizarPorGeneroDeshacerTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;
    private readonly string _musica;
    private readonly string _origen;

    public OrganizarPorGeneroDeshacerTests()
    {
        _paths = new AppPaths(Path.Combine(_dir, "datos"));
        _musica = Path.Combine(_dir, "Musica");
        _origen = Path.Combine(_musica, "Descargas");
        Directory.CreateDirectory(_origen);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string PorGenero => Path.Combine(_musica, OrganizarPorGenero.CarpetaPorGenero);

    /// <summary><paramref name="n"/> archivos en la carpeta de origen (basta con unos bytes: solo se mueven).</summary>
    private List<string> Archivos(int n, string? carpeta = null)
    {
        var dir = carpeta ?? _origen;
        Directory.CreateDirectory(dir);
        var lista = new List<string>();
        for (var i = 1; i <= n; i++)
        {
            var ruta = Path.Combine(dir, $"tema{i:0000}.mp3");
            File.WriteAllBytes(ruta, new byte[] { 1, 2, 3, (byte)(i % 256) });
            lista.Add(ruta);
        }
        return lista;
    }

    /// <summary>El plan de llevar cada archivo a «Por género/<paramref name="genero"/>».</summary>
    private PlanOrganizar PlanA(IEnumerable<string> archivos, string genero = "Pop")
        => new()
        {
            Movimientos = archivos.Select(a => new MovimientoOrganizar(a, Path.Combine(PorGenero, genero, Path.GetFileName(a)), genero, genero)).ToList(),
        };

    private ResultadoOrganizar Organizar(PlanOrganizar plan, CancellationToken ct = default,
                                         Action<string, string, SelloArchivo>? seguir = null, Action<string, string>? mover = null)
    {
        using var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(), out var error);
        return OrganizadorPorGenero.Ejecutar(plan, m, error, seguir, null, null, ct, mover);
    }

    private string[] LineasDelManifiesto()
        => Directory.GetFiles(_paths.UndoDir, "run_*.jsonl").Where(UndoEngine.EsManifiestoPendiente)
                    .SelectMany(f => File.ReadAllLines(f)).Where(l => l.Trim().Length > 0).ToArray();

    // --- B7: el motor de traslado ---

    [Fact]
    public void Caso_14_trescientos_archivos_en_lotes_quedan_anotados_con_su_origen()
    {
        var archivos = Archivos(300);

        var res = Organizar(PlanA(archivos));

        Assert.Equal(300, res.Movidas);
        Assert.False(res.Cancelado);
        Assert.Empty(res.Fallos);
        Assert.All(archivos, a => Assert.True(File.Exists(Path.Combine(PorGenero, "Pop", Path.GetFileName(a)))));
        var lineas = LineasDelManifiesto();
        Assert.Equal(300, lineas.Length);
        var registros = lineas.Select(l => JsonSerializer.Deserialize<UndoRecord>(l)!).ToList();
        Assert.All(registros, r => Assert.Equal(nameof(TipoEjecucion.OrganizarPorGenero), r.Origen));
        Assert.All(registros, r => Assert.True(r.Renamed));
        // Las carpetas creadas las lleva el primero que las necesitó, de la más alta a la más profunda.
        var conCarpetas = Assert.Single(registros, r => r.CarpetasCreadas != null);
        Assert.Same(registros[0], conCarpetas);
        Assert.Equal(new[] { PorGenero, Path.Combine(PorGenero, "Pop") }, conCarpetas.CarpetasCreadas);

        var ejecucion = Assert.Single(new HistorialDeshacer(_paths.UndoDir).Listar());
        Assert.Equal(TipoEjecucion.OrganizarPorGenero, ejecucion.Tipo);
        Assert.Equal("Organización por género", ejecucion.TipoTexto);
    }

    [Fact]
    public void Caso_15_cancelar_a_mitad_deja_el_historial_exacto_y_se_deshace_entero()
    {
        var archivos = Archivos(300);
        using var cts = new CancellationTokenSource();
        var movidos = 0;

        var res = Organizar(PlanA(archivos), cts.Token, seguir: (_, _, _) => { if (++movidos == 150) cts.Cancel(); });

        Assert.True(res.Cancelado);
        Assert.Equal(150, res.Movidas);
        Assert.Equal(150, LineasDelManifiesto().Length);

        var deshecho = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal(150, deshecho.Reverted);
        Assert.Equal(0, deshecho.Errors + deshecho.Missing);
        Assert.All(archivos, a => Assert.True(File.Exists(a)));
        Assert.False(Directory.Exists(PorGenero));   // las carpetas que creó, fuera
    }

    [Fact]
    public void Caso_17_sin_historial_no_se_mueve_nada_y_se_dice()
    {
        var archivos = Archivos(3);

        var res = OrganizadorPorGenero.Ejecutar(PlanA(archivos), null, "Acceso denegado.", null, null, null, CancellationToken.None);

        Assert.Equal(0, res.Movidas);
        Assert.StartsWith("No se ha tocado ningún archivo", res.Detenido);
        Assert.Contains("Acceso denegado", res.Detenido);
        Assert.All(archivos, a => Assert.True(File.Exists(a)));
    }

    /// <summary>Un historial por lotes que deja de poder escribirse en el lote número <c>fallaEn</c>.</summary>
    private sealed class HistorialQueFalla : IHistorialPorLotes
    {
        private readonly int _fallaEn;
        private int _lotes;
        public readonly List<UndoRecord> Anotados = new();
        public HistorialQueFalla(int fallaEn) => _fallaEn = fallaEn;
        public long Posicion => Anotados.Count;
        public string Anotar(UndoRecord registro) => AnotarLote(new[] { registro });

        public string AnotarLote(IReadOnlyList<UndoRecord> registros)
        {
            if (++_lotes >= _fallaEn) return "No hay espacio suficiente en el disco.";
            Anotados.AddRange(registros);
            return "";
        }

        public string ReescribirDesde(long posicion, IReadOnlyList<UndoRecord> hechos)
        {
            Anotados.RemoveRange((int)posicion, Anotados.Count - (int)posicion);
            Anotados.AddRange(hechos);
            return "";
        }
    }

    [Fact]
    public void Caso_17_si_un_lote_no_se_puede_anotar_no_se_mueve_nada_de_el()
    {
        var archivos = Archivos(250);
        var historial = new HistorialQueFalla(fallaEn: 2);

        var res = OrganizadorPorGenero.Ejecutar(PlanA(archivos), historial, "", null, null, null, CancellationToken.None);

        Assert.Equal(OrganizadorPorGenero.TamanoLote, res.Movidas);   // solo el primer lote
        Assert.True(res.Cancelado);
        Assert.Contains("tema0101.mp3", res.Detenido);
        Assert.Equal(OrganizadorPorGenero.TamanoLote, historial.Anotados.Count);
        Assert.All(archivos.Skip(OrganizadorPorGenero.TamanoLote), a => Assert.True(File.Exists(a)));
    }

    [Fact]
    public void Un_archivo_abierto_falla_solo_el_y_sale_de_su_lote()
    {
        var archivos = Archivos(10);
        var abierto = archivos[4];

        var res = Organizar(PlanA(archivos), mover: (o, d) =>
        {
            if (o == abierto) throw new IOException("El proceso no tiene acceso al archivo.", unchecked((int)0x80070020));
            File.Move(o, d, overwrite: false);
        });

        Assert.Equal(9, res.Movidas);
        var fallo = Assert.Single(res.Fallos);
        Assert.Equal("tema0005.mp3: " + OrganizadorPorGenero.Abierto, fallo);
        Assert.True(File.Exists(abierto));
        Assert.Equal(9, LineasDelManifiesto().Length);
        Assert.DoesNotContain(LineasDelManifiesto(), l => l.Contains("tema0005", StringComparison.Ordinal));
    }

    [Fact]
    public void Si_falla_el_traslado_que_creo_la_carpeta_otro_la_hereda_o_se_quita_si_queda_vacia()
    {
        // El primero (el que reclama las carpetas) falla y el segundo entra: el segundo las hereda.
        var dos = Archivos(2);
        var res = Organizar(PlanA(dos), mover: (o, d) =>
        {
            if (o == dos[0]) throw new UnauthorizedAccessException("Acceso denegado.");
            File.Move(o, d);
        });
        Assert.Equal(1, res.Movidas);
        var registro = JsonSerializer.Deserialize<UndoRecord>(Assert.Single(LineasDelManifiesto()))!;
        Assert.Equal(new[] { PorGenero, Path.Combine(PorGenero, "Pop") }, registro.CarpetasCreadas);
        new UndoEngine(_paths).UndoLastRun();
        Assert.False(Directory.Exists(PorGenero));

        // Uno solo, que falla: las carpetas que se crearon para él no se quedan.
        var uno = Archivos(1, Path.Combine(_musica, "Otra"));
        var solo = Organizar(PlanA(uno, "Techno"), mover: (_, _) => throw new UnauthorizedAccessException("Acceso denegado."));
        Assert.Equal(0, solo.Movidas);
        Assert.False(Directory.Exists(PorGenero));
    }

    [Fact]
    public void Un_destino_ocupado_justo_antes_de_moverlo_no_se_sobrescribe()
    {
        var archivos = Archivos(2);
        var ocupado = Path.Combine(PorGenero, "Pop", Path.GetFileName(archivos[1]));
        Directory.CreateDirectory(Path.GetDirectoryName(ocupado)!);
        File.WriteAllText(ocupado, "otro");

        var res = Organizar(PlanA(archivos));

        Assert.Equal(1, res.Movidas);
        Assert.Contains("no se sobrescribe", Assert.Single(res.Fallos));
        Assert.Equal("otro", File.ReadAllText(ocupado));
        Assert.True(File.Exists(archivos[1]));
    }

    [Fact]
    public void Prueba_de_humo_dos_mil_traslados()
    {
        var archivos = Archivos(2000);
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var res = Organizar(PlanA(archivos));

        reloj.Stop();
        Assert.Equal(2000, res.Movidas);
        // MEDIDO en un NVMe: menos de 5 s. En la CI los discos y las máquinas varían: solo se frena un
        // atasco de verdad.
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(60), $"2.000 traslados tardaron {reloj.Elapsed.TotalSeconds:0.0} s");
    }

    // --- B3: deshacer recrea el origen y quita lo que creó ---

    [Fact]
    public void Caso_18_con_la_carpeta_de_origen_borrada_deshacer_devuelve_todo()
    {
        var archivos = Archivos(300);
        Assert.Equal(300, Organizar(PlanA(archivos)).Movidas);
        Directory.Delete(_origen);   // vacía tras organizar: el usuario la borra

        var deshecho = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(300, deshecho.Reverted);
        Assert.Equal(0, deshecho.Errors);
        Assert.All(archivos, a => Assert.True(File.Exists(a)));
        Assert.False(Directory.Exists(PorGenero));
    }

    [Fact]
    public void Una_carpeta_creada_con_algo_ajeno_dentro_se_conserva()
    {
        var archivos = Archivos(3);
        Organizar(PlanA(archivos));
        var ajeno = Path.Combine(PorGenero, "Pop", "notas.txt");
        File.WriteAllText(ajeno, "mío");

        new UndoEngine(_paths).UndoLastRun();

        Assert.All(archivos, a => Assert.True(File.Exists(a)));
        Assert.True(File.Exists(ajeno));
        Assert.Equal("mío", File.ReadAllText(ajeno));
    }

    // Revisión 1: solo el registro que creó una carpeta la quitaba, y solo en su propio deshacer.
    // REPRODUCIDO: deshacer primero la línea que lleva las carpetas y después el resto dejaba «Por
    // género» y «Pop» vacías para siempre.
    [Fact]
    public void Deshacer_por_partes_quita_al_final_las_carpetas_que_creo_la_organizacion()
    {
        var archivos = Archivos(3);
        Assert.Equal(3, Organizar(PlanA(archivos)).Movidas);
        var mf = Assert.Single(Directory.GetFiles(_paths.UndoDir, "run_*.jsonl"));
        var primera = File.ReadAllLines(mf)[0];
        Assert.Contains("CarpetasCreadas", primera);
        var motor = new UndoEngine(_paths);

        Assert.Equal(1, motor.DeshacerCambios(mf, new[] { UndoEngine.HuellaLinea(primera) }).Reverted);
        Assert.True(Directory.Exists(Path.Combine(PorGenero, "Pop")));   // quedan dos dentro
        Assert.Equal(1, motor.DeshacerCambios(mf, new[] { UndoEngine.HuellaLinea(File.ReadAllLines(mf)[0]) }).Reverted);
        Assert.Equal(1, motor.UndoLastRun().Reverted);

        Assert.All(archivos, a => Assert.True(File.Exists(a)));
        Assert.False(Directory.Exists(PorGenero));
    }

    [Fact]
    public void Si_falta_el_archivo_que_creo_las_carpetas_se_quitan_igual_al_quedarse_vacias()
    {
        var archivos = Archivos(2);
        Organizar(PlanA(archivos));
        File.Delete(Path.Combine(PorGenero, "Pop", Path.GetFileName(archivos[0])));   // el usuario lo borra

        var deshecho = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal((1, 1), (deshecho.Reverted, deshecho.Missing));
        Assert.True(File.Exists(archivos[1]));
        Assert.False(Directory.Exists(PorGenero));
    }

    [Fact]
    public void Un_traslado_anotado_sin_carpetas_creadas_se_deshace_como_antes()
    {
        var a = Archivos(1)[0];
        var destino = Path.Combine(_musica, "Pop", "tema0001.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.Move(a, destino);
        using (var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(), out _))
            Assert.Equal("", m!.Anotar(new UndoRecord { OrigPath = a, FinalPath = destino, Renamed = true, Origen = nameof(TipoEjecucion.Traslado) }));
        Assert.DoesNotContain("CarpetasCreadas", Assert.Single(LineasDelManifiesto()));

        var deshecho = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(1, deshecho.Reverted);
        Assert.True(File.Exists(a));
        Assert.True(Directory.Exists(Path.Combine(_musica, "Pop")));   // no la creó esa ejecución: se queda
    }

    // --- B5: el manifiesto por lotes ---

    [Fact]
    public void Caso_16_un_lote_anotado_del_que_solo_se_movio_una_parte_se_deshace_sin_errores()
    {
        var archivos = Archivos(100);
        var plan = PlanA(archivos);
        using (var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(), out _))
        {
            var registros = plan.Movimientos.Select(x => new UndoRecord
            {
                OrigPath = x.Origen, FinalPath = x.Destino, Renamed = true, Origen = nameof(TipoEjecucion.OrganizarPorGenero),
            }).ToList();
            Assert.Equal("", m!.AnotarLote(registros));
            Assert.Equal(100, m.Anotados);
            // Se mueven 40 y la aplicación se cierra de golpe: sin reescribir el lote.
            Directory.CreateDirectory(Path.Combine(PorGenero, "Pop"));
            foreach (var x in plan.Movimientos.Take(40)) File.Move(x.Origen, x.Destino);
        }
        Assert.Equal(100, LineasDelManifiesto().Length);

        var deshecho = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(0, deshecho.Errors + deshecho.Missing);
        Assert.All(archivos, a => Assert.True(File.Exists(a)));
    }

    [Fact]
    public void Reescribir_un_lote_sin_nada_hecho_deja_el_manifiesto_vacio_y_se_borra_al_cerrar()
    {
        var ruta = _paths.NuevoManifiesto();
        using (var m = ManifiestoDeshacer.Abrir(ruta, out _))
        {
            var inicio = m!.Posicion;
            Assert.Equal("", m.AnotarLote(new[] { new UndoRecord { OrigPath = "a", FinalPath = "b", Renamed = true } }));
            Assert.Equal("", m.ReescribirDesde(inicio, Array.Empty<UndoRecord>()));
            Assert.Equal(0, m.Anotados);
        }
        Assert.False(File.Exists(ruta));
    }

    [Fact]
    public void Reescribir_el_segundo_lote_conserva_el_primero()
    {
        var ruta = _paths.NuevoManifiesto();
        UndoRecord R(string n) => new() { OrigPath = n, FinalPath = n + "-b", Renamed = true };
        using (var m = ManifiestoDeshacer.Abrir(ruta, out _))
        {
            m!.AnotarLote(new[] { R("1"), R("2") });
            var segundo = m.Posicion;
            m.AnotarLote(new[] { R("3"), R("4"), R("5") });
            Assert.Equal("", m.ReescribirDesde(segundo, new[] { R("4") }));
            Assert.Equal(3, m.Anotados);
        }
        Assert.Equal(new[] { "1", "2", "4" }, File.ReadAllLines(ruta).Select(l => JsonSerializer.Deserialize<UndoRecord>(l)!.OrigPath));
    }

    /// <summary>Lo que hay ahora en el disco del manifiesto (abierto por otro), línea a línea: el OrigPath de cada una que se lee entera.</summary>
    private static List<string> AnotadosEnElDisco(string ruta)
    {
        using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var res = new List<string>();
        foreach (var linea in sr.ReadToEnd().Split('\n'))
        {
            if (linea.Trim().Length == 0) continue;
            try { res.Add(JsonSerializer.Deserialize<UndoRecord>(linea)!.OrigPath); }
            catch (JsonException) { /* una línea a medias: deshacer la aparta sin tocar nada */ }
        }
        return res;
    }

    // Revisión 1: ReescribirDesde recortaba ANTES de escribir. Entre el recorte y el volcado, lo ya
    // movido del lote solo estaba en memoria: un cierre ahí lo dejaba movido y sin anotar.
    [Theory]
    [InlineData(false)]   // un traslado falla a mitad del lote: hay que quitar su línea
    [InlineData(true)]    // y además otro hereda las carpetas creadas: su línea crece
    public void Reescribir_un_lote_nunca_deja_lo_hecho_sin_anotar_en_el_disco(bool heredaCarpetas)
    {
        var ruta = _paths.NuevoManifiesto();
        UndoRecord R(string n) => new() { OrigPath = n, FinalPath = n + "-b", Renamed = true };
        using var m = ManifiestoDeshacer.Abrir(ruta, out _)!;
        m.AnotarLote(new[] { R("1"), R("2") });
        var lote = m.Posicion;
        var registros = new[] { R("3"), R("4"), R("5"), R("6") };
        registros[0].CarpetasCreadas = new() { Path.Combine("m", "Por género"), Path.Combine("m", "Por género", "Pop") };
        Assert.Equal("", m.AnotarLote(registros));
        var hechos = new[] { R("4"), R("6") };
        if (heredaCarpetas) hechos[0].CarpetasCreadas = registros[0].CarpetasCreadas;

        var pasos = new List<string>();
        m.TrasCadaPaso = paso =>
        {
            pasos.Add(paso);
            var enDisco = AnotadosEnElDisco(ruta);
            foreach (var h in new[] { "1", "2", "4", "6" })
                Assert.True(enDisco.Contains(h), $"tras «{paso}», «{h}» está movido y no está anotado en el disco");
        };

        Assert.Equal("", m.ReescribirDesde(lote, hechos));

        Assert.NotEmpty(pasos);
        Assert.Equal(new[] { "1", "2", "4", "6" }, AnotadosEnElDisco(ruta));
        Assert.Equal(4, m.Anotados);
    }

    [Fact]
    public void Si_reescribir_falla_en_cualquier_paso_lo_hecho_sigue_anotado_y_se_dice()
    {
        UndoRecord R(string n) => new() { OrigPath = n, FinalPath = n + "-b", Renamed = true };
        for (var fallaEn = 1; ; fallaEn++)
        {
            var ruta = _paths.NuevoManifiesto();
            using var m = ManifiestoDeshacer.Abrir(ruta, out _)!;
            var lote = m.Posicion;
            m.AnotarLote(new[] { R("1"), R("2"), R("3") });
            var paso = 0;
            m.TrasCadaPaso = _ => { if (++paso == fallaEn) throw new IOException("No hay espacio suficiente en el disco."); };

            var error = m.ReescribirDesde(lote, new[] { R("1"), R("3") });

            if (paso < fallaEn) { Assert.Equal("", error); break; }   // ya no quedan pasos que hacer fallar
            Assert.Contains("espacio", error);
            var enDisco = AnotadosEnElDisco(ruta);
            Assert.Contains("1", enDisco);
            Assert.Contains("3", enDisco);
            m.Dispose();
            File.Delete(ruta);
        }
    }

    // --- B4: el tipo en el historial ---

    [Fact]
    public void El_historial_reconoce_la_organizacion_por_su_origen_y_sin_el_la_da_por_traslado()
    {
        static JsonNode? Linea(string? origen) => JsonNode.Parse(JsonSerializer.Serialize(new UndoRecord
        {
            OrigPath = Path.Combine("m", "A", "x.mp3"), FinalPath = Path.Combine("m", "Por género", "Pop", "x.mp3"), Renamed = true, Origen = origen,
        }));

        Assert.Equal(TipoEjecucion.OrganizarPorGenero, HistorialDeshacer.DeducirTipo(new[] { Linea(nameof(TipoEjecucion.OrganizarPorGenero)) }));
        // Una versión que no conoce el tipo (u otro que se añada después) la deduce por los campos.
        Assert.Equal(TipoEjecucion.Traslado, HistorialDeshacer.DeducirTipo(new[] { Linea("UnaHerramientaFutura") }));
        Assert.Equal("Organización por género", HistorialDeshacer.Texto(TipoEjecucion.OrganizarPorGenero));
        Assert.Equal(nameof(TipoEjecucion.OrganizarPorGenero), Enum.GetNames<TipoEjecucion>()[^1]);   // al final: se guarda el nombre
    }

    // --- B6: lo que sigue al archivo ---

    [Fact]
    public void La_cache_de_escaneo_sigue_al_archivo_con_su_fecha_y_su_tamano()
    {
        var a = Path.Combine(_origen, "a.mp3");
        Mp3Fixture.WriteMinMp3(a);
        var archivoCache = Path.Combine(_dir, "scan.json");
        var cache = new ScanCache(archivoCache);
        cache.Read(a);
        var b = Path.Combine(_musica, "Pop", "a.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Move(a, b);

        Assert.True(cache.Mover(a, b));
        Assert.False(cache.Mover(a, b));
        Assert.Equal("", cache.Save());

        var leida = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry>>(File.ReadAllText(archivoCache))!;
        Assert.False(leida.ContainsKey(a));
        var fi = new FileInfo(b);
        Assert.Equal((fi.LastWriteTimeUtc.Ticks, fi.Length), (leida[b].M, leida[b].S));
    }

    [Fact]
    public void Aplicadas_descartadas_y_casillas_siguen_al_archivo()
    {
        var a = Path.Combine(_origen, "Tema.mp3");
        var b = Path.Combine(PorGenero, "Pop", "Tema.mp3");
        var lista = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        lista.Add(a);
        Assert.True(lista.Mover(a, b));
        Assert.True(lista.Contains(b));
        Assert.False(lista.Contains(a));
        Assert.False(lista.Mover(a, b));
        Assert.Equal("", lista.Save());
        Assert.True(new IgnoreList(Path.Combine(_dir, "aplicadas.json")).Contains(b));

        var marcas = new ApplyMarks(Path.Combine(_dir, "marcas.json"));
        marcas.Set(a, true);
        marcas.Set(EnrichViewModel.ClaveMarca(a, "huella"), false);
        marcas.Set(a + ".bak", true);   // otro archivo cuyo nombre empieza igual: no es suyo
        Assert.Equal(2, marcas.Mover(a, b));
        Assert.True(marcas.Get(b));
        Assert.False(marcas.Get(EnrichViewModel.ClaveMarca(b, "huella")));
        Assert.Null(marcas.Get(a));
        Assert.True(marcas.Get(a + ".bak"));
    }

    /// <summary>El motor de prueba con lo que usa Organizar por género además: la biblioteca, las fichas y deshacer.</summary>
    private AppEngine Motor(LibraryStore biblioteca)
    {
        var motor = MotorDePrueba.Crear(_paths.DataDir, new Logger(), biblioteca);
        void Poner(string campo, object valor) => typeof(AppEngine).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(motor, valor);
        Poner("<Fichas>k__BackingField", new AlmacenFichas(_paths.FichasDjPath));
        var seguirDeshecho = typeof(AppEngine).GetMethod("SeguirDeshecho", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(ArchivoDeshecho) })!;
        Poner("<Undo>k__BackingField", new UndoEngine(_paths) { AlDeshacer = a => seguirDeshecho.Invoke(motor, new object[] { a }) });
        return motor;
    }

    [Fact]
    public async Task Caso_19_tras_organizar_y_tras_deshacer_todo_sigue_al_archivo()
    {
        var a = Path.Combine(_origen, "Tema.mp3");
        Mp3Fixture.WriteMinMp3(a);
        var b = Path.Combine(PorGenero, "Pop", "Tema.mp3");
        var archivoCache = Path.Combine(_dir, "scan.json");
        var biblioteca = new LibraryStore(new AppConfig { Folders = new() { _musica } }, () => { }, new ScanCache(archivoCache));
        await biblioteca.ScanAsync();
        var motor = Motor(biblioteca);
        var huella = EnrichViewModel.ClaveMarca(a, "h1");
        motor.Applied.Add(a);
        motor.Ignored.Add(a);
        motor.Marks.Set(a, true);
        motor.Marks.Set(huella, false);
        motor.Fichas.Poner(a, new FichaDj { Energia = 7 });
        motor.Config.VolumenCancionesExcluidas.Add(a);

        void Comprobar(string aqui, string alli)
        {
            Assert.True(motor.Applied.Contains(aqui));
            Assert.False(motor.Applied.Contains(alli));
            Assert.True(motor.Ignored.Contains(aqui));
            Assert.True(motor.Marks.Get(aqui));
            Assert.False(motor.Marks.Get(EnrichViewModel.ClaveMarca(aqui, "h1")));
            Assert.Null(motor.Marks.Get(alli));
            Assert.Equal(7, motor.Fichas.Obtener(aqui)!.Energia);
            Assert.Null(motor.Fichas.Obtener(alli));
            Assert.Contains(aqui, motor.Config.VolumenCancionesExcluidas);
            Assert.DoesNotContain(alli, motor.Config.VolumenCancionesExcluidas);
            var guardada = JsonSerializer.Deserialize<Dictionary<string, ScanCache.Entry>>(File.ReadAllText(archivoCache))!;
            var fi = new FileInfo(aqui);
            Assert.Equal((fi.LastWriteTimeUtc.Ticks, fi.Length), (guardada[aqui].M, guardada[aqui].S));
            Assert.False(guardada.ContainsKey(alli));
            // Guardado en disco, no solo en memoria.
            Assert.True(new IgnoreList(_paths.AppliedPath).Contains(aqui));
            Assert.True(new ApplyMarks(_paths.ApplyMarksPath).Get(aqui));
            Assert.Equal(7, new AlmacenFichas(_paths.FichasDjPath).Obtener(aqui)!.Energia);
        }

        var res = motor.OrganizarPorGenero(PlanA(new[] { a }), null, CancellationToken.None);
        Assert.Equal(1, res.Movidas);
        Comprobar(b, a);

        var deshecho = motor.DeshacerUltima();
        Assert.Equal(1, deshecho.Reverted);
        Assert.True(File.Exists(a));
        Comprobar(a, b);
    }

    private static void EscribirCache(string archivo, string ruta, object entrada)
        => File.WriteAllText(archivo, JsonSerializer.Serialize(new Dictionary<string, object> { [ruta] = entrada }));

    [Fact]
    public void Caso_20_un_traslado_que_cambia_la_fecha_conserva_la_sonoridad_por_el_sello()
    {
        var a = Path.Combine(_origen, "Tema.mp3");
        Mp3Fixture.WriteMinMp3(a);
        var antes = SelloArchivo.De(a);
        Directory.CreateDirectory(_paths.DataDir);
        EscribirCache(_paths.LoudnessCachePath, a, new { M = antes.M, S = antes.S, Lufs = -9.5, Peak = -0.3 });
        var biblioteca = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(_dir, "scan.json")));
        var motor = Motor(biblioteca);
        Assert.NotNull(motor.Loudness.Get(a));

        var b = Path.Combine(PorGenero, "Pop", "Tema.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Move(a, b);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(5));   // un sistema de archivos que la cambia al mover
        motor.SeguirTraslado(a, b, antes);

        Assert.Null(motor.Loudness.Get(a));
        Assert.Equal(-9.5, motor.Loudness.Get(b)!.Value.Lufs, 3);
    }

    [Fact]
    public void Deshacer_un_cambio_de_carpeta_sigue_el_traslado_y_un_renombrado_no()
    {
        var llamadas = new List<string>();
        void Seguir(string de, string a, SelloArchivo s) => llamadas.Add("seguir");
        void Trasladar(string de, string a, SelloArchivo s) => llamadas.Add("traslado");
        void Volumen(string r, SelloArchivo s, double db, double anotados) => llamadas.Add("volumen");

        var otraCarpeta = new ArchivoDeshecho(Path.Combine(PorGenero, "Pop", "x.mp3"), Path.Combine(_origen, "x.mp3"), false, false, 0, 0, false);
        AppEngine.SeguirDeshecho(otraCarpeta, Seguir, Volumen, Trasladar);
        Assert.Equal(new[] { "traslado" }, llamadas);

        llamadas.Clear();
        var mismaCarpeta = new ArchivoDeshecho(Path.Combine(_origen, "Artista - Tema.mp3"), Path.Combine(_origen, "tema.mp3"), true, false, 1, 2, true);
        AppEngine.SeguirDeshecho(mismaCarpeta, Seguir, Volumen, Trasladar);
        Assert.Equal(new[] { "seguir" }, llamadas);
    }

    // --- B7 y B10: rekordbox repara lo organizado ---

    private sealed class DiscoCruce : IArchivosCruce
    {
        public bool Existe(string ruta) => File.Exists(ruta);
        public bool DiscoDisponible(string ruta) => true;
        public long Tamano(string ruta) => -1;
    }

    [Fact]
    public void Caso_21_rekordbox_repara_lo_organizado_como_solo_movido()
    {
        var a = Archivos(1)[0];
        var b = Path.Combine(PorGenero, "Pop", Path.GetFileName(a));
        Assert.Equal(1, Organizar(PlanA(new[] { a })).Movidas);

        var renombrados = RekordboxRelocator.ReadRenames(_paths.UndoDir);
        Assert.Equal(b, renombrados[a]);

        var coleccion = new ColeccionRekordbox { Pistas = new[] { new PistaRekordbox { TrackId = "1", Ruta = a, Nombre = "Tema" } } };
        var cruce = CruceRekordbox.Cruzar(coleccion, new[] { new Track { FilePath = b, Folder = _musica } }, renombrados, new DiscoCruce());

        var rota = Assert.Single(cruce.Rotas);
        Assert.Equal(ComoReparar.RenombradaPorBeatTag, rota.Como);
        Assert.Equal(b, rota.NuevaRuta);
        Assert.True(rota.MismoNombre);
    }

    [Fact]
    public void Un_registro_cuyo_traslado_no_llego_a_hacerse_no_repara_nada()
    {
        var a = Archivos(1)[0];
        var b = Path.Combine(PorGenero, "Pop", Path.GetFileName(a));
        using (var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(), out _))
            m!.AnotarLote(new[] { new UndoRecord { OrigPath = a, FinalPath = b, Renamed = true, Origen = nameof(TipoEjecucion.OrganizarPorGenero) } });

        var coleccion = new ColeccionRekordbox { Pistas = new[] { new PistaRekordbox { TrackId = "1", Ruta = a, Nombre = "Tema" } } };
        var cruce = CruceRekordbox.Cruzar(coleccion, new[] { new Track { FilePath = a, Folder = _musica } },
                                          RekordboxRelocator.ReadRenames(_paths.UndoDir), new DiscoCruce());

        Assert.Empty(cruce.Rotas);
        Assert.Empty(CruceRekordbox.MapaReparacion(cruce.Rotas));
    }
}
