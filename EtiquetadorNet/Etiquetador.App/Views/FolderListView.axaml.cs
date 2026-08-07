using Avalonia.Controls;

namespace Etiquetador.App.Views;

/// <summary>Lista de carpetas reutilizable (cabecera con recuento + ✕ por carpeta). Hereda el DataContext del VM.</summary>
public partial class FolderListView : UserControl
{
    public FolderListView() => InitializeComponent();
}
