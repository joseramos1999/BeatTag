using System.Net.Http;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Cancelar una comprobación tiene que CONTARSE como cancelada.
///
/// El escáner atrapa la cancelación para no perder lo ya respondido -que en el motor de pago se ha
/// pagado-, y al hacerlo la borraba: devolvía un resumen igual que el de una pasada terminada, así
/// que la pantalla anunciaba «Comprobadas N…» justo después de pulsar Cancelar. Quedarse a medias y
/// creer que has terminado es peor que no haber empezado, porque nadie vuelve a lanzarlo.
/// </summary>
public class CancelarComprobacionTests
{
    private static IdentificationScanner Montar(string dir)
    {
        var paths = new AppPaths(dir);
        paths.EnsureDirectories();
        var api = new ApiClient(paths);
        var http = new HttpClient();
        return new IdentificationScanner(
            Path.Combine(dir, "identificacion.json"),
            new Fingerprint(paths),
            new AcoustIdProvider(api),
            new AuddProvider(http),
            http);
    }

    [Fact]
    public async Task Cancelar_se_comunica_como_cancelado_y_no_como_terminado()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var scanner = Montar(dir);
            using var cts = new CancellationTokenSource();
            cts.Cancel();   // el usuario pulsa Cancelar antes de que arranque

            var opciones = new IdentificationScanner.Opciones("clave-acoustid", "", UsarPago: false, MaxPago: 0);
            var r = await scanner.ScanAsync(new[] { Path.Combine(dir, "a.mp3") }, opciones,
                                            force: true, progreso: null, ct: cts.Token);

            Assert.True(r.Cancelada);
            Assert.Equal(0, r.Consultadas);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Y lo contrario: sin cancelar, una pasada que no tiene nada que hacer no se marca como cancelada.
    [Fact]
    public async Task Una_pasada_normal_no_se_marca_como_cancelada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var scanner = Montar(dir);
            var opciones = new IdentificationScanner.Opciones("clave-acoustid", "", UsarPago: false, MaxPago: 0);

            var r = await scanner.ScanAsync(Array.Empty<string>(), opciones, force: true, progreso: null);

            Assert.False(r.Cancelada);
            Assert.Equal("", r.Error);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // El contrato de «se puede cifrar» tiene que ser cierto, no una suposición por el sistema
    // operativo: DPAPI necesita un perfil de usuario cargado, y hay Windows donde no lo hay.
    [Fact]
    public void Decir_que_hay_cifrado_disponible_significa_que_cifrar_funciona()
    {
        var funciona = false;
        try { funciona = Secretos.Protect("prueba").Length > 0; }
        catch { funciona = false; }

        Assert.Equal(funciona, Secretos.Disponible);
    }
}
