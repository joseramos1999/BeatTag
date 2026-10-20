using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.App.Views;
using Etiquetador.Core;

// Este espacio de nombres (solo esta clase) se publica en el de Avalonia, como StringConverters: así
// cualquier vista escribe {x:Static AccionesCancion.BuscarCompatibles} sin declarar un prefijo en su
// raíz, y añadir la opción a una tabla es UNA línea en su menú. Avalonia no admite declarar el prefijo
// en el propio MenuItem.
[assembly: Avalonia.Metadata.XmlnsDefinition("https://github.com/avaloniaui", "Etiquetador.App.Acciones")]

namespace Etiquetador.App.Acciones;

/// <summary>
/// «Buscar compatibles» y «Ver sus versiones» desde el menú contextual de cualquier tabla con
/// canciones: Biblioteca, Colecciones, Ficha DJ, Tendencias, Playlists y la tabla de mezcla del
/// Asistente.
///
/// Son comandos estáticos para que cada tabla los añada con una sola línea en su menú, sin tocar su
/// ViewModel: <c>Command="{x:Static AccionesCancion.BuscarCompatibles}" CommandParameter="{Binding SelectedRow}"</c>.
/// La fila puede ser una canción de la biblioteca, cualquier fila con archivo (<see cref="IFilaConArchivo"/>)
/// o una fila de Tendencias o Playlists; si no hay archivo (una canción de la lista que no tienes), la
/// opción sale desactivada.
/// </summary>
public static class AccionesCancion
{
    public static ICommand BuscarCompatibles { get; } =
        new AsyncRelayCommand<object?>(fila => AbrirCompatiblesAsync(RutaDe(fila)), fila => RutaDe(fila).Length > 0);

    public static ICommand VerVersiones { get; } =
        new AsyncRelayCommand<object?>(fila => AbrirVersionesAsync(RutaDe(fila)), fila => RutaDe(fila).Length > 0);

    /// <summary>
    /// «Editar etiquetas de esta canción» para las tablas cuyo ViewModel no tiene su propio comando
    /// (Colecciones, Tendencias, Playlists y su tabla del orden). Sale desactivada sin archivo (una
    /// canción de la lista que no está en la biblioteca) y mientras haya un proceso en marcha: saltar al
    /// Editor no llegaría a abrirlo. Antes solo miraba el archivo, y con Tendencias o Playlists
    /// trabajando el clic no hacía nada y no decía por qué; el rótulo emergente de la opción lo dice.
    /// </summary>
    public static ICommand EditarEtiquetas => _editarEtiquetas;

    private static readonly RelayCommand<object?> _editarEtiquetas =
        new(fila => Editar(RutaDe(fila)), fila => PuedeEditar(RutaDe(fila), AplicacionOcupada()));

    /// <summary>Si se puede abrir una fila en el Editor. Puro, para probarlo.</summary>
    internal static bool PuedeEditar(string ruta, bool ocupada) => ruta.Length > 0 && !ocupada;

    /// <summary>
    /// La aplicación ha empezado o terminado un proceso largo: las opciones que dependen de ello se
    /// vuelven a evaluar (un menú abierto en ese momento se pone al día). Lo llama MainViewModel.
    /// </summary>
    public static void OcupacionCambiada() => _editarEtiquetas.NotifyCanExecuteChanged();

