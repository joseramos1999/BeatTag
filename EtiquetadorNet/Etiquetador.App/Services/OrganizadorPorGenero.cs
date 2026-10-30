using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.Services;

/// <summary>Cómo fue Organizar por género.</summary>
/// <param name="Hechos">Los traslados hechos: dónde estaba cada archivo y dónde está.</param>
/// <param name="Fallos">Los que no se movieron al llegar su turno, con el motivo («Tema.mp3: Está abierto en otro programa»).</param>
/// <param name="Cancelado">Se paró antes de terminar: cancelado, o detenido por el historial.</param>
/// <param name="Detenido">Por qué se paró porque el historial para deshacer no se podía escribir, o "" (hallazgo M10).</param>
/// <param name="UndoErr">Traslados hechos que NO quedaron anotados para deshacer ("" si ninguno).</param>
public sealed record ResultadoOrganizar(IReadOnlyList<(string Origen, string Destino)> Hechos, IReadOnlyList<string> Fallos,
                                        bool Cancelado, string Detenido, string UndoErr)
{
    public int Movidas => Hechos.Count;

    /// <summary>Archivos asociados (.asd, .cue, .lrc) movidos con sus canciones; no cuentan en <see cref="Movidas"/>.</summary>
    public int Asociados { get; init; }
}

/// <summary>
/// Ejecuta el plan de <see cref="OrganizarPorGenero"/>: mueve cada archivo a la carpeta de su género,
/// con todo anotado para deshacer. El plan ya ha mirado el disco; aquí se vuelve a comprobar cada
/// archivo justo antes de moverlo, porque entre la vista previa y el clic pueden pasar cosas.
///
/// EL ORDEN, MEDIDO como el único seguro (con el proceso matado a mitad 18 veces por modo): por cada
/// lote de <see cref="TamanoLote"/>,
///   1. se calculan las carpetas que hay que crear (las lleva el primer registro que las necesita);
///   2. se anota el lote entero ANTES de mover nada (una escritura y un volcado al disco). Si no se
///      puede, se para sin mover nada de ese lote;
///   3. se mueve archivo a archivo, mirando entre uno y otro si se ha cancelado, sin sobrescribir nunca;
///   4. se reescribe el lote con lo que de verdad se movió, y las carpetas creadas para un traslado que
///      falló se reparten entre los que sí se hicieron o, si se quedaron vacías, se quitan.
/// Anotar antes dejó 0 de 18 cortes con archivos movidos sin anotar (anotar después, 2 de 18; el patrón
/// de la Bandeja, 7 de 18), y deshacer devolvió el 100 %. Mueve unos 13.000 archivos en unos 6 s en
/// un disco NVMe, sin bloquear la interfaz.
/// </summary>
internal static class OrganizadorPorGenero
{
    /// <summary>Traslados por lote: con 100, anotar cuesta lo mismo que no anotar (418 µs por archivo frente a 408).</summary>
    public const int TamanoLote = 100;

    /// <summary>El motivo de un archivo que otro programa tiene abierto (un reproductor, rekordbox analizándolo).</summary>
    public const string Abierto = "Está abierto en otro programa";

