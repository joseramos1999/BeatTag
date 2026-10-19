using System;
using System.Linq;
using Avalonia.Threading;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>Una fila de tabla con cuadro «Buscar…»: guarda su texto ya preparado para buscar.</summary>
public interface IFilaBuscable
{
    /// <summary>Los campos que se buscan, normalizados como lo hace <see cref="BusquedaTexto"/>.</summary>
    string TextoBusqueda { get; }
}

/// <summary>
/// La consulta del cuadro «Buscar…» de las tablas, preparada UNA vez por búsqueda.
///
/// Las reglas son las de <see cref="BusquedaTexto"/> (palabras sueltas en cualquier orden, sin
/// acentos ni mayúsculas, sin separadores) y el resultado es el mismo que el de
/// BusquedaTexto.Coincide. Lo que cambia es cuándo se calcula cada cosa: antes, cada evaluación
/// volvía a normalizar la consulta Y el texto de la fila (quitar acentos y dos expresiones
/// regulares). En Calidad, con una fila por canción, eso eran unas 15.000 normalizaciones por
/// pasada, dos pasadas por tecla. Ahora la consulta se prepara una vez y cada fila guarda su texto
/// ya normalizado (<see cref="IFilaBuscable"/>), así que filtrar se queda en buscar subcadenas.
///
/// MEDIDO con 15.000 filas de Calidad con nombres de pool reales («Bad_Bunny-Tití_Me_Preguntó…
/// [Djcity.com] 128 8A.mp3»): antes, unos 75 ms por pasada y 150 ms por tecla (escribir «bad bunny»
/// eran 1,4 s solo en el filtro, sin contar el reagrupado del DataGrid); ahora, 47 ms la primera
/// vez, al preparar los textos, y menos de 1 ms por tecla después.
/// </summary>
public sealed class ConsultaTabla
{
    private readonly string[] _palabras;

    private ConsultaTabla(string[] palabras) => _palabras = palabras;

    public static ConsultaTabla De(string? texto) => new(BusquedaTexto.Palabras(texto));

    /// <summary>No hay nada que buscar: la tabla se enseña entera.</summary>
    public bool Vacia => _palabras.Length == 0;

    /// <summary>¿Aparecen TODAS las palabras en un texto ya preparado con <see cref="Preparar"/>?</summary>
    public bool Coincide(string textoPreparado)
    {
        foreach (var p in _palabras)
            if (!textoPreparado.Contains(p, StringComparison.Ordinal)) return false;
        return true;
    }

    public bool Coincide(IFilaBuscable fila) => Coincide(fila.TextoBusqueda);

    /// <summary>
    /// Texto de una fila listo para buscar en él. Es exactamente lo que BusquedaTexto normaliza por
    /// dentro: sus palabras, unidas por un espacio.
    /// </summary>
    public static string Preparar(params string?[] campos)
        => string.Join(' ', BusquedaTexto.Palabras(string.Join(" ", campos.Where(c => !string.IsNullOrEmpty(c)))));
}

/// <summary>
/// Aplica el filtro de una tabla cuando se deja de teclear, no en cada pulsación: escribir
/// «bad bunny» eran nueve filtrados seguidos de toda la tabla, y el DataGrid además reagrupa por
/// carpeta cada vez.
/// </summary>
public sealed class BusquedaDiferida
{
    /// <summary>Pausa sin teclear a partir de la que se filtra.</summary>
    public static readonly TimeSpan Espera = TimeSpan.FromMilliseconds(250);

    private readonly Action _aplicar;
    private DispatcherTimer? _reloj;

    public BusquedaDiferida(Action aplicar) => _aplicar = aplicar;

    /// <summary>Hay un filtrado pedido que aún no se ha aplicado.</summary>
    public bool Pendiente { get; private set; }

    /// <summary>Pide filtrar: se hará cuando pase <see cref="Espera"/> sin otra petición.</summary>
    public void Pedir()
    {
        Pendiente = true;
        if (_reloj == null)
        {
            _reloj = new DispatcherTimer { Interval = Espera };
            _reloj.Tick += (_, _) => AplicarSiPendiente();
        }
        _reloj.Stop();
        _reloj.Start();
    }

    /// <summary>Filtra ya, sin esperar (al vaciar el cuadro o al cambiar otro filtro de la tabla).</summary>
    public void Ahora()
    {
        _reloj?.Stop();
        Pendiente = false;
        _aplicar();
    }

    /// <summary>
    /// Aplica lo que esté pendiente. Para antes de actuar sobre «las visibles»: lo que se ve tiene
    /// que corresponder a lo que hay escrito en el cuadro.
    /// </summary>
    public void AplicarSiPendiente()
    {
        _reloj?.Stop();
        if (!Pendiente) return;
        Pendiente = false;
        _aplicar();
    }
}

