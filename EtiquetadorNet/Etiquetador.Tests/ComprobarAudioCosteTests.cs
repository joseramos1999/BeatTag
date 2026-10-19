using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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

    // ---------- X3 (revisión): qué negativas del gratuito siguen abiertas ----------
    //
    // Visto en revisión: con «Usar también AudD» marcada pero SIN clave de AudD, la lista se filtraba
    // con la casilla tal cual. Las canciones a las que AcoustID ya había dicho «no» (el 97 % de la
    // biblioteca, si reconoce el 2,8 %) volvían a la lista, y como la pasada ya no vuelve a filtrar,
    // cada «Comprobar» repetía unas 13.000 consultas a AcoustID y las guardaba otra vez sin cerrar.

    /// <summary>Caché de identificación con un «no lo reconozco» de AcoustID, sin pasar por AudD.</summary>
    private static void CacheConNegativaDelGratuito(string cache, string ruta)
    {
        var fi = new FileInfo(ruta);
        var d = new Dictionary<string, object>
        {
            [ruta] = new { M = fi.LastWriteTimeUtc.Ticks, S = fi.Length, A = "", T = "", L = "", F = "",
                           Sin = true, P = false, O = 0.30 },
        };
        File.WriteAllText(cache, JsonSerializer.Serialize(d));
    }

    [Fact]
    public async Task Sin_clave_de_AudD_las_negativas_del_gratuito_no_vuelven_a_la_lista()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var respondida = Path.Combine(dir, "Quevedo - Columbia.mp3");
            var nueva = Path.Combine(dir, "Karol G - Provenza.mp3");
            Mp3Fixture.WriteMinMp3(respondida, frames: 20);
            Mp3Fixture.WriteMinMp3(nueva, frames: 20);
            CacheConNegativaDelGratuito(Path.Combine(dir, "identificacion.json"), respondida);
            using var http = new HttpClient(new AuddFalso());
            var escaner = Escaner(dir, http);
            var rutas = new[] { respondida, nueva };

            // Casilla marcada sin clave de AudD: el de pago no va a consultar, así que esa negativa
            // está respondida. Lo mismo con el máximo de pago a 0.
            var sinClave = IdentifyViewModel.PagoEfectivoPara(usarPago: true, hayPago: false, maxConsultasPago: 300);
            var sinMaximo = IdentifyViewModel.PagoEfectivoPara(usarPago: true, hayPago: true, maxConsultasPago: 0);
            Assert.Equal(new[] { nueva }, await IdentifyViewModel.PendientesAsync(rutas, escaner, sinClave, force: false));
            Assert.Equal(new[] { nueva }, await IdentifyViewModel.PendientesAsync(rutas, escaner, sinMaximo, force: false));

            // Con AudD de verdad, la negativa sí se reabre: es justo la que el de pago podría reconocer.
            var conPago = IdentifyViewModel.PagoEfectivoPara(usarPago: true, hayPago: true, maxConsultasPago: 300);
            Assert.Equal(rutas, await IdentifyViewModel.PendientesAsync(rutas, escaner, conPago, force: false));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El escáner, si filtra él (sin «force»), sigue la misma regla con sus opciones: no basta la
    // casilla, hace falta la clave y un máximo mayor que 0. Antes, con la casilla y sin clave, esta
    // canción volvía a AcoustID en cada pasada.
    [Theory]
    [InlineData("", 300)]
    [InlineData("token-de-prueba", 0)]
    public async Task El_escaner_no_reabre_una_negativa_si_el_pago_no_puede_consultar(string token, int maxPago)
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var respondida = Path.Combine(dir, "Quevedo - Columbia.mp3");
            Mp3Fixture.WriteMinMp3(respondida, frames: 20);
            CacheConNegativaDelGratuito(Path.Combine(dir, "identificacion.json"), respondida);
            var audd = new AuddFalso();
            using var http = new HttpClient(audd);

            var o = new IdentificationScanner.Opciones(AcoustIdKey: "clave-de-prueba", AuddToken: token,
                                                       UsarPago: true, MaxPago: maxPago);
            var r = await Escaner(dir, http).ScanAsync(new[] { respondida }, o, force: false, progreso: null);

            Assert.Equal(0, r.Consultadas);
            Assert.Equal("", r.Error);
            Assert.Equal(0, audd.Peticiones);   // ni AudD ni la descarga de fpcalc
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void El_pago_solo_es_efectivo_con_casilla_clave_y_maximo()
    {
        Assert.True(IdentifyViewModel.PagoEfectivoPara(true, true, 300));
        Assert.False(IdentifyViewModel.PagoEfectivoPara(false, true, 300));
        Assert.False(IdentifyViewModel.PagoEfectivoPara(true, false, 300));
        Assert.False(IdentifyViewModel.PagoEfectivoPara(true, true, 0));
    }

    // ---------- X2 (revisión): reanalizar mientras se prepara «Comprobar» ----------
    //
    // El reanálisis solo miraba si había una comprobación en curso, no si se estaba preparando la
    // lista de «Comprobar»; y el aviso que tapa la tabla lo quitaba el primero que terminaba. Si se
    // lanzaba un reanálisis entonces, la comprobación aceptada después se descartaba sin decir nada.

    [Fact]
    public void Mientras_se_prepara_la_lista_no_se_puede_empezar_otra_cosa()
    {
        Assert.Equal("", IdentifyViewModel.MotivoParaNoEmpezar(comprobando: false, preparando: false));
        Assert.Contains("«Comprobar»", IdentifyViewModel.MotivoParaNoEmpezar(comprobando: false, preparando: true));
        Assert.Contains("en curso", IdentifyViewModel.MotivoParaNoEmpezar(comprobando: true, preparando: false));
        Assert.Contains("en curso", IdentifyViewModel.MotivoParaNoEmpezar(comprobando: true, preparando: true));
    }

    // Preparar la lista y recomponer la tabla piden el aviso a la vez: si termina antes el recálculo,
    // el aviso sigue hasta que acabe la preparación.
    [Fact]
    public void El_aviso_de_cargando_sigue_mientras_quede_alguna_tarea()
    {
        var cambios = new List<bool>();
        var aviso = new AvisoCompartido(cambios.Add);

        var preparar = aviso.Mostrar();
        var recomponer = aviso.Mostrar();
        recomponer.Dispose();
        Assert.True(aviso.Visible);

        preparar.Dispose();
        Assert.False(aviso.Visible);
        Assert.Equal(new[] { true, false }, cambios);

        // Soltar dos veces la misma petición no quita la de otra tarea.
        var otra = aviso.Mostrar();
        preparar.Dispose();
        Assert.True(aviso.Visible);
        otra.Dispose();
        Assert.Equal(new[] { true, false, true, false }, cambios);
    }
}
