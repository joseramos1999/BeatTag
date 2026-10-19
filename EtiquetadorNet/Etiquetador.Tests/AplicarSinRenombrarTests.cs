using Etiquetador.App.ViewModels;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E7 · No encontradas e Incompletas daban por aplicada una canción aunque su renombrado hubiera
/// fallado. Escenario real: se acepta la sugerencia de la IA para «track07_final_master.mp3»
/// mientras el Explorador, rekordbox o el antivirus lo tienen abierto; se escriben artista y título,
/// File.Move falla con «being used by another process», la fila desaparece, el estado dice
/// «Aplicada la sugerencia» y la canción queda en aplicadas.json con su nombre viejo.
///
/// MEDIDO: TagLib abre el archivo en exclusiva para escribir, así que con el archivo YA abierto por
/// otro programa fallan las etiquetas, no el renombrado. Lo que deja las etiquetas escritas y el
/// nombre sin cambiar es que algo lo abra justo después de escribirlas (el antivirus revisando el
/// archivo recién modificado). Aquí se provoca de forma reproducible con una carpeta que se llama
/// como el nombre propuesto: RenameSafety solo mira si hay un ARCHIVO con ese nombre, así que el
/// destino se acepta y File.Move falla después de escribir las etiquetas.
/// </summary>
public class AplicarSinRenombrarTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<(ApplyOneResult Res, string Ruta)> AplicarConRenombradoFallido()
    {
        var p = Path.Combine(_dir, "track07_final_master.mp3");
        Mp3Fixture.WriteMinMp3(p);
        Directory.CreateDirectory(Path.Combine(_dir, "Bad Bunny - MONACO.mp3"));   // el destino no se puede ocupar

        var info = new ProcessResult
        {
            FilePath = p, Old = "track07_final_master.mp3", New = "Bad Bunny - MONACO.mp3",
            Artist = "Bad Bunny", Title = "MONACO", Found = true, Source = "IA (aceptada)",
        };
        var campos = new FieldFlags { Title = true, Artist = true, Album = false, Genre = false, Year = false, Bpm = false };
        var res = await new ApplyEngine().ApplyOneAsync(info, over: true, campos, "keep", null,
                                                        Path.Combine(_dir, "run_20261019_120000.jsonl"), Path.Combine(_dir, "done.log"));
        return (res, p);
    }

    [Fact]
    public async Task Etiquetas_escritas_sin_renombrar_no_es_aplicada()
    {
        var (res, p) = await AplicarConRenombradoFallido();

        Assert.True(res.TagOk);           // artista y título, escritos
        Assert.False(res.RenOk);          // el archivo conserva su nombre
        Assert.Equal(p, res.FinalPath);
        Assert.Equal(DesenlaceAplicacion.SinRenombrar, ResultadoAplicacion.Clasificar(res));
        Assert.StartsWith("⚠ etiquetas escritas, pero no se pudo renombrar: ", ResultadoAplicacion.EstadoFila(res));
    }

    // No encontradas: con una sola canción, el estado ya no dice «Aplicada la sugerencia».
    [Fact]
    public async Task No_encontradas_dice_que_no_se_pudo_renombrar()
    {
        var (res, _) = await AplicarConRenombradoFallido();
        var fila = new NotFoundRow { FileName = "track07_final_master.mp3", Suggestion = "Bad Bunny - MONACO" };

        var estado = NotFoundViewModel.ResumenSugerencias(1, hechas: 0, sinRenombrar: 1, fallidas: 0, fila, res);

        Assert.DoesNotContain("Aplicada", estado);
        Assert.Contains("no se pudo renombrar", estado);
        Assert.Contains("Sigue en la lista", estado);
    }

    [Fact]
    public void No_encontradas_cuenta_aparte_las_que_no_se_renombraron()
    {
        var fila = new NotFoundRow { FileName = "a.mp3", Suggestion = "A - B" };
        var estado = NotFoundViewModel.ResumenSugerencias(5, hechas: 3, sinRenombrar: 1, fallidas: 1, fila, null);

        Assert.StartsWith("Aplicadas 3 de 5", estado);
        Assert.Contains("1 sin renombrar", estado);
        Assert.Contains("1 sin poder escribir", estado);
    }

    // Incompletas: antes solo miraba las etiquetas y decía «Actualizada» con el nombre viejo.
    [Fact]
    public async Task Incompletas_dice_que_no_se_pudo_renombrar()
    {
        var (res, _) = await AplicarConRenombradoFallido();

        var estado = IncompleteViewModel.EstadoTrasReanalizar("track07_final_master.mp3", res);

        Assert.StartsWith("Etiquetas actualizadas en «track07_final_master.mp3», pero no se pudo renombrar", estado);
    }

    // Un nombre inválido aborta ANTES de escribir y deja el motivo en RenErr: el estado lo enseña.
    [Fact]
    public void Un_nombre_invalido_es_fallida_con_su_motivo()
    {
        var res = new ApplyOneResult(@"C:\m\a.mp3", false, null, false, "", false, "nombre inválido: contiene una ruta");

        Assert.Equal(DesenlaceAplicacion.Fallida, ResultadoAplicacion.Clasificar(res));
        Assert.Equal("⚠ no se pudo escribir: nombre inválido: contiene una ruta", ResultadoAplicacion.EstadoFila(res));
    }

    [Fact]
    public void Todo_bien_es_aplicada()
    {
        var res = new ApplyOneResult(@"C:\m\B - T.mp3", true, null, true, "", true, "");
        Assert.Equal(DesenlaceAplicacion.Aplicada, ResultadoAplicacion.Clasificar(res));
        Assert.Equal("Actualizada: a.mp3", IncompleteViewModel.EstadoTrasReanalizar("a.mp3", res));
    }
}
