using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// La ficha de DJ es trabajo hecho a mano, canción a canción. Lo que se prueba aquí es que no se
/// pierda: ni al editar muchas a la vez, ni al renombrar archivos, ni al volcarla al comentario.
/// </summary>
public class FichasDjTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static FichaDj Ficha(int energia = 0, MomentoSet momentos = MomentoSet.Ninguno,
                                 string ambiente = "", string etiquetas = "") => new()
    {
        Energia = energia,
        Momentos = momentos,
        Ambiente = Fichas.PartirLista(ambiente),
        Etiquetas = Fichas.PartirLista(etiquetas),
    };

    // --- Editar varias a la vez ---

    // EL CASO QUE JUSTIFICA TODO EL DISEÑO: subir la energía de varias canciones no puede borrarles
    // lo que cada una tenía y el editor no enseñaba.
    [Fact]
    public void Cambiar_la_energia_de_varias_no_toca_lo_demas()
    {
        var a = Ficha(energia: 5, ambiente: "oscura", etiquetas: "boda");
        var b = Ficha(energia: 3, ambiente: "alegre");
        var inicial = FichaComun.De(new FichaDj?[] { a, b });

        var editado = inicial with { Energia = 8 };
        var cambios = CambiosFicha.Calcular(inicial, editado);

        var a2 = cambios.AplicarA(a);
        var b2 = cambios.AplicarA(b);
        Assert.Equal(8, a2.Energia);
        Assert.Equal(8, b2.Energia);
        Assert.Equal(new[] { "oscura" }, a2.Ambiente);
        Assert.Equal(new[] { "boda" }, a2.Etiquetas);
        Assert.Equal(new[] { "alegre" }, b2.Ambiente);
    }

    // Añadir una etiqueta a varias se suma a las que ya tenía cada una.
    [Fact]
    public void Anadir_una_etiqueta_a_varias_conserva_las_suyas()
    {
        var a = Ficha(etiquetas: "boda");
        var b = Ficha(etiquetas: "club");
        var inicial = FichaComun.De(new FichaDj?[] { a, b });
        Assert.Empty(inicial.Etiquetas);   // no comparten ninguna: no se enseña ninguna

        var cambios = CambiosFicha.Calcular(inicial, inicial with { Etiquetas = new[] { "verano" } });

        Assert.Equal(new[] { "boda", "verano" }, cambios.AplicarA(a).Etiquetas);
        Assert.Equal(new[] { "club", "verano" }, cambios.AplicarA(b).Etiquetas);
    }

    // Quitar una etiqueta común la quita de todas, y solo esa.
    [Fact]
    public void Quitar_una_etiqueta_comun_solo_quita_esa()
    {
        var a = Ficha(etiquetas: "boda, verano");
        var b = Ficha(etiquetas: "club, verano");
        var inicial = FichaComun.De(new FichaDj?[] { a, b });
        Assert.Equal(new[] { "verano" }, inicial.Etiquetas);

        var cambios = CambiosFicha.Calcular(inicial, inicial with { Etiquetas = Array.Empty<string>() });

        Assert.Equal(new[] { "boda" }, cambios.AplicarA(a).Etiquetas);
        Assert.Equal(new[] { "club" }, cambios.AplicarA(b).Etiquetas);
    }

    // Un momento que tienen solo algunas sale desmarcado; dejarlo así no se lo quita a las que lo tenían.
    [Fact]
    public void Un_momento_que_solo_tienen_algunas_no_se_quita_por_no_tocarlo()
    {
        var a = Ficha(momentos: MomentoSet.Pico | MomentoSet.Cierre);
        var b = Ficha(momentos: MomentoSet.Pico);
        var inicial = FichaComun.De(new FichaDj?[] { a, b });
        Assert.Equal(MomentoSet.Pico, inicial.Momentos);

        var cambios = CambiosFicha.Calcular(inicial, inicial with { Momentos = MomentoSet.Pico | MomentoSet.Subida });

        Assert.Equal(MomentoSet.Pico | MomentoSet.Cierre | MomentoSet.Subida, cambios.AplicarA(a).Momentos);
        Assert.Equal(MomentoSet.Pico | MomentoSet.Subida, cambios.AplicarA(b).Momentos);
    }

    // Abrir el editor y guardar sin tocar nada no cambia nada.
    [Fact]
    public void Guardar_sin_tocar_nada_no_es_un_cambio()
    {
        var inicial = FichaComun.De(new FichaDj?[] { Ficha(energia: 5), Ficha(energia: 7, etiquetas: "boda") });

        Assert.False(CambiosFicha.Calcular(inicial, inicial).HayAlguno);
    }

    // Con energías distintas el editor enseña «varias»; dejarlo así no las iguala.
    [Fact]
    public void Energias_distintas_se_quedan_como_estaban_si_no_se_elige_una()
    {
        var inicial = FichaComun.De(new FichaDj?[] { Ficha(energia: 5), Ficha(energia: 7) });
        Assert.Null(inicial.Energia);

        var cambios = CambiosFicha.Calcular(inicial, inicial);

        Assert.Null(cambios.Energia);
    }

    // --- Listas escritas a mano ---

    [Fact]
    public void Las_listas_ignoran_vacios_y_repetidos_sin_distinguir_acentos()
    {
        Assert.Equal(new[] { "Oscura", "hipnótica" }, Fichas.PartirLista(" Oscura, ,hipnótica; oscura ; HIPNOTICA "));
    }

    // --- Comentario del archivo ---

    [Fact]
    public void El_comentario_resume_la_ficha_en_una_linea()
    {
        var f = Ficha(energia: 7, momentos: MomentoSet.Pico | MomentoSet.Apertura, ambiente: "oscura", etiquetas: "fin de año");
        f.Voz = TipoVoz.Vocal;
        f.ArmaSecreta = true;

        Assert.Equal("[DJ: E7 · Apertura, Pico · Vocal · oscura · #fin-de-año · Arma secreta]", Fichas.Comentario(f));
    }

    // Muchos DJs ya usan el comentario (Mixed In Key escribe ahí la tonalidad). No se puede pisar.
    [Fact]
    public void Volcar_la_ficha_conserva_lo_que_ya_habia_en_el_comentario()
    {
        var res = Fichas.ComentarioCombinado("8A - Energy 6", Ficha(energia: 7));

        Assert.Equal("8A - Energy 6 [DJ: E7]", res);
    }

    // Volcarla otra vez reemplaza el bloque anterior en vez de acumular uno por volcado.
    [Fact]
    public void Volcar_dos_veces_no_duplica_el_bloque()
    {
        var una = Fichas.ComentarioCombinado("mis notas", Ficha(energia: 5));
        var dos = Fichas.ComentarioCombinado(una, Ficha(energia: 9));

        Assert.Equal("mis notas [DJ: E9]", dos);
    }

    [Fact]
    public void Con_la_ficha_vacia_solo_se_retira_el_bloque()
    {
        Assert.Equal("mis notas", Fichas.ComentarioCombinado("mis notas [DJ: E5]", new FichaDj()));
    }

    // Un «]» en una etiqueta cerraría el bloque antes de tiempo y el siguiente volcado dejaría restos.
    [Fact]
    public void Un_corchete_en_una_etiqueta_no_rompe_el_bloque()
    {
        var una = Fichas.ComentarioCombinado("", Ficha(etiquetas: "raro]"));
        var dos = Fichas.ComentarioCombinado(una, Ficha(energia: 4));

        Assert.Equal("[DJ: E4]", dos);
    }

    // Deshacer tiene que saber devolver el comentario: es lo que hace reversible el volcado.
    [Fact]
    public void Deshacer_devuelve_el_comentario_anterior()
    {
        var mp3 = Path.Combine(_dir, "tema.mp3");
        Mp3Fixture.WriteMinMp3(mp3);
        using (var f = TagLib.File.Create(mp3)) { f.Tag.Comment = "8A - Energy 6"; f.Save(); }

        var nuevo = Fichas.ComentarioCombinado("8A - Energy 6", Ficha(energia: 7));
        using (var f = TagLib.File.Create(mp3)) { f.Tag.Comment = nuevo; f.Save(); }

        var undoDir = Path.Combine(_dir, "deshacer");
        Directory.CreateDirectory(undoDir);
        var rec = new Etiquetador.Core.Pipeline.UndoRecord
        {
            OrigPath = mp3, FinalPath = mp3,
            Fields = new() { ["Comment"] = Etiquetador.Core.Pipeline.FieldChange.Str("8A - Energy 6", nuevo) },
        };
        var manifiesto = Path.Combine(undoDir, "run_20260101_000000.jsonl");
        File.WriteAllText(manifiesto, System.Text.Json.JsonSerializer.Serialize(rec) + "\n");

        var res = new Etiquetador.Core.Pipeline.UndoEngine(new AppPaths(_dir)).UndoLastRun(manifiesto);

        Assert.Equal(1, res.Reverted);
        using var g = TagLib.File.Create(mp3);
        Assert.Equal("8A - Energy 6", g.Tag.Comment);
    }

    // --- Almacén ---

    [Fact]
    public void Las_fichas_sobreviven_a_cerrar_la_aplicacion()
    {
        var archivo = Path.Combine(_dir, "fichas.json");
        var almacen = new AlmacenFichas(archivo);
        var f = Ficha(energia: 8, momentos: MomentoSet.Pico, ambiente: "oscura");
        f.Letra = TipoLetra.Explicita;
        almacen.Poner(@"D:\Musica\a.mp3", f);
        Assert.Equal("", almacen.Guardar());

        var otra = new AlmacenFichas(archivo);
        var leida = otra.Obtener(@"d:\musica\A.MP3");   // la ruta no distingue mayúsculas

        Assert.NotNull(leida);
        Assert.Equal(8, leida!.Energia);
        Assert.Equal(MomentoSet.Pico, leida.Momentos);
        Assert.Equal(TipoLetra.Explicita, leida.Letra);
        Assert.Equal(new[] { "oscura" }, leida.Ambiente);
    }

    // Vaciar una ficha es borrarla: no se acumulan entradas vacías en el archivo.
    [Fact]
    public void Una_ficha_vacia_se_borra()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        almacen.Poner("a.mp3", Ficha(energia: 5));

        almacen.Poner("a.mp3", new FichaDj());

        Assert.Equal(0, almacen.Count);
    }

    // Lo que devuelve es una copia: tocarla sin guardarla no puede cambiar lo guardado.
    [Fact]
    public void Modificar_lo_leido_no_cambia_lo_guardado()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        almacen.Poner("a.mp3", Ficha(energia: 5));

        almacen.Obtener("a.mp3")!.Energia = 1;

        Assert.Equal(5, almacen.Obtener("a.mp3")!.Energia);
    }

    // Un archivo dañado no puede tratarse como «sin fichas»: el siguiente guardado lo pisaría.
    [Fact]
    public void Un_archivo_ilegible_se_aparta_en_vez_de_perderse()
    {
        var archivo = Path.Combine(_dir, "fichas.json");
        File.WriteAllText(archivo, "{ esto no es json");

        var almacen = new AlmacenFichas(archivo);

        Assert.NotEqual("", almacen.AvisoCarga);
        Assert.Single(Directory.GetFiles(_dir, "fichas.json.ilegible_*"));
    }

    // --- Renombrados ---

    // BeatTag renombra archivos. La ficha tiene que seguir al archivo, o el trabajo desaparece.
    [Fact]
    public void La_ficha_sigue_al_archivo_renombrado()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        almacen.Poner("viejo.mp3", Ficha(energia: 6));
        var existen = new HashSet<string> { "nuevo.mp3" };

        var n = almacen.Reubicar(new Dictionary<string, string> { ["viejo.mp3"] = "nuevo.mp3" }, existen.Contains);

        Assert.Equal(1, n);
        Assert.Equal(6, almacen.Obtener("nuevo.mp3")!.Energia);
        Assert.Null(almacen.Obtener("viejo.mp3"));
    }

    // Y si el renombrado se deshizo, vuelve con el archivo a su nombre original.
    [Fact]
    public void La_ficha_vuelve_si_el_renombrado_se_deshizo()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        almacen.Poner("nuevo.mp3", Ficha(energia: 6));
        var existen = new HashSet<string> { "viejo.mp3" };

        almacen.Reubicar(new Dictionary<string, string> { ["viejo.mp3"] = "nuevo.mp3" }, existen.Contains);

        Assert.Equal(6, almacen.Obtener("viejo.mp3")!.Energia);
    }

    // Un archivo que no aparece puede estar en un disco desconectado: su ficha no se toca.
    [Fact]
    public void La_ficha_de_un_archivo_que_no_aparece_no_se_borra()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        almacen.Poner(@"E:\Musica\a.mp3", Ficha(energia: 6));

        almacen.Reubicar(new Dictionary<string, string>(), _ => false);

        Assert.Equal(1, almacen.Count);
    }

    // Si el archivo sigue donde estaba, un renombrado antiguo de esa ruta no se la lleva.
    [Fact]
    public void Una_ficha_cuyo_archivo_existe_no_se_mueve()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        almacen.Poner("a.mp3", Ficha(energia: 6));
        var existen = new HashSet<string> { "a.mp3", "b.mp3" };

        almacen.Reubicar(new Dictionary<string, string> { ["a.mp3"] = "b.mp3" }, existen.Contains);

        Assert.NotNull(almacen.Obtener("a.mp3"));
        Assert.Null(almacen.Obtener("b.mp3"));
    }
}
