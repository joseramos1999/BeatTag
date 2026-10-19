using System.Text;
using System.Text.Json;

namespace Etiquetador.Core;

/// <summary>
/// Escritura y lectura de los archivos de datos de la aplicación -decisiones del usuario y cachés-
/// sin que un corte a media escritura ni un archivo dañado se lleven por delante lo guardado.
///
/// POR QUÉ. IgnoreList, ApplyMarks, AnalysisCache y ScanCache escribían directamente encima del
/// archivo, y al leer trataban cualquier error como «vacío». Si el proceso muere justo mientras se
/// guarda aplicadas.json (63 KB) o marcas-aplicar.json (84 KB) -un corte de luz, o el
/// Stop-Process -Force de Compilar-BeatTag.ps1 -CerrarApp-, el archivo queda truncado; el siguiente
/// arranque lo leía como vacío sin decir nada y el siguiente guardado lo pisaba: volvían a
/// proponerse canciones ya aplicadas y se perdían las 84 descartadas a mano.
///
/// Es la misma receta que ya usaban Dj/ArchivoJson y AppConfig: se escribe a un temporal y se
/// intercambia de golpe, y lo que no se entiende se aparta con otro nombre en vez de pisarlo. No se
/// reutiliza ArchivoJson porque serializa los enums como texto, y cambiaría el formato de unas
/// cachés que ya existen en disco.
/// </summary>
public static class ArchivoDatos
{
    /// <summary>
    /// Escribe <paramref name="contenido"/> en un temporal y lo intercambia con el archivo de una
    /// vez: un corte a media escritura deja el archivo anterior intacto. Devuelve "" si fue bien, o
    /// el motivo del fallo.
    /// </summary>
    public static string Escribir(string archivo, string contenido)
    {
        var tmp = archivo + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(archivo);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, contenido, Encoding.UTF8);
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

    /// <summary>Serializa y escribe con <see cref="Escribir"/>. Devuelve "" si fue bien, o el motivo.</summary>
    public static string EscribirJson<T>(string archivo, T datos, JsonSerializerOptions? opciones = null)
    {
        string json;
        try { json = JsonSerializer.Serialize(datos, opciones); }
        catch (Exception e) { return e.Message; }
        return Escribir(archivo, json);
    }

    /// <summary>
    /// Lo leído de un archivo de datos.
    /// </summary>
    /// <param name="Datos">El contenido, o default si no existía o no se pudo leer.</param>
    /// <param name="Aviso">"" si todo fue normal; si no, qué ha pasado, para el registro.</param>
    /// <param name="NoSobrescribir">
    /// El archivo existe pero no se ha podido leer NI apartar. Quien llama no debe guardar encima:
    /// perdería un archivo que quizá está bien y solo estaba bloqueado por otro programa.
    /// </param>
    public sealed record Lectura<T>(T? Datos, string Aviso, bool NoSobrescribir);

    /// <summary>
    /// Lee un JSON.
    ///   - No existe: Datos = default, sin aviso.
    ///   - Existe pero su contenido no se entiende (truncado, a cero bytes, dañado): se aparta como
    ///     «archivo.ilegible_fecha» y se devuelve default con un aviso. El siguiente guardado empieza
    ///     de cero SIN destruir lo que había.
    ///   - No se puede ni abrir (bloqueado, sin permiso), o no se puede apartar: NoSobrescribir.
    /// </summary>
    public static Lectura<T> LeerJson<T>(string archivo, JsonSerializerOptions? opciones = null)
    {
        var nombre = Path.GetFileName(archivo);
        string texto;
        try
        {
            if (!File.Exists(archivo)) return new(default, "", false);
            texto = File.ReadAllText(archivo, Encoding.UTF8);
        }
        catch (Exception e)
        {
            // No se ha podido mirar dentro: no hay motivo para creer que esté dañado, así que ni se
            // aparta ni se deja pisar.
            return new(default, $"No se pudo leer {nombre} ({e.Message}); no se guardará encima mientras no se pueda leer.", true);
        }

        string motivo;
        try
        {
            var datos = JsonSerializer.Deserialize<T>(texto, opciones);
            if (datos != null) return new(datos, "", false);
            motivo = "contenido vacío";
        }
        catch (Exception e)   // lo que sea del contenido: un archivo dañado no puede impedir arrancar
        {
            motivo = e.Message;
        }

        var apartado = Apartar(archivo);
        return apartado.Length > 0
            ? new(default, $"{nombre} no se pudo leer ({motivo}); se ha apartado como {Path.GetFileName(apartado)} y se empieza de cero.", false)
            : new(default, $"{nombre} no se pudo leer ({motivo}) ni apartar; no se guardará encima.", true);
    }

    /// <summary>
    /// Aparta un archivo que no se puede leer, con el sufijo «.ilegible_fecha» (el mismo que usa
    /// Dj/ArchivoJson). Devuelve la ruta nueva, o "" si no se pudo moverlo.
    /// </summary>
    public static string Apartar(string archivo)
    {
        var destino = archivo + $".ilegible_{DateTime.Now:yyyyMMdd_HHmmss}";
        try
        {
            if (File.Exists(destino)) destino += "_" + Guid.NewGuid().ToString("N")[..6];
            File.Move(archivo, destino);
            return destino;
        }
        catch { return ""; }
    }
}
