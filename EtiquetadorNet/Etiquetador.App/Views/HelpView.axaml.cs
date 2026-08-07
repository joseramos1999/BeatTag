using Avalonia.Controls;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
        VersionText.Text = $"Versión {AppInfo.Version}";
    }
}
