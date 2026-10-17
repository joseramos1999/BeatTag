using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// config.net.json que no se puede leer. Antes, Load devolvía los valores de fábrica y el primer
/// guardado (al cerrar la ventana, o el automático de las carpetas) escribía encima: se perdían las
/// 43 carpetas, las prioridades y exclusiones de Duplicados y las 4 credenciales cifradas, y en el
/// registro solo quedaba una línea.
/// </summary>
public class ConfiguracionIlegibleTests
{
    // Un secreto DPAPI que no se puede descifrar en este perfil (cabecera buena, contenido basura).
    private const string BlobAjeno = "01000000d08c9ddf00112233445566778899aabbccddeeff";

    private static AppPaths Nueva(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "etq-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new AppPaths(dir);
    }

    private static string[] Apartados(AppPaths paths)
        => Directory.GetFiles(paths.DataDir, "config.net.json.ilegible_*");

    // Un JSON cortado a medias (editado a mano, o un corte al copiarlo).
    [Fact]
    public void Un_json_roto_se_aparta_intacto_y_no_se_pisa()
    {
        var paths = Nueva(out var dir);
        try
        {
            const string roto = """{"Folders":["E:\\Musica\\COMERCIAL","C:\\Users\\joser\\Downloads\\MUSICA"],"PriorityFolders":["E:\\Mus""";
            File.WriteAllText(paths.ConfigPath, roto);

            var cfg = AppConfig.Load(paths, out var error);

            Assert.NotEqual("", error);
            Assert.Equal(error, cfg.AvisoCarga);
            var apartado = Assert.Single(Apartados(paths));
            Assert.Equal(roto, File.ReadAllText(apartado));

            // Guardar ya no destruye nada: el original sigue apartado tal cual.
            cfg.Folders.Add(@"C:\Users\joser\Downloads\MUSICA");
            Assert.True(cfg.Save(paths, out var errGuardar), errGuardar);
            Assert.Equal(roto, File.ReadAllText(apartado));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un ajuste que cambió de tipo al volver a una versión anterior: se descarta ESE ajuste y el
    // resto -las 43 carpetas, las credenciales- se conserva.
    [Fact]
    public void Un_ajuste_con_otro_tipo_no_tira_el_resto()
    {
        var paths = Nueva(out var dir);
        try
        {
            var carpetas = Enumerable.Range(1, 43).Select(i => $"E:\\\\Musica\\\\Carpeta {i:00}").ToList();
            var json = $$"""
            {
              "Folders": [{{string.Join(", ", carpetas.Select(c => "\"" + c + "\""))}}],
              "MinChangeIndex": "tres",
              "ExcludedDupFolders": ["E:\\Musica\\ACAPELLAS"],
              "AcoustIdKeyEnc": "{{BlobAjeno}}",
              "Theme": "oscuro"
            }
            """;
            File.WriteAllText(paths.ConfigPath, json);

            var cfg = AppConfig.Load(paths, out var error);

            Assert.Equal(43, cfg.Folders.Count);
            Assert.Equal(@"E:\Musica\Carpeta 01", cfg.Folders[0]);
            Assert.Equal(new[] { @"E:\Musica\ACAPELLAS" }, cfg.ExcludedDupFolders);
            Assert.Equal("oscuro", cfg.Theme);
            Assert.Equal(3.0, cfg.MinChangeIndex);              // el que no se entendía, de fábrica
            Assert.Contains("MinChangeIndex", error);
            Assert.Single(Apartados(paths));                     // el original, apartado

            Assert.True(cfg.Save(paths, out var errGuardar), errGuardar);
            var guardado = File.ReadAllText(paths.ConfigPath);
            Assert.Contains("Carpeta 43", guardado);
            Assert.Contains(BlobAjeno, guardado);                // la credencial ilegible, intacta
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si no se puede ni apartar (otro programa lo tiene abierto), no se escribe encima en toda la
    // sesión: más vale perder los cambios de hoy que la configuración de siempre.
    [WindowsFact]
    public void Si_no_se_puede_apartar_no_se_guarda_encima()
    {
        var paths = Nueva(out var dir);
        try
        {
            const string roto = """{"Folders":["E:\\Musica"],""";
            File.WriteAllText(paths.ConfigPath, roto);

            AppConfig cfg;
            using (new FileStream(paths.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                cfg = AppConfig.Load(paths, out _);

            Assert.True(cfg.GuardadoBloqueado);
            Assert.False(cfg.Save(paths, out var error));
            Assert.NotEqual("", error);
            Assert.Equal(roto, File.ReadAllText(paths.ConfigPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // La lectura temprana del tema (antes de la pantalla de carga) no puede apartar el archivo: el
    // aviso se perdería y el motor ya no se enteraría de nada.
    [Fact]
    public void Leer_el_aspecto_no_aparta_ni_descifra()
    {
        var paths = Nueva(out var dir);
        try
        {
            const string roto = """{"Theme":"oscuro","CompactRows":true,"Folders":[""";
            File.WriteAllText(paths.ConfigPath, roto);

            var aspecto = AppConfig.LeerAspecto(paths);

            Assert.Equal("sistema", aspecto.Theme);   // ilegible: el de fábrica
            Assert.Equal(roto, File.ReadAllText(paths.ConfigPath));
            Assert.Empty(Apartados(paths));

            File.WriteAllText(paths.ConfigPath, $$"""{"Theme":"oscuro","CompactRows":true,"AcoustIdKeyEnc":"{{BlobAjeno}}"}""");
            aspecto = AppConfig.LeerAspecto(paths);
            Assert.Equal("oscuro", aspecto.Theme);
            Assert.True(aspecto.CompactRows);
            Assert.False(aspecto.SecretsUnreadable);   // ni siquiera ha mirado las credenciales
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // La salida explícita para credenciales de otro equipo que nunca se podrán leer aquí.
    [Fact]
    public void Olvidar_las_credenciales_ilegibles_las_quita_al_guardar()
    {
        var paths = Nueva(out var dir);
        try
        {
            File.WriteAllText(paths.ConfigPath, $$"""{"SpotifySecretEnc":"{{BlobAjeno}}","Folders":["E:\\Musica"]}""");
            var cfg = AppConfig.Load(paths);
            Assert.Equal(new[] { "SpotifySecretEnc" }, cfg.CredencialesIlegibles);

            cfg.OlvidarCredencialesIlegibles();
            Assert.False(cfg.SecretsUnreadable);
            Assert.True(cfg.Save(paths, out var error), error);
            Assert.DoesNotContain(BlobAjeno, File.ReadAllText(paths.ConfigPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si el usuario escribe una credencial nueva, esa manda: la ilegible ya no se conserva.
    [DpapiFact]
    public void Una_credencial_nueva_sustituye_a_la_ilegible()
    {
        var paths = Nueva(out var dir);
        try
        {
            File.WriteAllText(paths.ConfigPath, $$"""{"AcoustIdKeyEnc":"{{BlobAjeno}}"}""");
            var cfg = AppConfig.Load(paths);
            Assert.True(cfg.SecretsUnreadable);

            cfg.AcoustIdKey = "clave-nueva";
            Assert.False(cfg.SecretsUnreadable);
            Assert.True(cfg.Save(paths, out var error), error);

            Assert.DoesNotContain(BlobAjeno, File.ReadAllText(paths.ConfigPath));
            Assert.Equal("clave-nueva", AppConfig.Load(paths).AcoustIdKey);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Lo que la pantalla de Ajustes hace al guardar: vuelve a asignar los textos tal como se ven. Con
    // la credencial ilegible, ese texto está vacío, y eso NO puede borrar el original.
    [Fact]
    public void Reasignar_la_credencial_vacia_no_borra_la_ilegible()
    {
        var paths = Nueva(out var dir);
        try
        {
            File.WriteAllText(paths.ConfigPath, $$"""{"DiscogsTokenEnc":"{{BlobAjeno}}"}""");
            var cfg = AppConfig.Load(paths);

            cfg.DiscogsToken = "".Trim();   // como SettingsViewModel
            Assert.True(cfg.Save(paths, out var error), error);

            Assert.Contains(BlobAjeno, File.ReadAllText(paths.ConfigPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
