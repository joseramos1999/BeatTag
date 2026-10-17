using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Canciones que el usuario ha descartado a mano ("Quitar de la lista"). Se guardan por ruta y se
/// excluyen del análisis y de la carga de la caché, para que no vuelvan a aparecer en Enriquecer.
/// </summary>
public sealed class IgnoreList
{
    private readonly string _file;
    private HashSet<string> _set = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    // El archivo existe pero no se pudo leer ni apartar: guardar encima lo sustituiría por lo poco
    // que haya en memoria. Mientras dure, Save no escribe.
    private bool _noSobrescribir;

    public IgnoreList(string file) { _file = file; Load(); }

    public int Count => _set.Count;

    /// <summary>
    /// "" si se cargó con normalidad; si no, qué pasó con el archivo (apartado por ilegible, o
    /// bloqueado). Son decisiones hechas a mano: quien lo use debe enseñarlo, no callarlo.
    /// </summary>
    public string AvisoCarga { get; private set; } = "";

    /// <summary>Las rutas descartadas, ordenadas por nombre de archivo. Para poder elegir cual recuperar.</summary>
    public IReadOnlyList<string> Items => _set.OrderBy(System.IO.Path.GetFileName, System.StringComparer.OrdinalIgnoreCase).ToList();

    public bool Contains(string path) => _set.Contains(path);

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_set.Add(path)) _dirty = true;
    }

    public void Remove(string path)
    {
        if (_set.Remove(path)) _dirty = true;
    }

    /// <summary>Olvida las descartadas cuyos archivos ya no existen en la biblioteca.</summary>
    public void Prune(ISet<string> livePaths)
    {
        foreach (var k in _set.Where(k => !livePaths.Contains(k)).ToList()) { _set.Remove(k); _dirty = true; }
    }

    public void Clear()
    {
        _set.Clear();
        _dirty = false;
        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
    }

    /// <summary>
    /// Guarda si hubo cambios, de forma atómica (temporal + sustitución). Devuelve "" si fue bien o
    /// no había nada que guardar, o el motivo del fallo; en ese caso los cambios siguen pendientes
    /// y el siguiente Save lo vuelve a intentar.
    /// </summary>
    public string Save()
    {
        if (!_dirty) return "";
        if (_noSobrescribir) return AvisoCarga;
        var error = ArchivoDatos.EscribirJson(_file, _set);
        if (error.Length == 0) _dirty = false;
        return error;
    }

    private void Load()
    {
        var leido = ArchivoDatos.LeerJson<HashSet<string>>(_file);
        AvisoCarga = leido.Aviso;
        _noSobrescribir = leido.NoSobrescribir;
        if (leido.Datos != null) _set = new HashSet<string>(leido.Datos, System.StringComparer.OrdinalIgnoreCase);
    }
}
