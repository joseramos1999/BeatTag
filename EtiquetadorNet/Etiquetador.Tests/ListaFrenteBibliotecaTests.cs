using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// El cruce de una lista de fuera (chart de Tendencias, playlist) con la biblioteca: qué cuenta como
/// «lo tengo», con qué copia, y que el índice no congele la ventana en cada consulta.
/// </summary>
public class ListaFrenteBibliotecaTests
{
    private static Track Archivo(string nombre, string? artista = null, string? titulo = null)
        => new() { FilePath = Rutas.Archivo(nombre), Folder = Rutas.Carpeta, Artist = artista, Title = titulo };

    private static IndiceBiblioteca Indice(params Track[] canciones) => IndiceBiblioteca.Construir(canciones);

    // --- X6: todos los artistas del crédito, en cualquier orden, y los alias ---

    // Caso del hallazgo: el chart pone a Bizarrap delante y el archivo, a Shakira. Solo con el primer
    // artista las claves no coincidían y salía «No disponible» teniéndola.
    [Fact]
    public void Una_colaboracion_casa_aunque_el_archivo_ponga_los_artistas_en_otro_orden()
    {
        var bzrp = Archivo("Shakira, Bizarrap - Bzrp Music Sessions Vol 53.mp3");
        var i = Indice(bzrp);

        Assert.Equal(bzrp.FilePath, i.Buscar("Bizarrap, Shakira", "Bzrp Music Sessions, Vol. 53"));
    }

    // «Wisin & Yandel» en el chart y «Wisin y Yandel» en el archivo son el mismo dúo.
    [Theory]
    [InlineData("Wisin & Yandel")]
    [InlineData("Wisin y Yandel")]
    [InlineData("Yandel and Wisin")]
    [InlineData("Wisin + Yandel")]
    [InlineData("Wisin with Yandel")]
    public void Los_separadores_y_and_with_y_mas_tambien_parten_el_credito(string credito)
    {
        var rakata = Archivo("Wisin y Yandel - Rakata.mp3");
        Assert.Equal(rakata.FilePath, Indice(rakata).Buscar(credito, "Rakata"));
    }

    // Con un alias registrado (Cruzzi es Cruz Cafuné, de serie en artist_aliases.json), el chart y el
    // archivo pueden usar nombres distintos del mismo artista.
    [Fact]
    public void Un_alias_registrado_cuenta_como_el_mismo_artista()
    {
        var lunares = Archivo("Cruz Cafune - Contando Lunares.mp3", "Cruz Cafuné", "Contando Lunares");
        Assert.Equal(lunares.FilePath, Indice(lunares).Buscar("Cruzzi", "Contando Lunares"));
    }

    // Partir el crédito no puede abrir la puerta a cualquier cosa: el título sigue teniendo que casar.
    [Fact]
    public void Compartir_un_artista_no_basta_si_el_titulo_es_otro()
    {
        var i = Indice(Archivo("Bad Bunny, Jhay Cortez - Dakiti.mp3"));
        Assert.Equal("", i.Buscar("Bad Bunny", "Monaco"));
    }

    // Las palabras separadoras van entre espacios: no se parte «Xavi», «Andy» ni «Yandel».
    [Theory]
    [InlineData("Xavi", new[] { "Xavi" })]
    [InlineData("Andy Rivera", new[] { "Andy Rivera" })]
    [InlineData("Bad Bunny feat. Drake", new[] { "Bad Bunny", "Drake" })]
    [InlineData("Daddy Yankee x Snow", new[] { "Daddy Yankee", "Snow" })]
    [InlineData("Bizarrap, Shakira", new[] { "Bizarrap", "Shakira" })]
    [InlineData("Wisin y Yandel", new[] { "Wisin", "Yandel" })]
    public void Partir_el_credito(string credito, string[] esperados)
        => Assert.Equal(esperados, IndiceBiblioteca.PartirArtistas(credito));

