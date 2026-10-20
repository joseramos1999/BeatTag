using System.Text.Json.Serialization;
using Etiquetador.Core.Dj;

namespace Etiquetador.Core.Providers;

/// <summary>Cómo se llegó al artista de Deezer de un artista de la biblioteca (página Novedades).</summary>
public enum EstadoArtistaDeezer
{
    /// <summary>Su catálogo en Deezer tiene canciones que están en la biblioteca con ese artista.</summary>
    Confirmado,

    /// <summary>
    /// Ninguno de los artistas de Deezer con ese nombre tiene canciones de la biblioteca: se ha tomado el
    /// de más seguidores, que puede ser un homónimo.
    /// </summary>
    Dudoso,

    /// <summary>Deezer no tiene a nadie con ese nombre.</summary>
    NoEncontrado,

    /// <summary>Lo ha indicado el usuario: no se vuelve a buscar.</summary>
    Manual,
}

/// <summary>
/// La correspondencia entre un artista de la biblioteca y su artista en Deezer. Se guarda para no
/// repetir la búsqueda cada vez y para que el usuario pueda corregirla.
/// </summary>
public sealed class ArtistaDeezerGuardado
{
    /// <summary>La clave del artista en la biblioteca (<see cref="ArtistaBiblioteca.Clave"/>).</summary>
    public string Clave { get; set; } = "";

    /// <summary>Cómo lo escribe la biblioteca.</summary>
    public string Nombre { get; set; } = "";

    /// <summary>Su número en Deezer, o 0 si no se encontró.</summary>
    public long DeezerId { get; set; }

    public string DeezerNombre { get; set; } = "";

    /// <summary>Seguidores en Deezer cuando se identificó.</summary>
    public int Fans { get; set; }

    public EstadoArtistaDeezer Estado { get; set; }

    /// <summary>Canciones de la biblioteca que casan con su catálogo de Deezer (0 si no se comprobó).</summary>
    public int Coincidencias { get; set; }

    /// <summary>El usuario no quiere ver sus novedades. Se conserva el artista de Deezer por si se vuelve a incluir.</summary>
    public bool Excluido { get; set; }

    /// <summary>Cuándo se identificó (o se corrigió).</summary>
    public DateTime Fecha { get; set; }

    [JsonIgnore] public string Enlace => DeezerId > 0 ? NovedadesDeezer.EnlaceArtista(DeezerId) : "";

    public ArtistaDeezerGuardado Copia() => (ArtistaDeezerGuardado)MemberwiseClone();
}

/// <summary>
/// Las correspondencias artista de la biblioteca → artista de Deezer («novedades-artistas.json»).
///
/// Las corrige el usuario, así que no son solo una caché: se escriben de forma atómica y un archivo
/// ilegible se aparta en vez de pisarse (<see cref="ArchivoJson"/>), como las fichas y las pendientes.
/// Es seguro con varias consultas a la vez: la búsqueda de novedades identifica dos artistas en paralelo.
/// </summary>
public sealed class AlmacenArtistasDeezer
{
    public const string NombreArchivo = "novedades-artistas.json";

    /// <summary>
    /// Cada cuánto se vuelve a identificar un artista dudoso o no encontrado: la biblioteca puede haber
    /// ganado canciones que lo confirmen, y Deezer puede haberlo dado de alta. Los confirmados y los
    /// corregidos a mano no se vuelven a buscar solos. Es lo mismo que dura la caché de red, de modo que
    /// la nueva búsqueda pregunta de verdad a Deezer.
    /// </summary>
    public static readonly TimeSpan Revision = TimeSpan.FromDays(30);

    private readonly string _archivo;
    private readonly object _cerrojo = new();
    private readonly object _cerrojoGuardar = new();   // dos guardados a la vez usarían el mismo temporal
    private readonly Dictionary<string, ArtistaDeezerGuardado> _porClave = new(StringComparer.Ordinal);

    public string AvisoCarga { get; }

    /// <summary>No se pudo leer ni apartar el archivo al abrirlo: <see cref="Guardar"/> no lo pisa.</summary>
    public bool SoloLectura { get; }

