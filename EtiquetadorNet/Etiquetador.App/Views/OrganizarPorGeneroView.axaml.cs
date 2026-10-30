using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class OrganizarPorGeneroView : UserControl
{
    public OrganizarPorGeneroView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "OrganizarPorGenero");
    }

    // Al volver a la página se mira si la vista previa sigue al día (las carpetas con ajustes de
    // Duplicados o de Volumen pueden haber cambiado en otra). El shell cambia IsVisible al cambiar de página.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == IsVisibleProperty || change.Property == DataContextProperty) && DataContext is IPaginaVisible p)
            p.Visible = IsVisible;
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private async void ElegirCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrganizarPorGeneroViewModel vm) return;
        var ruta = await ElegirAsync("Elige la carpeta de la biblioteca donde se crean las carpetas de los géneros");
        if (ruta != null) vm.ElegirCarpeta(ruta);
    }

    private async Task<string?> ElegirAsync(string titulo)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var carpetas = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = titulo, AllowMultiple = false });
        var ruta = carpetas.Count > 0 ? carpetas[0].TryGetLocalPath() : null;
        return string.IsNullOrEmpty(ruta) ? null : ruta;
    }

    // Mueve archivos de toda la biblioteca: se confirma con las cifras de la vista previa y con lo que
    // pasa con rekordbox, los demás programas de DJ, las listas exportadas y Enriquecer.
    private async void Organizar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OrganizarPorGeneroViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!vm.ComprobarVistaPrevia() || vm.Plan is not { } plan) return;   // desfasada: lo dice el aviso
        var (titulo, cuerpo, nota) = OrganizarPorGeneroViewModel.TextosConfirmacion(plan);
        var ok = await ConfirmDialog.AskAsync(owner, titulo, cuerpo, nota, "Organizar");
        if (ok) await vm.OrganizarAsync();
    }
}
