using Etiquetador.App.Services;

namespace Etiquetador.Tests;

/// <summary>
/// Una sola ventana de BeatTag a la vez. Dos abiertas comparten los archivos de datos y la que
/// guarda la última pisa lo de la otra; visto en el código (hallazgo I6): la segunda arrancaba con
/// fichas-dj.json bloqueado, con 0 fichas, y su primer guardado escribía una sola ficha encima de
/// cientos. Las pruebas usan un nombre propio para no chocar con un BeatTag abierto de verdad.
/// </summary>
public class InstanciaUnicaTests
{
    private static string NombreDePrueba() => "BeatTag.Pruebas." + Guid.NewGuid().ToString("N");

    [Fact]
    public void La_segunda_sabe_que_ya_hay_una_abierta()
    {
        var nombre = NombreDePrueba();
        using var primera = InstanciaUnica.Adquirir(nombre);
        using var segunda = InstanciaUnica.Adquirir(nombre);

        Assert.True(primera.EsLaPrimera);
        Assert.False(segunda.EsLaPrimera);
    }

    [Fact]
    public void Al_cerrarse_la_primera_se_puede_volver_a_abrir()
    {
        var nombre = NombreDePrueba();
        InstanciaUnica.Adquirir(nombre).Dispose();

        using var otra = InstanciaUnica.Adquirir(nombre);
        Assert.True(otra.EsLaPrimera);
    }

    // La que sobra le pide a la abierta que se ponga delante: quien hace doble clic otra vez ve la
    // ventana que ya tenía, en vez de nada.
    [WindowsFact]
    public void La_segunda_hace_que_la_primera_se_ponga_delante()
    {
        var nombre = NombreDePrueba();
        using var primera = InstanciaUnica.Adquirir(nombre);
        using var avisada = new ManualResetEventSlim(false);
        primera.OtraQuiereAbrir += () => avisada.Set();

        using var segunda = InstanciaUnica.Adquirir(nombre);
        Assert.True(segunda.AvisarALaPrimera());

        Assert.True(avisada.Wait(TimeSpan.FromSeconds(5)), "la primera no se enteró");
    }
}

/// <summary>
/// Cerrar con un proceso en marcha (hallazgos M9 y T3): se cancela y se espera a que guarde, pero
/// sin quedarse esperando para siempre a un proceso que no atiende la cancelación.
/// </summary>
public class CerrarConProcesoTests
{
    [Fact]
    public async Task Espera_a_que_el_proceso_cancelado_termine()
    {
        var vueltas = 0;
        var termino = await Etiquetador.App.ViewModels.MainViewModel.EsperarAQueTermineAsync(
            () => ++vueltas < 3, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));
        Assert.True(termino);
    }

    [Fact]
    public async Task No_espera_para_siempre_a_un_proceso_que_no_para()
    {
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var termino = await Etiquetador.App.ViewModels.MainViewModel.EsperarAQueTermineAsync(
            () => true, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        Assert.False(termino);
        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(3));
    }
}
