namespace Etiquetador.Tests;

/// <summary>
/// Un [Fact] que solo se ejecuta en Windows.
///
/// Para reglas que son de Windows y de nadie más, no para pruebas mal escritas. El ejemplo que lo
/// motivó: "A: B.mp3" se rechaza como nombre de archivo porque en Windows parece una unidad, y
/// Path.IsPathRooted lo confirma. En macOS ese mismo texto es un nombre de archivo perfectamente
/// legal, así que aceptarlo allí no es un fallo: es lo correcto.
///
/// Se marcan como OMITIDAS, no como correctas: en Windows siguen ejecutándose y protegiendo lo que
/// protegían.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Regla propia de Windows: en otros sistemas no aplica.";
    }
}
