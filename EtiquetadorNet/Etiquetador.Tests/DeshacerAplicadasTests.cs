using System.Text.Json;
using System.Text.Json.Nodes;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E8 · Tras «Deshacer última», las canciones que solo cambiaron etiquetas seguían como aplicadas
/// para siempre. Escenario: se aplican 50 canciones en las que solo cambiaban género y año, no
/// convence, se deshace; las etiquetas vuelven, pero la ruta sigue en aplicadas.json y Analizar las
/// omite como «ya aplicadas». Las renombradas sí volvían, porque su ruta cambia. La única salida era
/// «Olvidar aplicadas», que devolvía también las miles aplicadas de verdad.
///
/// Y la otra cara, vista en revisión: «Deshacer última» es un único botón para todo, y deshacer un
/// ajuste de volumen sacaba de aplicadas.json las 640 canciones aplicadas antes en Enriquecer. Solo
/// vuelve a pendientes lo que se había dado por aplicado.
/// </summary>
public class DeshacerAplicadasTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;

    public DeshacerAplicadasTests()
    {
        _paths = new AppPaths(_dir);   // siempre una carpeta temporal: nunca la de datos del usuario
        _paths.EnsureDirectories();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Cancion(string nombre, int tramas = 25)
    {
        var musica = Path.Combine(_dir, "musica");
        Directory.CreateDirectory(musica);
        var p = Path.Combine(musica, nombre);
        // Con más de 25 tramas, para ajustar el volumen: tramas con audio y margen para subir y bajar.
        // Las de WriteMinMp3 no llevan audio en ningún gránulo y Mp3Gain no les cambia ni un byte.
        if (tramas > 25) File.WriteAllBytes(p, Mp3GainSintetico.Tramas(tramas, _ => 150));
        else Mp3Fixture.WriteMinMp3(p, tramas);
        Mp3Fixture.SetTags(p, "Monaco", "Bad Bunny", null);
        return p;
    }

    private string Manifiesto(string nombre) => Path.Combine(_paths.UndoDir, nombre);

    /// <summary>Como Enriquecer y No encontradas: la canción queda dada por aplicada.</summary>
    private static Task<ApplyOneResult> Aplicar(string ruta, string nuevo, string manifiesto, string doneLog, bool aplicada = true)
    {
        var info = new ProcessResult
        {
            FilePath = ruta, Old = Path.GetFileName(ruta), New = nuevo,
            Title = "MONACO", Artist = "Bad Bunny", Genre = "Reggaeton", Year = "2023", Found = true,
        };
        return new ApplyEngine().ApplyOneAsync(info, over: true, FieldFlags.All, "keep", null, manifiesto, doneLog, aplicada: aplicada);
    }

    /// <summary>La línea de manifiesto tal como la escribía una versión anterior a la marca.</summary>
    private static string SinMarca(string linea)
    {
        var o = JsonNode.Parse(linea)!.AsObject();
        o.Remove(nameof(UndoRecord.Aplicada));
        return o.ToJsonString();
    }

    /// <summary>Un ajuste de volumen anotado como lo hace la pestaña Volumen.</summary>
    private static string Volumen(string mp3, int pasos)
    {
        var antes = File.ReadAllBytes(mp3);
        Assert.True(Mp3Gain.Apply(mp3, pasos).Ok);
        Assert.NotEqual(antes, File.ReadAllBytes(mp3));   // el ajuste cambia el archivo de verdad
        var cambio = pasos >= 0 ? FieldChange.Num(0, (uint)pasos) : FieldChange.Num((uint)(-pasos), 0);
        return JsonSerializer.Serialize(new UndoRecord
        {
            OrigPath = mp3, FinalPath = mp3, Renamed = false,
            Fields = new() { [UndoEngine.VolumeField] = cambio },
        });
    }

    [Fact]
    public async Task Una_que_solo_cambio_etiquetas_deja_de_contar_como_aplicada()
    {
        var p = Cancion("Bad Bunny - Monaco.mp3");
        var res = await Aplicar(p, "Bad Bunny - Monaco.mp3", _paths.NuevoManifiesto(), _paths.DoneLog);
        Assert.True(res.TagOk);
        Assert.Equal(p, res.FinalPath);   // sin renombrar: la ruta es la misma

        var aplicadas = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        aplicadas.Add(res.FinalPath);
        var marcas = new ApplyMarks(Path.Combine(_dir, "marcas.json"));
        marcas.Set(p, true);   // casilla antigua, guardada solo por ruta

        var u = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal(1, u.Reverted);
        Assert.Contains(p, u.AplicadasRevertidas);

        Assert.Equal(1, EnrichViewModel.OlvidarRevertidas(aplicadas, marcas, u.AplicadasRevertidas));
        Assert.False(aplicadas.Contains(p));
        Assert.Null(marcas.Get(p));
    }

    // Las renombradas: aplicadas.json tiene la ruta NUEVA, y deshacer las devuelve a la vieja.
    [Fact]
    public async Task Una_renombrada_se_olvida_por_su_ruta_nueva()
    {
        var p = Cancion("bad_bunny-monaco.mp3");
        var res = await Aplicar(p, "Bad Bunny - MONACO.mp3", _paths.NuevoManifiesto(), _paths.DoneLog);
        Assert.True(res.DidRename);

        var aplicadas = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        aplicadas.Add(res.FinalPath);

        var u = new UndoEngine(_paths).UndoLastRun();
        Assert.Contains(p, u.AplicadasRevertidas);
        Assert.Contains(res.FinalPath, u.AplicadasRevertidas);
        Assert.True(File.Exists(p));

        Assert.Equal(1, EnrichViewModel.OlvidarRevertidas(aplicadas, new ApplyMarks(Path.Combine(_dir, "m.json")), u.AplicadasRevertidas));
        Assert.Equal(0, aplicadas.Count);
    }

    // Lo que el deshacer conserva porque lo cambiaste a mano después NO se revierte: sigue aplicada.
    [Fact]
    public async Task Lo_conservado_por_cambios_manuales_sigue_aplicado()
    {
        var p = Cancion("Bad Bunny - Monaco.mp3");
        var info = new ProcessResult { FilePath = p, Old = Path.GetFileName(p), New = Path.GetFileName(p), Title = "MONACO", Found = true };
        var soloTitulo = new FieldFlags { Title = true, Artist = false, Album = false, Genre = false, Year = false, Bpm = false };
        await new ApplyEngine().ApplyOneAsync(info, over: true, soloTitulo, "keep", null, _paths.NuevoManifiesto(), _paths.DoneLog, aplicada: true);
        Mp3Fixture.SetTags(p, "Monaco (mi versión)", null, null);   // el usuario lo retoca después

        var u = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(1, u.Manual);
        Assert.Empty(u.Revertidas);
        Assert.Empty(u.AplicadasRevertidas);
        var aplicadas = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        aplicadas.Add(p);
        Assert.Equal(0, EnrichViewModel.OlvidarRevertidas(aplicadas, new ApplyMarks(Path.Combine(_dir, "m.json")), u.AplicadasRevertidas));
        Assert.True(aplicadas.Contains(p));
    }

    // Los manifiestos del formato antiguo (new/orig) también dicen qué rutas se revirtieron. Ese
    // formato solo lo escribía aplicar propuestas, así que cuentan como aplicadas.
    [Fact]
    public void El_formato_antiguo_tambien_da_las_rutas()
    {
        var p = Cancion("Bad Bunny - MONACO.mp3");
        var original = Path.Combine(Path.GetDirectoryName(p)!, "pista07.mp3");
        var linea = JsonSerializer.Serialize(new { @new = p, orig = "pista07.mp3", renamed = true });
        File.WriteAllText(Manifiesto("run_20250101_120000.jsonl"), linea + "\n");

        var u = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(1, u.Reverted);
        Assert.Contains(original, u.Revertidas);
        Assert.Contains(p, u.Revertidas);
        Assert.Contains(original, u.AplicadasRevertidas);
        Assert.Contains(p, u.AplicadasRevertidas);
        Assert.True(File.Exists(original));
    }

    // El caso de la revisión, a escala: aplicadas en Enriquecer y después ajustado el volumen de la
    // biblioteca. Deshacer el volumen las devuelve a su nivel, pero siguen aplicadas; deshacer
    // después la tirada de Enriquecer sí las devuelve a pendientes.
    [Fact]
    public async Task Deshacer_el_volumen_no_devuelve_a_pendientes_las_aplicadas_en_Enriquecer()
    {
        var aplicadas = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        var marcas = new ApplyMarks(Path.Combine(_dir, "marcas.json"));
        var enriquecer = Manifiesto("run_20261001_120000.jsonl");
        var canciones = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var p = Cancion($"Bad Bunny - Tema {i:00}.mp3", tramas: 30);
            var res = await Aplicar(p, Path.GetFileName(p), enriquecer, _paths.DoneLog);
            Assert.True(res is { TagOk: true, RenOk: true });
            aplicadas.Add(res.FinalPath);
            canciones.Add(p);
        }
        File.WriteAllText(Manifiesto("run_20261009_210831.jsonl"),
            string.Join("\n", canciones.Select(p => Volumen(p, -2))) + "\n");   // −3 dB a cada una

        var u = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal("run_20261009_210831.jsonl", u.File);
        Assert.Equal(3, u.Reverted);
        Assert.Empty(u.AplicadasRevertidas);
        // Nombre y etiquetas siguen igual: sus propuestas pendientes en Enriquecer, si las hubiera,
        // siguen valiendo y no se retiran de la lista (ajustar el volumen tampoco las retira).
        Assert.Empty(u.Revertidas);
        Assert.Equal(0, EnrichViewModel.OlvidarRevertidas(aplicadas, marcas, u.AplicadasRevertidas));
        Assert.All(canciones, p => Assert.True(aplicadas.Contains(p)));

        var u2 = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal("run_20261001_120000.jsonl", u2.File);
        Assert.Equal(3, EnrichViewModel.OlvidarRevertidas(aplicadas, marcas, u2.AplicadasRevertidas));
        Assert.Equal(0, aplicadas.Count);
    }

    // Un manifiesto de volumen de antes de la marca (sin el campo «Aplicada») tampoco las devuelve.
    [Fact]
    public async Task Un_manifiesto_antiguo_de_volumen_tampoco()
    {
        var p = Cancion("Bad Bunny - Monaco.mp3", tramas: 30);
        var res = await Aplicar(p, Path.GetFileName(p), Manifiesto("run_20261001_120000.jsonl"), _paths.DoneLog);
        var aplicadas = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        aplicadas.Add(res.FinalPath);
        File.WriteAllText(Manifiesto("run_20261009_210831.jsonl"), SinMarca(Volumen(p, -3)) + "\n");

        var u = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(1, u.Reverted);
        Assert.Empty(u.AplicadasRevertidas);
        Assert.Equal(0, EnrichViewModel.OlvidarRevertidas(aplicadas, new ApplyMarks(Path.Combine(_dir, "m.json")), u.AplicadasRevertidas));
        Assert.True(aplicadas.Contains(p));
    }

    // Una tirada de Enriquecer de antes de la marca sigue devolviendo sus canciones a pendientes.
    [Fact]
    public async Task Un_manifiesto_antiguo_de_Enriquecer_si_las_devuelve()
    {
        var p = Cancion("Bad Bunny - Monaco.mp3");
        var mf = Manifiesto("run_20261001_120000.jsonl");
        await Aplicar(p, Path.GetFileName(p), mf, _paths.DoneLog);
        File.WriteAllLines(mf, File.ReadAllLines(mf).Where(l => l.Trim().Length > 0).Select(SinMarca));

        var u = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(1, u.Reverted);
        Assert.Contains(p, u.AplicadasRevertidas);
    }

    // Incompletas aplica con el mismo motor pero no lleva a aplicadas.json: su deshacer no toca nada.
    [Fact]
    public async Task La_marca_solo_la_pone_quien_da_por_aplicada()
    {
        var p = Cancion("Bad Bunny - Monaco.mp3");
        var q = Cancion("Bad Bunny - Neverita.mp3");
        var enriquecer = Manifiesto("run_20261001_120000.jsonl");
        var incompletas = Manifiesto("run_20261002_120000.jsonl");
        await Aplicar(p, Path.GetFileName(p), enriquecer, _paths.DoneLog);
        await Aplicar(q, Path.GetFileName(q), incompletas, _paths.DoneLog, aplicada: false);

        var lineaE = JsonNode.Parse(File.ReadAllLines(enriquecer)[0])!.AsObject();
        var lineaI = JsonNode.Parse(File.ReadAllLines(incompletas)[0])!.AsObject();
        Assert.True(lineaE[nameof(UndoRecord.Aplicada)]!.GetValue<bool>());
        Assert.False(lineaI[nameof(UndoRecord.Aplicada)]!.GetValue<bool>());   // escrita también en false

        var u = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal("run_20261002_120000.jsonl", u.File);
        Assert.Contains(q, u.Revertidas);
        Assert.Empty(u.AplicadasRevertidas);
    }

    /// <summary>
    /// Un manifiesto antiguo descrito en corto: registros separados por «;», cada uno con sus campos
    /// separados por «,»; «-» es un renombrado sin campos (Renombrar con IA, Bandeja) y «=» un
    /// renombrado de Enriquecer sin cambios de etiquetas («Fields»: {}).
    /// </summary>
    private static List<JsonNode?> Antiguo(string forma)
        => forma.Split(';').Select(r =>
        {
            var rec = new UndoRecord { OrigPath = @"E:\Musica\a.mp3", FinalPath = @"E:\Musica\b.mp3" };
            if (r == "-") rec.Renamed = true;
            else if (r == "=") { rec.Renamed = true; rec.Fields = new(); }
            else rec.Fields = r.Split(',').ToDictionary(c => c, _ => FieldChange.Str("antes", "despues"));
            return JsonNode.Parse(SinMarca(JsonSerializer.Serialize(rec)));
        }).ToList();

    [Theory]
    [InlineData("Title,Artist", true)]           // una tirada de Enriquecer
    [InlineData("Genre,Year", true)]             // el escenario del hallazgo: solo género y año
    [InlineData("=", true)]                      // Enriquecer que solo renombra
    [InlineData("Title;Genre", true)]
    [InlineData("Genre;Genre", false)]           // Unificar géneros
    [InlineData("Bpm;Bpm,Key", false)]           // BPM y clave de rekordbox
    [InlineData("Bpm", false)]                   // rekordbox con la clave ya puesta
    [InlineData("Volumen", false)]               // la pestaña Volumen
    [InlineData("Comment", false)]               // fichas de DJ al comentario
    [InlineData("-;-", false)]                   // Renombrar con IA
    [InlineData("Title;Volumen", false)]         // ante la duda, no
    public void Los_manifiestos_antiguos_se_juzgan_por_lo_que_cambian(string forma, bool deAplicar)
        => Assert.Equal(deAplicar, UndoEngine.ManifiestoAntiguoDeAplicar(Antiguo(forma)));

    // Con la marca, manda la marca: la regla de los antiguos no se aplica.
    [Fact]
    public void Con_la_marca_manda_la_marca()
    {
        var si = JsonSerializer.SerializeToNode(new UndoRecord { OrigPath = "a", FinalPath = "a", Aplicada = true });
        var no = JsonSerializer.SerializeToNode(new UndoRecord { OrigPath = "a", FinalPath = "a", Fields = new() { ["Title"] = FieldChange.Str("x", "y") } });
        var antiguo = Antiguo("Title")[0];

        Assert.True(UndoEngine.DioPorAplicada(si, manifiestoAntiguoDeAplicar: false));
        Assert.False(UndoEngine.DioPorAplicada(no, manifiestoAntiguoDeAplicar: true));
        Assert.True(UndoEngine.DioPorAplicada(antiguo, manifiestoAntiguoDeAplicar: true));
        Assert.False(UndoEngine.DioPorAplicada(antiguo, manifiestoAntiguoDeAplicar: false));
        Assert.False(UndoEngine.ManifiestoAntiguoDeAplicar(new List<JsonNode?> { si, no }));   // ninguno antiguo
    }

    // Lo revertido sale de la lista de Enriquecer, y solo eso. Caso: Unificar géneros cambió el
    // género de una canción con propuesta pendiente; al deshacerlo, su propuesta (calculada con el
    // género de entonces) se retira, y las demás filas se quedan como estaban, con lo editado.
    [Fact]
    public void Deshacer_retira_de_la_lista_solo_las_filas_de_lo_revertido()
    {
        var p = Cancion("Bad Bunny - Monaco.mp3");
        var q = Cancion("Bad Bunny - Neverita.mp3");
        using (var f = TagLib.File.Create(p)) { f.Tag.Genres = new[] { "Reggaeton" }; f.Save(); }
        File.WriteAllText(Manifiesto("run_20261003_120000.jsonl"), JsonSerializer.Serialize(new UndoRecord
        {
            OrigPath = p, FinalPath = p, Renamed = false,
            Fields = new() { ["Genre"] = FieldChange.Arr(Array.Empty<string>(), new[] { "Reggaeton" }) },
        }) + "\n");

        PreviewRow Fila(string ruta)
        {
            var r = new ProcessResult { FilePath = ruta, Old = Path.GetFileName(ruta), New = Path.GetFileName(ruta), Found = true, Source = "Deezer", Score = "12" };
            var fila = new PreviewRow { Result = r, Old = r.Old };
            fila.UpdateFrom(r);
            return fila;
        }
        var filaP = Fila(p);
        var filaQ = Fila(q);
        filaQ.New = "Bad Bunny - NEVERITA (editado).mp3";   // edición de celda sin aplicar
        filaQ.RowStatus = "⚠ baja confianza — revisar";

        var u = new UndoEngine(_paths).UndoLastRun();
        var quitar = EnrichViewModel.FilasRevertidas(new[] { filaP, filaQ }, u.Revertidas);

        Assert.Equal(new[] { filaP }, quitar);
        Assert.Empty(u.AplicadasRevertidas);   // Unificar géneros no da nada por aplicado
        Assert.Equal("Bad Bunny - NEVERITA (editado).mp3", filaQ.New);
        Assert.Equal("⚠ baja confianza — revisar", filaQ.RowStatus);
    }

    [Fact]
    public void Sin_rutas_revertidas_no_se_retira_nada()
    {
        var r = new ProcessResult { FilePath = @"E:\Musica\COMERCIAL\Bad Bunny - Monaco.mp3", Old = "Bad Bunny - Monaco.mp3", Found = true };
        var fila = new PreviewRow { Result = r, Old = r.Old };
        Assert.Empty(EnrichViewModel.FilasRevertidas(new[] { fila }, Array.Empty<string>()));
        // La ruta se compara sin distinguir mayúsculas, como el resto de listas (Windows y macOS).
        Assert.Single(EnrichViewModel.FilasRevertidas(new[] { fila }, new[] { @"e:\musica\comercial\bad bunny - monaco.mp3" }));
    }
}
