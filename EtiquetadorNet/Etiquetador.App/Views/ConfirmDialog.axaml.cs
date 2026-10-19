using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Etiquetador.App.Views;

/// <summary>Confirmación para las acciones que modifican archivos del usuario.</summary>
public partial class ConfirmDialog : Window
{
    /// <summary>Qué botón se pulsó. Cerrar la ventana cuenta como Cancelar.</summary>
    public enum Eleccion { Cancelar, Aceptar, Alternativa }

    private Eleccion _eleccion = Eleccion.Cancelar;

    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string titulo, string cuerpo, string nota, string aceptar, string alternativa = "") : this()
    {
        TituloText.Text = titulo;
        CuerpoText.Text = cuerpo;
        NotaText.Text = nota;
        NotaText.IsVisible = nota.Length > 0;
        OkBtn.Content = aceptar;
        AltBtn.Content = alternativa;
        AltBtn.IsVisible = alternativa.Length > 0;
    }

    /// <summary>Muestra el diálogo; devuelve true si el usuario confirma.</summary>
    public static async Task<bool> AskAsync(Window owner, string titulo, string cuerpo,
        string nota = "", string aceptar = "Continuar")
        => await ElegirAsync(owner, titulo, cuerpo, nota, aceptar) == Eleccion.Aceptar;

    /// <summary>
    /// Como <see cref="AskAsync"/>, con un segundo camino además de aceptar: por ejemplo, «Solo las
    /// visibles» como opción principal y «Todas las marcadas» como alternativa. Sin
    /// <paramref name="alternativa"/> es la confirmación de siempre.
    /// </summary>
    public static async Task<Eleccion> ElegirAsync(Window owner, string titulo, string cuerpo,
        string nota = "", string aceptar = "Continuar", string alternativa = "")
    {
        var dlg = new ConfirmDialog(titulo, cuerpo, nota, aceptar, alternativa);
        await dlg.ShowDialog(owner);
        return dlg._eleccion;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) { _eleccion = Eleccion.Aceptar; Close(); }
    private void Alt_Click(object? sender, RoutedEventArgs e) { _eleccion = Eleccion.Alternativa; Close(); }
    private void Cancel_Click(object? sender, RoutedEventArgs e) { _eleccion = Eleccion.Cancelar; Close(); }
}
