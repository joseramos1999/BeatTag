using Etiquetador.Core.Providers;
using Xunit;

namespace Etiquetador.Tests;

// Parte pura del listado de coincidencias (sin red): formato y deduplicado.
public class CandidateFinderTests
{
    private static Candidate C(string src, string a, string t, string alb = "", string y = "", int d = 0)
        => new(src, a, t, alb, y, d);

    [Fact]
    public void Duracion_en_minutos_y_segundos()
    {
        Assert.Equal("3:05", C("Deezer", "A", "B", d: 185).DurText);
        Assert.Equal("5:20", C("Deezer", "A", "B", d: 320).DurText);
        Assert.Equal("", C("Deezer", "A", "B", d: 0).DurText);   // fuente sin duración
    }

    [Fact]
    public void Display_incluye_album_year_y_duracion()
    {
        var c = C("Deezer", "Daft Punk", "One More Time", "Discovery", "2001", 320);
        Assert.Equal("Daft Punk — One More Time   (Discovery · 2001 · 5:20)", c.Display);
    }

    [Fact]
    public void Display_omite_lo_que_falta()
    {
        Assert.Equal("Daft Punk — One More Time", C("Deezer", "Daft Punk", "One More Time").Display);
    }

    [Fact]
    public void Dedup_quita_el_mismo_tema_de_otra_fuente()
    {
        var list = CandidateFinder.Dedup(new[]
        {
            C("Deezer", "Daft Punk", "One More Time"),
            C("iTunes", "daft punk", "ONE MORE TIME"),   // mismo tema, otra grafía
            C("iTunes", "Daft Punk", "Aerodynamic"),
        });
        Assert.Equal(2, list.Count);
        Assert.Equal("Deezer", list[0].Source);          // gana el primero que llegó
        Assert.Equal("Aerodynamic", list[1].Title);
    }

    [Fact]
    public void Dedup_conserva_las_versiones_distintas()
    {
        // Un remix NO es el mismo tema: debe seguir ofreciéndose como opción.
        var list = CandidateFinder.Dedup(new[]
        {
            C("Deezer", "Daft Punk", "One More Time"),
            C("Deezer", "Daft Punk", "One More Time (Short Radio Edit)"),
        });
        Assert.Equal(2, list.Count);
    }
}
