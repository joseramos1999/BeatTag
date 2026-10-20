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
/// Envía varios archivos a la papelera FUERA del hilo de la interfaz, con progreso. Lo usan
/// Duplicados y la Bandeja.
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

    /// <summary>«Enviando a la papelera: 37 de 120…», para el estado mientras dura.</summary>
    public static string TextoAvance(int hechas, int total)
        => total == 1 ? "Enviando a la papelera…" : $"Enviando a la papelera: {hechas} de {total}…";

    /// <summary>
    /// El estado al terminar. Cuando todas fallan por el MISMO motivo (por ejemplo, una unidad sin
    /// papelera, donde enviar sería borrar para siempre), se dice ese motivo una vez en lugar de «0
    /// enviadas, 120 con problemas» y el primero de la lista.
    ///
    /// Con el singular y el plural de lo que se envía y su género (revisión de la tanda 3,
    /// interfaz-11): antes salía «1 descarga(s) enviadas a la papelera».
    /// </summary>
    /// <param name="que">Qué se envía: <see cref="Sustantivo.Copia"/>, <see cref="Sustantivo.Descarga"/>, <see cref="Sustantivo.Archivo"/>.</param>
    public static string Resumen(int enviadas, IReadOnlyList<(string Archivo, string Motivo)> fallos, Sustantivo que)
    {
        if (fallos.Count == 0)
            return enviadas == 0
                ? $"No se ha enviado {que.Ninguno} a la papelera."
                : $"{enviadas} {que.De(enviadas)} {que.Enviado(enviadas)} a la papelera.";

        var motivos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fallos) motivos.Add(f.Motivo);
        if (motivos.Count == 1)
            return enviadas == 0
                ? $"No se ha enviado {que.Ninguno} a la papelera: {Motivo(fallos[0].Motivo)}"
                : $"{enviadas} {que.De(enviadas)} {que.Enviado(enviadas)} a la papelera · "
                  + $"{fallos.Count} no se {(fallos.Count == 1 ? "ha" : "han")} enviado: {Motivo(fallos[0].Motivo)}";

        return $"{enviadas} {que.Enviado(enviadas)}, {fallos.Count} con problemas. {que.ElPrimero}: {fallos[0].Archivo}: {Motivo(fallos[0].Motivo)}";

        static string Motivo(string m) => string.IsNullOrWhiteSpace(m) ? "motivo desconocido." : m;
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

    /// <summary>Cómo se nombra el primer fallo: «La primera», «El primero».</summary>
    public string ElPrimero => Femenino ? "La primera" : "El primero";
}
