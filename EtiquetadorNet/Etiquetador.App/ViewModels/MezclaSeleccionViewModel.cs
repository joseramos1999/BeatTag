using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// «Ordenar para mezclar la selección», desde la Biblioteca: el mismo orden y la misma exportación que
/// la herramienta del Asistente, pero sobre las canciones elegidas a mano en vez de una colección.
///
/// Quien no usa colecciones (crearlas es un paso más) también tiene así cómo ordenar una sesión:
/// selecciona las canciones en la tabla y las ordena.
/// </summary>
public sealed partial class MezclaSeleccionViewModel : ObservableObject
{
    private readonly EntornoCanciones _entorno;
    private readonly IReadOnlyList<(Track Track, FichaDj? Ficha)> _canciones;
    private readonly HashSet<string> _escuchadas = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<PasoFila> Pasos { get; } = new();

    [ObservableProperty] private PasoFila? _seleccionada;
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _ocupado;

    /// <summary>Nombre que se propone para la lista M3U8.</summary>
    public string NombreSugerido { get; }

    public MezclaSeleccionViewModel(EntornoCanciones entorno, IEnumerable<Track> seleccion, string nombreSugerido = "Sesion (ordenada)")
    {
        _entorno = entorno;
        // Una sola vez cada archivo: la misma canción seleccionada dos veces no se pincha dos veces.
        _canciones = seleccion.GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
                              .Select(g => (g.First(), entorno.FichaDe(g.First())))
                              .ToList();
        NombreSugerido = nombreSugerido;
    }

    public int Canciones => _canciones.Count;

    /// <summary>
    /// Por qué no se puede ordenar esta selección, o "" si se puede. Lo dice la Biblioteca antes de
    /// abrir el diálogo, en su barra de estado.
    /// </summary>
    internal static string MotivoParaNoOrdenar(int seleccionadas)
        // Sin nombrar teclas: para seleccionar varias se usa Ctrl en Windows y Cmd en macOS.
        => seleccionadas < 2 ? "Selecciona al menos dos canciones para ordenarlas."
         : seleccionadas > MezclaViewModel.Maximo
            ? $"Has seleccionado {seleccionadas} canciones; para ordenar una sesión el máximo es {MezclaViewModel.Maximo}."
         : "";

    /// <summary>Ordena la selección; con <paramref name="primera"/>, empezando por esa. Fuera del hilo de la interfaz.</summary>
    public async Task OrdenarAsync(Track? primera = null)
    {
        Ocupado = true;
        Status = "Ordenando…";
        IReadOnlyList<PasoMezcla> orden;
        try { orden = await Task.Run(() => OrdenMezcla.Ordenar(_canciones, primera)); }
        finally { Ocupado = false; }

        Pasos.Clear();
        foreach (var p in orden) Pasos.Add(new PasoFila(p));
        var r = OrdenMezcla.Resumir(orden);
        Resumen = r.Texto;
        Status = (primera != null ? $"Ordenada empezando por «{EntornoCanciones.Nombre(primera)}». " : "")
                 + (r.FaltaTonalidad ? "Muchas canciones no tienen tonalidad en las etiquetas: impórtala de rekordbox en Biblioteca." : "");
    }

    [RelayCommand]
    private async Task EmpezarPorEstaAsync()
    {
        if (Seleccionada is not { } f) { Status = "Selecciona antes la canción con la que quieres empezar."; return; }
        await OrdenarAsync(f.Paso.Track);
    }

    [RelayCommand]
    private void Escuchar()
    {
        if (Seleccionada is not { } f) { Status = "Selecciona antes una canción."; return; }
        _escuchadas.Add(f.FilePath);
        Status = _entorno.Escuchar(f.FilePath);
    }

    [RelayCommand]
    private async Task AbrirCarpetaAsync()
    {
        if (Seleccionada is { } f) await Shell.OpenContainingFolderAsync(f.FilePath);
    }

    public void ExportarM3u(string destino)
    {
        if (Pasos.Count == 0) { Status = "No hay nada que guardar."; return; }
        Status = MezclaViewModel.Exportar(destino, Pasos.Select(p => p.Paso));
    }

    /// <summary>Al cerrar: si suena algo que se puso desde aquí, se para.</summary>
    public void AlCerrar() => _entorno.PararSiSuena(_escuchadas);
}
