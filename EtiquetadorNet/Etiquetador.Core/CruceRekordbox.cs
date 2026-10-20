namespace Etiquetador.Core;

/// <summary>Qué se sabe de una entrada de rekordbox cuyo archivo ya no está donde dice.</summary>
public enum ComoReparar
{
    /// <summary>No hay ningún archivo de la biblioteca que se pueda dar por el mismo.</summary>
    SinEncontrar,

    /// <summary>BeatTag la renombró o la movió, y lo tiene anotado en el historial de deshacer.</summary>
    RenombradaPorBeatTag,

    /// <summary>En la biblioteca hay un único archivo con el mismo nombre y exactamente el mismo tamaño.</summary>
    MismoNombreYTamano,

    /// <summary>En la biblioteca hay un único archivo con el mismo nombre y la misma duración (±1 s).</summary>
    MismoNombreYDuracion,

    /// <summary>El archivo que le corresponde ya está en rekordbox con su ruta nueva: repararla lo duplicaría.</summary>
    YaEnRekordbox,

    /// <summary>Hay varios archivos que encajan igual de bien: no se elige uno a ciegas.</summary>
    VariasCandidatas,

    /// <summary>Otra entrada rota apunta al mismo archivo: repararlas las dos lo duplicaría.</summary>
    MismoDestino,

    /// <summary>El disco o la unidad de red donde estaba no está conectado: no está perdida.</summary>
    DiscoNoConectado,
}

/// <summary>Una entrada rota de rekordbox y, si se ha encontrado, el archivo al que debería apuntar.</summary>
public sealed record PistaRota(PistaRekordbox Pista, ComoReparar Como, string NuevaRuta = "")
{
    /// <summary>Se puede reparar sin dudas (y entra en la colección reparada).</summary>
    public bool Reparable => Como is ComoReparar.RenombradaPorBeatTag or ComoReparar.MismoNombreYTamano or ComoReparar.MismoNombreYDuracion;

    /// <summary>
    /// Con <see cref="ComoReparar.VariasCandidatas"/>, los archivos de la biblioteca que encajan igual de
    /// bien. No se repara hacia ninguno, pero uno de ellos es esta entrada: ninguno se da por «no está
    /// en rekordbox» (ver <see cref="ResultadoCruce.ConRutaAntigua"/>).
    /// </summary>
    public IReadOnlyList<string> Candidatas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Se puede reparar y el archivo conserva su nombre: solo ha cambiado de carpeta. Para estas, la
    /// reubicación de archivos no encontrados del propio rekordbox, que busca por nombre, las arregla
    /// sin crear pistas nuevas y conserva las listas de reproducción y el historial.
    /// </summary>
    public bool MismoNombre => Reparable && NuevaRuta.Length > 0
                               && ComparadorRutas.Instancia.Equals(Path.GetFileName(Pista.Ruta), Path.GetFileName(NuevaRuta));

    /// <summary>Cómo se dice en la tabla.</summary>
    public string Texto => Como switch
    {
        ComoReparar.RenombradaPorBeatTag => "Renombrada o movida por BeatTag",
        ComoReparar.MismoNombreYTamano => "Mismo nombre y tamaño",
        ComoReparar.MismoNombreYDuracion => "Mismo nombre y duración",
        ComoReparar.YaEnRekordbox => "Ya está en rekordbox con la ruta nueva",
        ComoReparar.VariasCandidatas => "Varios archivos posibles",
        ComoReparar.MismoDestino => "Otra entrada apunta al mismo archivo",
        ComoReparar.DiscoNoConectado => "Disco no conectado",
        _ => "No encontrada",
    };
}

/// <summary>El resultado de cruzar la colección de rekordbox con la biblioteca.</summary>
public sealed class ResultadoCruce
{
    /// <summary>
    /// (a) Canciones de la biblioteca que no están en la colección de rekordbox. No incluye las de
    /// <see cref="ConRutaAntigua"/>: esas sí están, con otra ruta.
    /// </summary>
    public IReadOnlyList<Track> FaltanEnRekordbox { get; init; } = Array.Empty<Track>();

