using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.Views;

/// <summary>
/// Términos que el usuario dicta para reanalizar una canción concreta. <paramref name="Source"/>
/// va informado solo si eligió una coincidencia de la lista: entonces se busca solo en esa fuente.
/// </summary>
public sealed record SearchTerms(string Artist, string Title, string Source = "");

/// <summary>
/// Diálogo previo a "Reanalizar": permite afinar qué se busca y ELEGIR entre las coincidencias
/// del catálogo, en vez de quedarse con la que el scoring considera mejor.
/// </summary>
public partial class SearchDialog : Window
{
    private readonly Func<string, string, Task<System.Collections.Generic.IReadOnlyList<Candidate>>>? _search;
    private SearchTerms? _result;
    private bool _searching;

    public SearchDialog() => InitializeComponent();

    public SearchDialog(string fileName, string artist, string title,
        Func<string, string, Task<System.Collections.Generic.IReadOnlyList<Candidate>>> search) : this()
    {
        _search = search;
        FileText.Text = fileName;
        ArtistBox.Text = artist;
        TitleBox.Text = title;
        Opened += async (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
            await RunSearchAsync();   // ya trae coincidencias al abrir
        };
    }

    /// <summary>Muestra el diálogo; devuelve null si el usuario cancela.</summary>
    public static async Task<SearchTerms?> AskAsync(Window owner, string fileName, string artist, string title,
        Func<string, string, Task<System.Collections.Generic.IReadOnlyList<Candidate>>> search)
    {
        var dlg = new SearchDialog(fileName, artist, title, search);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private async Task RunSearchAsync()
    {
        if (_search == null || _searching) return;
        _searching = true;
        SearchBtn.IsEnabled = false;
        SearchStatus.Text = "Buscando…";
        Results.ItemsSource = null;
        UseSelectedBtn.IsEnabled = false;
        try
        {
            var items = await _search(ArtistBox.Text ?? "", TitleBox.Text ?? "");
            Results.ItemsSource = items;
            SearchStatus.Text = items.Count == 0
                ? "Sin coincidencias. Prueba otra grafía."
                : $"{items.Count} coincidencias — elige una o usa lo escrito.";
            if (items.Count > 0) Results.SelectedIndex = 0;
        }
        catch (Exception e) { SearchStatus.Text = "Error al buscar: " + e.Message; }
        finally { _searching = false; SearchBtn.IsEnabled = true; }
    }

    private async void Search_Click(object? sender, RoutedEventArgs e) => await RunSearchAsync();

    private void Results_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UseSelectedBtn.IsEnabled = Results.SelectedItem is Candidate;

    private void Results_DoubleTapped(object? sender, TappedEventArgs e) => UseSelected_Click(sender, e);

    // Elegir de la lista: se buscan EXACTAMENTE el artista y el título de esa coincidencia.
    private void UseSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not Candidate c) return;
        _result = new SearchTerms(c.Artist, c.Title, c.Source);   // se respeta la fuente elegida
        Close();
    }

    // Usar lo escrito, sin elegir de la lista.
    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        _result = new SearchTerms((ArtistBox.Text ?? "").Trim(), (TitleBox.Text ?? "").Trim());
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) { _result = null; Close(); }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await RunSearchAsync(); }   // Enter = buscar
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }
}
