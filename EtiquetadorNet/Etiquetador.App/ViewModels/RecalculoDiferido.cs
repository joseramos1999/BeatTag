using System;
using System.Collections;
using System.Threading.Tasks;
using Avalonia.Collections;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Una página que quiere saber si está a la vista. Lo avisa su vista: el shell crea cada página la
/// primera vez que se abre y luego solo cambia cuál es visible.
/// </summary>
public interface IPaginaVisible
{
    bool Visible { get; set; }
}

/// <summary>
/// Recalcular una página solo si está a la vista; si está oculta, se apunta que quedó pendiente y se
/// recalcula una vez al mostrarse.
///
/// Las páginas que se rehacen con cada cambio de la biblioteca lo hacían aunque nadie las mirase:
/// renombrar 3 archivos en el Asistente lanza un escaneo completo (entre 1,2 y 7,8 s en los registros
/// reales) y, tras él, Ficha DJ, Colecciones y Estadísticas volvían a construir sus filas para 13.500
/// canciones en el hilo de la interfaz. Y cada tecla de energía pulsada en Ficha DJ recalculaba la
/// colección abierta en Colecciones, aunque estuviera oculta. Ahora eso se hace una sola vez, al volver
/// a la página.
/// </summary>
public sealed class RecalculoDiferido
{
    private readonly Action _recalcular;
    private readonly Action? _refrescar;

    /// <param name="recalcular">Rehacer la página entera (sus filas, su vista).</param>
    /// <param name="refrescar">
    /// Un retoque ligero que conserva las filas, la selección y la posición de la tabla (por ejemplo,
    /// releer las fichas). Sin él, <see cref="PedirRefresco"/> rehace la página entera.
    /// </param>
    public RecalculoDiferido(Action recalcular, Action? refrescar = null)
    {
        _recalcular = recalcular;
        _refrescar = refrescar;
    }

    public bool Visible { get; private set; }

    /// <summary>Hay un recálculo por hacer. La primera vez que se muestra la página, siempre.</summary>
    public bool Pendiente { get; private set; } = true;

    /// <summary>Hay un retoque ligero por hacer al mostrarse (y ningún recálculo entero, que ya lo incluiría).</summary>
    public bool RefrescoPendiente { get; private set; }

    /// <summary>Hay que recalcular: ahora si se ve; si no, al mostrarse.</summary>
    public void Pedir()
    {
        if (Visible) Ejecutar();
        else Pendiente = true;
    }

    /// <summary>
    /// Basta con el retoque ligero: ahora si se ve; si no, al mostrarse.
    ///
    /// Visto en la revisión: aceptar fichas en el Asistente con Ficha DJ oculta dejaba la página
    /// marcada para recalcular entera, y al volver se rehacían las 15.000 filas, se sustituía la vista
    /// y se perdía la selección. Antes de diferir los recálculos solo se releían las fichas y la tabla
    /// se quedaba como estaba; ahora vuelve a ser así. Si además cambió la biblioteca, el recálculo
    /// entero ya relee las fichas y el retoque sobra.
    /// </summary>
    public void PedirRefresco()
    {
        if (_refrescar == null) { Pedir(); return; }
        if (Visible) _refrescar();
        else if (!Pendiente) RefrescoPendiente = true;
    }

    public void CambiarVisible(bool visible)
    {
        if (Visible == visible) return;
        Visible = visible;
        if (!visible) return;
        if (Pendiente) Ejecutar();
        else if (RefrescoPendiente)
        {
            RefrescoPendiente = false;
            _refrescar?.Invoke();
        }
    }

    private void Ejecutar()
    {
        Pendiente = false;
        RefrescoPendiente = false;
        _recalcular();
    }
}

/// <summary>
/// Un índice que se construye fuera del hilo de la interfaz y se reutiliza hasta que se invalida (en
/// la práctica, hasta el siguiente cambio de la biblioteca).
/// </summary>
public sealed class IndiceReutilizable<T> where T : class
{
    // Se guarda la TAREA, no solo el resultado: dos revisiones seguidas mientras se construye esperan
    // a la misma construcción en vez de lanzar dos. Se usa desde el hilo de la interfaz.
    private Task<T>? _tarea;

    /// <summary>Cuántas veces se ha empezado a construir. Para las pruebas.</summary>
    public int Construcciones { get; private set; }

    /// <summary>
    /// Hay un índice construido y vigente: pedirlo es inmediato. Si no, pedirlo cuesta construirlo
    /// (1-2 s con 15.000 canciones) y conviene decir en pantalla que se está trabajando.
    /// </summary>
    public bool Listo => _tarea is { IsCompletedSuccessfully: true };

    /// <summary>
    /// Lo guardado ya no vale. Una construcción que estuviera en marcha termina para quien la esperaba,
    /// pero la siguiente petición construye otra.
    /// </summary>
    public void Invalidar() => _tarea = null;

    /// <summary>
    /// El índice guardado o, si no hay, uno nuevo construido con <paramref name="construir"/> en
    /// Task.Run. Lo que necesite del hilo de la interfaz (una instantánea de la biblioteca) se toma
    /// antes de llamar. Si una construcción falló, la siguiente petición lo vuelve a intentar.
    /// </summary>
    public Task<T> ObtenerAsync(Func<T> construir)
    {
        if (_tarea is { IsFaulted: false, IsCanceled: false } enCurso) return enCurso;
        Construcciones++;
        return _tarea = Task.Run(construir);
    }
}

/// <summary>Trabajo que se lanza sin esperarlo (con «_ =») y que no puede fallar en silencio.</summary>
internal static class SinEsperar
{
    /// <summary>
    /// Ejecuta <paramref name="trabajo"/> y, si falla, se lo pasa a <paramref name="alFallar"/> (para
    /// el registro y la barra de estado). Cancelar no cuenta como fallo.
    ///
    /// Visto en la revisión: la Bandeja lanza su revisión sin esperarla al mostrarse la página o al
    /// cambiar la biblioteca. Si construir el índice de la biblioteca fallaba, la excepción quedaba en
    /// una tarea que nadie miraba y los avisos de la tabla se quedaban como estaban, sin decir nada.
    /// </summary>
    public static async Task Lanzar(Func<Task> trabajo, Action<Exception> alFallar)
    {
        try { await trabajo(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { alFallar(e); }
    }
}

/// <summary>Vistas de tabla que se sustituyen de una vez.</summary>
internal static class VistaTabla
{
    /// <summary>
    /// Una vista nueva sobre <paramref name="filas"/> con el filtro y el orden de la anterior.
    ///
    /// Reponer las filas con Clear y un Add por fila son tantos avisos a la tabla como filas (13.500 en
    /// una biblioteca real), y con un orden por columna activo cada alta además se coloca en su sitio.
    /// En Comprobar audio se midió que esas altas sueltas costaban más que el propio cálculo, y por eso
    /// allí ya se sustituye la vista entera. Una vista nueva es un único aviso. Se conservan el filtro y
    /// el orden que hubiera elegido el usuario.
    /// </summary>
    public static DataGridCollectionView Nueva(IEnumerable filas, DataGridCollectionView? anterior)
    {
        var vista = new DataGridCollectionView(filas);
        if (anterior == null) return vista;
        foreach (var orden in anterior.SortDescriptions) vista.SortDescriptions.Add(orden);
        vista.Filter = anterior.Filter;
        return vista;
    }
}