    /// <summary>
    /// Canciones de la biblioteca que rekordbox tiene con una ruta antigua: alguna entrada rota apunta
    /// a ellas (reparable, compartida con otra entrada o entre varias candidatas). Importarlas de nuevo
    /// las duplicaría sin sus cue points, su beatgrid ni su valoración, y en el cruce siguiente la
    /// entrada rota ya no se repararía (su archivo pasaría a estar en rekordbox): hay que reparar antes.
    /// </summary>
    public IReadOnlyList<Track> ConRutaAntigua { get; init; } = Array.Empty<Track>();

    /// <summary>
    /// De cada archivo de <see cref="ConRutaAntigua"/>, la entrada rota que le corresponde (si son
    /// varias, la más preparada). Con ella, una lista exportada a rekordbox apunta a la pista que ya
    /// tiene, con sus cue points, en lugar de añadir otra.
    /// </summary>
    public IReadOnlyDictionary<string, PistaRekordbox> EntradaAntigua { get; init; } = new Dictionary<string, PistaRekordbox>();

    /// <summary>De las reparables, las que conservan el nombre de archivo (ver <see cref="PistaRota.MismoNombre"/>).</summary>
    public int SoloMovidas => Rotas.Count(r => r.MismoNombre);

    /// <summary>(b) Entradas de rekordbox cuyo archivo no existe.</summary>
    public IReadOnlyList<PistaRota> Rotas { get; init; } = Array.Empty<PistaRota>();

    /// <summary>Entradas cuyo archivo existe, con su canción de la biblioteca si está en ella. Base de (c) y (d).</summary>
    public IReadOnlyList<PistaPresente> Presentes { get; init; } = Array.Empty<PistaPresente>();

    public int EnBiblioteca => Presentes.Count(p => p.Cancion != null);
    public int FueraDeBiblioteca => Presentes.Count(p => p.Cancion == null);
    public int Reparables => Rotas.Count(r => r.Reparable);
}

/// <summary>Una entrada de rekordbox cuyo archivo existe; <see cref="Cancion"/> es null si está fuera de la biblioteca.</summary>
public sealed record PistaPresente(PistaRekordbox Pista, Track? Cancion);

/// <summary>Lo que el cruce necesita saber del disco. Aparte para poder probarlo sin archivos.</summary>
public interface IArchivosCruce
{
    bool Existe(string ruta);

    /// <summary>La unidad, el recurso de red o el volumen de la ruta está conectado.</summary>
    bool DiscoDisponible(string ruta);

    /// <summary>Tamaño en bytes, o -1 si no se puede saber.</summary>
    long Tamano(string ruta);
}

/// <summary>
/// Cruza la colección de rekordbox con la biblioteca de BeatTag por ruta de archivo.
///
/// Las entradas rotas se intentan emparejar con un archivo de la biblioteca, en este orden:
///   1. El historial de renombrados de BeatTag (lo que ya usaba la reparación).
///   2. Mismo nombre de archivo y exactamente el mismo tamaño.
///   3. Mismo nombre de archivo y la misma duración, con un segundo de margen.
/// Solo se acepta una candidata ÚNICA, que exista y que rekordbox no tenga ya: si no, se dice qué
/// pasa y no se toca.
///
/// POR QUÉ LA DURACIÓN. MEDIDO en una colección real (19.690 pistas, exportada en agosto) frente a
/// la biblioteca de octubre (13.590 archivos): de las 9.213 pistas que seguían en su sitio, solo
/// 1.643 (17,8 %) tenían el mismo tamaño que en el disco, porque escribir etiquetas cambia el tamaño
/// del archivo (BeatTag, Mp3tag o el propio rekordbox); la duración coincidía en 9.177 (99,6 %). De
/// las 4.343 entradas rotas, el historial de BeatTag reparaba 610, el nombre y el tamaño 19 más, y el
/// nombre y la duración otras 2.735 (carpetas renombradas, como «Mashups Reggaeton1 A-C» →
/// «Mashups Reggaeton1 A-B»). Para medir el riesgo se buscó cada una de las 9.213 por nombre y
/// duración como si se hubiera perdido: en 9.155 la única candidata era la buena, y en 22 había una
/// copia del mismo nombre y la misma duración en otra carpeta (la misma canción repetida).
/// </summary>
public static class CruceRekordbox
{
    /// <summary>Segundos de diferencia que se admiten entre la duración de rekordbox y la de la biblioteca.</summary>
    public const int MargenDuracion = 1;

