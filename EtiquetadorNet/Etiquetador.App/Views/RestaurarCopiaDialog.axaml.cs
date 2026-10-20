using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Copias;

namespace Etiquetador.App.Views;

/// <summary>Una raíz de la música de la copia, con la carpeta de este equipo que le corresponde.</summary>
public sealed partial class RaizRestaurarFila : ObservableObject
{
    public string Origen { get; init; } = "";
    public string RutasTexto { get; init; } = "";
    public string Motivo { get; init; } = "";

    /// <summary>Dónde está ahora. Vacío: la ruta se conserva como está en la copia.</summary>
    [ObservableProperty] private string _destino = "";
}

/// <summary>
/// «Restaurar copia…» (equipo «volumen_copias», tanda 4, T15): enseña qué archivos de datos se van a
/// sustituir y deja confirmar dónde está ahora la música de cada raíz de la copia. No restaura nada:
/// devuelve las ubicaciones confirmadas, o null si se cancela.
/// </summary>
public partial class RestaurarCopiaDialog : Window
{
    private readonly ObservableCollection<RaizRestaurarFila> _raices = new();
    private readonly LecturaCopia? _lectura;
    private IReadOnlyList<CambioRaiz>? _resultado;

    public RestaurarCopiaDialog() => InitializeComponent();

    private RestaurarCopiaDialog(LecturaCopia lectura) : this()
    {
        _lectura = lectura;
        var m = lectura.Contenido.Manifiesto;
        DescripcionText.Text = TextosCopia.Describir(m)
            + (m.Sistema.Length > 0 && m.Sistema != CopiaSeguridad.Sistema()
                ? $" Este equipo es {CopiaSeguridad.Sistema()}: revisa abajo la ubicación de la música."
                : "");
        Archivos.ItemsSource = lectura.Filas;

        foreach (var r in lectura.Raices)
            _raices.Add(new RaizRestaurarFila
            {
                Origen = r.Origen, RutasTexto = TextosCopia.Rutas(r.Rutas), Motivo = r.Motivo,
                // Lo que existe aquí tal cual no cambia: se deja la casilla vacía.
                Destino = string.Equals(r.Propuesta, r.Origen, System.StringComparison.OrdinalIgnoreCase) ? "" : r.Propuesta,
            });
        Raices.ItemsSource = _raices;
        RaicesPanel.IsVisible = _raices.Count > 0;

        var descartados = lectura.Contenido.Descartados;
        if (descartados.Count > 0)
        {
            DescartadosText.Text = (descartados.Count == 1 ? "No se restaura: " : "No se restauran: ") + string.Join("; ", descartados) + ".";
            DescartadosText.IsVisible = true;
        }
    }

    /// <summary>Muestra la vista previa; devuelve las ubicaciones confirmadas, o null si se cancela.</summary>
    public static async Task<IReadOnlyList<CambioRaiz>?> MostrarAsync(Window owner, LecturaCopia lectura)
    {
        var dlg = new RestaurarCopiaDialog(lectura);
        await dlg.ShowDialog(owner);
        return dlg._resultado;
    }

    private async void ElegirCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RaizRestaurarFila fila }) return;
        var carpetas = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Carpeta de este equipo que corresponde a «{fila.Origen}»",
            AllowMultiple = false,
        });
        var ruta = carpetas.Count > 0 ? carpetas[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(ruta)) fila.Destino = ruta;
    }

    private void Cancelar_Click(object? sender, RoutedEventArgs e) { _resultado = null; Close(); }

    // La confirmación sale encima de este diálogo: si se cancela, lo escrito en las ubicaciones sigue ahí.
    private async void Continuar_Click(object? sender, RoutedEventArgs e)
    {
        if (_lectura == null) return;
        var cambios = _raices.Select(r => new CambioRaiz(r.Origen, (r.Destino ?? "").Trim())).ToList();
        var error = MapaRaices.Validar(cambios);
        ErrorText.Text = error;
        ErrorText.IsVisible = error.Length > 0;
        if (error.Length > 0) return;

        var mapa = new MapaRaices(cambios);
        var rutas = await SettingsViewModel.ContarRutasQueCambianAsync(_lectura, mapa);
        var (titulo, cuerpo, nota) = TextosCopia.Confirmacion(_lectura.Filas, mapa.Count, rutas);
        if (!await ConfirmDialog.AskAsync(this, titulo, cuerpo, nota, "Restaurar al volver a abrir")) return;
        _resultado = cambios;
        Close();
    }
}
