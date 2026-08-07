using System.Globalization;
using System.Text.Json.Nodes;

namespace Etiquetador.Core.Providers;

/// <summary>Accesores seguros sobre JsonNode (nunca lanzan; devuelven vacío/0/null).</summary>
internal static class J
{
    /// <summary>Propiedad hija de un objeto (null si no existe o no es objeto).</summary>
    public static JsonNode? P(JsonNode? n, string name)
        => n is JsonObject o && o.TryGetPropertyValue(name, out var v) ? v : null;

    /// <summary>Ruta de propiedades encadenadas: P(n,"album","cover_big").</summary>
    public static JsonNode? P(JsonNode? n, string a, string b) => P(P(n, a), b);

    public static string S(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s ?? "";
            return v.ToString();
        }
        return "";
    }

    public static int I(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return (int)ds;
        }
        return 0;
    }

    public static double D(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        }
        return 0;
    }

    public static JsonArray? A(JsonNode? n) => n as JsonArray;

    /// <summary>Nº de palabras separadas por espacios (equivale a (title -split '\s+' | ? {$_}).Count).</summary>
    public static int WordCount(string? s)
        => string.IsNullOrEmpty(s) ? 0 : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
