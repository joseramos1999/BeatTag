using System;
using System.Threading;

namespace Etiquetador.App.Services;

/// <summary>
/// Una sola ventana de BeatTag a la vez.
///
/// Dos abiertas comparten los mismos archivos de datos: cada una carga fichas, colecciones, cachés
/// y la configuración al arrancar, y la que cierra la última guarda encima de lo que guardó la otra.
/// Visto en el código (hallazgo I6): con fichas-dj.json abierto por otra ventana a mitad de guardar,
/// la segunda arrancaba con 0 fichas y el siguiente guardado escribía un archivo con una sola ficha
/// encima del que tenía cientos. Y es fácil abrir dos sin querer: la pantalla de carga tarda un
/// momento y se vuelve a hacer doble clic.
///
/// Si ya hay una abierta, la nueva le pide que se ponga delante y se cierra sin cargar nada.
/// </summary>
public sealed class InstanciaUnica : IDisposable
{
    /// <summary>Nombre de la aplicación de verdad. Las pruebas usan otros para no chocar con ella.</summary>
    public const string NombrePorDefecto = "BeatTag.InstanciaUnica";

    /// <summary>La de este proceso, si es la primera. La usa la aplicación para ponerse delante.</summary>
    public static InstanciaUnica? Actual { get; set; }

    private readonly Mutex? _cerrojo;
    private readonly bool _esPropio;
    private readonly EventWaitHandle? _senal;
    private readonly string _nombreSenal;
    private volatile bool _cerrada;

    /// <summary>No había ninguna otra abierta: esta es la que sigue.</summary>
    public bool EsLaPrimera { get; }

    /// <summary>Otra instancia ha intentado abrirse. Llega desde un hilo aparte.</summary>
    public event Action? OtraQuiereAbrir;

    private InstanciaUnica(string nombre)
    {
        _nombreSenal = nombre + ".Mostrar";
        try
        {
            _cerrojo = new Mutex(initiallyOwned: true, nombre, out var creada);
            _esPropio = creada;
            EsLaPrimera = creada;
        }
        catch
        {
            // Si no se puede saber, se deja abrir: impedir abrir la aplicación sería peor que el
            // riesgo que esto evita.
            EsLaPrimera = true;
        }

        // En macOS no hay señales con nombre. Ahí el sistema ya no abre dos veces la misma aplicación
        // desde el Finder; lo que no se puede es traer esta al frente desde la otra.
        if (!EsLaPrimera || !OperatingSystem.IsWindows()) return;
        try
        {
            _senal = new EventWaitHandle(false, EventResetMode.AutoReset, _nombreSenal);
            new Thread(Escuchar) { IsBackground = true, Name = "BeatTag: otra instancia" }.Start();
        }
        catch
        {
            _senal = null;   // sin señal, la otra instancia se cierra igual; solo no puede traer esta delante
        }
    }

    public static InstanciaUnica Adquirir(string nombre = NombrePorDefecto) => new(nombre);

    private void Escuchar()
    {
        while (!_cerrada)
        {
            try
            {
                if (_senal!.WaitOne(500) && !_cerrada) OtraQuiereAbrir?.Invoke();
            }
            catch { return; }
        }
    }

    /// <summary>
    /// Desde la que sobra: pide a la que ya está abierta que se ponga delante. Devuelve si se le pudo
    /// avisar.
    /// </summary>
    public bool AvisarALaPrimera()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var s = EventWaitHandle.OpenExisting(_nombreSenal);
            return s.Set();
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _cerrada = true;
        try { _senal?.Dispose(); } catch { }
        if (_cerrojo == null) return;
        try { if (_esPropio) _cerrojo.ReleaseMutex(); } catch { /* otro hilo: se libera al cerrar el proceso */ }
        try { _cerrojo.Dispose(); } catch { }
    }
}
