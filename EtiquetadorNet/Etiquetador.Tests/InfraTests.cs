using Etiquetador.Core;

namespace Etiquetador.Tests;

// Pruebas de la FASE 2 (infraestructura): DPAPI, caché/throttle de ApiClient, config round-trip.
public class InfraTests
{
    // --- DPAPI ---
    [Fact]
    public void Dpapi_roundtrip()
    {
        var secret = "mi-clave-secreta-123";
        var enc = Dpapi.Protect(secret);
        Assert.NotEqual(secret, enc);          // se ha cifrado
        Assert.Equal(secret, Dpapi.Unprotect(enc));
    }

    [Fact] public void Dpapi_vacio() => Assert.Equal("", Dpapi.Protect(""));

    // Un blob con la cabecera DPAPI pero ilegible = error criptográfico (NO tratarlo como texto en claro).
    private const string BadDpapiBlob = "01000000d08c9ddf00112233445566778899aabbccddeeff";

    [Fact]
    public void Dpapi_distingue_claro_de_error_criptografico()
    {
        Assert.Equal(UnprotectStatus.Cleartext, Dpapi.TryUnprotect("clave-en-claro-xyz").Status);   // no es hex
        Assert.Equal(UnprotectStatus.Cleartext, Dpapi.TryUnprotect("abcdef0123456789").Status);      // hex pero sin cabecera DPAPI
        Assert.Equal(UnprotectStatus.CryptoError, Dpapi.TryUnprotect(BadDpapiBlob).Status);           // cabecera DPAPI + basura
    }

    [Fact]
    public void Config_con_secreto_ilegible_bloquea_el_guardado()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-cfg-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        Directory.CreateDirectory(dir);
        try
        {
            // Config con un secreto DPAPI ilegible en este perfil.
            File.WriteAllText(paths.ConfigPath, $$"""{"AiKeyEnc":"{{BadDpapiBlob}}"}""");
            var cfg = AppConfig.Load(paths);
            Assert.True(cfg.SecretsUnreadable);
            Assert.False(cfg.Save(paths, out var err));    // no se guarda -> no se pierde el original
            Assert.NotEqual("", err);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Dpapi_texto_en_claro_se_devuelve_tal_cual()   // config heredada sin cifrar
        => Assert.Equal("no-es-hex-dpapi", Dpapi.Unprotect("no-es-hex-dpapi"));

    // --- InferThrottle ---
    [Theory]
    [InlineData("https://musicbrainz.org/ws/2/recording?x", 1100)]
    [InlineData("https://api.discogs.com/database/search?q=x", 1000)]
    [InlineData("https://api.deezer.com/search?q=x", 350)]
    [InlineData("https://api.spotify.com/v1/search?q=x", 150)]
    [InlineData("https://itunes.apple.com/search?term=x", 200)]
    public void InferThrottle_por_dominio(string url, int expected)
        => Assert.Equal(expected, ApiClient.InferThrottle(url));

    // --- CachePath (MD5 hex, subcarpeta de 2 chars, .json) ---
    [Fact]
    public void CachePath_es_determinista_y_con_formato()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "etq-test-" + Guid.NewGuid().ToString("N")));
        using var api = new ApiClient(paths);
        var url = "https://api.deezer.com/search?q=bad%20bunny";
        var cp1 = api.CachePath(url);
        var cp2 = api.CachePath(url);
        Assert.NotNull(cp1);
        Assert.Equal(cp1, cp2);                                  // determinista
        Assert.EndsWith(".json", cp1);
        var file = Path.GetFileNameWithoutExtension(cp1!);
        Assert.Equal(32, file.Length);                           // MD5 = 32 hex
        Assert.Equal(file.Substring(0, 2), Path.GetFileName(Path.GetDirectoryName(cp1)!));  // subcarpeta = 2 primeros
    }

    // --- AppConfig round-trip con secretos cifrados ---
    [Fact]
    public void Config_roundtrip_cifra_secretos_en_disco()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-cfg-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        try
        {
            var cfg = new AppConfig
            {
                Folders = { @"D:\Musica" },
                SpotifyId = "spid-en-claro",
                SpotifySecret = "sp-secreto",
                AiKey = "gemini-secreto",
                AcoustIdKey = "acoustid-secreto",
                UseDeezer = true,
                CoverMode = "png",
            };
            Assert.True(cfg.Save(paths, out var saveErr));
            Assert.Equal("", saveErr);
            Assert.False(File.Exists(paths.ConfigPath + ".tmp"));   // no queda temporal (escritura atómica)

            var raw = File.ReadAllText(paths.ConfigPath);
            Assert.DoesNotContain("sp-secreto", raw);            // el secreto NO aparece en claro
            Assert.DoesNotContain("gemini-secreto", raw);
            Assert.Contains("spid-en-claro", raw);               // el id no secreto sí

            var back = AppConfig.Load(paths);
            Assert.Equal("sp-secreto", back.SpotifySecret);      // se descifra bien
            Assert.Equal("gemini-secreto", back.AiKey);
            Assert.Equal("acoustid-secreto", back.AcoustIdKey);
            Assert.Equal("spid-en-claro", back.SpotifyId);
            Assert.Equal("png", back.CoverMode);
            Assert.Contains(@"D:\Musica", back.Folders);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Config_guardado_fallido_devuelve_error_y_conserva_el_anterior()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq-cfg-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dir);
        try
        {
            var a = new AppConfig { SpotifyId = "config-buena" };
            Assert.True(a.Save(paths, out _));
            var original = File.ReadAllText(paths.ConfigPath);

            // Fuerza un fallo de escritura: creamos una carpeta con el nombre del temporal.
            Directory.CreateDirectory(paths.ConfigPath + ".tmp");
            var b = new AppConfig { SpotifyId = "config-nueva" };
            Assert.False(b.Save(paths, out var err));            // falla y avisa
            Assert.NotEqual("", err);
            Assert.Equal(original, File.ReadAllText(paths.ConfigPath));   // el archivo anterior queda intacto
        }
        finally { try { Directory.Delete(paths.ConfigPath + ".tmp", true); } catch { } try { Directory.Delete(dir, true); } catch { } }
    }
}
