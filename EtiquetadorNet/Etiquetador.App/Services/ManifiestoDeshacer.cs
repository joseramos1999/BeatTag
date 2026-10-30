using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Un historial que anota por lotes (Organizar por género): el lote entero ANTES de mover nada y, al
/// terminar el lote, se reescribe con lo que de verdad se movió. Aparte para poder probar qué pasa si
/// falla.
/// </summary>
internal interface IHistorialPorLotes : IHistorialDeshacer
{
    /// <summary>Dónde empieza lo siguiente que se anote: es lo que se pasa a <see cref="ReescribirDesde"/>.</summary>
    long Posicion { get; }

    /// <summary>Anota todos los registros con una sola escritura y un solo volcado al disco. "" o el motivo; si falla, no queda ninguno.</summary>
    string AnotarLote(IReadOnlyList<UndoRecord> registros);

    /// <summary>
    /// Sustituye todo lo anotado desde <paramref name="posicion"/> por <paramref name="hechos"/> (los
    /// del último lote que sí se hicieron). "" o el motivo; si falla, lo anotado antes sigue ahí.
    /// </summary>
    string ReescribirDesde(long posicion, IReadOnlyList<UndoRecord> hechos);
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
///   · Cada línea se escribe entera y se vuelca al disco antes de seguir (o el lote entero, antes de
///     moverlo, en Organizar por género: ver <see cref="AnotarLote"/>).
/// </summary>
public sealed class ManifiestoDeshacer : IDisposable, IHistorialPorLotes
{
    private readonly FileStream _fs;
    private bool _cerrado;

    // Cuántos cambios había anotados al empezar cada escritura, por su posición: al reescribir desde
    // una de ellas, Anotados vuelve a ser exacto, y Dispose sigue borrando el manifiesto que se queda
    // vacío (si no, taparía en «Deshacer última» a la ejecución anterior).
    private readonly Dictionary<long, int> _anotadosEn = new() { [0] = 0 };

    // Lo que escribió el último lote, para dejarlo como estaba si falla su reescritura.
    private (long Posicion, byte[] Bytes)? _ultimoLote;

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
            _anotadosEn[antes] = Anotados;
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

    /// <summary>Dónde empieza lo siguiente que se anote (-1 si el archivo ya no responde).</summary>
    public long Posicion
    {
        get
        {
            try { return _fs.Position; }
            catch { return -1; }
        }
    }

    // --- Por lotes (Organizar por género, B5) ---
    //
    // MEDIDO con 2.000 traslados: anotar cada archivo y volcarlo al disco uno a uno costaba 881 µs por
    // archivo, frente a 408 µs sin historial; anotando antes, en lotes de 100, 418 µs. Y con el proceso
    // matado a mitad 18 veces, anotar antes por lotes no dejó ningún archivo movido sin anotar
    // (anotando después: 2 de 18; con el patrón de la Bandeja: 7 de 18), y deshacer devolvió el 100 %.
    // Lo anotado que no llegó a moverse no estorba: deshacer lo encuentra en su sitio y lo da por
    // devuelto. Aun así, al terminar cada lote se reescribe con lo hecho, para que el historial sea
    // exacto en una ejecución normal.

    /// <summary>Anota un lote con una sola escritura y un solo volcado. Si falla, se quita lo escrito.</summary>
    public string AnotarLote(IReadOnlyList<UndoRecord> registros)
    {
        if (_cerrado) return "el historial ya está cerrado";
        if (registros.Count == 0) return "";
        long antes = -1;
        try
        {
            var bytes = Lineas(registros);
            antes = _fs.Position;
            _anotadosEn[antes] = Anotados;
            _fs.Write(bytes, 0, bytes.Length);
            _fs.Flush(flushToDisk: true);
            Anotados += registros.Count;
            _ultimoLote = (antes, bytes);
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
    /// Deja desde <paramref name="posicion"/> solo <paramref name="hechos"/>, con un solo volcado. Si la
    /// escritura falla, se vuelve a poner lo que había (el lote entero, que incluye lo hecho) y se
    /// devuelve el motivo.
    /// </summary>
    public string ReescribirDesde(long posicion, IReadOnlyList<UndoRecord> hechos)
    {
        if (_cerrado) return "el historial ya está cerrado";
        try
        {
            var bytes = Lineas(hechos);
            _fs.SetLength(posicion);
            _fs.Seek(posicion, SeekOrigin.Begin);
            _fs.Write(bytes, 0, bytes.Length);
            _fs.Flush(flushToDisk: true);
            Anotados = (_anotadosEn.TryGetValue(posicion, out var a) ? a : 0) + hechos.Count;
            foreach (var k in _anotadosEn.Keys.Where(k => k > posicion).ToList()) _anotadosEn.Remove(k);
            _ultimoLote = null;
            return "";
        }
        catch (Exception e)
        {
            if (_ultimoLote is { } lote && lote.Posicion == posicion)
                try
                {
                    _fs.SetLength(posicion);
                    _fs.Seek(posicion, SeekOrigin.Begin);
                    _fs.Write(lote.Bytes, 0, lote.Bytes.Length);
                    _fs.Flush(flushToDisk: true);
                }
                catch { /* el error ya se devuelve: quien llama se para */ }
            return e.Message;
        }
    }

    /// <summary>Los registros como líneas del manifiesto, en UTF-8.</summary>
    private static byte[] Lineas(IReadOnlyList<UndoRecord> registros)
    {
        var sb = new StringBuilder();
        foreach (var r in registros) sb.Append(JsonSerializer.Serialize(r)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
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
