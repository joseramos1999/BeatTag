using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Lo que hace el motor de la aplicación al escribir en los archivos del usuario: que no toque nada
/// que no pueda deshacer (M10), que escriba bien las listas de géneros (I11), que las cachés de
/// audio sigan al archivo (A2, X4), que un «no encontrada» por fallos de red no se quede guardado
/// (C8), que recolocar fichas no relea los manifiestos sin motivo (I8) y cada cuánto guarda lo
/// hecho un bucle largo (M9).
/// </summary>
public class MotorHistorialYCachesTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Mp3(string nombre)
    {
        var ruta = Path.Combine(_dir, nombre);
        Mp3Fixture.WriteMinMp3(ruta);
        return ruta;
    }

    private static string[] Generos(string ruta)
    {
        using var f = TagLib.File.Create(ruta);
        return f.Tag.Genres ?? Array.Empty<string>();
    }

    private static void PonerGenero(string ruta, params string[] generos)
    {
        using var f = TagLib.File.Create(ruta);
        f.Tag.Genres = generos;
        f.Save();
    }

    /// <summary>Un historial que deja de poder escribirse en la anotación número <c>fallaEn</c> (disco lleno, OneDrive…).</summary>
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

    // --- M10: sin historial, no se toca nada más ---

    // Unificar el género de un lote y que el historial deje de poder escribirse a mitad: antes el
    // bucle seguía y el resto quedaba cambiado sin forma de deshacerlo.
    [Fact]
    public void Generos_se_paran_en_cuanto_el_historial_falla_y_no_tocan_el_resto()
    {
        var a = Mp3("a.mp3"); var b = Mp3("b.mp3"); var c = Mp3("c.mp3");
        foreach (var r in new[] { a, b, c }) PonerGenero(r, "Reggaeton");
        var historial = new HistorialQueFalla(fallaEn: 2);

        var res = AppEngine.EscribirGeneros(new[] { (a, "Reguetón"), (b, "Reguetón"), (c, "Reguetón") },
                                            historial, "", null, null, null, CancellationToken.None);

        Assert.Equal(1, res.Escritas);
        Assert.True(res.Cancelado);
        Assert.Contains("«b.mp3»", res.Detenido);
        Assert.Equal("", res.UndoErr);                        // lo escrito sí quedó anotado
        Assert.Equal(new[] { "Reguetón" }, Generos(a));
        Assert.Equal(new[] { "Reggaeton" }, Generos(b));      // ni el que falló…
        Assert.Equal(new[] { "Reggaeton" }, Generos(c));      // …ni los siguientes
        Assert.Single(historial.Anotados);
    }

    [Fact]
    public void Si_el_historial_no_se_puede_crear_no_se_toca_ningun_archivo()
    {
        var a = Mp3("a.mp3");
        PonerGenero(a, "Reggaeton");

        var res = AppEngine.EscribirGeneros(new[] { (a, "Reguetón") }, null, "Acceso denegado.", null, null, null, CancellationToken.None);

        Assert.Equal(0, res.Escritas);
        Assert.StartsWith("No se ha tocado ningún archivo", res.Detenido);
        Assert.Contains("Acceso denegado", res.Detenido);
        Assert.Equal(new[] { "Reggaeton" }, Generos(a));
    }

    // En un renombrado el destino solo se sabe después de mover el archivo: si no se puede anotar,
    // se le devuelve su nombre.
    [Fact]
    public void Un_renombrado_que_no_se_puede_anotar_vuelve_a_su_nombre()
    {
        var a = Mp3("bad bunny - titi me pregunto.mp3");
        var b = Mp3("karol g provenza.mp3");
        var historial = new HistorialQueFalla(fallaEn: 2);
        var movidos = new List<(string, string)>();

        var res = AppEngine.Renombrar(new[] { (a, "Bad Bunny - Tití Me Preguntó"), (b, "Karol G - Provenza") },
                                      historial, "", (de, para) => movidos.Add((de, para)), null, null, CancellationToken.None);

        Assert.Single(res.Hechos);
        Assert.True(File.Exists(Path.Combine(_dir, "Bad Bunny - Tití Me Preguntó.mp3")));
        Assert.True(File.Exists(b));                                   // devuelto a su nombre
        Assert.False(File.Exists(Path.Combine(_dir, "Karol G - Provenza.mp3")));
        Assert.Contains("karol g provenza.mp3", res.Detenido);
        Assert.Equal("", res.UndoErr);
        Assert.Single(movidos);                                        // las fichas y cachés solo siguen al que se quedó
    }

    [Fact]
    public void La_ficha_al_comentario_tampoco_escribe_sin_historial()
    {
        var a = Mp3("a.mp3");
        var historial = new HistorialQueFalla(fallaEn: 1);

        var res = AppEngine.VolcarComentarios(new[] { a }, _ => new FichaDj { Energia = 8 }, historial, "", null, null);

        Assert.Equal(0, res.Escritas);
        Assert.NotEqual("", res.Detenido);
        using var f = TagLib.File.Create(a);
        Assert.True(string.IsNullOrEmpty(f.Tag.Comment));
    }

    [Fact]
    public void El_manifiesto_deja_cada_linea_entera_en_disco()
    {
        var ruta = Path.Combine(_dir, "deshacer", "run_20261019_120000.jsonl");
        using (var m = ManifiestoDeshacer.Abrir(ruta, out var error))
        {
            Assert.NotNull(m);
            Assert.Equal("", error);
            Assert.Equal("", m!.Anotar(new UndoRecord { OrigPath = "a.mp3", FinalPath = "b.mp3", Renamed = true }));
            Assert.Equal("", m.Anotar(new UndoRecord { OrigPath = "c.mp3", FinalPath = "c.mp3" }));
        }
        var lineas = File.ReadAllLines(ruta);
        Assert.Equal(2, lineas.Length);
        Assert.Equal("b.mp3", JsonSerializer.Deserialize<UndoRecord>(lineas[0])!.FinalPath);
    }

    // Un manifiesto vacío pasaría por «la última ejecución» y taparía a la que de verdad lo es.
    [Fact]
    public void Un_manifiesto_sin_nada_anotado_no_se_queda_en_la_carpeta()
    {
        var ruta = Path.Combine(_dir, "run_20261019_120001.jsonl");
        using (var m = ManifiestoDeshacer.Abrir(ruta, out _)) Assert.NotNull(m);
        Assert.False(File.Exists(ruta));
    }

    [Fact]
    public void Si_el_manifiesto_no_se_puede_abrir_se_dice_por_que()
    {
        var ocupado = Path.Combine(_dir, "soy-un-archivo");
        File.WriteAllText(ocupado, "x");   // la «carpeta» de deshacer es un archivo: no se puede crear dentro

        var m = ManifiestoDeshacer.Abrir(Path.Combine(ocupado, "run.jsonl"), out var error);

        Assert.Null(m);
        Assert.NotEqual("", error);
    }

    // --- I11: una lista de géneros se escribe como varios géneros ---

    [Theory]
    [InlineData("Hip Hop; Latino", new[] { "Hip Hop", "Latino" })]
    [InlineData(" Hip Hop ;; Latino ", new[] { "Hip Hop", "Latino" })]
    [InlineData("Reggaeton", new[] { "Reggaeton" })]
    [InlineData("", new string[0])]
    public void Una_lista_de_generos_son_varios_valores(string propuesto, string[] esperado)
        => Assert.Equal(esperado, AppEngine.GenerosAEscribir(propuesto));

    [Fact]
    public void Hip_hop_y_latino_quedan_como_dos_generos_en_el_archivo()
    {
        var a = Mp3("a.mp3");
        PonerGenero(a, "Hip-Hop", "Latin");

        var res = AppEngine.EscribirGeneros(new[] { (a, "Hip Hop; Latino") }, new HistorialQueFalla(), "", null, null, null, CancellationToken.None);
        Assert.Equal(1, res.Escritas);
        Assert.Equal(new[] { "Hip Hop", "Latino" }, Generos(a));

        // Y volver a aplicarlo ya no cambia nada: antes «Hip Hop; Latino» en un solo valor no
        // coincidía nunca con los dos que tenía el archivo.
        var otra = AppEngine.EscribirGeneros(new[] { (a, "Hip Hop; Latino") }, new HistorialQueFalla(), "", null, null, null, CancellationToken.None);
        Assert.Equal(1, otra.SinCambio);
    }

    // --- A2 / X4: las cachés de audio siguen al archivo ---

    [Fact]
    public void Tras_escribir_un_genero_se_avisa_con_el_sello_de_antes()
    {
        var a = Mp3("a.mp3");
        var antes = SelloArchivo.De(a);
        var avisos = new List<(string De, string A, SelloArchivo Sello)>();

        AppEngine.EscribirGeneros(new[] { (a, "Reguetón") }, new HistorialQueFalla(), "", (de, para, s) => avisos.Add((de, para, s)),
                                  null, null, CancellationToken.None);

        var aviso = Assert.Single(avisos);
        Assert.Equal(a, aviso.De);
        Assert.Equal(a, aviso.A);
        Assert.Equal(antes, aviso.Sello);
        Assert.NotEqual(antes, SelloArchivo.De(a));   // el archivo sí cambió
    }

    private static void EscribirCache(string archivo, string ruta, object entrada)
        => File.WriteAllText(archivo, JsonSerializer.Serialize(new Dictionary<string, object> { [ruta] = entrada }));

    [Fact]
    public void La_huella_sigue_al_renombrado_y_a_las_etiquetas()
    {
        var a = Mp3("a.mp3");
        var s = SelloArchivo.De(a);
        var cache = Path.Combine(_dir, "huellas.json");
        EscribirCache(cache, a, new { M = s.M, S = s.S, D = 180.0, F = new[] { 1, 2, 3 } });
        var huellas = new FingerprintScanner(cache, Path.Combine(_dir, "fpcalc-inexistente"));
        Assert.NotNull(huellas.Get(a));

        var b = Path.Combine(_dir, "Artista - Tema.mp3");
        File.Move(a, b);
        Assert.True(huellas.Mover(a, b));
        Assert.Null(huellas.Get(a));
        Assert.Equal(new[] { 1, 2, 3 }, huellas.Get(b));

        var antes = SelloArchivo.De(b);
        Mp3Fixture.SetTags(b, "Tema", "Artista", null);   // solo etiquetas: el audio es el mismo
        Assert.Null(huellas.Get(b));                        // sin seguirlo, caducaba
        Assert.True(huellas.Retocar(b, antes.M, antes.S));
        Assert.Equal(new[] { 1, 2, 3 }, huellas.Get(b));
    }

    // Si la entrada ya no valía antes de que la aplicación escribiera (el audio cambió por otro
    // lado), no se resucita.
    [Fact]
    public void Una_huella_que_ya_no_valia_no_se_resucita()
    {
        var a = Mp3("a.mp3");
        var cache = Path.Combine(_dir, "huellas.json");
        EscribirCache(cache, a, new { M = 1L, S = 2L, D = 180.0, F = new[] { 1, 2, 3 } });   // de otro audio
        var huellas = new FingerprintScanner(cache, Path.Combine(_dir, "fpcalc-inexistente"));

        var antes = SelloArchivo.De(a);
        Mp3Fixture.SetTags(a, "Tema", null, null);

        Assert.False(huellas.Retocar(a, antes.M, antes.S));
        Assert.Null(huellas.Get(a));
    }

    // La respuesta de «Comprobar audio» puede haberse pagado en AudD: unificar géneros o renombrar
    // no puede obligar a pagarla otra vez.
    [Fact]
    public void La_identificacion_sigue_al_renombrado_y_a_las_etiquetas()
    {
        var a = Mp3("a.mp3");
        var s = SelloArchivo.De(a);
        var cache = Path.Combine(_dir, "identificacion.json");
        EscribirCache(cache, a, new { M = s.M, S = s.S, A = "Bad Bunny", T = "Tití Me Preguntó", L = "", F = "AudD", Sin = false, P = true, O = 0.3 });
        var paths = new AppPaths(_dir);
        var http = new HttpClient();
        var id = new IdentificationScanner(cache, new Fingerprint(paths), new Etiquetador.Core.Providers.AcoustIdProvider(new ApiClient(paths)),
                                           new Etiquetador.Core.Providers.AuddProvider(http), http);
        Assert.True(id.Consultado(a, usarPago: true));

        var b = Path.Combine(_dir, "Bad Bunny - Tití Me Preguntó.mp3");
        File.Move(a, b);
        Assert.True(id.Mover(a, b));
        var antes = SelloArchivo.De(b);
        Mp3Fixture.SetTags(b, "Tití Me Preguntó", "Bad Bunny", null);
        Assert.False(id.Consultado(b, usarPago: true));     // caducada por la fecha
        Assert.True(id.Retocar(b, antes.M, antes.S));

        Assert.True(id.Consultado(b, usarPago: true));      // no se vuelve a preguntar (ni a pagar)
        Assert.Equal("Tití Me Preguntó", id.Get(b)!.Title);
    }

    [Fact]
    public void La_sonoridad_sigue_al_renombrado_y_a_las_etiquetas()
    {
        var a = Mp3("a.mp3");
        var s = SelloArchivo.De(a);
        var cache = Path.Combine(_dir, "sonoridad.json");
        EscribirCache(cache, a, new { M = s.M, S = s.S, Lufs = -9.5, Peak = -0.3 });
        var son = new LoudnessScanner(cache);
        Assert.NotNull(son.Get(a));

        var b = Path.Combine(_dir, "b.mp3");
        File.Move(a, b);
        Assert.True(son.Mover(a, b));
        var antes = SelloArchivo.De(b);
        Mp3Fixture.SetTags(b, "Tema", null, null);
        Assert.True(son.Retocar(b, antes.M, antes.S));

        Assert.Equal(-9.5, son.Get(b)!.Value.Lufs, 3);
    }

    // --- C8: un «no encontrada» por fallos de red no se guarda ---

    [Fact]
    public void Un_no_encontrada_con_fallos_de_red_no_se_guarda()
    {
        Assert.False(AppEngine.SeGuardaEnCache(new ProcessResult { Found = false }, fallosDeRed: 2));
        Assert.True(AppEngine.SeGuardaEnCache(new ProcessResult { Found = false }, fallosDeRed: 0));   // de verdad no está
        Assert.True(AppEngine.SeGuardaEnCache(new ProcessResult { Found = true }, fallosDeRed: 3));    // lo encontró igual
        Assert.True(AppEngine.SeGuardaEnCache(new ProcessResult { Skip = true }, fallosDeRed: 1));
    }

    // --- I8: recolocar fichas solo relee los manifiestos si hay huérfanas nuevas ---

    [Fact]
    public void Una_huerfana_conocida_no_hace_releer_los_manifiestos()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        var enPapelera = Path.Combine(_dir, "enviada a la papelera.mp3");   // ya no existe
        var viva = Mp3("viva.mp3");
        almacen.Poner(enPapelera, new FichaDj { Energia = 7 });
        almacen.Poner(viva, new FichaDj { Energia = 5 });
        var lecturas = 0;
        IReadOnlyDictionary<string, string> Leer() { lecturas++; return new Dictionary<string, string>(); }

        var primera = AppEngine.ReubicarHuerfanas(almacen, File.Exists, Leer, new HashSet<string>());
        Assert.True(primera.LeyoManifiestos);
        Assert.Contains(enPapelera, primera.Huerfanas);

        // Cada escaneo, cada envío a la papelera y cada renombrado volvía a leerlos todos.
        for (var i = 0; i < 5; i++)
            Assert.False(AppEngine.ReubicarHuerfanas(almacen, File.Exists, Leer, primera.Huerfanas).LeyoManifiestos);
        Assert.Equal(1, lecturas);
    }

    [Fact]
    public void Una_huerfana_nueva_si_se_busca_y_vuelve_a_su_archivo()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        var viejo = Path.Combine(_dir, "karol g provenza (intro).mp3");
        var nuevo = Mp3("Karol G - Provenza (Intro).mp3");   // renombrado al aplicar en Enriquecer
        almacen.Poner(viejo, new FichaDj { Energia = 8 });

        var r = AppEngine.ReubicarHuerfanas(almacen, File.Exists,
                                            () => new Dictionary<string, string> { [viejo] = nuevo }, new HashSet<string>());

        Assert.Equal(1, r.Movidas);
        Assert.Empty(r.Huerfanas);
        Assert.Equal(8, almacen.Obtener(nuevo)!.Energia);
    }

    // --- M9: puntos de control en los bucles largos ---

    [Theory]
    [InlineData(199, 4, false)]
    [InlineData(200, 0, true)]     // cada ~200 canciones
    [InlineData(3, 5, true)]       // o cada 5 minutos, aunque vaya despacio
    public void Un_bucle_largo_guarda_cada_200_canciones_o_cada_5_minutos(int canciones, int minutos, bool guarda)
        => Assert.Equal(guarda, AppEngine.TocaPuntoDeControl(canciones, TimeSpan.FromMinutes(minutos)));

    // --- Avisos de la ventana (I6, T9, T10, M7) ---

    [Fact]
    public void Los_avisos_no_se_repiten_y_se_quitan_solos()
    {
        var avisos = new AvisosApp();
        var cambios = 0;
        avisos.Cambiado += () => cambios++;

        avisos.Poner("carpetas", "No se ha podido leer la carpeta «USB».");
        avisos.Poner("carpetas", "No se ha podido leer la carpeta «USB».");   // el mismo: no molesta otra vez
        avisos.Poner("carga:fichas", "No se pudo leer fichas-dj.json…");
        Assert.Equal(2, avisos.Count);
        Assert.Equal(2, cambios);

        avisos.Poner("carpetas", "");   // la carpeta vuelve a estar
        Assert.Single(avisos.Textos);
        avisos.QuitarTodos();
        Assert.Equal(0, avisos.Count);
    }
}
