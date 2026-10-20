using System.Text.Json;

namespace Etiquetador.Core.Dj;

// ---------------------------------------------------------------------------------------------
// Deshacer una sustitución de la Bandeja devuelve las fichas (I14, revisión de integración)
// ---------------------------------------------------------------------------------------------
//
// POR QUÉ. Al sustituir, la ficha de DJ de la copia antigua se fusiona con la de la descarga y pasa
// a la ruta nueva, y lo decidido sobre la copia antigua (aplicada, descartada, casillas, avisos de
// audio aceptados) se olvida porque ahora hay otro archivo. El registro para deshacer solo lleva el
// traslado. Visto en la verificación de la tanda 3: al deshacer, la descarga volvía a la bandeja y,
// al releer la biblioteca, la recolocación de fichas seguía la vuelta deshecha (destino → bandeja) y
// se llevaba la ficha fusionada a la descarga. Al recuperar la copia antigua de la papelera, como
// indica la ayuda, volvía SIN ficha; si después se descartaba la descarga, la ficha quedaba huérfana
// para siempre. Y lo olvidado sobre la copia antigua no volvía nunca.
//
// Ahora, al sustituir se apunta aquí lo que había de las dos (fichas y decisiones). Si la
// sustitución se deshace, la descarga vuelve a la bandeja con su propia ficha (o con la que el DJ le
// haya puesto después) y la de la copia antigua se aparta hasta que su archivo vuelva a aparecer en
// la biblioteca. Entonces recupera la ficha y lo decidido.

/// <summary>Lo que había de los dos archivos al sustituir la copia de la biblioteca por una descarga (I14).</summary>
public sealed class SustitucionHecha
{
    public DateTime Fecha { get; set; }

    /// <summary>Dónde estaba la descarga en la bandeja (adonde vuelve al deshacer).</summary>
    public string Bandeja { get; set; } = "";

    /// <summary>
    /// Dónde está la descarga en la biblioteca: al sustituir, la carpeta y el nombre de la copia
    /// antigua; después sigue a los renombrados que haga BeatTag (<see cref="AlmacenSustituciones.Seguir"/>).
    /// </summary>
    public string Destino { get; set; } = "";

    /// <summary>
    /// El historial de deshacer ha devuelto la descarga a la bandeja (lo ha visto
    /// <see cref="AlmacenSustituciones.Seguir"/>): la sustitución está deshecha, tenga la descarga el
    /// tamaño que tenga. Opcional: un archivo anterior se lee igual.
    /// </summary>
    public bool VueltaALaBandeja { get; set; }

    /// <summary>La copia de la biblioteca que fue a la papelera.</summary>
    public string Vieja { get; set; } = "";

    /// <summary>
    /// Tamaño de la copia antigua. Al recuperarla de la papelera vuelve igual; si en su ruta aparece un
    /// archivo de otro tamaño es OTRO archivo, y lo decidido sobre la copia antigua no vale para él.
    /// </summary>
    public long ViejaBytes { get; set; }

    /// <summary>
    /// Tamaño de la descarga. Solo sirve de respaldo para reconocer el deshacer de las sustituciones
    /// apuntadas antes de <see cref="VueltaALaBandeja"/>; 0 si se le copiaron etiquetas (cambia).
    /// </summary>
    public long NuevaBytes { get; set; }

    /// <summary>La ficha de la copia antigua antes de fusionarla.</summary>
    public FichaDj? FichaVieja { get; set; }

    /// <summary>La ficha que tenía la descarga en la bandeja antes de fusionarla.</summary>
    public FichaDj? FichaNueva { get; set; }

    /// <summary>La ficha que se puso en el destino (la fusión de las dos).</summary>
    public FichaDj? FichaPuesta { get; set; }

    // Lo decidido sobre la copia antigua, que se olvidó al sustituir.
    public bool Aplicada { get; set; }
    public bool Descartada { get; set; }
    public bool AudioAceptada { get; set; }

    /// <summary>Las casillas «Aplicar» de Enriquecer de la copia antigua, con su clave tal cual.</summary>
    public Dictionary<string, bool> Casillas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cuándo se vio deshecha (la descarga ha vuelto a la bandeja). Null mientras siga hecha. Desde
    /// entonces solo falta que la copia antigua vuelva de la papelera.
    /// </summary>
    public DateTime? Deshecha { get; set; }
}