    /// <param name="historial">El historial para deshacer; null si no se pudo abrir (el motivo, en <paramref name="errorHistorial"/>): entonces no se mueve nada.</param>
    /// <param name="seguir">Lo que sigue a cada archivo movido (cachés, ficha, decisiones): origen, destino y sello de antes.</param>
    /// <param name="mover">Cómo se mueve un archivo. Por defecto, File.Move sin sobrescribir; las pruebas simulan un archivo abierto.</param>
    internal static ResultadoOrganizar Ejecutar(PlanOrganizar plan, IHistorialPorLotes? historial, string errorHistorial,
                                                Action<string, string, SelloArchivo>? seguir, Logger? log,
                                                IProgress<double>? progreso, CancellationToken ct,
                                                Action<string, string>? mover = null)
    {
        var hechos = new List<(string Origen, string Destino)>();
        var fallos = new List<string>();
        if (historial == null)
            return new ResultadoOrganizar(hechos, fallos, true, AppEngine.SinHistorial(errorHistorial, log), "");
        mover ??= (origen, destino) => File.Move(origen, destino, overwrite: false);

        var reclamadas = new HashSet<string>(ComparadorRutas.Instancia);
        var total = plan.Movimientos.Count;
        var cancelado = false;
        var detenido = "";
        var asociadosMovidos = 0;
        for (var inicio = 0; inicio < total && !cancelado; inicio += TamanoLote)
        {
            if (ct.IsCancellationRequested) { cancelado = true; break; }
            var lote = plan.Movimientos.Skip(inicio).Take(TamanoLote).ToList();

            // 1. Las carpetas que faltan, cada una para el primer registro que la necesita. Cada canción
            //    lleva detrás un registro por cada archivo asociado que va con ella (ver ExtensionesAsociadas).
            var registros = new List<UndoRecord>(lote.Count);
            var deCadaUno = new List<(UndoRecord Cancion, List<UndoRecord> Asociados)>(lote.Count);
            var creadasDelLote = new List<string>();
            foreach (var m in lote)
            {
                var suyas = CarpetasQueFaltan(Path.GetDirectoryName(m.Destino) ?? "").Where(reclamadas.Add).ToList();
                creadasDelLote.AddRange(suyas);
                var cancion = new UndoRecord
                {
                    OrigPath = m.Origen, FinalPath = m.Destino, Renamed = true, Origen = nameof(TipoEjecucion.OrganizarPorGenero),
                    CarpetasCreadas = suyas.Count > 0 ? suyas : null,
                };
                var asociados = m.Asociados.Select(a => new UndoRecord
                {
                    OrigPath = a, FinalPath = m.DestinoDe(a), Renamed = true, Origen = nameof(TipoEjecucion.OrganizarPorGenero),
                }).ToList();
                registros.Add(cancion);
                registros.AddRange(asociados);
                deCadaUno.Add((cancion, asociados));
            }

            // 2. Todo el lote anotado antes de mover nada.
            var posicion = historial.Posicion;
            var anotado = posicion < 0 ? "el historial no responde" : historial.AnotarLote(registros);
            if (anotado.Length > 0)
            {
                detenido = AppEngine.Parada(lote[0].Origen, anotado, log);
                break;
            }

            // 3. Canción a canción, cada una con sus asociados detrás.
            var hechosLote = new List<UndoRecord>(registros.Count);
            for (var i = 0; i < lote.Count; i++)
            {
                if (ct.IsCancellationRequested) { cancelado = true; break; }
                var m = lote[i];
                var error = MoverUno(m, mover, seguir, log);
                if (error.Length == 0)
                {
                    hechosLote.Add(deCadaUno[i].Cancion);
                    hechos.Add((m.Origen, m.Destino));
                    foreach (var r in deCadaUno[i].Asociados)
                    {
                        var motivo = MoverAsociado(r.OrigPath, r.FinalPath, m.CarpetaGenero, mover);
                        if (motivo == null) continue;   // ya no está: no hay nada que llevar ni que anotar
                        if (motivo.Length == 0)
                        {
                            hechosLote.Add(r);
                            asociadosMovidos++;
                            continue;
                        }
                        // La canción ya se movió y se queda movida: su asociado se queda donde estaba y se dice.
                        fallos.Add($"{Path.GetFileName(r.OrigPath)}: {motivo}");
                        log?.Log($"Organizar por género: no se movió «{Path.GetFileName(r.OrigPath)}», que va con «{Path.GetFileName(m.Origen)}»: {motivo}", LogKind.Err);
                    }
                }
                else
                {
                    fallos.Add($"{Path.GetFileName(m.Origen)}: {error}");
                    log?.Log($"Organizar por género: no se movió «{Path.GetFileName(m.Origen)}»: {error}", LogKind.Err);
                }
                progreso?.Report(100.0 * (inicio + i + 1) / total);
            }

            // 4. El lote, con lo que de verdad se hizo.
            RepartirCarpetas(creadasDelLote, hechosLote, reclamadas, log);
            var reescrito = historial.ReescribirDesde(posicion, hechosLote);
            if (reescrito.Length > 0)
            {
                // ReescribirDesde nunca deja lo hecho solo en memoria: sigue anotado (en el lote tal como
                // se anotó, que es un superconjunto, o en lo reescrito), así que todo lo movido se puede
                // deshacer. Pero no se sigue moviendo sin un historial que se deje escribir.
                detenido = $"Se ha detenido: no se pudo poner al día el historial para deshacer ({reescrito}). "
                         + "Lo movido hasta aquí se puede deshacer; lo demás se ha quedado como estaba. "
                         + "Revisa que la carpeta de datos tenga espacio libre y permiso de escritura.";
                log?.Err(detenido);
                break;
            }
        }

        log?.Detail($"Organizar por género: {hechos.Count} movida(s) · {asociadosMovidos} asociado(s) · {fallos.Count} sin mover"
                    + (cancelado ? " · cancelado" : "") + (detenido.Length > 0 ? " · detenido" : "") + ".");
        return new ResultadoOrganizar(hechos, fallos, cancelado || detenido.Length > 0, detenido, "") { Asociados = asociadosMovidos };
    }

