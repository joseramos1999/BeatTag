using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Etiquetador.App.Views;

public partial class QualityView : UserControl
{
    public QualityView() => InitializeComponent();

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        if (e.Source is Control c && c.FindAncestorOfType<DataGridRow>() is { DataContext: { } item })
            grid.SelectedItem = item;
    }

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);
}
