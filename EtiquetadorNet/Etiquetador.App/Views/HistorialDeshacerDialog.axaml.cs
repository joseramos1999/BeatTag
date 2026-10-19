using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

/// <summary>
/// «Historial de cambios» (E14/M14): elegir qué ejecución, o qué canciones de ella, deshacer. La
/// lógica vive en <see cref="HistorialDeshacerViewModel"/>; aquí solo se confirma con
/// <see cref="ConfirmDialog"/>, como el resto de acciones que tocan archivos.
/// </summary>
public partial class HistorialDeshacerDialog : Window
{
    public HistorialDeshacerDialog() => InitializeComponent();

    /// <summary>Abre el historial y espera a que se cierre.</summary>
    public static async Task MostrarAsync(Window owner, HistorialDeshacerViewModel vm)
    {
        var dlg = new HistorialDeshacerDialog { DataContext = vm };
        _ = vm.CargarAsync();   // la lectura va fuera del hilo de la interfaz; la ventana se abre ya
        await dlg.ShowDialog(owner);
    }

    private HistorialDeshacerViewModel? Vm => DataContext as HistorialDeshacerViewModel;

    private async void DeshacerEjecucion_Click(object? sender, RoutedEventArgs e) => await DeshacerAsync(soloMarcadas: false);

    private async void DeshacerMarcadas_Click(object? sender, RoutedEventArgs e) => await DeshacerAsync(soloMarcadas: true);

    private async Task DeshacerAsync(bool soloMarcadas)
    {
        if (Vm is not { Ocupado: false } vm) return;
        var plan = await vm.PrepararAsync(soloMarcadas);
        if (plan == null) return;
        if (!plan.Posible) { vm.Estado = plan.Motivo; return; }
        if (!await ConfirmDialog.AskAsync(this, plan.Titulo, plan.Cuerpo, plan.Nota, plan.Aceptar)) return;
        await vm.EjecutarAsync(plan);
    }

    private async void DarPorPerdidos_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Ocupado: false, Seleccionada: { } fila } vm) return;
        var c = HistorialDeshacerViewModel.ConfirmacionPerdidos(fila.E);
        if (!await ConfirmDialog.AskAsync(this, c.Titulo, c.Cuerpo, c.Nota, c.Aceptar)) return;
        await vm.DarPorPerdidosAsync();
    }

    private void Cerrar_Click(object? sender, RoutedEventArgs e) => Close();
}
