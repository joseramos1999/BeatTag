using System.Reflection;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// La version vive en dos sitios: AppInfo.Version (lo que enseña la aplicacion y lo que viaja en el
/// User-Agent hacia MusicBrainz y compania) y la del ejecutable (lo que ve Windows al mirar las
/// propiedades del archivo). Publicar con las dos descuadradas es facil y no avisa nadie: durante
/// varias versiones el .exe declaro 1.0.0 mientras la aplicacion decia otra cosa.
/// </summary>
public class VersionTests
{
    [Fact]
    public void El_ejecutable_declara_la_misma_version_que_la_aplicacion()
    {
        var ensamblado = typeof(App.ViewModels.LoudnessRow).Assembly;
        var version = ensamblado.GetName().Version;

        Assert.NotNull(version);
        Assert.Equal(AppInfo.Version, $"{version!.Major}.{version.Minor}.{version.Build}");
    }

    // El User-Agent lo leen servicios de terceros: tiene que llevar nombre y version, sin espacios
    // raros ni quedarse a medias.
    [Fact]
    public void El_user_agent_lleva_nombre_y_version()
    {
        Assert.Equal($"BeatTag/{AppInfo.Version}", AppInfo.UserAgent);
        Assert.Contains(AppInfo.Version, AppInfo.MusicBrainzUserAgent);
        Assert.Contains("https://", AppInfo.MusicBrainzUserAgent);   // MusicBrainz exige contacto
    }

    [Fact]
    public void La_version_tiene_forma_de_version()
        => Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
}
