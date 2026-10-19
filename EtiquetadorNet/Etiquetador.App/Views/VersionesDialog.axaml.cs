using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

/// <summary>«Ver sus versiones»: todas las piezas que tienes de una canción.</summary>
public partial class VersionesDialog : Window
{
    public VersionesDialog()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Versiones");
    }

    /// <summary>Abre el diálogo y busca las versiones (el índice se prepara fuera del hilo de la interfaz).</summary>
    public static async Task MostrarAsync(Window owner, VersionesViewModel vm)
    {
        var dlg = new VersionesDialog { DataContext = vm };
        // La columna «Canción» solo dice algo cuando la consultada es un mashup.
        dlg.ColumnaCancion(vm.EsMashup);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VersionesViewModel.EsMashup)) dlg.ColumnaCancion(vm.EsMashup);
        };
        dlg.Closed += (_, _) => vm.AlCerrar();
        dlg.Opened += async (_, _) =>
        {
            try { await vm.CargarAsync(); }
            catch (System.Exception ex) { vm.Status = "No se pudieron buscar las versiones: " + ex.Message; }
        };
        await dlg.ShowDialog(owner);
    }

    private void ColumnaCancion(bool visible)
    {
        if (Tabla.Columns.Count > 1) Tabla.Columns[1].IsVisible = visible;
    }

    // Doble clic en una fila: escucharla. En la cabecera, ajustar el ancho de la columna.
    private void Tabla_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e)) return;
        if (DataContext is VersionesViewModel vm && e.Source is Control c && c.FindAncestorOfType<DataGridRow>() != null)
            vm.EscucharCommand.Execute(null);
    }

    private void Cerrar_Click(object? sender, RoutedEventArgs e) => Close();
}
