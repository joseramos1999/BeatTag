using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

// Extremo a extremo del orquestador SIN red: rutas "solo limpieza" y "mezcla" no llaman a proveedores.
public class FileProcessorTests
{
    private static FileProcessor NewProcessor(string dir)
    {
        var paths = new AppPaths(dir);
        var api = new ApiClient(paths);
        return new FileProcessor(
            new DeezerProvider(api), new ItunesProvider(api), new SpotifyProvider(api),
            new MusicBrainzProvider(api), new DiscogsProvider(api), new AcoustIdProvider(api),
            new GeminiClient(api), new Fingerprint(paths), new HttpClient(), new ArtistExceptions());
    }

    [Fact]
    public async Task CleanOnly_compone_nombre_sin_red()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "DADDY YANKEE - Gasolina (BRGS 2023).mp3");
            Mp3Fixture.WriteMinMp3(p);
            var proc = NewProcessor(dir);
            var r = await proc.ProcessAsync(p, isAcapella: false, new ProcessOptions { CleanOnly = true });
            Assert.False(r.Found);
            Assert.False(r.Skip);
            Assert.Equal("Limpieza", r.Source);
            Assert.Equal("Daddy Yankee - Gasolina.mp3", r.New);   // UnScream + normalización + pool fuera
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Mezcla_se_salta_sin_tocar_el_archivo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "Don Omar - Taboo Pedro Cabrera Mashup.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var proc = NewProcessor(dir);
            var r = await proc.ProcessAsync(p, isAcapella: false, new ProcessOptions { Deezer = true });
            Assert.True(r.Skip);
            Assert.Equal("Mezcla", r.Source);
            Assert.Equal(r.Old, r.New);   // no se renombra
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // --- Búsqueda manual (Reanalizar…) ---

    [Fact]
    public async Task Busqueda_manual_no_descarta_como_mezcla()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            // Mismo nombre que el test anterior (se saltaría por "Mashup"), pero el usuario dicta qué buscar.
            var p = Path.Combine(dir, "Don Omar - Taboo Pedro Cabrera Mashup.mp3");
            Mp3Fixture.WriteMinMp3(p);
            var proc = NewProcessor(dir);
            var r = await proc.ProcessAsync(p, isAcapella: false, new ProcessOptions
            {
                CleanOnly = true,   // sin red
                SearchArtist = "Don Omar",
                SearchTitle = "Taboo",
            });
            Assert.False(r.Skip);
            Assert.NotEqual("Mezcla", r.Source);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Busqueda_manual_manda_sobre_el_nombre_del_archivo()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var p = Path.Combine(dir, "TRACK01_BRGS_ripped.mp3");   // nombre inservible
            Mp3Fixture.WriteMinMp3(p);
            var proc = NewProcessor(dir);
            var r = await proc.ProcessAsync(p, isAcapella: false, new ProcessOptions
            {
                CleanOnly = true,
                SearchArtist = "Daft Punk",
                SearchTitle = "One More Time",
            });
            Assert.Contains("Daft Punk", r.New);
            Assert.Contains("One More Time", r.New);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Signature_ignora_los_terminos_manuales()
    {
        // Los términos NO entran en la firma: el resultado de una búsqueda manual se guarda en la
        // caché como la propuesta buena del archivo y se reutiliza en los análisis normales.
        var a = new ProcessOptions { Deezer = true };
        var b = new ProcessOptions { Deezer = true, SearchArtist = "Daft Punk", SearchTitle = "Da Funk" };
        Assert.Equal(a.Signature(), b.Signature());
    }
}
