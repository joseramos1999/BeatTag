using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Etiquetador.App.Views;

/// <summary>Confirmación para las acciones que modifican archivos del usuario.</summary>
public partial class ConfirmDialog : Window
{
    private bool _ok;

    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string titulo, string cuerpo, string nota, string aceptar) : this()
    {
        TituloText.Text = titulo;
        CuerpoText.Text = cuerpo;
        NotaText.Text = nota;
        NotaText.IsVisible = nota.Length > 0;
        OkBtn.Content = aceptar;
    }

    /// <summary>Muestra el diálogo; devuelve true si el usuario confirma.</summary>
    public static async Task<bool> AskAsync(Window owner, string titulo, string cuerpo,
        string nota = "", string aceptar = "Continuar")
    {
        var dlg = new ConfirmDialog(titulo, cuerpo, nota, aceptar);
        await dlg.ShowDialog(owner);
        return dlg._ok;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) { _ok = true; Close(); }
    private void Cancel_Click(object? sender, RoutedEventArgs e) { _ok = false; Close(); }
}
