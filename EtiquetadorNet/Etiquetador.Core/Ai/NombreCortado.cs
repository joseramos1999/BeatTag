using System.Text.RegularExpressions;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Core.Ai;

/// <summary>De dónde sale el nombre completo, de lo más fiable a lo menos.</summary>
public enum OrigenCompletado
{
    /// <summary>Las etiquetas del propio archivo ya lo traen entero. No hace falta nadie más.</summary>
    Etiquetas,
    /// <summary>La IA lo completó y un catálogo (Deezer/iTunes) confirmó que esa canción existe.</summary>
    IaConfirmada,
    /// <summary>La IA lo completó y ningún catálogo lo confirma.</summary>
    IaSinConfirmar,
}

/// <summary>Un nombre cortado y cómo quedaría completo.</summary>
public sealed record Completado(string Ruta, string Actual, string Propuesto, OrigenCompletado Origen, string Detalle);

/// <summary>
/// Nombres cortados a medias: «Daddy Yankee - Gasolina (F3LY Intro Hype Dur», «…UNA AMAPO».
///
/// Los dejan así los record pools y algunas descargas, que recortan el nombre a una longitud fija.
/// MEDIDO en una biblioteca de 15.173 canciones: 2.514 archivos miden exactamente 44 caracteres,
/// 822 tienen un paréntesis sin cerrar y en 463 la última palabra es el principio de una palabra que
/// SÍ está entera en las etiquetas del archivo.
///
/// De ahí el orden de preferencia: la mayoría se completan con sus PROPIAS ETIQUETAS, sin IA y sin
/// red. La IA solo hace falta para los que llegaron sin etiquetas, y entonces su propuesta se
/// contrasta con el catálogo antes de ofrecerla.
/// </summary>
public static class NombreCortado
{
    private const RegexOptions IC = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Los record pools, que distribuyen pero no interpretan.</summary>
    private static readonly Regex PoolRe = new(Descriptors.PoolRe);

    /// <summary>
    /// Un final que deja la frase a medias: «… (Jose», «… Bad Bunny x», «… feat.».
    ///
    /// NO entran las palabras de enlace corrientes (la, el, de, y…): «The Wiseguys - Ooh La La» es un
    /// título entero que acaba en «La», y con ellas en la lista se daba por cortado.
    /// </summary>
    private static readonly Regex FinalColgando = new(@"[\-,x&(\[]\s*$|\b(feat|ft|vs)\.?\s*$", IC);

