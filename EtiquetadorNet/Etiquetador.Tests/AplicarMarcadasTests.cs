using System.Collections.ObjectModel;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// E3 · «Aplicar seleccionados» aplicaba todas las MARCADAS, también las ocultas por la búsqueda,
/// y sin preguntar. Visto en un registro real (beattag_20260922_195200.log): «15184 revisadas · 728
/// propuestas», unas 640 marcadas; se buscaba «bunny», se veían 3, se seleccionaban (la insignia
/// decía «3 seleccionadas») y se renombraban y etiquetaban ~640 archivos.
/// </summary>
public class AplicarMarcadasTests
{
    private static PreviewRow Fila(string viejo, string nuevo, bool marcada = true, string score = "12")
    {
        var r = new ProcessResult { FilePath = @"E:\Musica\COMERCIAL\" + viejo, Old = viejo, New = nuevo, Source = "Deezer", Score = score };
        var f = new PreviewRow { Result = r, Old = viejo };
        f.UpdateFrom(r);
        f.Apply = marcada;
        return f;
    }

    /// <summary>
    /// La tirada del registro: 728 propuestas, 640 marcadas, 3 de Bad Bunny entre las marcadas.
    /// Renombran todas menos una de cada cuatro (las que solo cambian etiquetas).
    /// </summary>
    private static List<PreviewRow> Tirada()
    {
        var filas = new List<PreviewRow>
        {
            Fila("bad bunny - titi me pregunto.mp3", "Bad Bunny - Tití Me Preguntó.mp3"),
            Fila("Bad_Bunny-Monaco_(Intro).mp3", "Bad Bunny - MONACO (Intro).mp3"),
            Fila("Bad Bunny - Neverita.mp3", "Bad Bunny - Neverita.mp3"),   // solo etiquetas
        };
        for (var i = 3; i < 728; i++)
        {
            var viejo = $"Artista {i} - Tema {i}.mp3";
            filas.Add(Fila(viejo, i % 4 == 0 ? viejo : $"Artista {i} - Tema {i} (Extended).mp3", marcada: i < 640));
        }
        return filas;
    }

    private static Func<PreviewRow, bool> Busqueda(string texto)
    {
        var c = ConsultaTabla.De(texto);
        return r => c.Vacia || c.Coincide(r);
    }

    [Fact]
    public void Con_la_busqueda_activa_dice_cuantas_quedan_ocultas()
    {
        var plan = EnrichViewModel.Planificar(Tirada(), Busqueda("bunny"), "bunny");

        Assert.Equal(640, plan.Marcadas);
        Assert.Equal(3, plan.Visibles);
        Assert.Equal(637, plan.Ocultas);
        Assert.Equal(2, plan.VisiblesRenombran);   // Neverita solo cambia etiquetas
    }

    // Con la búsqueda activa se ofrecen primero las que se ven: es lo que espera quien acaba de
    // buscar, y lo que menos toca. Todas las marcadas quedan como segunda opción, con su número.
    [Fact]
    public void Con_busqueda_lo_primero_que_ofrece_son_las_visibles()
    {
        var c = EnrichViewModel.Confirmacion(EnrichViewModel.Planificar(Tirada(), Busqueda("bunny"), "bunny"));

        Assert.True(c.AceptarSoloVisibles);
        Assert.Equal("Solo las visibles (3)", c.Aceptar);
        Assert.Equal("Todas las marcadas (640)", c.Alternativa);
        Assert.Contains("«bunny»", c.Titulo);
        Assert.Contains("las otras 637 están ocultas", c.Cuerpo);
        Assert.Contains("se renombrarán 2 archivos", c.Cuerpo);
        Assert.Contains("rekordbox", c.Nota);
    }

    [Fact]
    public void Solo_las_visibles_aplica_esas_tres_y_todas_las_640()
    {
        var filas = Tirada();
        var visibles = EnrichViewModel.FilasAAplicar(filas, Busqueda("bunny"), soloVisibles: true);
        var todas = EnrichViewModel.FilasAAplicar(filas, Busqueda("bunny"), soloVisibles: false);

        Assert.Equal(3, visibles.Count);
        Assert.All(visibles, f => Assert.Contains("Bad Bunny", f.New));
        Assert.Equal(640, todas.Count);
    }

    // Una fila que se ve pero está desmarcada no se aplica: aplicar es de las marcadas.
    [Fact]
    public void Una_visible_desmarcada_no_se_aplica()
    {
        var filas = Tirada();
        filas[0].Apply = false;
        var visibles = EnrichViewModel.FilasAAplicar(filas, Busqueda("bunny"), soloVisibles: true);
        Assert.Equal(2, visibles.Count);
        Assert.DoesNotContain(filas[0], visibles);
    }

    [Fact]
    public void Sin_busqueda_una_sola_opcion_con_el_numero()
    {
        var c = EnrichViewModel.Confirmacion(EnrichViewModel.Planificar(Tirada(), Busqueda(""), ""));

        Assert.False(c.AceptarSoloVisibles);
        Assert.Equal("Aplicar (640)", c.Aceptar);
        Assert.Equal("", c.Alternativa);
        Assert.Equal("Aplicar 640 canciones marcadas", c.Titulo);
        Assert.StartsWith("Se escribirán las etiquetas de 640 canciones y se renombrarán", c.Cuerpo);
    }

    // Si la búsqueda no deja ver ninguna marcada, aplicar sería aplicar a ciegas: se dice así.
    [Fact]
    public void Si_la_busqueda_oculta_todas_las_marcadas_lo_dice()
    {
        var c = EnrichViewModel.Confirmacion(EnrichViewModel.Planificar(Tirada(), Busqueda("rosalia"), "rosalia"));

        Assert.Contains("ocultas por la búsqueda", c.Titulo);
        Assert.Contains("aunque no las estés viendo", c.Cuerpo);
        Assert.Equal("", c.Alternativa);
    }

    // Sin renombrados no hay nada que advertir de rekordbox.
    [Fact]
    public void Si_ninguna_renombra_no_habla_de_rekordbox()
    {
        var filas = new List<PreviewRow> { Fila("Bad Bunny - Neverita.mp3", "Bad Bunny - Neverita.mp3") };
        var c = EnrichViewModel.Confirmacion(EnrichViewModel.Planificar(filas, Busqueda(""), ""));

        Assert.Equal("Aplicar 1 canción marcada", c.Titulo);
        Assert.Contains("ningún archivo cambia de nombre", c.Cuerpo);
        Assert.DoesNotContain("rekordbox", c.Nota);
    }
}

/// <summary>
/// E10 · El aviso de rekordbox («Se van a renombrar N archivos») solo se recontaba tras analizar y
/// tras aplicar. Al abrir la aplicación con 300 propuestas en caché que renombraban no salía, y
/// después de «Desmarcar todo» seguía diciendo 300.
/// </summary>
public class RecuentoMarcadasTests
{
    private static PreviewRow Fila(int i, bool renombra = true, bool marcada = true)
    {
        var viejo = $"pista{i:000}.mp3";
        var r = new ProcessResult { FilePath = @"E:\Musica\" + viejo, Old = viejo, New = renombra ? $"Artista - Tema {i}.mp3" : viejo };
        var f = new PreviewRow { Result = r, Old = viejo };
        f.UpdateFrom(r);
        f.Apply = marcada;
        return f;
    }

    // Como LoadCached al arrancar: las filas entran una a una en la lista.
    [Fact]
    public void Al_arrancar_con_propuestas_en_cache_ya_hay_aviso()
    {
        var filas = new ObservableCollection<PreviewRow>();
        var recuento = new RecuentoMarcadas(filas);

        for (var i = 0; i < 300; i++) filas.Add(Fila(i));

        Assert.Equal(300, recuento.Renombran);
        Assert.Equal(300, recuento.Marcadas);
        Assert.True(recuento.HayRenombrados);
    }

    [Fact]
    public void Desmarcar_todo_quita_el_aviso_y_recuenta_una_sola_vez()
    {
        var filas = new ObservableCollection<PreviewRow>(Enumerable.Range(0, 300).Select(i => Fila(i)));
        var recuento = new RecuentoMarcadas(filas);
        var avisos = 0;
        recuento.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RecuentoMarcadas.Renombran)) avisos++; };

