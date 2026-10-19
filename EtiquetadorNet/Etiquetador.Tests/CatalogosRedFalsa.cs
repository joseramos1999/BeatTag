using System.Net;
using System.Net.Http;
using System.Text;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Una red de mentira para probar la capa de catálogos sin salir a internet: cada regla dice qué
/// responder a las URL que contienen un texto, y se apunta todo lo que se pide.
/// </summary>
internal sealed class RedFalsa : HttpMessageHandler
{
    private readonly List<(string Contiene, Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Da)> _reglas = new();
    private readonly Dictionary<string, int> _veces = new();
    private readonly List<string> _pedidas = new();

    /// <summary>Lo pedido, en orden.</summary>
    public IReadOnlyList<string> Pedidas { get { lock (_pedidas) return _pedidas.ToList(); } }

    public int Cuantas(string contiene) => Pedidas.Count(u => u.Contains(contiene, StringComparison.OrdinalIgnoreCase));

    /// <summary>Regla general: recibe cuántas veces se ha pedido ya esa regla (1, 2…).</summary>
    public RedFalsa Cuando(string contiene, Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> da)
    {
        _reglas.Add((contiene, da));
        return this;
    }

    public RedFalsa Responde(string contiene, string cuerpo, HttpStatusCode codigo = HttpStatusCode.OK)
        => Cuando(contiene, (_, req, _) => Task.FromResult(Respuesta(req, cuerpo, codigo)));

    /// <summary>Responde lo que toque según la vez: la primera, la segunda… (la última se repite).</summary>
    public RedFalsa Secuencia(string contiene, params (HttpStatusCode Codigo, string Cuerpo)[] respuestas)
        => Cuando(contiene, (n, req, _) =>
        {
            var r = respuestas[Math.Min(n, respuestas.Length) - 1];
            return Task.FromResult(Respuesta(req, r.Cuerpo, r.Codigo));
        });

    /// <summary>Sin conexión: la petición falla como falla un cable desenchufado.</summary>
    public RedFalsa SinConexion(string contiene)
        => Cuando(contiene, (_, _, _) => throw new HttpRequestException("No se puede establecer una conexión (red falsa)."));

    /// <summary>El servidor no contesta nunca: solo sale por el tiempo de espera o por cancelar.</summary>
    public RedFalsa Cuelga(string contiene)
        => Cuando(contiene, async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("inalcanzable");
        });

    /// <summary>Simula un enlace que redirige: la respuesta dice que la dirección final es <paramref name="destino"/>.</summary>
    public RedFalsa Redirige(string contiene, string destino, string cuerpo = "")
        => Cuando(contiene, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(cuerpo, Encoding.UTF8, "text/html"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, destino),
        }));

    public static HttpResponseMessage Respuesta(HttpRequestMessage req, string cuerpo, HttpStatusCode codigo = HttpStatusCode.OK)
        => new(codigo) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json"), RequestMessage = req };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (_pedidas) _pedidas.Add(url);
        foreach (var (contiene, da) in _reglas)
        {
            if (!url.Contains(contiene, StringComparison.OrdinalIgnoreCase)) continue;
            int n;
            lock (_veces) { _veces[contiene] = n = _veces.GetValueOrDefault(contiene) + 1; }
            return da(n, request, cancellationToken);
        }
        return Task.FromResult(Respuesta(request, "{}", HttpStatusCode.NotFound));
    }
}

/// <summary>
/// Reloj simulado para el limitador: «esperar» adelanta el reloj al instante, así que las pruebas
/// no tardan lo que tardaría la red, y se puede comprobar cuánto se habría esperado.
/// </summary>
internal sealed class RelojFalso
{
    private readonly object _l = new();
    private TimeSpan _ahora = TimeSpan.FromHours(1);
    private readonly List<TimeSpan> _esperas = new();

    public TimeSpan Ahora { get { lock (_l) return _ahora; } }
    public IReadOnlyList<TimeSpan> Esperas { get { lock (_l) return _esperas.ToList(); } }
    public TimeSpan EsperaTotal => Esperas.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    public void Avanzar(TimeSpan t) { lock (_l) _ahora += t; }

    public Task Esperar(TimeSpan t, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_l) { _esperas.Add(t); _ahora += t; }
        return Task.CompletedTask;
    }

    public LimitadorPeticiones Limitador() => new(() => Ahora, Esperar);
}

/// <summary>Un ApiClient sobre la red falsa, con su carpeta de datos temporal.</summary>
internal sealed class MontajeApi : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "etq-catalogos-" + Guid.NewGuid().ToString("N"));
    public RedFalsa Red { get; } = new();
    public RelojFalso Reloj { get; } = new();
    public AppPaths Paths { get; }
    public HttpClient Http { get; }
    public ApiClient Api { get; }

    public MontajeApi(bool cache = false, TimeSpan? timeout = null)
    {
        Paths = new AppPaths(Dir);
        Http = new HttpClient(Red) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        Api = new ApiClient(Paths, Http, Reloj.Limitador()) { CacheOn = cache, PodaAutomatica = false };
    }

    public void Dispose()
    {
        Api.Dispose();
        Http.Dispose();
        try { Directory.Delete(Dir, true); } catch { /* best-effort */ }
    }
}