    /// <summary>
    /// El nombre parece cortado. Hacen falta señales DUROS de corte, no que las etiquetas digan más:
    /// «Feid - Lady Mi Amor» con la etiqueta «Lady Mi Amor (Extended)» no está cortado, solo es más
    /// corto, y renombrarlo por eso sería otra cosa distinta de la que se pide aquí.
    /// </summary>
    public static bool Parece(string archivo, string tagArtista, string tagTitulo)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);
        if (nombre.Trim().Length < 8) return false;

        // 1. Un paréntesis o corchete que nunca se cierra.
        if (nombre.Count(c => c == '(') > nombre.Count(c => c == ')')) return true;
        if (nombre.Count(c => c == '[') > nombre.Count(c => c == ']')) return true;

        // 2. Termina colgando de un separador o de una palabra de enlace.
        if (FinalColgando.IsMatch(nombre)) return true;

        // 3. La última palabra es el PRINCIPIO de una palabra que las etiquetas traen entera:
        //    «… UNA AMAPO» con la etiqueta «… UNA AMAPOLA».
        //
        // Con eso solo no basta: en «Basstyler - Step Bass» la última palabra es el principio de
        // «Basstyler», que está en sus propias etiquetas, y el nombre no está cortado. Hace falta
        // además que lo que dicen las etiquetas CONTINÚE el nombre, no que se parezca de refilón.
        var ultima = Regex.Match(nombre, @"([\p{L}\p{N}]{3,})\s*$").Groups[1].Value;
        if (ultima.Length == 0) return false;
        var tags = $"{tagArtista} {tagTitulo}";
        return Regex.IsMatch(tags, @"\b" + Regex.Escape(ultima) + @"\p{L}+\b", IC)
               && DesdeEtiquetas(archivo, tagArtista, tagTitulo) != null;
    }

    /// <summary>
    /// El nombre completo según las etiquetas del propio archivo, o null si no sirven.
    ///
    /// Solo vale si lo que hay en el nombre es el PRINCIPIO de lo que dicen las etiquetas: así se
    /// completa el mismo nombre en vez de sustituirlo por otro. Las etiquetas de los record pools a
    /// veces llevan el pack o el editor, y eso no puede acabar renombrando el archivo entero.
    /// </summary>
    public static string? DesdeEtiquetas(string archivo, string tagArtista, string tagTitulo)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);
        var artista = (tagArtista ?? "").Trim();
        var titulo = (tagTitulo ?? "").Trim();
        if (titulo.Length == 0) return null;

        // Visto en la aplicación con la biblioteca real: la etiqueta de artista traía «@liyo dj98»,
        // el alias de quien hizo la edición, y el nombre propuesto empezaba por él. Un alias o un
        // record pool no es el intérprete: se descarta y queda el título, que sí es lo que se completa.
        if (artista.StartsWith("@", StringComparison.Ordinal) || PoolRe.IsMatch(artista)) artista = "";

        var completo = artista.Length > 0 ? $"{artista} - {titulo}" : titulo;
        var propuesto = AiSuggestion.Compose(artista, titulo, "");
        if (propuesto.Length == 0) return null;

        var nkNombre = TextUtils.Nk(nombre);
        var nkCompleto = TextUtils.Nk(completo);
        if (nkNombre.Length == 0 || nkCompleto.Length <= nkNombre.Length) return null;

        // El nombre tiene que ser el principio de lo que dicen las etiquetas, en el orden que sea
        // («Título - Artista» también se usa), y lo que falta, algo con contenido.
        var nkAlReves = TextUtils.Nk($"{titulo} {artista}");
        if (!nkCompleto.StartsWith(nkNombre, StringComparison.Ordinal) &&
            !nkAlReves.StartsWith(nkNombre, StringComparison.Ordinal))
        {
            // Segunda vía: la etiqueta de TÍTULO continúa el título del nombre, aunque la de artista
            // no cuadre. Pasa con los feat largos: «Policia Motores (feat. Le» tiene su título entero
            // en la etiqueta, pero ahí los artistas invitados se listan uno a uno. Se conserva
            // entonces el artista que ya trae el nombre.
            var corte = nombre.IndexOf(" - ", StringComparison.Ordinal);
            if (corte <= 0) return null;
            var artistaDelNombre = nombre[..corte].Trim();
            var tituloDelNombre = nombre[(corte + 3)..].Trim();
            var nkTituloNombre = TextUtils.Nk(tituloDelNombre);
            if (nkTituloNombre.Length == 0 || !TextUtils.Nk(titulo).StartsWith(nkTituloNombre, StringComparison.Ordinal)
                || TextUtils.Nk(titulo).Length <= nkTituloNombre.Length) return null;

            propuesto = AiSuggestion.Compose(artistaDelNombre, titulo, "");
            if (propuesto.Length == 0) return null;
        }

        return string.Equals(propuesto, TextUtils.ToAscii(nombre).Trim(), StringComparison.Ordinal) ? null : propuesto;
    }

    /// <summary>
    /// Lo que completa la IA, validado: tiene que CONTINUAR el nombre cortado, no cambiarlo por otro.
    /// Se compara palabra a palabra, admitiendo que la última esté a medias («Remi» → «Remix»).
    /// </summary>
    public static string? DesdeIa(string archivo, string artista, string titulo, string version)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);
        var propuesto = AiSuggestion.Compose(artista, titulo, version);
        if (propuesto.Length == 0) return null;
        if (string.Equals(propuesto, TextUtils.ToAscii(nombre).Trim(), StringComparison.Ordinal)) return null;

        var palabrasNombre = Palabras(nombre);
        var palabrasPropuesta = Palabras(propuesto);
        if (palabrasNombre.Count == 0 || palabrasPropuesta.Count < palabrasNombre.Count - 1) return null;

        // Todas las palabras del nombre, menos la última (que puede estar cortada), tienen que seguir
        // en la propuesta. La última vale si alguna palabra de la propuesta empieza por ella.
        var enPropuesta = palabrasPropuesta.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < palabrasNombre.Count - 1; i++)
            if (!enPropuesta.Contains(palabrasNombre[i]) &&
                !palabrasPropuesta.Any(p => Matching.JaroWinkler(p, palabrasNombre[i]) >= 0.9))
                return null;

        // Y tiene que COMPLETAR algo: o alarga la palabra cortada, o añade palabras que no estaban.
        //
        // Medido con 40 nombres cortados reales: de 25 propuestas, la mayoría se limitaba a recolocar
        // el trozo cortado entre paréntesis -«… (Paul» → «… (Paul)», «… (CARME-1» → «… (CARME-1)»-.
        // Eso no completa nada y solo da trabajo de revisión.
        var ultima = palabrasNombre[^1];
        var alarga = palabrasPropuesta.Any(p => p.Length > ultima.Length && p.StartsWith(ultima, StringComparison.Ordinal));
        var anade = palabrasPropuesta.Any(p => !palabrasNombre.Contains(p, StringComparer.Ordinal)
                                               && !p.StartsWith(ultima, StringComparison.Ordinal));
        return alarga || anade ? propuesto : null;
    }

    private static List<string> Palabras(string s)
        => Regex.Split(s, @"[^\p{L}\p{N}]+").Select(TextUtils.Nk).Where(w => w.Length >= 2).ToList();
}