    // De varias copias, la que antes se quiere pinchar: la extendida antes que la original, y la
    // original antes que un remix. También cuando cada copia entra por un artista distinto.
    [Fact]
    public void Entre_varias_copias_se_elige_la_mejor_version()
    {
        var remix = Archivo("Bad Bunny - Monaco (Yaniss Remix).mp3");
        var original = Archivo("Bad Bunny - Monaco.mp3");
        var extendida = Archivo("Bad Bunny, Jhay Cortez - Monaco (Extended Mix).mp3");

        Assert.Equal(extendida.FilePath, Indice(remix, original, extendida).Buscar("Bad Bunny", "Monaco"));
        Assert.Equal(original.FilePath, Indice(remix, original).Buscar("Bad Bunny", "Monaco"));
    }

    // Lo que ya se respetaba: una acapella no es tener la canción, aunque casen las claves.
    [Fact]
    public void La_acapella_sigue_sin_contar()
    {
        var i = Indice(Archivo("Bad Bunny - Monaco (Acapella).mp3"));
        Assert.Equal("", i.Buscar("Bad Bunny", "Monaco"));
        Assert.Equal(1, i.Apartadas);
    }

    // --- X9: alfabetos no latinos ---

    // Visto al revisar Tendencias: con la normalización de siempre, un título en hangul o en cirílico
    // quedaba vacío y todas esas filas casaban con el MISMO archivo.
    [Fact]
    public void Los_titulos_en_otros_alfabetos_casan_cada_uno_con_el_suyo()
    {
        var kino = Archivo("Кино - Группа крови.mp3", "Кино", "Группа крови");
        var basta = Archivo("Баста - Сансара.mp3", "Баста", "Сансара");
        var springDay = Archivo("BTS - 봄날.mp3", "BTS", "봄날");
        var i = Indice(kino, basta, springDay);

        Assert.Equal(kino.FilePath, i.Buscar("Кино", "Группа крови"));
        Assert.Equal(basta.FilePath, i.Buscar("Баста", "Сансара"));
        Assert.Equal(springDay.FilePath, i.Buscar("BTS", "봄날"));

        // Antes estas dos casaban con «bts|», es decir, con 봄날: el mismo archivo para todo el Top 50 de Corea.
        Assert.Equal("", i.Buscar("BTS", "피 땀 눈물"));
        Assert.Equal("", i.Buscar("방탄소년단", "Dynamite"));
    }

    // Una clave a medias no casa nunca: ni con un título que se queda en nada ni sin artista.
    [Theory]
    [InlineData("BTS", "")]
    [InlineData("BTS", "¡¡!!")]
    [InlineData("", "Monaco")]
    [InlineData("...", "Monaco")]
    public void Una_clave_vacia_nunca_casa(string artista, string titulo)
    {
        // Una biblioteca con algo bajo cada mitad vacía posible, por si se indexara.
        var i = Indice(Archivo("BTS - ¡¡!!.mp3", "BTS", "¡¡!!"), Archivo("... - Monaco.mp3", "...", "Monaco"));
        Assert.Empty(IndiceBiblioteca.ClavesDe(artista, titulo));
        Assert.Equal("", i.Buscar(artista, titulo));
    }

    // --- X1: el índice se reutiliza mientras la biblioteca no cambie, y es uno para las dos pestañas ---

    private static LibraryStore Biblioteca(string dir)
        => new(new AppConfig(), () => { }, new ScanCache(Path.Combine(dir, "scan.json")));

    [Fact]
    public async Task El_indice_se_reutiliza_hasta_que_la_biblioteca_cambia()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var compartido = IndiceBibliotecaCompartido.De(Biblioteca(dir));
            var canciones = new List<Track> { Archivo("Bad Bunny - Monaco.mp3"), Archivo("Karol G - Provenza.mp3") };

            var primero = await compartido.ObtenerAsync(canciones.ToList());
            var segundo = await compartido.ObtenerAsync(canciones.ToList());   // otro país, la misma biblioteca

            Assert.False(primero.Reutilizado);
            Assert.True(segundo.Reutilizado);
            Assert.Same(primero.Indice, segundo.Indice);
            Assert.Equal(1, compartido.Construcciones);

