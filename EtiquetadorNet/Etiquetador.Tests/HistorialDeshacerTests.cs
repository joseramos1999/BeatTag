using System.Text.Json;
using System.Text.Json.Nodes;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E14/M14 · Historial de deshacer: listar las ejecuciones, deshacer una concreta o solo algunas de
/// sus canciones, y avisar (o negarse) cuando una ejecución posterior tocó los mismos archivos.
///
/// Nace de lo visto en una biblioteca real: 177 manifiestos en la carpeta deshacer y solo «Deshacer
/// última», así que una corrección en el Editor después de aplicar 200 canciones en Enriquecer
/// dejaba ese lote fuera de alcance.
/// </summary>
public class HistorialDeshacerTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;
    private readonly string _musica;

    public HistorialDeshacerTests()
    {
        _paths = new AppPaths(_dir);   // siempre una carpeta temporal: nunca la de datos del usuario
        _paths.EnsureDirectories();
        _musica = Path.Combine(_dir, "musica");
        Directory.CreateDirectory(_musica);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Manifiesto(string nombre) => Path.Combine(_paths.UndoDir, nombre);

    private static string Linea(UndoRecord r) => JsonSerializer.Serialize(r);

    private void Escribir(string nombre, params UndoRecord[] registros)
        => File.WriteAllText(Manifiesto(nombre), string.Join("\n", registros.Select(Linea)) + "\n");

    private string Cancion(string nombre, string titulo)
    {
        var p = Path.Combine(_musica, nombre);
        Mp3Fixture.WriteMinMp3(p);
        Mp3Fixture.SetTags(p, titulo, "Bad Bunny", null);
        return p;
    }

    /// <summary>Cambia el título como lo haría la aplicación y devuelve el registro para el manifiesto.</summary>
    private static UndoRecord CambiarTitulo(string ruta, string nuevo, bool aplicada = true)
    {
        var antes = Mp3Fixture.GetTitle(ruta);
        Mp3Fixture.SetTags(ruta, nuevo, null, null);
        return new UndoRecord
        {
            OrigPath = ruta, FinalPath = ruta, Aplicada = aplicada,
            Fields = new() { ["Title"] = FieldChange.Str(antes, nuevo) },
        };
    }

    /// <summary>Renombra (y opcionalmente cambia el título) como lo haría la aplicación.</summary>
    private static UndoRecord Renombrar(string de, string a, string? titulo = null)
    {
        var campos = new Dictionary<string, FieldChange>();
        if (titulo != null)
        {
            campos["Title"] = FieldChange.Str(Mp3Fixture.GetTitle(de), titulo);
            Mp3Fixture.SetTags(de, titulo, null, null);
        }
        File.Move(de, a);
        return new UndoRecord { OrigPath = de, FinalPath = a, Renamed = true, Fields = campos };
    }

    private HistorialDeshacer Historial => new(_paths.UndoDir);

    // --- Listar ---

    [Fact]
    public void Lista_las_ejecuciones_con_estado_tipo_fecha_y_renombrados()
    {
        var a = Rutas.Archivo("a.mp3");
        var b = Rutas.Archivo("b.mp3");
        // Pendiente: aplicar propuestas, 2 cambios, 1 renombrado.
        Escribir("run_20261001_100000.jsonl",
            new UndoRecord { OrigPath = a, FinalPath = Rutas.Archivo("A.mp3"), Renamed = true, Aplicada = true, Fields = new() { ["Title"] = FieldChange.Str("x", "y") } },
            new UndoRecord { OrigPath = b, FinalPath = b, Aplicada = true, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "y" }) } });
        // Deshecha entera: volumen.
        Escribir("run_20261002_100000.deshecho.jsonl",
            new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(0, 2) } });
        // Parcial: uno pendiente, uno deshecho y uno dado por perdido.
        Escribir("run_20261003_100000.jsonl", new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) } });
        Escribir("run_20261003_100000.deshecho.jsonl", new UndoRecord { OrigPath = b, FinalPath = b, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) } });
        Escribir("run_20261003_100000.perdidos.jsonl", new UndoRecord { OrigPath = Rutas.Archivo("c.mp3"), FinalPath = Rutas.Archivo("c.mp3"), Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) } });
        // Dada por perdida entera.
        Escribir("run_20261004_100000.perdidos.jsonl", new UndoRecord { OrigPath = a, FinalPath = Rutas.Archivo("z.mp3"), Renamed = true });
        // Lo que dejó el fallo T1 (archivado dos veces) sigue siendo deshecho.
        Escribir("run_20260920_230254.deshecho.deshecho.jsonl", new UndoRecord { OrigPath = a, FinalPath = Rutas.Archivo("q.mp3"), Renamed = true });
        // No son ejecuciones: no salen.
        File.WriteAllText(Manifiesto("run_rb.jsonl"), "{}\n");
        File.WriteAllText(Manifiesto("run_20261005_100000.jsonl"), "");   // vacío, resto de un archivado a medias

        var lista = Historial.Listar();

        Assert.Equal(new[] { "run_20261004_100000", "run_20261003_100000", "run_20261002_100000", "run_20261001_100000", "run_20260920_230254" },
                     lista.Select(e => e.Id));
        var aplicar = lista.Single(e => e.Id == "run_20261001_100000");
        Assert.Equal(TipoEjecucion.AplicarPropuestas, aplicar.Tipo);
        Assert.Equal(EstadoEjecucion.Pendiente, aplicar.Estado);
        Assert.Equal(2, aplicar.Cambios);
        Assert.Equal(1, aplicar.Renombrados);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), aplicar.Fecha);
        Assert.Equal(Manifiesto("run_20261001_100000.jsonl"), aplicar.Manifiesto);

        var volumen = lista.Single(e => e.Id == "run_20261002_100000");
        Assert.Equal(TipoEjecucion.Volumen, volumen.Tipo);
        Assert.Equal(EstadoEjecucion.Deshecha, volumen.Estado);
        Assert.Null(volumen.Manifiesto);
        Assert.NotNull(volumen.UltimoDeshacer);

        var parcial = lista.Single(e => e.Id == "run_20261003_100000");
        Assert.Equal(TipoEjecucion.Generos, parcial.Tipo);
        Assert.Equal(EstadoEjecucion.Parcial, parcial.Estado);
        Assert.Equal((1, 1, 1), (parcial.Pendientes, parcial.Deshechos, parcial.Perdidos));
        Assert.True(parcial.TienePendientes);

        var perdida = lista.Single(e => e.Id == "run_20261004_100000");
        Assert.Equal(EstadoEjecucion.Perdida, perdida.Estado);
        Assert.Equal(TipoEjecucion.Renombrado, perdida.Tipo);
        Assert.False(perdida.TienePendientes);

        Assert.Equal(EstadoEjecucion.Deshecha, lista.Single(e => e.Id == "run_20260920_230254").Estado);
    }

    [Fact]
    public void El_estado_concuerda_en_singular()
    {
        static EjecucionDeshacer E(int pendientes, int deshechos, int perdidos)
            => new("run_20261001_100000", new DateTime(2026, 10, 1), TipoEjecucion.Generos, pendientes, deshechos, perdidos, 0, "x", null);

        Assert.Equal("Deshecha en parte (2 de 3), 1 dado por perdido", E(1, 1, 1).EstadoTexto);
        Assert.Equal("Deshecha en parte (3 de 4), 2 dados por perdidos", E(1, 1, 2).EstadoTexto);
        Assert.Equal("Deshecha (1 dado por perdido)", E(0, 1, 1).EstadoTexto);
        Assert.Equal("Deshecha (2 dados por perdidos)", E(0, 1, 2).EstadoTexto);
    }

    [Fact]
    public void Sin_carpeta_de_deshacer_la_lista_esta_vacia()
        => Assert.Empty(new HistorialDeshacer(Path.Combine(_dir, "no-existe")).Listar());

    [Fact]
    public void Los_cambios_de_una_ejecucion_dicen_que_se_hizo_y_como_estan()
    {
        var a = Rutas.Archivo("pista01.mp3");
        var b = Rutas.Archivo("b.mp3");
        Escribir("run_20261003_100000.jsonl",
            new UndoRecord { OrigPath = a, FinalPath = Rutas.Archivo("Bad Bunny - Monaco.mp3"), Renamed = true, Aplicada = true,
                             Fields = new() { ["Title"] = FieldChange.Str("", "Monaco"), ["Artist"] = FieldChange.Arr(Array.Empty<string>(), new[] { "Bad Bunny" }) } });
        File.AppendAllText(Manifiesto("run_20261003_100000.jsonl"), "esto no es json\n");
        Escribir("run_20261003_100000.deshecho.jsonl", new UndoRecord { OrigPath = b, FinalPath = b, Aplicada = true, Fields = new() { ["Year"] = FieldChange.Num(0, 2023) } });

        var e = Historial.Listar().Single();
        var cambios = Historial.Cambios(e);

        Assert.Equal(3, cambios.Count);
        var primero = cambios[0];
        Assert.Equal(EstadoCambio.Pendiente, primero.Estado);
        Assert.Equal("Bad Bunny - Monaco.mp3", primero.Archivo);
        Assert.Equal("pista01.mp3", primero.ArchivoAntes);
        Assert.Equal("título, artista, nombre", primero.CamposTexto);
        Assert.False(cambios[1].Legible);
        Assert.Equal("(registro ilegible)", cambios[1].CamposTexto);
        Assert.Equal(EstadoCambio.Deshecho, cambios[2].Estado);
        Assert.Equal("año", cambios[2].CamposTexto);
    }

    [Fact]
    public void Lee_el_formato_antiguo()
    {
        var actual = Rutas.Archivo("Bad Bunny - Monaco.mp3");
        File.WriteAllText(Manifiesto("run_20250101_100000.jsonl"),
            "{\"new\":\"" + JsonEncodedText.Encode(actual) + "\",\"orig\":\"pista01.mp3\",\"renamed\":true,\"tags\":{\"Title\":\"\"}}\n");

        var e = Historial.Listar().Single();
        var c = Historial.Cambios(e).Single();

        Assert.Equal(TipoEjecucion.AplicarPropuestas, e.Tipo);
        Assert.Equal(1, e.Renombrados);
        Assert.Equal(Rutas.Archivo("pista01.mp3"), c.Antes);
        Assert.Equal(actual, c.Despues);
        Assert.Equal("título, nombre", c.CamposTexto);
    }

    // --- Tipo deducido ---

    private static JsonNode Nodo(UndoRecord r) => JsonNode.Parse(Linea(r))!;

    private static UndoRecord Campos(params string[] campos) => new()
    {
        OrigPath = Rutas.Archivo("a.mp3"), FinalPath = Rutas.Archivo("a.mp3"),
        Fields = campos.ToDictionary(c => c, c => FieldChange.Str("x", "y")),
    };

    [Theory]
    [InlineData(TipoEjecucion.Volumen, new[] { "Volumen" })]
    [InlineData(TipoEjecucion.Rekordbox, new[] { "Bpm", "Key" })]
    [InlineData(TipoEjecucion.Generos, new[] { "Genre" })]
    [InlineData(TipoEjecucion.Comentario, new[] { "Comment" })]
    [InlineData(TipoEjecucion.Caratula, new[] { "Cover" })]
    [InlineData(TipoEjecucion.Bpm, new[] { "Bpm" })]
    [InlineData(TipoEjecucion.EdicionEtiquetas, new[] { "Title", "Artist" })]
    public void El_tipo_sale_de_los_campos(TipoEjecucion esperado, string[] campos)
        => Assert.Equal(esperado, HistorialDeshacer.DeducirTipo(new[] { Nodo(Campos(campos)) }));

    [Fact]
    public void Aplicar_propuestas_se_reconoce_por_la_marca_y_en_los_manifiestos_antiguos()
    {
        var conMarca = Campos("Genre");
        conMarca.Aplicada = true;
        Assert.Equal(TipoEjecucion.AplicarPropuestas, HistorialDeshacer.DeducirTipo(new[] { Nodo(conMarca) }));

        // Anterior a la marca Aplicada: la misma regla que deshacer (título, artista… → aplicar).
        var sinMarca = Nodo(Campos("Title", "Genre")).AsObject();
        sinMarca.Remove(nameof(UndoRecord.Aplicada));
        Assert.Equal(TipoEjecucion.AplicarPropuestas, HistorialDeshacer.DeducirTipo(new JsonNode?[] { sinMarca }));

        // Con la marca a false (esta versión) y los mismos campos: el Editor o Incompletas.
        Assert.Equal(TipoEjecucion.EdicionEtiquetas, HistorialDeshacer.DeducirTipo(new[] { Nodo(Campos("Title", "Genre")) }));
    }

    // Visto en una biblioteca real: run_20260810_115221, 6.883 registros con título, artista, álbum y
    // año (anteriores a la marca Aplicada) y 3 sin campos. La regla de deshacer, conservadora a
    // propósito, dice «no» por esos 3; para poner nombre a la ejecución basta la señal clara.
    [Fact]
    public void Un_lote_antiguo_con_algun_registro_sin_campos_sigue_siendo_aplicar()
    {
        JsonNode SinMarca(UndoRecord r) { var o = Nodo(r).AsObject(); o.Remove(nameof(UndoRecord.Aplicada)); return o; }
        var conCampos = SinMarca(Campos("Title", "Artist", "Album", "Year"));
        var sinCampos = SinMarca(new UndoRecord { OrigPath = Rutas.Archivo("a.mp3"), FinalPath = Rutas.Archivo("b.mp3"), Renamed = true });
        var lote = new JsonNode?[] { conCampos, conCampos.DeepClone(), sinCampos };

        Assert.False(UndoEngine.ManifiestoAntiguoDeAplicar(lote));   // deshacer sigue siendo conservador
        Assert.Equal(TipoEjecucion.AplicarPropuestas, HistorialDeshacer.DeducirTipo(lote));
        // Con un solo registro no se distingue del Editor (que también escribe el comentario): decide
        // la regla de deshacer.
        Assert.Equal(TipoEjecucion.EdicionEtiquetas, HistorialDeshacer.DeducirTipo(new JsonNode?[] { SinMarca(Campos("Title", "Comment")) }));
    }

    // Visto en una biblioteca real: run_20260925_195430, un registro con "Renamed":true y la misma ruta.
    [Fact]
    public void Un_renombrado_que_no_cambio_nada_sigue_siendo_renombrado()
    {
        var mismo = new UndoRecord { OrigPath = Rutas.Archivo("a.mp3"), FinalPath = Rutas.Archivo("a.mp3"), Renamed = true };
        Assert.Equal(TipoEjecucion.Renombrado, HistorialDeshacer.DeducirTipo(new[] { Nodo(mismo) }));
        var nada = new UndoRecord { OrigPath = Rutas.Archivo("a.mp3"), FinalPath = Rutas.Archivo("a.mp3") };
        Assert.Equal(TipoEjecucion.Desconocido, HistorialDeshacer.DeducirTipo(new[] { Nodo(nada) }));
    }

    [Fact]
    public void Solo_renombrar_es_renombrado_y_cambiar_de_carpeta_es_traslado()
    {
        var renombrar = new UndoRecord { OrigPath = Rutas.Archivo("a.mp3"), FinalPath = Rutas.Archivo("b.mp3"), Renamed = true };
        var trasladar = new UndoRecord { OrigPath = Rutas.De("descargas", "a.mp3"), FinalPath = Rutas.De("musica", "a.mp3"), Renamed = true };
        Assert.Equal(TipoEjecucion.Renombrado, HistorialDeshacer.DeducirTipo(new[] { Nodo(renombrar) }));
        Assert.Equal(TipoEjecucion.Traslado, HistorialDeshacer.DeducirTipo(new[] { Nodo(trasladar), Nodo(renombrar) }));
        Assert.Equal(TipoEjecucion.Desconocido, HistorialDeshacer.DeducirTipo(new JsonNode?[] { null }));
    }

    [Fact]
    public void El_origen_anotado_manda_y_no_se_escribe_si_no_hay()
    {
        var r = Campos("Genre");
        Assert.DoesNotContain(nameof(UndoRecord.Origen), Linea(r));   // los manifiestos de siempre no cambian

        r.Origen = nameof(TipoEjecucion.EdicionEtiquetas);
        Assert.Contains("\"Origen\":\"EdicionEtiquetas\"", Linea(r));
        Assert.Equal(TipoEjecucion.EdicionEtiquetas, HistorialDeshacer.DeducirTipo(new[] { Nodo(r) }));

        // Un valor desconocido (de una versión futura) no rompe nada: se deduce como siempre.
        r.Origen = "AlgoNuevo";
        Assert.Equal(TipoEjecucion.Generos, HistorialDeshacer.DeducirTipo(new[] { Nodo(r) }));
    }

    // --- Deshacer solo algunas canciones ---

    [Fact]
    public void Deshacer_solo_las_marcadas_deja_el_resto_pendiente_y_archiva_lo_revertido()
    {
        var a = Cancion("a.mp3", "a0");
        var b = Cancion("b.mp3", "b0");
        var c = Cancion("c.mp3", "c0");
        Escribir("run_20261009_160301.jsonl", CambiarTitulo(a, "A"), CambiarTitulo(b, "B"), CambiarTitulo(c, "C"));
        var e = Historial.Listar().Single();
        var cambios = Historial.Cambios(e);
        var soloB = cambios.Single(x => x.Antes == b).Huella;

        var undo = new UndoEngine(_paths);
        var res = undo.DeshacerCambios(e.Manifiesto!, new[] { soloB });

        Assert.Equal(1, res.Reverted);
        Assert.Equal(0, res.Pendientes);
        Assert.Equal("b0", Mp3Fixture.GetTitle(b));
        Assert.Equal("A", Mp3Fixture.GetTitle(a));   // lo no elegido no se toca
        Assert.Equal("C", Mp3Fixture.GetTitle(c));
        Assert.Equal(2, File.ReadAllLines(Manifiesto("run_20261009_160301.jsonl")).Count(l => l.Length > 0));
        Assert.Single(File.ReadAllLines(Manifiesto("run_20261009_160301.deshecho.jsonl")), l => l.Length > 0);
        Assert.Contains(b, res.AplicadasRevertidas);

        var parcial = Historial.Listar().Single();
        Assert.Equal(EstadoEjecucion.Parcial, parcial.Estado);
        Assert.Equal((2, 1), (parcial.Pendientes, parcial.Deshechos));

        // Lo que queda se deshace después igual que siempre, y la ejecución queda deshecha.
        var resto = undo.UndoLastRun();
        Assert.Equal(2, resto.Reverted);
        Assert.Equal("a0", Mp3Fixture.GetTitle(a));
        Assert.Equal("c0", Mp3Fixture.GetTitle(c));
        Assert.False(File.Exists(Manifiesto("run_20261009_160301.jsonl")));
        Assert.True(File.Exists(Manifiesto("run_20261009_160301.deshecho-2.jsonl")));
        Assert.Equal(EstadoEjecucion.Deshecha, Historial.Listar().Single().Estado);
    }

    [Fact]
    public void Deshacer_una_ejecucion_que_no_es_la_ultima()
    {
        var a = Cancion("a.mp3", "a0");
        var b = Cancion("b.mp3", "b0");
        Escribir("run_20261001_100000.jsonl", CambiarTitulo(a, "A"));
        Escribir("run_20261002_100000.jsonl", CambiarTitulo(b, "B"));   // la última, de otra canción
        var vieja = Historial.Listar().Single(e => e.Id == "run_20261001_100000");

        var res = new UndoEngine(_paths).DeshacerCambios(vieja.Manifiesto!, null);

        Assert.Equal(1, res.Reverted);
        Assert.Equal("a0", Mp3Fixture.GetTitle(a));
        Assert.Equal("B", Mp3Fixture.GetTitle(b));
        Assert.True(File.Exists(Manifiesto("run_20261002_100000.jsonl")));   // la última sigue pendiente
    }

    [Fact]
    public void No_se_deshace_un_manifiesto_archivado_ni_una_seleccion_vacia()
    {
        var a = Cancion("a.mp3", "a0");
        Escribir("run_20261001_100000.deshecho.jsonl", CambiarTitulo(a, "A"));
        var undo = new UndoEngine(_paths);
        Assert.True(undo.DeshacerCambios(Manifiesto("run_20261001_100000.deshecho.jsonl"), null).None);
        Assert.Equal("A", Mp3Fixture.GetTitle(a));

        Escribir("run_20261002_100000.jsonl", new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { ["Title"] = FieldChange.Str("a0", "A") } });
        Assert.True(undo.DeshacerCambios(Manifiesto("run_20261002_100000.jsonl"), Array.Empty<string>()).None);
        Assert.Equal("A", Mp3Fixture.GetTitle(a));
    }

    [Fact]
    public void Lo_tocado_va_con_la_ruta_de_despues_antes_que_la_de_antes()
    {
        var antes = Cancion("pista01.mp3", "x");
        var despues = Path.Combine(_musica, "Bad Bunny - Monaco.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(antes, despues));

        var res = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(new[] { despues, antes }, res.Tocadas);
        Assert.True(File.Exists(antes));
    }

    [Fact]
    public void La_reparacion_de_rekordbox_ve_lo_deshecho_desde_el_historial()
    {
        var a = Cancion("a.mp3", "x");
        var b = Cancion("b.mp3", "y");
        var a2 = Path.Combine(_musica, "A2.mp3");
        var b2 = Path.Combine(_musica, "B2.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(a, a2), Renombrar(b, b2));
        var e = Historial.Listar().Single();
        var huellaA = Historial.Cambios(e).Single(c => c.Antes == a).Huella;

        new UndoEngine(_paths).DeshacerCambios(e.Manifiesto!, new[] { huellaA });

        var vigentes = RekordboxRelocator.RenombradosVigentes(_paths.UndoDir);
        Assert.False(vigentes.ContainsKey(a));      // deshecho: a.mp3 vuelve a ser su ruta
        Assert.Equal(b2, vigentes[b]);              // sigue renombrado
    }

    // --- Ejecuciones posteriores que tocaron los mismos archivos ---

    [Fact]
    public void Se_niega_si_una_posterior_renombro_el_archivo_y_dice_donde_esta()
    {
        // Enriquecer renombra pista01 → Monaco; después Renombrar con IA lo vuelve a renombrar.
        var p0 = Cancion("pista01.mp3", "pista01");
        var p1 = Path.Combine(_musica, "Bad Bunny - Monaco.mp3");
        var p2 = Path.Combine(_musica, "Bad Bunny - MONACO (Extended).mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(p0, p1, titulo: "Monaco"));
        Escribir("run_20261002_100000.jsonl", Renombrar(p1, p2));

        var h = Historial;
        var todas = h.Listar();
        var vieja = todas.Single(e => e.Id == "run_20261001_100000");
        var analisis = h.Analizar(vieja, null, todas);

        Assert.Empty(analisis.ADeshacer);
        var bloqueo = Assert.Single(analisis.Bloqueados);
        Assert.Equal("run_20261002_100000", bloqueo.Posterior.Id);
        Assert.Equal(p2, bloqueo.RutaActual);
        Assert.False(bloqueo.OtroArchivo);
        Assert.False(bloqueo.Perdido);   // la posterior se puede deshacer antes

        // Deshaciendo antes la posterior, ya se puede.
        new UndoEngine(_paths).UndoLastRun();
        var otra = h.Analizar(h.Listar().Single(e => e.Id == "run_20261001_100000"), null);
        Assert.Single(otra.ADeshacer);
        Assert.Empty(otra.Bloqueados);
    }

    [Fact]
    public void Se_niega_si_otro_archivo_ocupa_ahora_el_nombre()
    {
        var mio = Cancion("tema.mp3", "x");
        var renombrado = Path.Combine(_musica, "Tema bueno.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(mio, renombrado));
        // Después, a mano, el archivo se movió fuera y otra ejecución dejó otro con ese nombre.
        File.Move(renombrado, Path.Combine(_dir, "fuera.mp3"));
        var otro = Cancion("otro.mp3", "y");
        Escribir("run_20261002_100000.jsonl", Renombrar(otro, renombrado));

        var h = Historial;
        var analisis = h.Analizar(h.Listar().Single(e => e.Id == "run_20261001_100000"), null);

        var bloqueo = Assert.Single(analisis.Bloqueados);
        Assert.True(bloqueo.OtroArchivo);
        Assert.Equal(renombrado, bloqueo.RutaActual);
    }

    [Fact]
    public void Avisa_de_lo_que_se_pisaria_si_una_posterior_toco_el_mismo_archivo()
    {
        // Enriquecer aplica (renombra y pone título); luego el Editor corrige el título a mano.
        var p0 = Cancion("pista01.mp3", "pista01");
        var p1 = Path.Combine(_musica, "Bad Bunny - Monaco.mp3");
        var otra = Cancion("otra.mp3", "o0");
        var aplicar = Renombrar(p0, p1, titulo: "Monaco");
        aplicar.Aplicada = true;
        Escribir("run_20261001_100000.jsonl", aplicar, CambiarTitulo(otra, "Otra"));
        Escribir("run_20261002_100000.jsonl", CambiarTitulo(p1, "MONACO", aplicada: false));

        var h = Historial;
        var todas = h.Listar();
        var lote = todas.Single(e => e.Id == "run_20261001_100000");
        var analisis = h.Analizar(lote, null, todas);

        Assert.Equal(2, analisis.ADeshacer.Count);
        Assert.Empty(analisis.Bloqueados);
        var solape = Assert.Single(analisis.Solapes);
        Assert.Equal(p0, solape.Cambio.Antes);
        Assert.Equal(new[] { "Title" }, solape.CamposCompartidos);
        Assert.True(solape.PierdeDeshacer);
        Assert.Equal("run_20261002_100000", Assert.Single(analisis.Posteriores).Id);

        // Y es verdad lo que se avisa: el título del Editor se conserva, el archivo vuelve a su
        // nombre y la ejecución posterior ya no lo encuentra.
        var undo = new UndoEngine(_paths);
        var res = undo.DeshacerCambios(lote.Manifiesto!, analisis.Huellas);
        Assert.Equal(2, res.Reverted);
        Assert.True(File.Exists(p0));
        Assert.Equal("MONACO", Mp3Fixture.GetTitle(p0));
        Assert.Equal("o0", Mp3Fixture.GetTitle(otra));
        Assert.Equal(1, undo.UndoLastRun().Missing);
    }

    [Fact]
    public void Lo_ya_deshecho_de_las_posteriores_no_cuenta_y_lo_perdido_si()
    {
        var a = Rutas.Archivo("a.mp3");
        var b = Rutas.Archivo("b.mp3");
        Escribir("run_20261001_100000.jsonl",
            new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) } },
            new UndoRecord { OrigPath = b, FinalPath = b, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) } });
        // Posterior ya deshecha que tocó a: no estorba.
        Escribir("run_20261002_100000.deshecho.jsonl", new UndoRecord { OrigPath = a, FinalPath = Rutas.Archivo("a2.mp3"), Renamed = true });
        // Posterior dada por perdida que renombró b: ese renombrado sigue en pie.
        Escribir("run_20261003_100000.perdidos.jsonl", new UndoRecord { OrigPath = b, FinalPath = Rutas.Archivo("b2.mp3"), Renamed = true });

        var h = Historial;
        var analisis = h.Analizar(h.Listar().Single(e => e.Id == "run_20261001_100000"), null);

        Assert.Equal(a, Assert.Single(analisis.ADeshacer).Antes);
        Assert.Empty(analisis.Solapes);
        var bloqueo = Assert.Single(analisis.Bloqueados);
        Assert.Equal(b, bloqueo.Cambio.Antes);
        Assert.True(bloqueo.Perdido);   // ya no se puede deshacer antes: solo cabe darlo por perdido también
        Assert.Equal(2, analisis.NoEncontrados.Count + analisis.Bloqueados.Count);   // a no existe en disco
    }

    // --- El nombre anterior, ocupado ---

    [Fact]
    public void Se_niega_si_una_posterior_llevo_otro_archivo_al_nombre_anterior()
    {
        // Enriquecer renombra «tema.mp3»; después la Bandeja trae a la misma carpeta otra descarga
        // con ese nombre. Deshacer el renombrado fallaría: File.Move no sobrescribe.
        var tema = Cancion("tema.mp3", "x");
        var renombrado = Path.Combine(_musica, "Bad Bunny - Tema.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(tema, renombrado, titulo: "Tema"));
        var descarga = Path.Combine(_dir, "descarga.mp3");
        Mp3Fixture.WriteMinMp3(descarga);
        Escribir("run_20261002_100000.jsonl", Renombrar(descarga, tema));

        var h = Historial;
        var analisis = h.Analizar(h.Listar().Single(e => e.Id == "run_20261001_100000"), null);

        Assert.Empty(analisis.ADeshacer);
        var bloqueo = Assert.Single(analisis.Bloqueados);
        Assert.True(bloqueo.OtroArchivo);
        Assert.True(bloqueo.NombreAnterior);
        Assert.False(bloqueo.Perdido);
        Assert.Equal(tema, bloqueo.RutaActual);
        Assert.Equal("run_20261002_100000", bloqueo.Posterior.Id);

        // Deshaciendo antes la Bandeja, la descarga se va y el nombre queda libre.
        new UndoEngine(_paths).UndoLastRun();
        var libre = h.Analizar(h.Listar().Single(e => e.Id == "run_20261001_100000"), null);
        Assert.Single(libre.ADeshacer);
        Assert.Empty(libre.Bloqueados);
        Assert.Empty(libre.NombreOcupado);
    }

    [Fact]
    public void Si_otro_archivo_llego_al_nombre_anterior_y_se_fue_no_estorba()
    {
        var a = Rutas.Archivo("a.mp3");
        Escribir("run_20261001_100000.jsonl", new UndoRecord { OrigPath = a, FinalPath = Rutas.Archivo("A bueno.mp3"), Renamed = true });
        Escribir("run_20261002_100000.jsonl", new UndoRecord { OrigPath = Rutas.Archivo("otro.mp3"), FinalPath = a, Renamed = true });
        Escribir("run_20261003_100000.jsonl", new UndoRecord { OrigPath = a, FinalPath = Rutas.Archivo("otro bueno.mp3"), Renamed = true });

        var h = Historial;
        var analisis = h.Analizar(h.Listar().Single(e => e.Id == "run_20261001_100000"), null);

        Assert.Single(analisis.ADeshacer);
        Assert.Empty(analisis.Bloqueados);
    }

    [Fact]
    public void Un_nombre_anterior_ocupado_en_el_disco_se_aparta_sin_tocar_nada()
    {
        var tema = Cancion("tema.mp3", "x");
        var renombrado = Path.Combine(_musica, "Bad Bunny - Tema.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(tema, renombrado, titulo: "Tema"));
        Cancion("tema.mp3", "copia descargada otra vez");   // fuera de la aplicación: el historial no lo sabe

        var h = Historial;
        var e = h.Listar().Single();
        var analisis = h.Analizar(e, null);

        Assert.Empty(analisis.ADeshacer);
        Assert.Empty(analisis.Bloqueados);
        Assert.Equal(tema, Assert.Single(analisis.NombreOcupado).Antes);
    }

    [Fact]
    public void Dentro_de_una_misma_ejecucion_el_nombre_que_se_libera_no_cuenta_como_ocupado()
    {
        // tema.mp3 → «Bad Bunny - Tema.mp3» y, en la misma ejecución, «tema (1).mp3» → tema.mp3.
        var tema = Cancion("tema.mp3", "original");
        var copia = Cancion("tema (1).mp3", "copia");
        var renombrado = Path.Combine(_musica, "Bad Bunny - Tema.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(tema, renombrado), Renombrar(copia, tema));

        var h = Historial;
        var e = h.Listar().Single();
        var todo = h.Analizar(e, null);
        Assert.Equal(2, todo.ADeshacer.Count);
        Assert.Empty(todo.NombreOcupado);

        // Solo el primero: el segundo no se deshace, así que «tema.mp3» sigue ocupado.
        var huellaPrimero = h.Cambios(e).Single(c => c.Antes == tema).Huella;
        Assert.Single(h.Analizar(e, new List<string> { huellaPrimero }).NombreOcupado);

        var res = new UndoEngine(_paths).DeshacerCambios(e.Manifiesto!, todo.Huellas);
        Assert.Equal(2, res.Reverted);
        Assert.Equal("original", Mp3Fixture.GetTitle(tema));
        Assert.Equal("copia", Mp3Fixture.GetTitle(copia));
    }

    [Fact]
    public void Se_sabe_si_la_posterior_que_pierde_su_archivo_cambio_el_volumen()
    {
        var a = Rutas.Archivo("a.mp3");
        Escribir("run_20261001_100000.jsonl", new UndoRecord { OrigPath = Rutas.Archivo("viejo.mp3"), FinalPath = a, Renamed = true });
        Escribir("run_20261002_100000.jsonl", new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(0, 2) } });

        var h = Historial;
        var solape = Assert.Single(h.Analizar(h.Listar().Single(e => e.Id == "run_20261001_100000"), null).Solapes);

        Assert.True(solape.PierdeDeshacer);
        Assert.True(solape.VolumenPosterior);
    }

    // --- Dar por perdidos con el manifiesto bloqueado ---

    // Visto en revisión: si no se podía mover ni vaciar el manifiesto, la copia «.perdidos» se
    // quedaba al lado de un manifiesto aún pendiente; el historial contaba esas líneas dos veces y
    // un reintento creaba «.perdidos-2». En macOS y Linux abrir un archivo no impide renombrarlo.
    [WindowsFact]
    public void Dar_por_perdidos_que_no_puede_vaciar_el_manifiesto_no_deja_copia()
    {
        var mf = Manifiesto("run_20261009_160301.jsonl");
        Escribir("run_20261009_160301.jsonl", new UndoRecord { OrigPath = Rutas.Archivo("a.mp3"), FinalPath = Rutas.Archivo("b.mp3"), Renamed = true });
        var contenido = File.ReadAllText(mf);
        var undo = new UndoEngine(_paths);

        PendientesDescartados r;
        using (new FileStream(mf, FileMode.Open, FileAccess.Read, FileShare.Read))   // se deja leer, pero no mover ni sustituir
            r = undo.DarPorPerdidos(mf);

        Assert.False(r.Ok);
        Assert.Empty(Directory.GetFiles(_paths.UndoDir, "*.perdidos*"));
        Assert.Equal(contenido, File.ReadAllText(mf));
        var e = Historial.Listar().Single();
        Assert.Equal((1, 0), (e.Pendientes, e.Perdidos));   // no cuenta dos veces lo que sigue pendiente

        // Ya libre, se da por perdido con el nombre de siempre.
        Assert.True(undo.DarPorPerdidos(mf).Ok);
        Assert.True(File.Exists(Manifiesto("run_20261009_160301.perdidos.jsonl")));
        Assert.False(File.Exists(Manifiesto("run_20261009_160301.perdidos-2.jsonl")));
    }

    [Fact]
    public void Las_anteriores_y_el_volumen_no_son_conflicto()
    {
        var a = Rutas.Archivo("a.mp3");
        Escribir("run_20261001_100000.jsonl", new UndoRecord { OrigPath = Rutas.Archivo("viejo.mp3"), FinalPath = a, Renamed = true });
        Escribir("run_20261002_100000.jsonl", new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(0, 2) } });
        Escribir("run_20261003_100000.jsonl", new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(2, 0) } });

        var h = Historial;
        var todas = h.Listar();
        var analisis = h.Analizar(todas.Single(e => e.Id == "run_20261002_100000"), null, todas);

        Assert.Single(analisis.ADeshacer);
        var solape = Assert.Single(analisis.Solapes);
        Assert.Empty(solape.CamposCompartidos);   // el volumen es relativo: deshacer uno no pisa al otro
        Assert.False(solape.PierdeDeshacer);
    }

    [Fact]
    public void Solo_se_analiza_lo_elegido()
    {
        var a = Rutas.Archivo("a.mp3");
        var b = Rutas.Archivo("b.mp3");
        Escribir("run_20261001_100000.jsonl",
            new UndoRecord { OrigPath = a, FinalPath = a, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) } },
            new UndoRecord { OrigPath = b, FinalPath = b, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "House" }) } });
        var h = Historial;
        var e = h.Listar().Single();
        var huellaB = h.Cambios(e).Single(c => c.Antes == b).Huella;

        var analisis = h.Analizar(e, new[] { huellaB });

        Assert.Equal(b, Assert.Single(analisis.ADeshacer).Antes);
        Assert.Equal(new[] { huellaB }, analisis.Huellas);
    }
}
