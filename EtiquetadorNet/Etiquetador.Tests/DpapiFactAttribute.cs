using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Un [Fact] que solo se ejecuta si DPAPI funciona en este entorno.
///
/// DPAPI cifra ligado al usuario de Windows, asi que necesita un perfil cargado. En la maquina del
/// usuario siempre lo hay; en un proceso de pruebas sin perfil (un servidor de integracion, una
/// sesion de servicio) no, y esas pruebas fallaban por el entorno, no por el codigo. Un fallo que
/// no significa nada es peor que no tener la prueba: enseña a ignorar los rojos.
///
/// Se marcan como OMITIDAS, no como correctas: en el equipo del usuario siguen ejecutandose y
/// protegiendo lo que protegian.
/// </summary>
public sealed class DpapiFactAttribute : FactAttribute
{
    private static readonly bool Disponible = Probar();

    public DpapiFactAttribute()
    {
        if (!Disponible) Skip = "DPAPI no está disponible en este entorno (hace falta un perfil de Windows cargado).";
    }

    private static bool Probar()
    {
        try { return Dpapi.Protect("prueba").Length > 0; }
        catch { return false; }
    }
}
