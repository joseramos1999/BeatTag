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
    /// COMPLETA el nombre que ya hay; no lo recompone. La primera versión de esto armaba
    /// «Artista - Título» con las etiquetas, y sobre una biblioteca real salió mal de tres formas a
    /// la vez: anteponía listas larguísimas de artistas a nombres que ya estaban bien (284 de 827
    /// crecían más de 40 caracteres), repetía el artista cuando ya estaba en el nombre («Bad Bunny x
    /// Feid - Bad Bunny x Feid - …», 49 casos) y perdía los acentos al pasar todo a ASCII («Feliz
    /// Cumpleaños» → «Cumpleanos», 81 casos).
    ///
    /// Ahora solo se añade LO QUE FALTA, al final, conservando el nombre tal cual estaba.
    /// </summary>
    public static string? DesdeEtiquetas(string archivo, string tagArtista, string tagTitulo)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);
        var artista = (tagArtista ?? "").Trim();
        var titulo = (tagTitulo ?? "").Trim();
        if (titulo.Length == 0) return null;

        // Un alias de editor o un record pool no es el intérprete: «@liyo dj98» no puede acabar
        // delante del nombre.
        if (artista.StartsWith("@", StringComparison.Ordinal) || PoolRe.IsMatch(artista)) artista = "";

        // Se prueba primero con el título solo: si el nombre ya trae su artista, meter el de la
        // etiqueta sería repetirlo.
        foreach (var candidato in new[] { titulo, artista.Length > 0 ? $"{artista} - {titulo}" : "" })
            if (candidato.Length > 0 && Extender(nombre, SinPublicidad(candidato)) is { } completo)
                return completo;

        return null;
    }

    /// <summary>Una web, un correo o un usuario de red social dentro del texto de una etiqueta.</summary>
    private static readonly Regex Web = new(@"@?\b[\w-]+\.(?:com|net|org|io|fm|es|info|vip|club|to|me|blogspot\.com)\b|https?://\S+|\bwww\.\S+", IC);

    /// <summary>
    /// Quita de una etiqueta lo que no es el nombre de la canción: la web del pack y la tonalidad con
    /// el BPM estampados al final.
    ///
    /// Medido en la biblioteca real: al completar desde las etiquetas se colaban nombres como «…
    /// (DJ Baur vs DJ Nejtrino Mashup)@djxizmusic.blogspot.com» o «… [Catchfraze &amp; Zapdos Mashup]
    /// [EdmPacks.com] 6A 130». Completar un nombre no puede ser meterle la publicidad del pool.
    /// </summary>
    private static string SinPublicidad(string texto)
    {
        // Primero los paréntesis o corchetes que solo contienen la web: se van enteros.
        var t = Regex.Replace(texto, @"[\[(]\s*[^\[\]()]*[\])]", m => Web.IsMatch(m.Value) ? " " : m.Value);
        t = Web.Replace(t, " ");
        // Y la tonalidad con el BPM pegados al final: «… 6A 130», «… 130 6A».
        t = Regex.Replace(t, @"\s*(?:\b(?:1[0-2]|[1-9])[AB]\b\s*\d{2,3}|\d{2,3}\s*\b(?:1[0-2]|[1-9])[AB]\b)\s*$", "", IC);
        return Regex.Replace(t, @"\s{2,}", " ").Trim().TrimEnd('-', '@', ' ');
    }

    /// <summary>
    /// Empalma <paramref name="nombre"/> con <paramref name="textoCompleto"/> por donde los dos
    /// coinciden, y devuelve el nombre ya completo. Null si no coinciden en nada aprovechable.
    ///
    /// Se busca la coincidencia MÁS LARGA entre el final del nombre y un trozo del texto completo,
    /// admitiendo que la última palabra del nombre esté cortada: en «… - Mos» con «Moscow Mule x Mi
    /// Gente (Transition 100-105 Bpm)» coincide «Mos» con «Moscow», así que lo que hay delante del
    /// empalme -«Bad Bunny X J Balvin X Comando Tiburon - »- se queda TAL CUAL y detrás va el texto
    /// completo. Así no se antepone nada, no se repite el artista y no se pierde lo que ya estaba.
    /// </summary>
    public static string? Extender(string nombre, string textoCompleto)
    {
        var n = PalabrasCon(nombre);
        var c = PalabrasCon(textoCompleto);
        if (n.Count == 0 || c.Count == 0) return null;

        for (var largo = Math.Min(n.Count, c.Count); largo >= 1; largo--)
        {
            var desde = n.Count - largo;
            for (var j = 0; j + largo <= c.Count; j++)
            {
                var cuadra = true;
                for (var k = 0; k < largo - 1 && cuadra; k++)
                    cuadra = string.Equals(n[desde + k].Nk, c[j + k].Nk, StringComparison.Ordinal);
                if (!cuadra) continue;

                // La última palabra del nombre es la que puede estar cortada: vale si la del texto
                // completo empieza por ella.
                var ultimaNombre = n[^1].Nk;
                var ultimaTexto = c[j + largo - 1].Nk;
                if (!ultimaTexto.StartsWith(ultimaNombre, StringComparison.Ordinal)) continue;

                // Empalmar por una sola palabra es frágil: solo se admite si esa palabra es larga y
                // está claramente cortada («Mos» → «Moscow»), nunca si coincide entera.
                if (largo == 1 && (ultimaNombre.Length < 3 || ultimaTexto.Length == ultimaNombre.Length)) continue;

                var conservado = nombre[..n[desde].Pos];
                var cola = textoCompleto[c[j].Pos..];

                // Lo que se pega no puede repetir lo que se conserva. Sin esto, «Basstyler - Step
                // Bass» empalmaba «Bass» con «Basstyler» y salía «Basstyler - Step Basstyler - Step
                // Bass»: el empalme estaba en el sitio equivocado.
                var yaEstaban = PalabrasCon(conservado).Select(p => p.Nk).Where(w => w.Length >= 3).ToHashSet(StringComparer.Ordinal);
                if (PalabrasCon(cola).Any(p => p.Nk.Length >= 3 && yaEstaban.Contains(p.Nk))) continue;

                var propuesto = Sanear(conservado + cola);

                // Y tiene que aportar algo: si no alarga el nombre, no se ha completado nada.
                if (TextUtils.Nk(propuesto).Length <= TextUtils.Nk(nombre).Length) continue;

                // Los acentos que tenía el nombre se quedan: las etiquetas y la IA suelen venir sin
                // ellos, y renombrar «Feliz Cumpleaños» a «Cumpleanos» es estropear el nombre.
                return AiSuggestion.RestaurarAcentos(propuesto, nombre);
            }
        }

        return null;
    }

    /// <summary>Las palabras de un texto (sin acentos ni mayúsculas) con su posición en el original.</summary>
    private static List<(string Nk, int Pos)> PalabrasCon(string texto)
        => Regex.Matches(texto, @"[\p{L}\p{N}']+")
                .Select(m => (Nk: TextUtils.Nk(m.Value), m.Index))
                .Where(p => p.Nk.Length > 0)
                .Select(p => (p.Nk, Pos: p.Index))
                .ToList();

    /// <summary>
    /// Deja el texto utilizable como nombre de archivo SIN tocar acentos ni eñes: solo quita los
    /// caracteres que Windows no admite y colapsa los espacios dobles.
    /// </summary>
    public static string Sanear(string s)
    {
        var t = Regex.Replace(s.Normalize(System.Text.NormalizationForm.FormC), @"[""<>|?*:]", "");
        t = Regex.Replace(t, @"[/\\]", " ");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim();
        return t.TrimEnd('.', ' ').Trim();
    }

    /// <summary>
    /// Lo que completa la IA, validado: solo puede CONTINUAR el nombre cortado por donde se cortó.
    ///
    /// Antes se admitía que la propuesta reordenase o rehiciera el nombre mientras conservara sus
    /// palabras, y con eso el modelo acababa reescribiéndolo entero. Ahora tiene que empezar
    /// exactamente por el nombre actual: lo único que puede hacer es añadirle la cola que le falta,
    /// y lo que ya estaba escrito se queda tal cual, con sus acentos.
    /// </summary>
    public static string? DesdeIa(string archivo, string artista, string titulo, string version)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);

        // Se prueba también con el título y la versión solos: el modelo casi siempre rellena el campo
        // de artista, y si el nombre no empezaba por ese artista, anteponerlo no sería completarlo.
        foreach (var candidato in new[]
                 {
                     AiSuggestion.Compose(artista, titulo, version, conservarAcentos: true),
                     AiSuggestion.Compose("", titulo, version, conservarAcentos: true),
                 })
        {
            if (candidato.Length == 0) continue;

            // El empalme ya exige que la propuesta aporte texto nuevo, y eso descarta de paso el
            // fallo más repetido del modelo: medido con 40 nombres cortados reales, de 25 propuestas
            // la mayoría se limitaba a recolocar el trozo cortado entre paréntesis -«… (Paul» → «…
            // (Paul)», «… (CARME-1» → «… (CARME-1)»-. Eso no completa nada.
            if (Extender(nombre, candidato) is { } completo) return completo;
        }

        return null;
    }
}
