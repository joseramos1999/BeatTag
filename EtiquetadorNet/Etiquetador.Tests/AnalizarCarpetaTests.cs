using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// F1 · Analizar una carpeta sin desmarcar las demás.
///
/// Visto en los registros de un usuario real: en cada sesión desmarcaba 42 de sus 43 carpetas para
/// analizar solo «Descargas» (33, 59, 98 y 114 canciones) y luego las volvía a marcar. Ahora se elige
/// la carpeta (o una subcarpeta) y el análisis, la tabla y aplicar se limitan a ella, sin tocar las
/// casillas y sin podar la caché de análisis del resto (E1).
/// </summary>
public class AnalizarCarpetaTests
{
    private static readonly string Musica = Rutas.De("Musica");
    private static readonly string Descargas = Rutas.De("Descargas");
    private static readonly string Archivo = Rutas.De("Archivo");

    private static List<FolderItem> Carpetas() => new()
    {
        new FolderItem(Musica),
        new FolderItem(Descargas),
        new FolderItem(Archivo, enabled: false),
    };

    private static Track Pista(string carpeta, params string[] tramos)
        => new() { FilePath = Path.Combine(carpeta, Path.Combine(tramos)), Folder = carpeta };

    [Fact]
    public void Una_carpeta_marcada_de_la_lista_se_analiza_sin_preguntar()
    {
        var p = EnrichViewModel.PrepararAmbito(Carpetas(), Descargas);

        Assert.Equal("", p.Motivo);
        Assert.False(p.RaizDesmarcada);
        Assert.Equal(Descargas, p.Ambito!.Carpeta);
        Assert.Equal(Descargas, p.Ambito.Raiz);
        Assert.True(p.Ambito.EsRaiz);
    }

    [Fact]
    public void Una_subcarpeta_queda_con_su_carpeta_de_la_lista()
    {
        var house = Path.Combine(Musica, "House") + Path.DirectorySeparatorChar;   // con barra final, como la da a veces el selector

        var p = EnrichViewModel.PrepararAmbito(Carpetas(), house);

        Assert.Equal(Path.Combine(Musica, "House"), p.Ambito!.Carpeta);
        Assert.Equal(Musica, p.Ambito.Raiz);
        Assert.False(p.Ambito.EsRaiz);
        Assert.Equal(Path.Combine("Musica", "House"), EnrichViewModel.NombreAmbito(p.Ambito));
    }

    [Fact]
    public void Una_carpeta_desmarcada_pide_marcarla_y_lo_explica()
    {
        var p = EnrichViewModel.PrepararAmbito(Carpetas(), Path.Combine(Archivo, "2019"));

        Assert.True(p.RaizDesmarcada);
        Assert.Equal(Archivo, p.Ambito!.Raiz);
        var c = EnrichViewModel.ConfirmacionCarpetaDesmarcada(p.Ambito);
        Assert.Equal("«Archivo» está desmarcada", c.Titulo);
        Assert.Contains("hay que marcar «Archivo»", c.Cuerpo);
        Assert.Contains("lo analizado se conserva", c.Nota);
        Assert.Equal("Marcar y analizar", c.Aceptar);
    }

    [Fact]
    public void Lo_de_fuera_de_la_lista_no_se_analiza()
    {
        var p = EnrichViewModel.PrepararAmbito(Carpetas(), Rutas.De("Otra", "cosa"));

        Assert.Null(p.Ambito);
        Assert.Contains("no está dentro de ninguna carpeta de la lista", p.Motivo);
        // «Musica2» no está dentro de «Musica».
        Assert.Null(EnrichViewModel.PrepararAmbito(Carpetas(), Rutas.De("Musica2")).Ambito);
        Assert.Null(EnrichViewModel.PrepararAmbito(Carpetas(), " ").Ambito);
    }

    [Fact]
    public void Con_carpetas_solapadas_manda_la_marcada_y_la_de_fuera()
    {
        var dentro = Path.Combine(Musica, "House");
        var carpetas = new List<FolderItem> { new(dentro), new(Musica) };
        Assert.Equal(Musica, EnrichViewModel.PrepararAmbito(carpetas, Path.Combine(dentro, "2024")).Ambito!.Raiz);

        carpetas[1].Enabled = false;   // la de fuera, desmarcada: se usa la de dentro, que sí cuenta
        var p = EnrichViewModel.PrepararAmbito(carpetas, Path.Combine(dentro, "2024"));
        Assert.Equal(dentro, p.Ambito!.Raiz);
        Assert.False(p.RaizDesmarcada);
    }