    /// <summary>Hay un proceso largo en marcha en alguna página (el resto de la aplicación está bloqueado).</summary>
    private static bool AplicacionOcupada()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainViewModel { Busy: true } };

    // --- El orden del menú contextual ---
    //
    // Hallazgo de la revisión de la tanda 3: la misma canción ofrecía menús distintos según la pestaña
    // («Abrir carpeta contenedora» salía en segunda, cuarta o última posición, y Colecciones,
    // Tendencias y Playlists no tenían «Editar»). Todas las tablas con canciones siguen este orden, en
    // grupos separados: escuchar y editar · lo que busca a partir de la canción · lo que toca el
    // archivo · lo propio de las listas. Cada tabla pone solo las opciones que le corresponden.

    public const string TextoEscuchar = "Escuchar / Parar";
    public const string TextoEditar = "Editar etiquetas de esta canción";
    public const string TextoCompatibles = "Buscar compatibles…";
    public const string TextoVersiones = "Ver sus versiones…";
    public const string TextoCarpeta = "Abrir carpeta contenedora";
    public const string TextoPapelera = "Enviar a la papelera";

    /// <summary>El orden común, por el principio de cada rótulo (los de ordenar y papelera llevan cifras o avisos detrás).</summary>
    private static readonly string[] OrdenComun =
    {
        TextoEscuchar, TextoEditar, TextoCompatibles, TextoVersiones, "Ordenar para mezclar", "Empezar por esta",
        TextoCarpeta, TextoPapelera, "Añadir a pendientes", "Buscar en mi pool",
    };

    /// <summary>
    /// La posición de una opción en el orden común, o -1 si no es de las comunes. «Ordenar la
    /// selección» (Biblioteca) y «Empezar por esta» (la tabla de mezcla) ocupan el mismo puesto.
    /// </summary>
    public static int Puesto(string rotulo)
    {
        for (var i = 0; i < OrdenComun.Length; i++)
            if (rotulo.StartsWith(OrdenComun[i], System.StringComparison.Ordinal))
                return OrdenComun[i] == "Empezar por esta" ? i - 1 : i;
        return -1;
    }

    private static void Editar(string ruta)
    {
        if (ruta.Length == 0 || !Contexto(out var ventana, out var engine)) return;
        // Con otra página trabajando, el resto está bloqueado: saltar al Editor no llegaría a abrirlo y
        // dejaría elegida en él otra canción.
        if (ventana.DataContext is MainViewModel { Busy: true }) return;
        engine.RequestEdit(ruta);
    }

    /// <summary>La ruta del archivo de una fila de cualquier tabla, o "" si no tiene.</summary>
    internal static string RutaDe(object? fila) => fila switch
    {
        Track t => t.FilePath,
        IFilaConArchivo f => f.RutaArchivo,
        TrendRow r => r.FilePath,
        string s => s,
        _ => "",
    };

    public static async Task AbrirCompatiblesAsync(string ruta)
    {
        if (!Contexto(out var ventana, out var engine)) return;
        var entorno = EntornoCanciones.De(engine);
        await CompatiblesDialog.MostrarAsync(ventana, new CompatiblesViewModel(entorno, entorno.Buscar(ruta)));
    }

    public static async Task AbrirVersionesAsync(string ruta)
    {
        if (!Contexto(out var ventana, out var engine)) return;
        var entorno = EntornoCanciones.De(engine);
        await VersionesDialog.MostrarAsync(ventana, new VersionesViewModel(entorno, entorno.Buscar(ruta)));
    }

    /// <summary>«Ordenar para mezclar» sobre unas canciones elegidas a mano (la selección de la Biblioteca).</summary>
    public static async Task AbrirOrdenAsync(IEnumerable<Track> seleccion, string nombreSugerido)
    {
        if (!Contexto(out var ventana, out var engine)) return;
        var vm = new MezclaSeleccionViewModel(EntornoCanciones.De(engine), seleccion.ToList(), nombreSugerido);
        await MezclaDialog.MostrarAsync(ventana, vm);
    }

    /// <summary>
    /// La ventana principal (dueña de los diálogos) y el motor que cuelga de ella. Se sacan de la
    /// ventana y no de cada ViewModel porque estos comandos los usan tablas de páginas distintas.
    /// </summary>
    private static bool Contexto(out Window ventana, out AppEngine engine)
    {
        ventana = null!;
        engine = null!;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
            return false;
        var motor = (w.DataContext as MainViewModel)?.Engine ?? AppEngine.Current;
        if (motor == null) return false;
        ventana = w;
        engine = motor;
        return true;
    }
}
