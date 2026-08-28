using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

/// <summary>Una canción descartada, con su casilla para recuperarla.</summary>
public sealed partial class DescartadaItem : ObservableObject
{
    [ObservableProperty] private bool _marcada;
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public string Folder => Path.GetDirectoryName(FilePath) ?? "";
}

/// <summary>
/// Elegir QUÉ canciones descartadas se recuperan.
///
/// Antes recuperar era todo o nada: quien había descartado trescientas y quería una sola de vuelta
/// tenía que devolverlas todas y volver a descartar las otras doscientas noventa y nueve. Descartar
/// es una decisión que se toma canción a canción, así que deshacerla también.
/// </summary>
public partial class RestoreIgnoredDialog : Window
{
    private readonly ObservableCollection<DescartadaItem> _todas = new();
    private List<string>? _resultado;

    public RestoreIgnoredDialog() => InitializeComponent();

    private RestoreIgnoredDialog(IEnumerable<string> descartadas) : this()
    {
        foreach (var p in descartadas) _todas.Add(new DescartadaItem { FilePath = p });
        AplicarFiltro();
    }

    /// <summary>Muestra el diálogo; devuelve las rutas a recuperar, o null si se cancela.</summary>
    public static async Task<List<string>?> AskAsync(Window owner, IEnumerable<string> descartadas)
    {
        var dlg = new RestoreIgnoredDialog(descartadas);
        await dlg.ShowDialog(owner);
        return dlg._resultado;
    }

    /// <summary>
    /// Filtra lo que se ve, PERO las marcas se conservan en la lista completa: buscar un artista,
    /// marcarlo, buscar otro y marcarlo también tiene que recuperar los dos.
    /// </summary>
    private void AplicarFiltro()
    {
        var q = FilterBox.Text ?? "";
        var visibles = _todas.Where(d => BusquedaTexto.Coincide(q, d.FileName, d.Folder)).ToList();
        List.ItemsSource = visibles;
        CountText.Text = q.Trim().Length == 0
            ? $"{_todas.Count} descartadas"
            : $"{visibles.Count} de {_todas.Count}";
    }

    private void Filter_Changed(object? sender, TextChangedEventArgs e) => AplicarFiltro();

    // Marcar todo actúa sobre lo VISIBLE, que es lo que el usuario tiene delante: con un filtro
    // puesto, marcar de golpe las que no se ven sería justo lo contrario de lo que ha pedido.
    private void MarkAll_Click(object? sender, RoutedEventArgs e) => Marcar(true);
    private void MarkNone_Click(object? sender, RoutedEventArgs e) => Marcar(false);

    private void Marcar(bool valor)
    {
        if (List.ItemsSource is IEnumerable<DescartadaItem> visibles)
            foreach (var d in visibles) d.Marcada = valor;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) { _resultado = null; Close(); }

    private void Restore_Click(object? sender, RoutedEventArgs e)
    {
        _resultado = _todas.Where(d => d.Marcada).Select(d => d.FilePath).ToList();
        Close();
    }
}
