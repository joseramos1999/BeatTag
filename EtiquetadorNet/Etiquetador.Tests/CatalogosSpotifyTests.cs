using System.Net;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// C11 · Estado de Spotify que no se reiniciaba: el token sobrevivía a un cambio de credenciales,
/// el cortacircuitos de 429 leía errores de otros servidores y el bloqueo duraba hasta cerrar.
/// </summary>
public class CatalogosSpotifyTests
{
    private const string Token = "accounts.spotify.com/api/token";
    private const string Busqueda = "api.spotify.com/v1/search";

    private static string TokenJson(string t) => $"{{\"access_token\":\"{t}\",\"token_type\":\"Bearer\",\"expires_in\":3600}}";

    [Fact]
    public async Task Cambiar_las_credenciales_pide_un_token_nuevo()
    {
        // Escenario: se cambia el Client ID en Ajustes y «Probar» respondía OK con el token viejo.
        using var m = new MontajeApi();
        m.Red.Cuando(Token, (n, req, _) => Task.FromResult(RedFalsa.Respuesta(req, TokenJson("tok" + n))));
        var sp = new SpotifyProvider(m.Api);

        Assert.Equal("tok1", await sp.GetTokenAsync("id-a", "secreto-a"));
        Assert.Equal("tok1", await sp.GetTokenAsync("id-a", "secreto-a"));   // mismas: se reutiliza
        Assert.Equal("tok2", await sp.GetTokenAsync("id-b", "secreto-a"));   // otro id: nuevo
        Assert.Equal(2, m.Red.Cuantas(Token));
    }

    [Fact]
    public async Task Credenciales_nuevas_y_malas_no_devuelven_el_token_anterior()
    {
        using var m = new MontajeApi();
        m.Red.Secuencia(Token, (HttpStatusCode.OK, TokenJson("bueno")), (HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\"}"));
        var sp = new SpotifyProvider(m.Api);

        Assert.Equal("bueno", await sp.GetTokenAsync("id", "secreto"));
        Assert.Null(await sp.GetTokenAsync("id", "secreto-mal-copiado"));
    }

    [Fact]
    public async Task Forzar_pide_uno_nuevo_aunque_el_guardado_valga()
    {
        using var m = new MontajeApi();
        m.Red.Cuando(Token, (n, req, _) => Task.FromResult(RedFalsa.Respuesta(req, TokenJson("tok" + n))));
        var sp = new SpotifyProvider(m.Api);
        await sp.GetTokenAsync("id", "secreto");
        Assert.Equal("tok2", await sp.GetTokenAsync("id", "secreto", forzarNuevo: true));
    }

    [Fact]
    public async Task Un_429_de_otro_servidor_no_cuenta_como_de_Spotify()
    {
        // LastApiError lo comparten todos los servidores y no se limpia tras un acierto.
        using var m = new MontajeApi();
        m.Red.Responde(Token, TokenJson("tok")).Responde(Busqueda, "{\"tracks\":{\"items\":[]}}");
        var sp = new SpotifyProvider(m.Api);
        m.Api.LastApiError = "HTTP 429 Too Many Requests";   // de MusicBrainz, hace un rato

        for (var i = 0; i < SpotifyProvider.LimiteDe429 + 2; i++)
            Assert.Null(await sp.SearchAsync("Artista", $"Edicion de DJ {i}", "id", "secreto", false, false));

        Assert.Equal(0, sp.Sp429);
        Assert.False(sp.SpBlocked);
    }

    [Fact]
    public async Task Los_429_propios_bloquean_un_rato_y_se_puede_reiniciar()
    {
        using var m = new MontajeApi();
        m.Red.Responde(Token, TokenJson("tok")).Responde(Busqueda, "{}", HttpStatusCode.TooManyRequests);
        var sp = new SpotifyProvider(m.Api);

        for (var i = 0; i < SpotifyProvider.LimiteDe429; i++)
            await sp.SearchAsync("Artista", $"Tema {i}", "id", "secreto", false, false);

        Assert.True(sp.SpBlocked);
        // Con un 429 no se gastan las otras dos consultas de cada búsqueda.
        Assert.Equal(SpotifyProvider.LimiteDe429 * 2, m.Red.Cuantas(Busqueda));   // 1 consulta × 2 intentos
        Assert.Contains("429", sp.SpDiag);

        sp.ReiniciarBloqueo();
        Assert.False(sp.SpBlocked);
        Assert.Equal(0, sp.Sp429);
    }
}
