using System.Diagnostics;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// C9 · El limitador por servidor sustituye a la espera fija que se pagaba ENTERA antes de cada
/// petición. MEDIDO en beattag_20260922_162643.log: 195 canciones en 7 min 15 s con 371
/// peticiones a Deezer y 207 a Discogs; 371 × 0,35 s + 207 × 0,9 s = 316 s de espera pura frente a
/// 110 s de latencia. Las pruebas usan un reloj simulado: no dependen de la red ni de lo rápida que
/// sea la máquina.
/// </summary>
public class CatalogosLimitadorTests
{
    private static readonly LimiteServidor Deezer = ApiClient.LimitePara("https://api.deezer.com/search?q=x").Limite;
    private static readonly LimiteServidor MusicBrainz = ApiClient.LimitePara("https://musicbrainz.org/ws/2/recording").Limite;

    [Fact]
    public async Task Si_ya_paso_el_intervalo_no_se_espera_nada()
    {
        // La espera fija esperaba 1,1 s aunque la anterior a MusicBrainz fuera de hacía un minuto.
        var reloj = new RelojFalso();
        var lim = reloj.Limitador();
        await lim.EsperarTurnoAsync("musicbrainz.org", MusicBrainz);
        reloj.Avanzar(TimeSpan.FromSeconds(3));   // otras fuentes, latencia…
        var espera = await lim.EsperarTurnoAsync("musicbrainz.org", MusicBrainz);
        Assert.Equal(TimeSpan.Zero, espera);
        Assert.Empty(reloj.Esperas);
    }

    [Fact]
    public async Task Solo_se_espera_lo_que_falta_desde_la_anterior()
    {
        var reloj = new RelojFalso();
        var lim = reloj.Limitador();
        await lim.EsperarTurnoAsync("musicbrainz.org", MusicBrainz);
        reloj.Avanzar(TimeSpan.FromMilliseconds(400));   // la petición tardó 400 ms
        var espera = await lim.EsperarTurnoAsync("musicbrainz.org", MusicBrainz);
        Assert.Equal(TimeSpan.FromMilliseconds(700), espera);   // 1100 - 400, no 1100
    }

    [Fact]
    public async Task Deezer_deja_45_seguidas_y_frena_la_46()
    {
        // Deezer publica 50 cada 5 s; se usan 45. Las primeras van sin esperar.
        var reloj = new RelojFalso();
        var lim = reloj.Limitador();
        for (var i = 0; i < 45; i++)
        {
            await lim.EsperarTurnoAsync("api.deezer.com", Deezer);
            reloj.Avanzar(TimeSpan.FromMilliseconds(20));
        }
        Assert.Empty(reloj.Esperas);

        var espera = await lim.EsperarTurnoAsync("api.deezer.com", Deezer);
        // La primera empezó hace 45 × 20 ms = 900 ms: faltan 4,1 s para que salga de la ventana.
        Assert.Equal(TimeSpan.FromMilliseconds(4100), espera);
    }

    [Fact]
    public async Task Nunca_hay_mas_de_N_peticiones_en_ninguna_ventana()
    {
        var reloj = new RelojFalso();
        var lim = reloj.Limitador();
        var inicios = new List<TimeSpan>();
        for (var i = 0; i < 200; i++)
        {
            await lim.EsperarTurnoAsync("api.deezer.com", Deezer);
            inicios.Add(reloj.Ahora);
            reloj.Avanzar(TimeSpan.FromMilliseconds(7));
        }
        for (var i = 0; i < inicios.Count; i++)
        {
            var enVentana = inicios.Count(t => t >= inicios[i] && t < inicios[i] + Deezer.Ventana);
            Assert.True(enVentana <= Deezer.Peticiones, $"{enVentana} peticiones en 5 s desde la {i}");
        }
    }

