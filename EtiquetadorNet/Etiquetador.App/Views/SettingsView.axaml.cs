using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Etiquetador.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => EngancharAutoScroll();
        EngancharAutoScroll();
    }

    // Cada vez que se vuelve a Ajustes se recuentan los renombrados reparables: aplicar o deshacer
    // en Enriquecer los cambia, y la página se crea una sola vez.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && DataContext is ViewModels.SettingsViewModel vm)
            _ = vm.ActualizarRenombradosAsync();
    }

    // Vaciar la caché no toca la música, pero sale caro después: se dice qué se borra y cuánto. La
    // página queda ocupada desde el clic (recuento incluido), así que un segundo clic no hace nada.
    private async void ClearCache_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm || vm.IsBusy) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        await vm.VaciarCacheAsync(contenido =>
        {
            var (cuerpo, nota) = ViewModels.SettingsViewModel.TextosVaciarCache(contenido);
            return ConfirmDialog.AskAsync(owner, "Vaciar la caché", cuerpo, nota, "Vaciar caché");
        });
    }

    // Olvidar aplicadas no se puede deshacer y afecta a todas de golpe.
    private async void ForgetApplied_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        if (vm.AplicadasCount == 0) { vm.Status = "No hay ninguna canción marcada como aplicada."; return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var n = vm.AplicadasCount;
        var ok = await ConfirmDialog.AskAsync(owner,
            $"Olvidar las {n:N0} canciones aplicadas",
            $"Las canciones que ya aplicaste no vuelven a proponerse al analizar. Si olvidas esta marca, las {n:N0} "
            + "volverán a salir en Enriquecer en el próximo análisis, mezcladas con las pendientes.",
            "No se toca ningún archivo, pero no se puede deshacer. Para recuperar solo las de una tirada concreta, "
            + "es mejor usar «Deshacer última» en Enriquecer.",
            "Olvidar aplicadas");
        if (ok) vm.OlvidarAplicadas();
    }

    // Credenciales cifradas en otro equipo: se ofrece descartarlas, diciendo qué se pierde.
    private async void ForgetUnreadableCredentials_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var nombres = vm.CredencialesIlegibles;
        if (nombres.Length == 0) { vm.OlvidarCredencialesIlegibles(); return; }

        var ok = await ConfirmDialog.AskAsync(owner,
            "Olvidar credenciales ilegibles",
            $"Las credenciales de {nombres} no se pueden descifrar en este equipo. Si las olvidas, se borrarán "
            + "de la configuración y tendrás que volver a introducirlas en «Claves de API».",
            "Mientras no las olvides, se conservan cifradas tal cual: siguen sirviendo si vuelves a abrir esta "
            + "configuración en el equipo y con el usuario donde se guardaron.",
            "Olvidar");
        if (ok) vm.OlvidarCredencialesIlegibles();
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

    // --- Pendientes: búsqueda del pool (equipo «listas», tanda 3) ---

    // La plantilla se guarda al salir de la casilla, no con cada tecla.
    private void PoolBusqueda_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm) vm.GuardarPoolBusqueda();
    }
}
