using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Ai;

public enum OrigenFiltro
{
    /// <summary>Lo ha encontrado una regla fija en lo que escribiste. Es lo fiable.</summary>
    Frase,
    /// <summary>Lo ha propuesto la IA y ha pasado las comprobaciones.</summary>
    Ia,
}

/// <summary>
/// Una condición sacada de la frase. <see cref="Descartado"/> dice por qué NO se usa; vacío si se usa.
/// Los descartados se enseñan igualmente: ver qué propuso la IA y por qué no se le hizo caso es lo
/// que permite fiarse de lo que sí se aceptó.
/// </summary>
public sealed record FiltroPropuesto(string Campo, string Valor, string Cita, OrigenFiltro Origen, string Descartado = "")
{
    public bool Valido => Descartado.Length == 0;

    public string NombreCampo => Campo switch
    {
        "genero" => "Género",
        "artista" => "Artista",
        "bpm" => "BPM",
        "anio" => "Años",
        "energia" => "Energía",
        "momento" => "Momento",
        "voz" => "Voz",
        "letra" => "Letra",
        "idioma" => "Idioma",
        _ => Campo,
    };
}

/// <summary>Los géneros y artistas que hay de verdad en la biblioteca, para no buscar lo que no existe.</summary>
public sealed class VocabularioBiblioteca
{
    /// <summary>Clave normalizada → nombre para mostrar. Solo géneros con varias canciones.</summary>
    private readonly Dictionary<string, string> _generos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _artistas = new(StringComparer.Ordinal);

    public VocabularioBiblioteca(IEnumerable<Track> canciones, int minimoPorGenero = 3)
    {
        var cuenta = new Dictionary<string, (string Nombre, int N)>(StringComparer.Ordinal);
        foreach (var t in canciones)
        {
            var g = GenreNormalizer.Canonical(t.Genre);
            var k = Clave(g);
            if (k.Length > 0) cuenta[k] = cuenta.TryGetValue(k, out var v) ? (v.Nombre, v.N + 1) : (g, 1);

            foreach (var a in PartirArtistas(t.Artist))
            {
                var ka = Clave(a);
                // Nombres muy cortos coinciden con cualquier palabra de la frase («La», «DJ»).
                if (ka.Replace(" ", "").Length >= 4) _artistas.TryAdd(ka, a);
            }
        }
        foreach (var (k, v) in cuenta)
            if (v.N >= minimoPorGenero) _generos[k] = v.Nombre;
    }

