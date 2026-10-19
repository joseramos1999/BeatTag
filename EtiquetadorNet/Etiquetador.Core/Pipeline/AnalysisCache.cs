using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Caché persistente del RESULTADO del análisis (ProcessResult) por archivo, validada por
/// fecha+tamaño y por la firma de opciones. Evita reprocesar (red incluida) lo que no ha cambiado;
/// solo se rehace lo nuevo/modificado o cuando el usuario fuerza el reanálisis.
/// </summary>
public sealed class AnalysisCache
{
    private sealed class Entry
    {
        public long M { get; set; }
        public long S { get; set; }
        public string Sig { get; set; } = "";
        public ProcessResult R { get; set; } = new();
    }

    private readonly string _file;
    private Dictionary<string, Entry> _map = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public AnalysisCache(string file) { _file = file; Load(); }

    /// <summary>Resultado cacheado si el archivo no ha cambiado y la firma de opciones coincide; si no, null.</summary>
    public ProcessResult? Get(string path, string sig)
    {
        if (!Stat(path, out var m, out var s)) return null;
        if (_map.TryGetValue(path, out var e) && e.M == m && e.S == s && e.Sig == sig)
        {
            // Lo que guardó una versión anterior de las reglas sale ya puesto al día (sin red): sin
            // esto se aplicaría tal cual, sin las comprobaciones de ahora. Ver ResultadoAnterior.
            if (ResultadoAnterior.Actualizar(e.R)) _dirty = true;
            return e.R;
        }
        return null;
    }

    public void Set(string path, string sig, ProcessResult r)
    {
        // Un «no encontrada» con fallos de red no es un dato: no se pudo preguntar. Guardado, se
        // servía para siempre (la caché no caduca) y solo «Reanalizar todo» lo recuperaba. Visto en
        // los registros reales: 98 «musicbrainz.org HTTP 503» y 23 «se abandona tras 2 intentos»,
        // todas en canciones que acabaron guardadas como no encontradas. Sin guardarlo, el próximo
        // análisis vuelve a preguntar; lo que había antes (si había) se queda como estaba.
        if (r.IncompletoPorLaRed) return;
        if (!Stat(path, out var m, out var s)) return;
        _map[path] = new Entry { M = m, S = s, Sig = sig, R = r };
        _dirty = true;
    }

    /// <summary>Olvida el resultado de un archivo concreto (al descartarlo a mano).</summary>
    public void Remove(string path)
    {
        if (_map.Remove(path)) _dirty = true;
    }

    /// <summary>
    /// Cambia una firma antigua por la equivalente actual en todo lo guardado, y devuelve cuántas
    /// entradas se han actualizado.
    ///
    /// Sirve para cuando cambia el FORMATO de la firma sin cambiar lo que de verdad afecta al
    /// resultado. Sin esto, un retoque de formato tira la caché entera y obliga a reanalizar toda
    /// la biblioteca para acabar exactamente en el mismo sitio.
    /// </summary>
    public int MigrarFirma(string vieja, string nueva)
    {
        if (vieja == nueva) return 0;
        var n = 0;
        foreach (var e in _map.Values.Where(e => e.Sig == vieja)) { e.Sig = nueva; n++; }
        if (n > 0) _dirty = true;
        return n;
    }

    /// <summary>
    /// Olvida los resultados de los archivos que ya no están DENTRO de las carpetas analizadas.
    /// Lo de fuera se conserva, igual que hace <see cref="ScanCache.Prune"/>.
    ///
    /// Visto en una biblioteca real: con 42 de las 43 carpetas desmarcadas, analizar las 59
    /// canciones de Descargas\MUSICA borró los resultados de las otras ~13.500, y
    /// analysis-cache.json pasó de unos 6 MB a 57.730 bytes. La siguiente pasada completa tuvo que
    /// volver a consultarlo todo en red e IA (las completas llegaron a durar unas 3 h), y Enriquecer
    /// y No encontradas perdieron las propuestas pendientes, que solo se cargan de aquí.
    ///
    /// <paramref name="raices"/> deben ser las carpetas que la pasada ha recorrido de verdad. Una
    /// carpeta que no ha aportado ninguna canción -un disco desconectado- no debe ir aquí: todo lo
    /// suyo parecería borrado. Sin raíces no se poda nada.
    /// </summary>
    public void Prune(ISet<string> livePaths, IReadOnlyCollection<string> raices)
    {
        if (raices.Count == 0) return;
        var stale = _map.Keys.Where(k => !livePaths.Contains(k) && raices.Any(r => Rutas.EstaDentroDe(k, r))).ToList();
        foreach (var k in stale) { _map.Remove(k); _dirty = true; }
    }

    /// <summary>Entradas guardadas (para pruebas y diagnóstico).</summary>
    public int Count => _map.Count;

    /// <summary>"" si se cargó con normalidad; si no, qué pasó con el archivo (apartado por ilegible…).</summary>
    public string AvisoCarga { get; private set; } = "";

    // El archivo existe pero no se pudo leer ni apartar: no se guarda encima.
    private bool _noSobrescribir;

    public void Clear()
    {
        _map.Clear();
        _dirty = false;
        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
    }

    /// <summary>
    /// Guarda si hubo cambios, de forma atómica (temporal + sustitución): un corte a media escritura
    /// de un archivo de varios MB dejaba la caché entera inservible. Devuelve "" si fue bien o no
    /// había nada que guardar, o el motivo del fallo.
    /// </summary>
    public string Save()
    {
        if (!_dirty) return "";
        if (_noSobrescribir && !Releer()) return AvisoCarga;
        var error = ArchivoDatos.EscribirJson(_file, _map);
        if (error.Length == 0) _dirty = false;
        return error;
    }

    /// <summary>
    /// Si al arrancar el archivo no se pudo leer (bloqueado un momento por OneDrive o el antivirus),
    /// se vuelve a intentar al guardar y se junta lo guardado con lo de esta sesión, que manda.
    /// Antes no se reintentaba nunca y todo lo analizado en la sesión se perdía al cerrar (T10).
    /// </summary>
    private bool Releer()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, Entry>>(_file);
        AvisoCarga = leido.Aviso;
        if (leido.NoSobrescribir) return false;
        if (leido.Datos != null)
            foreach (var (ruta, entrada) in leido.Datos) _map.TryAdd(ruta, entrada);
        _noSobrescribir = false;
        return true;
    }

    private void Load()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, Entry>>(_file);
        AvisoCarga = leido.Aviso;
        _noSobrescribir = leido.NoSobrescribir;
        if (leido.Datos != null) _map = new Dictionary<string, Entry>(leido.Datos, System.StringComparer.OrdinalIgnoreCase);
    }

    private static bool Stat(string path, out long m, out long s)
    {
        m = 0; s = 0;
        try { var fi = new FileInfo(path); if (!fi.Exists) return false; m = fi.LastWriteTimeUtc.Ticks; s = fi.Length; return true; }
        catch { return false; }
    }
}