    /// <summary>
    /// Lleva un archivo asociado a la carpeta a la que acaba de ir su canción. "" si fue bien, null si ya
    /// no está (no hay nada que llevar), o el motivo. Nunca sobrescribe. No hay nada que lo siga: las
    /// cachés y lo decidido en Enriquecer son de la canción.
    /// </summary>
    private static string? MoverAsociado(string origen, string destino, string carpeta, Action<string, string> mover)
    {
        try
        {
            if (!File.Exists(origen)) return null;
            if (File.Exists(destino) || Directory.Exists(destino)) return $"Ya hay un archivo con ese nombre en «{carpeta}»; no se sobrescribe";
            mover(origen, destino);
            return "";
        }
        catch (IOException e) when (EsArchivoAbierto(e)) { return Abierto; }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>Mueve un archivo del plan. "" si fue bien, o el motivo.</summary>
    private static string MoverUno(MovimientoOrganizar m, Action<string, string> mover, Action<string, string, SelloArchivo>? seguir, Logger? log)
    {
        try
        {
            if (!File.Exists(m.Origen)) return "Ya no está en su carpeta";
            if (File.Exists(m.Destino) || Directory.Exists(m.Destino))
                return $"Ya hay un archivo con ese nombre en «{m.CarpetaGenero}»; no se sobrescribe";
            var sello = SelloArchivo.De(m.Origen);
            Directory.CreateDirectory(Path.GetDirectoryName(m.Destino)!);
            mover(m.Origen, m.Destino);
            try { seguir?.Invoke(m.Origen, m.Destino, sello); }
            catch (Exception e) { log?.Error($"Organizar por género: «{Path.GetFileName(m.Destino)}» se movió, pero no se pudo poner al día lo que lo sigue", e); }
            return "";
        }
        catch (IOException e) when (EsArchivoAbierto(e)) { return Abierto; }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>El sistema no deja moverlo porque otro programa lo tiene abierto (en Windows; macOS sí lo deja).</summary>
    internal static bool EsArchivoAbierto(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION

    /// <summary>Las carpetas que faltan hasta <paramref name="carpeta"/>, de la más alta a la más profunda.</summary>
    internal static List<string> CarpetasQueFaltan(string carpeta)
    {
        var faltan = new List<string>();
        for (var c = carpeta; !string.IsNullOrEmpty(c) && !Directory.Exists(c); c = Path.GetDirectoryName(c))
            faltan.Add(c);
        faltan.Reverse();
        return faltan;
    }

    /// <summary>
    /// Las carpetas creadas en el lote pasan a los traslados que de verdad se hicieron: cada una la lleva
    /// el primero que movió un archivo dentro (de la más alta a la más profunda, como espera deshacer).
    /// Las que no son de ninguno se quitan si se quedaron vacías (el traslado para el que se crearon
    /// falló) y dejan de estar reclamadas: un lote posterior puede volver a necesitarlas.
    /// </summary>
    private static void RepartirCarpetas(List<string> creadas, List<UndoRecord> hechos, HashSet<string> reclamadas, Logger? log)
    {
        if (creadas.Count == 0) return;
        foreach (var r in hechos) r.CarpetasCreadas = null;
        var arriba = creadas.OrderBy(c => c.Length).ToList();
        var conDueno = new HashSet<string>(ComparadorRutas.Instancia);
        foreach (var c in arriba)
        {
            var dueno = hechos.FirstOrDefault(h => Rutas.EstaDentroDe(h.FinalPath, c));
            if (dueno == null) continue;
            (dueno.CarpetasCreadas ??= new List<string>()).Add(c);
            conDueno.Add(c);
        }
        for (var i = arriba.Count - 1; i >= 0; i--)
        {
            var c = arriba[i];
            if (conDueno.Contains(c)) continue;
            reclamadas.Remove(c);
            try
            {
                if (Directory.Exists(c) && !Directory.EnumerateFileSystemEntries(c).Any()) Directory.Delete(c, recursive: false);
            }
            catch (Exception e) { log?.Log($"Organizar por género: no se pudo quitar la carpeta vacía «{c}»: {e.Message}", LogKind.Err); }
        }
    }
}
