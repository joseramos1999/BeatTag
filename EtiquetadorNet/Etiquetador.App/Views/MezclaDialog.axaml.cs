using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

/// <summary>«Ordenar para mezclar la selección», desde la Biblioteca.</summary>
public partial class MezclaDialog : Window
{
    public MezclaDialog()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "MezclaSeleccion");
    }

    /// <summary>Abre el diálogo y ordena la selección (fuera del hilo de la interfaz).</summary>
    public static async Task MostrarAsync(Window owner, MezclaSeleccionViewModel vm)
    {
        var dlg = new MezclaDialog { DataContext = vm };
        dlg.Closed += (_, _) => vm.AlCerrar();
        dlg.Opened += async (_, _) =>
        {
            try { await vm.OrdenarAsync(); }
            catch (System.Exception ex) { vm.Status = "No se pudo ordenar: " + ex.Message; }
        };
        await dlg.ShowDialog(owner);
    }

    // Doble clic en una fila: escucharla. En la cabecera, ajustar el ancho de la columna.
    private void Tabla_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e)) return;
        if (DataContext is MezclaSeleccionViewModel vm && e.Source is Control c && c.FindAncestorOfType<DataGridRow>() != null)
            vm.EscucharCommand.Execute(null);
    }

    private async void Exportar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MezclaSeleccionViewModel vm) return;
        var archivo = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar lista ordenada",
            SuggestedFileName = TextUtils.Sanitize(vm.NombreSugerido),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Lista de reproducción") { Patterns = new[] { "*.m3u8" } } },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.ExportarM3u(destino);
    }

    private void Cerrar_Click(object? sender, RoutedEventArgs e) => Close();
}
