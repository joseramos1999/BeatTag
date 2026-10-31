using System;
using System.Linq;
using System.Threading;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.Services;

/// <summary>
/// Ejecuta el plan de <see cref="OrganizarPorGenero"/>: mueve cada archivo a la carpeta de su género,
/// con sus archivos asociados, por lotes y con todo anotado para deshacer. El motor, con el orden que
/// se midió como el único seguro, es <see cref="TrasladoPorLotes"/>, el mismo que usa la Bandeja.
/// </summary>
internal static class OrganizadorPorGenero
{
    /// <param name="historial">El historial para deshacer; null si no se pudo abrir (el motivo, en <paramref name="errorHistorial"/>): entonces no se mueve nada.</param>
    /// <param name="seguir">Lo que sigue a cada archivo movido (cachés, ficha, decisiones): origen, destino y sello de antes.</param>
    /// <param name="mover">Cómo se mueve un archivo. Por defecto, File.Move sin sobrescribir; las pruebas simulan un archivo abierto.</param>
    internal static ResultadoTraslado Ejecutar(PlanOrganizar plan, IHistorialPorLotes? historial, string errorHistorial,
                                               Action<string, string, SelloArchivo>? seguir, Logger? log,
                                               IProgress<double>? progreso, CancellationToken ct,
                                               Action<string, string>? mover = null)
    {
        var movimientos = plan.Movimientos
            .Select(m => new MovimientoArchivo(m.Origen, m.Destino, m.CarpetaGenero) { Asociados = m.Asociados })
            .ToList();
        return TrasladoPorLotes.Ejecutar(movimientos, TipoEjecucion.OrganizarPorGenero, "Organizar por género",
                                         historial, errorHistorial, seguir, log, progreso, ct, mover);
    }
}
