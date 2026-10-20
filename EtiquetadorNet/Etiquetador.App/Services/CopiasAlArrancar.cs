using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Etiquetador.Core;
using Etiquetador.Core.Copias;

namespace Etiquetador.App.Services;

/// <summary>
/// Lo que la copia de seguridad hace al arrancar (equipo «volumen_copias», tanda 4, T15). Lo llama el
/// constructor del motor, que ya corre fuera del hilo de la interfaz.
/// </summary>
internal static class CopiasAlArrancar
{
    /// <summary>Clave del aviso de la ventana con el resultado de una restauración.</summary>
    public const string ClaveAviso = "copia:restauracion";

    /// <summary>
    /// Completa la restauración que se dejó preparada en Ajustes, si la hay. Tiene que ir ANTES de
    /// leer ningún archivo de datos: con ellos ya en memoria, el primer guardado pisaría lo restaurado.
    /// El resultado se dice en la banda de avisos de la ventana, no solo en el registro.
    /// </summary>
    public static void CompletarRestauracion(AppPaths paths, Logger log, AvisosApp avisos)
    {
        ResultadoRestauracion? r;
        try { r = CopiaSeguridad.AplicarRestauracionPendiente(paths, DateTime.Now); }
        catch (Exception e)
        {
            log.Error("No se pudo completar la restauración de la copia de seguridad", e);
            avisos.Poner(ClaveAviso, "No se pudo completar la restauración de la copia de seguridad: " + e.Message);
            return;
        }
        if (r == null) return;
        if (r.Ok) log.Sum($"Copia de seguridad restaurada: {r.Restaurados} archivos · copia previa: {Path.GetFileName(r.CopiaPrevia)}");
        else log.Err("Restauración de la copia de seguridad: " + r.Texto);
        avisos.Poner(ClaveAviso, r.Texto);
    }

    /// <summary>
    /// La copia automática del arranque, en un hilo de fondo de prioridad baja: no retrasa la apertura
    /// de la ventana. Copia el estado con el que se cerró BeatTag la vez anterior (se lanza antes de
    /// que el motor cambie nada) y solo si es distinto de la última copia; se conservan las 5 últimas.
    /// </summary>
    public static void IniciarCopiaAutomatica(AppPaths paths, Logger log)
    {
        var hilo = new Thread(() =>
        {
            try
            {
                var reloj = Stopwatch.StartNew();
                var r = CopiaSeguridad.CopiaAutomatica(paths, DateTime.Now);
                if (r == null) { log.Detail($"Copia automática: sin cambios desde la anterior ({reloj.ElapsedMilliseconds} ms)."); return; }
                if (!r.Ok) { log.Err("No se pudo hacer la copia automática de los datos: " + r.Error); return; }
                log.Detail($"Copia automática: {Path.GetFileName(r.Ruta)} · {r.Archivos} archivos · {TextosCopia.Tamano(r.Bytes)} · {reloj.ElapsedMilliseconds} ms");
                foreach (var a in r.Avisos) log.Err("Copia automática: " + a);
            }
            catch (Exception e) { log.Error("Copia automática de los datos", e); }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Copia automática",
        };
        hilo.Start();
    }
}
