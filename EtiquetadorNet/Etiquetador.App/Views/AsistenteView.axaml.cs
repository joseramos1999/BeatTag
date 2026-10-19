using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Etiquetador.App.ViewModels;

namespace Etiquetador.App.Views;

public partial class AsistenteView : UserControl
{
    public AsistenteView()
    {
        InitializeComponent();

        // El mismo menú contextual que en el resto de pestañas, en las seis tablas. Cada herramienta
        // tiene su propio tipo de fila; el menú solo necesita saber si la fila es un archivo y si
        // tiene casilla (IFilaConArchivo, IFilaMarcable).
        foreach (var tabla in new[] { TablaBusqueda, TablaFichas, TablaGeneros, TablaRenombrar, TablaCompletar, TablaMezcla })
        {
            tabla.ContextMenu = CrearMenu(tabla);
            // El clic derecho selecciona la fila bajo el puntero, para que el menú actúe sobre ella.
            tabla.AddHandler(PointerPressedEvent, (s, e) => TeclaEnergia.SeleccionarConClicDerecho(s, e),
                             Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private ContextMenu CrearMenu(DataGrid tabla)
    {
        // Cuándo se permite cada opción mientras una herramienta trabaja: ver CalcularMenu.
        var escuchar = Opcion("Escuchar / Parar", () => ConArchivo(tabla, (vm, ruta) => vm.Engine.Preview.Toggle(ruta),
                                                                   bloqueada: vm => vm.EscribiendoArchivos));
        var editar = Opcion("Editar etiquetas de esta canción", () => ConArchivo(tabla, (vm, ruta) => vm.Engine.RequestEdit(ruta),
                                                                               bloqueada: vm => vm.IsBusy));
        var marcar = Opcion("Marcar las seleccionadas", () => Marcar(tabla, true));
        var desmarcar = Opcion("Desmarcar las seleccionadas", () => Marcar(tabla, false));
        var carpeta = Opcion("Abrir carpeta contenedora", () => ConArchivo(tabla, (vm, ruta) => { _ = Services.Shell.OpenContainingFolderAsync(ruta); },
                                                                         bloqueada: _ => false));
        var papelera = Opcion("Enviar a la papelera", () => _ = EnviarAPapeleraAsync(tabla));
        var separaMarcas = new Separator();
        var separaCarpeta = new Separator();

        var menu = new ContextMenu();
        foreach (var c in new Control[] { escuchar, editar, separaMarcas, marcar, desmarcar, separaCarpeta, carpeta, papelera })
            menu.Items.Add(c);

        // Qué se enseña depende de la tabla: la de géneros no tiene archivos y la de mezcla no tiene
        // casillas. Se mira la fila seleccionada o, sin selección, la primera.
        menu.Opening += (_, _) =>
        {
            var muestra = tabla.SelectedItem ?? (tabla.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().FirstOrDefault();
            var conArchivo = muestra is IFilaConArchivo;
            var marcable = muestra is IFilaMarcable;
            escuchar.IsVisible = editar.IsVisible = carpeta.IsVisible = papelera.IsVisible = separaCarpeta.IsVisible = conArchivo;
            marcar.IsVisible = desmarcar.IsVisible = marcable;
            separaMarcas.IsVisible = conArchivo && marcable;

            var vm = DataContext as AsistenteViewModel;
            var estado = CalcularMenu(tabla.SelectedItem != null, vm?.IsBusy == true, vm?.EscribiendoArchivos == true,
                                      tabla.SelectedItems.OfType<IFilaConArchivo>().Count());
            escuchar.IsEnabled = estado.Escuchar;
            editar.IsEnabled = estado.Editar;
            carpeta.IsEnabled = estado.Carpeta;
            marcar.IsEnabled = desmarcar.IsEnabled = estado.Marcar;
            papelera.IsEnabled = estado.Papelera;
            escuchar.Header = estado.TextoEscuchar;
            editar.Header = estado.TextoEditar;
            papelera.Header = estado.TextoPapelera;
        };
        return menu;
    }

    /// <summary>Qué opciones del menú contextual se pueden usar y cómo se rotulan.</summary>
    internal readonly record struct EstadoMenu(bool Escuchar, bool Editar, bool Carpeta, bool Marcar, bool Papelera,
                                               string TextoEscuchar, string TextoEditar, string TextoPapelera);

    /// <summary>
    /// Qué se deja hacer mientras una herramienta trabaja.
    ///
    /// Escuchar: no mientras una herramienta ESCRIBE archivos (aplicar géneros, renombrar, completar).
    /// El reproductor mantiene el archivo abierto y la herramienta solo libera el audio una vez, al
    /// empezar: escuchar desde «Buscar con una frase» una canción que «Aplicar las marcadas» estaba
    /// reetiquetando la dejaba en «no se pudo escribir». Mientras solo se lee (Proponer fichas, la fase
    /// de propuesta de Renombrar, Analizar géneros, que con límite 5000 duran minutos) sí se puede:
    /// visto en la revisión, bloquearlo con cualquier proceso impedía escuchar las propuestas que ya
    /// estaban en la tabla.
    ///
    /// Editar y mandar a la papelera: no con ningún proceso en marcha. Editar salta a otra página, y el
    /// bloqueo global no deja salir de esta mientras trabaja; mover un archivo a mitad de una pasada
    /// rompería lo que la herramienta tenga apuntado de él. Abrir la carpeta y marcar o desmarcar no
    /// tocan el archivo y siguen disponibles.
    /// </summary>
    internal static EstadoMenu CalcularMenu(bool haySeleccion, bool ocupado, bool escribiendo, int cuantas)
    {
        var espera = ocupado ? " (espera a que termine el proceso)" : "";
        var esperaEscritura = escribiendo ? " (espera a que se terminen de escribir los archivos)" : "";
        var conArchivo = haySeleccion && !ocupado;
        return new EstadoMenu(
            Escuchar: haySeleccion && !escribiendo,
            Editar: conArchivo,
            Carpeta: haySeleccion,
            Marcar: haySeleccion,
            Papelera: conArchivo,
            TextoEscuchar: "Escuchar / Parar" + esperaEscritura,
            TextoEditar: "Editar etiquetas de esta canción" + espera,
            TextoPapelera: (cuantas > 1 ? $"Enviar a la papelera ({cuantas})" : "Enviar a la papelera") + espera);
    }

    /// <summary>
    /// Envía a la papelera los archivos de las filas seleccionadas, tras confirmar. Van a la papelera
    /// del sistema, así que se pueden recuperar; se quitan de la biblioteca y de la tabla.
    /// </summary>
    private async Task EnviarAPapeleraAsync(DataGrid tabla)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.IsBusy) { vm.Status = "Espera a que termine el proceso en curso."; return; }
        var filas = tabla.SelectedItems.OfType<IFilaConArchivo>().ToList();
        if (filas.Count == 0) return;

        var titulo = filas.Count == 1
            ? "Se va a enviar a la papelera"
            : $"Se van a enviar {filas.Count} archivos a la papelera";
        var cuerpo = filas.Count == 1
            ? System.IO.Path.GetFileName(filas[0].RutaArchivo)
            : string.Join("\n", filas.Take(5).Select(f => System.IO.Path.GetFileName(f.RutaArchivo)))
              + (filas.Count > 5 ? $"\n… y {filas.Count - 5} más" : "");
        var ok = await ConfirmDialog.AskAsync(owner, titulo, cuerpo,
            Services.Papelera.NotaConfirmacion(filas.Select(f => f.RutaArchivo)),
            "Enviar a la papelera");
        if (!ok) return;
        // Por si una herramienta empezó mientras se confirmaba.
        if (vm.IsBusy) { vm.Status = "Espera a que termine el proceso en curso."; return; }

        vm.Engine.Preview.Stop();   // si suena, el archivo está abierto y no se puede mover
        var enviadas = new List<IFilaConArchivo>();
        var fallos = new List<string>();
        foreach (var f in filas)
        {
            var (bien, error) = Services.Papelera.Enviar(f.RutaArchivo);
            if (bien) enviadas.Add(f);
            else fallos.Add($"{System.IO.Path.GetFileName(f.RutaArchivo)}: {error}");
        }

        // De una sola vez: la biblioteca avisa a todas las pestañas en cada cambio.
        vm.Engine.Library.RemoveTracks(enviadas.Select(f => f.RutaArchivo));

        // Y fuera de la tabla. La de «Renombrar con IA» va a través de una vista con filtro.
        var lista = tabla.ItemsSource is Avalonia.Collections.DataGridCollectionView v
            ? v.SourceCollection as System.Collections.IList
            : tabla.ItemsSource as System.Collections.IList;
        foreach (var f in enviadas) lista?.Remove(f);

        vm.Status = fallos.Count == 0
            ? (enviadas.Count == 1 ? "Enviado a la papelera." : $"{enviadas.Count} archivos enviados a la papelera.")
              + (ReferenceEquals(tabla, TablaMezcla) ? " Vuelve a ordenar para recalcular las transiciones." : "")
            : $"{enviadas.Count} enviados, {fallos.Count} con problemas. El primero: {fallos[0]}";
    }

    private static MenuItem Opcion(string texto, Action accion)
    {
        var item = new MenuItem { Header = texto };
        item.Click += (_, _) => accion();
        return item;
    }

    /// <summary>
    /// Ejecuta la acción sobre el archivo de la fila seleccionada; si falla, lo dice en la barra de
    /// estado. No hace nada mientras <paramref name="bloqueada"/> diga que no se puede (se vuelve a
    /// mirar al pulsar: el menú pudo abrirse antes de que la herramienta empezara a escribir).
    /// </summary>
    private void ConArchivo(DataGrid tabla, Action<AsistenteViewModel, string> accion, Func<AsistenteViewModel, bool> bloqueada)
    {
        if (DataContext is not AsistenteViewModel vm || tabla.SelectedItem is not IFilaConArchivo fila) return;
        if (bloqueada(vm)) { vm.Status = "Espera a que termine el proceso en curso."; return; }
        try { accion(vm, fila.RutaArchivo); }
        catch (Exception e) { vm.Status = "No se pudo: " + e.Message; }
    }

    private static void Marcar(DataGrid tabla, bool valor)
    {
        foreach (var fila in tabla.SelectedItems.OfType<IFilaMarcable>().ToList())
            fila.Marcada = valor;
    }

    private async void AplicarGeneros_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Generos.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ningún género marcado."; return; }

        var canciones = marcadas.Sum(f => f.Canciones);
        var quitar = marcadas.Where(f => f.Propuesto.Trim().Length == 0).Sum(f => f.Canciones);
        var deIa = marcadas.Count(f => f.Origen == "IA");
        var nota = "Se escribe el género en las etiquetas de los archivos. Se puede deshacer desde Enriquecer.";
        if (quitar > 0) nota = $"En {quitar} de ellas se quitará el género. " + nota;
        if (deIa > 0) nota = $"⚠ {deIa} de los cambios son propuestas de la IA: comprueba que los has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Unificar géneros",
            $"Se cambiará el género de {canciones} canciones ({marcadas.Count} valores distintos).",
            nota, "Aplicar");
        if (ok) await vm.Generos.AplicarAsync();
    }

