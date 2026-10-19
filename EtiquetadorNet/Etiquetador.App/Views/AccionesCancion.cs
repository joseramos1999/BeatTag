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
