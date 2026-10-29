using Etiquetador.App.ViewModels;

namespace Etiquetador.Tests;

/// <summary>
/// La fila de la pestaña Volumen. Estas pruebas cubren el lado de la interfaz de la salvaguarda que
/// hay en el nucleo (ver SafetyTests): el nucleo se niega a tocar lo que no es MP3, y aqui se
/// comprueba que ademas ni siquiera se le ofrece al usuario como ajustable.
///
/// Hasta ahora las pruebas solo miraban Etiquetador.Core, y justo en la capa de vistas estan los
/// flujos que tocan los archivos.
/// </summary>
public class LoudnessRowTests
{
    private static LoudnessRow Fila(string archivo, double lufs = -20, double pico = -8)
        => new() { FileName = Path.GetFileName(archivo), FilePath = archivo, Folder = @"C:\m", Lufs = lufs, PeakDb = pico, Target = -14 };

    [Theory]
    [InlineData(@"C:\m\cancion.flac")]
    [InlineData(@"C:\m\cancion.wav")]
    [InlineData(@"C:\m\cancion.m4a")]
    public void Lo_que_no_es_mp3_no_se_ofrece_como_ajustable(string archivo)
    {
        var f = Fila(archivo);
        Assert.False(f.Ajustable);
        Assert.Contains("no se ajusta", f.Aviso);
    }

    [Fact]
    public void Un_mp3_desviado_si_se_ajusta_y_no_lleva_aviso()
    {
        var f = Fila(@"C:\m\cancion.mp3");
        Assert.True(f.Ajustable);
        Assert.Equal("", f.Aviso);
    }

    // El aviso de "no es MP3" manda sobre el de saturacion: si no se va a tocar el archivo, que no
    // tenga margen para subirlo es una discusion que no viene al caso.
    [Fact]
    public void En_un_no_mp3_el_aviso_que_importa_es_que_no_se_toca()
    {
        var f = Fila(@"C:\m\cancion.flac", lufs: -20, pico: -0.1);   // sin margen para subir
        Assert.True(f.Satura);
        Assert.Contains("no se ajusta", f.Aviso);
    }

    // Y la medida se sigue mostrando igual: un FLAC se mide, solo que no se toca.
    [Fact]
    public void Un_no_mp3_se_sigue_midiendo()
    {
        var f = Fila(@"C:\m\cancion.flac", lufs: -20);
        Assert.Equal("Muy bajo", f.Estado);
        Assert.Equal("Aumentar 6,0 dB", f.Accion.Replace('.', ','));
    }

    // ---------- V1: umbral de 1 dB, el mismo que la banda «Correcto» ----------

    private static LoudnessRow ConGanancia(double ganancia, double pico, string archivo = @"C:\m\a.mp3")
        => new() { FilePath = archivo, Folder = @"C:\m", Lufs = -11 - ganancia, PeakDb = pico, Target = -11 };

    // Antes solo se ajustaba lo que se desviaba más de 2 dB, y quedaban sin tocar las que estaban entre
    // 1 y 2 dB: con pasos de 1,5 dB, ajustar cualquier desviación de más de 0,75 dB mejora.
    [Fact]
    public void Se_ajusta_lo_que_se_desvia_mas_de_1_dB()
    {
        Assert.Equal(1.0, LoudnessViewModel.UmbralAjuste);
        Assert.True(ConGanancia(-1.1, -3).Candidata);
        Assert.False(ConGanancia(-0.9, -3).Candidata);
        Assert.Equal(-1, LoudnessViewModel.PasosAplicables(-1.05, -3));   // 1,05 dB a la baja: un paso
    }

    // La banda «Correcto» y lo que se marca para ajustar usan el mismo umbral: una fila «Correcto»
    // nunca sale marcada, y toda fila fuera de la banda que admite algún paso es candidata.
    [Fact]
    public void Correcto_y_candidata_nunca_se_contradicen()
    {
        for (var g = -6.0; g <= 6.0001; g += 0.05)
            for (var pico = -10.0; pico <= 0.0001; pico += 0.25)
            {
                var f = ConGanancia(Math.Round(g, 2), pico);
                if (f.Candidata) Assert.NotEqual("Correcto", f.Estado);
                if (f.Ajustable && f.Estado != "Correcto" && LoudnessViewModel.PasosAplicables(f.Gain, f.PicoDecision) != 0)
                    Assert.True(f.Candidata, $"ganancia {f.Gain:0.00}, pico {pico:0.00}");
            }
    }

    // «Sin margen suficiente» solo donde se iba a aumentar: antes salía también en filas «Correcto» con
    // el pico alto, que nadie iba a tocar.
    [Fact]
    public void El_aviso_de_margen_solo_sale_en_las_que_se_aumentarian()
    {
        var correcta = ConGanancia(0.6, -0.2);
        Assert.Equal("Correcto", correcta.Estado);
        Assert.Equal("", correcta.Aviso);

        var sinMargen = ConGanancia(3, -0.2);
        Assert.StartsWith("Sin margen suficiente", sinMargen.Aviso);
        Assert.Contains("distorsión", sinMargen.Aviso);

        var aMedias = ConGanancia(4.5, -2.5);   // caben 2 dB: 1 paso de 3 deseados
        Assert.StartsWith("Sin margen suficiente", aMedias.Aviso);
        Assert.Contains("en parte", aMedias.Aviso);

        Assert.Equal("", ConGanancia(-3, -0.1).Aviso);   // bajar no depende del pico
    }

    // ---------- V3: la columna «Margen (dB)» y las decisiones usan el pico real si se conoce ----------

    [Fact]
    public void Con_pico_real_se_decide_con_el_y_si_no_con_el_de_muestra()
    {
        var sinReal = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -3.0, Target = -11 };
        var conReal = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -3.0, TruePeakDb = -1.4, Target = -11 };

        Assert.Equal(-3.0, sinReal.PicoDecision);
        Assert.Equal(-1.4, conReal.PicoDecision);
        Assert.Equal("-1,4", conReal.PeakText.Replace('.', ','));
        Assert.True(sinReal.Candidata);
        Assert.False(conReal.Candidata);   // con el pico real no cabe ni un paso
        Assert.StartsWith("Sin margen suficiente", conReal.Aviso);

        // Un pico real «por debajo» del de muestra no puede rebajar la decisión.
        var raro = new LoudnessRow { FilePath = @"C:\m\a.mp3", Lufs = -16, PeakDb = -3.0, TruePeakDb = -4.0, Target = -11 };
        Assert.Equal(-3.0, raro.PicoDecision);
    }
}
