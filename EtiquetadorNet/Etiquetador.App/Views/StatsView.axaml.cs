using Avalonia;
using Avalonia.Controls;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class StatsView : UserControl
{
    public StatsView() => InitializeComponent();

    // La página solo recalcula a la vista: el shell cambia IsVisible al cambiar de página.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == IsVisibleProperty || change.Property == DataContextProperty) && DataContext is IPaginaVisible p)
            p.Visible = IsVisible;
    }
}
