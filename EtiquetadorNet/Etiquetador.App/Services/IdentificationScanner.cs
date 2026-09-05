using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.Services;

/// <summary>
/// Averigua QUÉ ES cada archivo escuchando su audio, y guarda la respuesta para no volver a
/// preguntarla nunca por el mismo archivo.
///
/// Usa DOS motores encadenados, y el orden importa porque uno es gratis y el otro no:
///
///   1. <b>AcoustID</b> (gratis, 3 consultas por segundo). Compara la huella Chromaprint contra su
///      base. Se prueba SIEMPRE y con todo.
///   2. <b>AudD</b> (de pago). Solo se le pregunta por lo que AcoustID no ha sabido, y solo si el
///      usuario lo activa. Reconoce a partir de un fragmento cualquiera, así que sí identifica las
///      ediciones de pool que a AcoustID se le escapan.
///
/// <b>La huella se calcula desde MITAD del tema, no desde el principio.</b> Es la corrección que da
/// sentido a volver a intentarlo con AcoustID: fpcalc analiza por defecto los primeros 120 segundos,
/// y en una biblioteca de DJ ese trozo es justo el intro añadido por el editor, que no existe en la
/// grabación publicada. Se le estaba preguntando por la única parte que garantizaba no encontrar
/// nada. Cuánto mejora eso está SIN MEDIR: es lo que hay que comprobar.
///
/// La caché no es aquí una optimización, es lo que hace la función asumible: cada consulta de pago
/// cuesta dinero. Por eso se guardan TAMBIÉN las respuestas negativas, y se anota con qué motores
/// se preguntó: un «no» del gratuito no cierra la puerta a preguntarle luego al de pago.
///
/// Lo que NUNCA se guarda son los errores. Un fallo de red o una cuota agotada no dicen nada sobre
/// la canción, y guardarlos dejaría la biblioteca marcada como «sin identificar» para siempre.
/// </summary>
public sealed class IdentificationScanner
{
    private sealed class Entry
    {
        public long M { get; set; }                    // fecha de modificación
        public long S { get; set; }                    // tamaño
        public string A { get; set; } = "";            // artista
        public string T { get; set; } = "";            // título
        public string L { get; set; } = "";            // álbum
        public string F { get; set; } = "";            // motor que respondió
        public bool Sin { get; set; }                  // respondieron, pero no la reconocieron
        public bool P { get; set; }                    // ¿se llegó a preguntar al motor de PAGO?
        public double O { get; set; }                  // de qué punto del tema salió el fragmento
    }

    /// <summary>Qué motores usar en esta pasada y con qué límite de gasto.</summary>
    /// <param name="AcoustIdKey">Clave de AcoustID; vacía = no se usa el motor gratuito.</param>
    /// <param name="AuddToken">Clave de AudD; vacía = no se usa el de pago.</param>
    /// <param name="UsarPago">Permiso explícito para gastar.</param>
    /// <param name="MaxPago">Tope de consultas de pago en esta pasada. Es el freno de mano.</param>
    public readonly record struct Opciones(string AcoustIdKey, string AuddToken, bool UsarPago, int MaxPago);

    /// <summary>Resumen de una pasada.</summary>
    /// <param name="Cancelada">
    /// La paró el USUARIO. Va aparte de <paramref name="Error"/> porque no es un fallo, pero
    /// tampoco es un final normal: sin distinguirlo, pulsar Cancelar terminaba anunciando
    /// «Comprobadas N…» como si hubiera acabado sola.
    /// </param>
    public readonly record struct Resumen(
        int Consultadas, int PorGratis, int PorPago, int ConsultasDePago, int Fallidas, string Error,
        bool Cancelada = false);

    /// <summary>
    /// Segundos que se envían a AudD. Doce bastan de sobra para reconocer un tema -Shazam se
    /// arregla con menos- y mantienen el envío pequeño: un WAV de 12 s son unos 2 MB, muy por
    /// debajo del límite de 10 MB del servicio.
    /// </summary>
    private const double SegundosPago = 12;

    /// <summary>
    /// Segundos que se analizan para la huella de AcoustID. Ciento veinte es lo que analiza fpcalc
    /// por defecto, así que la huella tiene el mismo tamaño de siempre; lo único que cambia es de
    /// DÓNDE sale.
    /// </summary>
    private const double SegundosGratis = 120;

