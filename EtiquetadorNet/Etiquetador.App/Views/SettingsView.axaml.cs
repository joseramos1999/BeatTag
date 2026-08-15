using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Etiquetador.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => EngancharAutoScroll();
        EngancharAutoScroll();
    }

    // Sin esto hay que arrastrar la barra a mano para ver lo que va pasando, que en una tirada
    // larga es justo lo que uno quiere mirar. Se respeta la casilla: si el usuario está leyendo
    // hacia atrás, no se le mueve la vista.
    private INotifyCollectionChanged? _enganchado;

    private void EngancharAutoScroll()
    {
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        if (ReferenceEquals(_enganchado, vm.Log)) return;

        if (_enganchado != null) _enganchado.CollectionChanged -= AlLlegarLinea;
        _enganchado = vm.Log;
        _enganchado.CollectionChanged += AlLlegarLinea;
    }

    private void AlLlegarLinea(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (this.FindControl<CheckBox>("AutoScroll") is not { IsChecked: true }) return;
        if (this.FindControl<ListBox>("LogList") is not { } lista) return;
        if (DataContext is not ViewModels.SettingsViewModel vm || vm.Log.Count == 0) return;

        try { lista.ScrollIntoView(vm.Log[^1]); } catch { }
    }

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not { } portapapeles) return;

        var sb = new StringBuilder();
        foreach (var l in vm.Log) sb.AppendLine($"{l.Hora}  {l.Message}");

        // Avalonia 12 retiró SetTextAsync: ahora se envuelve en un DataTransfer.
        var datos = new Avalonia.Input.DataTransfer();
        datos.Add(Avalonia.Input.DataTransferItem.CreateText(sb.ToString()));
        await portapapeles.SetDataAsync(datos);

        vm.Status = $"{vm.Log.Count} líneas copiadas al portapapeles.";
    }

    // Limpiar borra archivos del usuario: se dice exactamente qué se va y qué se queda.
    private async void CleanData_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var (resumen, bytes, rutas) = vm.InspeccionarLimpieza();
        if (rutas.Count == 0) { vm.Status = "No hay nada que limpiar."; return; }

        var ok = await ConfirmDialog.AskAsync(owner,
            "Limpiar la carpeta de datos",
            "Se va a borrar:\n" + resumen,
            "NO se tocan los ajustes ni las claves, ni las listas de nombres de artista, ni el historial "
            + "para deshacer (es la única forma de revertir cambios ya aplicados a tus archivos). "
            + "Las cachés se regeneran solas, pero el próximo análisis será más lento porque volverá "
            + "a consultarlo todo.",
            "Limpiar");

        if (ok) vm.LimpiarCarpetaDatos();
    }
}
