using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// La bandeja de entrada: la música nueva se revisa ANTES de entrar en la biblioteca. Lo que se
/// prueba es que los avisos salgan cuando tienen que salir -y no cuando no-, que el estado que pone
/// el usuario no retroceda solo, y que pasar a la biblioteca nunca se lleve por delante un archivo.
/// </summary>
public class BandejaTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Track Tema(string artista, string titulo, string ruta, int kbps = 320, string genero = "House", uint anio = 2024) => new()
    {
        Artist = artista, Title = titulo, FilePath = ruta, Bitrate = kbps, Genre = genero, Year = anio,
    };

    private static IReadOnlyList<AvisoBandeja> Revisar(Track t, IEnumerable<Track> biblioteca, IEnumerable<Track>? bandeja = null,
                                                      string destino = "", Func<string, bool>? existe = null)
        => RevisionBandeja.Revisar(t, new IndiceCanciones(biblioteca), new IndiceCanciones(bandeja ?? new[] { t }),
                                   destino, existe ?? (_ => false));

    // --- Avisos ---

    [Fact]
    public void Una_cancion_limpia_no_tiene_avisos()
    {
        var t = Tema("Fisher", "Losing It", @"C:\Bandeja\Fisher - Losing It.mp3");

        Assert.Empty(Revisar(t, new[] { Tema("Otro", "Otra", @"C:\Musica\Otro - Otra.mp3") }));
    }

    [Fact]
    public void Avisa_si_ya_esta_en_la_biblioteca()
    {
        var t = Tema("Fisher", "Losing It", @"C:\Bandeja\Fisher - Losing It.mp3");
        var biblioteca = new[] { Tema("FISHER", "Losing it", @"C:\Musica\Fisher - Losing It.mp3") };

        var aviso = Assert.Single(Revisar(t, biblioteca));
        Assert.Equal(TipoAviso.YaEnBiblioteca, aviso.Tipo);
    }

    // Para un DJ, saber que ya tiene la extended importa tanto como un duplicado exacto.
    [Fact]
    public void Avisa_si_hay_otra_version_y_dice_cual()
    {
        var t = Tema("Fisher", "Losing It", @"C:\Bandeja\Fisher - Losing It.mp3");
        var biblioteca = new[] { Tema("Fisher", "Losing It (Extended Mix)", @"C:\Musica\Fisher - Losing It (Extended Mix).mp3") };

        var aviso = Assert.Single(Revisar(t, biblioteca));
        Assert.Equal(TipoAviso.OtraVersion, aviso.Tipo);
        Assert.Contains("Extended", aviso.Texto);
    }

    // Si es exacta, no se avisa además de «otra versión»: sería decir lo mismo dos veces.
    [Fact]
    public void Una_exacta_no_se_avisa_tambien_como_otra_version()
    {
        var t = Tema("Fisher", "Losing It", @"C:\Bandeja\a.mp3");
        var biblioteca = new[]
        {
            Tema("Fisher", "Losing It", @"C:\Musica\b.mp3"),
            Tema("Fisher", "Losing It (Extended Mix)", @"C:\Musica\c.mp3"),
        };

        Assert.DoesNotContain(Revisar(t, biblioteca), a => a.Tipo == TipoAviso.OtraVersion);
    }

    // Con solo el título, cualquier «Intro» coincidiría con todas las demás.
    [Fact]
    public void Sin_artista_no_se_da_por_duplicada()
    {
        var t = Tema("", "Intro", @"C:\Bandeja\Intro.mp3");
        var biblioteca = new[] { Tema("", "Intro", @"C:\Musica\Intro.mp3") };

        Assert.DoesNotContain(Revisar(t, biblioteca), a => a.Tipo is TipoAviso.YaEnBiblioteca or TipoAviso.OtraVersion);
    }

    [Fact]
    public void Avisa_de_copias_repetidas_dentro_de_la_bandeja()
    {
        var a = Tema("Fisher", "Losing It", @"C:\Bandeja\a.mp3");
        var b = Tema("Fisher", "Losing It", @"C:\Bandeja\b.mp3");

        Assert.Contains(Revisar(a, Array.Empty<Track>(), new[] { a, b }), x => x.Tipo == TipoAviso.RepetidaEnBandeja);
    }

    [Fact]
    public void Avisa_de_calidad_baja_y_de_etiquetas_que_faltan()
    {
        var t = Tema("Fisher", "Losing It", @"C:\Bandeja\a.mp3", kbps: 128, genero: "", anio: 0);

        var tipos = Revisar(t, Array.Empty<Track>()).Select(x => x.Tipo).ToList();

        Assert.Contains(TipoAviso.CalidadBaja, tipos);
        Assert.Contains(TipoAviso.Incompleta, tipos);
    }

    // Pasarla a la biblioteca con un nombre ya usado en el destino no puede hacerse; mejor saberlo antes.
    // Las rutas se montan con Path.Combine y no escritas a mano: con «C:\Bandeja\x.mp3» en macOS la
    // barra invertida no separa carpetas, el nombre del archivo sale entero y la prueba falla sin que
    // haya nada roto. Pasó en la CI de macOS.
    [Fact]
    public void Avisa_si_el_nombre_ya_existe_en_el_destino()
    {
        var destino = Path.Combine(_dir, "Musica", "House");
        var t = Tema("Fisher", "Losing It", Path.Combine(_dir, "Bandeja", "Fisher - Losing It.mp3"));
        var ocupado = Path.Combine(destino, "Fisher - Losing It.mp3");

        var avisos = Revisar(t, Array.Empty<Track>(), destino: destino, existe: p => p == ocupado);

        Assert.Contains(avisos, x => x.Tipo == TipoAviso.NombreOcupado);
    }

    // --- Carpeta de la bandeja ---

    // Dentro de la biblioteca, cada canción se encontraría a sí misma como duplicada.
    [Theory]
    [InlineData("Musica/Bandeja", "Musica")]   // dentro
    [InlineData("Musica", "Musica/House")]     // la contiene
    [InlineData("Musica", "Musica")]           // la misma
    public void La_bandeja_no_puede_solaparse_con_la_biblioteca(string bandeja, string biblioteca)
    {
        string R(string s) => Path.Combine(_dir, s.Replace('/', Path.DirectorySeparatorChar));

        Assert.NotEqual("", RevisionBandeja.ValidarCarpeta(R(bandeja), new[] { R(biblioteca) }, _ => true));
    }

    [Fact]
    public void Una_carpeta_aparte_vale_como_bandeja()
    {
        var bandeja = Path.Combine(_dir, "Descargas");
        var musica = Path.Combine(_dir, "Musica");

        Assert.Equal("", RevisionBandeja.ValidarCarpeta(bandeja, new[] { musica }, _ => true));
    }

    // --- Estados ---

    // Pulsar Analizar otra vez no puede deshacer lo que el usuario ya decidió.
    [Theory]
    [InlineData(EstadoBandeja.Recibida, EstadoBandeja.Analizada)]
    [InlineData(EstadoBandeja.Analizada, EstadoBandeja.Analizada)]
    [InlineData(EstadoBandeja.Revisada, EstadoBandeja.Revisada)]
    [InlineData(EstadoBandeja.Preparada, EstadoBandeja.Preparada)]
    public void Analizar_solo_hace_avanzar_a_las_recien_llegadas(EstadoBandeja antes, EstadoBandeja despues)
        => Assert.Equal(despues, AlmacenBandeja.TrasAnalizar(antes));

    [Fact]
    public void El_estado_se_recuerda_entre_sesiones()
    {
        var archivo = Path.Combine(_dir, "bandeja.json");
        var almacen = new AlmacenBandeja(archivo);
        var llegada = new DateTime(2026, 9, 1, 10, 0, 0);
        almacen.Registrar(@"C:\Bandeja\a.mp3", llegada);
        almacen.Marcar(@"C:\Bandeja\a.mp3", EstadoBandeja.Preparada);
        Assert.Equal("", almacen.Guardar());

        var leida = new AlmacenBandeja(archivo).Obtener(@"c:\bandeja\A.mp3");

        Assert.NotNull(leida);
        Assert.Equal(EstadoBandeja.Preparada, leida!.Estado);
        Assert.Equal(llegada, leida.Llegada);
    }

    // Registrar otra vez una que ya estaba no la devuelve a recibida ni le cambia la fecha.
    [Fact]
    public void Volver_a_registrar_no_reinicia_la_entrada()
    {
        var almacen = new AlmacenBandeja(Path.Combine(_dir, "bandeja.json"));
        var primera = new DateTime(2026, 9, 1);
        almacen.Registrar("a.mp3", primera);
        almacen.Marcar("a.mp3", EstadoBandeja.Revisada);

        var e = almacen.Registrar("a.mp3", primera.AddDays(3));

        Assert.Equal(EstadoBandeja.Revisada, e.Estado);
        Assert.Equal(primera, e.Llegada);
    }

    [Fact]
    public void Olvida_las_que_ya_no_estan_en_la_carpeta()
    {
        var almacen = new AlmacenBandeja(Path.Combine(_dir, "bandeja.json"));
        almacen.Registrar("a.mp3", DateTime.Now);
        almacen.Registrar("b.mp3", DateTime.Now);

        Assert.Equal(1, almacen.Podar(new[] { "a.mp3" }));
        Assert.Null(almacen.Obtener("b.mp3"));
    }

    // --- Pasar a la biblioteca ---

    [Fact]
    public void Pasar_a_la_biblioteca_mueve_el_archivo()
    {
        var bandeja = Directory.CreateDirectory(Path.Combine(_dir, "Bandeja")).FullName;
        var musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        var origen = Path.Combine(bandeja, "tema.mp3");
        Mp3Fixture.WriteMinMp3(origen);

        var t = TrasladoBandeja.Mover(origen, musica);

        Assert.True(t.Ok, t.Error);
        Assert.False(File.Exists(origen));
        Assert.True(File.Exists(Path.Combine(musica, "tema.mp3")));
    }

    // LO QUE NO PUEDE PASAR NUNCA: machacar una canción de la biblioteca al ordenar la bandeja.
    [Fact]
    public void Pasar_a_la_biblioteca_nunca_sobrescribe()
    {
        var bandeja = Directory.CreateDirectory(Path.Combine(_dir, "Bandeja")).FullName;
        var musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica")).FullName;
        var origen = Path.Combine(bandeja, "tema.mp3");
        var existente = Path.Combine(musica, "tema.mp3");
        Mp3Fixture.WriteMinMp3(origen, frames: 10);
        Mp3Fixture.WriteMinMp3(existente, frames: 40);
        var tamanoOriginal = new FileInfo(existente).Length;

        var t = TrasladoBandeja.Mover(origen, musica);

        Assert.False(t.Ok);
        Assert.True(File.Exists(origen));                                  // sigue en la bandeja
        Assert.Equal(tamanoOriginal, new FileInfo(existente).Length);      // y la de la biblioteca, intacta
    }
}
