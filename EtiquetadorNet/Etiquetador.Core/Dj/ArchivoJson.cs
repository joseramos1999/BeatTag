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
/// vacío: se aparta con otro nombre antes de que el siguiente guardado lo pise.
/// </summary>
internal static class ArchivoJson
{
    public static readonly JsonSerializerOptions Opciones = new()
    {
        WriteIndented = false,
        // Los enums por nombre: el archivo se entiende al abrirlo y sobrevive a reordenar el enum.
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Leer<T>(string archivo, Func<T> vacio, out string aviso)
    {
        aviso = "";
        try
        {
            if (!File.Exists(archivo)) return vacio();
            return JsonSerializer.Deserialize<T>(File.ReadAllText(archivo, Encoding.UTF8), Opciones) ?? vacio();
        }
        catch (Exception e)
        {
            var apartado = archivo + $".ilegible_{DateTime.Now:yyyyMMdd_HHmmss}";
            try { File.Move(archivo, apartado); aviso = $"No se pudo leer {Path.GetFileName(archivo)} ({e.Message}); se ha apartado como {Path.GetFileName(apartado)}."; }
            catch { aviso = $"No se pudo leer {Path.GetFileName(archivo)}: {e.Message}"; }
            return vacio();
        }
    }

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