        using (recuento.EnLote())
            foreach (var f in filas) f.Apply = false;   // «Desmarcar todo»

        Assert.Equal(0, recuento.Renombran);
        Assert.Equal(0, recuento.Marcadas);
        Assert.False(recuento.HayRenombrados);
        Assert.Equal(1, avisos);
    }

    // El doble clic sobre una fila cambia la casilla sin pasar por ningún comando.
    [Fact]
    public void El_doble_clic_sobre_una_fila_recuenta()
    {
        var filas = new ObservableCollection<PreviewRow>(Enumerable.Range(0, 3).Select(i => Fila(i)));
        var recuento = new RecuentoMarcadas(filas);

        filas[1].Toggle();

        Assert.Equal(2, recuento.Renombran);
        Assert.Equal(2, recuento.Marcadas);
    }

    // Editar «Archivo propuesto» y dejarlo como estaba hace que esa fila ya no renombre.
    [Fact]
    public void Editar_el_nombre_propuesto_recuenta()
    {
        var filas = new ObservableCollection<PreviewRow>(Enumerable.Range(0, 3).Select(i => Fila(i)));
        var recuento = new RecuentoMarcadas(filas);

        filas[0].New = filas[0].Old;

        Assert.Equal(2, recuento.Renombran);
        Assert.Equal(3, recuento.Marcadas);
    }

    [Fact]
    public void Quitar_filas_y_vaciar_la_lista_recuenta()
    {
        var filas = new ObservableCollection<PreviewRow>(Enumerable.Range(0, 4).Select(i => Fila(i, renombra: i != 3)));
        var recuento = new RecuentoMarcadas(filas);
        Assert.Equal(3, recuento.Renombran);

        var quitada = filas[0];
        filas.Remove(quitada);
        Assert.Equal(2, recuento.Renombran);

        quitada.Apply = false;   // una fila que ya no está en la lista no cuenta
        Assert.Equal(2, recuento.Renombran);

        filas.Clear();   // como al empezar a analizar
        Assert.Equal(0, recuento.Renombran);
        Assert.Equal(0, recuento.Marcadas);
    }
}

