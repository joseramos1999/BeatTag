using System.Text.RegularExpressions;

namespace Etiquetador.Tests;

/// <summary>
/// Las páginas están descritas en dos sitios que tienen que decir lo mismo: la barra lateral, que
/// sale de MainViewModel.Grupos, y el contenido de MainWindow.axaml, donde vive una vista por
/// página marcada con su número.
///
/// Si se añade una entrada a la barra y no su vista, el botón lleva a una ventana en blanco. Si se
/// añade la vista y no la entrada, la pestaña existe pero no hay forma de llegar a ella. Ninguna de
/// las dos cosas rompe la compilación, y por eso están estas pruebas: es un fallo silencioso, y ya
/// pasó una vez con la lista de nombres del aviso de «proceso en curso».
/// </summary>
public class PestanasTests
{
    /// <summary>Sube desde el binario de pruebas hasta encontrar la raíz de la solución.</summary>
    private static string Fuente(params string[] tramos)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "MainWindow.axaml")))
            dir = dir.Parent;

        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, Path.Combine(tramos));
    }

    private static List<int> Numeros(string texto, string patron)
        => Regex.Matches(texto, patron).Select(m => int.Parse(m.Groups[1].Value)).ToList();

    /// <summary>Las páginas de la barra lateral (MainViewModel) y las vistas del contenido (XAML).</summary>
    private static (List<int> Nav, List<int> Vistas) Leer()
    {
        var vm = File.ReadAllText(Fuente("Etiquetador.App", "ViewModels", "MainViewModel.cs"));
        var xaml = File.ReadAllText(Fuente("Etiquetador.App", "Views", "MainWindow.axaml"));
        return (Numeros(vm, @"Indice = (\d+)"), Numeros(xaml, @"ConverterParameter=(\d+)"));
    }

    [Fact]
    public void La_barra_lateral_y_el_contenido_cubren_las_mismas_paginas()
    {
        var (nav, vistas) = Leer();

        Assert.NotEmpty(nav);
        Assert.Equal(nav.OrderBy(n => n), vistas.OrderBy(n => n));
    }

    // Sin huecos y sin repetidos: los números son el índice con el que se decide qué se ve y qué se
    // bloquea, así que dos páginas con el mismo número se taparían la una a la otra.
    [Fact]
    public void Las_paginas_estan_numeradas_de_cero_en_adelante_sin_repetir()
    {
        var (nav, _) = Leer();

        Assert.Equal(nav.Count, nav.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, nav.Count).ToList(), nav.OrderBy(n => n).ToList());
    }

    // Las constantes de salto ("Editar esta canción" va al Editor, entrar en Tendencias carga los
    // países) son números escritos a mano aparte. Si alguien renumera las páginas y se las deja,
    // los saltos llevan a otro sitio sin que nada falle al compilar.
    [Theory]
    [InlineData("LibraryTabIndex", "Biblioteca")]
    [InlineData("EditorTabIndex", "Editor")]
    [InlineData("IdentifyTabIndex", "Comprobar audio")]
    [InlineData("TrendsTabIndex", "Tendencias")]
    [InlineData("SettingsTabIndex", "Ajustes")]
    [InlineData("HelpTabIndex", "Ayuda")]
    public void Las_constantes_de_salto_apuntan_a_la_pagina_que_dicen(string constante, string nombre)
    {
        var vm = File.ReadAllText(Fuente("Etiquetador.App", "ViewModels", "MainViewModel.cs"));

        var declarada = Regex.Match(vm, @"const int " + constante + @" = (\d+);");
        Assert.True(declarada.Success, $"no se encontró la constante {constante}");

        // El índice con el que esa página aparece de verdad en la lista de la barra lateral.
        var enLaLista = Regex.Match(vm, @"Indice = (\d+),[^\n]*Nombre = ""“?" + Regex.Escape(nombre) + @"""");
        if (!enLaLista.Success)
            enLaLista = Regex.Match(vm, @"Indice = (\d+),[^\n]*Nombre = """ + Regex.Escape(nombre) + @"""");

        Assert.True(enLaLista.Success, $"no se encontró la página «{nombre}» en la barra lateral");
        Assert.Equal(enLaLista.Groups[1].Value, declarada.Groups[1].Value);
    }
}
