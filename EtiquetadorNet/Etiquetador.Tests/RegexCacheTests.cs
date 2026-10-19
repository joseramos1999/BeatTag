using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// N12 · Las expresiones regulares de la limpieza de nombres se volvían a construir en cada canción:
/// los métodos estáticos de Regex guardan solo 15 patrones y una lectura de un nombre recorre unos
/// 30. MEDIDO sobre 13.545 nombres reales: FileNameParser.Parse pasó de 5,2 s a 0,5 s, y la cadena
/// completa (lectura, consulta, versión, descriptores y título del catálogo) de 11,3 s a 2,8 s.
/// </summary>
public class RegexCacheTests
{
    [Fact]
    public void El_mismo_patron_es_la_misma_instancia()
    {
        var a = TextUtils.Rx(Descriptors.DescRe, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var b = TextUtils.Rx(Descriptors.DescRe, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.Same(a, b);
        // Con otras opciones es otra expresión.
        Assert.NotSame(a, TextUtils.Rx(Descriptors.DescRe));
    }

    // Leer el mismo nombre muchas veces da siempre lo mismo (la caché no deja estado entre llamadas).
    [Fact]
    public void La_lectura_del_nombre_no_depende_de_lo_leido_antes()
    {
        var primero = FileNameParser.Parse("Quevedo - EL BAIFO  Alex Torres Open Show.mp3");
        for (int i = 0; i < 50; i++)
        {
            FileNameParser.Parse("Behaviour (Extended Mix) - James Hype ft A.D.O.R. - 134bpm - DJTOOLSVIP.mp3");
            FileNameParser.Parse("Bad Gyal - 44.mp3");
        }
        Assert.Equal(primero, FileNameParser.Parse("Quevedo - EL BAIFO  Alex Torres Open Show.mp3"));
    }
}
