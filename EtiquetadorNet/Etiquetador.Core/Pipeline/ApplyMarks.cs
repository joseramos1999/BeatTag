using System.Collections.Generic;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Recuerda qué casillas "Aplicar" ha tocado el usuario en Enriquecer.
///
/// Revisar cientos de propuestas es trabajo de un rato largo, y hasta ahora cerrar la aplicación a
/// media revisión lo tiraba entero: al volver, todas las casillas aparecían otra vez como las dejó
/// el análisis. Aquí se guarda SOLO lo que el usuario ha cambiado a mano, no el estado de todas las
/// filas: si mañana una propuesta cambia de confianza, su valor por defecto vuelve a decidirlo el
/// análisis, que es lo correcto, y solo manda la decisión explícita de la persona.
/// </summary>
public sealed class ApplyMarks
{
    private readonly string _file;
    private Dictionary<string, bool> _map = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public ApplyMarks(string file) { _file = file; Load(); }

    public int Count => _map.Count;

    /// <summary>Lo que el usuario decidió para este archivo, o null si nunca tocó su casilla.</summary>
    public bool? Get(string path)
        => _map.TryGetValue(path, out var v) ? v : null;

    public void Set(string path, bool apply)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_map.TryGetValue(path, out var v) && v == apply) return;
        _map[path] = apply;
        _dirty = true;
    }

    /// <summary>Olvida la decisión de un archivo (al aplicarlo o descartarlo ya no hace falta).</summary>
    public void Forget(string path)
    {
        if (_map.Remove(path)) _dirty = true;
        if (_noSobrescribir) _olvidadas.Add(path);
    }

    public void Clear()
    {
        if (_noSobrescribir) { _vaciadas = true; _dirty = true; }
        if (_map.Count == 0) return;
        _map.Clear();
        _dirty = true;
    }

    /// <summary>
    /// "" si se cargó con normalidad; si no, qué pasó con el archivo. Un archivo dañado no impide
    /// arrancar -se empieza sin marcas-, pero ya no se vacía en silencio: se aparta como
    /// «.ilegible_fecha» y aquí queda dicho.
    /// </summary>
    public string AvisoCarga { get; private set; } = "";

    // El archivo existe pero no se pudo leer ni apartar: guardar encima lo sustituiría por lo poco
    // que haya en memoria. Mientras dure, Save no escribe.
    private bool _noSobrescribir;

    public void Load()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, bool>>(_file);
        AvisoCarga = leido.Aviso;
        _noSobrescribir = leido.NoSobrescribir;
        if (leido.Datos != null) _map = new Dictionary<string, bool>(leido.Datos, System.StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Guarda si hubo cambios, de forma atómica (temporal + sustitución): marcas-aplicar.json pesa
    /// unos 84 KB y un corte a media escritura lo dejaba truncado. Devuelve "" si fue bien o no
    /// había nada que guardar, o el motivo del fallo (los cambios siguen pendientes).
    /// </summary>
    public string Save()
    {
        if (!_dirty) return "";
        if (_noSobrescribir && !Releer()) return AvisoCarga;
        var error = ArchivoDatos.EscribirJson(_file, _map);
        if (error.Length == 0) _dirty = false;
        return error;
    }

    // Lo que se olvidó (o si se vació todo) mientras el archivo no se podía leer: al juntar con lo
    // guardado no puede volver.
    private readonly HashSet<string> _olvidadas = new(System.StringComparer.OrdinalIgnoreCase);
    private bool _vaciadas;

    /// <summary>
    /// Vuelve a intentar leer el archivo que al arrancar no se pudo leer y, si ya se puede, junta lo
    /// guardado con lo de esta sesión (manda lo de esta sesión). Devuelve true si ya se puede guardar.
    /// Visto en revisión (T10): sin esto, un marcas-aplicar.json bloqueado un momento al arrancar
    /// hacía perder todas las casillas de la sesión al cerrar.
    /// </summary>
    private bool Releer()
    {
        var leido = ArchivoDatos.LeerJson<Dictionary<string, bool>>(_file);
        AvisoCarga = leido.Aviso;
        if (leido.NoSobrescribir) return false;
        if (leido.Datos != null && !_vaciadas)
            foreach (var (ruta, aplicar) in leido.Datos)
                if (!_olvidadas.Contains(ruta)) _map.TryAdd(ruta, aplicar);
        _noSobrescribir = false;
        _olvidadas.Clear();
        _vaciadas = false;
        return true;
    }
}
