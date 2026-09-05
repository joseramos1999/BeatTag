using System.Text.RegularExpressions;

namespace Etiquetador.Tests;

/// <summary>
/// El número de cada pestaña está escrito a mano en DOS sitios: el ConverterParameter de cada
/// TabItem en MainWindow.axaml y la lista de pestañas de MainViewModel, más un puñado de constantes
/// para saltar a Editor, Tendencias, Ajustes o Ayuda.
///
/// Si se inserta una pestaña nueva y esos números no se corrigen todos, no falla nada al compilar:
/// la aplicación arranca y lo que ocurre es que se bloquea la pestaña equivocada mientras otra
/// trabaja, o «Editar esta canción» te lleva a otro sitio. Un fallo silencioso y difícil de atar.
/// Esto lo convierte en un fallo ruidoso.
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

    [Fact]
    public void Las_pestanas_estan_numeradas_en_orden_y_sin_huecos()
    {
        var xaml = File.ReadAllText(Fuente("Etiquetador.App", "Views", "MainWindow.axaml"));

        var numeros = Regex.Matches(xaml, @"ConverterParameter=(\d+)")
                           .Select(m => int.Parse(m.Groups[1].Value))
                           .ToList();

        Assert.NotEmpty(numeros);
        Assert.Equal(Enumerable.Range(0, numeros.Count).ToList(), numeros);
    }

    // La lista de MainViewModel tiene que cubrir todas las pestañas menos Ayuda, que no ejecuta
    // ningún proceso. Si sobra o falta una, el bloqueo mientras se trabaja apunta a la que no es.
    [Fact]
    public void La_lista_del_shell_cubre_todas_las_pestanas_con_proceso()
    {
        var xaml = File.ReadAllText(Fuente("Etiquetador.App", "Views", "MainWindow.axaml"));
        var vm = File.ReadAllText(Fuente("Etiquetador.App", "ViewModels", "MainViewModel.cs"));

        var pestanas = Regex.Matches(xaml, @"ConverterParameter=(\d+)").Count;

        // Las entradas de la tabla son de la forma: (() => Algo.IsBusy, "Nombre"),
        var entradas = Regex.Matches(vm, @"\(\(\) => [^,]+,\s*""[^""]+""\)").Count;

        Assert.Equal(pestanas - 1, entradas);   // -1: Ayuda no tiene proceso propio
    }
}
