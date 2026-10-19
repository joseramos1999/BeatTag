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
    /// <summary>
    /// Está cortado, pero ni las etiquetas ni la IA dan con el final. Se enseña igualmente para que
    /// el usuario pueda escribirlo: la cifra de cortados no puede no cuadrar con lo que se ve.
    /// </summary>
    SinPropuesta,
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
    ///
    /// La «x» solo cuenta cuando va suelta, como separador entre dos canciones. Sin esa condición
    /// entraba la «x» final de cualquier palabra, y eso daba por cortado TODO nombre acabado en
    /// «Remix», «Mix» o «Rmx».
    /// </summary>
    private static readonly Regex FinalColgando = new(@"[\-,&(\[]\s*$|(?<![\p{L}\p{N}])x\s*$|\b(feat|ft|vs)\.?\s*$", IC);

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
        var candidatos = new[] { titulo, artista.Length > 0 ? $"{artista} - {titulo}" : "" }
                         .Where(c => c.Length > 0).Select(SinPublicidad).ToList();
        foreach (var candidato in candidatos)
            if (Extender(nombre, candidato) is { } completo)
                return completo;

        // Si empalmar con el texto de la etiqueta le quitaba al nombre su « - », se completa en
        // modo literal: el nombre tal cual y solo la cola que faltaba. Visto en una biblioteca real:
        // «LA PRENDO - BAD GYAL (AGA TECH REMIX) [EXTEN» con la etiqueta «La Prendo (Bad Gyal AgA
        // Tech Remix) [Extended]» quedaba sin el separador del artista.
        foreach (var candidato in candidatos)
            if (Extender(nombre, candidato, literal: true) is { } completo)
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
        // Y la tonalidad con el BPM pegados al final: «… 6A 130», «… 130 6A», también delante del
        // cierre de un paréntesis. Visto en revisión: la IA da la versión «Intro Clean 6A 95»,
        // Compose la envuelve en paréntesis, y con el ancla en el final del texto el «)» impedía
        // quitarlos: «Karol G - Provenza (Intro Clea» → «… (Intro Clean 6A 95)».
        t = Regex.Replace(t, @"\s*(?:\b(?:1[0-2]|[1-9])[AB]\b\s*\d{2,3}|\d{2,3}\s*\b(?:1[0-2]|[1-9])[AB]\b)\s*(?=[\)\]]*\s*$)", "", IC);
        // Un paréntesis que solo traía eso se queda vacío: fuera.
        t = Regex.Replace(t, @"[\(\[]\s*[\)\]]", " ");
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
    ///
    /// Con <paramref name="literal"/> ni siquiera las palabras que coinciden se toman del texto
    /// completo: el nombre se queda letra a letra hasta la palabra cortada, y de
    /// <paramref name="textoCompleto"/> solo se aprovecha lo que va desde esa palabra. Es lo que
    /// necesita la IA, que no escribe el nombre: lo recompone (ver <see cref="DesdeIa"/>).
    /// </summary>
    public static string? Extender(string nombre, string textoCompleto, bool literal = false)
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

                string propuesto;
                if (literal)
                {
                    // El nombre hasta la palabra cortada, tal cual, y desde ella lo que dice el
                    // texto completo. Visto en una biblioteca real: con el empalme desde la primera
                    // palabra, «Bzrp 48 x Aprovecha (97-90 Bpm) (Try It Mash» acababa como «Bzrp 48 x
                    // Aprovecha ((97-90 Bpm) (Try It Mashup))», que es lo que la IA había recompuesto.
                    var empalmado = EmpalmarLiteral(nombre[..n[^1].Pos], textoCompleto[c[j + largo - 1].Pos..], nombre[n[^1].Pos..]);
                    if (empalmado == null) continue;
                    propuesto = Sanear(empalmado);
                    // Ni paréntesis anidados que el nombre no tenía: «(Intr» con «Intro (Clean)»
                    // dejaría «(Intro (Clean))».
                    if (Profundidad(propuesto) > Math.Max(1, Profundidad(nombre))) continue;
                }
                else propuesto = Sanear(SinCierreSuelto(conservado + cola));

                // Completar un nombre no puede crear un «Artista - Título» de más. Visto en la
                // aplicación: «Vazteria X - No Break» (que ni siquiera estaba cortado) empalmaba
                // «Break» con el «Zona Breakbeat DJ's» de la etiqueta de artista y quedaba «Vazteria
                // X - No Breakbeat DJ's; DJ Goku - No Break».
                var maxGuiones = Math.Max(Separadores(nombre), Separadores(textoCompleto));
                if (Separadores(propuesto) > maxGuiones) continue;

                // Ni quitar los que ya tenía. Visto en una biblioteca real: «LA PRENDO - BAD GYAL
                // (AGA TECH REMIX) [EXTEN» acababa como «La Prendo (Bad Gyal (AgA Tech Remix)
                // Extended)», sin el guion que separaba al artista.
                if (Separadores(propuesto) < Separadores(nombre)) continue;

                // Ni los paréntesis o corchetes que ya tenía. Con la etiqueta «La Prendo (Bad Gyal
                // AgA Tech Remix) [Extended]», el empalme a partir de «BAD» dejaba «LA PRENDO - Bad
                // Gyal AgA Tech Remix [Extended]»: el paréntesis del nombre había desaparecido.
                if (Aperturas(propuesto) < Aperturas(nombre)) continue;

                // Si el resultado sigue pareciendo cortado, no se ha completado nada: pasa cuando la
                // etiqueta viene cortada a su vez («Efecto Vs DJ No Pare Remix (DJ»).
                if (propuesto.Count(ch => ch == '(') > propuesto.Count(ch => ch == ')')) continue;
                if (propuesto.Count(ch => ch == '[') > propuesto.Count(ch => ch == ']')) continue;
                if (FinalColgando.IsMatch(propuesto)) continue;

                // Y tiene que aportar algo: si no alarga el nombre, no se ha completado nada.
                if (TextUtils.Nk(propuesto).Length <= TextUtils.Nk(nombre).Length) continue;

                // Los acentos que tenía el nombre se quedan: las etiquetas y la IA suelen venir sin
                // ellos, y renombrar «Feliz Cumpleaños» a «Cumpleanos» es estropear el nombre.
                return AiSuggestion.RestaurarAcentos(propuesto, nombre);
            }
        }

        return null;
    }

    /// <summary>Cuántos «Artista - Título» separa un texto: los guiones con espacio a los dos lados.</summary>
    private static int Separadores(string s) => Regex.Matches(s, @"\s[-–]\s").Count;

    /// <summary>Cuántos paréntesis o corchetes se abren en un texto.</summary>
    private static int Aperturas(string s) => s.Count(ch => ch is '(' or '[');

    /// <summary>Cuántos paréntesis o corchetes hay como mucho uno dentro de otro.</summary>
    private static int Profundidad(string s)
    {
        int nivel = 0, max = 0;
        foreach (var ch in s)
        {
            if (ch is '(' or '[') max = Math.Max(max, ++nivel);
            else if (ch is ')' or ']' && nivel > 0) nivel--;
        }
        return max;
    }

    /// <summary>
    /// Pega a <paramref name="conservado"/> (el nombre hasta la palabra cortada, sin tocar) la
    /// <paramref name="cola"/> que propone el texto completo, desde la palabra que completa la cortada.
    ///
    /// Los cierres de la cola se ajustan a lo que abrió el NOMBRE: el modelo recompone el nombre a su
    /// manera y cierra con «)» lo que el nombre abrió con «[» («… [EXTEN» → «… [EXTENDED]»), y los
    /// cierres que sobran porque el modelo había envuelto la versión en otro paréntesis se quitan. Lo
    /// que el nombre dejó abierto y la cola no cierra se cierra al final. Si es la propia cola la que
    /// abre algo y no lo cierra, el texto completo también venía cortado: null.
    ///
    /// La palabra completada conserva lo que ya estaba escrito, con sus mayúsculas: «MASHU» se
    /// completa como «MASHUP» y no como «Mashup».
    /// </summary>
    private static string? EmpalmarLiteral(string conservado, string cola, string cortadaEnElNombre)
    {
        var cortada = Regex.Match(cortadaEnElNombre, @"^[\p{L}\p{N}']+").Value;
        var completa = Regex.Match(cola, @"^[\p{L}\p{N}']+").Value;
        if (cortada.Length > 0 && completa.StartsWith(cortada, StringComparison.OrdinalIgnoreCase))
        {
            var resto = completa[cortada.Length..];
            var letras = cortada.Where(char.IsLetter).ToList();
            resto = letras.Count > 0 && letras.All(char.IsUpper) ? resto.ToUpperInvariant() : resto.ToLowerInvariant();
            cola = cortada + resto + cola[completa.Length..];
        }

        var abiertos = new Stack<(char Abre, bool DeLaCola)>();
        foreach (var ch in conservado)
        {
            if (ch is '(' or '[') abiertos.Push((ch, false));
            else if (ch is ')' or ']' && abiertos.Count > 0) abiertos.Pop();
        }

        var sb = new System.Text.StringBuilder(conservado, conservado.Length + cola.Length + 2);
        foreach (var ch in cola)
        {
            if (ch is '(' or '[') { abiertos.Push((ch, true)); sb.Append(ch); }
            else if (ch is ')' or ']')
            {
                if (abiertos.Count == 0) continue;   // cierre sin pareja: era del envoltorio del modelo
                sb.Append(abiertos.Pop().Abre == '(' ? ')' : ']');
            }
            else sb.Append(ch);
        }

        if (abiertos.Any(a => a.DeLaCola)) return null;
        foreach (var (abre, _) in abiertos) sb.Append(abre == '(' ? ')' : ']');   // de dentro afuera
        return sb.ToString();
    }

    /// <summary>
    /// Quita un paréntesis o corchete de cierre que se quedó sin su pareja.
    ///
    /// Sale al empalmar cuando el nombre traía el paréntesis convertido en guión: «… AMANECER - DJ
    /// EMILIA» con la etiqueta «… (EMILIANO NEGRI REMIX)» dejaba un «)» colgando al final.
    /// </summary>
    private static string SinCierreSuelto(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        int abiertos = 0, corchetes = 0;
        foreach (var c in s)
        {
            if (c == '(') abiertos++;
            if (c == '[') corchetes++;
            if (c == ')' && abiertos == 0) continue;
            if (c == ']' && corchetes == 0) continue;
            if (c == ')') abiertos--;
            if (c == ']') corchetes--;
            sb.Append(c);
        }
        return sb.ToString();
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
    ///
    /// Y «tal cual» es letra a letra (empalme literal): la propuesta del modelo pasa por
    /// <see cref="AiSuggestion.Compose"/>, que mete la versión entre paréntesis aunque ya traiga los
    /// suyos. Visto en una biblioteca real (4 de 27 renombrados de una tanda): «Todo De Ti X Se
    /// Vuelve Loca v2 (TRY IT MASHU» → «… Loca (v2 (TRY IT MASHUP))».
    /// </summary>
    public static string? DesdeIa(string archivo, string artista, string titulo, string version)
    {
        var nombre = Path.GetFileNameWithoutExtension(archivo);
        // La versión se limpia ANTES de componer (web del pool, tonalidad y BPM): Compose la mete
        // entre paréntesis, y lo que va dentro ya no está al final del texto.
        version = SinPublicidad(version ?? "");

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
            //
            // La publicidad del pool se quita como con las etiquetas: el modelo también la copia.
            // Visto en una biblioteca real: «Doja Cat - Woman [The Shooters Bootleg] [Edm» se
            // «completaba» con «[EdmPacks.com]».
            if (Extender(nombre, SinPublicidad(candidato), literal: true) is { } completo) return completo;
        }

        return null;
    }
}
