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

        // --- Equipo «asistente», tanda 3: el mismo menú en las tablas de «Limpiar publicidad de
        // packs» y «Género por carpeta y artista». La de carpetas no lleva: sus filas no son archivos.
        foreach (var tabla in new[] { TablaPacks, TablaGeneroReglas })
        {
            tabla.ContextMenu = CrearMenu(tabla);
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
            // Se cuentan ARCHIVOS, no filas: en «Limpiar publicidad de packs» un archivo tiene una fila
            // por etiqueta (el álbum y el artista).
            var estado = CalcularMenu(tabla.SelectedItem != null, vm?.IsBusy == true, vm?.EscribiendoArchivos == true,
                                      RutasDistintas(tabla.SelectedItems.OfType<IFilaConArchivo>()).Count);
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
    /// Los archivos de unas filas, sin repetir y en el orden en que aparecen. En «Limpiar publicidad de
    /// packs» cada archivo tiene una fila por etiqueta (el álbum y el artista): con las dos
    /// seleccionadas, el diálogo decía «2 archivos» y el segundo envío fallaba con un error en inglés
    /// de que el archivo ya no existía.
    /// </summary>
    internal static List<string> RutasDistintas(IEnumerable<IFilaConArchivo> filas)
        => filas.Select(f => f.RutaArchivo).Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Las filas de la tabla cuyos archivos se han enviado a la papelera, estén seleccionadas o no: la
    /// fila del álbum de un archivo cuya fila de artista se envió ya no tiene archivo, y aplicarla
    /// fallaría.
    /// </summary>
    internal static List<object> FilasDeArchivos(System.Collections.IEnumerable filas, IReadOnlyCollection<string> enviadas)
    {
        var rutas = new HashSet<string>(enviadas, StringComparer.OrdinalIgnoreCase);
        return filas.Cast<object>().Where(f => f is IFilaConArchivo a && rutas.Contains(a.RutaArchivo)).ToList();
    }

    /// <summary>
    /// Envía a la papelera los archivos de las filas seleccionadas, tras confirmar. Van a la papelera
    /// del sistema, así que se pueden recuperar; se quitan de la biblioteca y de la tabla.
    /// </summary>
    private async Task EnviarAPapeleraAsync(DataGrid tabla)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (vm.IsBusy) { vm.Status = "Espera a que termine el proceso en curso."; return; }
        var rutas = RutasDistintas(tabla.SelectedItems.OfType<IFilaConArchivo>());
        if (rutas.Count == 0) return;

        var (titulo, cuerpo) = LibraryViewModel.TextosConfirmacionPapelera(rutas);
        // La nota mira la unidad y la papelera de cada archivo: con muchas filas, fuera del hilo de la interfaz.
        var nota = await Task.Run(() => Services.Papelera.NotaConfirmacion(rutas));
        var ok = await ConfirmDialog.AskAsync(owner, titulo, cuerpo, nota, "Enviar a la papelera");
        if (!ok) return;
        // Por si una herramienta empezó mientras se confirmaba.
        if (vm.IsBusy) { vm.Status = "Espera a que termine el proceso en curso."; return; }

        vm.Engine.Preview.Stop();   // si suena, el archivo está abierto y no se puede mover
        // T2 (equipo catalogo_bandeja): fuera del hilo de la interfaz, con avance y cancelable, como en
        // Duplicados y la Bandeja. Antes el bucle iba aquí mismo y la ventana se quedaba congelada (en
        // macOS, un osascript por archivo que puede esperar hasta 20 s al permiso del sistema).
        Services.EnvioPapeleraResultado? res = null;
        await vm.TrabajarAsync(Services.EnvioPapelera.TextoAvance(0, rutas.Count), async ct =>
        {
            var avance = new Progress<(int Hechas, int Total)>(p =>
            {
                if (!vm.IsBusy) return;
                vm.Progress = p.Total > 0 ? 100.0 * p.Hechas / p.Total : 0;
                vm.Status = Services.EnvioPapelera.TextoAvance(p.Hechas, p.Total);
            });
            res = await Services.EnvioPapelera.EnviarAsync(rutas, progreso: avance, ct: ct);
        }, escribeArchivos: true);
        if (res == null) return;   // el error ya lo ha dicho TrabajarAsync

        // De una sola vez: la biblioteca avisa a todas las pestañas en cada cambio.
        vm.Engine.Library.RemoveTracks(res.Enviadas);

        // Y fuera de la tabla, con todas sus filas. La de «Renombrar con IA» va a través de una vista con filtro.
        var lista = tabla.ItemsSource is Avalonia.Collections.DataGridCollectionView v
            ? v.SourceCollection as System.Collections.IList
            : tabla.ItemsSource as System.Collections.IList;
        if (lista != null)
            foreach (var f in FilasDeArchivos(lista, res.Enviadas)) lista.Remove(f);

        vm.Status = Services.EnvioPapelera.Resumen(res, rutas.Count, Services.Sustantivo.Archivo)
                    + (res.Fallos.Count == 0 && !res.Interrumpido && ReferenceEquals(tabla, TablaMezcla)
                        ? " Vuelve a ordenar para recalcular las transiciones." : "");
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
        var nota = AsistenteViewModel.NotaRenombrar;
        if (conAvisos > 0) nota = (conAvisos == 1 ? "⚠ 1 de ellas tiene avisos: comprueba que la has revisado. " : $"⚠ {conAvisos} de ellas tienen avisos: comprueba que las has revisado. ") + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Renombrar con IA",
            marcadas.Count == 1
                ? "Se renombrará 1 archivo con el nombre propuesto (o el que hayas corregido)."
                : $"Se renombrarán {marcadas.Count} archivos con el nombre propuesto (o el que hayas corregido).",
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
        var nota = AsistenteViewModel.NotaRenombrar;
        if (aMano > 0) nota = (aMano == 1 ? "1 lo has escrito tú. " : $"{aMano} los has escrito tú. ") + nota;
        if (deIa > 0)
            nota = (deIa == 1 ? "⚠ 1 viene de la IA y no de las etiquetas del archivo: comprueba que la has revisado. "
                              : $"⚠ {deIa} vienen de la IA y no de las etiquetas del archivo: comprueba que las has revisado. ") + nota;

        var ok = await ConfirmDialog.AskAsync(owner,
            "Completar nombres cortados",
            marcadas.Count == 1
                ? "Se renombrará 1 archivo con el nombre completo (o el que hayas corregido)."
                : $"Se renombrarán {marcadas.Count} archivos con el nombre completo (o el que hayas corregido).",
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

    // =================================================================================================
    // Equipo «asistente», tanda 3: «Limpiar publicidad de packs» (P8) y «Género por carpeta y artista» (P9)
    // =================================================================================================

    private async void AplicarPacks_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.Packs.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ninguna propuesta marcada."; return; }

        var (cuerpo, nota) = LimpiarPacksViewModel.Confirmacion(marcadas, vm.Packs.AlComentario);
        var ok = await ConfirmDialog.AskAsync(owner, "Limpiar la publicidad de los packs", cuerpo, nota, "Aplicar");
        if (ok) await vm.Packs.AplicarAsync();
    }

    private async void AplicarGeneroReglas_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var marcadas = vm.GeneroReglas.Marcadas;
        if (marcadas.Count == 0) { vm.Status = "No hay ninguna propuesta marcada."; return; }

        var (cuerpo, nota) = GeneroPorReglasViewModel.Confirmacion(marcadas);
        var ok = await ConfirmDialog.AskAsync(owner, "Género por carpeta y por artista", cuerpo, nota, "Aplicar");
        if (ok) await vm.GeneroReglas.AplicarAsync();
    }

    private IReadOnlyList<CarpetaGeneroFila> CarpetasSeleccionadas()
        => TablaCarpetasGenero.SelectedItems.OfType<CarpetaGeneroFila>().ToList();

    private void PonerReglaCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm) return;
        // Lo que se ve escrito en el desplegable, aunque aún no haya pasado al modelo.
        var genero = GeneroCarpeta.Text;
        if (string.IsNullOrWhiteSpace(genero)) genero = GeneroCarpeta.SelectedItem as string ?? vm.GeneroReglas.GeneroElegido;
        vm.GeneroReglas.PonerRegla(CarpetasSeleccionadas(), genero);
    }

    private void QuitarReglaCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AsistenteViewModel vm) vm.GeneroReglas.QuitarRegla(CarpetasSeleccionadas());
    }

    private void ExcluirReglaCarpeta_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AsistenteViewModel vm) vm.GeneroReglas.ExcluirDeLaRegla(CarpetasSeleccionadas());
    }

    /// <summary>Al seleccionar una sola carpeta con regla, el desplegable enseña su género, listo para cambiarlo.</summary>
    private void CarpetasGenero_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm) return;
        var sel = CarpetasSeleccionadas();
        if (sel.Count == 1 && sel[0].Genero.Length > 0) vm.GeneroReglas.GeneroElegido = sel[0].Genero;
    }

    private async void AnadirCarpetaGenero_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AsistenteViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var carpetas = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Carpeta a la que dar un género (vale también para sus subcarpetas)",
            AllowMultiple = false,
        });
        var ruta = carpetas.Count > 0 ? carpetas[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(ruta)) await vm.GeneroReglas.AnadirCarpetaAsync(ruta);
    }

    // =================================================================================================
    // Ordenar para mezclar: opciones propias del menú de su tabla («Empezar por esta», «Buscar
    // compatibles», «Ver sus versiones»). El menú común de las seis tablas lo monta CrearMenu en el
    // constructor; aquí se le añaden estas la primera vez que se carga la tabla, sin tocar lo común.
    // =================================================================================================

    private bool _menuMezclaAmpliado;

    private void TablaMezcla_Loaded(object? sender, RoutedEventArgs e)
    {
        if (_menuMezclaAmpliado || TablaMezcla.ContextMenu is not { } menu) return;
        _menuMezclaAmpliado = true;

        var empezar = Opcion("Empezar por esta", () =>
        {
            if (DataContext is AsistenteViewModel vm && TablaMezcla.SelectedItem is PasoFila fila && !vm.IsBusy)
                _ = vm.Mezcla.EmpezarPorAsync(fila);
        });
        var compatibles = Opcion("Buscar compatibles…",
            () => _ = Acciones.AccionesCancion.AbrirCompatiblesAsync(Acciones.AccionesCancion.RutaDe(TablaMezcla.SelectedItem)));
        var versiones = Opcion("Ver sus versiones…",
            () => _ = Acciones.AccionesCancion.AbrirVersionesAsync(Acciones.AccionesCancion.RutaDe(TablaMezcla.SelectedItem)));

        // Detrás de «Editar»: son acciones sobre la canción, y van antes de las del archivo. En el orden
        // común a todas las tablas con canciones (ver AccionesCancion.Puesto): Compatibles, Versiones y, al
        // final del grupo, la de ordenar («Empezar por esta» aquí, «Ordenar la selección» en Biblioteca).
        var donde = Math.Min(2, menu.Items.Count);
        foreach (var c in new Control[] { new Separator(), compatibles, versiones, empezar })
            menu.Items.Insert(donde++, c);

        menu.Opening += (_, _) =>
        {
            var vm = DataContext as AsistenteViewModel;
            var hay = TablaMezcla.SelectedItem is PasoFila;
            empezar.IsEnabled = hay && vm?.IsBusy != true;
            empezar.Header = "Empezar por esta" + (vm?.IsBusy == true ? " (espera a que termine el proceso)" : "");
            // Los diálogos solo leen, pero desde ellos se escucha: no mientras una herramienta escribe archivos.
            compatibles.IsEnabled = versiones.IsEnabled = hay && vm?.EscribiendoArchivos != true;
        };
    }
}
