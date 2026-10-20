using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Etiquetador.Core.Dj;

/// <summary>
/// Lectura y escritura de los datos del DJ en disco.
///
/// A diferencia de las cachés, esto NO se puede regenerar: es trabajo hecho a mano, canción a
/// canción. Por eso se escribe a un temporal y se intercambia de golpe (un corte a media escritura
/// deja el archivo anterior intacto), y si el archivo existe pero no se puede leer NO se trata como
/// vacío: si está dañado se aparta con otro nombre antes de que el siguiente guardado lo pise, y si
/// no se puede ni leer ni apartar, el almacén queda en solo lectura (ver <see cref="Leer{T}(string, Func{T}, out string, out bool)"/>).
/// </summary>
internal static class ArchivoJson
{
    public static readonly JsonSerializerOptions Opciones = new()
    {
        WriteIndented = false,
        // Los enums por nombre: el archivo se entiende al abrirlo y sobrevive a reordenar el enum.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Cuántas veces se intenta leer un archivo que otro proceso tiene bloqueado.</summary>
    internal const int IntentosLectura = 3;

    /// <summary>Lo que se espera entre un intento y el siguiente.</summary>
    internal static readonly TimeSpan EsperaEntreIntentos = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Solo para las pruebas: se llama en el hilo que lee tras cada intento fallido por un bloqueo, con
    /// el número de intento. Permite soltar el bloqueo justo después del primer fallo, en lugar de
    /// adivinar con una espera fija si ha llegado a fallar (con las pruebas en paralelo, una espera de
    /// 100 ms podía pasar de los 500 ms de reintentos y la prueba fallaba de vez en cuando). Va por
    /// hilo para que las demás pruebas que leen a la vez no lo vean.
    /// </summary>
    [ThreadStatic] internal static Action<string, int>? TrasIntentoFallido;

    public static T Leer<T>(string archivo, Func<T> vacio, out string aviso) => Leer(archivo, vacio, out aviso, out _);

    /// <summary>
    /// Lee el archivo, o devuelve <paramref name="vacio"/> si no existe.
    ///
    /// Si existe y no se puede leer, devolver vacío sin más era perderlo: el siguiente guardado lo
    /// pisaba con lo poco que se hubiera hecho en la sesión. Pasa si al arrancar otro proceso lo tiene
    /// abierto sin compartir (otra ventana de BeatTag a mitad de guardar, un antivirus, un cliente de
    /// sincronización): fallan la lectura y el renombrado, la aplicación arranca con 0 fichas, se pone
    /// la energía de una canción y se escribe un archivo con 1 ficha encima del que tenía cientos.
    ///
    /// Por eso:
    ///   · Un bloqueo de otro proceso suele ser cuestión de un momento: se reintenta un par de veces.
    ///   · Si el CONTENIDO está dañado, se aparta con otro nombre y se empieza de cero, como siempre.
    ///   · Si no se puede leer por un bloqueo o por permisos, el archivo está bien y no se aparta:
    ///     se devuelve vacío con <paramref name="soloLectura"/> a true, y el almacén no debe escribir
    ///     encima (<see cref="AvisoSoloLectura"/>). Lo mismo si está dañado y tampoco se puede apartar.
    /// </summary>
    public static T Leer<T>(string archivo, Func<T> vacio, out string aviso, out bool soloLectura)
    {
        aviso = "";
        soloLectura = false;
        Exception error;
        for (var intento = 1; ; intento++)
        {
            try
            {
                if (!File.Exists(archivo)) return vacio();
                return JsonSerializer.Deserialize<T>(File.ReadAllText(archivo, Encoding.UTF8), Opciones) ?? vacio();
            }
            catch (IOException) when (intento < IntentosLectura)
            {
                TrasIntentoFallido?.Invoke(archivo, intento);
                Thread.Sleep(EsperaEntreIntentos);
            }
            catch (Exception e)
            {
                error = e;
                break;
            }
        }

        var nombre = Path.GetFileName(archivo);
        if (error is JsonException)
        {
            var apartado = archivo + $".ilegible_{DateTime.Now:yyyyMMdd_HHmmss}";
            try
            {
                File.Move(archivo, apartado);
                aviso = $"No se pudo leer {nombre} ({error.Message}); se ha apartado como {Path.GetFileName(apartado)}.";
                return vacio();
            }
            catch (Exception e)
            {
                soloLectura = true;
                aviso = $"No se pudo leer {nombre} ({error.Message}) ni apartarlo ({e.Message}). "
                      + "Para no sobrescribirlo, no se guardarán cambios en él durante esta sesión.";
                return vacio();
            }
        }

        soloLectura = true;
        aviso = $"No se pudo leer {nombre}: {error.Message} Puede que otra ventana de BeatTag u otro programa lo tenga "
              + "abierto. Para no sobrescribirlo, no se guardarán cambios en él hasta que vuelvas a abrir BeatTag.";
        return vacio();
    }

    /// <summary>El motivo que devuelve el guardado de un almacén que se abrió en solo lectura.</summary>
    public static string AvisoSoloLectura(string archivo)
        => $"No se guarda {Path.GetFileName(archivo)}: al abrir BeatTag no se pudo leer, y escribirlo ahora borraría "
         + "lo que contiene. Cierra las demás ventanas de BeatTag y vuelve a abrirlo.";

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo.</summary>
    public static string Escribir<T>(string archivo, T datos)
    {
        var tmp = archivo + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(archivo);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(datos, Opciones), new UTF8Encoding(false));
            if (File.Exists(archivo)) File.Replace(tmp, archivo, null);
            else File.Move(tmp, archivo);
            return "";
        }
        catch (Exception e)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return e.Message;
        }
    }
}