    private static IEnumerable<string> PartirArtistas(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : Regex.Split(s, @"\s*(?:;|,|&|/|\bfeat\.?\b|\bft\.?\b|\bx\b|\bvs\.?\b)\s*", RegexOptions.IgnoreCase)
                   .Select(x => x.Trim()).Where(x => x.Length > 0);

    /// <summary>Minúsculas, sin acentos ni signos, con las palabras separadas por un espacio.</summary>
    public static string Clave(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = TextUtils.RemoveDiacritics(s).ToLowerInvariant();
        return Regex.Replace(Regex.Replace(d, @"[^a-z0-9]+", " "), @"\s+", " ").Trim();
    }

    /// <summary>Contiene la frase entera como palabras sueltas, no como trozo de otra («rap» no está en «trapero»).</summary>
    public static bool ContienePalabras(string claveTexto, string claveBuscada)
        => claveBuscada.Length > 0 && (" " + claveTexto + " ").Contains(" " + claveBuscada + " ", StringComparison.Ordinal);

    /// <summary>Los géneros de la biblioteca que se nombran en el texto, los más largos primero («afro house» antes que «house»).</summary>
    public IReadOnlyList<string> GenerosEn(string texto)
    {
        var clave = Clave(texto);
        var hallados = new List<string>();
        var usado = clave;
        foreach (var (k, nombre) in _generos.OrderByDescending(g => g.Key.Length))
        {
            if (!ContienePalabras(usado, k)) continue;
            hallados.Add(nombre);
            usado = (" " + usado + " ").Replace(" " + k + " ", " | ").Trim();   // «afro house» no cuenta además como «house»
        }
        return hallados;
    }

    /// <summary>Los artistas de la biblioteca que se nombran en el texto, los más largos primero.</summary>
    public IReadOnlyList<string> ArtistasEn(string texto)
    {
        var clave = Clave(texto);
        return _artistas.Where(a => ContienePalabras(clave, a.Key))
                        .OrderByDescending(a => a.Key.Length).Select(a => a.Value).Take(3).ToList();
    }

    public string? Genero(string valor)
    {
        var k = Clave(GenreNormalizer.Canonical(valor));
        if (k.Length == 0) return null;
        if (_generos.TryGetValue(k, out var exacto)) return exacto;
        // «reggaeton latino» → «Reggaeton»: el género de la biblioteca contenido en lo propuesto.
        return _generos.Where(g => ContienePalabras(k, g.Key)).OrderByDescending(g => g.Key.Length)
                       .Select(g => g.Value).FirstOrDefault();
    }

    public string? Artista(string valor)
    {
        var k = Clave(valor);
        return k.Length > 0 && _artistas.TryGetValue(k, out var a) ? a : null;
    }
}

/// <summary>
/// «Bachata romántica para cerrar» → una colección inteligente.
///
/// MEDIDO ANTES DE DISEÑAR (llama3.2 3B, 8 peticiones reales): pidiéndole el JSON de filtros sin
/// más, el modelo rellenaba TODOS los campos aunque no se pidieran (a «reggaeton suave» le ponía
/// energía 8-9, idioma «LA» y años 2010-2020). Obligándole a citar las palabras que justifican cada
/// filtro mejoró mucho, pero seguía acertando la mitad: convertía «sin palabrotas» en
/// «instrumental», trataba a Bad Bunny como género y no veía «house» ni «techno».
///
/// Con llama3.1:8b y este mismo prompt la extracción es claramente mejor (género, voz, artista y
/// décadas bien citados); sus errores son otros (niveles de energía escritos con la palabra de la
/// frase), y también se corrigen aquí.
///
/// De ahí el diseño en dos capas:
///   1. <see cref="DesdeFrase"/>: reglas fijas para lo que se puede leer sin interpretar (BPM,
///      décadas, «sin palabrotas», momentos) y para los géneros y artistas que EXISTEN en la
///      biblioteca. Esto es lo fiable, y no necesita la IA.
///   2. <see cref="DesdeIa"/>: lo que la IA añade, solo si cita palabras que están de verdad en la
///      frase, con un valor válido, y con una cita que hable de lo que dice el filtro.
/// </summary>
public static class BusquedaIa
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public const string Sistema =
        "Extraes filtros de la peticion de un DJ. Para CADA filtro devuelves el valor y \"cita\": las palabras EXACTAS " +
        "de la peticion que lo justifican. Si ninguna palabra de la peticion lo justifica, NO incluyas ese filtro. " +
        "Es mejor devolver pocos filtros que inventar.\n" +
        "Filtros posibles: \"genero\" (string), \"artista\" (string), \"bpm_min\" (int), \"bpm_max\" (int), " +
        "\"energia\" (\"baja\",\"media\",\"alta\"), \"momento\" (\"apertura\",\"subida\",\"pico\",\"cierre\",\"after\"), " +
        "\"voz\" (\"vocal\",\"instrumental\"), \"letra\" (\"limpia\",\"explicita\"), \"idioma\" (ES, EN, PT, FR, IT), " +
        "\"anio_min\" (int), \"anio_max\" (int).\n" +
        "Formato: {\"filtros\":[{\"campo\":\"...\",\"valor\":...,\"cita\":\"...\"}]}\n" +
        "Ejemplo: peticion \"salsa en ingles para el cierre\" -> {\"filtros\":[{\"campo\":\"genero\",\"valor\":\"salsa\",\"cita\":\"salsa\"}," +
        "{\"campo\":\"idioma\",\"valor\":\"EN\",\"cita\":\"en ingles\"},{\"campo\":\"momento\",\"valor\":\"cierre\",\"cita\":\"para el cierre\"}]}";

    // --- Capa 1: reglas fijas ---

