using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Etiquetador.Core.Copias;

/// <summary>
/// Las rutas que hay dentro de un archivo de datos, se llamen como se llamen sus campos: claves de
/// diccionario (fichas-dj.json, identificacion.json, generos-por-carpeta.json…), elementos de lista
/// (aplicadas.json, descartadas.json) o valores sueltos (el «Destino» de bandeja-sustituidas.json,
/// las carpetas de config.net.json). Recorrer el JSON entero en vez de conocer cada formato hace que
/// un archivo de datos nuevo (de otra pestaña, de una versión posterior) se traslade igual.
/// </summary>
public static class RutasJson
{
    private static readonly JsonDocumentOptions Tolerante = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>El texto de un archivo, sin la marca de orden de bytes que ponen algunos (IgnoreList).</summary>
    public static string Texto(byte[] bytes)
    {
        using var lector = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return lector.ReadToEnd();
    }

    /// <summary>El JSON leído, o null si no lo es.</summary>
    public static JsonNode? Leer(string texto)
    {
        try { return JsonNode.Parse(texto, null, Tolerante); }
        catch (JsonException) { return null; }
    }

    /// <summary>Todos los textos que son rutas absolutas (claves y valores), tantas veces como aparezcan.</summary>
    public static List<string> Rutas(JsonNode? nodo)
    {
        var res = new List<string>();
        Recorrer(nodo, s => { if (RutaPartida.De(s) != null) res.Add(s); });
        return res;
    }

    private static void Recorrer(JsonNode? nodo, Action<string> cadena)
    {
        switch (nodo)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    cadena(k);
                    Recorrer(v, cadena);
                }
                break;
            case JsonArray a:
                foreach (var v in a) Recorrer(v, cadena);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                cadena(v.GetValue<string>());
                break;
        }
    }

    /// <summary>Lo que ha pasado al reescribir un archivo.</summary>
    /// <param name="Raiz">El JSON ya reescrito (el mismo objeto, salvo que la raíz fuera un texto suelto).</param>
    /// <param name="Cambiadas">Rutas reescritas (claves y valores).</param>
    /// <param name="Repetidas">
    /// Claves que, ya reescritas, coincidían con otra que ya estaba: se conserva la primera. Pasa si la
    /// copia tenía la misma canción con dos rutas que ahora son una.
    /// </param>
    public sealed record Reescritura(JsonNode? Raiz, int Cambiadas, int Repetidas);

    /// <summary>
    /// Cambia cada texto (clave o valor) para el que <paramref name="traducir"/> devuelve otro valor.
    /// Las claves de los objetos conservan su orden.
    /// </summary>
    public static Reescritura Reescribir(JsonNode? nodo, Func<string, string?> traducir)
    {
        int cambiadas = 0, repetidas = 0;
        var raiz = Procesar(nodo, traducir, ref cambiadas, ref repetidas);
        return new Reescritura(raiz, cambiadas, repetidas);
    }

    /// <summary>
    /// Tolerancia al comparar la fecha guardada con la del archivo en su sitio nuevo: 2 segundos, la
    /// resolución de FAT32 (exFAT guarda 10 ms y HFS+ 1 s). Lo que cabe en ella es redondeo del sistema
    /// de archivos, no una modificación.
    /// </summary>
    public static readonly long ToleranciaFecha = TimeSpan.FromSeconds(2).Ticks;

    /// <summary>
    /// Pone al día la fecha («M») de las entradas guardadas por ruta con su sello de fecha y tamaño
    /// («M» y «S», en ticks UTC y bytes), cuando el archivo de esa ruta tiene el MISMO tamaño y una
    /// fecha que solo difiere en el redondeo del sistema de archivos. Devuelve cuántas ha cambiado.
    ///
    /// POR QUÉ. Las identificaciones por audio (identificacion.json, una consulta de pago cada una)
    /// solo valen si la fecha del archivo coincide al tick. Al copiar la música a otro disco la fecha
    /// se conserva, pero con la resolución del disco de destino: en un USB en exFAT o FAT32 se
    /// redondea a 10 ms o 2 s, y sin esto todas caducarían al restaurar la copia en el equipo nuevo.
    /// Un archivo retocado de verdad cambia de fecha bastante más (y casi siempre de tamaño).
    /// </summary>
    /// <param name="sello">Fecha (ticks UTC) y tamaño del archivo en este equipo, o null si no está.</param>
    public static int Resellar(JsonNode? nodo, Func<string, (long Ticks, long Bytes)?> sello)
    {
        var n = 0;
        ResellarEn(nodo, sello, ref n);
        return n;
    }

    private static void ResellarEn(JsonNode? nodo, Func<string, (long Ticks, long Bytes)?> sello, ref int n)
    {
        switch (nodo)
        {
            case JsonArray a:
                foreach (var v in a) ResellarEn(v, sello, ref n);
                break;
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                {
                    if (v is JsonObject e && RutaPartida.De(k) != null
                        && Numero(e["M"]) is { } m && Numero(e["S"]) is { } s
                        && sello(k) is { } real && real.Bytes == s && real.Ticks != m
                        && Math.Abs(real.Ticks - m) <= ToleranciaFecha)
                    {
                        e["M"] = real.Ticks;
                        n++;
                        continue;
                    }
                    ResellarEn(v, sello, ref n);
                }
                break;
        }
    }

    /// <summary>Un número entero, leído del archivo (long) o puesto desde el código (int o long).</summary>
    private static long? Numero(JsonNode? n)
    {
        if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        return v.TryGetValue<int>(out var i) ? i : null;
    }

    /// <summary>El nodo que va en lugar de <paramref name="nodo"/>: el mismo, cambiado por dentro, o uno nuevo si era un texto.</summary>
    private static JsonNode? Procesar(JsonNode? nodo, Func<string, string?> traducir, ref int cambiadas, ref int repetidas)
    {
        switch (nodo)
        {
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
            {
                var s = v.GetValue<string>();
                if (traducir(s) is not { } t || string.Equals(t, s, StringComparison.Ordinal)) return nodo;
                cambiadas++;
                return JsonValue.Create(t);
            }
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var n = Procesar(a[i], traducir, ref cambiadas, ref repetidas);
                    if (!ReferenceEquals(n, a[i])) a[i] = n;
                }
                return a;
            case JsonObject o:
            {
                var pares = o.ToList();
                var claves = new string[pares.Count];
                var valores = new JsonNode?[pares.Count];
                var cambiaClave = false;
                for (var i = 0; i < pares.Count; i++)
                {
                    var k = pares[i].Key;
                    claves[i] = k;
                    if (traducir(k) is { } nk && !string.Equals(nk, k, StringComparison.Ordinal))
                    {
                        claves[i] = nk;
                        cambiadas++;
                        cambiaClave = true;
                    }
                    valores[i] = Procesar(pares[i].Value, traducir, ref cambiadas, ref repetidas);
                }

                if (!cambiaClave)
                {
                    for (var i = 0; i < pares.Count; i++)
                        if (!ReferenceEquals(valores[i], pares[i].Value)) o[claves[i]] = valores[i];
                    return o;
                }

                // Con alguna clave nueva se rehace el objeto en el mismo orden. Clear suelta los
                // valores de su padre, así que se pueden volver a colgar.
                o.Clear();
                for (var i = 0; i < pares.Count; i++)
                {
                    if (o.ContainsKey(claves[i])) { repetidas++; continue; }
                    o[claves[i]] = valores[i];
                }
                return o;
            }
            default:
                return nodo;
        }
    }
}
