using System;
using System.Globalization;
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
    /// el número exacto se puede calcular.
    /// </summary>
    private async System.Threading.Tasks.Task PreguntarYComprobar(bool force)
    {
        if (DataContext is not IdentifyViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        if (!vm.HayAlgunMotor) { vm.Status = vm.MotoresInfo; return; }

        var pendientes = vm.PorConsultar(force);
        if (pendientes.Count == 0)
        {
            await vm.RecomponerAsync();
            vm.Status = "No hay nada nuevo que comprobar: todas tienen ya su respuesta guardada.";
            return;
        }

        // Cuántas acabarán en el motor de pago NO se sabe de antemano: depende de cuántas resuelva
        // el gratuito, que es justo lo que se va a averiguar. Así que se da el TECHO -el caso peor,
        // si el gratuito no acertara ni una- acotado por el tope que el propio usuario ha puesto.
        // Prometer una cifra exacta que no se puede calcular sería peor que dar el máximo.
        var dePago = vm.UsarPago && vm.HayPago ? Math.Min(pendientes.Count, vm.MaxConsultasPago) : 0;
        var coste = dePago * 5.0 / 1000.0;

        // Qué sale del ordenador NO es lo mismo en los dos motores, y decirlo mal es lo peor que
        // puede hacer un aviso de privacidad. AcoustID recibe una HUELLA -una firma numérica que se
        // calcula aquí y de la que no se puede reconstruir el sonido-; AudD recibe AUDIO de verdad.
        var cuerpo = dePago == 0
            ? "De cada una se calcula aquí, en tu ordenador, una huella digital del sonido y se envía "
              + "SOLO esa huella a AcoustID. El audio no sale de tu equipo."
            : "De cada una se calcula aquí una huella digital del sonido y se envía a AcoustID, que no "
              + "recibe audio. Únicamente de las que AcoustID no reconozca se enviará además un "
              + "FRAGMENTO DE AUDIO de 12 segundos a AudD, que es lo que le permite reconocerlas.";
        if (force) cuerpo += " Se preguntará también por las que ya tenían respuesta guardada.";

        var nota = dePago == 0
            ? "Solo se usará AcoustID, que es gratuito e ilimitado: esta pasada no cuesta nada. "
            : $"Primero AcoustID, que es gratis. Solo lo que no reconozca irá a AudD, con un máximo de "
            + $"{dePago} consultas de pago: como mucho "
            + coste.ToString("0.00", CultureInfo.CurrentCulture) + " $, y menos cuantas más resuelva el gratuito. "
            + "Las primeras 300 de tu cuenta de AudD no se cobran. ";

        nota += "El recorte se escribe en una carpeta temporal y se borra en cuanto se recibe la "
              + "respuesta. Los archivos no se modifican: esta pestaña solo informa, y ninguna canción "
              + "se pregunta dos veces.";

        var ok = await ConfirmDialog.AskAsync(owner,
            $"Se van a comprobar {pendientes.Count} canciones", cuerpo, nota, "Comprobar");

        if (ok) await vm.RunAsync(force);
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
