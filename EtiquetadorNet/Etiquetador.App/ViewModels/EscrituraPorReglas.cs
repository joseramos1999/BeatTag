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
    /// <param name="origen">
    /// Qué herramienta escribe, para el historial de cambios (<see cref="UndoRecord.Origen"/>): con los
    /// campos solos, una limpieza de packs (álbum, artista y comentario) no se distingue del Editor.
    /// </param>
    internal static Resultado Escribir(IReadOnlyList<CambioEtiquetas> cambios, IHistorialDeshacer? historial, string errorHistorial,
        Action<string, string, SelloArchivo>? seguir, Logger? log, IProgress<double>? progreso, CancellationToken ct, string que,
        TipoEjecucion? origen = null)
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
                // Un archivo que ya no está (enviado a la papelera desde otra tabla, movido) se dice en
                // español; TagLib lo diría en inglés («Could not find file…»).
                if (!File.Exists(ruta))
                {
                    fallos.Add(Path.GetFileName(ruta) + ": " + YaNoEsta);
                    log?.Log($"{que}: {Path.GetFileName(ruta)}: {YaNoEsta}", LogKind.Err);
                    progreso?.Report(100.0 * (i + 1) / cambios.Count);
                    continue;
                }

                SelloArchivo? sello = null;
                Dictionary<string, FieldChange>? escrito = null, anotadoTalCual = null;
                using (var f = Tagging.AbrirParaEscribir(ruta))   // TagLib no escribe AIFF-C: así el motivo sale en español
                {
                    var calculo = EtiquetasPorReglas.Calcular(f.Tag, c);
                    if (calculo.Respetados > 0) respetados.Add(ruta);
                    if (calculo.Cambios.Count > 0)
                    {
                        // Se anota lo que el archivo va a leer después (una lista con «/» en ID3v2.3 vuelve
                        // partida): así queda UNA línea por archivo en el historial.
                        var aAnotar = EtiquetasPorReglas.ComoSeLeera(f, calculo.Cambios);

                        // Antes de tocar el archivo: si el historial ya no se puede escribir, este y los
                        // siguientes se quedan como estaban.
                        var anotado = historial.Anotar(Registro(ruta, aAnotar, origen));
                        if (anotado.Length > 0)
                            return new Resultado(escritos, respetados, fallos, "", true, Parada(ruta, anotado, log));

                        sello = SelloArchivo.De(ruta);
                        EtiquetasPorReglas.Poner(f.Tag, calculo.Cambios);
                        f.Save();
                        escrito = calculo.Cambios;
                        anotadoTalCual = aAnotar;
                    }
                }

                if (escrito != null && anotadoTalCual != null)
                {
                    escritos.Add(ruta);
                    // Con el archivo ya cerrado, que es cuando su fecha queda al día.
                    if (sello is { } s) seguir?.Invoke(ruta, ruta, s);

                    // La red de seguridad: si lo previsto no es lo que quedó, se anota lo que quedó.
                    if (EtiquetasPorReglas.HayQueReleer(escrito))
                    {
                        Dictionary<string, FieldChange> corregidos;
                        using (var releido = TagLib.File.Create(ruta)) corregidos = EtiquetasPorReglas.Corregidos(releido.Tag, anotadoTalCual);
                        if (corregidos.Count > 0)
                        {
                            var anotado = historial.Anotar(Registro(ruta, corregidos, origen));
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

    internal const string YaNoEsta = "ya no está en su carpeta: se ha movido, renombrado o enviado a la papelera";

    // Origen va con el nombre del tipo, como lo lee HistorialDeshacer.DeducirTipo; sin él, el
    // historial lo deduce de los campos, como en los manifiestos anteriores.
    private static UndoRecord Registro(string ruta, Dictionary<string, FieldChange> campos, TipoEjecucion? origen)
        => new()
        {
            OrigPath = ruta, FinalPath = ruta, Renamed = false, Fields = new Dictionary<string, FieldChange>(campos),
            Origen = origen?.ToString(),
        };

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
        IProgress<double>? progreso, CancellationToken ct, string que, TipoEjecucion origen)
    {
        Resultado res;
        using (var manifiesto = ManifiestoDeshacer.Abrir(engine.Paths.NuevoManifiesto(), out var error))
            res = Escribir(cambios, manifiesto, error, engine.SeguirArchivo, engine.Logger, progreso, ct, que, origen);
        if (res.Escritos.Count > 0)
        {
            try { engine.Identificacion.Save(); engine.Loudness.Save(); }
            catch (Exception e) { engine.Logger.Error("Guardar las cachés de audio tras " + que, e); }
        }
        return res;
    }

    /// <summary>«1 canción», «2 canciones»: el número con el sustantivo en singular o en plural.</summary>
    internal static string Cuenta(int n, string singular, string plural) => n == 1 ? "1 " + singular : $"{n:N0} {plural}";

    /// <summary>El final del estado, igual en las dos herramientas: fallos, parada, cancelación y si se puede deshacer.</summary>
    internal static string Cola(Resultado res)
    {
        var texto = "";
        if (res.Respetados.Count > 0)
            texto += res.Respetados.Count == 1
                ? " · 1 se ha dejado como estaba porque cambió después de proponerla"
                : $" · {res.Respetados.Count:N0} se han dejado como estaban porque cambiaron después de proponerlas";
        if (res.Fallos.Count > 0)
            texto += res.Fallos.Count == 1
                ? $" · 1 no se pudo escribir ({res.Fallos[0]})"
                : $" · {res.Fallos.Count:N0} no se pudieron escribir (la primera, {res.Fallos[0]})";
        if (res.Detenido.Length > 0) texto += " · ⚠ " + res.Detenido.TrimEnd('.');
        else if (res.Cancelado) texto += " · cancelado a medias";
        texto += res.UndoErr.Length > 0
            ? $". ⚠ Un cambio NO quedó anotado tal cual y no se podrá deshacer: {res.UndoErr}"
            : res.Escritos.Count > 0 ? ". Se puede deshacer desde Enriquecer con «Deshacer última»." : ".";
        return texto;
    }
}