/// <summary>
/// E13 · La casilla «Aplicar» se guardaba solo por ruta y aprobaba de antemano propuestas
/// distintas: tras «Marcar todo», una propuesta nueva para esa canción semanas después (otra
/// coincidencia, confianza 0,8, fondo rojo de «baja confianza») salía ya marcada.
/// </summary>
public class CasillaPorPropuestaTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Ruta = @"E:\Musica\COMERCIAL\Bad Bunny - Monaco.mp3";

    private string Archivo => Path.Combine(_dir, "marcas-aplicar.json");

    private static ProcessResult Propuesta(string nuevo, string source, string score)
        => new() { FilePath = Ruta, Old = "Bad Bunny - Monaco.mp3", New = nuevo, Artist = "Bad Bunny", Title = "MONACO", Source = source, Score = score };

    [Fact]
    public void Marcar_todo_no_aprueba_una_propuesta_distinta()
    {
        var marcas = new ApplyMarks(Archivo);
        var antes = Propuesta("Bad Bunny - MONACO.mp3", "Deezer", "12");
        EnrichViewModel.GuardarDecision(marcas, Ruta, EnrichViewModel.HuellaPropuesta(antes), true);   // «Marcar todo»
        Assert.Equal("", marcas.Save());

        // Semanas después, «Reanalizar todo» da otra coincidencia, de confianza baja.
        var despues = Propuesta("Bad Bunny - MONACO (Live).mp3", "iTunes", "0.8");
        var reabierta = new ApplyMarks(Archivo);

        Assert.Null(EnrichViewModel.DecisionGuardada(reabierta, Ruta, EnrichViewModel.HuellaPropuesta(despues)));
        Assert.Equal((false, "⚠ baja confianza — revisar"), EnrichViewModel.EstadoInicial(despues));   // decide el análisis
    }

    // La decisión sobre la MISMA propuesta se respeta, también tras cerrar la aplicación.
    [Fact]
    public void La_misma_propuesta_conserva_la_casilla()
    {
        var p = Propuesta("Bad Bunny - MONACO (Live).mp3", "iTunes", "0.8");
        var marcas = new ApplyMarks(Archivo);
        EnrichViewModel.GuardarDecision(marcas, Ruta, EnrichViewModel.HuellaPropuesta(p), true);
        marcas.Save();

        Assert.True(EnrichViewModel.DecisionGuardada(new ApplyMarks(Archivo), Ruta, EnrichViewModel.HuellaPropuesta(p)));
    }

    // Las casillas de antes (solo por ruta, marcas-aplicar.json pesaba 84 KB en el equipo del
    // usuario): un «no» se respeta y pasa a la clave nueva; un «sí» no aprueba lo que haya ahora.
    [Fact]
    public void Una_casilla_antigua_por_ruta()
    {
        var p = Propuesta("Bad Bunny - MONACO.mp3", "Deezer", "12");
        var huella = EnrichViewModel.HuellaPropuesta(p);

        var marcas = new ApplyMarks(Archivo);
        marcas.Set(Ruta, false);
        Assert.False(EnrichViewModel.DecisionGuardada(marcas, Ruta, huella));
        Assert.Null(marcas.Get(Ruta));
        Assert.False(marcas.Get(EnrichViewModel.ClaveMarca(Ruta, huella)));

        var otras = new ApplyMarks(Path.Combine(_dir, "otras.json"));
        otras.Set(Ruta, true);
        Assert.Null(EnrichViewModel.DecisionGuardada(otras, Ruta, huella));
        Assert.Null(otras.Get(Ruta));
    }

    [Fact]
    public void La_huella_es_de_la_coincidencia()
    {
        var a = Propuesta("Bad Bunny - MONACO.mp3", "Deezer", "12");
        Assert.Equal(EnrichViewModel.HuellaPropuesta(a), EnrichViewModel.HuellaPropuesta(Propuesta("Bad Bunny - MONACO.mp3", "Deezer", "12")));
        Assert.NotEqual(EnrichViewModel.HuellaPropuesta(a), EnrichViewModel.HuellaPropuesta(Propuesta("Bad Bunny - MONACO.mp3", "Deezer", "4")));
        Assert.NotEqual(EnrichViewModel.HuellaPropuesta(a), EnrichViewModel.HuellaPropuesta(Propuesta("Bad Bunny - MONACO.mp3", "iTunes", "12")));
        Assert.NotEqual(EnrichViewModel.HuellaPropuesta(a), EnrichViewModel.HuellaPropuesta(Propuesta("Bad Bunny - MONACO (Live).mp3", "Deezer", "12")));
    }

    // Editar una celda no cambia de qué propuesta es la casilla: lo que se reabre es la del análisis.
    [Fact]
    public void Editar_una_celda_no_cambia_la_huella()
    {
        var p = Propuesta("Bad Bunny - MONACO.mp3", "Deezer", "12");
        var fila = new PreviewRow { Result = p, Old = p.Old };
        fila.UpdateFrom(p);
        var huella = fila.Huella;

        fila.New = "Bad Bunny - Monaco (Editado).mp3";
        fila.Title = "Monaco";

        Assert.Equal(huella, fila.Huella);
        Assert.Equal(EnrichViewModel.HuellaPropuesta(p), huella);
    }

    [Fact]
    public void Olvidar_quita_la_nueva_y_la_antigua()
    {
        var marcas = new ApplyMarks(Archivo);
        marcas.Set(Ruta, true);
        EnrichViewModel.GuardarDecision(marcas, Ruta, "abc", false);

        EnrichViewModel.OlvidarDecision(marcas, Ruta, "abc");

        Assert.Equal(0, marcas.Count);
    }

    // Lo que hay que revisar sale desmarcado con su motivo, aunque la confianza sea alta.
    [Fact]
    public void Lo_que_hay_que_revisar_sale_desmarcado()
    {
        var p = Propuesta("Bad Bunny - MONACO.mp3", "MusicBrainz", "9");
        p.Revisar = "solo lo identificó MusicBrainz";
        Assert.Equal((false, "⚠ revisar: solo lo identificó MusicBrainz"), EnrichViewModel.EstadoInicial(p));
        Assert.Equal((true, ""), EnrichViewModel.EstadoInicial(Propuesta("Bad Bunny - MONACO.mp3", "Deezer", "12")));
    }
}
