using System.Text.Json;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E8 · Tras «Deshacer última», las canciones que solo cambiaron etiquetas seguían como aplicadas
/// para siempre. Escenario: se aplican 50 canciones en las que solo cambiaban género y año, no
/// convence, se deshace; las etiquetas vuelven, pero la ruta sigue en aplicadas.json y Analizar las
/// omite como «ya aplicadas». Las renombradas sí volvían, porque su ruta cambia. La única salida era
/// «Olvidar aplicadas», que devolvía también las miles aplicadas de verdad.
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

    private string Cancion(string nombre)
    {
        var musica = Path.Combine(_dir, "musica");
        Directory.CreateDirectory(musica);
        var p = Path.Combine(musica, nombre);
        Mp3Fixture.WriteMinMp3(p);
        Mp3Fixture.SetTags(p, "Monaco", "Bad Bunny", null);
        return p;
    }

    private static Task<ApplyOneResult> Aplicar(string ruta, string nuevo, string manifiesto, string doneLog)
    {
        var info = new ProcessResult
        {
            FilePath = ruta, Old = Path.GetFileName(ruta), New = nuevo,
            Title = "MONACO", Artist = "Bad Bunny", Genre = "Reggaeton", Year = "2023", Found = true,
        };
        return new ApplyEngine().ApplyOneAsync(info, over: true, FieldFlags.All, "keep", null, manifiesto, doneLog);
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
        Assert.Contains(p, u.Revertidas);

        Assert.Equal(1, EnrichViewModel.OlvidarRevertidas(aplicadas, marcas, u.Revertidas));
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
        Assert.Contains(p, u.Revertidas);
        Assert.Contains(res.FinalPath, u.Revertidas);
        Assert.True(File.Exists(p));

        Assert.Equal(1, EnrichViewModel.OlvidarRevertidas(aplicadas, new ApplyMarks(Path.Combine(_dir, "m.json")), u.Revertidas));
        Assert.Equal(0, aplicadas.Count);
    }

    // Lo que el deshacer conserva porque lo cambiaste a mano después NO se revierte: sigue aplicada.
    [Fact]
    public async Task Lo_conservado_por_cambios_manuales_sigue_aplicado()
    {
        var p = Cancion("Bad Bunny - Monaco.mp3");
        var info = new ProcessResult { FilePath = p, Old = Path.GetFileName(p), New = Path.GetFileName(p), Title = "MONACO", Found = true };
        var soloTitulo = new FieldFlags { Title = true, Artist = false, Album = false, Genre = false, Year = false, Bpm = false };
        await new ApplyEngine().ApplyOneAsync(info, over: true, soloTitulo, "keep", null, _paths.NuevoManifiesto(), _paths.DoneLog);
        Mp3Fixture.SetTags(p, "Monaco (mi versión)", null, null);   // el usuario lo retoca después

        var u = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(1, u.Manual);
        Assert.Empty(u.Revertidas);
        var aplicadas = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        aplicadas.Add(p);
        Assert.Equal(0, EnrichViewModel.OlvidarRevertidas(aplicadas, new ApplyMarks(Path.Combine(_dir, "m.json")), u.Revertidas));
        Assert.True(aplicadas.Contains(p));
    }

    // Los manifiestos del formato antiguo (new/orig) también dicen qué rutas se revirtieron.
    [Fact]
    public void El_formato_antiguo_tambien_da_las_rutas()
    {
        var p = Cancion("Bad Bunny - MONACO.mp3");
        var original = Path.Combine(Path.GetDirectoryName(p)!, "pista07.mp3");
        var linea = JsonSerializer.Serialize(new { @new = p, orig = "pista07.mp3", renamed = true });
        File.WriteAllText(Path.Combine(_paths.UndoDir, "run_20250101_120000.jsonl"), linea + "\n");

        var u = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(1, u.Reverted);
        Assert.Contains(original, u.Revertidas);
        Assert.Contains(p, u.Revertidas);
        Assert.True(File.Exists(original));
    }
}
