using System;
using Avalonia;
using Avalonia.Styling;
using Etiquetador.Core;

namespace Etiquetador.App.Services;

/// <summary>
/// Cómo se ve la aplicación: tema claro/oscuro y densidad de las tablas.
///
/// Vive en un solo sitio y se aplica sobre los recursos de la aplicación, no sobre cada vista. Eso
/// es lo que permite que cambiar el tema o la densidad en Ajustes se note al instante en las trece
/// pestañas sin tener que reabrir nada ni tocar sus XAML.
/// </summary>
public static class Apariencia
{
    /// <summary>Altura de fila normal y compacta, en pixeles.</summary>
    private const double FilaNormal = 34;
    private const double FilaCompacta = 26;

    /// <summary>Las tres opciones, tal como se leen en Ajustes.</summary>
    public const string Sistema = "sistema";
    public const string Claro = "claro";
    public const string Oscuro = "oscuro";

    /// <summary>Aplica lo que diga la configuración. Se llama al arrancar y al cambiarlo.</summary>
    public static void Aplicar(AppConfig config)
    {
        AplicarTema(config.Theme);
        AplicarDensidad(config.CompactRows);
    }

    /// <summary>
    /// «sistema» deja que mande el sistema operativo; las otras dos lo fuerzan.
    ///
    /// Merece la pena poder forzarlo aunque el sistema ya se siga solo: quien pincha de noche no
    /// quiere que Windows le encienda la pantalla en blanco a las siete de la mañana.
    /// </summary>
    public static void AplicarTema(string? tema)
    {
        var app = Application.Current;
        if (app == null) return;

        app.RequestedThemeVariant = (tema ?? Sistema).Trim().ToLowerInvariant() switch
        {
            Claro => ThemeVariant.Light,
            Oscuro => ThemeVariant.Dark,
            _ => ThemeVariant.Default,   // lo que diga el sistema
        };
    }

    /// <summary>
    /// Cambia la altura de fila de TODAS las tablas a la vez.
    ///
    /// Funciona porque el estilo global de DataGrid lee esta altura con DynamicResource: basta con
    /// cambiar el recurso para que las dieciocho vistas se enteren. La alternativa era poner una
    /// clase en cada tabla y acordarse de hacerlo también en la siguiente que se añada.
    /// </summary>
    public static void AplicarDensidad(bool compacta)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Resources["FilaAltura"] = compacta ? FilaCompacta : FilaNormal;
    }
}
