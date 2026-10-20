using System.Text;

namespace Etiquetador.Core.Copias;

// ---------------------------------------------------------------------------------------------
// Reescritura de rutas al restaurar una copia en otro sitio (equipo «volumen_copias», tanda 4, T15)
// ---------------------------------------------------------------------------------------------
//
// POR QUÉ. Casi todo el trabajo del DJ está guardado por RUTA de archivo: las fichas, lo aplicado y
// lo descartado en Enriquecer, las casillas, las identificaciones pagadas, las reglas de género por
// carpeta… Si la música cambia de sitio (otro disco, otra letra de unidad, o de Windows a macOS:
// «D:\Musica» pasa a ser «/Volumes/USB/Musica»), una copia restaurada tal cual no casaría con
// ninguna canción. Es la misma idea que RekordboxRelocator, que reescribe las Location del XML con
// un mapa de ruta antigua → ruta nueva; aquí el mapa va de CARPETA RAÍZ antigua → nueva, y lo
// confirma el usuario antes de restaurar.
//
// Entre sistemas cambian también el separador (\ frente a /) y, en macOS, la forma de escribir los
// acentos: HFS+ guarda los nombres descompuestos (NFD: «a» + tilde), Windows y casi todo lo demás
// compuestos (NFC: «á»). Para comparar se normaliza todo a NFC; para escribir se elige la forma
// que tiene el archivo en el disco de destino si se puede mirar (ver Traducir).

/// <summary>Cómo escribe las rutas un sistema.</summary>
public enum EstiloRuta { Windows, Unix }

/// <summary>Una raíz de la copia y dónde está ahora. Destino vacío: no cambia.</summary>
public sealed record CambioRaiz(string Origen, string Destino);

/// <summary>Una ruta absoluta partida en volumen y tramos, para comparar sin depender del separador.</summary>
public sealed record RutaPartida(EstiloRuta Estilo, string Volumen, IReadOnlyList<string> Tramos)
{
    /// <summary>
    /// Parte una ruta absoluta. null si no lo es (texto que no es una ruta, una ruta relativa…).
    ///   Windows   «D:\Musica\x.mp3»           → volumen «D:», tramos [Musica, x.mp3]
    ///   red       «\\nas\musica\x.mp3»        → volumen «\\nas\musica», tramos [x.mp3]
    ///   macOS     «/Volumes/USB/Musica/x.mp3» → volumen «/», tramos [Volumes, USB, Musica, x.mp3]
    /// En las de Windows valen las dos barras; en las de macOS la invertida es un carácter más del
    /// nombre, como en el propio sistema.
    /// </summary>
    public static RutaPartida? De(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || ruta.Length < 2) return null;
        if (ruta.Length >= 3 && char.IsAsciiLetter(ruta[0]) && ruta[1] == ':' && ruta[2] is '\\' or '/')
            return new(EstiloRuta.Windows, char.ToUpperInvariant(ruta[0]) + ":", Partir(ruta[3..], windows: true));
        if (ruta.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var p = Partir(ruta[2..], windows: true);
            if (p.Count < 2) return null;
            return new(EstiloRuta.Windows, @"\\" + p[0] + @"\" + p[1], p.Skip(2).ToList());
        }
        if (ruta[0] == '/' && ruta[1] != '/')
        {
            var p = Partir(ruta, windows: false);
            return p.Count == 0 ? null : new(EstiloRuta.Unix, "/", p);
        }
        return null;
    }