    private static readonly (Regex Re, string Valor)[] Momentos =
    {
        (new(@"\b(abrir|apertura|empezar|arrancar|warm\s*up|inicio de la (noche|sesion|fiesta))\b", IC), "apertura"),
        (new(@"\b(subida|ir subiendo|calentar)\b", IC), "subida"),
        (new(@"\b(pico|peak|hora punta|momento fuerte|lo mas alto|petar(lo|la)?|reventar(lo|la)?)\b", IC), "pico"),
        (new(@"\b(cerrar|cierre|acabar|terminar|despedir|final de la (noche|sesion|fiesta))\b", IC), "cierre"),
        (new(@"\b(after|afterhours?|madrugada)\b", IC), "after"),
    };

    private static readonly (Regex Re, string Valor)[] Idiomas =
    {
        (new(@"\b(en )?(espanol|castellano)(as|es|a)?\b", IC), "ES"),
        (new(@"\b(en )?ingles(as|es|a)?\b|\bin english\b", IC), "EN"),
        (new(@"\b(en )?portugues(as|es|a)?\b", IC), "PT"),
        (new(@"\b(en )?frances(as|es|a)?\b", IC), "FR"),
        (new(@"\b(en )?italian[oa]s?\b", IC), "IT"),
    };

    private static readonly Regex LetraLimpia = new(@"\b(sin (palabrotas|groserias|palabras feas|letras? explicitas?)|limpi[ao]s?|clean|apt[ao]s? para (todos|todo el publico|ninos|familias)|para ninos)\b", IC);
    private static readonly Regex LetraExplicita = new(@"\b(explicit[ao]s?|dirty|con palabrotas)\b", IC);
    private static readonly Regex VozInstrumental = new(@"\binstrumental(es)?\b|\bsin voz\b", IC);
    private static readonly Regex VozVocal = new(@"\b(vocal(es)?|con voz|cantad[ao]s?)\b", IC);
    private static readonly Regex EnergiaBaja = new(@"\b(suaves?|tranquil[ao]s?|relajad[ao]s?|chill|calmad[ao]s?)\b", IC);
    private static readonly Regex EnergiaAlta = new(@"\b(mucha energia|dur[ao]s?|potentes?|caner[ao]s?|intens[ao]s?|energetic[ao]s?|a tope)\b", IC);

    private static readonly Regex BpmRango = new(@"\b(\d{2,3})\s*(?:-|a|y|hasta|to)\s*(\d{2,3})\s*bpm\b|\bentre\s+(\d{2,3})\s+y\s+(\d{2,3})\s*(?:bpm)?\b", IC);
    private static readonly Regex BpmSuelto = new(@"\b(?:a\s+)?(\d{2,3})\s*bpm\b", IC);
    private static readonly Regex Decada = new(@"\b(?:de\s+)?(?:los\s+)?(?:anos\s+)?(\d{2}|19\d0|20\d0)s?\b", IC);
    private static readonly Regex AniosRango = new(@"\b(19\d\d|20\d\d)\s*(?:-|a|y|hasta)\s*(19\d\d|20\d\d)\b", IC);

