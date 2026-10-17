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

    /// <summary>
    /// El archivo existe pero no se pudo leer ni apartar al abrirlo: lo que hay en memoria no es lo
    /// que hay en disco, y <see cref="Guardar"/> no lo escribe para no pisarlo. Ver <see cref="ArchivoJson.Leer{T}(string, Func{T}, out string, out bool)"/>.
    /// </summary>
    public bool SoloLectura { get; }

    public AlmacenFichas(string archivo)
    {
        _archivo = archivo;
        var leidas = ArchivoJson.Leer(archivo, () => new Dictionary<string, FichaDj>(), out var aviso, out var soloLectura);
        _fichas = new Dictionary<string, FichaDj>(leidas, StringComparer.OrdinalIgnoreCase);
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
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
    /// La ficha acompaña a un archivo que la aplicación acaba de mover o renombrar.
    ///
    /// Se llama DESPUÉS de mover el archivo, y los dos que lo hacen (pasar de la bandeja a la
    /// biblioteca y renombrar con IA) nunca sobrescriben: si el movimiento salió bien, en el destino
    /// no había ningún archivo. Una ficha que hubiera en esa ruta es, por tanto, huérfana: de un
    /// archivo que se envió a la papelera desde Duplicados o desde el Asistente, que no tocan las
    /// fichas. Antes se conservaba esa huérfana y se perdía la que se movía: mandar a la papelera
    /// «Artista - Tema.mp3» a 128 kbps (E7, Pico) y traer de la bandeja la versión buena con el mismo
    /// nombre y su ficha nueva (E9, Pico, «boda») dejaba el archivo con la ficha vieja y la nueva
    /// perdida sin aviso. La ficha que se mueve es la del archivo que ahora está ahí: es la que manda.
    ///
    /// Si el archivo movido no tenía ficha, la del destino se queda donde está: no hay nada que la
    /// sustituya.
    /// </summary>
    public void Mover(string de, string a)
    {
        if (string.IsNullOrWhiteSpace(de) || string.IsNullOrWhiteSpace(a)) return;
        if (string.Equals(de, a, StringComparison.OrdinalIgnoreCase)) return;
        lock (_cerrojo)
        {
            if (!_fichas.Remove(de, out var f)) return;
            _fichas[a] = f;
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

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.</summary>
    public string Guardar()
    {
        if (SoloLectura) return ArchivoJson.AvisoSoloLectura(_archivo);
        Dictionary<string, FichaDj> copia;
        lock (_cerrojo) copia = new Dictionary<string, FichaDj>(_fichas, StringComparer.OrdinalIgnoreCase);
        return ArchivoJson.Escribir(_archivo, copia);
    }
}