    public static ResultadoCruce Cruzar(ColeccionRekordbox coleccion, IReadOnlyList<Track> biblioteca,
                                        IReadOnlyDictionary<string, string> renombrados, IArchivosCruce disco,
                                        CancellationToken ct = default)
    {
        var porRuta = new Dictionary<string, Track>(ComparadorRutas.Instancia);
        foreach (var t in biblioteca) porRuta.TryAdd(t.FilePath, t);

        var enRekordbox = new HashSet<string>(coleccion.Pistas.Select(p => p.Ruta), ComparadorRutas.Instancia);

        var presentes = new List<PistaPresente>();
        var rotas = new List<PistaPresente>();
        for (var i = 0; i < coleccion.Pistas.Count; i++)
        {
            if (i % 1024 == 0) ct.ThrowIfCancellationRequested();
            var p = coleccion.Pistas[i];
            if (porRuta.TryGetValue(p.Ruta, out var t)) presentes.Add(new PistaPresente(p, t));
            else if (disco.Existe(p.Ruta)) presentes.Add(new PistaPresente(p, null));
            else rotas.Add(new PistaPresente(p, null));
        }

        // Para buscar por nombre: los archivos de la biblioteca agrupados por nombre de archivo.
        var porNombre = new Dictionary<string, List<Track>>(ComparadorRutas.Instancia);
        foreach (var t in porRuta.Values)
        {
            var n = Path.GetFileName(t.FilePath);
            if (!porNombre.TryGetValue(n, out var l)) porNombre[n] = l = new List<Track>(1);
            l.Add(t);
        }
        var mapa = new Dictionary<string, string>(ComparadorRutas.Instancia);
        foreach (var kv in renombrados) mapa[kv.Key] = kv.Value;

        bool Existe(string ruta) => porRuta.ContainsKey(ruta) || disco.Existe(ruta);

        var clasificadas = new List<PistaRota>(rotas.Count);
        for (var i = 0; i < rotas.Count; i++)
        {
            if (i % 256 == 0) ct.ThrowIfCancellationRequested();
            var r = Clasificar(rotas[i].Pista, mapa, porNombre, disco, Existe);
            // Repararla hacia un archivo que rekordbox ya tiene crearía dos entradas del mismo archivo.
            if (r.Reparable && enRekordbox.Contains(r.NuevaRuta)) r = r with { Como = ComoReparar.YaEnRekordbox };
            clasificadas.Add(r);
        }

        // Dos entradas rotas hacia el mismo archivo: cualquiera de las dos que se elija dejaría la otra
        // duplicada o perdida, así que no se elige.
        var repetidos = clasificadas.Where(r => r.Reparable)
                                    .GroupBy(r => r.NuevaRuta, ComparadorRutas.Instancia)
                                    .Where(g => g.Count() > 1)
                                    .Select(g => g.Key)
                                    .ToHashSet(ComparadorRutas.Instancia);
        for (var i = 0; i < clasificadas.Count; i++)
            if (clasificadas[i].Reparable && repetidos.Contains(clasificadas[i].NuevaRuta))
                clasificadas[i] = clasificadas[i] with { Como = ComoReparar.MismoDestino };

        // (a) Lo que no está en rekordbox, sin los archivos a los que apunta una entrada rota: esos SÍ
        // están, con la ruta antigua. MEDIDO con la colección real: de las 4.385 canciones que salían
        // como «no están en rekordbox», 3.378 eran el archivo de una entrada rota; quien guardaba la
        // tabla como M3U8 y la importaba antes de reparar creaba 3.378 pistas duplicadas sin cue
        // points, y en el cruce siguiente esas entradas rotas ya no se reparaban (su archivo pasaba a
        // estar en rekordbox).
        var antiguas = EntradasAntiguas(clasificadas, enRekordbox);
        var faltan = new List<Track>();
        var conRutaAntigua = new List<Track>();
        foreach (var t in biblioteca)
        {
            if (enRekordbox.Contains(t.FilePath)) continue;
            (antiguas.ContainsKey(t.FilePath) ? conRutaAntigua : faltan).Add(t);
        }

        return new ResultadoCruce
        {
            FaltanEnRekordbox = faltan, ConRutaAntigua = conRutaAntigua, EntradaAntigua = antiguas,
            Rotas = clasificadas, Presentes = presentes,
        };
    }