/// <summary>Lo que ha encontrado una pasada de <see cref="AlmacenSustituciones.Conciliar"/>.</summary>
/// <param name="Deshechas">Sustituciones que se acaban de ver deshechas: la descarga ya tiene su ficha en la bandeja.</param>
/// <param name="Recuperadas">Copias antiguas que han vuelto: ya tienen su ficha; falta devolverles lo decidido.</param>
/// <param name="Olvidadas">Apuntes que dejan de vigilarse (caducados, o la ruta la ocupa otro archivo).</param>
public sealed record ConciliacionSustituciones(IReadOnlyList<SustitucionHecha> Deshechas,
                                               IReadOnlyList<SustitucionHecha> Recuperadas, int Olvidadas)
{
    public bool HayCambios => Deshechas.Count > 0 || Recuperadas.Count > 0 || Olvidadas > 0;
}

/// <summary>Las sustituciones hechas desde la Bandeja que todavía se pueden deshacer, en disco.</summary>
public sealed class AlmacenSustituciones
{
    /// <summary>
    /// Cuántas se recuerdan como mucho. Cada una ocupa poco, pero cada cambio de la biblioteca mira
    /// las que siguen hechas: sin tope, la lista crecería con cada sustitución durante años. Un DJ
    /// que sustituya unas pocas a la semana deshace, si acaso, las de los últimos días.
    /// </summary>
    public const int Maximo = 300;

    /// <summary>
    /// Cuánto se espera a que vuelva de la papelera la copia antigua de una sustitución deshecha. La
    /// papelera de macOS se puede vaciar sola a los 30 días; la de Windows, cuando se llena.
    /// </summary>
    public static readonly TimeSpan EsperaCopiaAntigua = TimeSpan.FromDays(180);

    private readonly string _archivo;
    private readonly object _cerrojo = new();
    private readonly List<SustitucionHecha> _lista;

    public string AvisoCarga { get; }
    public bool SoloLectura { get; }

    public AlmacenSustituciones(string archivo)
    {
        _archivo = archivo;
        _lista = ArchivoJson.Leer(archivo, () => new List<SustitucionHecha>(), out var aviso, out var soloLectura);
        AvisoCarga = aviso;
        SoloLectura = soloLectura;
    }

    /// <summary>El archivo, en la carpeta de datos de la aplicación.</summary>
    public static string RutaEn(string carpetaDatos) => Path.Combine(carpetaDatos, "bandeja-sustituidas.json");

    public int Count { get { lock (_cerrojo) return _lista.Count; } }

    public IReadOnlyList<SustitucionHecha> Todas { get { lock (_cerrojo) return _lista.ToList(); } }

    /// <summary>Apunta una sustitución recién hecha. Si se pasa del máximo, se olvidan las más antiguas.</summary>
    public void Anotar(SustitucionHecha s)
    {
        lock (_cerrojo)
        {
            _lista.Add(s);
            if (_lista.Count > Maximo) _lista.RemoveRange(0, _lista.Count - Maximo);
        }
    }

