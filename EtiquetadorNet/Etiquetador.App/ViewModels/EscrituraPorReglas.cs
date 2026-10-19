using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Escribe en los archivos lo que proponen las herramientas de reglas del Asistente («Limpiar la
/// publicidad de los packs», «Género por carpeta y por artista»), con las mismas garantías que
/// «Unificar géneros» (AppEngine.EscribirGeneros):
///   · Cada cambio se anota en el historial para deshacer ANTES de tocar el archivo. Si el historial
///     no se puede abrir, no se toca ningún archivo; si deja de poder escribirse a mitad, se para ahí
///     y lo anterior sigue pudiéndose deshacer (hallazgo M10).
///   · Solo se escribe lo que sigue como se vio al proponer; lo que el usuario cambió después se
///     respeta y se cuenta aparte.
///   · Solo cambian etiquetas: las cachés de audio (huellas, «Comprobar audio», sonoridad) siguen
///     valiendo y se les avisa con el sello de antes de escribir.
/// </summary>
internal static class EscrituraPorReglas
{
    /// <param name="Escritos">Archivos en los que se ha escrito algo.</param>
    /// <param name="Respetados">Archivos con algún campo que cambió después de proponerlo y se ha dejado como estaba.</param>
    /// <param name="UndoErr">Cambios que están en el archivo y NO quedaron anotados tal cual ("" si ninguno).</param>
    /// <param name="Detenido">Por qué se paró porque el historial no se podía escribir, o "".</param>
    internal sealed record Resultado(IReadOnlyList<string> Escritos, IReadOnlyList<string> Respetados, IReadOnlyList<string> Fallos,
                                     string UndoErr, bool Cancelado, string Detenido = "");

    /// <summary>
    /// El bucle de escritura. <paramref name="historial"/> null significa que no se pudo abrir (el
    /// motivo, en <paramref name="errorHistorial"/>): entonces no se toca nada. Hay que llamarlo fuera
    /// del hilo de la interfaz y con el reproductor parado.
    /// </summary>
    internal static Resultado Escribir(IReadOnlyList<CambioEtiquetas> cambios, IHistorialDeshacer? historial, string errorHistorial,
        Action<string, string, SelloArchivo>? seguir, Logger? log, IProgress<double>? progreso, CancellationToken ct, string que)
    {
        var escritos = new List<string>();
        var respetados = new List<string>();
        var fallos = new List<string>();
        if (historial == null)
            return new Resultado(escritos, respetados, fallos, "", true, SinHistorial(errorHistorial, log));

        for (var i = 0; i < cambios.Count; i++)
        {
            if (ct.IsCancellationRequested) return new Resultado(escritos, respetados, fallos, "", true);
            var c = cambios[i];
            var ruta = c.Ruta;
            try
            {
                SelloArchivo? sello = null;
                Dictionary<string, FieldChange>? escrito = null;
                using (var f = TagLib.File.Create(ruta))
                {
                    var calculo = EtiquetasPorReglas.Calcular(f.Tag, c);
                    if (calculo.Respetados > 0) respetados.Add(ruta);
                    if (calculo.Cambios.Count > 0)
                    {
                        // Antes de tocar el archivo: si el historial ya no se puede escribir, este y los
                        // siguientes se quedan como estaban.
                        var anotado = historial.Anotar(Registro(ruta, calculo.Cambios));
                        if (anotado.Length > 0)
                            return new Resultado(escritos, respetados, fallos, "", true, Parada(ruta, anotado, log));

                        sello = SelloArchivo.De(ruta);
                        EtiquetasPorReglas.Poner(f.Tag, calculo.Cambios);
                        f.Save();
                        escrito = calculo.Cambios;
                    }
                }

                if (escrito != null)
                {
                    escritos.Add(ruta);
                    // Con el archivo ya cerrado, que es cuando su fecha queda al día.
                    if (sello is { } s) seguir?.Invoke(ruta, ruta, s);

                    if (EtiquetasPorReglas.HayQueReleer(escrito))
                    {
                        Dictionary<string, FieldChange> corregidos;
                        using (var releido = TagLib.File.Create(ruta)) corregidos = EtiquetasPorReglas.Corregidos(releido.Tag, escrito);
                        if (corregidos.Count > 0)
                        {
                            var anotado = historial.Anotar(Registro(ruta, corregidos));
                            if (anotado.Length > 0)
                            {
                                var sinAnotar = $"«{Path.GetFileName(ruta)}» quedó escrito, pero lo que guardó el archivo no se pudo anotar tal cual ({anotado}): deshacerlo no lo devolverá";
                                log?.Err($"{que}: {sinAnotar}");
                                return new Resultado(escritos, respetados, fallos, sinAnotar, true, Parada(ruta, anotado, log));
                            }
                        }
                    }
                    log?.Detail($"{que}: {Path.GetFileName(ruta)} · {string.Join(", ", escrito.Keys)}");
                }
            }
            catch (Exception e)
            {
                fallos.Add(Path.GetFileName(ruta) + ": " + e.Message);
                log?.Log($"{que}: {Path.GetFileName(ruta)}: {e.Message}", LogKind.Err);
            }
            progreso?.Report(100.0 * (i + 1) / cambios.Count);
        }
        log?.Detail($"{que}: {escritos.Count} archivos escritos · {respetados.Count} con cambios posteriores respetados · {fallos.Count} fallos.");
        return new Resultado(escritos, respetados, fallos, "", false);
    }

    private static UndoRecord Registro(string ruta, Dictionary<string, FieldChange> campos)
        => new() { OrigPath = ruta, FinalPath = ruta, Renamed = false, Fields = new Dictionary<string, FieldChange>(campos) };

    /// <summary>El historial no se pudo ni abrir: no se ha tocado nada.</summary>
    private static string SinHistorial(string error, Logger? log)
    {
        var texto = $"No se ha tocado ningún archivo: no se pudo crear el historial para deshacer ({error}). " +
                    "Revisa que la carpeta de datos tenga espacio libre y permiso de escritura.";
        log?.Err(texto);
        return texto;
    }

    /// <summary>El historial dejó de poder escribirse a mitad: se para en ese archivo.</summary>
    private static string Parada(string ruta, string error, Logger? log)
    {
        var texto = $"Se ha detenido en «{Path.GetFileName(ruta)}»: no se pudo anotar en el historial para deshacer ({error}). " +
                    "Ese archivo y los siguientes se han quedado como estaban; lo anterior sí se puede deshacer. " +
                    "Revisa que la carpeta de datos tenga espacio libre y permiso de escritura.";
        log?.Err(texto);
        return texto;
    }

    /// <summary>
    /// Lo mismo, con el historial de verdad (un manifiesto nuevo de esta ejecución) y las cachés del
    /// motor. Al terminar se guardan la identificación y la sonoridad, que pesan poco; las huellas
    /// (más de 150 MB en una biblioteca grande) las guarda el propio motor cada cinco minutos y al
    /// cerrar, y una entrada que no llegara a guardarse solo se volvería a calcular.
    /// </summary>
    internal static Resultado EscribirConHistorial(AppEngine engine, IReadOnlyList<CambioEtiquetas> cambios,
        IProgress<double>? progreso, CancellationToken ct, string que)
    {
        Resultado res;
        using (var manifiesto = ManifiestoDeshacer.Abrir(engine.Paths.NuevoManifiesto(), out var error))
            res = Escribir(cambios, manifiesto, error, engine.SeguirArchivo, engine.Logger, progreso, ct, que);
        if (res.Escritos.Count > 0)
        {
            try { engine.Identificacion.Save(); engine.Loudness.Save(); }
            catch (Exception e) { engine.Logger.Error("Guardar las cachés de audio tras " + que, e); }
        }
        return res;
    }

    /// <summary>El final del estado, igual en las dos herramientas: fallos, parada, cancelación y si se puede deshacer.</summary>
    internal static string Cola(Resultado res)
    {
        var texto = "";
        if (res.Respetados.Count > 0)
            texto += $" · {res.Respetados.Count} se han dejado como estaban porque cambiaron después de proponerlas";
        if (res.Fallos.Count > 0) texto += $" · {res.Fallos.Count} no se pudieron escribir ({res.Fallos[0]})";
        if (res.Detenido.Length > 0) texto += " · ⚠ " + res.Detenido.TrimEnd('.');
        else if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ Un cambio NO quedó anotado tal cual y no se podrá deshacer: {res.UndoErr}"
            : res.Escritos.Count > 0 ? ". Se puede deshacer desde Enriquecer con «Deshacer última»." : ".";
        return texto;
    }
}