    /// <summary>
    /// De cada archivo al que apunta una entrada rota, esa entrada. Cuentan las reparables, las que
    /// comparten archivo con otra (<see cref="ComoReparar.MismoDestino"/>) y las que tienen varias
    /// candidatas, que son copias de la misma canción (mismo nombre y mismo tamaño o duración): sea
    /// cual sea la buena, la canción está en rekordbox. Si varias entradas apuntan al mismo archivo, se
    /// queda la más preparada (más marcadores, con beatgrid, más reproducciones), y a igualdad la
    /// primera de la colección. Los destinos que rekordbox ya tiene con su ruta nueva no cuentan.
    /// </summary>
    private static Dictionary<string, PistaRekordbox> EntradasAntiguas(IEnumerable<PistaRota> rotas, HashSet<string> enRekordbox)
    {
        var d = new Dictionary<string, PistaRekordbox>(ComparadorRutas.Instancia);
        foreach (var r in rotas)
        {
            IEnumerable<string> destinos = r.Como switch
            {
                ComoReparar.VariasCandidatas => r.Candidatas,
                ComoReparar.RenombradaPorBeatTag or ComoReparar.MismoNombreYTamano or ComoReparar.MismoNombreYDuracion
                    or ComoReparar.MismoDestino => r.NuevaRuta.Length > 0 ? new[] { r.NuevaRuta } : Array.Empty<string>(),
                _ => Array.Empty<string>(),
            };
            foreach (var destino in destinos)
            {
                if (enRekordbox.Contains(destino)) continue;
                if (!d.TryGetValue(destino, out var ya) || Preparacion(r.Pista).CompareTo(Preparacion(ya)) > 0) d[destino] = r.Pista;
            }
        }
        return d;
    }

    private static (int Marcadores, bool Beatgrid, int Reproducciones) Preparacion(PistaRekordbox p)
        => (p.HotCues + p.CuesMemoria, p.Beatgrid, p.Reproducciones);

    private static PistaRota Clasificar(PistaRekordbox p, Dictionary<string, string> renombrados,
                                        Dictionary<string, List<Track>> porNombre, IArchivosCruce disco, Func<string, bool> existe)
    {
        if (!disco.DiscoDisponible(p.Ruta)) return new PistaRota(p, ComoReparar.DiscoNoConectado);

        if (renombrados.TryGetValue(p.Ruta, out var nueva) && existe(nueva))
            return new PistaRota(p, ComoReparar.RenombradaPorBeatTag, nueva);

        if (!porNombre.TryGetValue(Path.GetFileName(p.Ruta), out var candidatas)) return new PistaRota(p, ComoReparar.SinEncontrar);

        if (p.Tamano > 0)
        {
            var mismoTamano = candidatas.Where(c => disco.Tamano(c.FilePath) == p.Tamano).ToList();
            if (mismoTamano.Count == 1) return new PistaRota(p, ComoReparar.MismoNombreYTamano, mismoTamano[0].FilePath);
            if (mismoTamano.Count > 1) return Varias(p, mismoTamano);
        }

        if (p.Duracion > 0)
        {
            var mismaDuracion = candidatas.Where(c => c.DurationSeconds > 0 && Math.Abs(c.DurationSeconds - p.Duracion) <= MargenDuracion).ToList();
            if (mismaDuracion.Count == 1) return new PistaRota(p, ComoReparar.MismoNombreYDuracion, mismaDuracion[0].FilePath);
            if (mismaDuracion.Count > 1) return Varias(p, mismaDuracion);
        }
        return new PistaRota(p, ComoReparar.SinEncontrar);
    }

    private static PistaRota Varias(PistaRekordbox p, List<Track> candidatas)
        => new(p, ComoReparar.VariasCandidatas) { Candidatas = candidatas.Select(c => c.FilePath).ToList() };

    /// <summary>De ruta rota a ruta nueva, para <see cref="RekordboxRelocator.Repair"/>.</summary>
    public static Dictionary<string, string> MapaReparacion(IEnumerable<PistaRota> rotas)
    {
        var mapa = new Dictionary<string, string>(ComparadorRutas.Instancia);
        foreach (var r in rotas.Where(r => r.Reparable)) mapa[r.Pista.Ruta] = r.NuevaRuta;
        return mapa;
    }

