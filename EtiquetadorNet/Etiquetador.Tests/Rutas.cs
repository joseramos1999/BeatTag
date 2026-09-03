namespace Etiquetador.Tests;

/// <summary>
/// Rutas de prueba válidas en la plataforma donde se ejecuten.
///
/// Muchas pruebas se escribieron con rutas de Windows a pelo (@"C:\m\cancion.mp3"). En Windows eso
/// funciona; en macOS y Linux la barra invertida NO separa carpetas, así que
/// Path.GetFileName(@"C:\m\cancion.mp3") devuelve la cadena entera y la prueba falla por la ruta,
/// no por lo que pretendía comprobar.
///
/// El producto no tiene ese problema -en un Mac las rutas reales serían /Users/…-, pero las pruebas
/// sí, y una prueba que falla por el sistema operativo en el que corre no dice nada útil.
/// </summary>
internal static class Rutas
{
    /// <summary>Raíz absoluta de la plataforma: "C:\" en Windows, "/" en macOS y Linux.</summary>
    public static string Raiz => OperatingSystem.IsWindows() ? @"C:\" : "/";

    /// <summary>Ruta absoluta a partir de sus tramos: De("m", "cancion.mp3").</summary>
    public static string De(params string[] tramos) => Path.Combine(Raiz, Path.Combine(tramos));

    /// <summary>Carpeta absoluta de prueba, la que usan casi todas: "C:\m" o "/m".</summary>
    public static string Carpeta => Path.Combine(Raiz, "m");

    /// <summary>Un archivo dentro de esa carpeta.</summary>
    public static string Archivo(string nombre) => Path.Combine(Carpeta, nombre);
}
