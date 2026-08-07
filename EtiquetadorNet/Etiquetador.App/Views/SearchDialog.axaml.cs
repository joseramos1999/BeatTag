using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Etiquetador.App.Views;

/// <summary>Términos que el usuario dicta para reanalizar una canción concreta.</summary>
public sealed record SearchTerms(string Artist, string Title);

/// <summary>Diálogo previo a "Reanalizar": permite afinar qué se busca.</summary>
public partial class SearchDialog : Window
{
    private SearchTerms? _result;

    public SearchDialog() => InitializeComponent();

    public SearchDialog(string fileName, string artist, string title) : this()
    {
        FileText.Text = fileName;
        ArtistBox.Text = artist;
        TitleBox.Text = title;
        // Foco en el título: suele ser lo que más hay que corregir.
        Opened += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    /// <summary>Muestra el diálogo; devuelve null si el usuario cancela.</summary>
    public static async Task<SearchTerms?> AskAsync(Window owner, string fileName, string artist, string title)
    {
        var dlg = new SearchDialog(fileName, artist, title);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        _result = new SearchTerms((ArtistBox.Text ?? "").Trim(), (TitleBox.Text ?? "").Trim());
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) { _result = null; Close(); }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }
}
