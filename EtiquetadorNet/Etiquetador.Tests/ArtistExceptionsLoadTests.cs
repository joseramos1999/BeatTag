using System.Text.Json;
using Etiquetador.Core;

namespace Etiquetador.Tests;

public class ArtistExceptionsLoadTests
{
    [Fact]
    public void Carga_grafias_personalizadas_del_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-artex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "ArtistsExceptions.json");
        try
        {
            File.WriteAllText(json, JsonSerializer.Serialize(new[] { "eLeGaNtE JR" }));
            var exc = ArtistExceptions.Load(json);
            Assert.Equal("eLeGaNtE JR", exc.NormalizeArtists("ELEGANTE JR"));   // usa la grafía del usuario
            Assert.Equal("DJ Snake", exc.NormalizeArtists("DJ SNAKE"));         // y sigue la lista por defecto
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Crea_archivo_de_arranque_si_no_existe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-artex-" + Guid.NewGuid().ToString("N"));
        var json = Path.Combine(dir, "ArtistsExceptions.json");
        try
        {
            var exc = ArtistExceptions.Load(json);
            Assert.True(File.Exists(json));   // se crea la plantilla
            Assert.Contains("DJ Snake", File.ReadAllText(json));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