    /// <summary>Lo que se puede leer en la frase sin interpretar nada.</summary>
    public static List<FiltroPropuesto> DesdeFrase(string peticion, VocabularioBiblioteca voc)
    {
        var res = new List<FiltroPropuesto>();
        var texto = VocabularioBiblioteca.Clave(peticion);   // sin acentos: las reglas se escriben sin ellos
        if (texto.Length == 0) return res;

        void Anadir(string campo, string valor, Match m) => res.Add(new FiltroPropuesto(campo, valor, m.Value.Trim(), OrigenFiltro.Frase));

        var generos = voc.GenerosEn(peticion);
        if (generos.Count > 0) res.Add(new FiltroPropuesto("genero", string.Join(", ", generos), string.Join(", ", generos).ToLowerInvariant(), OrigenFiltro.Frase));

        foreach (var a in voc.ArtistasEn(peticion).Take(1))
            res.Add(new FiltroPropuesto("artista", a, a, OrigenFiltro.Frase));

        // El BPM se lee sobre el texto con números intactos: Clave() no los toca.
        if (BpmRango.Match(texto) is { Success: true } br)
        {
            var a = int.Parse(br.Groups[1].Success ? br.Groups[1].Value : br.Groups[3].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(br.Groups[2].Success ? br.Groups[2].Value : br.Groups[4].Value, CultureInfo.InvariantCulture);
            if (BpmValido(a) && BpmValido(b)) Anadir("bpm", $"{Math.Min(a, b)}-{Math.Max(a, b)}", br);
        }
        else if (BpmSuelto.Match(texto) is { Success: true } bs && int.TryParse(bs.Groups[1].Value, out var bpm) && BpmValido(bpm))
            Anadir("bpm", $"{bpm - 2}-{bpm + 2}", bs);   // «a 128 bpm» admite el margen de un pitch pequeño

        if (AniosRango.Match(texto) is { Success: true } ar)
            Anadir("anio", $"{ar.Groups[1].Value}-{ar.Groups[2].Value}", ar);
        else
            foreach (Match d in Decada.Matches(texto))
            {
                // Solo cuenta como década si la frase lo dice como década: «de los 80», «años 90», «1980s».
                if (!Regex.IsMatch(d.Value, @"\blos\b|\banos\b|s$", IC)) continue;
                if (RangoDecada(d.Groups[1].Value) is { } rango) { Anadir("anio", rango, d); break; }
            }

        foreach (var (re, valor) in Momentos)
            if (re.Match(texto) is { Success: true } m) Anadir("momento", valor, m);

        foreach (var (re, valor) in Idiomas)
            if (re.Match(texto) is { Success: true } m) { Anadir("idioma", valor, m); break; }

        if (LetraLimpia.Match(texto) is { Success: true } ll) Anadir("letra", "limpia", ll);
        else if (LetraExplicita.Match(texto) is { Success: true } le) Anadir("letra", "explicita", le);

        if (VozInstrumental.Match(texto) is { Success: true } vi) Anadir("voz", "instrumental", vi);
        else if (VozVocal.Match(texto) is { Success: true } vv) Anadir("voz", "vocal", vv);

        if (EnergiaBaja.Match(texto) is { Success: true } eb) Anadir("energia", "baja", eb);
        else if (EnergiaAlta.Match(texto) is { Success: true } ea) Anadir("energia", "alta", ea);

        return res;
    }

    private static bool BpmValido(int bpm) => bpm is >= 50 and <= 220;

    /// <summary>«80» → 1980-1989; «2000» → 2000-2009; «10» → 2010-2019.</summary>
    public static string? RangoDecada(string d)
    {
        if (!int.TryParse(d, out var n)) return null;
        int inicio;
        if (d.Length == 4) inicio = n;
        else if (n % 10 != 0) return null;
        else inicio = n >= 50 ? 1900 + n : 2000 + n;
        if (inicio % 10 != 0 || inicio < 1950 || inicio > 2090) return null;
        return $"{inicio}-{inicio + 9}";
    }

    // --- Capa 2: lo que propone la IA, validado ---

    private static readonly HashSet<string> Energias = new(StringComparer.Ordinal) { "baja", "media", "alta" };
    private static readonly HashSet<string> MomentosValidos = new(StringComparer.Ordinal) { "apertura", "subida", "pico", "cierre", "after" };
    private static readonly HashSet<string> IdiomasValidos = new(StringComparer.Ordinal) { "ES", "EN", "PT", "FR", "IT" };

    /// <summary>
    /// Para ciertos campos, la cita tiene que HABLAR de ese campo. Es la comprobación que caza el
    /// fallo medido: «sin palabrotas» propuesto como voz instrumental cita palabras que están en la
    /// frase, pero no dicen nada de la voz.
    /// </summary>
    private static readonly Dictionary<string, Regex> CitaDebeHablarDe = new()
    {
        ["voz"] = new(@"instrumental|vocal|\bvoz\b|cantad|canta", IC),
        ["letra"] = new(@"palabrot|groser|limpi|clean|explicit|dirty|ninos|publico|familia", IC),
        ["idioma"] = new(@"espanol|castellan|ingles|english|portugu|frances|italian", IC),
    };

    public static List<FiltroPropuesto> DesdeIa(JsonNode? json, string peticion, VocabularioBiblioteca voc)
    {
        var res = new List<FiltroPropuesto>();
        if (J.A(J.P(json, "filtros")) is not { } lista) return res;
        var textoFrase = VocabularioBiblioteca.Clave(peticion);
        int? bpmMin = null, bpmMax = null, anioMin = null, anioMax = null;
        string citaBpm = "", citaAnio = "";

        foreach (var nodo in lista)
        {
            var campo = J.S(J.P(nodo, "campo")).Trim().ToLowerInvariant();
            var valor = ValorTexto(J.P(nodo, "valor"));
            var cita = J.S(J.P(nodo, "cita")).Trim();
            if (campo.Length == 0 || valor.Length == 0) continue;

            FiltroPropuesto Fuera(string motivo, string c = "") => new(c.Length > 0 ? c : campo, valor, cita, OrigenFiltro.Ia, motivo);
            FiltroPropuesto Dentro(string c, string v) => new(c, v, cita, OrigenFiltro.Ia);

            var claveCita = VocabularioBiblioteca.Clave(cita);
            if (claveCita.Length == 0 || !VocabularioBiblioteca.ContienePalabras(textoFrase, claveCita))
            {
                res.Add(Fuera("No lo justifica con palabras de tu frase."));
                continue;
            }
            if (CitaDebeHablarDe.TryGetValue(campo, out var re) && !re.IsMatch(claveCita))
            {
                res.Add(Fuera($"«{cita}» no dice nada de {campo}."));
                continue;
            }

            switch (campo)
            {
                case "genero":
                    if (voc.Genero(valor) is { } g) res.Add(Dentro("genero", g));
                    else if (voc.Artista(valor) is { } comoArtista) res.Add(Dentro("artista", comoArtista));
                    else res.Add(Fuera("No hay ese género en tu biblioteca."));
                    break;
                case "artista":
                    if (voc.Artista(valor) is { } a) res.Add(Dentro("artista", a));
                    else res.Add(Fuera("No hay ese artista en tu biblioteca."));
                    break;
                case "energia":
                    // llama3.1:8b devuelve a veces la palabra de la frase («suave», «duro») en vez del
                    // nivel. Es la respuesta correcta mal escrita: se traduce con las mismas reglas.
                    var e = VocabularioBiblioteca.Clave(valor);
                    if (!Energias.Contains(e))
                        e = EnergiaBaja.IsMatch(e) ? "baja" : EnergiaAlta.IsMatch(e) ? "alta" : e;
                    res.Add(Energias.Contains(e) ? Dentro("energia", e) : Fuera("Valor de energía no válido."));
                    break;
                case "momento":
                    var mo = VocabularioBiblioteca.Clave(valor);
                    res.Add(MomentosValidos.Contains(mo) ? Dentro("momento", mo) : Fuera("No es un momento de la sesión."));
                    break;
                case "voz":
                    var vz = VocabularioBiblioteca.Clave(valor);
                    res.Add(vz is "vocal" or "instrumental" ? Dentro("voz", vz) : Fuera("Valor de voz no válido."));
                    break;
                case "letra":
                    var le = VocabularioBiblioteca.Clave(valor);
                    res.Add(le is "limpia" or "explicita" ? Dentro("letra", le) : Fuera("Valor de letra no válido."));
                    break;
                case "idioma":
                    var id = valor.Trim().ToUpperInvariant();
                    res.Add(IdiomasValidos.Contains(id) ? Dentro("idioma", id) : Fuera("Idioma no válido."));
                    break;
                case "bpm_min" or "bpm_max":
                    if (int.TryParse(valor, out var b) && BpmValido(b))
                    {
                        if (campo == "bpm_min") bpmMin = b; else bpmMax = b;
                        citaBpm = cita;
                    }
                    else res.Add(Fuera("BPM no válido.", "bpm"));
                    break;
                case "anio_min" or "anio_max":
                    if (int.TryParse(valor, out var y))
                    {
                        // El modelo devuelve «80» para «de los 80»: es una década, no el año 80.
                        if (y < 100 && RangoDecada(y.ToString("00")) is { } rango)
                        {
                            var p = rango.Split('-');
                            if (campo == "anio_min") anioMin = int.Parse(p[0]); else anioMax = int.Parse(p[1]);
                            if (campo == "anio_min" && anioMax == null) anioMax = int.Parse(p[1]);
                        }
                        else if (y is >= 1900 and <= 2100) { if (campo == "anio_min") anioMin = y; else anioMax = y; }
                        citaAnio = cita;
                    }
                    break;
                default:
                    res.Add(Fuera("Campo desconocido."));
                    break;
            }
        }

        if (bpmMin != null || bpmMax != null)
            res.Add(new FiltroPropuesto("bpm", $"{bpmMin ?? 0}-{bpmMax ?? 0}", citaBpm, OrigenFiltro.Ia));
        if (anioMin != null || anioMax != null)
            res.Add(new FiltroPropuesto("anio", $"{anioMin ?? 0}-{anioMax ?? 0}", citaAnio, OrigenFiltro.Ia));
        return res;
    }

    private static string ValorTexto(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => ((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture),
        _ => J.S(n).Trim(),
    };

    /// <summary>
    /// Junta las dos capas. Lo que encontró la regla fija manda: si la frase ya dice el género, lo
    /// que proponga la IA para el género no se añade (tampoco se enseña, sería ruido). La IA solo
    /// aporta campos que la regla no cubrió, más los descartes, para que se vea qué se ignoró.
    /// </summary>
    public static List<FiltroPropuesto> Combinar(IReadOnlyList<FiltroPropuesto> frase, IReadOnlyList<FiltroPropuesto> ia)
    {
        var res = new List<FiltroPropuesto>(frase);
        var cubiertos = new HashSet<string>(frase.Select(f => f.Campo), StringComparer.Ordinal);
        foreach (var f in ia)
        {
            if (cubiertos.Contains(f.Campo)) continue;
            // Varios momentos válidos se suman; del resto, uno por campo.
            if (f.Valido && f.Campo != "momento") cubiertos.Add(f.Campo);
            if (res.Any(x => x.Campo == f.Campo && x.Valor == f.Valor)) continue;
            res.Add(f);
        }
        return res;
    }

    /// <summary>La colección que resulta de los filtros aceptados.</summary>
    public static ColeccionInteligente AColeccion(IEnumerable<FiltroPropuesto> aceptados, string nombre)
    {
        var c = new ColeccionInteligente { Nombre = nombre };
        var generos = new List<string>();
        foreach (var f in aceptados.Where(f => f.Valido))
        {
            switch (f.Campo)
            {
                case "genero": generos.AddRange(Fichas.PartirLista(f.Valor)); break;
                case "artista": c.Texto = f.Valor; break;
                case "bpm": (c.BpmMin, c.BpmMax) = Rango(f.Valor); break;
                case "anio": (c.AnioMin, c.AnioMax) = Rango(f.Valor); break;
                case "energia":
                    (c.EnergiaMin, c.EnergiaMax) = f.Valor switch { "baja" => (1, 4), "media" => (4, 7), _ => (7, 10) };
                    break;
                case "momento":
                    c.Momentos |= f.Valor switch
                    {
                        "apertura" => MomentoSet.Apertura, "subida" => MomentoSet.Subida, "pico" => MomentoSet.Pico,
                        "cierre" => MomentoSet.Cierre, _ => MomentoSet.After,
                    };
                    break;
                case "voz": c.Voz = f.Valor == "instrumental" ? TipoVoz.Instrumental : TipoVoz.Vocal; break;
                case "letra": c.Letra = f.Valor == "limpia" ? TipoLetra.Limpia : TipoLetra.Explicita; break;
                case "idioma": c.Idioma = f.Valor; break;
            }
        }
        c.Genero = string.Join(", ", generos.Distinct(StringComparer.OrdinalIgnoreCase));
        return c;
    }

    private static (int, int) Rango(string valor)
    {
        var p = valor.Split('-');
        int.TryParse(p.ElementAtOrDefault(0), out var a);
        int.TryParse(p.ElementAtOrDefault(1), out var b);
        return (a, b);
    }

    /// <summary>Los campos que solo encuentran canciones con ficha rellenada.</summary>
    public static bool DependeDeFicha(string campo) => campo is "energia" or "momento" or "voz" or "idioma";
}
