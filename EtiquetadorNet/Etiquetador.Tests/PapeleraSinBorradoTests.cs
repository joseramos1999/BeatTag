using System.Diagnostics;
using System.Runtime.Versioning;
using Etiquetador.App.Services;
using static Etiquetador.App.Services.Papelera;

namespace Etiquetador.Tests;

/// <summary>
/// «Enviar a la papelera» no puede ser un borrado definitivo disfrazado (hallazgos M4 y T2).
///
/// En Windows, FileSystem.DeleteFile sin diálogos le pide al shell que recicle sin preguntar, y el
/// shell, cuando la unidad no tiene papelera, destruye el archivo en silencio y devuelve éxito. El
/// caso de un DJ: la exportación de rekordbox en un pendrive, 300 copias enviadas desde Duplicados,
/// «enviadas a la papelera» en la barra de estado y nada en la papelera.
/// </summary>
public class PapeleraSinBorradoTests
{
    // --- La regla ---

    [Fact]
    public void En_una_unidad_de_red_se_niega_y_lo_dice()
    {
        var m = Motivo(TipoUnidad.Red, @"\\nas\musica", false, 0, 0);
        Assert.Contains("unidad de red", m);
        Assert.Contains("para siempre", m);
        Assert.Contains("No se ha tocado", m);
    }

    [Fact]
    public void En_una_memoria_usb_se_niega_y_lo_dice()
    {
        var m = Motivo(TipoUnidad.Extraible, "F:", false, 0, 0);
        Assert.Contains("memoria USB", m);
        Assert.Contains("F:", m);
    }

    [Fact]
    public void Con_la_papelera_desactivada_en_esa_unidad_tambien_se_niega()
    {
        // «No mover archivos a la papelera» marcado para la unidad: el shell borra para siempre.
        var m = Motivo(TipoUnidad.Fija, "E:", papeleraDesactivada: true, 0, 10_000_000);
        Assert.Contains("desactivada", m);
    }

    [Fact]
    public void Un_archivo_que_no_cabe_en_la_papelera_no_se_envia()
    {
        // Un WAV de 120 MB con la papelera de la unidad limitada a 50 MB.
        var m = Motivo(TipoUnidad.Fija, "E:", false, capacidadMaxMb: 50, tamano: 120L * 1024 * 1024);
        Assert.Contains("50 MB", m);
    }

    [Fact]
    public void En_un_disco_fijo_con_papelera_no_hay_motivo_para_negarse()
    {
        Assert.Equal("", Motivo(TipoUnidad.Fija, "E:", false, 0, 9_000_000));
        Assert.Equal("", Motivo(TipoUnidad.Fija, "E:", false, capacidadMaxMb: 4000, tamano: 9_000_000));
    }

    // Óptica, en memoria o sin identificar: no se sabe si tiene papelera, así que tampoco se borra.
    [Theory]
    [InlineData("Optica")]
    [InlineData("Memoria")]
    [InlineData("Desconocida")]
    public void Ante_la_duda_tampoco_se_borra(string tipo)
        => Assert.NotEqual("", Motivo(Enum.Parse<TipoUnidad>(tipo), "X:", false, 0, 0));

    // --- La unidad, a partir de la ruta (Windows) ---

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Una_ruta_unc_es_de_red_sin_preguntar_al_servidor()
    {
        Assert.Equal(TipoUnidad.Red, TipoDeUnidad(@"\\nas\musica\Hits 2024\tema.mp3", out var raiz));
        Assert.Equal(@"\\nas\musica", raiz);
        Assert.Equal(TipoUnidad.Red, TipoDeUnidad(@"\\?\UNC\nas\musica\tema.mp3", out raiz));
        Assert.Equal(@"\\nas\musica", raiz);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void La_carpeta_temporal_esta_en_un_disco_fijo()
        => Assert.Equal(TipoUnidad.Fija, TipoDeUnidad(Path.Combine(Path.GetTempPath(), "tema.mp3"), out _));

    // El servidor no existe: si Enviar lo intentara, tardaría decenas de segundos en fallar, y si
    // existiera lo borraría para siempre. Tiene que negarse antes de tocar nada.
    [WindowsFact]
    public void Enviar_algo_de_una_unidad_de_red_no_lo_intenta_siquiera()
    {
        var reloj = Stopwatch.StartNew();
        var (ok, error) = Papelera.Enviar(@"\\nas-beattag-inexistente\musica\Hits\tema.mp3");
        Assert.False(ok);
        Assert.Contains("unidad de red", error);
        Assert.True(reloj.ElapsedMilliseconds < 2000, $"tardó {reloj.ElapsedMilliseconds} ms: parece que fue a la red");
    }

    // --- Lo que dice el diálogo antes de confirmar ---

    [WindowsFact]
    public void El_dialogo_avisa_cuando_ninguno_va_a_ir_a_la_papelera()
    {
        var nota = NotaConfirmacion(new[] { @"\\nas\musica\a.mp3", @"\\nas\musica\b.mp3" });
        Assert.StartsWith("No se va a enviar ninguno", nota);
        Assert.DoesNotContain("puedes recuperarlos", nota);
    }

    [WindowsFact]
    public void El_dialogo_cuenta_los_que_no_van_cuando_hay_mezcla()
    {
        var local = Path.Combine(Path.GetTempPath(), "a.mp3");
        var nota = NotaConfirmacion(new[] { local, @"\\nas\musica\b.mp3" });
        Assert.StartsWith("1 de ellos no se van a enviar", nota);
        Assert.Contains("El resto va a la papelera", nota);
    }

    [WindowsFact]
    public void Sin_problemas_el_dialogo_sigue_prometiendo_lo_de_siempre()
        => Assert.StartsWith("Van a la papelera del sistema",
                             NotaConfirmacion(new[] { Path.Combine(Path.GetTempPath(), "a.mp3") }));
}
