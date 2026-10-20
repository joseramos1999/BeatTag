using System.Text.RegularExpressions;
using Etiquetador.App.Acciones;

namespace Etiquetador.Tests;

/// <summary>
/// El menú contextual de las tablas con canciones sigue el mismo orden en todas las pestañas
/// (hallazgo interfaz-7 de la revisión de la tanda 3): la misma canción ofrecía «Abrir carpeta
/// contenedora» en segunda, cuarta o última posición, y Colecciones, Tendencias y Playlists no tenían
/// «Editar». El orden está en <see cref="AccionesCancion.Puesto"/>; estas pruebas leen las vistas, porque
/// un menú desordenado compila y funciona igual, y nadie lo notaría hasta usarlo.
/// </summary>
public class MenusCancionTests
{
    /// <summary>Sube desde el binario de pruebas hasta encontrar la raíz de la solución (como PestanasTests).</summary>
    private static string Fuente(params string[] tramos)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.App", "Views", "MainWindow.axaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, Path.Combine(tramos));
    }

    /// <summary>Los rótulos del menú contextual de la (primera) tabla de una vista XAML, en orden.</summary>
    private static List<string> MenuXaml(string vista) => MenusXaml(vista)[0];

    /// <summary>
    /// Los rótulos de los menús contextuales de TODAS las tablas de una vista, cada uno en orden. La
    /// página rekordbox tiene cuatro tablas: leyendo solo la primera, un menú desordenado en cualquiera
    /// de las otras tres compilaba y pasaba las pruebas.
    /// </summary>
    private static List<List<string>> MenusXaml(string vista)
    {
        var texto = File.ReadAllText(Fuente("Etiquetador.App", "Views", vista + ".axaml"));
        var bloques = Regex.Matches(texto, @"<DataGrid\.ContextMenu>(.*?)</DataGrid\.ContextMenu>", RegexOptions.Singleline);
        Assert.True(bloques.Count > 0, vista + " no tiene menú contextual en su tabla");
        return bloques.Select(b => Regex.Matches(b.Groups[1].Value, @"<MenuItem\s+Header=""([^""]+)""").Select(m => m.Groups[1].Value).ToList())
                      .ToList();
    }

    private static void TodosEnElOrdenComun(string vista)
    {
        var menus = MenusXaml(vista);
        for (var i = 0; i < menus.Count; i++) EnElOrdenComun($"{vista} (tabla {i + 1})", menus[i]);
    }

    private static void EnElOrdenComun(string donde, IReadOnlyList<string> rotulos)
    {
        var puestos = rotulos.Select(AccionesCancion.Puesto).Where(p => p >= 0).ToList();
        Assert.True(puestos.Count >= 2, $"{donde}: no se reconoce ninguna opción común");
        for (var i = 1; i < puestos.Count; i++)
            Assert.True(puestos[i - 1] < puestos[i], $"{donde}: «{string.Join(" · ", rotulos)}» no sigue el orden común");
    }

    [Theory]
    [InlineData("ColeccionesView")]
    [InlineData("FichasView")]
    [InlineData("TrendsView")]
    [InlineData("PlaylistView")]
    [InlineData("OrdenMezclaPanel")]   // la tabla del orden de Tendencias y Playlists, la misma que la de mezcla del Asistente
    public void Las_tablas_con_canciones_tienen_las_mismas_opciones_en_el_mismo_orden(string vista)
    {
        var menu = MenuXaml(vista);

        TodosEnElOrdenComun(vista);
        foreach (var comun in new[] { AccionesCancion.TextoEscuchar, AccionesCancion.TextoEditar, AccionesCancion.TextoCompatibles,
                                      AccionesCancion.TextoVersiones, AccionesCancion.TextoCarpeta })
            Assert.Contains(comun, menu);
    }

    [Theory]
    [InlineData("CompatiblesDialog")]
    [InlineData("VersionesDialog")]
    [InlineData("MezclaDialog")]
    [InlineData("PendientesView")]   // sin escuchar ni editar (sus filas no suelen tener archivo), pero en el mismo orden
    [InlineData("RekordboxView")]    // cuatro tablas; «Rutas rotas» sin compatibles ni versiones
    [InlineData("LoudnessView")]     // escuchar y carpeta, y detrás lo propio del ajuste de volumen
    [InlineData("NovedadesView")]
    public void Los_dialogos_de_mezcla_y_Pendientes_siguen_el_mismo_orden(string vista)
        => TodosEnElOrdenComun(vista);

    // Las cuatro tablas de la página rekordbox tienen su menú (no solo la primera).
    [Fact]
    public void La_pagina_rekordbox_tiene_un_menu_en_cada_tabla()
    {
        var menus = MenusXaml("RekordboxView");
        Assert.Equal(4, menus.Count);
        Assert.All(menus, m => Assert.Equal(AccionesCancion.TextoEscuchar, m[0]));
        Assert.All(menus, m => Assert.Contains(AccionesCancion.TextoEditar, m));
        Assert.All(menus, m => Assert.Equal(AccionesCancion.TextoCarpeta, m[^1]));
    }

    // La tabla del orden en Tendencias y Playlists y la de mezcla del Asistente son la misma tabla:
    // el mismo menú, con «Empezar por esta» detrás de compatibles y versiones (antes iba la segunda).
    [Fact]
    public void La_tabla_del_orden_tiene_el_mismo_menu_que_la_de_mezcla_del_asistente()
        => Assert.Equal(new[] { AccionesCancion.TextoEscuchar, AccionesCancion.TextoEditar, AccionesCancion.TextoCompatibles,
                                AccionesCancion.TextoVersiones, "Empezar por esta", AccionesCancion.TextoCarpeta },
                        MenuXaml("OrdenMezclaPanel"));

    // «Editar etiquetas de esta canción» sale desactivada mientras otra página trabaja, y el rótulo
    // emergente lo dice también con la opción desactivada: antes el clic no hacía nada y no decía por qué.
    [Theory]
    [InlineData("ColeccionesView")]
    [InlineData("TrendsView")]
    [InlineData("PlaylistView")]
    [InlineData("OrdenMezclaPanel")]
    [InlineData("RekordboxView")]
    [InlineData("NovedadesView")]
    public void Editar_desactivada_dice_por_que(string vista)
    {
        var texto = File.ReadAllText(Fuente("Etiquetador.App", "Views", vista + ".axaml"));
        var items = Regex.Matches(texto, @"<MenuItem Header=""Editar etiquetas de esta canción""[^>]*/>", RegexOptions.Singleline);
        Assert.True(items.Count > 0, vista + " no tiene «Editar etiquetas de esta canción»");
        foreach (Match item in items)
        {
            Assert.Contains("ToolTip.ShowOnDisabled=\"True\"", item.Value);
            Assert.Contains("proceso en marcha", item.Value);
        }
    }

    // En la página rekordbox, «Sin preparar», «Nunca pinchadas» y «Rutas rotas» pueden enseñar archivos
    // que no están en la biblioteca: lo que trabaja con canciones de la biblioteca recibe la ruta solo si
    // lo están (RutaEnBiblioteca), y su rótulo lo dice. Con la ruta de la fila, el Editor se abría con
    // otra canción cargada.
    [Fact]
    public void En_rekordbox_editar_compatibles_y_versiones_solo_con_canciones_de_la_biblioteca()
    {
        var texto = File.ReadAllText(Fuente("Etiquetador.App", "Views", "RekordboxView.axaml"));
        foreach (var elegida in new[] { "SinPrepararElegida", "NuncaElegida", "RotaElegida" })
        {
            var items = Regex.Matches(texto, $@"<MenuItem Header=""[^""]+"" Command=""\{{x:Static AccionesCancion\.\w+\}}"" CommandParameter=""\{{Binding {elegida}[^""]*\}}""[^>]*/>",
                                      RegexOptions.Singleline);
            Assert.NotEmpty(items);
            foreach (Match m in items)
            {
                Assert.Contains($"{elegida}.RutaEnBiblioteca", m.Value);
                if (m.Value.Contains(AccionesCancion.TextoEditar)) Assert.Contains("no está en la biblioteca", m.Value);
            }
        }
    }

    [Fact]
    public void Editar_se_puede_solo_con_archivo_y_sin_nada_en_marcha()
    {
        Assert.True(AccionesCancion.PuedeEditar(@"E:\Musica\a.mp3", ocupada: false));
        Assert.False(AccionesCancion.PuedeEditar(@"E:\Musica\a.mp3", ocupada: true));
        Assert.False(AccionesCancion.PuedeEditar("", ocupada: false));
    }

    // El de Biblioteca se monta en el código (sus rótulos dependen de la selección): se leen los
    // rótulos de cada opción y el orden en que se añaden.
    [Fact]
    public void El_menu_de_la_biblioteca_sigue_el_orden_comun()
    {
        var codigo = File.ReadAllText(Fuente("Etiquetador.App", "Views", "LibraryView.axaml.cs"));
        var rotulos = Regex.Matches(codigo, @"var (\w+) = Opcion\(""([^""]+)""")
                           .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var orden = Regex.Match(codigo, @"foreach \(var c in new Control\[\] \{([^}]*)\}\)\s*menu\.Items\.Add");
        Assert.True(orden.Success, "no se encontró cómo se monta el menú de la Biblioteca");
        var menu = orden.Groups[1].Value.Split(',').Select(s => s.Trim())
                        .Where(rotulos.ContainsKey).Select(n => rotulos[n]).ToList();

        EnElOrdenComun("Biblioteca", menu);
        Assert.Equal(new[] { AccionesCancion.TextoEscuchar, AccionesCancion.TextoEditar, AccionesCancion.TextoCompatibles,
                             AccionesCancion.TextoVersiones, "Ordenar para mezclar la selección…", AccionesCancion.TextoCarpeta,
                             AccionesCancion.TextoPapelera }, menu);
    }

    [Fact]
    public void Ordenar_y_empezar_por_esta_ocupan_el_mismo_puesto_y_lo_ajeno_no_tiene()
    {
        Assert.Equal(AccionesCancion.Puesto("Ordenar para mezclar la selección (12)…"), AccionesCancion.Puesto("Empezar por esta"));
        Assert.True(AccionesCancion.Puesto(AccionesCancion.TextoVersiones) < AccionesCancion.Puesto("Empezar por esta"));
        Assert.True(AccionesCancion.Puesto("Empezar por esta") < AccionesCancion.Puesto(AccionesCancion.TextoCarpeta));
        Assert.True(AccionesCancion.Puesto("Enviar a la papelera (3) (espera a que termine el proceso)")
                    < AccionesCancion.Puesto("Añadir a pendientes"));
        Assert.Equal(-1, AccionesCancion.Puesto("Marcar las seleccionadas"));
    }
}
