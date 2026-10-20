using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Etiquetador.App.Services;

/// <summary>Lo que dio enviar varios archivos a la papelera.</summary>
/// <param name="Enviadas">Rutas que ya están en la papelera, en el orden en que se pidieron.</param>
/// <param name="Fallos">Nombre del archivo y motivo de cada una que se quedó donde estaba.</param>
/// <param name="Interrumpido">Se canceló antes de terminar: las que faltaban no se han tocado.</param>
public sealed record EnvioPapeleraResultado(IReadOnlyList<string> Enviadas,
                                            IReadOnlyList<(string Archivo, string Motivo)> Fallos,
                                            bool Interrumpido = false);

/// <summary>
/// Envía varios archivos a la papelera FUERA del hilo de la interfaz, con progreso. Lo usan todas
/// las páginas que envían a la papelera: Biblioteca, el Asistente, Duplicados y la Bandeja.
///
/// POR QUÉ (resto del hallazgo T2): Duplicados recorría las copias marcadas en el hilo de la
/// interfaz. Cada envío pregunta a la unidad si tiene papelera y llama al shell de Windows; en macOS
/// lanza un osascript por archivo que puede esperar hasta 20 s si el sistema pide permiso. Con 300
/// copias, la ventana se quedaba congelada todo el bucle sin decir por dónde iba.
/// </summary>
public static class EnvioPapelera
{
    /// <summary>
    /// Envía las rutas en orden con <paramref name="enviar"/> y avisa del avance (hechas, total).
    /// No se detiene en un fallo: el que no se puede enviar se anota con su motivo y se sigue, igual
    /// que antes. Nunca lanza: si se cancela con <paramref name="ct"/>, para entre un archivo y el
    /// siguiente y devuelve lo hecho hasta ahí, porque quien llama tiene que quitar de la biblioteca y
    /// de la tabla las que ya están en la papelera.
    /// </summary>
    public static EnvioPapeleraResultado Enviar(IReadOnlyList<string> rutas, Func<string, (bool Ok, string Error)> enviar,
                                                IProgress<(int Hechas, int Total)>? progreso = null,
                                                CancellationToken ct = default)
    {
        var enviadas = new List<string>(rutas.Count);
        var fallos = new List<(string Archivo, string Motivo)>();
        for (var i = 0; i < rutas.Count; i++)
        {
            if (ct.IsCancellationRequested) return new EnvioPapeleraResultado(enviadas, fallos, Interrumpido: true);
            var ruta = rutas[i];
            (bool Ok, string Error) r;
            try { r = enviar(ruta); }
            catch (Exception e) { r = (false, e.Message); }

            if (r.Ok) enviadas.Add(ruta);
            else fallos.Add((Path.GetFileName(ruta), r.Error ?? ""));
            progreso?.Report((i + 1, rutas.Count));
        }
        return new EnvioPapeleraResultado(enviadas, fallos);
    }

    /// <summary>
    /// Lo mismo en segundo plano. Sin <paramref name="enviar"/>, usa <see cref="Papelera.Enviar"/>,
    /// que se niega en las unidades sin papelera y lo dice (ver su motivo).
    /// </summary>
    public static Task<EnvioPapeleraResultado> EnviarAsync(IReadOnlyList<string> rutas,
                                                           Func<string, (bool Ok, string Error)>? enviar = null,
                                                           IProgress<(int Hechas, int Total)>? progreso = null,
                                                           CancellationToken ct = default)
    {
        var copia = new List<string>(rutas);   // la lista de quien llama puede cambiar mientras tanto
        // Sin pasar el token a Task.Run: cancelado antes de empezar, Enviar devuelve «nada hecho» en
        // vez de lanzar, y quien llama trata igual los dos casos.
        return Task.Run(() => Enviar(copia, enviar ?? Papelera.Enviar, progreso, ct));
    }

    /// <summary>
    /// «Enviando a la papelera: 1.200 de 15.000…», para el estado mientras dura. Con separador de
    /// miles: desde la Biblioteca se envían miles de archivos de una vez, y «1200 de 15000» se lee mal.
    /// </summary>
    public static string TextoAvance(int hechas, int total)
        => total == 1 ? "Enviando a la papelera…" : $"Enviando a la papelera: {hechas:N0} de {total:N0}…";

