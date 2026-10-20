using System.Text;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// P6 · Pendientes: lo que falta de Tendencias y Playlists se guarda, se tacha solo al aparecer en la
/// biblioteca con el mismo cruce que Tendencias, y se puede buscar en el pool del usuario.
/// </summary>
public class PendientesTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Archivo => Path.Combine(_dir, AlmacenPendientes.NombreArchivo);

    /// <summary>Con la misma clave que Tendencias, como en la aplicación.</summary>
    private AlmacenPendientes Almacen() => new(Archivo, IndiceBiblioteca.ClavesDe);

    private static readonly DateTime Lunes = new(2026, 10, 19, 18, 0, 0);

    private static Track Cancion(string nombre, string? artista = null, string? titulo = null)
        => new() { FilePath = Rutas.Archivo(nombre), Folder = Rutas.Carpeta, Artist = artista, Title = titulo };

    // --- Altas ---

    // La misma canción, con los artistas en otro orden en otra lista, no entra dos veces.
    [Fact]
    public void La_misma_cancion_con_los_artistas_en_otro_orden_no_se_duplica()
    {
        var a = Almacen();
        var r1 = a.Anadir(new[] { new NuevaPendiente("Bizarrap, Shakira", "Bzrp Music Sessions, Vol. 53", "Top 50 de Spain (Spotify)") }, Lunes);
        var r2 = a.Anadir(new[] { new NuevaPendiente("Shakira, Bizarrap", "Bzrp Music Sessions Vol. 53", "Mi playlist") }, Lunes);

        Assert.Equal(new AltaPendientes(1, 0), r1);
        Assert.Equal(new AltaPendientes(0, 1), r2);
        Assert.Equal(1, a.Count);
    }

    // Las sesiones de Bizarrap llevan su nombre en todas. Con la limpieza de títulos (que quita los
    // números sueltos de dos cifras), «Vol. 52» y «Vol. 53» quedaban con la misma clave por Bizarrap:
    // la 53 no entraba en pendientes, y teniendo la 52 Tendencias la daba por «En la biblioteca».
    [Fact]
    public void Dos_volumenes_de_una_serie_son_canciones_distintas()
    {
        var a = Almacen();
        var r = a.Anadir(new[]
        {
            new NuevaPendiente("Bizarrap, Quevedo", "Bzrp Music Sessions, Vol. 52", "Top"),
            new NuevaPendiente("Bizarrap, Shakira", "Bzrp Music Sessions, Vol. 53", "Top"),
        }, Lunes);
        Assert.Equal(2, r.Anadidas);

        var vol52 = Cancion("Bizarrap, Quevedo - Bzrp Music Sessions Vol 52.mp3");
        var indice = IndiceBiblioteca.Construir(new[] { vol52 });
        Assert.Equal(vol52.FilePath, indice.Buscar("Bizarrap, Quevedo", "Bzrp Music Sessions, Vol. 52"));
        Assert.Equal("", indice.Buscar("Bizarrap, Shakira", "Bzrp Music Sessions, Vol. 53"));
    }

    // Las formas vistas en una biblioteca real: «Vol. 57», «#54», «Vol.066».
    [Theory]
    [InlineData("Bzrp Music Sessions, Vol. 53", "Bzrp Music Sessions, n53")]
    [InlineData("Bzrp Music Sessions, Vol.066", "Bzrp Music Sessions, n66")]
    [InlineData("Session #12", "Session n12")]
    [InlineData("Mix Part 128", "Mix n128")]
    [InlineData("Volume 0", "n0")]
    [InlineData("Provenza", "Provenza")]
    [InlineData("Volando", "Volando")]
    public void El_numero_de_una_serie_se_protege(string titulo, string esperado)
        => Assert.Equal(esperado, IndiceBiblioteca.PegarNumeroDeSerie(titulo));

    [Fact]
    public void Vol_con_ceros_y_volume_son_el_mismo_numero()
        => Assert.Equal(IndiceBiblioteca.ClaveTitulo("Bzrp Music Sessions, Vol. 66"), IndiceBiblioteca.ClaveTitulo("Bzrp Music Sessions Volume 066"));

    [Fact]
    public void Dos_iguales_en_la_misma_tanda_entran_una_vez()
    {
        var a = Almacen();
        var r = a.Anadir(new[]
        {
            new NuevaPendiente("Karol G", "Provenza", "Top"),
            new NuevaPendiente("KAROL G", "Provenza", "Top"),
        }, Lunes);
        Assert.Equal(new AltaPendientes(1, 1), r);
    }

    // Una conseguida que vuelve a faltar (se borró el archivo) puede volver a pendientes; la conseguida
    // se queda como historial.
    [Fact]
    public void Una_conseguida_no_impide_volver_a_anadirla()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Karol G", "Provenza", "Top") }, Lunes);
        var id = a.Todas()[0].Id;
        Assert.Equal(1, a.MarcarConseguidas(new[] { (id, Rutas.Archivo("Karol G - Provenza.mp3")) }, Lunes));

        var r = a.Anadir(new[] { new NuevaPendiente("Karol G", "Provenza", "Otra lista") }, Lunes.AddDays(3));
        Assert.Equal(1, r.Anadidas);
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public void Sin_titulo_no_entra()
        => Assert.Equal(0, Almacen().Anadir(new[] { new NuevaPendiente("Karol G", "  ", "Top") }, Lunes).Anadidas);

    // --- Persistencia ---

    [Fact]
    public void Se_guarda_y_se_vuelve_a_leer_entera()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Bad Bunny", "DtMF", "Top 50 de Spain (Spotify)", "https://open.spotify.com/track/x") }, Lunes);
        var id = a.Todas()[0].Id;
        a.MarcarConseguidas(new[] { (id, Rutas.Archivo("Bad Bunny - DtMF.mp3")) }, Lunes.AddDays(2));
        Assert.True(a.VolverAPendiente(id));
        Assert.Equal("", a.Guardar());

        var b = Almacen();
        Assert.Equal("", b.AvisoCarga);
        var p = Assert.Single(b.Todas());
        Assert.Equal("Bad Bunny", p.Artista);
        Assert.Equal("Top 50 de Spain (Spotify)", p.Origen);
        Assert.Equal("https://open.spotify.com/track/x", p.Enlace);
        Assert.Equal(Lunes, p.Alta);
        Assert.False(p.EstaConseguida);
        Assert.Equal(new[] { Rutas.Archivo("Bad Bunny - DtMF.mp3") }, p.Descartados);
    }

    // Es trabajo del usuario: un archivo dañado se aparta con otro nombre y se avisa, nunca se pisa.
    [Fact]
    public void Un_archivo_ilegible_se_aparta_y_se_avisa()
    {
        File.WriteAllText(Archivo, "[{\"Artista\": \"Karol G\", \"Titulo\": ", Encoding.UTF8);

        var a = Almacen();

        Assert.Contains("se ha apartado", a.AvisoCarga);
        Assert.False(a.SoloLectura);
        Assert.Equal(0, a.Count);
        Assert.Single(Directory.GetFiles(_dir, AlmacenPendientes.NombreArchivo + ".ilegible_*"));
    }

    [WindowsFact]
    public void Un_archivo_bloqueado_al_abrir_no_se_pisa()
    {
        var a = Almacen();
        a.Anadir(Enumerable.Range(1, 30).Select(i => new NuevaPendiente("Artista", $"Tema {i}", "Top")), Lunes);
        Assert.Equal("", a.Guardar());
        var antes = File.ReadAllBytes(Archivo);

        AlmacenPendientes b;
        using (new FileStream(Archivo, FileMode.Open, FileAccess.Read, FileShare.None))
            b = Almacen();

        Assert.True(b.SoloLectura);
        b.Anadir(new[] { new NuevaPendiente("Otro", "Tema", "Top") }, Lunes);
        Assert.NotEqual("", b.Guardar());
        Assert.Equal(antes, File.ReadAllBytes(Archivo));
    }

    // --- Tachar sola ---

    private static CambiosPendientes Cruzar(AlmacenPendientes a, params Track[] biblioteca)
    {
        var indice = IndiceBiblioteca.Construir(biblioteca);
        var rutas = new HashSet<string>(biblioteca.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
        return CrucePendientes.Calcular(a.Todas(), indice.Buscar, rutas.Contains);
    }

    [Fact]
    public void Se_tacha_al_aparecer_en_la_biblioteca_con_alias_y_artistas_en_otro_orden()
    {
        var a = Almacen();
        a.Anadir(new[]
        {
            new NuevaPendiente("Bizarrap, Shakira", "Bzrp Music Sessions, Vol. 53", "Top"),
            new NuevaPendiente("Cruzzi", "Contando Lunares", "Top"),
            new NuevaPendiente("Rosalía", "Despechá", "Top"),
        }, Lunes);
        var bzrp = Cancion("Shakira, Bizarrap - Bzrp Music Sessions Vol 53.mp3");
        var lunares = Cancion("Cruz Cafune - Contando Lunares.mp3", "Cruz Cafuné", "Contando Lunares");

        var cambios = Cruzar(a, bzrp, lunares);

        Assert.Equal(2, cambios.Conseguidas.Count);
        Assert.Equal(2, a.MarcarConseguidas(cambios.Conseguidas, Lunes.AddDays(1)));
        var todas = a.Todas();
        Assert.Equal(bzrp.FilePath, todas[0].Archivo);
        Assert.Equal(lunares.FilePath, todas[1].Archivo);
        Assert.Equal(Lunes.AddDays(1), todas[0].Conseguida);
        Assert.False(todas[2].EstaConseguida);
    }

    // Tener la acapella o un mashup no es tener la canción: el día que la busques solo tendrás la voz.
    [Fact]
    public void Una_acapella_o_un_mashup_no_la_tachan()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Bad Bunny", "Monaco", "Top") }, Lunes);

        var cambios = Cruzar(a,
            Cancion("Bad Bunny - Monaco (Acapella).mp3"),
            new Track { FilePath = Rutas.De("m", "Mashups", "Bad Bunny - Monaco.mp3"), Folder = Rutas.Carpeta });

        Assert.True(cambios.Vacio);
    }

    // El caso de «Volver a pendiente»: un remix con el mismo título la tachó. Ese archivo ya no la
    // tacha; la canción buena, cuando llega, sí.
    [Fact]
    public void Un_archivo_descartado_no_vuelve_a_tacharla_pero_otro_si()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Karol G", "Provenza", "Top") }, Lunes);
        var falsa = Cancion("Karol G - Provenza (Tech House Remix).mp3");
        a.MarcarConseguidas(Cruzar(a, falsa).Conseguidas, Lunes);
        var id = a.Todas()[0].Id;

        Assert.True(a.VolverAPendiente(id));
        Assert.True(Cruzar(a, falsa).Vacio);

        var buena = Cancion("Karol G - Provenza (Extended Mix).mp3");
        var cambios = Cruzar(a, falsa, buena);
        Assert.Equal(buena.FilePath, Assert.Single(cambios.Conseguidas).Ruta);
    }

    [Fact]
    public void Volver_a_pendiente_una_que_no_esta_conseguida_no_hace_nada()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Karol G", "Provenza", "Top") }, Lunes);
        Assert.False(a.VolverAPendiente(a.Todas()[0].Id));
    }

    // Las conseguidas siguen a su archivo cuando BeatTag lo renombra; si no aparece (disco
    // desconectado), se quedan como estaban: nunca se deshace la tachadura por eso.
    [Fact]
    public void Una_conseguida_sigue_a_su_archivo_renombrado_y_no_se_destacha_si_falta()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Karol G", "Provenza", "Top") }, Lunes);
        var antes = Cancion("karol g - provenza.mp3");
        a.MarcarConseguidas(Cruzar(a, antes).Conseguidas, Lunes);

        var despues = Cancion("Karol G - Provenza (Extended Mix).mp3");
        var cambios = Cruzar(a, despues);
        Assert.Empty(cambios.Conseguidas);
        Assert.Equal(1, a.CambiarArchivos(cambios.Movidas));
        Assert.Equal(despues.FilePath, a.Todas()[0].Archivo);
        Assert.Equal(Lunes, a.Todas()[0].Conseguida);

        // Biblioteca sin ella (el disco está desconectado): ni se mueve ni se destacha.
        Assert.True(Cruzar(a, Cancion("Otro - Tema.mp3")).Vacio);
        Assert.True(a.Todas()[0].EstaConseguida);
    }

    // Si el usuario la quita mientras se cruzaba fuera del hilo de la interfaz, el resultado no la revive.
    [Fact]
    public void Tachar_una_que_ya_se_quito_no_hace_nada()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Karol G", "Provenza", "Top") }, Lunes);
        var cambios = Cruzar(a, Cancion("Karol G - Provenza.mp3"));
        Assert.Equal(1, a.Quitar(new[] { a.Todas()[0].Id }));

        Assert.Equal(0, a.MarcarConseguidas(cambios.Conseguidas, Lunes));
        Assert.Equal(0, a.Count);
    }

    [Fact]
    public void Quitar_las_conseguidas_deja_las_pendientes()
    {
        var a = Almacen();
        a.Anadir(new[] { new NuevaPendiente("Karol G", "Provenza", "Top"), new NuevaPendiente("Rosalía", "Despechá", "Top") }, Lunes);
        a.MarcarConseguidas(Cruzar(a, Cancion("Karol G - Provenza.mp3")).Conseguidas, Lunes);

        Assert.Equal(1, a.QuitarConseguidas());
        Assert.Equal("Despechá", Assert.Single(a.Todas()).Titulo);
    }

    // --- Exportar ---

    [Fact]
    public void El_texto_es_una_linea_artista_titulo_por_cancion()
    {
        var conseguida = new Pendiente { Artista = "Karol G", Titulo = "Provenza", Conseguida = new DateTime(2026, 10, 21) };
        var texto = ExportacionPendientes.Texto(new[] { new Pendiente { Artista = "Rosalía", Titulo = "Despechá" }, conseguida });
        var lineas = texto.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Rosalía - Despechá", lineas[0]);
        Assert.Equal("Karol G - Provenza  · conseguida el 21/10/2026", lineas[1]);
    }

    // Separador «;» y todo entrecomillado, como los demás informes: un «;» en un título no parte la fila.
    [Fact]
    public void El_csv_lleva_todas_las_columnas_entrecomilladas()
    {
        var csv = ExportacionPendientes.Csv(new[]
        {
            new Pendiente { Artista = "Artista", Titulo = "Uno; dos \"tres\"", Origen = "Top", Alta = new DateTime(2026, 10, 19), Enlace = "https://x" },
        });
        var lineas = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("estado;artista;titulo;origen;anadida;conseguida;archivo;enlace", lineas[0]);
        Assert.Equal("\"pendiente\";\"Artista\";\"Uno; dos \"\"tres\"\"\";\"Top\";\"2026-10-19\";\"\";\"\";\"https://x\"", lineas[1]);
    }

    // --- Buscar en mi pool ---

    [Theory]
    [InlineData("", "")]
    [InlineData("https://www.mipool.com/search?q={q}", "")]
    [InlineData("https://www.mipool.com/search?q={Q}&genre=latin", "")]
    [InlineData("https://www.mipool.com/search", "Falta {q}")]
    [InlineData("www.mipool.com/search?q={q}", "https://")]
    [InlineData("ftp://mipool.com/{q}", "https://")]
    public void La_plantilla_del_pool_se_valida(string plantilla, string motivo)
    {
        var error = BusquedaEnPool.Validar(plantilla);
        if (motivo.Length == 0) Assert.Equal("", error);
        else Assert.Contains(motivo, error);
    }

    // Los pools exigen casi siempre todas las palabras: el primer artista y el título sin colaboraciones.
    [Theory]
    [InlineData("Bad Bunny, Jhay Cortez", "DÁKITI", "Bad Bunny DÁKITI")]
    [InlineData("Karol G", "Provenza (feat. Nadie)", "Karol G Provenza")]
    [InlineData("Daddy Yankee x Snow", "Con Calma", "Daddy Yankee Con Calma")]
    [InlineData("Quevedo", "Columbia [with Alguien]", "Quevedo Columbia")]
    [InlineData("Peso Pluma", "Lady Gaga ft. Gabito Ballesteros", "Peso Pluma Lady Gaga")]
    [InlineData("Ozuna", "Hey Mor - Remix", "Ozuna Hey Mor - Remix")]
    public void Se_busca_el_primer_artista_y_el_titulo_sin_colaboraciones(string artista, string titulo, string esperado)
        => Assert.Equal(esperado, BusquedaEnPool.TextoBusqueda(artista, titulo));

    [Fact]
    public void La_busqueda_va_codificada_en_la_direccion()
    {
        var url = BusquedaEnPool.Url("https://www.mipool.com/search?q={q}&tipo=audio", "Rosalía & C. Tangana", "Llylm", out var motivo);
        Assert.Equal("", motivo);
        Assert.Equal("https://www.mipool.com/search?q=Rosal%C3%ADa%20Llylm&tipo=audio", url);
    }

    // Sin plantilla, el botón explica dónde se configura en vez de no hacer nada.
    [Fact]
    public void Sin_plantilla_se_dice_donde_configurarla()
    {
        Assert.Equal("", BusquedaEnPool.Url("", "Karol G", "Provenza", out var motivo));
        Assert.Contains("Ajustes → Pendientes", motivo);

        Assert.Equal("", BusquedaEnPool.Url("https://www.mipool.com/search", "Karol G", "Provenza", out motivo));
        Assert.Contains("no es válida", motivo);
    }

    // --- Textos ---

    [Theory]
    [InlineData(3, 2, "3 canciones añadidas a Pendientes (2 ya estaban).")]
    [InlineData(1, 0, "1 canción añadida a Pendientes.")]
    [InlineData(0, 1, "Esa canción ya estaba en Pendientes.")]
    [InlineData(0, 4, "Esas 4 canciones ya estaban en Pendientes.")]
    public void El_alta_dice_cuantas_entraron(int anadidas, int yaEstaban, string empieza)
        => Assert.StartsWith(empieza.TrimEnd('.'), ListaFrenteBibliotecaViewModel.TextoAlta(anadidas, yaEstaban));

    [Theory]
    [InlineData(0, 0, "La lista de pendientes está vacía.")]
    [InlineData(1, 1, "1 pendiente · 1 conseguida")]
    [InlineData(12, 5, "12 pendientes · 5 conseguidas")]
    public void El_resumen_cuenta_pendientes_y_conseguidas(int pendientes, int conseguidas, string esperado)
        => Assert.Equal(esperado, PendientesViewModel.TextoResumen(pendientes, conseguidas));

    // =============================================================================================
    // Pulido de la tanda 3
    // =============================================================================================

    /// <summary>Una pendiente tachada el lunes con <paramref name="archivo"/>. Devuelve su Id.</summary>
    private static string Tachada(AlmacenPendientes a, Track archivo, string artista = "Karol G", string titulo = "Provenza")
    {
        a.Anadir(new[] { new NuevaPendiente(artista, titulo, "Top") }, Lunes);
        var id = a.Todas().Single(p => p.Titulo == titulo).Id;
        Assert.Equal(1, a.MarcarConseguidas(new[] { (id, archivo.FilePath) }, Lunes));
        return id;
    }

    // --- «Volver a pendiente» se puede deshacer ---

    // Revisión: pulsado por error sobre una conseguida correcta, la dejaba pendiente para siempre: ese
    // archivo ya no la tachaba y no había forma de sacarlo de Descartados.
    [Fact]
    public void Volver_a_pendiente_se_deshace_con_la_fecha_de_entonces()
    {
        var a = Almacen();
        var buena = Cancion("Karol G - Provenza.mp3");
        var id = Tachada(a, buena);

        Assert.True(a.VolverAPendiente(id));
        var p = a.Todas()[0];
        Assert.False(p.EstaConseguida);
        Assert.Equal(new[] { buena.FilePath }, p.Descartados);
        Assert.Equal(Lunes, p.ConseguidaAntes);

        var (archivo, tachada) = a.RecuperarDescartado(id, ruta => ruta == buena.FilePath);
        Assert.Equal(buena.FilePath, archivo);
        Assert.True(tachada);
        p = a.Todas()[0];
        Assert.Equal(Lunes, p.Conseguida);          // la de entonces, no la de hoy
        Assert.Equal(buena.FilePath, p.Archivo);
        Assert.Empty(p.Descartados);
        Assert.Null(p.ConseguidaAntes);
    }

    // Si el archivo ya no está en la biblioteca, deja de estar descartado y el próximo cruce decide.
    [Fact]
    public void Recuperar_un_descartado_que_no_esta_en_la_biblioteca_la_deja_pendiente()
    {
        var a = Almacen();
        var falsa = Cancion("Karol G - Provenza (Tech House Remix).mp3");
        var id = Tachada(a, falsa);
        a.VolverAPendiente(id);

        var (archivo, tachada) = a.RecuperarDescartado(id, _ => false);
        Assert.Equal(falsa.FilePath, archivo);
        Assert.False(tachada);
        Assert.False(a.Todas()[0].EstaConseguida);
        Assert.Empty(a.Todas()[0].Descartados);

        // Ese archivo vuelve a contar: si reaparece en la biblioteca, la tacha.
        Assert.Equal(falsa.FilePath, Assert.Single(Cruzar(a, falsa).Conseguidas).Ruta);
        // Sin descartados no hay nada que deshacer.
        Assert.Equal(("", false), a.RecuperarDescartado(id, _ => true));
        Assert.Equal(("", false), a.RecuperarDescartado("no-existe", _ => true));
    }

    // Se deshace del último al primero, y el mismo archivo descartado dos veces está una sola.
    [Fact]
    public void Se_deshace_el_ultimo_descartado_y_no_se_repiten()
    {
        var a = Almacen();
        var uno = Cancion("Karol G - Provenza (Remix).mp3");
        var dos = Cancion("Karol G - Provenza (Edit).mp3");
        var id = Tachada(a, uno);
        a.VolverAPendiente(id);
        a.MarcarConseguidas(new[] { (id, dos.FilePath) }, Lunes.AddDays(2));
        a.VolverAPendiente(id);
        a.MarcarConseguidas(new[] { (id, uno.FilePath) }, Lunes.AddDays(3));
        a.VolverAPendiente(id);

        Assert.Equal(new[] { dos.FilePath, uno.FilePath }, a.Todas()[0].Descartados);
        Assert.Equal(uno.FilePath, a.RecuperarDescartado(id, _ => true).Archivo);
        Assert.Equal(Lunes.AddDays(3), a.Todas()[0].Conseguida);
    }

    [Fact]
    public void La_fecha_para_deshacer_se_guarda_en_el_archivo()
    {
        var a = Almacen();
        var id = Tachada(a, Cancion("Karol G - Provenza.mp3"));
        a.VolverAPendiente(id);
        Assert.Equal("", a.Guardar());

        var b = Almacen();
        Assert.Equal(Lunes, b.Todas()[0].ConseguidaAntes);
        Assert.True(b.RecuperarDescartado(id, _ => true).Tachada);
        Assert.Equal(Lunes, b.Todas()[0].Conseguida);
    }

    [Theory]
    [InlineData(0, "Pendiente")]
    [InlineData(1, "Pendiente (1 descartado)")]
    [InlineData(3, "Pendiente (3 descartados)")]
    public void El_estado_dice_si_hay_descartados(int descartados, string esperado)
    {
        var p = new Pendiente { Descartados = Enumerable.Range(0, descartados).Select(i => $"x{i}.mp3").ToList() };
        Assert.Equal(esperado, PendienteFila.TextoEstado(p));
        Assert.Equal(descartados > 0, new PendienteFila(p).TieneDescartados);
        p.Conseguida = Lunes;
        Assert.Equal("Conseguida el 19/10/2026", PendienteFila.TextoEstado(p));
    }

    [Fact]
    public void Los_textos_de_volver_a_pendiente_dicen_como_deshacerlo()
    {
        Assert.StartsWith("1 canción vuelve a pendientes. El archivo con el que se tachó ya no cuenta para ella.",
                          PendientesViewModel.TextoVueltaAPendiente(1));
        Assert.StartsWith("3 canciones vuelven a pendientes.", PendientesViewModel.TextoVueltaAPendiente(3));
        Assert.Contains("«Volver a contar el archivo descartado»", PendientesViewModel.TextoVueltaAPendiente(2));

        Assert.Equal("El archivo descartado vuelve a contar y la canción vuelve a estar conseguida.", PendientesViewModel.TextoRecuperados(1, 1));
        Assert.Equal("3 archivos descartados vuelven a contar; 1 canción vuelve a estar conseguida.", PendientesViewModel.TextoRecuperados(3, 1));
        Assert.Equal("3 archivos descartados vuelven a contar; 2 canciones vuelven a estar conseguidas.", PendientesViewModel.TextoRecuperados(3, 2));
        Assert.StartsWith("El archivo descartado vuelve a contar: si está", PendientesViewModel.TextoRecuperados(1, 0));
        Assert.StartsWith("2 archivos descartados vuelven a contar: si están", PendientesViewModel.TextoRecuperados(2, 0));
    }

    // --- Un descartado que BeatTag renombra sigue descartado ---

    // Revisión: el descarte se compara por ruta; si Enriquecer renombraba después el remix descartado,
    // volvía a tachar la canción con su nombre nuevo.
    [Fact]
    public void Un_descartado_renombrado_sigue_descartado()
    {
        var a = Almacen();
        var falsa = Cancion("karol g - provenza (tech house remix).mp3");
        var id = Tachada(a, falsa);
        a.VolverAPendiente(id);

        var renombrada = Cancion("Karol G - Provenza (Tech House Remix) [Extended].mp3");
        var biblioteca = new HashSet<string>(new[] { renombrada.FilePath }, StringComparer.OrdinalIgnoreCase);
        var renombres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [falsa.FilePath] = renombrada.FilePath };

        // Sin seguir el renombrado, el mismo remix con su nombre nuevo volvía a tacharla.
        Assert.Single(Cruzar(a, renombrada).Conseguidas);
        Assert.Equal(new[] { falsa.FilePath }, CrucePendientes.DescartadosFuera(a.Todas(), biblioteca.Contains));
        var cambios = CrucePendientes.SeguirDescartados(a.Todas(), renombres, biblioteca.Contains);
        Assert.Equal((id, falsa.FilePath, renombrada.FilePath), Assert.Single(cambios));
        Assert.Equal(1, a.CambiarDescartados(cambios));

        Assert.Equal(new[] { renombrada.FilePath }, a.Todas()[0].Descartados);
        Assert.True(Cruzar(a, renombrada).Vacio);
        Assert.Empty(CrucePendientes.DescartadosFuera(a.Todas(), biblioteca.Contains));
        // Y «Volver a contar el archivo descartado» deshace con el nombre nuevo.
        Assert.Equal(renombrada.FilePath, a.RecuperarDescartado(id, biblioteca.Contains).Archivo);
    }

    [Fact]
    public void Solo_se_sigue_un_descartado_que_salio_de_la_biblioteca_hacia_ella()
    {
        var a = Almacen();
        var falsa = Cancion("Karol G - Provenza (Remix).mp3");
        var id = Tachada(a, falsa);
        a.VolverAPendiente(id);
        var otra = Cancion("Karol G - Provenza (Remix) (1).mp3");
        var renombres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [falsa.FilePath] = otra.FilePath };

        // Sigue en la biblioteca con su nombre: no se toca.
        Assert.Empty(CrucePendientes.SeguirDescartados(a.Todas(), renombres, r => r == falsa.FilePath || r == otra.FilePath));
        // La ruta nueva tampoco está: no puede tacharla, no hace falta seguirla.
        Assert.Empty(CrucePendientes.SeguirDescartados(a.Todas(), renombres, _ => false));
        Assert.Empty(CrucePendientes.SeguirDescartados(a.Todas(), new Dictionary<string, string>(), _ => true));

        // Si la ruta nueva ya estaba descartada, la vieja sobra.
        a.MarcarConseguidas(new[] { (id, otra.FilePath) }, Lunes);
        a.VolverAPendiente(id);
        Assert.Equal(1, a.CambiarDescartados(new[] { (id, falsa.FilePath, otra.FilePath) }));
        Assert.Equal(new[] { otra.FilePath }, a.Todas()[0].Descartados);
        Assert.Equal(0, a.CambiarDescartados(new[] { (id, falsa.FilePath, otra.FilePath) }));
    }

    // --- interfaz-10: una conseguida cuyo archivo se envía a la papelera vuelve a pendiente ---

    [Fact]
    public void Una_conseguida_cuyo_archivo_ya_no_existe_vuelve_a_pendiente()
    {
        var a = Almacen();
        var tema = Cancion("Karol G - Provenza.mp3");
        var id = Tachada(a, tema);
        var otra = Cancion("Otro - Tema.mp3");

        // Enviada a la papelera desde Biblioteca o el Asistente: sale de la biblioteca y ya no existe.
        var indice = IndiceBiblioteca.Construir(new[] { otra });
        var cambios = CrucePendientes.Calcular(a.Todas(), indice.Buscar, r => r == otra.FilePath, sinArchivo: r => r == tema.FilePath);
        Assert.False(cambios.Vacio);
        Assert.Equal((id, tema.FilePath), Assert.Single(cambios.SinArchivo));
        Assert.Equal(1, a.Reabrir(cambios.SinArchivo));

        var p = a.Todas()[0];
        Assert.False(p.EstaConseguida);
        Assert.Equal("", p.Archivo);
        Assert.Empty(p.Descartados);   // no era una copia equivocada: si vuelve de la papelera, la tacha
        Assert.Equal(tema.FilePath, Assert.Single(Cruzar(a, tema).Conseguidas).Ruta);
    }

    [Fact]
    public void Una_conseguida_no_se_destacha_si_su_archivo_existe_o_hay_otra_copia()
    {
        var a = Almacen();
        var tema = Cancion("Karol G - Provenza.mp3");
        var id = Tachada(a, tema);

        // Carpeta desmarcada en Biblioteca o disco desconectado: el archivo no consta como desaparecido.
        var vacia = IndiceBiblioteca.Construir(Array.Empty<Track>());
        Assert.True(CrucePendientes.Calcular(a.Todas(), vacia.Buscar, _ => false, sinArchivo: _ => false).Vacio);
        Assert.True(CrucePendientes.Calcular(a.Todas(), vacia.Buscar, _ => false).Vacio);

        // «Sustituir» con otra extensión: hay otra copia, la sigue en vez de destacharse.
        var nueva = Cancion("Karol G - Provenza.aiff");
        var conNueva = IndiceBiblioteca.Construir(new[] { nueva });
        var cambios = CrucePendientes.Calcular(a.Todas(), conNueva.Buscar, r => r == nueva.FilePath, sinArchivo: _ => true);
        Assert.Empty(cambios.SinArchivo);
        Assert.Equal((id, nueva.FilePath), Assert.Single(cambios.Movidas));

        // Si entre el cruce y el resultado ya se tachó con otro archivo, el resultado no la destacha.
        a.CambiarArchivos(cambios.Movidas);
        Assert.Equal(0, a.Reabrir(new[] { (id, tema.FilePath) }));
        Assert.True(a.Todas()[0].EstaConseguida);
    }

    [Fact]
    public void Un_archivo_solo_ha_desaparecido_si_su_carpeta_sigue_ahi()
    {
        var carpeta = Path.Combine(_dir, "musica");
        Directory.CreateDirectory(carpeta);
        var archivo = Path.Combine(carpeta, "tema.mp3");
        File.WriteAllText(archivo, "x");

        Assert.False(CrucePendientes.ArchivoDesaparecido(archivo));
        File.Delete(archivo);
        Assert.True(CrucePendientes.ArchivoDesaparecido(archivo));
        // Sin la carpeta puede ser un disco desconectado: no se da por perdido.
        Assert.False(CrucePendientes.ArchivoDesaparecido(Path.Combine(_dir, "no-montado", "tema.mp3")));
        Assert.False(CrucePendientes.ArchivoDesaparecido(""));
    }

    // --- El menú de una sola canción actúa sobre la fila pulsada ---

    [Fact]
    public void Una_opcion_de_una_cancion_actua_sobre_la_fila_pulsada()
    {
        var elegidas = new[] { "a", "b", "c" };
        Assert.Equal("c", Etiquetador.App.Views.AccionesLista.FilaParaUnaCancion("c", "a", elegidas));
        // La pulsada ya no está elegida (se cambió la selección después): manda la elegida.
        Assert.Equal("a", Etiquetador.App.Views.AccionesLista.FilaParaUnaCancion("z", "a", elegidas));
        Assert.Equal("a", Etiquetador.App.Views.AccionesLista.FilaParaUnaCancion(null, "a", elegidas));
        Assert.Equal("b", Etiquetador.App.Views.AccionesLista.FilaParaUnaCancion<string>(null, null, new[] { "b" }));
        Assert.Null(Etiquetador.App.Views.AccionesLista.FilaParaUnaCancion<string>(null, null, Array.Empty<string>()));
    }
}