    /// <summary>
    /// Desde qué punto del tema se recorta. Al 30 % ya se está en el cuerpo de la canción: las
    /// intros de DJ, los silencios y las cortinillas de pool viven al principio, y es justo ahí
    /// donde el audio NO se parece a la grabación original.
    /// </summary>
    private const double InicioFraccion = 0.30;

    /// <summary>
    /// Puntos del tema por los que se va probando al pedir «otro fragmento», en este orden.
    ///
    /// Que un fragmento concreto no valga es de lo más normal y no significa que la canción sea
    /// irreconocible: puede haber caído en un break, en un solo de percusión, en un trozo hablado o
    /// en un silencio. Cambiar de sitio es la respuesta correcta a eso, y por eso se recuerda de
    /// dónde salió cada respuesta: para no volver a mandar exactamente el mismo trozo.
    ///
    /// No se llega al final del tema a propósito: los últimos compases suelen ser cola, aplausos o
    /// una salida mezclada, y ahí el reconocimiento empeora igual que en el intro.
    /// </summary>
    internal static readonly double[] Fracciones = { 0.30, 0.55, 0.15, 0.70, 0.45 };

    /// <summary>El siguiente punto que probar, dado el que se usó la última vez.</summary>
    internal static double SiguienteFraccion(double actual)
    {
        var i = Array.FindIndex(Fracciones, f => Math.Abs(f - actual) < 0.001);
        // Una respuesta guardada antes de que esto existiera no trae punto: se hizo con el de
        // partida, así que lo siguiente que toca es el segundo de la lista, no repetir el primero.
        if (i < 0) i = 0;
        return Fracciones[(i + 1) % Fracciones.Length];
    }

    /// <summary>
    /// Milisegundos mínimos entre dos consultas a AcoustID. Su límite gratuito son 3 por segundo;
    /// 400 ms dejan 2,5, con margen. Se aplica de verdad, con una puerta compartida: el «throttle»
    /// del cliente de API es una espera ANTES de cada petición, y con varias en paralelo eso no
    /// limita nada (cuatro hilos esperando 350 ms dan once por segundo, no tres).
    /// </summary>
    private const int MsEntreConsultasGratis = 400;

    /// <summary>
    /// Consultas a la vez. Pocas a propósito: cada una de pago cuesta dinero y casi todo el tiempo
    /// se va en la red, así que un puñado en paralelo ya aprovecha la espera sin castigar a nadie.
    /// </summary>
    private const int Simultaneas = 4;

    private readonly string _cacheFile;
    private readonly Fingerprint _huellas;
    private readonly AcoustIdProvider _acoustId;
    private readonly AuddProvider _audd;
    private readonly HttpClient _http;
    private readonly Logger? _log;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _sucio;

    // Puerta del motor gratuito: garantiza el hueco mínimo entre consultas pase lo que pase.
    private readonly SemaphoreSlim _turno = new(1, 1);
    private DateTime _ultimaGratis = DateTime.MinValue;

    public IdentificationScanner(string cacheFile, Fingerprint huellas, AcoustIdProvider acoustId,
                                 AuddProvider audd, HttpClient http, Logger? log = null)
    {
        _cacheFile = cacheFile;
        _huellas = huellas;
        _acoustId = acoustId;
        _audd = audd;
        _http = http;
        _log = log;
        Cargar();
    }

    /// <summary>
    /// ¿Hay ya una respuesta buena para este archivo?
    ///
    /// Un «no lo reconozco» del motor gratuito NO cuenta como respuesta cerrada si ahora se va a
    /// usar además el de pago: sería dar por perdida justo la canción que el de pago sí sabría.
    /// </summary>
    public bool Consultado(string path, bool usarPago)
    {
        var e = Vigente(path);
        return e != null && RespuestaCerrada(e.Sin, e.P, usarPago);
    }

    /// <summary>
    /// La regla de la caché, aislada para poder probarla: ¿la respuesta guardada zanja el asunto?
    ///
    /// Es la decisión con más consecuencias de toda la pestaña, y falla hacia los dos lados. Si es
    /// demasiado laxa, se vuelve a preguntar por canciones ya respondidas y eso se paga. Si es
    /// demasiado estricta, un «no lo reconozco» del motor gratuito entierra para siempre justo las
    /// canciones que el de pago sí sabría, y son las únicas por las que merecía la pena pagar.
    /// </summary>
    /// <param name="sinResultado">La respuesta guardada fue «no la reconozco».</param>
    /// <param name="pagoIntentado">Esa respuesta llegó tras preguntar también al motor de pago.</param>
    /// <param name="usarPago">Ahora mismo el motor de pago está activado.</param>
    internal static bool RespuestaCerrada(bool sinResultado, bool pagoIntentado, bool usarPago)
    {
        if (!sinResultado) return true;          // ya se sabe qué es: no hay nada más que preguntar
        return !usarPago || pagoIntentado;       // una negativa solo cierra si se agotaron los motores
    }

