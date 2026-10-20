using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Hallazgo A14 en la página de Duplicados: las otras ediciones del mismo tema se enseñan en el grupo,
/// distinguidas de las copias (columna Edición) y NUNCA se marcan como sobrantes: una Extended y su
/// Original no son duplicados para un DJ. «Excluir ediciones de DJ» sigue dejando fuera las ediciones
/// para pinchar, también como «Otra edición».
/// </summary>
public class DuplicadosEdicionesTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Track Pista(string ruta, int kbps, int segundos)
        => new() { FilePath = ruta, Folder = @"E:\Musica", Bitrate = kbps, DurationSeconds = segundos };

    private static (List<DupRow> Filas, int ConPrioridad) Filas(IReadOnlyList<DuplicateGroup> grupos,
                                                                 IReadOnlyDictionary<string, AlineacionAudio>? alineacion,
                                                                 bool porAudio = true)
        => DuplicatesViewModel.ConstruirFilas(grupos, porAudio,
            puntuar: t => t.Bitrate * 1000.0 + t.DurationSeconds,
            motivoDe: t => $"Mejor calidad ({t.Bitrate} kbps)",
            esPrioritaria: _ => false,
            alineacion: alineacion);

    // Original en dos copias, una Extended y otra edición sin descriptor en el nombre: la copia peor
    // del Original es la única sobrante. Las otras dos son las únicas de su edición y se conservan,
    // diciendo por qué.
    [Fact]
    public void Cada_edicion_conserva_su_copia_y_solo_sobran_las_copias_de_la_misma()
    {
        var o1 = Pista(@"E:\Musica\Gasolina.mp3", 320, 193);
        var o2 = Pista(@"E:\Musica\Gasolina [pool].mp3", 192, 193);
        var ext = Pista(@"E:\Musica\Gasolina (Extended).mp3", 256, 226);
        var otra = Pista(@"E:\Musica\Gasolina 2005.mp3", 128, 199);
        var grupos = new[] { new DuplicateGroup("fp:Gasolina", "Daddy Yankee", "Gasolina", new[] { o1, o2, ext, otra }) };
        var alineacion = new Dictionary<string, AlineacionAudio>(StringComparer.OrdinalIgnoreCase)
        {
            [o1.FilePath] = new(7, 0),
            [o2.FilePath] = new(7, 0.1),
            [ext.FilePath] = new(9, 32.2),
            [otra.FilePath] = new(11, 5.1),
        };

        var (filas, _) = Filas(grupos, alineacion);

        Assert.All(filas, f => Assert.Equal("Daddy Yankee - Gasolina   (4 archivos · 3 ediciones)", f.Group));
        var fo1 = filas.Single(f => f.FilePath == o1.FilePath);
        var fo2 = filas.Single(f => f.FilePath == o2.FilePath);
        var fext = filas.Single(f => f.FilePath == ext.FilePath);
        var fotra = filas.Single(f => f.FilePath == otra.FilePath);
        Assert.True(fo1.EsMejor);
        Assert.False(fo2.EsMejor);
        Assert.True(fext.EsMejor);
        Assert.True(fotra.EsMejor);
        Assert.Equal("Mejor calidad (320 kbps)", fo1.Motivo);
        Assert.Equal("Se conserva (Extended)", fext.Motivo);
        Assert.Equal("Única de su edición", fotra.Motivo);
        Assert.Equal(EdicionesPorAudio.Referencia, fo1.Edicion);
        Assert.Equal(EdicionesPorAudio.Referencia, fo2.Edicion);
        Assert.Equal("Otra edición: empieza 32 s después", fext.Edicion);
        Assert.Equal("Otra edición: empieza 5 s después", fotra.Edicion);

        // «Marcar sobrantes» marca lo que no es la mejor de su edición: solo la copia peor del Original.
        Assert.Equal(new[] { o2.FilePath }, filas.Where(f => !f.EsMejor).Select(f => f.FilePath));

        // Marcar a mano la Extended la deja sin ninguna copia: el aviso de la papelera lo cuenta.
        fext.Marcada = true;
        Assert.Equal(1, DuplicatesViewModel.SinNingunaCopia(filas));
        Assert.Contains("Hay 1 canción con TODAS sus copias marcadas", DuplicatesViewModel.CuerpoConfirmarPapelera(filas));
    }

    // Sin datos de edición (o con otros criterios) todo sigue como antes.
    [Fact]
    public void Sin_ediciones_el_grupo_son_copias_como_siempre()
    {
        var a = Pista(@"E:\Musica\a.mp3", 320, 200);
        var b = Pista(@"E:\Musica\b.mp3", 128, 200);
        var grupos = new[] { new DuplicateGroup("fp:a", "Quevedo", "Columbia", new[] { a, b }) };

        foreach (var (alineacion, porAudio) in new (IReadOnlyDictionary<string, AlineacionAudio>?, bool)[]
                 {
                     (null, true), (null, false),
                     (new Dictionary<string, AlineacionAudio> { [a.FilePath] = new(1, 0), [b.FilePath] = new(1, 0) }, true),
                 })
        {
            var (filas, _) = Filas(grupos, alineacion, porAudio);
            Assert.All(filas, f => Assert.Equal("Quevedo - Columbia   (2 copias)", f.Group));
            Assert.All(filas, f => Assert.Equal("", f.Edicion));
            Assert.Equal("Mejor calidad (320 kbps)", filas.Single(f => f.EsMejor).Motivo);
        }
    }

    [Fact]
    public void El_estado_dice_cuantos_grupos_reunen_otras_ediciones()
    {
        Assert.Equal("12 grupos con el mismo audio · 30 archivos implicados · 4 con otras ediciones del mismo tema.",
                     DuplicatesViewModel.TextoPorAudio(12, 30, 4));
        Assert.Equal("1 grupo con el mismo audio · 2 archivos implicados.", DuplicatesViewModel.TextoPorAudio(1, 2, 0));
    }

    // ---------- El camino de verdad: la página comparando huellas guardadas ----------

    private static int[] Huella(int semilla, int n = 960)
    {
        var r = new Random(semilla);
        var f = new int[n];
        for (var i = 0; i < n; i++) f[i] = r.Next(int.MinValue, int.MaxValue);
        return f;
    }

    /// <summary>
    /// Una biblioteca ya escaneada (de una carpeta vacía) con estas canciones dentro, y una caché de
    /// huellas con su fecha y tamaño de verdad, que es lo que comprueba el escáner antes de darlas.
    /// </summary>
    private async Task<(LibraryStore Store, AppConfig Config, FingerprintScanner Huellas)> BibliotecaAsync(
        params (string Nombre, int Kbps, int Segundos, int[] F)[] canciones)
    {
        var raiz = Path.Combine(_dir, "biblioteca");
        var audio = Path.Combine(_dir, "audio");
        Directory.CreateDirectory(raiz);
        Directory.CreateDirectory(audio);
        var config = new AppConfig { Folders = new() { raiz } };
        var store = new LibraryStore(config, () => { }, new ScanCache(Path.Combine(_dir, "scan.json")));
        await store.ScanAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(store.IsScanned);

        var cache = new Dictionary<string, object>();
        foreach (var (nombre, kbps, segundos, f) in canciones)
        {
            var ruta = Path.Combine(audio, nombre);
            File.WriteAllBytes(ruta, new byte[64 + nombre.Length]);
            var fi = new FileInfo(ruta);
            cache[ruta] = new { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length, D = (double)segundos, F = f };
            store.Tracks.Add(new Track { FilePath = ruta, Folder = raiz, Bitrate = kbps, DurationSeconds = segundos });
        }
        var archivo = Path.Combine(_dir, "huellas.json");
        File.WriteAllText(archivo, JsonSerializer.Serialize(cache));
        return (store, config, new FingerprintScanner(archivo, Path.Combine(_dir, "no-hay-fpcalc.exe")));
    }

    [Fact]
    public async Task Por_audio_la_extended_sale_como_otra_edicion_y_marcar_sobrantes_no_la_toca()
    {
        var tema = Huella(1);
        var intro = Huella(2, 258);   // 32 s de intro
        var (store, config, huellas) = await BibliotecaAsync(
            ("Gasolina.mp3", 320, 193, tema),
            ("Gasolina [pool].mp3", 192, 193, tema[1..]),
            ("Gasolina (Extended).mp3", 320, 226, intro.Concat(tema).Take(960).ToArray()));

        await UnSoloHilo.Ejecutar(async () =>
        {
            var vm = new DuplicatesViewModel(store, config, () => { }, huellas, new AudioPreview());
            vm.SelectedMode = vm.ModeOptions.Single(m => m.Value == DuplicateMode.Fingerprint);
            await UnSoloHilo.HastaQue(() => vm.Status.Contains("con el mismo audio") && !vm.IsBusy, "la comparación por audio");

            Assert.Equal("1 grupo con el mismo audio · 3 archivos implicados · 1 con otras ediciones del mismo tema.", vm.Status);
            Assert.Equal(3, vm.Rows.Count);
            var ext = vm.Rows.Single(r => r.FileName == "Gasolina (Extended).mp3");
            Assert.Equal("Otra edición: empieza 32 s después", ext.Edicion);
            Assert.Equal(EdicionesPorAudio.Referencia, vm.Rows.Single(r => r.FileName == "Gasolina.mp3").Edicion);

            vm.MarkAllButBestCommand.Execute(null);
            Assert.Equal(1, vm.Marcadas);
            Assert.Equal("Gasolina [pool].mp3", vm.Rows.Single(r => r.Marcada).FileName);
            Assert.False(ext.Marcada);
            Assert.Equal("1 copia marcada. Se conserva una de cada canción y de cada edición.", vm.Status);
        });
    }

    // «Excluir ediciones de DJ» deja fuera las ediciones para pinchar también como «Otra edición»:
    // la Hype Intro desaparece del grupo y quedan solo las copias. Las Extended no son edición de DJ.
    [Fact]
    public async Task Excluir_ediciones_de_dj_las_quita_tambien_como_otra_edicion()
    {
        var tema = Huella(3);
        var (store, config, huellas) = await BibliotecaAsync(
            ("Columbia.mp3", 320, 186, tema),
            ("Columbia [pool].mp3", 192, 186, tema[2..]),
            ("Columbia (Hype Intro).mp3", 320, 202, Huella(4, 130).Concat(tema).Take(960).ToArray()));

        await UnSoloHilo.Ejecutar(async () =>
        {
            var vm = new DuplicatesViewModel(store, config, () => { }, huellas, new AudioPreview());
            vm.SelectedMode = vm.ModeOptions.Single(m => m.Value == DuplicateMode.Fingerprint);
            await UnSoloHilo.HastaQue(() => vm.Status.Contains("con el mismo audio") && !vm.IsBusy, "la comparación por audio");
            Assert.Equal(3, vm.Rows.Count);
            Assert.Equal("Otra edición: empieza 16 s después", vm.Rows.Single(r => r.FileName.Contains("Hype")).Edicion);

            vm.Status = "";
            vm.ExcluirEdicionesDj = true;
            await UnSoloHilo.HastaQue(() => vm.Status.Contains("con el mismo audio") && !vm.IsBusy, "la comparación sin ediciones de DJ");
            Assert.Equal(2, vm.Rows.Count);
            Assert.DoesNotContain(vm.Rows, r => r.FileName.Contains("Hype"));
            Assert.All(vm.Rows, r => Assert.Equal("", r.Edicion));
            Assert.True(config.DupExcluirEdicionesDj);
        });
    }
}
