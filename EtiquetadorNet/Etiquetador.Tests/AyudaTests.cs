using System.Text.RegularExpressions;
using System.Xml.Linq;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Copias;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// La Ayuda (HelpView.axaml) y el README describen la aplicación con texto escrito a mano, y nada
/// falla al compilar cuando se quedan atrás: una pestaña nueva sin su sección (le pasó a Pendientes
/// en la tanda 3), un archivo de datos nuevo que nadie sabe que hay que copiar, un formato que la
/// biblioteca ya lee y la Ayuda no menciona, o un botón que cambia de nombre y la Ayuda sigue citando
/// el antiguo. Estas pruebas atan lo que dicen esos textos al código.
/// </summary>
public class AyudaTests
{
    /// <summary>Sube desde el binario de pruebas hasta la carpeta de la solución (EtiquetadorNet).</summary>
    private static DirectoryInfo Solucion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "HelpView.axaml")))
            dir = dir.Parent;

        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);
        return dir!;
    }

    private static string Fuente(params string[] tramos)
        => File.ReadAllText(Path.Combine(Solucion().FullName, Path.Combine(tramos)));

    /// <summary>El README está en la raíz del repositorio, una carpeta por encima de la solución.</summary>
    private static string Readme()
        => File.ReadAllText(Path.Combine(Solucion().Parent!.FullName, "README.md"));

    /// <summary>
    /// Los textos de la Ayuda tal como se ven (el atributo Text de cada TextBlock, ya sin escapes XML),
    /// con su clase: «h» son las cabeceras de sección.
    /// </summary>
    private static List<(string Clase, string Texto)> TextosAyuda()
    {
        XNamespace av = "https://github.com/avaloniaui";
        var doc = XDocument.Parse(Fuente("Etiquetador.App", "Views", "HelpView.axaml"));
        return doc.Descendants(av + "TextBlock")
                  .Select(t => ((string?)t.Attribute("Classes") ?? "", (string?)t.Attribute("Text") ?? ""))
                  .Where(t => t.Item2.Length > 0)
                  .ToList();
    }

    private static string TodaLaAyuda() => string.Join("\n", TextosAyuda().Select(t => t.Texto));

    /// <summary>Las pestañas de la barra lateral, con el icono y el nombre que ve el usuario.</summary>
    private static List<(string Icono, string Nombre)> Pestanas()
        => Regex.Matches(Fuente("Etiquetador.App", "ViewModels", "MainViewModel.cs"),
                         @"Icono = ""([^""]+)"",\s*Nombre = ""([^""]+)""")
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
                .ToList();

    // Con el MISMO icono que en la barra lateral: es lo que el usuario busca con la vista al pasar de
    // una a otra, y antes la Ayuda usaba 📝 para el Editor, que en la barra es el de Pendientes.
    [Fact]
    public void Cada_pestana_de_la_barra_lateral_tiene_su_seccion_en_la_Ayuda_con_su_icono()
    {
        var pestanas = Pestanas().Where(p => p.Nombre != "Ayuda").ToList();
        Assert.True(pestanas.Count >= 15, $"solo se encontraron {pestanas.Count} pestañas en MainViewModel: ¿ha cambiado su forma?");

        var cabeceras = TextosAyuda().Where(t => t.Clase == "h").Select(t => t.Texto).ToList();
        var sinSeccion = pestanas
            .Where(p => !cabeceras.Any(c => c.Contains(p.Icono + " " + p.Nombre, StringComparison.Ordinal)))
            .Select(p => p.Icono + " " + p.Nombre)
            .ToList();

        Assert.True(sinSeccion.Count == 0, "Sin sección en la Ayuda: " + string.Join(", ", sinSeccion));
    }

    // Son trabajo del usuario y no se regeneran: si la Ayuda y el README no los nombran, nadie los
    // incluye en su copia de seguridad. Los nombres salen del código, no de una lista aparte.
    [Fact]
    public void La_Ayuda_y_el_README_nombran_los_archivos_de_datos_que_hay_que_copiar()
    {
        var p = new AppPaths(Path.Combine(Path.GetTempPath(), "beattag-ayuda-no-se-crea"));
        var archivos = new[]
        {
            Path.GetFileName(p.FichasDjPath),
            Path.GetFileName(p.ColeccionesPath),
            Path.GetFileName(p.BandejaPath),
            AlmacenPendientes.NombreArchivo,
            AlmacenListasSeguidas.NombreArchivo,
            MapaGenerosCarpeta.NombreArchivo,
            Path.GetFileName(SustitucionBandeja.CarpetaDiario("datos")),
            // Pulido de la tanda 3: guarda la ficha y lo decidido sobre la copia antigua mientras
            // está en la papelera. Perderlo deja sin ficha la canción al deshacer la sustitución.
            Path.GetFileName(AlmacenSustituciones.RutaEn("datos")),
            // Tanda 4 (Novedades): las correcciones y exclusiones de artistas hechas a mano. Las
            // búsquedas en Deezer van a la caché de red y no hace falta copiarlas; esto sí.
            AlmacenArtistasDeezer.NombreArchivo,
            // Volumen: la ganancia ya aplicada. sonoridad.json se regenera; esto no, y sin ello el
            // siguiente ajuste vuelve a bajar lo que ya se había bajado.
            Path.GetFileName(p.VolumenAplicadoPath),
        };

        var ayuda = TodaLaAyuda();
        var readme = Readme();
        foreach (var a in archivos)
        {
            Assert.True(ayuda.Contains(a, StringComparison.Ordinal), $"la Ayuda no nombra {a}");
            Assert.True(readme.Contains(a, StringComparison.Ordinal), $"el README no nombra {a}");
        }
    }

    // La copia de seguridad (tanda 4) mete en un ZIP los archivos de trabajo, pero las copias que
    // hace sola al arrancar viven en una carpeta propia de la carpeta de datos. Quien guarda esa
    // carpeta a mano tiene que saber que existe, cómo se llaman sus archivos y cuántos se conservan;
    // los nombres y la cifra salen del código, para que un cambio ahí no deje la Ayuda desfasada.
    [Fact]
    public void La_Ayuda_y_el_README_explican_la_carpeta_de_copias_automaticas()
    {
        var p = new AppPaths(Path.Combine(Path.GetTempPath(), "beattag-ayuda-no-se-crea"));
        var nombres = new[]
        {
            CopiaSeguridad.PrefijoAutomatica + "…zip",
            CopiaSeguridad.PrefijoAntesDeRestaurar + "…zip",
            Path.GetFileName(CopiaSeguridad.CarpetaRestauracion(p)),
            $"las {CopiaSeguridad.CopiasQueSeConservan} últimas",
        };

        var ayuda = TodaLaAyuda();
        var readme = Readme();
        foreach (var n in nombres)
        {
            Assert.True(ayuda.Contains(n, StringComparison.Ordinal), $"la Ayuda no dice «{n}»");
            Assert.True(readme.Contains(n, StringComparison.Ordinal), $"el README no dice «{n}»");
        }

        var carpeta = Path.GetFileName(CopiaSeguridad.CarpetaCopias(p));
        Assert.Contains("La carpeta " + carpeta + " guarda", ayuda);
        Assert.Contains("La carpeta `" + carpeta + "` guarda", readme);
    }

    // Las secciones nuevas de la tanda 4 van en el orden de la barra lateral: rekordbox dentro de TU
    // MÚSICA (junto a la Bandeja) y Novedades detrás de Pendientes, como en la barra.
    [Fact]
    public void Las_secciones_de_rekordbox_y_Novedades_siguen_el_orden_de_la_barra_lateral()
    {
        var cabeceras = TextosAyuda().Where(t => t.Clase == "h").Select(t => t.Texto).ToList();
        int Posicion(string texto)
        {
            var i = cabeceras.FindIndex(c => c.Contains(texto, StringComparison.Ordinal));
            Assert.True(i >= 0, $"no hay cabecera «{texto}» en la Ayuda");
            return i;
        }

        Assert.Equal(Posicion("📥 Bandeja de entrada") + 1, Posicion("🎛 rekordbox"));
        Assert.Equal(Posicion("📝 Pendientes") + 1, Posicion("🆕 Novedades"));
    }

    /// <summary>¿Nombra <paramref name="texto"/> esa extensión, con su punto o como palabra («AIFF», «Opus»)?</summary>
    internal static bool NombraFormato(string texto, string extension)
        => texto.Contains(extension, StringComparison.OrdinalIgnoreCase)
           || Regex.IsMatch(texto, @"\b" + Regex.Escape(extension.TrimStart('.')) + @"\b", RegexOptions.IgnoreCase);

    [Fact]
    public void NombraFormato_distingue_aif_de_aiff_solo_por_el_punto_o_la_palabra()
    {
        Assert.True(NombraFormato("AIFF (.aif y .aiff)", ".aif"));
        Assert.True(NombraFormato("MP3, FLAC y AIFF", ".aiff"));
        Assert.False(NombraFormato("MP3, FLAC y AIFF", ".aif"));   // «AIFF» no es la palabra «aif»
        Assert.False(NombraFormato("MP3 y FLAC", ".opus"));
    }

    // La lista de formatos de la Ayuda (Biblioteca) y del README tiene que cubrir todo lo que escanea
    // la biblioteca: con AIFF (M15) entró una extensión nueva y ninguno de los dos textos la citaba.
    [Fact]
    public void La_lista_de_formatos_de_la_Ayuda_y_del_README_cubre_lo_que_escanea_la_biblioteca()
    {
        var enAyuda = TextosAyuda().Select(t => t.Texto).Single(t => t.StartsWith("• Formatos admitidos:", StringComparison.Ordinal));
        var enReadme = Readme().Split('\n').Single(l => l.StartsWith("- **Formatos**:", StringComparison.Ordinal));

        foreach (var ext in LibraryScanner.AudioExtensions)
        {
            Assert.True(NombraFormato(enAyuda, ext), $"la Ayuda no cita el formato {ext}");
            Assert.True(NombraFormato(enReadme, ext), $"el README no cita el formato {ext}");
        }
    }

    // Lo que la Ayuda cita entre comillas tiene que ser lo que pone en la pantalla. Si un equipo cambia
    // el rótulo de uno de estos botones, esta prueba le recuerda que la Ayuda dice el nombre antiguo.
    [Theory]
    [InlineData("Analizar solo esta", "Views/FolderListView.axaml")]
    [InlineData("Subcarpeta…", "Views/FolderListView.axaml")]
    [InlineData("Ver todas las carpetas", "Views/EnrichView.axaml")]
    [InlineData("Historial de cambios…", "Views/EnrichView.axaml")]
    [InlineData("Mostrando solo «", "ViewModels/EnrichViewModel.cs")]
    [InlineData("Deshacer esta ejecución", "Views/HistorialDeshacerDialog.axaml")]
    [InlineData("Deshacer las marcadas", "Views/HistorialDeshacerDialog.axaml")]
    [InlineData("Dar por perdidos…", "Views/HistorialDeshacerDialog.axaml")]
    [InlineData("Mostrar también las ya deshechas", "Views/HistorialDeshacerDialog.axaml")]
    [InlineData("Buscar compatibles…", "Views/LibraryView.axaml.cs")]
    [InlineData("Ver sus versiones…", "Views/LibraryView.axaml.cs")]
    [InlineData("Ordenar para mezclar la selección…", "Views/LibraryView.axaml.cs")]
    [InlineData("Editar etiquetas de esta canción", "Views/LibraryView.axaml.cs")]
    [InlineData("Incluir saltos de tono", "Views/CompatiblesDialog.axaml")]
    [InlineData("Buscar compatibles con esta", "Views/CompatiblesDialog.axaml")]
    [InlineData("Empezar por esta", "Views/MezclaDialog.axaml")]
    [InlineData("Entrada desde la anterior", "Views/AsistenteView.axaml")]
    [InlineData("Guardar en este orden (M3U8)", "Views/OrdenMezclaPanel.axaml")]
    [InlineData("Volver a la lista", "Views/OrdenMezclaPanel.axaml")]
    [InlineData("Añadir a pendientes", "Views/TrendsView.axaml")]
    [InlineData("Buscar en mi pool", "Views/TrendsView.axaml")]
    [InlineData("Solo nuevas", "Views/TrendsView.axaml")]
    [InlineData("Novedad", "Views/TrendsView.axaml")]
    [InlineData("Seguir esta lista", "ViewModels/ListaFrenteBibliotecaViewModel.cs")]
    [InlineData("Dejar de seguir", "ViewModels/ListaFrenteBibliotecaViewModel.cs")]
    [InlineData("Listas que sigues", "Views/PlaylistView.axaml")]
    [InlineData("Actualizar todas", "Views/PendientesView.axaml")]
    [InlineData("Añadir a pendientes las nuevas que faltan", "Views/PendientesView.axaml")]
    [InlineData("Volver a pendiente", "Views/PendientesView.axaml")]
    [InlineData("Mostrar solo las pendientes", "Views/PendientesView.axaml")]
    [InlineData("Quitar las conseguidas", "Views/PendientesView.axaml")]
    [InlineData("Exportar…", "Views/PendientesView.axaml")]
    [InlineData("Abrir enlace", "Views/PendientesView.axaml")]
    [InlineData("Buscar publicidad", "Views/AsistenteView.axaml")]
    [InlineData("Lo que se quita", "Views/AsistenteView.axaml")]
    [InlineData("Proponer géneros", "Views/AsistenteView.axaml")]
    [InlineData("Poner a las seleccionadas", "Views/AsistenteView.axaml")]
    [InlineData("Añadir otra carpeta…", "Views/AsistenteView.axaml")]
    [InlineData("Descartar a la papelera", "Views/BandejaView.axaml")]
    [InlineData("Descartar (a la papelera)…", "Views/BandejaView.axaml")]
    [InlineData("Sustituir la copia de la biblioteca…", "Views/BandejaView.axaml")]
    [InlineData("Con aviso de duplicado", "ViewModels/BandejaViewModel.cs")]
    [InlineData("Mejora la de la biblioteca", "ViewModels/BandejaViewModel.cs")]
    [InlineData("Enviar marcadas a la papelera", "Views/DuplicatesView.axaml")]
    [InlineData("Código ISRC", "Views/SettingsView.axaml")]
    [InlineData("Escribir el ISRC al aplicar propuestas", "Views/SettingsView.axaml")]
    // Los del pulido de la tanda 3 (los rótulos nuevos y los que cambiaron de nombre).
    [InlineData("Cancelar el envío", "Views/LibraryView.axaml")]
    [InlineData("Cancelar el proceso", "Views/AsistenteView.axaml")]
    [InlineData("Comprobar la IA", "Views/AsistenteView.axaml")]
    [InlineData("Excluir de la regla de su carpeta madre", "Views/AsistenteView.axaml")]
    [InlineData("Quitar la regla", "Views/AsistenteView.axaml")]
    [InlineData("Empezar por esta", "Views/OrdenMezclaPanel.axaml")]
    [InlineData("Las seleccionadas que faltan", "Views/TrendsView.axaml")]
    [InlineData("Todas las que faltan", "Views/TrendsView.axaml")]
    [InlineData("Volver a contar el archivo descartado", "Views/PendientesView.axaml")]
    [InlineData("Faltan", "Views/PendientesView.axaml")]
    [InlineData("Al revisarla otro día", "ViewModels/PendientesViewModel.cs")]
    [InlineData("Pendiente (1 descartado)", "ViewModels/PendientesViewModel.cs")]
    // Tanda 4. Página rekordbox (P3 y P11) y su acceso desde Ajustes.
    [InlineData("Abrir colección exportada…", "Views/RekordboxView.axaml")]
    [InlineData("Volver a leer", "Views/RekordboxView.axaml")]
    [InlineData("No están en rekordbox", "ViewModels/RekordboxViewModel.cs")]
    [InlineData("Rutas rotas", "ViewModels/RekordboxViewModel.cs")]
    [InlineData("Sin preparar", "ViewModels/RekordboxViewModel.cs")]
    [InlineData("Nunca pinchadas", "ViewModels/RekordboxViewModel.cs")]
    [InlineData("Guardar como lista M3U8…", "Views/RekordboxView.axaml")]
    [InlineData("Resultado", "Views/RekordboxView.axaml")]
    [InlineData("Mismo nombre", "Views/RekordboxView.axaml")]
    [InlineData("Cómo llevar la reparación a rekordbox", "Views/RekordboxView.axaml")]
    [InlineData("Guardar colección reparada…", "Views/RekordboxView.axaml")]
    [InlineData("Contar como preparación un único hot cue en el primer tiempo de compás", "Views/RekordboxView.axaml")]
    [InlineData("Solo canciones de la biblioteca", "Views/RekordboxView.axaml")]
    [InlineData("Llevar listas a rekordbox", "Views/RekordboxView.axaml")]
    [InlineData("Exportar a XML…", "Views/RekordboxView.axaml")]
    [InlineData("Reparar colección de rekordbox", "Views/SettingsView.axaml")]
    // Novedades (P5).
    [InlineData("Buscar novedades", "Views/NovedadesView.axaml")]
    [InlineData("Solo estrenos", "Views/NovedadesView.axaml")]
    [InlineData("Incluir artistas sin confirmar", "Views/NovedadesView.axaml")]
    [InlineData("Solo las que no están en la biblioteca", "Views/NovedadesView.axaml")]
    [InlineData("Artistas consultados", "Views/NovedadesView.axaml")]
    [InlineData("Corregir", "Views/NovedadesView.axaml")]
    [InlineData("Volver a identificar", "Views/NovedadesView.axaml")]
    [InlineData("Abrir en Deezer", "Views/NovedadesView.axaml")]
    [InlineData("No buscar sus novedades", "ViewModels/NovedadesViewModel.cs")]
    [InlineData("Volver a incluir", "ViewModels/NovedadesViewModel.cs")]
    [InlineData("Origen", "Views/PendientesView.axaml")]
    // Volumen: exclusiones del ajuste (E15).
    [InlineData("Ajustar solo las seleccionadas…", "Views/LoudnessView.axaml")]
    [InlineData("Excluir del ajuste", "Views/LoudnessView.axaml")]
    [InlineData("Excluir esta carpeta del ajuste", "Views/LoudnessView.axaml")]
    [InlineData("Exclusiones del ajuste", "ViewModels/AjusteVolumenSeleccion.cs")]
    [InlineData("ya no está en la biblioteca", "ViewModels/AjusteVolumenSeleccion.cs")]
    // Volumen: el modo automático, la columna del pico y el ajuste.
    [InlineData("Nivel común de la biblioteca", "ViewModels/LoudnessViewModel.cs")]
    [InlineData("Margen (dB)", "Views/LoudnessView.axaml")]
    [InlineData("Ajuste necesario", "Views/LoudnessView.axaml")]
    [InlineData("Ajustar volumen", "Views/LoudnessView.axaml")]
    [InlineData("Por medir", "ViewModels/LoudnessViewModel.cs")]
    // Copia de seguridad y restauración (T15).
    [InlineData("Copia de seguridad…", "Views/SettingsView.axaml")]
    [InlineData("Restaurar copia…", "Views/SettingsView.axaml")]
    [InlineData("Copias automáticas", "Views/SettingsView.axaml")]
    [InlineData("Cerrar BeatTag ahora", "Views/SettingsView.axaml")]
    [InlineData("Cancelar la restauración", "Views/SettingsView.axaml")]
    [InlineData("Ubicación de la música", "Views/RestaurarCopiaDialog.axaml")]
    // Calidad real por espectro (P4).
    [InlineData("Analizar calidad real", "Views/QualityView.axaml")]
    [InlineData("Corte medido", "Views/QualityView.axaml")]
    [InlineData("Calidad real (estimada)", "Views/QualityView.axaml")]
    [InlineData("Solo sospechosos", "Views/QualityView.axaml")]
    [InlineData("Solo baja calidad", "Views/QualityView.axaml")]
    // Duplicados: otras ediciones del mismo tema (A14).
    [InlineData("Mismo audio (huella acústica)", "ViewModels/DuplicatesViewModel.cs")]
    [InlineData("Analizar audio", "Views/DuplicatesView.axaml")]
    [InlineData("Marcar sobrantes", "Views/DuplicatesView.axaml")]
    [InlineData("Única de su edición", "ViewModels/DuplicatesViewModel.cs")]
    public void Lo_que_cita_la_Ayuda_existe_en_la_pantalla(string rotulo, string archivo)
    {
        var fuente = Fuente(new[] { "Etiquetador.App" }.Concat(archivo.Split('/')).ToArray());

        Assert.True(TodaLaAyuda().Contains(rotulo, StringComparison.Ordinal), $"la Ayuda ya no cita «{rotulo}»");
        Assert.True(fuente.Contains(rotulo, StringComparison.Ordinal), $"«{rotulo}» no aparece en {archivo}: actualiza la Ayuda");
    }

    // Volumen: la Ayuda cita el umbral, la referencia y el margen de pico que usa el código, no los de
    // antes (2 dB y la mediana a secas).
    [Fact]
    public void La_seccion_de_Volumen_cita_los_valores_del_codigo()
    {
        var textos = TextosAyuda().Select(t => t.Texto).ToList();
        var inicio = textos.FindIndex(t => t == "🔊 Volumen");
        Assert.True(inicio >= 0, "no hay sección de Volumen en la Ayuda");
        var fin = textos.FindIndex(inicio + 1, t => t.StartsWith("📊", StringComparison.Ordinal));
        var volumen = string.Join("\n", textos.Skip(inicio).Take((fin < 0 ? textos.Count : fin) - inicio));

        var umbral = Etiquetador.App.ViewModels.LoudnessViewModel.UmbralAjuste;
        Assert.Contains($"más de {umbral:0.#} dB", volumen);
        Assert.DoesNotContain("2 dB", volumen);
        Assert.DoesNotContain("Nivel medio de la biblioteca", volumen);
        Assert.Contains($"el {(1 - ReferenciaVolumen.PercentilAlcance) * 100:0} %", volumen);
        Assert.Contains($"más de {ReferenciaVolumen.DescensoMaximo:0} dB por debajo", volumen);
        Assert.Contains("−0,5 dBTP", volumen);
        Assert.Contains($"pasos de {Mp3Gain.DbPerStep:0.0} dB".Replace('.', ','), volumen);
        // La referencia con el nivel de antes de ajustar y la salvaguarda de las carpetas pequeñas.
        Assert.Contains("antes de que BeatTag la ajustara", volumen);
        Assert.Contains($"menos de {ReferenciaVolumen.MinimoGrabaciones} grabaciones", volumen);
        // Cambiar el nivel de referencia ya no lanza mediciones por su cuenta.
        Assert.Contains("Cambiar el nivel de referencia o las exclusiones no mide nada", volumen);
        // Revisión final: la ganancia aplicada no se borra con las mediciones, y el ajuste parte de la
        // medida del archivo tal como está (tras un deshacer, con su nivel de ahora).
        Assert.Contains("volumen-aplicado.json", volumen);
        Assert.Contains("no las borra aunque borre las mediciones", volumen);
        Assert.DoesNotContain("esas anotaciones se borran con ellas", volumen);
        Assert.Contains("se parte siempre de la medida del archivo tal como está", volumen);
        // Revisión de las correcciones: el ajuste no hace lo contrario de lo confirmado, las «Por medir»
        // no cuentan, los ajustes de versiones anteriores no quedaron anotados y «Limpiar» vacía la tabla.
        Assert.Contains("ahora requiere el ajuste contrario", volumen);
        Assert.Contains("ahora requiere el ajuste contrario", Etiquetador.App.ViewModels.AjusteVolumenSeleccion.TextoSentidoContrario(1));
        Assert.Contains("no la cuenta en el resumen ni en la referencia automática", volumen);
        Assert.Contains("Los ajustes hechos con una versión anterior de BeatTag no quedaron anotados", volumen);
        Assert.Contains("la tabla de esta pestaña queda vacía hasta que vuelvas a pulsar Analizar", volumen);
    }

    // La norma de registro del producto: neutro y profesional, sin coloquialismos. Son los que ya se
    // colaron alguna vez en estos textos; lo que pinta una lista es si una canción está en la
    // biblioteca, no si «la tienes».
    [Theory]
    [InlineData("te faltan")]
    [InlineData("te falta ")]
    [InlineData("ya tienes")]
    [InlineData("no tienes")]
    [InlineData("la tienes")]
    [InlineData("las tienes")]
    [InlineData("merece la pena")]
    [InlineData("mándame")]
    [InlineData("te ha tocado")]
    public void La_Ayuda_y_el_README_no_usan_coloquialismos(string expresion)
    {
        Assert.False(TodaLaAyuda().Contains(expresion, StringComparison.OrdinalIgnoreCase), $"la Ayuda dice «{expresion}»");
        Assert.False(Readme().Contains(expresion, StringComparison.OrdinalIgnoreCase), $"el README dice «{expresion}»");
    }

    // Rótulos que no están en una vista sino en el código: el botón de copiar etiquetas lo crea el
    // diálogo con una constante, y los tipos del historial salen de HistorialDeshacer.Texto. Se toman
    // de ahí para que un cambio de nombre en el código haga fallar esta prueba y no la Ayuda en silencio.
    [Fact]
    public void La_Ayuda_cita_los_rotulos_que_salen_del_codigo()
    {
        var ayuda = TodaLaAyuda();
        var rotulos = new[]
        {
            SustitucionBandeja.BotonCopiar,
            HistorialDeshacer.Texto(TipoEjecucion.LimpiezaPacks),
            HistorialDeshacer.Texto(TipoEjecucion.GeneroPorReglas),
            HistorialDeshacer.Texto(TipoEjecucion.Sustitucion),
            // Tanda 4: la lista que deja la reparación de rekordbox, la carpeta de la exportación de
            // listas, las columnas de la calidad real y la columna Edición de Duplicados. Los ejemplos
            // de la Ayuda («empieza 32 s después», «1:15 más larga») se generan con la función real.
            RekordboxRelocator.ListaReparadas,
            ExportadorRekordbox.CarpetaRaiz,
            CorteEspectral.TextoCorte(new MedidaEspectro(EstadoCorte.SinCorte, 0, 22050)),
            CorteEspectral.TextoCorte(new MedidaEspectro(EstadoCorte.NoConcluyente, 0, 22050)),
            CorteEspectral.TextoValoracion(ValoracionCalidad.Sospechosa),
            CorteEspectral.TextoValoracion(ValoracionCalidad.Dudosa),
            EdicionesPorAudio.Referencia,
            EdicionesPorAudio.Texto(32, 0),
            EdicionesPorAudio.Texto(0, 75),
            EdicionesPorAudio.Texto(0, 0),
        };
        foreach (var r in rotulos)
            Assert.True(ayuda.Contains("«" + r + "»", StringComparison.Ordinal), $"la Ayuda no cita «{r}»");
    }

    // No es la Ayuda, pero es el mismo fallo: un aviso que pide pulsar un botón por su nombre. La banda
    // de la IA del Asistente rotula «Comprobar la IA» desde el pulido de la tanda 3, y el aviso de la IA
    // perdida a mitad de una herramienta seguía diciendo «Pulsa «Comprobar»».
    [Fact]
    public void El_aviso_de_la_IA_perdida_nombra_el_boton_como_se_ve()
    {
        var vista = Fuente("Etiquetador.App", "Views", "AsistenteView.axaml");
        var boton = Regex.Match(vista, @"Content=""(Comprobar[^""]*)""\s+Command=""\{Binding ComprobarIaCommand\}""");
        Assert.True(boton.Success, "no se encontró el botón de ComprobarIaCommand en AsistenteView.axaml");

        var aviso = Etiquetador.App.ViewModels.AsistenteViewModel.TextoIaPerdida("Ollama no responde.");
        Assert.StartsWith("Ollama no responde.", aviso);
        Assert.Contains("«" + boton.Groups[1].Value + "»", aviso);
        Assert.Contains("«" + boton.Groups[1].Value + "»", TodaLaAyuda());
    }

    // Lo que estos textos llegaron a decir y el código ya no hace (pulido de la tanda 3). Si alguien
    // copia de una versión anterior de la Ayuda, esta prueba lo frena.
    [Theory]
    [InlineData("puedes seguir usando la aplicación", "el envío a la papelera de la Biblioteca bloquea las demás pestañas")]
    [InlineData("sin artista, título ni duración", "los AIFF-C entran con su duración, su calidad y su bloque ID3")]
    [InlineData("sus etiquetas todavía no se leen", "las etiquetas ID3 de un AIFF-C sí se leen; lo que no se puede es escribirlas")]
    [InlineData("siguen funcionando solo con reglas", "«Renombrar con IA» necesita la IA")]
    [InlineData("a partir del día siguiente", "las novedades llegan cuando se revisa la lista otro día, no necesariamente mañana")]
    [InlineData("a partir de mañana", "las novedades llegan cuando se revisa la lista otro día, no necesariamente mañana")]
    [InlineData("la próxima vez que analices la bandeja", "una sustitución cortada se recoge también al arrancar")]
    [InlineData("la próxima vez que la analices", "una sustitución cortada se recoge también al arrancar")]
    // Tanda 4: la reparación de rekordbox se mudó a su página (Ajustes solo la abre), «Ajustar
    // volumen» ya solo corrige lo marcado en la columna «Ajustar», y la detección de la tonalidad por
    // el audio (A15) se midió pero no se ofrece.
    [InlineData("Ajustes → Reparar colección", "la reparación de rekordbox está en la página rekordbox; Ajustes solo la abre")]
    [InlineData("repárala aquí", "la reparación de rekordbox está en la página rekordbox; Ajustes solo la abre")]
    [InlineData("la colección en Ajustes", "la reparación de rekordbox está en la página rekordbox; Ajustes solo la abre")]
    [InlineData("corrige las grabaciones que se desvían", "«Ajustar volumen» solo corrige las marcadas en la columna «Ajustar» y nunca las excluidas")]
    [InlineData("detecta la tonalidad", "la detección de la tonalidad por el audio no se ofrece en la interfaz")]
    [InlineData("deduce la tonalidad", "la detección de la tonalidad por el audio no se ofrece en la interfaz")]
    public void La_Ayuda_y_el_README_no_dicen_lo_que_el_codigo_ya_no_hace(string expresion, string motivo)
    {
        Assert.False(TodaLaAyuda().Contains(expresion, StringComparison.OrdinalIgnoreCase), $"la Ayuda dice «{expresion}», y {motivo}");
        Assert.False(Readme().Contains(expresion, StringComparison.OrdinalIgnoreCase), $"el README dice «{expresion}», y {motivo}");
    }
}
