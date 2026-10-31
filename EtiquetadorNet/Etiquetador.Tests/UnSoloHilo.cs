namespace Etiquetador.Tests;

/// <summary>
/// Un contexto de sincronización de un solo hilo, como el de la interfaz, para las pruebas. Con él,
/// lo que una página hace «de vuelta en la interfaz» (el avance, el estado final, quitar de la
/// biblioteca) ocurre en un hilo y en orden, igual que en la aplicación; sin él, el avance llegaría
/// por el grupo de hilos y podría pisar el estado final, y la prueba fallaría de vez en cuando.
/// </summary>
internal sealed class UnSoloHilo : SynchronizationContext
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _cola = new();

    /// <summary>
    /// Lo que llega cuando la prueba ya ha terminado se descarta. Si una prueba falla con algo todavía
    /// en marcha (una comparación, un envío), su continuación llegaba tarde, Add lanzaba en un hilo del
    /// grupo y tumbaba el proceso de pruebas entero en vez de dar un fallo limpio.
    /// </summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _cola.Add((d, state)); }
        catch (InvalidOperationException) when (_cola.IsAddingCompleted) { }
    }

    public static Task Ejecutar(Func<Task> trabajo)
    {
        var listo = new TaskCompletionSource();
        var hilo = new Thread(() =>
        {
            var ctx = new UnSoloHilo();
            SetSynchronizationContext(ctx);
            Task t;
            try { t = trabajo(); }
            catch (Exception e) { listo.SetException(e); return; }
            t.ContinueWith(_ => ctx._cola.CompleteAdding(), TaskScheduler.Default);
            foreach (var (d, s) in ctx._cola.GetConsumingEnumerable()) d(s);
            if (t.IsFaulted) listo.SetException(t.Exception!.InnerExceptions);
            else listo.SetResult();
        }) { IsBackground = true };
        hilo.Start();
        return listo.Task;
    }

    /// <summary>Espera, sin bloquear el hilo, a que se cumpla una condición; falla la prueba si no llega.</summary>
    public static async Task HastaQue(Func<bool> condicion, string que)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (!condicion())
        {
            Assert.True(DateTime.UtcNow < limite, "no llegó a pasar: " + que);
            await Task.Delay(10);
        }
    }
}