            // El Editor cambia el título en la misma canción, sin reescanear: hay que notarlo igual.
            canciones[1].Title = "Bichota";
            canciones[1].Artist = "Karol G";
            var tercero = await compartido.ObtenerAsync(canciones.ToList());
            Assert.False(tercero.Reutilizado);
            Assert.Equal(2, compartido.Construcciones);
            Assert.Equal(canciones[1].FilePath, tercero.Indice.Buscar("Karol G", "Bichota"));

            // Un escaneo que deja lo mismo (objetos nuevos, mismos datos) no obliga a rehacerlo.
            var reescaneo = canciones.Select(t => new Track { FilePath = t.FilePath, Folder = t.Folder, Artist = t.Artist, Title = t.Title }).ToList();
            Assert.True((await compartido.ObtenerAsync(reescaneo)).Reutilizado);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Tendencias_y_Playlists_comparten_el_indice_de_la_misma_biblioteca()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var una = Biblioteca(dir);
            var otra = Biblioteca(dir);
            Assert.Same(IndiceBibliotecaCompartido.De(una), IndiceBibliotecaCompartido.De(una));
            Assert.NotSame(IndiceBibliotecaCompartido.De(una), IndiceBibliotecaCompartido.De(otra));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void La_firma_cambia_con_la_ruta_el_artista_o_el_titulo()
    {
        var a = new List<Track> { Archivo("x.mp3", "A", "B") };
        var b = new List<Track> { Archivo("x.mp3", "AB", "") };   // mismas letras, otro reparto
        var c = new List<Track> { Archivo("y.mp3", "A", "B") };
        Assert.NotEqual(IndiceBiblioteca.FirmaDe(a), IndiceBiblioteca.FirmaDe(b));
        Assert.NotEqual(IndiceBiblioteca.FirmaDe(a), IndiceBiblioteca.FirmaDe(c));
        Assert.Equal(IndiceBiblioteca.FirmaDe(a), IndiceBiblioteca.FirmaDe(new List<Track> { Archivo("x.mp3", "A", "B") }));
    }

    // --- X11: numeración de la carpeta ---

