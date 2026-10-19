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
}