    private async void Renombrar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Renombrar.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ninguna propuesta marcada."; return; }

        var conAvisos = marcadas.Count(f => f.HayAvisos);
        var nota = "Solo cambia el nombre del archivo, no sus etiquetas. Si ya existe un archivo con el nombre nuevo, ese no se renombra. "
                 + "Se puede deshacer desde Enriquecer. Si usas rekordbox, después repara la colección en Ajustes para no perder los cue points.";
        if (conAvisos > 0) nota = $"⚠ {conAvisos} de ellas tienen avisos: comprueba que las has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Renombrar con IA",
            $"Se renombrarán {marcadas.Count} archivos con el nombre propuesto (o el que hayas corregido).",
            nota, "Renombrar");
        if (ok) await vm.Renombrar.RenombrarAsync();
    }

    private async void Completar_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Completar.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ninguna propuesta marcada."; return; }

        var deIa = marcadas.Count(f => f.Origen.StartsWith("IA", StringComparison.Ordinal));
        var aMano = marcadas.Count(f => !f.TienePropuesta);
        var nota = "Solo cambia el nombre del archivo, no sus etiquetas. Si ya existe un archivo con el nombre nuevo, ese no se renombra. "
                 + "Se puede deshacer desde Enriquecer. Si usas rekordbox, después repara la colección en Ajustes para no perder los cue points.";
        if (aMano > 0) nota = $"{aMano} los has escrito tú. " + nota;
        if (deIa > 0) nota = $"⚠ {deIa} vienen de la IA y no de las etiquetas del archivo: comprueba que las has revisado. " + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Completar nombres cortados",
            $"Se renombrarán {marcadas.Count} archivos con el nombre completo (o el que hayas corregido).",
            nota, "Completar");
        if (ok) await vm.Completar.RenombrarAsync();
    }

    private async void ExportarMezcla_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var archivo = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar lista ordenada",
            SuggestedFileName = Etiquetador.Core.TextUtils.Sanitize((vm.Mezcla.Coleccion?.Nombre ?? "Sesion") + " (ordenada)"),
            DefaultExtension = "m3u8",
            FileTypeChoices = new[] { new FilePickerFileType("Lista de reproducción") { Patterns = new[] { "*.m3u8" } } },
        });
        var destino = archivo?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(destino)) vm.Mezcla.ExportarM3u(destino);
    }

    /// <summary>Las condiciones descartadas se ven apagadas: siguen ahí para saber qué se ignoró y por qué.</summary>
    public static readonly IValueConverter Atenuar = new FuncValueConverter<bool, double>(usable => usable ? 1.0 : 0.55);
}