/// <summary>
/// Lo que se pone a cero al empezar cada análisis (Enriquecer y No encontradas), y lo que hay que
/// contar al usuario cuando una fuente se desactiva sola por el camino.
/// </summary>
public static class TiradaAnalisis
{
    /// <summary>
    /// Da otra oportunidad a Spotify y a la IA local. Los dos se desactivan solos tras fallar
    /// (Spotify tras varios 429 seguidos; la IA si Ollama no responde o falta el modelo), y hasta
    /// ahora eso duraba hasta cerrar la aplicación: arrancar Ollama o esperar a que Spotify dejara
    /// de limitar no servía de nada en el análisis siguiente.
    /// </summary>
    public static void Empezar(SpotifyProvider spotify, OllamaClient ia, bool usaIa)
    {
        spotify.ReiniciarBloqueo();
        if (usaIa) ia.Reset();
    }

    /// <summary>
    /// Aviso para el usuario si la IA local se ha desactivado sola en este análisis, o "".
    ///
    /// Visto en una biblioteca real: con el modelo elegido sin descargar, Enriquecer analizó la
    /// biblioteca entera sin IA y el motivo solo quedó en el archivo de registro.
    /// </summary>
    public static string AvisoIa(OllamaClient ia, bool usaIa)
    {
        if (!usaIa || !ia.AiBlocked) return "";
        var motivo = ia.MotivoBloqueo.Trim();
        return "La IA local se ha desactivado durante este análisis; el resto de fuentes sigue funcionando."
             + (motivo.Length > 0 ? " " + motivo : "");
    }
}

/// <summary>Cómo ha ido aplicar una canción, visto desde la interfaz.</summary>
public enum DesenlaceAplicacion
{
    /// <summary>Etiquetas escritas y, si tocaba, archivo renombrado.</summary>
    Aplicada,

    /// <summary>
    /// Las etiquetas se escribieron (y quedaron anotadas para deshacer), pero el archivo conserva su
    /// nombre: renombrar falló, normalmente porque otro programa lo tenía abierto.
    /// </summary>
    SinRenombrar,

    /// <summary>No se tocó nada: ni etiquetas ni nombre.</summary>
    Fallida,
}

/// <summary>
/// Una sola regla para todas las pestañas que aplican cambios. Enriquecer ya exigía etiquetas Y
/// nombre; No encontradas daba por aplicada una canción con solo las etiquetas escritas, la quitaba
/// de la lista y la apuntaba como aplicada, de modo que Analizar no volvía a proponer el nombre.
/// </summary>
public static class ResultadoAplicacion
{
    public static DesenlaceAplicacion Clasificar(ApplyOneResult r)
        => !r.TagOk ? DesenlaceAplicacion.Fallida
         : !r.RenOk ? DesenlaceAplicacion.SinRenombrar
         : DesenlaceAplicacion.Aplicada;

    /// <summary>Texto de la columna Estado de la fila.</summary>
    public static string EstadoFila(ApplyOneResult r) => Clasificar(r) switch
    {
        // Un nombre inválido aborta antes de escribir y deja el motivo en RenErr, no en TagErr.
        DesenlaceAplicacion.Fallida => "⚠ no se pudo escribir: " + (r.TagErr + " " + r.RenErr).Trim(),
        DesenlaceAplicacion.SinRenombrar => "⚠ etiquetas escritas, pero no se pudo renombrar: " + r.RenErr,
        _ => "✔ aplicada",
    };
}

/// <summary>
/// Una acción larga de una página que la deja OCUPADA desde el primer instante: también mientras
/// mira qué hay y mientras pregunta, no solo mientras hace el trabajo.
///
/// Visto en revisión: «Vaciar caché» contaba unos 69.000 archivos antes de preguntar (unos segundos)
/// con el botón habilitado y la página libre. Un doble clic abría dos confirmaciones, y entretanto se
/// podía cambiar de pestaña y lanzar un análisis: al confirmar, se vaciaba la caché de análisis
/// mientras Enriquecer la usaba desde Task.Run. Con la página ocupada, el botón se deshabilita y las
/// demás pestañas quedan bloqueadas hasta terminar o cancelar la confirmación.
/// </summary>
public static class TareaExclusiva
{
    /// <summary>
    /// Ejecuta <paramref name="trabajo"/> con la página marcada como ocupada, y la libera al terminar
    /// (también si falla). Devuelve false, sin hacer nada, si ya estaba ocupada.
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> EjecutarAsync(
        Func<bool> ocupada, Action<bool> marcar, Func<System.Threading.Tasks.Task> trabajo)
    {
        if (ocupada()) return false;
        marcar(true);
        try { await trabajo(); }
        finally { marcar(false); }
        return true;
    }
}
