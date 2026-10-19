using System.Text.RegularExpressions;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Salvaguardas de la publicación (.github/workflows). No se pueden ejecutar aquí, pero sí
/// comprobar que nadie las quita sin darse cuenta: permisos mínimos, acciones fijadas por commit,
/// sumas SHA-256 y la comprobación de que la etiqueta coincide con AppInfo.Version.
/// </summary>
public class PublicacionTests
{
    /// <summary>La carpeta .github/workflows del repositorio, o null si las pruebas corren fuera de él.</summary>
    private static string? Flujos()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var w = Path.Combine(d.FullName, ".github", "workflows");
            if (Directory.Exists(w)) return w;
        }
        return null;
    }

    private static string Leer(string nombre) => File.ReadAllText(Path.Combine(Flujos()!, nombre)).Replace("\r\n", "\n");

    // Una etiqueta como «v4» se puede mover a otro código; un commit, no.
    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void Las_acciones_van_fijadas_por_commit(string flujo)
    {
        if (Flujos() == null) return;
        var usos = Regex.Matches(Leer(flujo), @"uses:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(usos);
        Assert.All(usos, u => Assert.Matches(@"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$", u));
    }

    // Todo el flujo solo lee; compilar ejecuta código de paquetes de terceros.
    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    public void Por_defecto_solo_se_lee_el_repositorio(string flujo)
    {
        if (Flujos() == null) return;
        Assert.Contains("\npermissions:\n  contents: read\n", Leer(flujo));
    }

    // Y solo el trabajo que crea la Release puede escribir.
    [Fact]
    public void Solo_publicar_escribe()
    {
        if (Flujos() == null) return;
        var release = Leer("release.yml");
        Assert.Single(Regex.Matches(release, @"contents:\s*write"));
        var publicar = release[release.IndexOf("\n  publicar:", StringComparison.Ordinal)..];
        Assert.Contains("contents: write", publicar);
    }

    [Fact]
    public void Se_publican_las_sumas_y_se_comprueba_la_version()
    {
        if (Flujos() == null) return;
        var release = Leer("release.yml");
        Assert.Contains("entregas/SHA256SUMS.txt", release);
        Assert.Contains("GITHUB_REF_NAME#v", release);
        Assert.Contains("Etiquetador.Core/AppInfo.cs", release);
    }

    // La comprobación de la etiqueta lee la versión con esta misma expresión: si AppInfo cambiara de
    // forma, la publicación fallaría siempre. Se comprueba aquí, que sí se ejecuta en cada commit.
    [Fact]
    public void La_version_de_AppInfo_tiene_la_forma_que_busca_la_publicacion()
    {
        if (Flujos() == null) return;
        var appInfo = File.ReadAllText(Path.Combine(Directory.GetParent(Flujos()!)!.Parent!.FullName,
                                                    "EtiquetadorNet", "Etiquetador.Core", "AppInfo.cs"));
        var m = Regex.Match(appInfo, @"Version = ""([0-9]+\.[0-9]+\.[0-9]+)""");
        Assert.True(m.Success);
        Assert.Equal(AppInfo.Version, m.Groups[1].Value);
    }
}