    /// <summary>Lo que resultó ser, o null si no se ha consultado o no la reconocieron.</summary>
    public Identificado? Get(string path)
    {
        var e = Vigente(path);
        if (e == null || e.Sin) return null;
        return new Identificado(e.A, e.T, e.L, e.F);
    }

    /// <summary>¿Se ha llegado a preguntar por este archivo, aunque fuera para un «no»?</summary>
    public bool TieneRespuesta(string path) => Vigente(path) != null;

    private Entry? Vigente(string path)
    {
        if (!_cache.TryGetValue(path, out var e)) return null;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.LastWriteTimeUtc.Ticks != e.M || fi.Length != e.S) return null;
            return e;
        }
        catch { return null; }
    }

    /// <summary>
    /// Consulta las que falten. Se puede cancelar, y lo ya respondido queda guardado: cancelar no
    /// tira a la basura consultas que ya se han pagado.
    /// </summary>
    /// <param name="otraParte">
    /// Repetir la consulta con un fragmento DISTINTO del que se usó la vez anterior. Es lo que se
    /// pide sobre una canción concreta cuando el resultado no cuadra: casi siempre el problema no
    /// es la canción, sino que el trozo enviado cayó en un break o en un trozo hablado.
    /// </param>
    public async Task<Resumen> ScanAsync(IReadOnlyList<string> paths, Opciones o, bool force,
                                         IProgress<(int Hechas, int Total, string Archivo)>? progreso,
                                         CancellationToken ct = default, bool otraParte = false)
    {
        if (otraParte) force = true;   // pedir otro fragmento implica no hacer caso de lo guardado
        var pendientes = force ? paths.ToList() : paths.Where(p => !Consultado(p, o.UsarPago)).ToList();
        if (pendientes.Count == 0)
        {
            progreso?.Report((paths.Count, paths.Count, ""));
            return new Resumen(0, 0, 0, 0, 0, "");
        }

        var gratisOn = o.AcoustIdKey.Trim().Length > 0;
        var pagoOn = o.UsarPago && o.AuddToken.Trim().Length > 0 && o.MaxPago > 0;
        if (!gratisOn && !pagoOn)
            return new Resumen(0, 0, 0, 0, 0, "no hay ningún motor disponible: falta la clave de AcoustID y la de AudD");

        _log?.Head($"Identificación: {pendientes.Count} por comprobar de {paths.Count}"
                 + $" · gratuito={(gratisOn ? "AcoustID" : "no")}"
                 + $" · pago={(pagoOn ? $"AudD (máx. {o.MaxPago})" : "no")}");

        int hechas = 0, porGratis = 0, porPago = 0, consultasPago = 0, fallidas = 0;
        var errorParada = "";

        using var parada = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await Parallel.ForEachAsync(pendientes,
                new ParallelOptions { MaxDegreeOfParallelism = Simultaneas, CancellationToken = parada.Token },
                async (ruta, tk) =>
                {
                    var nombre = Path.GetFileName(ruta);
                    var inicio = otraParte ? SiguienteFraccionDe(ruta) : InicioFraccion;
                    if (otraParte) _log?.Detail($"    {nombre} · se prueba con el fragmento del {inicio:P0} del tema");

                    // --- 1) Motor gratuito ---
                    if (gratisOn)
                    {
                        var g = await PorAcoustIdAsync(ruta, o.AcoustIdKey.Trim(), inicio, tk).ConfigureAwait(false);
                        if (g.Match != null)
                        {
                            Guardar(ruta, g.Match, pagoIntentado: false, inicio);
                            Interlocked.Increment(ref porGratis);
                            _log?.Detail($"    {nombre} · GRATIS · el audio es «{g.Match.Artist} – {g.Match.Title}»");
                            Avanzar(ruta);
                            return;
                        }
                        if (g.Error.Length > 0)
                        {
                            // Un fallo del gratuito no cancela nada: se sigue con el de pago si lo hay.
                            _log?.Detail($"    {nombre} · gratis: {g.Error}");
                            if (!pagoOn)
                            {
                                Interlocked.Increment(ref fallidas);
                                Avanzar(ruta);
                                return;
                            }
                        }
                    }

                    // --- 2) Motor de pago, solo para lo que quedó sin resolver ---
                    if (!pagoOn)
                    {
                        Guardar(ruta, null, pagoIntentado: false, inicio);
                        _log?.Detail($"    {nombre} · GRATIS no lo reconoce (sin motor de pago activado)");
                        Avanzar(ruta);
                        return;
                    }

                    // Freno de mano: pasado el tope no se gasta más, y la canción queda SIN cerrar
                    // para poder retomarla otro día. Marcarla como «no reconocida» sería mentir:
                    // a esta no se le ha llegado a preguntar.
                    if (Interlocked.Increment(ref consultasPago) > o.MaxPago)
                    {
                        Interlocked.Decrement(ref consultasPago);
                        if (Interlocked.CompareExchange(ref errorParada, $"alcanzado el tope de {o.MaxPago} consultas de pago", "") == "")
                            _log?.Log($"Identificación: alcanzado el tope de {o.MaxPago} consultas de pago; se detiene.", LogKind.No);
                        parada.Cancel();
                        return;
                    }

                    var p = await PorAuddAsync(ruta, o.AuddToken.Trim(), inicio, tk).ConfigureAwait(false);

                    if (p.Detener)
                    {
                        if (Interlocked.CompareExchange(ref errorParada, p.Error, "") == "")
                            _log?.Err("Identificación: " + p.Error + ". Se detiene la pasada.");
                        parada.Cancel();
                        return;
                    }

                    if (p.Error.Length > 0)
                    {
                        Interlocked.Increment(ref fallidas);
                        _log?.Detail($"    {nombre} · PAGO · FALLO: {p.Error}");
                    }
                    else
                    {
                        Guardar(ruta, p.Match, pagoIntentado: true, inicio);
                        if (p.Match != null)
                        {
                            Interlocked.Increment(ref porPago);
                            _log?.Detail($"    {nombre} · PAGO · el audio es «{p.Match.Artist} – {p.Match.Title}»");
                        }
                        else _log?.Detail($"    {nombre} · ningún motor lo reconoce");
                    }

                    Avanzar(ruta);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* lo respondido queda guardado */ }

        Save();

        // Se distingue quién paró esto. El token propio (`parada`) se cancela solo por cuota o por
        // el tope, y eso ya viaja en `errorParada`; el token de FUERA solo lo cancela el usuario.
        // Tragarse la excepción y devolver un resumen sin más hacía que la pantalla anunciara una
        // comprobación terminada justo después de pulsar Cancelar.
        var cancelada = ct.IsCancellationRequested;

        var res = new Resumen(hechas, porGratis, porPago, Math.Min(consultasPago, o.MaxPago), fallidas,
                              errorParada, cancelada);
        _log?.Sum($"Identificación: {porGratis} por el gratuito · {porPago} por el de pago"
                + $" · {res.ConsultasDePago} consultas de pago gastadas · {fallidas} con fallo"
                + (cancelada ? " · CANCELADA por el usuario" : "")
                + (errorParada.Length > 0 ? $" · detenida: {errorParada}" : ""));
        return res;

        void Avanzar(string ruta)
        {
            var n = Interlocked.Increment(ref hechas);
            if (n % 5 == 0 || n == pendientes.Count)
                progreso?.Report((n, pendientes.Count, Path.GetFileName(ruta)));
        }
    }

    /// <summary>
    /// Motor gratuito: huella Chromaprint del CUERPO del tema, no del principio, contra AcoustID.
    ///
    /// La duración que se envía es la del tema ENTERO, no la del recorte. Comprobado ejecutando
    /// fpcalc: el «duration» que emite es siempre el del archivo, aunque solo analice los primeros
    /// segundos. Mandar la del recorte describiría mal lo que se envía.
    /// </summary>
    private async Task<RespuestaId> PorAcoustIdAsync(string ruta, string clave, double inicio, CancellationToken ct)
    {
        AudioSamples.Recorte? recorte = null;
        try
        {
            recorte = await Task.Run(
                () => AudioSamples.EscribirRecorteWav(ruta, inicio, SegundosGratis, ct), ct)
                .ConfigureAwait(false);

            var fp = await _huellas.GetAsync(recorte.Value.Ruta, _http, ct).ConfigureAwait(false);
            if (fp is not FingerprintResult f || f.Fingerprint.Length == 0)
                return new RespuestaId(null, "no se pudo calcular la huella");

            await EsperarTurnoAsync(ct).ConfigureAwait(false);

            var hit = await _acoustId.LookupAsync(recorte.Value.SegundosOriginal, f.Fingerprint, clave, ct)
                                     .ConfigureAwait(false);
            if (hit == null || hit.Title.Length == 0) return new RespuestaId(null);

            return new RespuestaId(new Identificado(hit.Artist, hit.Title, hit.Album, "AcoustID"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return new RespuestaId(null, "no se pudo leer el audio: " + e.Message);
        }
        finally
        {
            if (recorte != null) { try { File.Delete(recorte.Value.Ruta); } catch { } }
        }
    }

    /// <summary>Motor de pago: recorta, pregunta y borra. El fragmento no sobrevive a la consulta.</summary>
    private async Task<RespuestaId> PorAuddAsync(string ruta, string token, double inicio, CancellationToken ct)
    {
        AudioSamples.Recorte? recorte = null;
        try
        {
            recorte = await Task.Run(
                () => AudioSamples.EscribirRecorteWav(ruta, inicio, SegundosPago, ct), ct)
                .ConfigureAwait(false);

            return await _audd.IdentificarAsync(recorte.Value.Ruta, token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            _log?.Detail($"      identificación: no se pudo preparar {Path.GetFileName(ruta)} · {e.Message}");
            return new RespuestaId(null, "no se pudo leer el audio: " + e.Message);
        }
        finally
        {
            if (recorte != null) { try { File.Delete(recorte.Value.Ruta); } catch { } }
        }
    }

    /// <summary>Reparte los turnos del motor gratuito para no pasarse de su límite de 3 por segundo.</summary>
    private async Task EsperarTurnoAsync(CancellationToken ct)
    {
        await _turno.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var falta = TimeSpan.FromMilliseconds(MsEntreConsultasGratis) - (DateTime.UtcNow - _ultimaGratis);
            if (falta > TimeSpan.Zero) await Task.Delay(falta, ct).ConfigureAwait(false);
            _ultimaGratis = DateTime.UtcNow;
        }
        finally { _turno.Release(); }
    }

    /// <summary>De qué punto salió el fragmento guardado; el de partida si no hay nada guardado.</summary>
    private double SiguienteFraccionDe(string ruta)
        => SiguienteFraccion(_cache.TryGetValue(ruta, out var e) ? e.O : InicioFraccion);

    private void Guardar(string ruta, Identificado? id, bool pagoIntentado, double inicio)
    {
        try
        {
            var fi = new FileInfo(ruta);
            if (!fi.Exists) return;
            _cache[ruta] = new Entry
            {
                M = fi.LastWriteTimeUtc.Ticks,
                S = fi.Length,
                A = id?.Artist ?? "",
                T = id?.Title ?? "",
                L = id?.Album ?? "",
                F = id?.Fuente ?? "",
                Sin = id == null,
                P = pagoIntentado,
                O = inicio,
            };
            Interlocked.Exchange(ref _sucio, 1);
        }
        catch { /* si el archivo desapareció a mitad, no hay nada que guardar */ }
    }

    private void Cargar()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_cacheFile));
            if (d == null) return;
            foreach (var kv in d) _cache[kv.Key] = kv.Value;
        }
        catch { /* caché ilegible: se vuelve a preguntar */ }
    }

    public void Save()
    {
        if (Interlocked.Exchange(ref _sucio, 0) == 0) return;
        try
        {
            var dir = Path.GetDirectoryName(_cacheFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
            File.Move(tmp, _cacheFile, true);
        }
        catch (Exception e) { _log?.Detail("No se pudo guardar la caché de identificación: " + e.Message); }
    }

    /// <summary>
    /// Olvida la respuesta de unas canciones concretas, para poder volver a preguntarlas. Sirve
    /// cuando una respuesta parece equivocada; las que vuelvan al motor de pago costarán otra vez.
    /// </summary>
    public void Olvidar(IEnumerable<string> paths)
    {
        var n = 0;
        foreach (var p in paths)
            if (!string.IsNullOrWhiteSpace(p) && _cache.TryRemove(p, out _)) n++;
        if (n == 0) return;
        Interlocked.Exchange(ref _sucio, 1);
        Save();
    }

    /// <summary>
    /// Olvida TODAS las respuestas. Volver a pedirlas puede costar dinero, así que quien llame debe
    /// preguntar antes.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _sucio, 0);
        try { if (File.Exists(_cacheFile)) File.Delete(_cacheFile); } catch { }
    }
}