    [Fact]
    public void Las_canciones_del_ambito_son_las_de_esa_carpeta()
    {
        var pistas = new List<Track>
        {
            Pista(Musica, "a.mp3"),
            Pista(Musica, "House", "b.mp3"),
            Pista(Musica, "House", "2024", "c.mp3"),
            Pista(Musica, "Housemusic", "d.mp3"),   // parecida, pero otra carpeta
            Pista(Descargas, "e.mp3"),
        };

        Assert.Equal(5, EnrichViewModel.PistasDelAmbito(pistas, null).Count);
        Assert.Equal(4, EnrichViewModel.PistasDelAmbito(pistas, new AmbitoAnalisis(Musica, Musica)).Count);
        Assert.Equal(new[] { "b.mp3", "c.mp3" },
                     EnrichViewModel.PistasDelAmbito(pistas, new AmbitoAnalisis(Path.Combine(Musica, "House"), Musica)).Select(t => t.FileName));
    }

    [Fact]
    public void Solo_vale_mientras_su_carpeta_siga_marcada()
    {
        var carpetas = Carpetas();
        var ambito = new AmbitoAnalisis(Path.Combine(Musica, "House"), Musica);
        Assert.True(EnrichViewModel.AmbitoVigente(carpetas, ambito));

        carpetas[0].Enabled = false;
        Assert.False(EnrichViewModel.AmbitoVigente(carpetas, ambito));
        carpetas.RemoveAt(0);
        Assert.False(EnrichViewModel.AmbitoVigente(carpetas, ambito));
    }

    [Fact]
    public void La_banda_dice_que_carpeta_se_ve_y_cuantas_canciones()
    {
        Assert.Equal("Mostrando solo «Descargas» · 98 canciones", EnrichViewModel.TextoAmbito(new AmbitoAnalisis(Descargas, Descargas), 98));
        Assert.Equal("Mostrando solo «Descargas» · 1 canción", EnrichViewModel.TextoAmbito(new AmbitoAnalisis(Descargas, Descargas), 1));
    }

    [Fact]
    public void Con_ambito_solo_se_poda_dentro_de_esa_carpeta()
    {
        var house = Path.Combine(Musica, "House");
        var pistas = new List<Track> { Pista(Musica, "House", "b.mp3") };

        Assert.Equal(new[] { house }, EnrichViewModel.RaicesAPodar(pistas, new AmbitoAnalisis(house, Musica)));
        Assert.Equal(new[] { Musica }, EnrichViewModel.RaicesAPodar(pistas, null));   // sin ámbito, como en E1
        Assert.Empty(EnrichViewModel.RaicesAPodar(new List<Track>(), new AmbitoAnalisis(house, Musica)));   // nada leído: nada se poda
    }

    // E1 en el camino de F1: analizar una subcarpeta no puede borrar de la caché lo analizado en el
    // resto de su carpeta ni en las demás carpetas.
    [Fact]
    public void Analizar_una_subcarpeta_conserva_la_cache_del_resto()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var musica = Path.Combine(dir, "Musica");
            var house = Path.Combine(musica, "House");
            var otra = Path.Combine(dir, "Descargas");
            Directory.CreateDirectory(house);
            Directory.CreateDirectory(otra);
            string Archivo(string carpeta, string nombre) { var p = Path.Combine(carpeta, nombre); File.WriteAllText(p, nombre); return p; }
            var a = Archivo(musica, "a.mp3");
            var b = Archivo(house, "b.mp3");
            var borrado = Archivo(house, "borrado.mp3");
            var c = Archivo(otra, "c.mp3");

            var cache = new AnalysisCache(Path.Combine(dir, "analysis-cache.json"));
            foreach (var p in new[] { a, b, borrado, c }) cache.Set(p, "firma", new ProcessResult { FilePath = p, Found = true });

            // La pasada de la subcarpeta solo ve b (borrado ya no está en disco).
            var ambito = new AmbitoAnalisis(house, musica);
            var vistas = new List<Track> { new() { FilePath = b, Folder = musica } };
            cache.Prune(new HashSet<string>(vistas.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase),
                        EnrichViewModel.RaicesAPodar(vistas, ambito));

            Assert.Equal(3, cache.Count);
            Assert.NotNull(cache.Get(a, "firma"));   // resto de su carpeta de la lista
            Assert.NotNull(cache.Get(c, "firma"));   // otra carpeta
            Assert.NotNull(cache.Get(b, "firma"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
