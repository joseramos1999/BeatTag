namespace Etiquetador.Core.Dj;

/// <summary>
/// Las fichas de DJ de toda la biblioteca, por ruta de archivo.
///
/// La ruta es la clave porque es lo único que identifica un archivo sin leerlo, pero tiene un
/// punto débil: BeatTag renombra y mueve archivos. Si la clave no siguiera al archivo, cada
/// renombrado dejaría una ficha huérfana y el trabajo hecho a mano desaparecería de la vista sin
/// avisar. Para eso está <see cref="Reubicar"/>, que usa lo que ya anotan los manifiestos de
/// deshacer, y <see cref="Mover"/>, para los movimientos que hace la propia aplicación.
/// </summary>
public sealed class AlmacenFichas
{
    private readonly string _archivo;
    private readonly object _cerrojo = new();
    private Dictionary<string, FichaDj> _fichas;

    /// <summary>Si al abrir no se pudo leer el archivo, el motivo. Vacío si fue bien.</summary>
    public string AvisoCarga { get; }

    public AlmacenFichas(string archivo)
    {
        _archivo = archivo;
        var leidas = ArchivoJson.Leer(archivo, () => new Dictionary<string, FichaDj>(), out var aviso);
        _fichas = new Dictionary<string, FichaDj>(leidas, StringComparer.OrdinalIgnoreCase);
        AvisoCarga = aviso;
    }

    public int Count { get { lock (_cerrojo) return _fichas.Count; } }

    public IReadOnlyList<string> Rutas { get { lock (_cerrojo) return _fichas.Keys.ToList(); } }

    /// <summary>La ficha de un archivo, o null si no tiene. Devuelve una COPIA: modificarla no cambia nada.</summary>
    public FichaDj? Obtener(string ruta)
    {
        lock (_cerrojo) return _fichas.TryGetValue(ruta, out var f) ? f.Copia() : null;
    }

    /// <summary>Guarda la ficha de un archivo. Una ficha vacía se borra en vez de guardarse.</summary>
    public void Poner(string ruta, FichaDj ficha)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return;
        lock (_cerrojo)
        {
            if (ficha.EstaVacia) _fichas.Remove(ruta);
            else _fichas[ruta] = ficha.Copia();
        }
    }

    public void Quitar(string ruta)
    {
        lock (_cerrojo) _fichas.Remove(ruta);
    }

    /// <summary>
    /// La ficha acompaña a un archivo que la aplicación acaba de mover o renombrar. Si en el destino
    /// ya había una ficha, se conserva la del destino: es la que corresponde al archivo que ya estaba
    /// allí, y el traslado no debe pisarla.
    /// </summary>
    public void Mover(string de, string a)
    {
        if (string.IsNullOrWhiteSpace(de) || string.IsNullOrWhiteSpace(a)) return;
        if (string.Equals(de, a, StringComparison.OrdinalIgnoreCase)) return;
        lock (_cerrojo)
        {
            if (!_fichas.Remove(de, out var f)) return;
            _fichas.TryAdd(a, f);
        }
    }

    /// <summary>
    /// Devuelve a su archivo las fichas cuya ruta ya no existe, siguiendo los renombrados anotados
    /// (de ruta antigua a ruta actual). Devuelve cuántas se han recolocado.
    ///
    /// Se mira también el sentido contrario, y no por capricho: si un renombrado se deshizo, el
    /// archivo volvió a su nombre original mientras la ficha ya se había ido al nuevo. Sin eso,
    /// deshacer dejaría la ficha perdida.
    ///
    /// Una ficha cuyo archivo no aparece NO se borra: puede estar en un disco desconectado, y
    /// borrarla sería perder el trabajo por haber arrancado sin el disco puesto.
    /// </summary>
    public int Reubicar(IReadOnlyDictionary<string, string> renombres, Func<string, bool> existe)
    {
        var inverso = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (antes, despues) in renombres) inverso.TryAdd(despues, antes);

        var movidas = 0;
        lock (_cerrojo)
        {
            foreach (var ruta in _fichas.Keys.ToList())
            {
                if (existe(ruta)) continue;

                string? destino = null;
                if (renombres.TryGetValue(ruta, out var adelante) && existe(adelante)) destino = adelante;
                else if (inverso.TryGetValue(ruta, out var atras) && existe(atras)) destino = atras;
                if (destino == null || _fichas.ContainsKey(destino)) continue;

                _fichas[destino] = _fichas[ruta];
                _fichas.Remove(ruta);
                movidas++;
            }
        }
        return movidas;
    }

    /// <summary>Hay alguna ficha cuyo archivo no está donde dice. Barato: solo pregunta, no lee manifiestos.</summary>
    public bool HayHuerfanas(Func<string, bool> existe)
    {
        lock (_cerrojo) return _fichas.Keys.Any(k => !existe(k));
    }

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo.</summary>
    public string Guardar()
    {
        Dictionary<string, FichaDj> copia;
        lock (_cerrojo) copia = new Dictionary<string, FichaDj>(_fichas, StringComparer.OrdinalIgnoreCase);
        return ArchivoJson.Escribir(_archivo, copia);
    }
}