    /// <summary>
    /// El estado al terminar, el MISMO en todas las páginas que envían a la papelera (Biblioteca, el
    /// Asistente, Duplicados y la Bandeja). Antes cada una lo escribía a su manera y la misma situación
    /// se leía de tres formas («Cancelado: 1 de 10 enviados…», «se ha enviado a la papelera 1 de las 5
    /// copias…», «Descarte detenido. …»), alguna sin concordar con 0 o con 1.
    ///
    /// Reglas: concuerda en número y género con lo que se envía; si se canceló, dice cuántas llegaron
    /// de cuántas se pidieron y que el resto no se ha tocado; y cuando varias fallan por el MISMO motivo
    /// (una unidad sin papelera, donde enviar sería borrar para siempre), ese motivo se dice una vez en
    /// lugar de un recuento y el primero de la lista.
    /// </summary>
    /// <param name="pedidas">Cuántas se pidió enviar: las enviadas, las que fallaron y, si se canceló, las que no se tocaron.</param>
    /// <param name="que">Qué se envía: <see cref="Sustantivo.Copia"/>, <see cref="Sustantivo.Descarga"/>, <see cref="Sustantivo.Archivo"/>.</param>
    public static string Resumen(EnvioPapeleraResultado res, int pedidas, Sustantivo que)
    {
        var enviadas = res.Enviadas.Count;
        var fallos = res.Fallos;

        if (res.Interrumpido)
        {
            var cancelado = enviadas switch
            {
                0 => $"Cancelado: no se ha enviado {que.Ninguno} a la papelera.",
                1 => $"Cancelado: se ha enviado 1 de {que.Los} {pedidas:N0} {que.Plural} a la papelera; el resto no se ha tocado.",
                _ => $"Cancelado: se han enviado {enviadas:N0} de {que.Los} {pedidas:N0} {que.Plural} a la papelera; el resto no se ha tocado.",
            };
            return fallos.Count == 0 ? cancelado : cancelado + " " + LosFallos(fallos, que);
        }

        if (fallos.Count == 0)
            return enviadas switch
            {
                0 => $"No se ha enviado {que.Ninguno} a la papelera.",
                // Una sola, pedida sola: su nombre dice más que «1 archivo enviado».
                1 when pedidas == 1 => $"Enviado a la papelera: {Path.GetFileName(res.Enviadas[0])}.",
                _ => $"{enviadas:N0} {que.De(enviadas)} {que.Enviado(enviadas)} a la papelera.",
            };

        if (enviadas == 0)
        {
            if (fallos.Count == 1)
                return $"No se ha podido enviar {fallos[0].Archivo} a la papelera: {Motivo(fallos[0].Motivo)}";
            return MismoMotivo(fallos)
                ? $"No se ha enviado {que.Ninguno} a la papelera: {Motivo(fallos[0].Motivo)}"
                : $"No se ha enviado {que.Ninguno} a la papelera. {que.ElPrimero}, {fallos[0].Archivo}: {Motivo(fallos[0].Motivo)}";
        }

        return $"{enviadas:N0} {que.De(enviadas)} {que.Enviado(enviadas)} a la papelera. " + LosFallos(fallos, que);
    }

    /// <summary>
    /// La frase de las que no se han enviado cuando otras sí: con una sola, su nombre; con varias por
    /// el mismo motivo, el motivo una vez; con motivos distintos, el primero.
    /// </summary>
    private static string LosFallos(IReadOnlyList<(string Archivo, string Motivo)> fallos, Sustantivo que)
        => fallos.Count == 1 ? $"No se ha enviado {fallos[0].Archivo}: {Motivo(fallos[0].Motivo)}"
         : MismoMotivo(fallos) ? $"{fallos.Count:N0} no se han enviado: {Motivo(fallos[0].Motivo)}"
         : $"{fallos.Count:N0} no se han enviado. {que.ElPrimero}, {fallos[0].Archivo}: {Motivo(fallos[0].Motivo)}";

    private static bool MismoMotivo(IReadOnlyList<(string Archivo, string Motivo)> fallos)
    {
        for (var i = 1; i < fallos.Count; i++)
            if (!string.Equals(fallos[i].Motivo, fallos[0].Motivo, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>El motivo tal cual, con su punto final; uno vacío no deja el mensaje cortado.</summary>
    private static string Motivo(string m)
    {
        if (string.IsNullOrWhiteSpace(m)) return "motivo desconocido.";
        m = m.Trim();
        return m.EndsWith('.') || m.EndsWith('!') || m.EndsWith('?') || m.EndsWith('…') ? m : m + ".";
    }
}

/// <summary>Lo que se envía a la papelera, para que el resumen concuerde en número y género.</summary>
public sealed record Sustantivo(string Singular, string Plural, bool Femenino)
{
    public static readonly Sustantivo Copia = new("copia", "copias", true);
    public static readonly Sustantivo Descarga = new("descarga", "descargas", true);
    public static readonly Sustantivo Archivo = new("archivo", "archivos", false);

    /// <summary>«copia» o «copias».</summary>
    public string De(int n) => n == 1 ? Singular : Plural;

    /// <summary>«enviada», «enviadas», «enviado», «enviados».</summary>
    public string Enviado(int n) => (Femenino ? "enviada" : "enviado") + (n == 1 ? "" : "s");

    /// <summary>«ninguna copia», «ningún archivo».</summary>
    public string Ninguno => (Femenino ? "ninguna " : "ningún ") + Singular;

    /// <summary>El artículo del plural: «de las 5 copias», «de los 10 archivos».</summary>
    public string Los => Femenino ? "las" : "los";

    /// <summary>Cómo se nombra el primer fallo: «La primera», «El primero».</summary>
    public string ElPrimero => Femenino ? "La primera" : "El primero";
}
