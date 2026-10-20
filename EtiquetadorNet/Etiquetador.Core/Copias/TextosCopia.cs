using System.Globalization;

namespace Etiquetador.Core.Copias;

/// <summary>
/// Los textos de la copia de seguridad que ve el usuario (Ajustes, el diálogo de restaurar y el aviso
/// del arranque), aparte para poder probar que concuerdan en número (equipo «volumen_copias», tanda 4, T15).
/// </summary>
public static class TextosCopia
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private static string N(int n, string singular, string plural) => n == 1 ? $"1 {singular}" : $"{n.ToString("N0", Es)} {plural}";

    /// <summary>«812 bytes», «64,2 KB», «1,7 MB».</summary>
    public static string Tamano(long bytes)
        => bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.#", Es) + " MB"
         : bytes >= 1024 ? (bytes / 1024.0).ToString("0.#", Es) + " KB"
         : bytes == 1 ? "1 byte" : $"{bytes} bytes";

    /// <summary>«64,2 KB · 17/10/2026 15:56».</summary>
    public static string TamanoYFecha(long bytes, DateTime? fecha)
        => fecha is { } f && f != default ? $"{Tamano(bytes)} · {f:dd/MM/yyyy HH:mm}" : Tamano(bytes);

    /// <summary>«copia manual», «copia automática»…, para describir una copia.</summary>
    public static string Tipo(string tipo) => tipo switch
    {
        "automatica" => "copia automática",
        "antes-de-restaurar" => "copia hecha antes de restaurar otra",
        _ => "copia manual",
    };

    /// <summary>«Copia manual del 17/10/2026 15:56, hecha en Windows con BeatTag 2.4.0.»</summary>
    public static string Describir(ManifiestoCopia m)
    {
        var tipo = Tipo(m.Tipo);
        var donde = m.Sistema.Length > 0 ? $", hecha en {m.Sistema}" : "";
        var version = m.Version.Length > 0 ? $" con {m.Aplicacion} {m.Version}" : "";
        return $"{char.ToUpperInvariant(tipo[0])}{tipo[1..]} del {m.Fecha:dd/MM/yyyy HH:mm}{donde}{version}.";
    }

    /// <summary>Lo que se dice al terminar «Copia de seguridad…».</summary>
    public static string CopiaHecha(ResultadoCopia r)
    {
        if (!r.Ok) return "No se pudo guardar la copia de seguridad: " + r.Error;
        var texto = $"Copia de seguridad guardada en «{Path.GetFileName(r.Ruta)}»: {N(r.Archivos, "archivo", "archivos")}, {Tamano(r.Bytes)}. "
                  + "No incluye las claves de API: en otro equipo hay que volver a introducirlas.";
        if (r.Avisos.Count > 0) texto += " Atención: " + string.Join("; ", r.Avisos) + ".";
        return texto;
    }

    /// <summary>
    /// Estado de las copias automáticas, de la más antigua a la más reciente:
    /// «Copias automáticas: 5 guardadas; la última, del 20/10/2026 07:12.»
    /// </summary>
    public static string CopiasAutomaticas(IReadOnlyList<DateTime> fechas)
        => fechas.Count switch
        {
            0 => "Todavía no hay ninguna copia automática: se hace al abrir BeatTag, si hay algo que guardar.",
            1 => $"Copias automáticas: 1 guardada, del {fechas[0]:dd/MM/yyyy HH:mm}.",
            _ => $"Copias automáticas: {fechas.Count} guardadas; la última, del {fechas[^1]:dd/MM/yyyy HH:mm}.",
        };

    /// <summary>El aviso de Ajustes mientras hay una restauración preparada esperando al próximo arranque.</summary>
    public static string Pendiente(RestauracionPreparada m)
        => $"Restauración preparada: la copia del {m.FechaCopia:dd/MM/yyyy HH:mm} ({N(m.Archivos.Count, "archivo", "archivos")}) "
         + "sustituirá a los datos actuales la próxima vez que se abra BeatTag. Hasta entonces, lo que hagas se guarda como siempre, "
         + "y quedará en la copia que se hace justo antes de restaurar.";

    /// <summary>
    /// La confirmación final de «Restaurar copia…», con lo que se sustituye, lo que se añade y lo que
    /// se conserva, y las ubicaciones que cambian.
    /// </summary>
    public static (string Titulo, string Cuerpo, string Nota) Confirmacion(IReadOnlyList<FilaRestauracion> filas, int raicesQueCambian, int rutasAfectadas)
    {
        var sustituye = filas.Count(f => f.EnLaCopia && f.BytesActual != null);
        var anade = filas.Count(f => f.EnLaCopia && f.BytesActual == null);
        var conserva = filas.Count(f => !f.EnLaCopia);
        var total = sustituye + anade;

        var partes = new List<string>();
        if (sustituye > 0) partes.Add(sustituye == 1 ? "1 archivo de datos sustituye al actual" : $"{sustituye} archivos de datos sustituyen a los actuales");
        if (anade > 0) partes.Add(anade == 1 ? "se añade 1 que no existe ahora" : $"se añaden {anade} que no existen ahora");
        var cuerpo = Frase(string.Join(" y ", partes)) + ".";
        if (conserva > 0)
            cuerpo += conserva == 1 ? " Se conserva tal cual 1 archivo que no está en la copia." : $" Se conservan tal cual {conserva} archivos que no están en la copia.";
        if (raicesQueCambian > 0)
            cuerpo += " " + (raicesQueCambian == 1 ? "Cambia la ubicación de 1 carpeta de música" : $"Cambia la ubicación de {raicesQueCambian} carpetas de música")
                    + (rutasAfectadas == 1 ? ": se reescribe 1 ruta." : $": se reescriben {rutasAfectadas.ToString("N0", Es)} rutas.");

        return (total == 1 ? "Restaurar 1 archivo de la copia de seguridad" : $"Restaurar {total} archivos de la copia de seguridad",
                cuerpo,
                "La restauración se completa la próxima vez que se abra BeatTag, antes de leer ningún dato. Justo antes se guarda "
              + "una copia de lo que hay ahora en la carpeta «copias», para poder volver atrás restaurándola. Las claves de API "
              + "de este equipo se conservan. No se toca ningún archivo de música.");
    }

    /// <summary>Lo que se dice al dejar preparada la restauración.</summary>
    public static string Preparada(ResultadoPreparacion r)
    {
        if (!r.Ok) return r.Error;
        var texto = $"Restauración preparada ({N(r.Archivos, "archivo", "archivos")}";
        if (r.RutasReescritas > 0) texto += r.RutasReescritas == 1 ? ", 1 ruta reescrita" : $", {r.RutasReescritas.ToString("N0", Es)} rutas reescritas";
        texto += "). Se completará la próxima vez que se abra BeatTag.";
        if (r.Avisos.Count > 0) texto += " Atención: " + string.Join("; ", r.Avisos) + ".";
        return texto;
    }

    /// <summary>«N rutas» de una raíz, para la lista de ubicaciones del diálogo.</summary>
    public static string Rutas(int n) => n == 1 ? "1 ruta guardada" : $"{n.ToString("N0", Es)} rutas guardadas";

    private static string Frase(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
