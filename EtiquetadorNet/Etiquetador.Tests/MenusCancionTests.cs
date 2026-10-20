using System.Text.RegularExpressions;
using Etiquetador.App.Acciones;

namespace Etiquetador.Tests;

/// <summary>
/// El menú contextual de las tablas con canciones sigue el mismo orden en todas las pestañas
/// (hallazgo interfaz-7 de la revisión de la tanda 3): la misma canción ofrecía «Abrir carpeta
/// contenedora» en segunda, cuarta o última posición, y Colecciones, Tendencias y Playlists no tenían
/// «Editar». El orden está en <see cref="AccionesCancion.Puesto"/>; estas pruebas leen las vistas, porque
/// un menú desordenado compila y funciona igual, y nadie lo notaría hasta usarlo.
/// </summary>
public class MenusCancionTests
{
    /// <summary>Sube desde el binario de pruebas hasta encontrar la raíz de la solución (como PestanasTests).</summary>
    private static string Fuente(params string[] tramos)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "MainWindow.axaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, Path.Combine(tramos));
    }

    /// <summary>Los rótulos del menú contextual de la tabla de una vista XAML, en orden.</summary>
    private static List<string> MenuXaml(string vista)
    {
        var texto = File.ReadAllText(Fuente("Etiquetador.App", "Views", vista + ".axaml"));
        var bloque = Regex.Match(texto, @"<DataGrid\.ContextMenu>(.*?)</DataGrid\.ContextMenu>", RegexOptions.Singleline);
        Assert.True(bloque.Success, vista + " no tiene menú contextual en su tabla");
        return Regex.Matches(bloque.Groups[1].Value, @"<MenuItem\s+Header=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
    }

    private static void EnElOrdenComun(string donde, IReadOnlyList<string> rotulos)
    {
        var puestos = rotulos.Select(AccionesCancion.Puesto).Where(p => p >= 0).ToList();
        Assert.True(puestos.Count >= 2, $"{donde}: no se reconoce ninguna opción común");
        for (var i = 1; i < puestos.Count; i++)
            Assert.True(puestos[i - 1] < puestos[i], $"{donde}: «{string.Join(" · ", rotulos)}» no sigue el orden común");
    }

    [Theory]
    [InlineData("ColeccionesView")]
    [InlineData("FichasView")]
    [InlineData("TrendsView")]
    [InlineData("PlaylistView")]
    public void Las_tablas_con_canciones_tienen_las_mismas_opciones_en_el_mismo_orden(string vista)
    {
        var menu = MenuXaml(vista);

        EnElOrdenComun(vista, menu);
        foreach (var comun in new[] { AccionesCancion.TextoEscuchar, AccionesCancion.TextoEditar, AccionesCancion.TextoCompatibles,
                                      AccionesCancion.TextoVersiones, AccionesCancion.TextoCarpeta })
            Assert.Contains(comun, menu);
    }

    [Theory]
    [InlineData("CompatiblesDialog")]
    [InlineData("VersionesDialog")]
    [InlineData("MezclaDialog")]
    public void Los_dialogos_de_mezcla_siguen_el_mismo_orden(string vista)
        => EnElOrdenComun(vista, MenuXaml(vista));

    // El de Biblioteca se monta en el código (sus rótulos dependen de la selección): se leen los
    // rótulos de cada opción y el orden en que se añaden.
    [Fact]
    public void El_menu_de_la_biblioteca_sigue_el_orden_comun()
    {
        var codigo = File.ReadAllText(Fuente("Etiquetador.App", "Views", "LibraryView.axaml.cs"));
        var rotulos = Regex.Matches(codigo, @"var (\w+) = Opcion\(""([^""]+)""")
                           .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var orden = Regex.Match(codigo, @"foreach \(var c in new Control\[\] \{([^}]*)\}\)\s*menu\.Items\.Add");
        Assert.True(orden.Success, "no se encontró cómo se monta el menú de la Biblioteca");
        var menu = orden.Groups[1].Value.Split(',').Select(s => s.Trim())
                        .Where(rotulos.ContainsKey).Select(n => rotulos[n]).ToList();

        EnElOrdenComun("Biblioteca", menu);
        Assert.Equal(new[] { AccionesCancion.TextoEscuchar, AccionesCancion.TextoEditar, AccionesCancion.TextoCompatibles,
                             AccionesCancion.TextoVersiones, "Ordenar para mezclar la selección…", AccionesCancion.TextoCarpeta,
                             AccionesCancion.TextoPapelera }, menu);
    }

    [Fact]
    public void Ordenar_y_empezar_por_esta_ocupan_el_mismo_puesto_y_lo_ajeno_no_tiene()
    {
        Assert.Equal(AccionesCancion.Puesto("Ordenar para mezclar la selección (12)…"), AccionesCancion.Puesto("Empezar por esta"));
        Assert.True(AccionesCancion.Puesto(AccionesCancion.TextoVersiones) < AccionesCancion.Puesto("Empezar por esta"));
        Assert.True(AccionesCancion.Puesto("Empezar por esta") < AccionesCancion.Puesto(AccionesCancion.TextoCarpeta));
        Assert.True(AccionesCancion.Puesto("Enviar a la papelera (3) (espera a que termine el proceso)")
                    < AccionesCancion.Puesto("Añadir a pendientes"));
        Assert.Equal(-1, AccionesCancion.Puesto("Marcar las seleccionadas"));
    }
}
