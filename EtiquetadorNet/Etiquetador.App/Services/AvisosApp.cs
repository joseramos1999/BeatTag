using System;
using System.Collections.Generic;
using System.Linq;

namespace Etiquetador.App.Services;

/// <summary>
/// Avisos que la ventana principal enseña de forma persistente, en una banda arriba, hasta que el
/// usuario los da por vistos (hallazgos I6, T9, T10 y M7).
///
/// Antes solo iban al registro. Visto en el código: con fichas-dj.json bloqueado por otra ventana de
/// BeatTag, el almacén quedaba en solo lectura y el usuario seguía editando fichas que no se iban a
/// guardar, sin nada en pantalla que lo dijera; lo mismo con una configuración apartada por ilegible,
/// que dejaba la aplicación con los valores de fábrica, o con una carpeta en un disco desconectado.
///
/// Cada aviso va con una clave: el mismo problema no se repite, y cuando se arregla (la carpeta
/// vuelve a estar disponible, un guardado que falló sale bien) se quita solo. Se puede usar desde
/// cualquier hilo; quien lo enseña se ocupa de pasar el aviso al de la interfaz.
/// </summary>
public sealed class AvisosApp
{
    private readonly object _cerrojo = new();
    private readonly List<(string Clave, string Texto)> _avisos = new();

    /// <summary>Ha cambiado la lista. Puede llegar desde cualquier hilo.</summary>
    public event Action? Cambiado;

    /// <summary>Los avisos en el orden en que llegaron.</summary>
    public IReadOnlyList<string> Textos
    {
        get { lock (_cerrojo) return _avisos.Select(a => a.Texto).ToList(); }
    }

    public int Count { get { lock (_cerrojo) return _avisos.Count; } }

    /// <summary>Pone el aviso de esa clave, o lo sustituye si ya estaba. Un texto vacío lo quita.</summary>
    public void Poner(string clave, string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) { Quitar(clave); return; }
        lock (_cerrojo)
        {
            var i = _avisos.FindIndex(a => a.Clave == clave);
            if (i >= 0 && _avisos[i].Texto == texto) return;
            if (i >= 0) _avisos[i] = (clave, texto);
            else _avisos.Add((clave, texto));
        }
        Cambiado?.Invoke();
    }

    public void Quitar(string clave)
    {
        lock (_cerrojo)
            if (_avisos.RemoveAll(a => a.Clave == clave) == 0) return;
        Cambiado?.Invoke();
    }

    /// <summary>El usuario los ha leído. Si el problema sigue, volverá a aparecer cuando se repita.</summary>
    public void QuitarTodos()
    {
        lock (_cerrojo)
        {
            if (_avisos.Count == 0) return;
            _avisos.Clear();
        }
        Cambiado?.Invoke();
    }
}