    /// <summary>
    /// (c) Sin preparar: sin beatgrid o sin hot cues. Con <paramref name="cueInicialCuenta"/> a false,
    /// una pista cuyo único marcador es un hot cue en el primer tiempo cuenta como sin hot cues (ver
    /// <see cref="PistaRekordbox.SoloCueInicial"/>).
    /// </summary>
    public static bool SinPreparar(PistaRekordbox p, bool sinHotCues, bool sinBeatgrid, bool cueInicialCuenta)
        => (sinHotCues && p.SinHotCues(contarCueInicial: !cueInicialCuenta)) || (sinBeatgrid && !p.Beatgrid);

    /// <summary>
    /// (d) Nunca pinchada en rekordbox y en la colección desde hace más de <paramref name="meses"/>
    /// meses. Sin fecha de alta no se puede afirmar, así que no entra.
    /// </summary>
    public static bool NuncaPinchada(PistaRekordbox p, int meses, DateTime hoy)
        => p.Reproducciones == 0 && p.Alta is { } alta && alta < hoy.Date.AddMonths(-Math.Max(0, meses));
}

/// <summary>
/// El disco de verdad, con lo ya mirado guardado: cada carpeta se lista una sola vez en lugar de
/// preguntar archivo por archivo. MEDIDO con la colección real (10.477 entradas fuera de la
/// biblioteca, en 58 carpetas, disco de giro): 47 ms por carpetas frente a 72 ms archivo a archivo con
/// el disco ya leído, y 8,7 s archivo a archivo la primera vez, con el disco en frío. Se usa desde un
/// solo hilo.
/// </summary>
public sealed class ArchivosEnDisco : IArchivosCruce
{
    private readonly Dictionary<string, HashSet<string>?> _carpetas = new(ComparadorRutas.Instancia);
    private readonly Dictionary<string, bool> _discos = new(ComparadorRutas.Instancia);
    private readonly HashSet<string> _sinListar = new(ComparadorRutas.Instancia);

    public bool Existe(string ruta)
    {
        var dir = Path.GetDirectoryName(ruta);
        if (string.IsNullOrEmpty(dir)) return File.Exists(ruta);
        if (_sinListar.Contains(dir)) return File.Exists(ruta);
        if (!_carpetas.TryGetValue(dir, out var nombres))
        {
            try
            {
                nombres = Directory.Exists(dir)
                    ? new HashSet<string>(Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)), ComparadorRutas.Instancia)
                    : null;
                _carpetas[dir] = nombres;
            }
            catch
            {
                // Sin permiso para listarla (o cualquier otro problema): se pregunta archivo a archivo.
                _sinListar.Add(dir);
                return File.Exists(ruta);
            }
        }
        return nombres != null && nombres.Contains(Path.GetFileName(ruta));
    }

    public bool DiscoDisponible(string ruta)
    {
        var raiz = Raiz(ruta);
        if (raiz.Length == 0) return true;
        if (!_discos.TryGetValue(raiz, out var ok))
        {
            try { ok = Directory.Exists(raiz); } catch { ok = false; }
            _discos[raiz] = ok;
        }
        return ok;
    }

    public long Tamano(string ruta)
    {
        try { return new FileInfo(ruta).Length; }
        catch { return -1; }
    }

    /// <summary>
    /// Lo que tiene que estar conectado para que la ruta exista: la unidad («E:\»), el recurso de red
    /// («\\NAS\Musica») o el volumen de macOS («/Volumes/USB DJ»). "" si es el disco del sistema.
    /// </summary>
    public static string Raiz(string ruta)
    {
        var r = ruta ?? "";
        if (RekordboxRelocator.EsRutaDeUnidad(r)) return r[..2] + Path.DirectorySeparatorChar;
        if (r.StartsWith(@"\\", StringComparison.Ordinal) || r.StartsWith("//", StringComparison.Ordinal))
        {
            var partes = r[2..].Split('\\', '/');
            return partes.Length >= 2 ? r[..2] + partes[0] + r[1] + partes[1] : "";
        }
        if (r.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var fin = r.IndexOf('/', "/Volumes/".Length);
            return fin > 0 ? r[..fin] : r;
        }
        return "";
    }
}