    // Caso real: una playlist de Deezer de 456 canciones. Con dos cifras, en el USB quedaba
    // 1, 10, 100, 101… y el número dejaba de mantener el orden.
    [Fact]
    public void Una_lista_de_mas_de_99_se_numera_con_tres_cifras()
    {
        var ancho = ListaFrenteBibliotecaViewModel.AnchoNumeracion(Enumerable.Range(1, 456));
        Assert.Equal(3, ancho);
        Assert.Equal("001 - Tema.mp3", ListaFrenteBibliotecaViewModel.NombreNumerado(1, ancho, Rutas.Archivo("Tema.mp3")));
        Assert.Equal("100 - Tema.mp3", ListaFrenteBibliotecaViewModel.NombreNumerado(100, ancho, Rutas.Archivo("Tema.mp3")));

        // Ordenadas carácter a carácter, como un CDJ, quedan en el orden de la lista.
        var nombres = Enumerable.Range(1, 456).Select(p => ListaFrenteBibliotecaViewModel.NombreNumerado(p, ancho, "x.mp3")).ToList();
        Assert.Equal(nombres, nombres.OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    [Theory]
    [InlineData(20, 2)]
    [InlineData(50, 2)]
    [InlineData(99, 2)]
    [InlineData(100, 3)]
    [InlineData(1000, 4)]
    [InlineData(0, 2)]
    public void El_ancho_depende_de_la_posicion_mas_alta(int maxima, int ancho)
        => Assert.Equal(ancho, ListaFrenteBibliotecaViewModel.AnchoNumeracion(maxima == 0 ? Array.Empty<int>() : Enumerable.Range(1, maxima)));

    [Fact]
    public void Las_listas_cortas_siguen_con_dos_cifras()
        => Assert.Equal("07 - Tema.mp3", ListaFrenteBibliotecaViewModel.NombreNumerado(7, 2, Rutas.Archivo("Tema.mp3")));

    // --- C6: de cuándo es la lista ---

    private static readonly DateTime Ahora = new(2026, 10, 19, 18, 0, 0);

    [Fact]
    public void La_fecha_de_la_lista_se_dice_en_palabras()
    {
        Assert.Equal("Lista descargada hoy a las 14:32.",
            TrendsViewModel.TextoFechaLista(new DateTime(2026, 10, 19, 14, 32, 0), Ahora, ChartsProvider.VigenciaLista));
        Assert.StartsWith("Lista descargada ayer a las 23:10.",
            TrendsViewModel.TextoFechaLista(new DateTime(2026, 10, 18, 23, 10, 0), Ahora, ChartsProvider.VigenciaLista));
        Assert.StartsWith("Lista descargada el 02/10/2026 a las 09:05.",
            TrendsViewModel.TextoFechaLista(new DateTime(2026, 10, 2, 9, 5, 0), Ahora, ChartsProvider.VigenciaLista));
    }

    // Caso real de los registros: el Top 50 de España salió de caché durante un mes. Si lo que se
    // enseña es más viejo que la vigencia, es que la red no respondió: se dice, y cómo reintentarlo.
    [Fact]
    public void Una_lista_vieja_avisa_y_ofrece_actualizar()
    {
        var vieja = TrendsViewModel.TextoFechaLista(new DateTime(2026, 9, 11, 10, 0, 0), Ahora, ChartsProvider.VigenciaLista);
        Assert.Contains("Actualizar", vieja);

        var reciente = TrendsViewModel.TextoFechaLista(Ahora.AddHours(-2), Ahora, ChartsProvider.VigenciaLista);
        Assert.DoesNotContain("Actualizar", reciente);
    }

    [Fact]
    public void Sin_fecha_no_se_dice_nada()
        => Assert.Equal("", TrendsViewModel.TextoFechaLista(null, Ahora, ChartsProvider.VigenciaLista));

    // El caso de la revisión: se pulsa «Actualizar» a las 18:00 sin red y la guardada es de las 14:32,
    // dentro de las 6 horas de vigencia. Antes solo decía «Lista descargada hoy a las 14:32.», como si
    // la actualización pedida hubiera ido bien.
    [Fact]
    public void Si_actualizar_falla_se_dice_aunque_la_guardada_sea_reciente()
    {
        var guardada = new DateTime(2026, 10, 19, 14, 32, 0);
        Assert.True(TrendsViewModel.NoSeActualizo(guardada, pedidaEn: Ahora));

        var texto = TrendsViewModel.TextoFechaLista(guardada, Ahora, ChartsProvider.VigenciaLista, actualizacionFallida: true);
        Assert.StartsWith("Lista descargada hoy a las 14:32.", texto);
        Assert.Contains("No se ha podido descargar de nuevo", texto);
        Assert.Contains("Actualizar", texto);
    }

    // Al descargar bien, la copia guardada se reescribe después de pedirla: su fecha es posterior. Se
    // deja un margen por la resolución distinta del reloj y de la fecha del archivo.
    [Theory]
    [InlineData(3, false)]      // escrita 3 s después de pedirla
    [InlineData(0, false)]
    [InlineData(-1, false)]     // dentro del margen
    [InlineData(-60, true)]     // de hace un minuto: no es la que se acaba de pedir
    public void Se_sabe_si_la_lista_es_la_que_se_acaba_de_pedir(int segundos, bool noSeActualizo)
        => Assert.Equal(noSeActualizo, TrendsViewModel.NoSeActualizo(Ahora.AddSeconds(segundos), pedidaEn: Ahora));

    [Fact]
    public void Sin_nada_guardado_tras_pedirla_es_que_no_llego()
        => Assert.True(TrendsViewModel.NoSeActualizo(null, pedidaEn: Ahora));

    // Una actualización fallida con una lista además vieja da un solo aviso, no dos.
    [Fact]
    public void Un_solo_aviso_aunque_la_lista_ademas_sea_vieja()
    {
        var texto = TrendsViewModel.TextoFechaLista(new DateTime(2026, 9, 11, 10, 0, 0), Ahora, ChartsProvider.VigenciaLista, actualizacionFallida: true);
        Assert.Equal(1, texto.Split("Actualizar").Length - 1);
    }
}
