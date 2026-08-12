using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
        GridBehaviors.EnableWidthMemory(this, "Editor");
    }

    // "Reanalizar…": mismo diálogo que en Enriquecer, pero aquí la propuesta solo RELLENA el
    // formulario; no se escribe nada hasta que el usuario pulse Guardar.
    private async void Reanalyze_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel vm) return;
        if (vm.SelectedTrack is not { } track) { vm.Status = "Selecciona antes una canción."; return; }
        if (TopLevel.GetTopLevel(this) is not Window owner) { vm.Status = "No se pudo abrir la ventana de búsqueda."; return; }

        // Parámetros ORIGINALES, los deducidos del nombre del archivo.
        var pr = Etiquetador.Core.Pipeline.FileNameParser.Parse(track.FileName);
        var artist = pr.FnArtist;
        var title = pr.QTitle.Length > 0 ? pr.QTitle : pr.FnTitle;

        var terms = await SearchDialog.AskAsync(owner, track.FileName, artist, title, vm.SearchServicesFor(track.FilePath));
        if (terms == null) return;   // cancelado
        await vm.ReanalyzeIntoFormAsync(track, terms.Artist, terms.Title, terms.Source);
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);
}
