using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Etiquetador.App.Views;

public partial class EditorView : UserControl
{
    public EditorView() => InitializeComponent();

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    private void ExpandAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), true);
    private void CollapseAll_Click(object? sender, RoutedEventArgs e) => GridBehaviors.SetAllGroups(this.FindDescendantOfType<DataGrid>(), false);
}