    /// <summary>
    /// Las rutas que hay que mirar en la biblioteca: el destino de las que siguen hechas y la copia
    /// antigua de las deshechas. Sirve para recorrer la biblioteca una sola vez buscando solo esas.
    /// </summary>
    public IReadOnlyCollection<string> RutasAVigilar()
    {
        lock (_cerrojo)
            return _lista.Select(s => s.Deshecha == null ? s.Destino : s.Vieja)
                         .Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// BeatTag ha movido o renombrado un archivo (aplicar una propuesta, renombrar, deshacer…; lo
    /// avisa AppEngine.SeguirArchivo). El destino de las sustituciones hechas lo sigue y, si lo que se
    /// mueve es la descarga de vuelta a su sitio en la bandeja, es que se ha deshecho la sustitución.
    ///
    /// POR QUÉ. Antes el deshacer se reconocía por el TAMAÑO de lo que hubiera en la bandeja: tras
    /// sustituir y aplicar la propuesta de Enriquecer (con carátula), deshacer las dos devolvía la
    /// descarga con otro tamaño y no se reconocía; y con etiquetas copiadas (tamaño 0) bastaba con que
    /// llegara otra descarga con ese nombre. Además, como el destino no seguía al renombrado que suele
    /// hacer Enriquecer después de sustituir, salía de la biblioteca y cada cambio de ella miraba el
    /// disco por esa entrada, en el hilo de la interfaz y para siempre. Devuelve si ha cambiado algo.
    /// </summary>
    public bool Seguir(string origen, string destino)
    {
        if (string.IsNullOrEmpty(origen) || string.IsNullOrEmpty(destino)
            || string.Equals(origen, destino, StringComparison.Ordinal)) return false;
        var cambio = false;
        lock (_cerrojo)
        {
            foreach (var s in _lista)
            {
                if (s.Deshecha != null || s.VueltaALaBandeja
                    || !string.Equals(s.Destino, origen, StringComparison.OrdinalIgnoreCase)) continue;
                // El destino se queda donde estaba la ficha fusionada: DevolverFichaALaDescarga la quita de ahí.
                if (string.Equals(destino, s.Bandeja, StringComparison.OrdinalIgnoreCase)) s.VueltaALaBandeja = true;
                else s.Destino = destino;
                cambio = true;
            }
        }
        return cambio;
    }

    /// <summary>
    /// Las hechas cuyo destino estaba fuera de la biblioteca y ya se miraron en disco en esta sesión
    /// sin encontrar nada: no se vuelven a mirar con cada cambio de la biblioteca (ver
    /// <see cref="Conciliar"/>). Si después se deshacen, lo dice <see cref="Seguir"/>.
    /// </summary>
    private readonly HashSet<SustitucionHecha> _miradasSinNovedad = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Mira si alguna sustitución se ha deshecho o si ha vuelto la copia antigua de alguna deshecha, y
    /// coloca las fichas en consecuencia. Se llama con cada cambio de la biblioteca, en el hilo de la
    /// interfaz, y ANTES de la recolocación de fichas huérfanas, que si no seguiría la vuelta deshecha
    /// y se llevaría la ficha fusionada a la descarga.
    ///
    /// Apenas mira el disco: el deshacer lo dice el historial (<see cref="Seguir"/>) y el destino sigue
    /// a los renombrados, así que una sustitución hecha suele seguir en la biblioteca. Solo una cuyo
    /// destino ha salido de la biblioteca por otra vía (o apuntada por una versión anterior) se mira en
    /// disco, UNA vez por sesión, por si se deshizo antes de seguirla; y se olvida cuando pasa el mismo
    /// plazo que se espera a la copia antigua.
    ///
    /// Lo decidido sobre los archivos (aplicadas, casillas…) lo devuelve quien llama, con lo que se
    /// devuelve aquí: el formato de esas listas es cosa de la aplicación.
    /// </summary>
    /// <param name="enBiblioteca">Si una ruta está ahora en la biblioteca (sin mirar el disco).</param>
    /// <param name="tamano">El tamaño de un archivo, o null si no existe.</param>
    public ConciliacionSustituciones Conciliar(AlmacenFichas fichas, Func<string, bool> enBiblioteca,
                                               Func<string, long?> tamano, DateTime ahora)
    {
        var deshechas = new List<SustitucionHecha>();
        var recuperadas = new List<SustitucionHecha>();
        var olvidadas = 0;
        lock (_cerrojo)
        {
            foreach (var s in _lista.ToList())
            {
                if (s.Deshecha == null)
                {
                    if (!s.VueltaALaBandeja)
                    {
                        // Sigue en la biblioteca (el destino sigue a los renombrados): nada que hacer.
                        if (enBiblioteca(s.Destino)) continue;
                        // Fuera de ella y hecha hace más de lo que se espera a una copia antigua: ya no se
                        // deshará. (Sin fecha, que no debería pasar, no caduca.)
                        if (s.Fecha != default && ahora - s.Fecha > EsperaCopiaAntigua)
                        {
                            _lista.Remove(s);
                            _miradasSinNovedad.Remove(s);
                            olvidadas++;
                            continue;
                        }
                        if (!_miradasSinNovedad.Add(s)) continue;   // ya mirada en esta sesión
                        // El respaldo de antes, para las apuntadas sin seguir sus renombrados: la descarga en
                        // la bandeja con SU tamaño. Sin tamaño fiable (etiquetas copiadas) solo vale el historial.
                        if (tamano(s.Destino) != null || s.NuevaBytes <= 0) continue;
                        if (tamano(s.Bandeja) != s.NuevaBytes) continue;
                    }

                    DevolverFichaALaDescarga(fichas, s);
                    s.Deshecha = ahora;
                    _miradasSinNovedad.Remove(s);
                    deshechas.Add(s);
                }

                // Deshecha: falta que vuelva la copia antigua (también puede haber vuelto ya).
                if (ahora - s.Deshecha.Value > EsperaCopiaAntigua) { _lista.Remove(s); olvidadas++; continue; }
                if (!enBiblioteca(s.Vieja)) continue;
                var bytes = tamano(s.Vieja);
                if (bytes == null) continue;   // la biblioteca aún no se ha enterado de que no está
                _lista.Remove(s);
                if (s.ViejaBytes > 0 && bytes != s.ViejaBytes) { olvidadas++; continue; }   // es otro archivo

                // Si mientras tanto alguien le ha puesto ficha, manda esa.
                if (s.FichaVieja != null && fichas.Obtener(s.Vieja) == null) fichas.Poner(s.Vieja, s.FichaVieja);
                recuperadas.Add(s);
            }
        }
        return new ConciliacionSustituciones(deshechas, recuperadas, olvidadas);
    }

    /// <summary>
    /// La descarga ha vuelto a la bandeja: su ficha es la que tenía allí, salvo que el DJ cambiara la
    /// fusionada después de sustituir (entonces se queda con lo cambiado, que es el trabajo más
    /// reciente y se hizo escuchando ese archivo). La del destino se quita, para que la recolocación
    /// de huérfanas no se la lleve a la bandeja. Funciona igual si esa recolocación ya ha pasado.
    /// </summary>
    internal static void DevolverFichaALaDescarga(AlmacenFichas fichas, SustitucionHecha s)
    {
        var actual = fichas.Obtener(s.Destino) ?? fichas.Obtener(s.Bandeja);
        var paraLaDescarga = MismaFicha(actual, s.FichaPuesta) ? s.FichaNueva : actual;
        fichas.Quitar(s.Destino);
        var enBandeja = fichas.Obtener(s.Bandeja);
        // Una ficha que alguien puso en la bandeja después (no la que llegó desde el destino) se respeta.
        if (enBandeja != null && !MismaFicha(enBandeja, actual)) return;
        if (paraLaDescarga == null) fichas.Quitar(s.Bandeja);
        else fichas.Poner(s.Bandeja, paraLaDescarga);
    }

    /// <summary>Dos fichas dicen lo mismo (dos null también).</summary>
    internal static bool MismaFicha(FichaDj? a, FichaDj? b)
    {
        if (a == null || a.EstaVacia) return b == null || b.EstaVacia;
        if (b == null || b.EstaVacia) return false;
        return JsonSerializer.Serialize(a, ArchivoJson.Opciones) == JsonSerializer.Serialize(b, ArchivoJson.Opciones);
    }

    /// <summary>Solo un guardado a la vez; ver <see cref="Guardar"/>.</summary>
    private readonly object _cerrojoGuardado = new();

    /// <summary>
    /// Devuelve "" si fue bien, o el motivo del fallo. En solo lectura no escribe nada.
    ///
    /// POR QUÉ el cerrojo propio (revisión de la tanda 3). Se guarda desde los hilos de fondo de
    /// aplicar y deshacer (AppEngine.SeguirSustituciones) y desde el de la interfaz (al conciliar con
    /// la biblioteca y al sustituir en la Bandeja). Dos guardados a la vez escribían el MISMO
    /// «bandeja-sustituidas.json.tmp»: uno fallaba con el archivo en uso, o el segundo intercambio no
    /// encontraba el temporal que el primero ya había puesto en su sitio. Y como la foto de la lista se
    /// tomaba fuera, un guardado con la foto anterior podía terminar el último y dejar en disco un
    /// estado viejo. Ahora la foto se toma DENTRO del turno de escritura: el último en escribir
    /// lleva siempre lo más reciente. La foto es un JsonElement, para que nadie cambie un registro
    /// (Seguir cambia el destino) mientras se serializa fuera del cerrojo de la lista.
    /// </summary>
    public string Guardar()
    {
        if (SoloLectura) return ArchivoJson.AvisoSoloLectura(_archivo);
        lock (_cerrojoGuardado)
        {
            JsonElement foto;
            lock (_cerrojo) foto = JsonSerializer.SerializeToElement(_lista, ArchivoJson.Opciones);
            return ArchivoJson.Escribir(_archivo, foto);
        }
    }
}
