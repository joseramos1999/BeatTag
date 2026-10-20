using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class LoudnessView : UserControl
{
    public LoudnessView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Volumen");

        // La tabla admite selección múltiple, y la opción excluye la carpeta de CADA fila elegida: el
        // rótulo dice cuántas son al abrir el menú, como «Ordenar para mezclar la selección (N)…».
        if (this.FindControl<DataGrid>("RowsGrid")?.ContextMenu is { } menu)
            menu.Opening += (_, _) =>
            {
                var rotulo = AjusteVolumenSeleccion.RotuloExcluirCarpetas(AjusteVolumenSeleccion.CarpetasDe(Seleccionadas()).Count);
                foreach (var item in menu.Items.OfType<MenuItem>())
                    if (Equals(item.Tag, "ExcluirCarpetas")) item.Header = rotulo;
            };
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    /// <summary>Las filas seleccionadas en la tabla (varias con Ctrl o Mayús).</summary>
    private List<LoudnessRow> Seleccionadas()
        => this.FindControl<DataGrid>("RowsGrid")?.SelectedItems.OfType<LoudnessRow>().ToList() ?? new List<LoudnessRow>();

    // Ajustar el volumen modifica los archivos del usuario: se confirma antes, indicando
    // exactamente cuántos se van a tocar, qué implica y qué queda fuera.
    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoudnessViewModel vm) return;
        await ConfirmarYAjustarAsync(vm, vm.Elegir(), porSeleccion: false);
    }

    // E15: solo lo seleccionado, marcado o no; las excluidas, nunca.
    private async void AjustarSeleccion_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LoudnessViewModel vm) return;
        var sel = Seleccionadas();
        if (sel.Count == 0) { vm.Status = "Elige antes las grabaciones en la tabla."; return; }
        await ConfirmarYAjustarAsync(vm, vm.Elegir(sel), porSeleccion: true);
    }

    private async Task ConfirmarYAjustarAsync(LoudnessViewModel vm, SeleccionAjuste s, bool porSeleccion)
    {
        if (vm.IsBusy) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (s.Ajustar.Count == 0)
        {
            vm.Status = AjusteVolumenSeleccion.TextoNadaQueAjustar(s, porSeleccion, vm.DesviadasNoMp3);
            return;
        }

        // El mismo criterio que usa el ajuste: con el aumento redondeado hacia abajo por el pico.
        var parciales = s.Ajustar.Count(LoudnessViewModel.AumentoParcial);
        var (titulo, cuerpo, nota) = AjusteVolumenSeleccion.TextosConfirmacion(s, porSeleccion, parciales);
        var ok = await ConfirmDialog.AskAsync(owner, titulo, cuerpo, nota, "Ajustar volumen");
        if (ok) await vm.ApplyAsync(s.Ajustar);
    }

    // Excluir no toca ningún archivo y se deshace con «Quitar»: no hace falta confirmación.
    private void ExcluirCanciones_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoudnessViewModel vm) vm.ExcluirCanciones(Seleccionadas());
    }

    private void ExcluirCarpetas_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoudnessViewModel vm) vm.ExcluirCarpetas(Seleccionadas());
    }

    // El clic derecho selecciona la fila sin deshacer una selección múltiple, para que el menú actúe
    // sobre lo que se ve.
    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e) => TeclaEnergia.SeleccionarConClicDerecho(sender, e);
}
