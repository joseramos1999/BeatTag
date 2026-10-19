using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Providers;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Etiquetador.Tests;

/// <summary>
/// Pestaña «Comprobar audio».
///
/// A6: en Windows, todo lo que no fuera MP3 o WAV fallaba con «Fuera de Windows solo se pueden leer
/// MP3 y WAV», y con el motor de pago activo cada canción ilegible se contaba como consulta de pago
/// sin haberse enviado: 300 FLAC paraban la pasada por «alcanzado el tope» sin gastar nada.
///
/// X2: «Reanalizar con otro fragmento» podía gastar consultas de pago y enviar audio sin ninguna
/// confirmación.
///
/// X3: la lista de pendientes se calculaba en el hilo de la interfaz, y dos veces por clic.
/// </summary>
public class ComprobarAudioCosteTests
{
    // ---------- A6: leer FLAC, M4A, AAC… en Windows ----------

    /// <summary>
    /// Un M4A de verdad, codificado aquí con el propio Media Foundation de Windows. Devuelve null si
    /// el equipo no tiene codificador AAC (las ediciones N de Windows no lo traen): entonces no hay
    /// nada que probar.
    /// </summary>
    private static string? M4aDePrueba(string dir, double segundos)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var m4a = Path.Combine(dir, "Karol G - Provenza.m4a");
        try
        {
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            var tono = new SignalGenerator(44100, 2) { Frequency = 440, Gain = 0.2 }
                .Take(TimeSpan.FromSeconds(segundos))
                .ToWaveProvider16();
            MediaFoundationEncoder.EncodeToAac(tono, m4a);
            return File.Exists(m4a) ? m4a : null;
        }
        catch { return null; }
    }

    [WindowsFact]
    public void En_windows_un_m4a_se_puede_recortar_para_identificarlo()
    {
        var dir = Mp3Fixture.NewTempDir();
        string? recorte = null;
        try
        {
            var m4a = M4aDePrueba(dir, segundos: 6);
            if (m4a == null) return;   // sin codificador AAC en este equipo

            using (var s = AudioSamples.AbrirComoWaveStream(m4a))
                Assert.InRange(s.TotalTime.TotalSeconds, 5, 7);

            var r = AudioSamples.EscribirRecorteWav(m4a, inicioFraccion: 0.30, segundos: 1.0);
            recorte = r.Ruta;
            using var wav = new WaveFileReader(recorte);
            Assert.InRange(wav.TotalTime.TotalSeconds, 0.5, 1.05);
            Assert.InRange(r.SegundosOriginal, 5, 7);
        }
        finally
        {
            if (recorte != null) { try { File.Delete(recorte); } catch { } }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // El mensaje no puede decir «fuera de Windows» a quien está en Windows: si no se puede leer, se
    // dice por qué y de qué archivo se trata.
    [Fact]
    public void Un_archivo_que_no_se_puede_leer_lo_explica_sin_hablar_de_otro_sistema()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var flac = Path.Combine(dir, "Enur - Calabria 2007.flac");
            File.WriteAllBytes(flac, new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0, 0, 0 });

            var ex = Assert.Throws<NotSupportedException>(() => AudioSamples.AbrirComoWaveStream(flac));

            Assert.DoesNotContain("Fuera de Windows", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".flac", ex.Message);
            if (!OperatingSystem.IsWindows()) Assert.Contains("MP3", ex.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- A6: una consulta de pago solo cuenta si se envía ----------

    /// <summary>AudD de mentira: cuenta las peticiones y responde «no lo reconozco».</summary>
    private sealed class AuddFalso : HttpMessageHandler
    {
        public int Peticiones;
        public readonly ConcurrentBag<string> Servidores = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Peticiones);
            Servidores.Add(request.RequestUri?.Host ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"success\",\"result\":null}"),
            });
        }
    }

    private static IdentificationScanner Escaner(string dir, HttpClient http)
    {
        var paths = new AppPaths(dir);
        paths.EnsureDirectories();
        return new IdentificationScanner(Path.Combine(dir, "identificacion.json"), new Fingerprint(paths),
                                         new AcoustIdProvider(new ApiClient(paths)), new AuddProvider(http), http);
    }

    private static readonly IdentificationScanner.Opciones SoloPago =
        new(AcoustIdKey: "", AuddToken: "token-de-prueba", UsarPago: true, MaxPago: 1);

    // El caso del hallazgo, en pequeño: canciones que no se pueden leer, con el tope de pago en 1.
    // Antes la primera agotaba el tope sin enviarse y la pasada se paraba por «alcanzado el tope».
    [Fact]
    public async Task Lo_que_no_se_puede_leer_no_gasta_consultas_de_pago()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var ilegibles = Enumerable.Range(0, 3).Select(i =>
            {
                var f = Path.Combine(dir, $"Tema {i}.flac");
                File.WriteAllBytes(f, new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0, 0, 0 });
                return f;
            }).ToList();
            var audd = new AuddFalso();
            using var http = new HttpClient(audd);

            var r = await Escaner(dir, http).ScanAsync(ilegibles, SoloPago, force: true, progreso: null);

            Assert.Equal(0, r.ConsultasDePago);
            Assert.Equal(3, r.Fallidas);
            Assert.Equal("", r.Error);            // no se ha parado por ningún tope
            Assert.Equal(0, audd.Peticiones);    // y no ha salido nada del equipo
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Lo que sí se envía, sí cuenta: el freno de mano sigue funcionando.
    [Fact]
    public async Task Lo_que_se_envia_si_cuenta_como_consulta_de_pago()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var flac = Path.Combine(dir, "Tema roto.flac");
            File.WriteAllBytes(flac, new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0, 0, 0 });
            var mp3 = Path.Combine(dir, "Quevedo - Columbia.mp3");
            Mp3Fixture.WriteMinMp3(mp3, frames: 200);
            var audd = new AuddFalso();
            using var http = new HttpClient(audd);

            var r = await Escaner(dir, http).ScanAsync(new[] { flac, mp3 }, SoloPago, force: true, progreso: null);

            Assert.Equal(1, r.ConsultasDePago);
            Assert.Equal(1, audd.Peticiones);
            Assert.Equal(1, r.Fallidas);
            Assert.Equal("", r.Error);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------- X2: confirmar antes de gastar, también al reanalizar ----------

    // El escenario del hallazgo: AudD activado, máximo 2.000, 400 filas seleccionadas.
    [Fact]
    public void Reanalizar_con_el_motor_de_pago_pide_confirmacion_con_coste_y_privacidad()
    {
        var aviso = IdentifyViewModel.AvisoAntesDeConsultar(400, pagoEfectivo: true, maxPago: 2000,
                                                            force: true, otroFragmento: true);

        Assert.NotNull(aviso);
        Assert.Equal("Se van a reanalizar 400 canciones con otro fragmento", aviso!.Titulo);
        Assert.Contains("FRAGMENTO DE AUDIO", aviso.Cuerpo);
        Assert.Contains("400 consultas de pago", aviso.Nota);
        Assert.Contains((400 * IdentifyViewModel.CostePorConsultaPago).ToString("0.00", CultureInfo.CurrentCulture) + " $", aviso.Nota);
        Assert.Equal("Reanalizar", aviso.Boton);
        // Reanalizar pregunta otra vez a propósito: no puede prometer que nada se pregunte dos veces.
        Assert.DoesNotContain("dos veces", aviso.Nota);
    }

    // El techo de gasto lo pone el tope del usuario, no el número de filas.
    [Fact]
    public void El_coste_anunciado_respeta_el_tope_de_consultas()
    {
        var aviso = IdentifyViewModel.AvisoAntesDeConsultar(400, pagoEfectivo: true, maxPago: 300,
                                                            force: true, otroFragmento: true)!;
        Assert.Contains("300 consultas de pago", aviso.Nota);
        Assert.Contains((300 * IdentifyViewModel.CostePorConsultaPago).ToString("0.00", CultureInfo.CurrentCulture) + " $", aviso.Nota);
    }

    // Sin pago, reanalizar unas pocas filas no molesta con un diálogo…
    [Fact]
    public void Reanalizar_pocas_sin_pago_no_pregunta()
        => Assert.Null(IdentifyViewModel.AvisoAntesDeConsultar(3, pagoEfectivo: false, maxPago: 300,
                                                               force: true, otroFragmento: true));

    // …pero muchas sí, por el tiempo, y deja claro que no cuesta nada ni sale audio.
    [Fact]
    public void Reanalizar_muchas_sin_pago_avisa_del_tiempo()
    {
        var aviso = IdentifyViewModel.AvisoAntesDeConsultar(400, pagoEfectivo: false, maxPago: 300,
                                                            force: true, otroFragmento: true);
        Assert.NotNull(aviso);
        Assert.Contains("no cuesta nada", aviso!.Nota);
        Assert.Contains("El audio no sale de tu equipo", aviso.Cuerpo);
        Assert.DoesNotContain("FRAGMENTO DE AUDIO", aviso.Cuerpo);
    }

    // La comprobación de la biblioteca pregunta siempre, como antes, aunque sea una sola canción.
    [Fact]
    public void Comprobar_la_biblioteca_pregunta_siempre()
    {
        var aviso = IdentifyViewModel.AvisoAntesDeConsultar(1, pagoEfectivo: false, maxPago: 300,
                                                            force: false, otroFragmento: false);
        Assert.NotNull(aviso);
        Assert.Equal("Se va a comprobar 1 canción", aviso!.Titulo);
        Assert.Contains("ninguna canción se pregunta dos veces", aviso.Nota);
    }

    // ---------- X3: la lista de pendientes, fuera del hilo de la interfaz ----------

    // El filtro (partir el nombre y mirar el archivo, canción por canción) no puede correr en el
    // hilo que lo pide. Si lo hiciera, la llamada no volvería hasta que la puerta se abriera.
    [Fact]
    public async Task La_lista_de_pendientes_se_calcula_en_segundo_plano()
    {
        using var puerta = new ManualResetEventSlim(false);
        var rutas = new[] { @"E:\Musica\Quevedo - Columbia.mp3" };

        var tarea = IdentifyViewModel.FiltrarPorConsultarAsync(rutas, _ => { puerta.Wait(5000); return false; }, force: false);
        var volvioAntes = !tarea.IsCompleted;
        puerta.Set();
        var r = await tarea;

        Assert.True(volvioAntes, "el filtro se ejecutó en el hilo que lo pidió");
        Assert.Equal(rutas, r);
    }

    [Fact]
    public async Task La_lista_de_pendientes_deja_fuera_lo_excluido_y_lo_ya_respondido()
    {
        var mashup = @"E:\Musica\Mashups\Quevedo vs Bizarrap - Columbia.mp3";
        var acapella = @"E:\Musica\Enur - Calabria 2007 (Acapella).mp3";
        var respondida = @"E:\Musica\Karol G - Provenza.mp3";
        var nueva = @"E:\Musica\Bad Bunny - Titi Me Pregunto.mp3";
        var todas = new[] { mashup, acapella, respondida, nueva };
        Func<string, bool> consultado = p => p == respondida;

        Assert.Equal(new[] { nueva }, await IdentifyViewModel.FiltrarPorConsultarAsync(todas, consultado, force: false));
        Assert.Equal(new[] { respondida, nueva }, await IdentifyViewModel.FiltrarPorConsultarAsync(todas, consultado, force: true));
    }
}
