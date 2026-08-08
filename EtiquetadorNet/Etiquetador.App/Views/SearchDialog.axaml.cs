using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.Views;

/// <summary>
/// Términos que el usuario dicta para reanalizar una canción concreta. <paramref name="Source"/>
/// va informado solo si eligió una coincidencia de la lista o pegó un enlace: entonces se busca
/// solo en esa fuente.
/// </summary>
public sealed record SearchTerms(string Artist, string Title, string Source = "");

/// <summary>Servicios que el diálogo necesita del ViewModel (búsqueda y resolución de enlaces).</summary>
public sealed record SearchServices(
    Func<string, string, Task<IReadOnlyList<Candidate>>> Find,
    Func<string, Task<LinkResolver.Result>> ResolveLink);

/// <summary>
/// Diálogo previo a "Reanalizar": permite afinar qué se busca, ELEGIR entre las coincidencias del
/// catálogo, o pegar directamente el enlace de la canción (lo más exacto).
/// </summary>
public partial class SearchDialog : Window
{
    private readonly SearchServices? _svc;
    private SearchTerms? _result;
    private bool _busy;

    public SearchDialog() => InitializeComponent();

    public SearchDialog(string fileName, string artist, string title, SearchServices svc) : this()
    {
        _svc = svc;
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
        SearchServices svc)
    {
        var dlg = new SearchDialog(fileName, artist, title, svc);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    // --- Búsqueda por texto ---

    private async Task RunSearchAsync()
    {
        if (_svc == null || _busy) return;
        _busy = true;
        SearchBtn.IsEnabled = false;
        SearchStatus.Text = "Buscando…";
        Results.ItemsSource = null;
        UseSelectedBtn.IsEnabled = false;
        try
        {
            var items = await _svc.Find(ArtistBox.Text ?? "", TitleBox.Text ?? "");
            Results.ItemsSource = items;
            SearchStatus.Text = items.Count == 0
                ? "Sin coincidencias. Prueba otra grafía."
                : $"{items.Count} coincidencias — elige una o usa lo escrito.";
            if (items.Count > 0) Results.SelectedIndex = 0;
        }
        catch (Exception e) { SearchStatus.Text = "Error al buscar: " + e.Message; }
        finally { _busy = false; SearchBtn.IsEnabled = true; }
    }

    private async void Search_Click(object? sender, RoutedEventArgs e) => await RunSearchAsync();

    // --- Enlace directo ---

    private async Task UseLinkAsync()
    {
        if (_svc == null || _busy) return;
        var url = (LinkBox.Text ?? "").Trim();
        if (url.Length == 0) { LinkStatus.Text = "Pega primero un enlace."; return; }

        _busy = true;
        LinkBtn.IsEnabled = false;
        LinkStatus.Text = "Leyendo el enlace…";
        try
        {
            var res = await _svc.ResolveLink(url);
            if (res.Candidate is { } c)
            {
                // Un enlace es inequívoco: se acepta directamente con su fuente.
                _result = new SearchTerms(c.Artist, c.Title, c.Source);
                Close();
                return;
            }
            LinkStatus.Text = "⚠ " + res.Error;
        }
        catch (Exception e) { LinkStatus.Text = "⚠ No se pudo leer el enlace: " + e.Message; }
        finally { _busy = false; LinkBtn.IsEnabled = true; }
    }

    private async void UseLink_Click(object? sender, RoutedEventArgs e) => await UseLinkAsync();

    private async void OnLinkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await UseLinkAsync(); }
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    // --- Elegir / aceptar ---

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
