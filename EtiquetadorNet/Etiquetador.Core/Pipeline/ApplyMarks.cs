using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

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
    }

    public void Clear()
    {
        if (_map.Count == 0) return;
        _map.Clear();
        _dirty = true;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var json = File.ReadAllText(_file, Encoding.UTF8);
            var d = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (d != null) _map = new Dictionary<string, bool>(d, System.StringComparer.OrdinalIgnoreCase);
        }
        catch { /* un archivo corrupto no puede impedir arrancar: se empieza sin marcas */ }
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_file, JsonSerializer.Serialize(_map), Encoding.UTF8);
            _dirty = false;
        }
        catch { }
    }
}
