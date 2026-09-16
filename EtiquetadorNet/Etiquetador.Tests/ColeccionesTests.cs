using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Las colecciones inteligentes: listas que se rellenan solas a partir de unas condiciones. Lo que
/// importa es que no se cuele nada que no cumple, porque lo que sale de aquí acaba en una lista
/// que se pincha.
/// </summary>
public class ColeccionesTests
{
    private static Track Tema(string artista = "Artista", string titulo = "Tema", string genero = "House",
                              uint bpm = 124, string archivo = "") => new()
    {
        Artist = artista,
        Title = titulo,
        Genre = genero,
        Bpm = bpm,
        FilePath = archivo.Length > 0 ? archivo : $@"C:\Musica\{artista} - {titulo}.mp3",
    };

    private static FichaDj Ficha(int energia = 0, MomentoSet momentos = MomentoSet.Ninguno,
                                 TipoVoz voz = TipoVoz.SinIndicar, string etiquetas = "") => new()
    {
        Energia = energia, Momentos = momentos, Voz = voz, Etiquetas = Fichas.PartirLista(etiquetas),
    };

    // El ejemplo de siempre: «house vocal, energía 6-7, para apertura».
    [Fact]
    public void House_vocal_de_energia_media_para_abrir()
    {
        var c = new ColeccionInteligente
        {
            Genero = "house", EnergiaMin = 6, EnergiaMax = 7, Voz = TipoVoz.Vocal, Momentos = MomentoSet.Apertura,
        };

        Assert.True(FiltroColeccion.Cumple(c, Tema(genero: "Deep House"), Ficha(6, MomentoSet.Apertura, TipoVoz.Vocal)));
        Assert.False(FiltroColeccion.Cumple(c, Tema(genero: "Deep House"), Ficha(8, MomentoSet.Apertura, TipoVoz.Vocal)));
        Assert.False(FiltroColeccion.Cumple(c, Tema(genero: "Deep House"), Ficha(6, MomentoSet.Apertura, TipoVoz.Instrumental)));
        Assert.False(FiltroColeccion.Cumple(c, Tema(genero: "Techno"), Ficha(6, MomentoSet.Apertura, TipoVoz.Vocal)));
    }

    // Una colección de «energía alta» no puede llenarse de canciones que nadie ha valorado.
    [Fact]
    public void Sin_ficha_no_entra_en_una_condicion_que_pide_la_ficha()
    {
        var c = new ColeccionInteligente { EnergiaMin = 8 };

        Assert.False(FiltroColeccion.Cumple(c, Tema(), null));
        Assert.False(FiltroColeccion.Cumple(c, Tema(), new FichaDj()));
    }

    // Dentro de los momentos basta con uno: un tema de subida sirve en «subida o pico».
    [Fact]
    public void Basta_con_uno_de_los_momentos_marcados()
    {
        var c = new ColeccionInteligente { Momentos = MomentoSet.Subida | MomentoSet.Pico };

        Assert.True(FiltroColeccion.Cumple(c, Tema(), Ficha(momentos: MomentoSet.Subida)));
        Assert.False(FiltroColeccion.Cumple(c, Tema(), Ficha(momentos: MomentoSet.Cierre)));
    }

    // Las etiquetas, en cambio, se añaden para afinar: hacen falta todas.
    [Fact]
    public void Hacen_falta_todas_las_etiquetas_pedidas()
    {
        var c = new ColeccionInteligente { Etiquetas = Fichas.PartirLista("boda, verano") };

        Assert.True(FiltroColeccion.Cumple(c, Tema(), Ficha(etiquetas: "Verano, boda, exterior")));
        Assert.False(FiltroColeccion.Cumple(c, Tema(), Ficha(etiquetas: "boda")));
    }

    // Sin BPM conocido no se puede afirmar que esté dentro del margen.
    [Fact]
    public void Sin_bpm_no_cumple_un_margen_de_tempo()
    {
        var c = new ColeccionInteligente { BpmMin = 120, BpmMax = 128 };

        Assert.True(FiltroColeccion.Cumple(c, Tema(bpm: 124), null));
        Assert.False(FiltroColeccion.Cumple(c, Tema(bpm: 130), null));
        Assert.False(FiltroColeccion.Cumple(c, Tema(bpm: 0), null));
    }

    // Una colección de «solo limpias» funciona desde el primer día: si la ficha no dice nada, vale
    // lo que ya se deduce del nombre.
    [Fact]
    public void La_letra_usa_el_nombre_del_archivo_si_la_ficha_no_dice_nada()
    {
        var c = new ColeccionInteligente { Letra = TipoLetra.Limpia };

        Assert.True(FiltroColeccion.Cumple(c, Tema(titulo: "Tema (Clean)"), null));
        Assert.False(FiltroColeccion.Cumple(c, Tema(titulo: "Tema (Dirty)"), null));
        Assert.False(FiltroColeccion.Cumple(c, Tema(titulo: "Tema"), null));

        // Y si la ficha lo dice, manda la ficha.
        Assert.False(FiltroColeccion.Cumple(c, Tema(titulo: "Tema (Clean)"), new FichaDj { Letra = TipoLetra.Explicita }));
    }

    // Una colección sin condiciones sería la biblioteca entera; exportarla por error sería fácil.
    [Fact]
    public void Sin_condiciones_no_entra_nada()
    {
        var c = new ColeccionInteligente();

        Assert.False(FiltroColeccion.TieneCriterios(c));
        Assert.False(FiltroColeccion.Cumple(c, Tema(), Ficha(energia: 5)));
    }

    [Fact]
    public void El_texto_busca_en_artista_titulo_y_archivo_en_cualquier_orden()
    {
        var c = new ColeccionInteligente { Texto = "monaco bunny" };

        Assert.True(FiltroColeccion.Cumple(c, Tema(artista: "Bad Bunny", titulo: "MONACO"), null));
        Assert.False(FiltroColeccion.Cumple(c, Tema(artista: "Bad Bunny", titulo: "Tití Me Preguntó"), null));
    }

    [Fact]
    public void Las_colecciones_se_guardan()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var archivo = Path.Combine(dir, "colecciones.json");
            var almacen = new AlmacenColecciones(archivo);
            almacen.Todas.Add(new ColeccionInteligente
            {
                Nombre = "Apertura house", Genero = "house", EnergiaMax = 5, Momentos = MomentoSet.Apertura | MomentoSet.Subida,
            });
            Assert.Equal("", almacen.Guardar());

            var leida = Assert.Single(new AlmacenColecciones(archivo).Todas);
            Assert.Equal("Apertura house", leida.Nombre);
            Assert.Equal(MomentoSet.Apertura | MomentoSet.Subida, leida.Momentos);
            Assert.Equal(5, leida.EnergiaMax);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