    [Fact]
    public async Task Cada_servidor_lleva_su_propia_cuenta()
    {
        var reloj = new RelojFalso();
        var lim = reloj.Limitador();
        await lim.EsperarTurnoAsync("musicbrainz.org", MusicBrainz);
        // Que MusicBrainz esté en su segundo de espera no retrasa a Deezer.
        Assert.Equal(TimeSpan.Zero, await lim.EsperarTurnoAsync("api.deezer.com", Deezer));
    }

    [Fact]
    public async Task Con_varias_canciones_en_paralelo_se_respeta_el_ritmo()
    {
        // Con el reloj real y márgenes amplios: 6 tareas a la vez contra un servidor de 1 cada
        // 100 ms. Sin un turno por servidor saldrían todas juntas.
        var lim = new LimitadorPeticiones();
        var limite = LimiteServidor.Espaciado(100);
        var sw = Stopwatch.StartNew();
        var inicios = new System.Collections.Concurrent.ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await lim.EsperarTurnoAsync("servidor.test", limite);
            inicios.Add(sw.ElapsedMilliseconds);
        })));
        var orden = inicios.OrderBy(x => x).ToList();
        // 6 peticiones separadas 100 ms: la última no puede empezar antes de ~500 ms.
        Assert.True(orden[^1] >= 450, $"la última empezó a los {orden[^1]} ms");
        for (var i = 1; i < orden.Count; i++)
            Assert.True(orden[i] - orden[i - 1] >= 80, $"dos peticiones a {orden[i] - orden[i - 1]} ms");
    }

    [Fact]
    public async Task Cancelar_mientras_se_espera_sale_y_no_deja_el_turno_tomado()
    {
        var lim = new LimitadorPeticiones();
        var limite = LimiteServidor.Espaciado(60_000);   // un minuto entre peticiones
        await lim.EsperarTurnoAsync("lento.test", limite);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lim.EsperarTurnoAsync("lento.test", limite, cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 10_000, "cancelar debe salir enseguida, no al acabar la espera");

        // El turno quedó libre: con un límite que ya no obliga a esperar, entra a la primera. Si la
        // espera cancelada se hubiera quedado con el turno, esto se colgaría hasta los 5 s.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var espera = await lim.EsperarTurnoAsync("lento.test", new LimiteServidor(1, TimeSpan.FromMilliseconds(1)), cts2.Token);
        Assert.Equal(TimeSpan.Zero, espera);
    }

    [Fact]
    public async Task ApiClient_no_espera_entre_dos_peticiones_a_servidores_distintos()
    {
        // Como en la tirada medida: Deezer, Discogs, Deezer… Antes cada una pagaba su espera fija.
        using var m = new MontajeApi();
        m.Red.Responde("api.deezer.com", "{\"data\":[]}")
             .Responde("api.discogs.com", "{\"results\":[]}");
        await m.Api.GetAsync("https://api.deezer.com/search?q=a");
        await m.Api.GetAsync("https://api.discogs.com/database/search?q=a");
        await m.Api.GetAsync("https://api.deezer.com/search?q=b");
        Assert.Equal(TimeSpan.Zero, m.Reloj.EsperaTotal);
    }

    [Fact]
    public void Discogs_con_token_tiene_mas_margen_que_sin_el()
    {
        var con = ApiClient.LimitePara("https://api.discogs.com/database/search?q=x", conToken: true);
        var sin = ApiClient.LimitePara("https://api.discogs.com/database/search?q=x", conToken: false);
        Assert.Equal(55, con.Limite.Peticiones);   // publica 60/min con token
        Assert.Equal(22, sin.Limite.Peticiones);   // y 25/min sin él
        Assert.NotEqual(con.Servidor, sin.Servidor);
    }

    [Theory]
    [InlineData(0, true)]     // throttle 0 en un servidor desconocido: sin límite
    [InlineData(-1, false)]   // por defecto, 200 ms
    [InlineData(300, false)]
    public void Un_servidor_desconocido_usa_la_separacion_que_se_le_pase(int throttleMs, bool sinLimite)
        => Assert.Equal(sinLimite, ApiClient.LimitePara("https://ejemplo.test/x", throttleMs).Limite.SinLimite);
}
