using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class IdentifyView : UserControl
{
    public IdentifyView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "ComprobarAudio");
    }

    private void Check_Click(object? sender, RoutedEventArgs e) => _ = PreguntarYComprobar(force: false);
    private void CheckAll_Click(object? sender, RoutedEventArgs e) => _ = PreguntarYComprobar(force: true);

    /// <summary>
    /// Confirma ANTES de empezar, diciendo cuántas consultas van a salir y qué cuestan.
    ///
    /// No es una formalidad. Esto es lo único de la aplicación que gasta dinero del usuario y que
    /// además saca audio de su ordenador; arrancar una pasada de doce mil canciones porque alguien
    /// pulsó un botón sin saber qué implicaba sería indefendible. Se dice el número exacto, porque
    /// el número exacto se puede calcular. El texto lo compone el ViewModel
    /// (<see cref="IdentifyViewModel.AvisoAntesDeConsultar"/>), el mismo que usa el reanálisis.
    ///
    /// La lista se calcula en segundo plano UNA vez y es la que se consulta al aceptar.
    /// </summary>
    private async System.Threading.Tasks.Task PreguntarYComprobar(bool force)
    {
        if (DataContext is not IdentifyViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (!vm.HayAlgunMotor) { vm.Status = vm.MotoresInfo; return; }

        var pendientes = await vm.PrepararComprobacionAsync(force);
        if (pendientes == null) return;   // ya había una preparación o una comprobación en marcha
        if (pendientes.Count == 0)
        {
            await vm.RecomponerAsync();
            vm.Status = "No hay nada nuevo que comprobar: todas tienen ya su respuesta guardada.";
            return;
        }

        var aviso = IdentifyViewModel.AvisoAntesDeConsultar(pendientes.Count, vm.PagoEfectivo,
                                                            vm.MaxConsultasPago, force, otroFragmento: false);
        if (aviso != null && !await ConfirmDialog.AskAsync(owner, aviso.Titulo, aviso.Cuerpo, aviso.Nota, aviso.Boton))
            return;

        await vm.RunAsync(force, pendientes: pendientes);
    }

    private void Reanalyze_Click(object? sender, RoutedEventArgs e) => _ = PreguntarYReanalizar();

    /// <summary>
    /// «Reanalizar con otro fragmento» desde el clic derecho. Con el motor de pago puede gastar una
    /// consulta por fila y enviar audio, así que pide la misma confirmación que «Comprobar»; sin
    /// él, solo si son muchas filas, por el tiempo.
    /// </summary>
    private async System.Threading.Tasks.Task PreguntarYReanalizar()
    {
        if (DataContext is not IdentifyViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (vm.IsBusy) { vm.Status = "Hay una comprobación en curso."; return; }
        if (!vm.HayAlgunMotor) { vm.Status = vm.MotoresInfo; return; }

        var (filas, aviso) = vm.PrepararReanalisis();
        if (filas.Count == 0) { vm.Status = "Selecciona antes alguna canción de la lista."; return; }

        if (aviso != null && !await ConfirmDialog.AskAsync(owner, aviso.Titulo, aviso.Cuerpo, aviso.Nota, aviso.Boton))
            return;

        await vm.ReanalizarOtroFragmentoAsync(filas);
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;

        // Pulsar con el derecho sobre una fila que YA forma parte de la selección no debe deshacerla:
        // si no, abrir el menú para actuar sobre un bloque lo reduce a una sola fila.
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item }
            && !grid.SelectedItems.Contains(item))
            grid.SelectedItem = item;
    }

    private void Grid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not IdentifyViewModel vm) return;
        vm.SetSelection(grid.SelectedItems.OfType<IdentifyRow>());
    }
}
