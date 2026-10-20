using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Revisión de integración de la tanda 3 (equipo catalogo): singular y plural en los textos nuevos
/// (interfaz-11), Duplicados ocupada mientras envía a la papelera (rendimiento-1) y la propuesta de
/// Enriquecer de una copia sustituida (interfaz-4).
/// </summary>
public class PulidoCatalogoTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- interfaz-11 · singular y plural

    private static readonly IReadOnlyList<(string, string)> SinFallos = Array.Empty<(string, string)>();

    /// <summary>Un resultado con <paramref name="enviadas"/> archivos enviados y esos fallos.</summary>
    private static EnvioPapeleraResultado Res(int enviadas, IReadOnlyList<(string, string)> fallos, bool cancelado = false)
        => new(Enumerable.Range(0, enviadas).Select(i => $"e{i}.mp3").ToList(), fallos, cancelado);

    [Fact]
    public void El_resumen_de_la_papelera_concuerda_en_numero_y_genero()
    {
        Assert.Equal("Enviado a la papelera: e0.mp3.", EnvioPapelera.Resumen(Res(1, SinFallos), 1, Sustantivo.Descarga));
        Assert.Equal("4 archivos enviados a la papelera.", EnvioPapelera.Resumen(Res(4, SinFallos), 4, Sustantivo.Archivo));
        Assert.Equal("4 descargas enviadas a la papelera.", EnvioPapelera.Resumen(Res(4, SinFallos), 4, Sustantivo.Descarga));
        Assert.Equal("No se ha enviado ningún archivo a la papelera.", EnvioPapelera.Resumen(Res(0, SinFallos), 0, Sustantivo.Archivo));

        var uno = new List<(string, string)> { ("b.mp3", "La unidad no tiene papelera.") };
        Assert.Equal("1 copia enviada a la papelera. No se ha enviado b.mp3: La unidad no tiene papelera.",
                     EnvioPapelera.Resumen(Res(1, uno), 2, Sustantivo.Copia));
        Assert.Equal("1 archivo enviado a la papelera. No se ha enviado b.mp3: La unidad no tiene papelera.",
                     EnvioPapelera.Resumen(Res(1, uno), 2, Sustantivo.Archivo));

        var distintos = new List<(string, string)> { ("b.mp3", "En uso."), ("c.mp3", "Sin permiso.") };
        Assert.Equal("1 archivo enviado a la papelera. 2 no se han enviado. El primero, b.mp3: En uso.",
                     EnvioPapelera.Resumen(Res(1, distintos), 3, Sustantivo.Archivo));
        Assert.Equal("3 descargas enviadas a la papelera. 2 no se han enviado. La primera, b.mp3: En uso.",
                     EnvioPapelera.Resumen(Res(3, distintos), 5, Sustantivo.Descarga));
    }

    [Fact]
    public void El_resumen_de_la_mezcla_con_una_sola_de_cada()
    {
        Assert.Equal("1 canción · 0 de 0 transiciones armónicas · 0 choques de tono · 0 sin tonalidad",
                     new ResumenMezcla(1, 0, 0, 0, 0, 0, 0).Texto);
        Assert.Equal("2 canciones · 0 de 1 transición armónica · 1 choque de tono · 0 sin tonalidad · 1 vuelve a un momento anterior · 1 se salta un momento",
                     new ResumenMezcla(2, 1, 0, 1, 0, 1, 1).Texto);
        Assert.Equal("5 canciones · 2 de 4 transiciones armónicas · 2 choques de tono · 0 sin tonalidad · 2 vuelven a un momento anterior · 3 se saltan un momento",
                     new ResumenMezcla(5, 4, 2, 2, 0, 2, 3).Texto);
    }

    [Fact]
    public void La_bandeja_ya_no_dice_cancion_es()
    {
        Assert.Equal("1 canción", BandejaViewModel.Canciones(1));
        Assert.Equal("3 canciones", BandejaViewModel.Canciones(3));
        Assert.Equal("1 canción marcada como revisada.", BandejaViewModel.TextoMarcadas(1, EstadoBandeja.Revisada));
        Assert.Equal("3 canciones marcadas como preparadas.", BandejaViewModel.TextoMarcadas(3, EstadoBandeja.Preparada));
    }

    [Fact]
    public void Duplicados_ya_no_dice_grupo_s()
    {
        Assert.Equal("1 grupo de duplicados · 2 archivos implicados · 1 carpeta excluida · 1 edición de DJ fuera · 1 resuelto por carpeta prioritaria.",
                     DuplicatesViewModel.TextoAgrupados(1, 2, 1, 1, 1, false));
        Assert.Equal("3 grupos de duplicados · 7 archivos implicados · 2 carpetas excluidas · cada versión conserva un ejemplar.",
                     DuplicatesViewModel.TextoAgrupados(3, 7, 2, 0, 0, true));
        Assert.Equal("0 grupos de duplicados · 0 archivos implicados.", DuplicatesViewModel.TextoAgrupados(0, 0, 0, 0, 0, false));
    }

    // ---------------------------------------------------------------- rendimiento-1 · Duplicados ocupada mientras envía

    // La página sigue ocupada mientras quede ALGUNA de sus tareas: el envío a la papelera puede
    // solaparse con el análisis del audio, y la primera que acaba no puede desbloquear la aplicación.
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    public void La_pagina_sigue_ocupada_mientras_quede_algo(bool huellas, bool agrupando, bool papelera, bool ocupada)
        => Assert.Equal(ocupada, DuplicatesViewModel.SigueOcupada(huellas, agrupando, papelera));

    [Fact]
    public void Cancelar_el_envio_dice_cuantas_llegaron_a_la_papelera()
    {
        var cancelado = new EnvioPapeleraResultado(new[] { "a.mp3" }, Array.Empty<(string, string)>(), Interrumpido: true);
        Assert.Equal("Cancelado: se ha enviado 1 de las 5 copias a la papelera; el resto no se ha tocado.",
                     EnvioPapelera.Resumen(cancelado, 5, Sustantivo.Copia));

        var conFallo = new EnvioPapeleraResultado(new[] { "a.mp3", "b.mp3" }, new[] { ("c.mp3", "En uso.") }, Interrumpido: true);
        Assert.Equal("Cancelado: se han enviado 2 de las 5 copias a la papelera; el resto no se ha tocado. No se ha enviado c.mp3: En uso.",
                     EnvioPapelera.Resumen(conFallo, 5, Sustantivo.Copia));

        var nada = new EnvioPapeleraResultado(Array.Empty<string>(), Array.Empty<(string, string)>(), Interrumpido: true);
        Assert.Equal("Cancelado: no se ha enviado ninguna copia a la papelera.", EnvioPapelera.Resumen(nada, 5, Sustantivo.Copia));

        var entero = new EnvioPapeleraResultado(new[] { "a.mp3" }, Array.Empty<(string, string)>());
        Assert.Equal("Enviado a la papelera: a.mp3.", EnvioPapelera.Resumen(entero, 1, Sustantivo.Copia));
    }

    // El envío se para entre un archivo y el siguiente, y lo enviado se devuelve para quitarlo de la tabla.
    [Fact]
    public async Task El_envio_se_puede_cancelar_a_mitad()
    {
        using var cts = new CancellationTokenSource();
        var enviadas = new List<string>();
        var res = await EnvioPapelera.EnviarAsync(new[] { "a", "b", "c" }, r =>
        {
            enviadas.Add(r);
            if (r == "a") cts.Cancel();
            return (true, "");
        }, ct: cts.Token);

        Assert.True(res.Interrumpido);
        Assert.Equal(new[] { "a" }, res.Enviadas);
        Assert.Equal(new[] { "a" }, enviadas);
    }

    // La defensa en la biblioteca: quitar canciones (enviadas a la papelera) con un escaneo en marcha
    // ya no las devuelve al publicar lo que ese escaneo había leído antes. Reproducido en la revisión:
    // un escaneo retenido tras listar la carpeta, File.Delete + RemoveTracks, y al soltarlo volvía.
    [Fact]
    public async Task Quitar_canciones_a_mitad_de_escaneo_no_las_devuelve()
    {
        var carpeta = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        var a1 = Path.Combine(carpeta, "a1.mp3");
        var a2 = Path.Combine(carpeta, "a2.mp3");
        Mp3Fixture.WriteMinMp3(a1);
        Mp3Fixture.WriteMinMp3(a2);

        // Un recorrido que, cuando se le pide, se queda parado DESPUÉS de listar la carpeta (con a1 dentro).
        var dentro = new SemaphoreSlim(0);
        var soltar = new ManualResetEventSlim(false);
        var retener = 0;
        var store = new LibraryStore(new AppConfig { Folders = new() { carpeta } }, () => { }, new ScanCache(Path.Combine(_dir, "scan.json")),
            r =>
            {
                var leida = LibraryScanner.Recorrer(r);
                if (Interlocked.Exchange(ref retener, 0) == 1) { dentro.Release(); soltar.Wait(TimeSpan.FromSeconds(20)); }
                return leida;
            });
        await store.ScanAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, store.Tracks.Count);

        Interlocked.Exchange(ref retener, 1);
        var escaneo = store.ScanAsync();
        Assert.True(await dentro.WaitAsync(TimeSpan.FromSeconds(20)), "el escaneo no llegó a empezar");
        File.Delete(a1);                       // lo que hace la papelera…
        store.RemoveTracks(new[] { a1 });      // …y lo que hace Duplicados al terminar
        soltar.Set();
        await escaneo.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(store.Tracks, t => t.FilePath == a1);
        Assert.Contains(store.Tracks, t => t.FilePath == a2);
    }

    // ---------------------------------------------------------------- interfaz-4 · la propuesta de una copia sustituida

    // Tras «Sustituir», la propuesta de Enriquecer de la copia antigua (calculada con ella y quizá
    // con su casilla marcada) se retira: «Aplicar marcadas» la escribiría en la descarga. Se retiran
    // por las dos rutas, la de la copia antigua y la que ocupa ahora la descarga (otra extensión).
    [Fact]
    public void La_propuesta_de_la_copia_sustituida_se_retira()
    {
        PreviewRow Fila(string ruta)
        {
            var r = new ProcessResult { FilePath = ruta, Old = Path.GetFileName(ruta), New = Path.GetFileName(ruta), Found = true };
            return new PreviewRow { Result = r, Old = r.Old, Apply = true };
        }
        var vieja = Fila(@"E:\Musica\Fisher - Losing It.mp3");
        var otra = Fila(@"E:\Musica\Fisher - Stop It.mp3");

        var quitar = EnrichViewModel.FilasRevertidas(new[] { vieja, otra }, new[] { @"E:\Musica\Fisher - Losing It.mp3", @"E:\Musica\Fisher - Losing It.aiff" });

        Assert.Equal(new[] { vieja }, quitar);
    }
}
