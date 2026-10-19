using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Resto de T2 y X15/I14 en la aplicación: enviar a la papelera ya no recorre los archivos en el hilo
/// de la interfaz (Duplicados y Bandeja comparten <see cref="EnvioPapelera"/>), y lo que la Bandeja
/// enseña y anota al descartar y al sustituir.
/// </summary>
public class PapeleraEnSegundoPlanoTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- T2 · fuera del hilo de la interfaz

    // Con 300 copias y un osascript por archivo en macOS, la ventana se quedaba congelada todo el
    // bucle. Ahora quien llama recupera el control enseguida, mientras los archivos se envían.
    [Fact]
    public async Task Quien_llama_no_espera_a_que_se_envien_los_archivos()
    {
        using var puerta = new ManualResetEventSlim();
        var rutas = new[] { Path.Combine(_dir, "a.mp3"), Path.Combine(_dir, "b.mp3") };

        var tarea = EnvioPapelera.EnviarAsync(rutas, _ => { puerta.Wait(TimeSpan.FromSeconds(10)); return (true, ""); });

        Assert.False(tarea.IsCompleted);   // el «hilo de la interfaz» ya está libre
        puerta.Set();
        var res = await tarea;
        Assert.Equal(rutas, res.Enviadas);
    }

    [Fact]
    public void Envia_en_orden_anota_los_fallos_con_su_motivo_y_avisa_del_avance()
    {
        var rutas = new[] { Path.Combine(_dir, "a.mp3"), Path.Combine(_dir, "Pack", "b.mp3"), Path.Combine(_dir, "c.mp3") };
        var avance = new List<(int, int)>();

        var res = EnvioPapelera.Enviar(rutas,
            r => r.EndsWith("b.mp3", StringComparison.Ordinal) ? (false, "Está en una unidad de red.") : (true, ""),
            new ProgresoInmediato(avance));

        Assert.Equal(new[] { rutas[0], rutas[2] }, res.Enviadas);
        Assert.Equal(("b.mp3", "Está en una unidad de red."), Assert.Single(res.Fallos));
        Assert.Equal(new[] { (1, 3), (2, 3), (3, 3) }, avance);
    }

    // Un error inesperado en un archivo no corta el resto ni tumba la aplicación.
    [Fact]
    public void Una_excepcion_cuenta_como_fallo_y_se_sigue()
    {
        var res = EnvioPapelera.Enviar(new[] { "x.mp3", "y.mp3" },
            r => r == "x.mp3" ? throw new IOException("El archivo está en uso.") : (true, ""));

        Assert.Equal(new[] { "y.mp3" }, res.Enviadas);
        Assert.Equal(("x.mp3", "El archivo está en uso."), Assert.Single(res.Fallos));
    }

    [Fact]
    public void El_resumen_de_la_bandeja_habla_de_descargas_y_dice_el_motivo()
    {
        Assert.Equal("25 descarga(s) enviadas a la papelera.", EnvioPapelera.Resumen(25, Array.Empty<(string, string)>(), "descarga"));
        var fallos = new List<(string, string)> { ("a.mp3", "Está en una unidad extraíble (F:)."), ("b.mp3", "Está en una unidad extraíble (F:).") };
        Assert.Equal("No se ha enviado ninguna descarga a la papelera: Está en una unidad extraíble (F:).",
                     EnvioPapelera.Resumen(0, fallos, "descarga"));
        // Duplicados sigue diciendo lo mismo que antes.
        Assert.Equal("3 copia(s) enviadas a la papelera · 2 no se han enviado: Está en una unidad extraíble (F:).",
                     DuplicatesViewModel.ResumenPapelera(3, fallos));
    }

    [Theory]
    [InlineData(0, 1, "Enviando a la papelera…")]
    [InlineData(37, 120, "Enviando a la papelera: 37 de 120…")]
    public void El_estado_dice_por_donde_va(int hechas, int total, string esperado)
        => Assert.Equal(esperado, EnvioPapelera.TextoAvance(hechas, total));

    /// <summary>Progress&lt;T&gt; publica en el contexto de quien lo crea; este apunta en el acto, para la prueba.</summary>
    private sealed class ProgresoInmediato(List<(int, int)> destino) : IProgress<(int Hechas, int Total)>
    {
        public void Report((int Hechas, int Total) v) => destino.Add(v);
    }

    // ---------------------------------------------------------------- X15 / I14 · la fila de la bandeja

    private static BandejaRow Fila(Track t, IReadOnlyList<AvisoBandeja> avisos, PropuestaSustitucion? sustitucion = null)
    {
        var fila = new BandejaRow(t, null, new EntradaBandeja());
        fila.PonerAvisos(avisos, sustitucion);
        return fila;
    }

    [Fact]
    public void El_filtro_de_duplicados_no_coge_otra_version()
    {
        var t = new Track { FilePath = Path.Combine(_dir, "a.mp3") };
        Assert.True(Fila(t, new[] { new AvisoBandeja(TipoAviso.YaEnBiblioteca, "Ya está") }).TieneAvisoDeDuplicado);
        Assert.True(Fila(t, new[] { new AvisoBandeja(TipoAviso.RepetidaEnBandeja, "Repetida") }).TieneAvisoDeDuplicado);
        Assert.False(Fila(t, new[] { new AvisoBandeja(TipoAviso.OtraVersion, "Otra") }).TieneAvisoDeDuplicado);
        Assert.Contains(BandejaViewModel.FiltroDuplicado, BandejaViewModel.TodosLosFiltros);
    }

    // La columna dice que esta es la que conviene SUSTITUIR, no una más de las que ya tienes.
    [Fact]
    public void Una_descarga_mejor_se_distingue_en_la_columna_y_en_el_detalle()
    {
        var vieja = new Track { FilePath = Path.Combine(_dir, "Musica", "Fisher - Losing It.mp3"), Bitrate = 128 };
        var fila = Fila(new Track { FilePath = Path.Combine(_dir, "Bandeja", "x.mp3") },
                        new[] { new AvisoBandeja(TipoAviso.YaEnBiblioteca, "Ya está en la biblioteca: Fisher - Losing It.mp3") },
                        new PropuestaSustitucion(vieja, "320 kbps frente a 128 kbps"));

        Assert.True(fila.PuedeSustituir);
        Assert.Equal("Mejora la de la biblioteca", fila.AvisoPrincipal);
        Assert.Contains("320 kbps frente a 128 kbps", fila.AvisosDetalle);
        Assert.Contains("Ya está en la biblioteca", fila.AvisosDetalle);
    }

    [Fact]
    public void El_estado_tras_sustituir_dice_donde_esta_cada_copia_y_lo_de_rekordbox()
    {
        var destino = Path.Combine(_dir, "Musica", "Fisher - Losing It.mp3");
        var texto = BandejaViewModel.TextoSustituida("Fisher - Losing It.mp3", destino,
                                                     SustitucionBandeja.TextoRekordbox(destino, destino), "");
        Assert.StartsWith("Sustituida: «Fisher - Losing It.mp3» está en la papelera", texto);
        Assert.Contains("Se puede deshacer desde Enriquecer", texto);
        Assert.Contains("rekordbox", texto);
        Assert.DoesNotContain("⚠", texto);
        Assert.Contains("⚠ No se pudieron guardar las fichas: acceso denegado",
                        BandejaViewModel.TextoSustituida("a.mp3", destino, "", "acceso denegado"));
    }

    // Con el historial de verdad (ManifiestoDeshacer, el que usa la Bandeja): lo anotado se deshace.
    [Fact]
    public void Sustituir_con_el_historial_de_la_aplicacion_se_deshace()
    {
        var nueva = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "Bandeja")).FullName, "fisher 320.mp3");
        var vieja = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName, "Fisher - Losing It.mp3");
        var papelera = Directory.CreateDirectory(Path.Combine(_dir, "Papelera")).FullName;
        File.WriteAllText(nueva, "nueva");
        File.WriteAllText(vieja, "vieja");
        var paths = new AppPaths(_dir);

        ResultadoSustitucion r;
        string ruta;
        using (var manifiesto = ManifiestoDeshacer.Abrir(paths.NuevoManifiesto(), out var error))
        {
            Assert.NotNull(manifiesto);
            ruta = manifiesto!.Ruta;
            r = SustitucionBandeja.Ejecutar(nueva, vieja, p => { File.Move(p, Path.Combine(papelera, Path.GetFileName(p))); return (true, ""); },
                                            manifiesto.Anotar);
        }

        Assert.True(r.Ok, r.Error);
        var rec = JsonSerializer.Deserialize<UndoRecord>(Assert.Single(File.ReadAllLines(ruta)))!;
        Assert.Equal((nueva, vieja, true), (rec.OrigPath, rec.FinalPath, rec.Renamed));

        Assert.Equal(1, new UndoEngine(paths).UndoLastRun().Reverted);
        Assert.Equal("nueva", File.ReadAllText(nueva));
    }
}
