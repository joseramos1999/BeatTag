using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.Services;

/// <summary>Dónde se anota cada cambio para poder deshacerlo. Aparte para poder probar qué pasa si falla.</summary>
internal interface IHistorialDeshacer
{
    /// <summary>"" si quedó anotado, o el motivo.</summary>
    string Anotar(UndoRecord registro);
}

/// <summary>
/// El historial de deshacer de UNA ejecución, abierto de principio a fin.
///
/// Existe por el hallazgo M10. Unificar géneros, renombrar con IA y escribir la ficha en el
/// comentario anotaban con File.AppendAllText y, si fallaba, apuntaban el error y SEGUÍAN tocando
/// archivos. Con la carpeta de datos sin espacio, o con OneDrive o un antivirus bloqueando el .jsonl
/// a mitad de unificar el género de 2.000 canciones, el resto se escribía sin historial y el aviso
/// llegaba al final, con cientos de cambios ya irreversibles. Volumen ya hacía lo contrario
/// («seguir tocando archivos que ya no se podrían devolver a su sitio no es decisión de la
/// máquina»); esto hace lo mismo para todos:
///   · Se abre UNA vez, antes de tocar nada. Si no se puede, no se toca ningún archivo.
///   · Tenerlo abierto mientras dura la ejecución impide que otro programa lo bloquee a mitad.
///   · Cada línea se escribe entera y se vuelca al disco antes de seguir.
/// </summary>
public sealed class ManifiestoDeshacer : IDisposable, IHistorialDeshacer
{
    private readonly FileStream _fs;
    private bool _cerrado;

    /// <summary>Ruta del manifiesto.</summary>
    public string Ruta { get; }

    /// <summary>Cambios anotados hasta ahora.</summary>
    public int Anotados { get; private set; }

    private ManifiestoDeshacer(string ruta, FileStream fs)
    {
        Ruta = ruta;
        _fs = fs;
    }

    /// <summary>
    /// Abre (o crea) el manifiesto. Devuelve null, con el motivo en <paramref name="error"/>, si no se
    /// puede: en ese caso quien llama NO debe tocar ningún archivo.
    /// </summary>
    public static ManifiestoDeshacer? Abrir(string ruta, out string error)
    {
        try
        {
            var dir = Path.GetDirectoryName(ruta);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Se deja leer a otros (el antivirus, una copia de seguridad), pero no escribir.
            var fs = new FileStream(ruta, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            fs.Seek(0, SeekOrigin.End);
            error = "";
            return new ManifiestoDeshacer(ruta, fs);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>
    /// Anota un cambio y lo deja escrito en el disco. Devuelve "" si quedó anotado, o el motivo. Una
    /// línea que se quedó a medias se quita: estropearía la lectura del manifiesto entero.
    /// </summary>
    public string Anotar(UndoRecord registro)
    {
        if (_cerrado) return "el historial ya está cerrado";
        long antes = -1;
        try
        {
            var linea = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(registro) + "\n");
            antes = _fs.Position;
            _fs.Write(linea, 0, linea.Length);
            _fs.Flush(flushToDisk: true);
            Anotados++;
            return "";
        }
        catch (Exception e)
        {
            if (antes >= 0)
                try { _fs.SetLength(antes); _fs.Seek(0, SeekOrigin.End); } catch { /* lo intentará quien lea */ }
            return e.Message;
        }
    }

    /// <summary>
    /// Cierra el manifiesto. Si no se llegó a anotar nada, lo borra: un manifiesto vacío pasaría por
    /// «la última ejecución» y taparía en «Deshacer última» a la que de verdad lo es.
    /// </summary>
    public void Dispose()
    {
        if (_cerrado) return;
        _cerrado = true;
        var vacio = false;
        try { vacio = _fs.Length == 0; } catch { }
        try { _fs.Dispose(); } catch { }
        if (vacio && Anotados == 0)
            try { File.Delete(Ruta); } catch { /* vacío y sin borrar: no estorba más que antes */ }
    }
}
