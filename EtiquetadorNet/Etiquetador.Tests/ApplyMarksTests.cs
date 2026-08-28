using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Las casillas "Aplicar" que el usuario toca a mano en Enriquecer. Revisar cientos de propuestas
/// lleva su rato, y cerrar la aplicacion a media revision no puede tirar ese trabajo.
/// </summary>
public class ApplyMarksTests
{
    private static string TempFile() => Path.Combine(Mp3Fixture.NewTempDir(), "marcas.json");

    // Lo que NO ha tocado el usuario no tiene decision: su valor por defecto lo pone el analisis.
    [Fact]
    public void Sin_tocar_no_hay_decision()
        => Assert.Null(new ApplyMarks(TempFile()).Get(@"C:\m\a.mp3"));

    [Fact]
    public void Se_recuerda_lo_marcado_y_lo_desmarcado()
    {
        var m = new ApplyMarks(TempFile());
        m.Set(@"C:\m\a.mp3", true);
        m.Set(@"C:\m\b.mp3", false);

        Assert.True(m.Get(@"C:\m\a.mp3"));
        Assert.False(m.Get(@"C:\m\b.mp3"));
    }

    // Lo importante: sobrevive a cerrar la aplicacion.
    [Fact]
    public void Sobrevive_a_cerrar_la_aplicacion()
    {
        var f = TempFile();
        try
        {
            var antes = new ApplyMarks(f);
            antes.Set(@"C:\m\a.mp3", false);
            antes.Save();

            var despues = new ApplyMarks(f);   // como si se hubiera reabierto la app
            Assert.False(despues.Get(@"C:\m\a.mp3"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(f)!, true); } catch { } }
    }

    // Aplicar o descartar una cancion cumple la decision: guardarla mas tiempo solo acumula basura.
    [Fact]
    public void Al_olvidarla_vuelve_a_no_haber_decision()
    {
        var m = new ApplyMarks(TempFile());
        m.Set(@"C:\m\a.mp3", false);
        m.Forget(@"C:\m\a.mp3");
        Assert.Null(m.Get(@"C:\m\a.mp3"));
    }

    // La ruta no distingue mayusculas: en Windows es el mismo archivo.
    [Fact]
    public void La_ruta_no_distingue_mayusculas()
    {
        var m = new ApplyMarks(TempFile());
        m.Set(@"C:\Musica\A.mp3", false);
        Assert.False(m.Get(@"c:\musica\a.mp3"));
    }

    // Un archivo corrupto no puede impedir arrancar: se empieza sin marcas y ya esta.
    [Fact]
    public void Un_archivo_corrupto_no_rompe_el_arranque()
    {
        var f = TempFile();
        try
        {
            File.WriteAllText(f, "{esto no es json");
            var m = new ApplyMarks(f);
            Assert.Equal(0, m.Count);
            Assert.Null(m.Get(@"C:\m\a.mp3"));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(f)!, true); } catch { } }
    }

    // Guardar sin cambios no reescribe el archivo: no hay por que tocar el disco en cada arranque.
    [Fact]
    public void Guardar_sin_cambios_no_escribe()
    {
        var f = TempFile();
        try
        {
            new ApplyMarks(f).Save();
            Assert.False(File.Exists(f));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(f)!, true); } catch { } }
    }
}