    private static List<string> Partir(string resto, bool windows)
        => resto.Split(windows ? new[] { '\\', '/' } : new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>La ruta escrita con su estilo («D:\a\b», «/a/b»).</summary>
    public string Texto()
    {
        if (Estilo == EstiloRuta.Unix) return "/" + string.Join("/", Tramos);
        var baseVol = Volumen.StartsWith(@"\\", StringComparison.Ordinal) ? Volumen : Volumen + @"\";
        return Tramos.Count == 0 ? baseVol : baseVol.TrimEnd('\\') + @"\" + string.Join(@"\", Tramos);
    }

    /// <summary>Mismo volumen (sin distinguir mayúsculas).</summary>
    public bool MismoVolumen(RutaPartida otra)
        => Estilo == otra.Estilo && string.Equals(Volumen, otra.Volumen, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="raiz"/> es esta misma ruta o una carpeta que la contiene. Sin distinguir
    /// mayúsculas ni la forma de los acentos (NFC/NFD).
    /// </summary>
    public bool EmpiezaPor(RutaPartida raiz)
    {
        if (!MismoVolumen(raiz) || raiz.Tramos.Count > Tramos.Count) return false;
        for (var i = 0; i < raiz.Tramos.Count; i++)
            if (!MismoTramo(Tramos[i], raiz.Tramos[i])) return false;
        return true;
    }

    /// <summary>Dos nombres de carpeta o archivo iguales, sin distinguir mayúsculas ni NFC/NFD.</summary>
    public static bool MismoTramo(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
           || string.Equals(Nfc(a), Nfc(b), StringComparison.OrdinalIgnoreCase);

    internal static string Nfc(string s) => s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC);
}

/// <summary>
/// Las raíces confirmadas por el usuario: ruta antigua → ruta nueva. Se aplica a cualquier texto de
/// los archivos de datos que sea una ruta dentro de una de esas raíces; lo demás no se toca.
/// </summary>
public sealed class MapaRaices
{
    private readonly List<(RutaPartida Origen, RutaPartida Destino, string DestinoTexto)> _cambios = new();

    public MapaRaices(IEnumerable<CambioRaiz> cambios)
    {
        foreach (var c in cambios)
        {
            var o = RutaPartida.De(c.Origen?.Trim());
            var d = RutaPartida.De(c.Destino?.Trim());
            if (o == null || d == null) continue;   // destino vacío o que no es una ruta: no cambia
            // «/» a secas no puede ser una raíz: toda ruta de macOS empieza así.
            if (o.Estilo == EstiloRuta.Unix && o.Tramos.Count == 0) continue;
            if (d.EmpiezaPor(o) && o.EmpiezaPor(d)) continue;   // la misma carpeta: nada que hacer
            _cambios.Add((o, d, d.Texto()));
        }
        // La raíz más concreta manda: con «D:\Musica» y «D:\Musica\Acapellas» en el mapa, una
        // acapella va adonde diga la segunda.
        _cambios.Sort((a, b) => b.Origen.Tramos.Count.CompareTo(a.Origen.Tramos.Count));
    }

    /// <summary>
    /// Por qué no se pueden usar estas ubicaciones, o "" si se pueden. Un destino vacío es «sin
    /// cambios» y vale; uno escrito tiene que ser una ruta completa (con su unidad en Windows o empezar
    /// por «/» en macOS), porque una relativa no diría a qué disco se refiere.
    /// </summary>
    public static string Validar(IEnumerable<CambioRaiz> cambios)
    {
        foreach (var c in cambios)
        {
            var d = c.Destino?.Trim() ?? "";
            if (d.Length == 0) continue;
            if (d.Trim('/').Length == 0) return "«/» no puede ser la ubicación de la música: elige la carpeta donde está.";
            var r = RutaPartida.De(d);
            if (r == null)
                return $"«{d}» no es una ruta completa de carpeta. Escríbela entera (por ejemplo, D:\\Musica o /Volumes/USB/Musica) o elígela con «Elegir…».";
            if (r.Estilo == EstiloRuta.Unix && r.Tramos.Count == 0)
                return "«/» no puede ser la ubicación de la música: elige la carpeta donde está.";
        }
        return "";
    }

    /// <summary>No hay nada que reescribir.</summary>
    public bool Vacio => _cambios.Count == 0;

    public int Count => _cambios.Count;

    /// <summary>
    /// La ruta en su sitio nuevo, o null si no está dentro de ninguna raíz que cambie (o no es una
    /// ruta). El resto de la ruta se escribe con el separador del destino.
    ///
    /// Los acentos: con <paramref name="existe"/> se mira en disco qué forma tiene de verdad el
    /// archivo (tal cual, NFC o NFD), porque Windows distingue «á» compuesta de «a» + tilde y una ficha
    /// con la otra forma no casaría con su canción. Sin poder mirarlo (el disco aún no está), se deja
    /// como venía, salvo hacia Windows, donde se compone (NFC), que es lo que usan sus programas.
    /// </summary>
    public string? Traducir(string valor, Func<string, bool>? existe = null)
    {
        if (_cambios.Count == 0) return null;
        var r = RutaPartida.De(valor);
        if (r == null) return null;
        foreach (var (origen, destino, destinoTexto) in _cambios)
        {
            if (!r.EmpiezaPor(origen)) continue;
            var resto = r.Tramos.Skip(origen.Tramos.Count).ToList();
            var sep = destino.Estilo == EstiloRuta.Windows ? @"\" : "/";
            string Unir(IEnumerable<string> tramos)
                => resto.Count == 0 ? destinoTexto : destinoTexto.TrimEnd('\\', '/') + sep + string.Join(sep, tramos);

            var tal = Unir(resto);
            var nfc = Unir(resto.Select(RutaPartida.Nfc));
            var porDefecto = destino.Estilo == EstiloRuta.Windows ? nfc : tal;
            if (existe == null || resto.All(EsAscii)) return porDefecto;

            var nfd = Unir(resto.Select(t => t.Normalize(NormalizationForm.FormD)));
            foreach (var candidata in new[] { porDefecto, tal, nfc, nfd }.Distinct(StringComparer.Ordinal))
                if (existe(candidata)) return candidata;
            return porDefecto;
        }
        return null;
    }

    private static bool EsAscii(string s)
    {
        foreach (var ch in s) if (ch > 127) return false;
        return true;
    }
}
