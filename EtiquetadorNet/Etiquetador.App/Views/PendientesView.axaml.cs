using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.App.Views;

public partial class PendientesView : UserControl
{
    public PendientesView()
    {
        InitializeComponent();
        // Cambiar la selección (clic, Ctrl+clic, teclado) devuelve las opciones de una canción a la fila
        // elegida hasta el próximo clic derecho.
        TablaPendientes.SelectionChanged += (_, _) => _pulsada = null;
        // Dos tablas con columnas distintas: cada una recuerda sus anchos por separado.
        AttachedToVisualTree += (_, _) =>
        {
            var engine = Services.AppEngine.Current;
            if (engine == null) return;
            GridBehaviors.RememberColumnWidths(TablaPendientes, "Pendientes", engine.Config, engine.SaveConfig);
            GridBehaviors.RememberColumnWidths(TablaListas, "ListasSeguidas", engine.Config, engine.SaveConfig);
        };
    }

    private PendientesViewModel? Vm => DataContext as PendientesViewModel;

    private IReadOnlyList<PendienteFila> Elegidas() => TablaPendientes.SelectedItems.OfType<PendienteFila>().ToList();

    private IReadOnlyList<ListaSeguidaFila> ListasElegidas() => TablaListas.SelectedItems.OfType<ListaSeguidaFila>().ToList();

    private void Grid_DoubleTapped(object? sender, TappedEventArgs e) => GridBehaviors.AutoFitOnHeaderDoubleTap(sender, e);

    /// <summary>La última fila de pendientes pulsada con el botón derecho (ver <see cref="UnaFila"/>).</summary>
    private PendienteFila? _pulsada;

    // Clic derecho: sin perder una selección de varias filas, y recordando cuál se pulsó.
    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        TeclaEnergia.SeleccionarConClicDerecho(sender, e);
        if (ReferenceEquals(sender, TablaPendientes) && AccionesLista.FilaPulsadaConDerecho<PendienteFila>(sender, e) is { } fila)
            _pulsada = fila;
    }

    /// <summary>
    /// La canción sobre la que actúan las opciones de una sola canción: con varias elegidas, la pulsada
    /// con el botón derecho, no la primera de la selección (que podía ser otra).
    /// </summary>
    private PendienteFila? UnaFila() => AccionesLista.FilaParaUnaCancion(_pulsada, Vm?.Seleccionada, Elegidas());

    // --- Canciones pendientes ---

    private async void BuscarEnPool_Click(object? sender, RoutedEventArgs e)
    {
        // Una sola: abrir de golpe una pestaña del navegador por cada canción elegida no ayuda a nadie.
        if (Vm is { } vm) await vm.BuscarEnPoolAsync(UnaFila());
    }

    private async void AbrirEnlace_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.AbrirEnlaceAsync(UnaFila());
    }

    private async void AbrirCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.AbrirCarpetaAsync(UnaFila());
    }

    // Pide confirmación: ese archivo deja de contar para la canción. Antes se hacía sin preguntar y, pulsado
    // por error sobre una conseguida correcta, no había forma de deshacerlo.
    private async void VolverAPendiente_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var conseguidas = Elegidas().Where(f => f.Conseguida).ToList();
        if (conseguidas.Count == 0) { vm.Status = "Elige alguna canción conseguida: las pendientes ya lo están."; return; }
        var ok = await ConfirmDialog.AskAsync(owner,
            "Volver a pendiente",
            conseguidas.Count == 1
                ? $"«{conseguidas[0].Artista} - {conseguidas[0].Titulo}» volverá a pendiente y el archivo «{conseguidas[0].Archivo}» dejará de contar como esa canción."
                : $"{conseguidas.Count} canciones volverán a pendiente y los archivos con los que se tacharon dejarán de contar como ellas.",
            "Úsalo cuando el archivo no es la canción (un remix con otro nombre, otra canción con el mismo título). No se toca ningún archivo. "
            + "Se deshace con «Volver a contar el archivo descartado», en el menú del botón derecho.",
            "Volver a pendiente");
        if (ok) vm.VolverAPendiente(conseguidas);
    }

    private async void RecuperarDescartado_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.RecuperarDescartadosAsync(Elegidas());
    }

    private async void Quitar_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var filas = Elegidas();
        if (filas.Count == 0) { vm.Status = "Elige antes alguna canción de la tabla."; return; }
        var ok = await ConfirmDialog.AskAsync(owner,
            "Quitar de pendientes",
            filas.Count == 1 ? $"Se quitará «{filas[0].Artista} - {filas[0].Titulo}» de la lista de pendientes."
                             : $"Se quitarán {filas.Count} canciones de la lista de pendientes.",
            "No se toca ningún archivo. Se pueden volver a añadir desde Tendencias o Playlists.",
            "Quitar");
        if (ok) vm.Quitar(filas);
    }

    private async void QuitarConseguidas_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var n = vm.CuantasConseguidas;
        if (n == 0) { vm.Status = "No hay ninguna canción conseguida en la lista."; return; }
        var ok = await ConfirmDialog.AskAsync(owner,
            "Quitar las conseguidas",
            n == 1 ? "Se quitará de la lista la canción ya conseguida." : $"Se quitarán de la lista las {n} canciones ya conseguidas.",
            "No se toca ningún archivo: las canciones siguen en la biblioteca.",
            "Quitar");
        if (ok) vm.QuitarConseguidas();
    }

    private async void Exportar_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var pendientes = vm.Visibles();
        if (pendientes.Count == 0) { vm.Status = "No hay nada que exportar con el filtro actual."; return; }

        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar pendientes",
            SuggestedFileName = TextUtils.Sanitize($"Pendientes {DateTime.Now:yyyy-MM-dd}"),
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Hoja de cálculo (CSV)") { Patterns = new[] { "*.csv" } },
                new FilePickerFileType("Texto") { Patterns = new[] { "*.txt" } },
            },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.Exportar(destino, pendientes);
    }

    // --- Listas que sigues ---

    private async void AnadirNuevas_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.AnadirNuevasAPendientesAsync(ListasElegidas());
    }

    private async void DejarDeSeguir_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var listas = ListasElegidas();
        if (listas.Count == 0) { vm.Status = "Elige antes alguna lista de la tabla."; return; }
        var ok = await ConfirmDialog.AskAsync(owner,
            "Dejar de seguir",
            listas.Count == 1 ? $"Dejarás de seguir «{listas[0].Nombre}»." : $"Dejarás de seguir {listas.Count} listas.",
            "Se olvida lo que tenían en las revisiones anteriores: si vuelves a seguirlas, las novedades se cuentan desde ese día. Las canciones que ya estén en pendientes se quedan.",
            "Dejar de seguir");
        if (ok) vm.DejarDeSeguir(listas);
    }
}
