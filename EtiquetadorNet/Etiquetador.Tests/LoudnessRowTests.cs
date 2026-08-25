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
}
