using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etiquetador.Core.Analysis;

namespace Etiquetador.Core.Providers;

/// <summary>
/// Reglas de casación que comparten los catálogos (Deezer, iTunes, Spotify, MusicBrainz).
///
/// Existen en un solo sitio porque, repartidas, un arreglo medido se quedaba en una sola fuente:
/// el umbral del respaldo aproximado se subió a 0,95 en Deezer tras medirlo sobre 12.428
/// canciones, mientras iTunes seguía en 0,90 y 0,88 y colaba exactamente los mismos errores.
/// </summary>
public static class ReglasCatalogo
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Versiones «herramienta»: la misma canción sin voz o solo con la voz. Para un DJ son otro
    /// archivo, no la canción. (El karaoke ya lo descarta BadRe; aquí se repite para iTunes y
    /// para quien pregunte con <see cref="EsHerramienta"/>.)
    /// </summary>
    private static readonly Regex HerramientaRe =
        new(@"\b(?:instrumental(?:es)?|karaoke|playback|backing\s*tracks?)\b|\binst\b\.?", IC);

    /// <summary>
    /// ¿El texto nombra una versión instrumental o acapella? Las acapellas se reconocen con la
    /// misma regla que ya usa la comprobación de audio (<see cref="Identificacion.MencionaAcapella"/>),
    /// que admite todas sus grafías.
    /// </summary>
    public static bool EsHerramienta(string? texto)
        => !string.IsNullOrEmpty(texto) && (HerramientaRe.IsMatch(texto) || Identificacion.MencionaAcapella(texto));

    /// <summary>
    /// Parecido mínimo entre títulos para el respaldo aproximado. Estaba en 0,88/0,90 y era
    /// demasiado laxo: medido sobre una tirada real de 12.428 canciones, en la franja 0,88-0,95
    /// entraban temas DISTINTOS que empiezan igual («Carnavalito Style» contra «Carnavalito
    /// Soleado»). A 0,95 entran las erratas de verdad y ninguno de esos.
    /// </summary>
    public const double UmbralTituloAproximado = 0.95;

    /// <summary>Parecido mínimo entre artistas en el respaldo aproximado (el título, entonces, tiene que estar incluido).</summary>
    public const double UmbralArtistaAproximado = 0.90;

    /// <summary>Diferencia de duración (s) que se admite como «la misma grabación» en el respaldo aproximado.</summary>
    public const int ToleranciaDuracion = 4;

    /// <summary>
    /// Un candidato del catálogo, ya leído de su JSON, para el respaldo aproximado.
    /// <see cref="VersionNoPedida"/> lo decide cada fuente con sus propias expresiones.
    /// </summary>
    public readonly record struct CandidatoAproximado(JsonNode Nodo, string Artista, string Titulo, int Dur, bool VersionNoPedida);

    /// <summary>
    /// Último recurso cuando la vía normal no encontró nada: tolera ERRATAS del nombre del archivo,
    /// en el artista («Bad Buny») o en el título («Prrum» por «Prrrum»).
    ///
    /// Es para erratas, no para segundas oportunidades. Visto en una biblioteca real: el original
    /// de 331 s «Los Chicanos del Sur - Beso a Beso» solo encontraba en Deezer un remix de 247 s; la
    /// vía normal lo RECHAZABA (remix no pedido, 84 s de diferencia) y este respaldo lo rescataba
    /// con confianza 5, así que se renombraba como el remix sin aviso. Igual «Wisin - Fie» (229 s)
    /// acababa como «Fiel (Remix)» de 349 s. Por eso:
    ///   · no se vuelve a mirar lo que la vía normal ya juzgó (mismo artista y título incluido);
    ///   · no entran las versiones no pedidas (remix, directo, instrumental, acapella…);
    ///   · si las dos duraciones se conocen, tienen que cuadrar.
    /// </summary>
    public static JsonNode? RespaldoAproximado(IEnumerable<CandidatoAproximado> candidatos, string na, string nt,
        int localDur, bool isEdit, out double score, out string motivo)
    {
        score = -999;
        motivo = "";
        if (na.Length == 0 || nt.Length == 0) return null;

        JsonNode? mejorParecido = null;
        double mejorJw = 0.0;
        foreach (var c in candidatos)
        {
            var an = TextUtils.Nk(c.Artista);
            var tn = TextUtils.Nk(c.Titulo);
            if (an.Length == 0 || tn.Length == 0 || c.VersionNoPedida) continue;

            var aStrict = Matching.ArtistMatch(na, an);
            var tStrict = tn.Contains(nt) || nt.Contains(tn);
            // Ya puntuó (y, si no está elegido, fue por algo) en la vía normal.
            if (aStrict && tStrict) continue;

            // El artista puede ir algo más suelto porque esta rama exige además que el título
            // coincida por inclusión, que ya es una guarda fuerte.
            var aFuzzy = na.Length >= 5 && an.Length >= 5 && Matching.JaroWinkler(na, an) >= UmbralArtistaAproximado;
            // El título, no: aquí el artista casa siempre (es el mismo intérprete con varios
            // temas), así que el parecido del título es lo único que decide.
            var tFuzzy = nt.Length >= 5 && tn.Length >= 5 && Matching.JaroWinkler(nt, tn) >= UmbralTituloAproximado;
            var conocida = localDur > 0 && c.Dur > 0;
            var durOk = conocida && Math.Abs(c.Dur - localDur) <= ToleranciaDuracion;
            // Sin las dos duraciones no hay contradicción que ver; con ellas, deben cuadrar.
            var durSinContradiccion = !conocida || durOk;

            if ((aFuzzy && tStrict && durSinContradiccion) || (aStrict && tFuzzy && durOk && !isEdit))
            {
                score = 5;
                motivo = "coincidencia aproximada: tolera erratas del nombre";
                return c.Nodo;
            }

            // Mismo artista y un título PARECIDO pero no incluido: erratas del nombre del archivo
            // («Prrum» por «Prrrum», «CUENTA REGREVISA» por «Cuenta Regresiva»). Como esta vía
            // asigna confianza 4, por encima del umbral de revisión, equivocar una etiqueta aquí
            // se aplicaría sin que nadie la mirara: se pierde algún acierto aislado, pero dejar una
            // canción sin identificar se ve en la lista y una etiqueta mala no.
            if (aStrict && !tStrict && nt.Length >= 6 && tn.Length >= 6)
            {
                var jw = Matching.JaroWinkler(nt, tn);
                if (jw >= UmbralTituloAproximado && jw > mejorJw) { mejorJw = jw; mejorParecido = c.Nodo; }
            }
        }
        if (mejorParecido != null)
        {
            score = 4;
            motivo = $"mismo artista y título casi igual ({mejorJw:0.00} de parecido)";
        }
        return mejorParecido;
    }

    /// <summary>
    /// ¿El título del catálogo es de verdad el del archivo? Para las fuentes que no puntúan el
    /// título por su cuenta (MusicBrainz). Casa si es el mismo, si uno contiene al otro y el
    /// contenido es al menos la MITAD del otro, o si difieren en una errata (parecido ≥ 0,95).
    ///
    /// La mitad es lo que separa «Gasolina» de «Gasolina (DJ Buddha Remix)» -que el limpiado deja
    /// iguales- de «Fogo» dentro de «Watch out for fogo», que es otra canción: visto en una
    /// biblioteca real, «Garmiani - Watch out for fogo» se proponía como «Garmiani, Julimar Santos -
    /// Fogo». Los títulos sin letras latinas no se pueden comparar así y no casan nunca: mejor sin
    /// identificar que con el título de otra.
    /// </summary>
    public static bool TituloVerificado(string? tituloArchivo, string? tituloCatalogo, out bool exacto)
    {
        exacto = false;
        var nt = TextUtils.NkClave(Descriptors.CleanKeywords(tituloArchivo));
        var rn = TextUtils.NkClave(Descriptors.CleanKeywords(tituloCatalogo));
        if (nt.Length == 0 || rn.Length == 0) return false;
        if (nt == rn) { exacto = true; return true; }

        var corto = nt.Length <= rn.Length ? nt : rn;
        var largo = nt.Length <= rn.Length ? rn : nt;
        if (corto.Length >= 4 && largo.Contains(corto) && corto.Length * 2 >= largo.Length) return true;

        return nt.Length >= 5 && rn.Length >= 5 && Matching.JaroWinkler(nt, rn) >= UmbralTituloAproximado;
    }
}