    public AlmacenArtistasDeezer(string archivo)
    {
        _archivo = archivo;
        var leidos = ArchivoJson.Leer(archivo, () => new List<ArtistaDeezerGuardado>(), out var aviso, out var soloLectura);
        foreach (var a in leidos)
        {
            // Un archivo editado a mano puede traer nulos o claves vacías.
            if (a == null || string.IsNullOrWhiteSpace(a.Clave)) continue;
            a.Nombre ??= "";
            a.DeezerNombre ??= "";
            _porClave[a.Clave] = a;
        }
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    public int Count { get { lock (_cerrojo) return _porClave.Count; } }

    /// <summary>Una copia de lo guardado para ese artista, o null.</summary>
    public ArtistaDeezerGuardado? Buscar(string clave)
    {
        lock (_cerrojo) return _porClave.TryGetValue(clave, out var a) ? a.Copia() : null;
    }

    /// <summary>El usuario ha pedido no buscar las novedades de ese artista.</summary>
    public bool EstaExcluido(string clave)
    {
        lock (_cerrojo) return _porClave.TryGetValue(clave, out var a) && a.Excluido;
    }

    /// <summary>Copias de los artistas excluidos, por nombre.</summary>
    public List<ArtistaDeezerGuardado> Excluidos()
    {
        lock (_cerrojo)
            return _porClave.Values.Where(a => a.Excluido)
                            .OrderBy(a => a.Nombre, StringComparer.CurrentCultureIgnoreCase)
                            .Select(a => a.Copia()).ToList();
    }

    /// <summary>
    /// Guarda lo que se ha averiguado de un artista. Conserva la exclusión que hubiera puesto el usuario,
    /// y nunca pisa una corrección suya con una búsqueda automática.
    /// </summary>
    public void Poner(ArtistaDeezerGuardado nuevo)
    {
        lock (_cerrojo)
        {
            if (_porClave.TryGetValue(nuevo.Clave, out var antes))
            {
                if (antes.Estado == EstadoArtistaDeezer.Manual && nuevo.Estado != EstadoArtistaDeezer.Manual) return;
                nuevo.Excluido = antes.Excluido;
            }
            _porClave[nuevo.Clave] = nuevo.Copia();
        }
    }

    /// <summary>El usuario indica cuál es su artista en Deezer: queda como <see cref="EstadoArtistaDeezer.Manual"/>.</summary>
    public void Corregir(string clave, string nombre, ArtistaDeezer deezer, DateTime ahora)
        => PonerManual(new ArtistaDeezerGuardado
        {
            Clave = clave, Nombre = nombre, DeezerId = deezer.Id, DeezerNombre = deezer.Nombre, Fans = deezer.Fans,
            Estado = EstadoArtistaDeezer.Manual, Fecha = ahora,
        });

    private void PonerManual(ArtistaDeezerGuardado a)
    {
        lock (_cerrojo)
        {
            if (_porClave.TryGetValue(a.Clave, out var antes)) a.Excluido = antes.Excluido;
            _porClave[a.Clave] = a;
        }
    }

    /// <summary>No buscar (o volver a buscar) las novedades de ese artista. Devuelve si cambió algo.</summary>
    public bool Excluir(string clave, string nombre, bool excluir, DateTime ahora)
    {
        lock (_cerrojo)
        {
            if (!_porClave.TryGetValue(clave, out var a))
            {
                if (!excluir) return false;
                // Excluir a uno que todavía no se ha identificado: queda apuntado sin artista de Deezer.
                _porClave[clave] = new ArtistaDeezerGuardado
                {
                    Clave = clave, Nombre = nombre, Estado = EstadoArtistaDeezer.NoEncontrado, Excluido = true, Fecha = ahora,
                };
                return true;
            }
            if (a.Excluido == excluir) return false;
            a.Excluido = excluir;
            return true;
        }
    }

    /// <summary>
    /// Olvida lo averiguado de un artista (también una corrección a mano), para que la próxima búsqueda
    /// lo vuelva a identificar. La exclusión se conserva. Devuelve si había algo.
    /// </summary>
    public bool Olvidar(string clave)
    {
        lock (_cerrojo)
        {
            if (!_porClave.TryGetValue(clave, out var a)) return false;
            if (a.Excluido)
            {
                a.DeezerId = 0;
                a.DeezerNombre = "";
                a.Fans = 0;
                a.Coincidencias = 0;
                a.Estado = EstadoArtistaDeezer.NoEncontrado;
                a.Fecha = DateTime.MinValue;   // ya «caducado»: se identifica en cuanto se vuelva a incluir
                return true;
            }
            return _porClave.Remove(clave);
        }
    }

    /// <summary>
    /// ¿Hay que (volver a) identificar a este artista en Deezer? Sí si no se ha hecho nunca, o si quedó
    /// dudoso o sin encontrar hace más de <see cref="Revision"/>.
    /// </summary>
    public static bool HayQueIdentificar(ArtistaDeezerGuardado? guardado, DateTime ahora)
        => guardado == null
        || (guardado.Estado is EstadoArtistaDeezer.Dudoso or EstadoArtistaDeezer.NoEncontrado && ahora - guardado.Fecha > Revision);

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo.</summary>
    public string Guardar()
    {
        if (SoloLectura) return ArchivoJson.AvisoSoloLectura(_archivo);
        List<ArtistaDeezerGuardado> copia;
        lock (_cerrojo) copia = _porClave.Values.OrderBy(a => a.Clave, StringComparer.Ordinal).Select(a => a.Copia()).ToList();
        lock (_cerrojoGuardar) return ArchivoJson.Escribir(_archivo, copia);
    }
}
