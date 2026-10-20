using System.Text.RegularExpressions;
using System.Xml.Linq;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

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
        };

        var ayuda = TodaLaAyuda();
        var readme = Readme();
        foreach (var a in archivos)
        {
            Assert.True(ayuda.Contains(a, StringComparison.Ordinal), $"la Ayuda no nombra {a}");
            Assert.True(readme.Contains(a, StringComparison.Ordinal), $"el README no nombra {a}");
        }
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
    public void Lo_que_cita_la_Ayuda_existe_en_la_pantalla(string rotulo, string archivo)
    {
        var fuente = Fuente(new[] { "Etiquetador.App" }.Concat(archivo.Split('/')).ToArray());

        Assert.True(TodaLaAyuda().Contains(rotulo, StringComparison.Ordinal), $"la Ayuda ya no cita «{rotulo}»");
        Assert.True(fuente.Contains(rotulo, StringComparison.Ordinal), $"«{rotulo}» no aparece en {archivo}: actualiza la Ayuda");
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
    public void La_Ayuda_y_el_README_no_dicen_lo_que_el_codigo_ya_no_hace(string expresion, string motivo)
    {
        Assert.False(TodaLaAyuda().Contains(expresion, StringComparison.OrdinalIgnoreCase), $"la Ayuda dice «{expresion}», y {motivo}");
        Assert.False(Readme().Contains(expresion, StringComparison.OrdinalIgnoreCase), $"el README dice «{expresion}», y {motivo}");
    }
}
