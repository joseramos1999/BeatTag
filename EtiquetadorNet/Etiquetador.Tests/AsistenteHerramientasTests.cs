using Etiquetador.App.ViewModels;
using Etiquetador.App.Views;
using Etiquetador.Core;
using Etiquetador.Core.Ai;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Las piezas de las herramientas del Asistente IA que deciden qué se revisa, sobre qué colección,
/// cuándo dejar de preguntar a la IA, qué sale marcado y cómo se deshace una aceptación.
/// </summary>
public class AsistenteHerramientasTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Track Cancion(string nombre) => new() { FilePath = Rutas.Archivo(nombre), Folder = Rutas.Carpeta };

    // --- X5: colecciones por identidad, no por nombre ---

    private static ColeccionInteligente Coleccion(string nombre) => new() { Nombre = nombre, Genero = "bachata" };

    [Fact]
    public void Una_coleccion_renombrada_sigue_siendo_el_ambito_elegido()
    {
        var bachata = Coleccion("Bachata para cerrar");
        var todas = new List<ColeccionInteligente> { Coleccion("Pico"), bachata };
        var elegida = SelectorAmbito.Opciones(todas, conTodaLaBiblioteca: true).Single(o => o.Nombre == "Bachata para cerrar");

        bachata.Nombre = "Bachata final";   // se renombra en Colecciones

        Assert.True(SelectorAmbito.Resolver(elegida, todas, out var c, out _));
        Assert.Same(bachata, c);
        // Y al rehacer el desplegable, la elección se conserva con el nombre nuevo.
        var nuevas = SelectorAmbito.Opciones(todas, conTodaLaBiblioteca: true);
        Assert.Equal("Bachata final", SelectorAmbito.Elegir(nuevas, elegida)!.Nombre);
    }

    // Antes, una colección que ya no se encontraba por nombre hacía recorrer TODA la biblioteca sin
    // avisar. Ahora se dice, y no se recorre nada.
    [Fact]
    public void Una_coleccion_borrada_no_se_convierte_en_toda_la_biblioteca()
    {
        var bachata = Coleccion("Bachata para cerrar");
        var elegida = SelectorAmbito.Opciones(new[] { bachata }, conTodaLaBiblioteca: true)[1];

        Assert.False(SelectorAmbito.Resolver(elegida, new List<ColeccionInteligente>(), out var c, out var error));
        Assert.Null(c);
        Assert.Contains("ya no existe", error);
    }

    [Fact]
    public void Toda_la_biblioteca_es_la_opcion_sin_coleccion()
    {
        Assert.True(SelectorAmbito.Resolver(AmbitoOpcion.TodaLaBiblioteca, new List<ColeccionInteligente>(), out var c, out var error));
        Assert.Null(c);
        Assert.Equal("", error);
    }

    // Dos colecciones con el mismo nombre, o una llamada «Toda la biblioteca»: antes se cogía la
    // primera con ese nombre (o la biblioteca entera). Ahora cada opción es su colección.
    [Fact]
    public void Los_nombres_repetidos_se_distinguen_y_cada_uno_lleva_a_su_coleccion()
    {
        var a = Coleccion("Boda");
        var b = Coleccion("Boda");
        var trampa = Coleccion("Toda la biblioteca");
        var todas = new List<ColeccionInteligente> { a, b, trampa };
        var opciones = SelectorAmbito.Opciones(todas, conTodaLaBiblioteca: true);

        Assert.Equal(new[] { "Toda la biblioteca", "Boda", "Boda (2)", "Toda la biblioteca (colección)" }, opciones.Select(o => o.Nombre));
        Assert.True(SelectorAmbito.Resolver(opciones[2], todas, out var c2, out _));
        Assert.Same(b, c2);
        Assert.True(SelectorAmbito.Resolver(opciones[3], todas, out var c3, out _));
        Assert.Same(trampa, c3);
    }

    [Fact]
    public void Ordenar_para_mezclar_no_ofrece_toda_la_biblioteca()
    {
        var opciones = SelectorAmbito.Opciones(new[] { Coleccion("Pico") }, conTodaLaBiblioteca: false);
        Assert.Single(opciones);
        Assert.Equal("Pico", SelectorAmbito.Elegir(opciones, null)!.Nombre);
    }

    // --- X8: no atascarse en las mismas N primeras ---

    // Caso del hallazgo: «Renombrar con IA», límite 200, y las 200 primeras con nombre sucio son de un
    // pool que la IA no sabe arreglar. Cada pasada volvía a coger esas 200 y nunca llegaba a la 201.
    [Fact]
    public void Cada_pasada_sigue_por_donde_iba_la_anterior()
    {
        var sucias = Enumerable.Range(1, 300).Select(i => Cancion($"{i:000} pool_sucio.mp3")).ToList();
        var revisadas = new RevisadasEnSesion();

        var (primera, saltadas1) = revisadas.Elegir(sucias, 200);
        Assert.Equal(sucias.Take(200), primera);
        Assert.Equal(0, saltadas1);
        foreach (var t in primera) revisadas.Anotar(t.FilePath);   // ninguna se aceptó

        var (segunda, saltadas2) = revisadas.Elegir(sucias, 200);
        Assert.Equal(sucias.Skip(200), segunda);
        Assert.Equal(200, saltadas2);
        foreach (var t in segunda) revisadas.Anotar(t.FilePath);

        var (tercera, saltadas3) = revisadas.Elegir(sucias, 200);
        Assert.Empty(tercera);
        Assert.Equal(300, saltadas3);

        // «Empezar desde el principio».
        revisadas.Vaciar();
        Assert.Equal(sucias.Take(200), revisadas.Elegir(sucias, 200).Elegidas);
    }

    // No hace falta recorrer la biblioteca entera para llenar el cupo: cada candidata cuesta leer su nombre.
    [Fact]
    public void Elegir_deja_de_mirar_al_llenar_el_cupo()
    {
        var miradas = 0;
        IEnumerable<Track> Candidatas()
        {
            for (var i = 0; i < 1000; i++) { miradas++; yield return Cancion($"{i}.mp3"); }
        }
        new RevisadasEnSesion().Elegir(Candidatas(), 50);
        Assert.Equal(50, miradas);
    }

    // --- X13: dejar de preguntar a la IA tras varios fallos seguidos ---

    [Fact]
    public void Tres_fallos_seguidos_cortan_y_uno_bueno_reinicia_la_cuenta()
    {
        var corte = new CorteIa();
        Assert.False(corte.Fallo("a", "No se pudo contactar con Ollama."));
        Assert.False(corte.Fallo("b", "No se pudo contactar con Ollama."));
        corte.Bien();
        Assert.False(corte.Fallo("c", "x"));
        Assert.False(corte.Fallo("d", "x"));
        Assert.True(corte.Fallo("e", "El modelo «llama3.1:8b» no está descargado. Descárgalo en Ajustes."));
        Assert.True(corte.Cortado);
        Assert.Equal("El modelo «llama3.1:8b» no está descargado. Descárgalo en Ajustes.", corte.Motivo);
        Assert.Equal(CorteIa.FallosSeguidos, OllamaClient.FallosSeguidosParaPausar);
    }

    // Las canciones de la racha que cortó no se llegaron a preguntar de verdad: vuelven a estar
    // pendientes. Un fallo suelto (una respuesta ilegible) sí cuenta como revisada.
    [Fact]
    public void La_racha_que_corta_vuelve_a_quedar_pendiente()
    {
        var revisadas = new RevisadasEnSesion();
        var corte = new CorteIa();
        foreach (var r in new[] { "suelta", "a", "b", "c" }) revisadas.Anotar(r);
        corte.Fallo("suelta", "La IA no devolvió una respuesta legible.");
        corte.Bien();
        corte.Fallo("a", "sin conexión");
        corte.Fallo("b", "sin conexión");
        corte.Fallo("c", "sin conexión");

        corte.DevolverRacha(revisadas);

        Assert.True(revisadas.Contiene("suelta"));
        Assert.False(revisadas.Contiene("a"));
        Assert.False(revisadas.Contiene("c"));
    }

    // --- I9 / X13: qué dice la cabecera ---

    [Fact]
    public void Un_modelo_sin_descargar_no_se_da_por_disponible_y_se_dice_por_que()
    {
        var motivo = "El modelo «llama3.1:8b» no está descargado en Ollama. Descárgalo en Ajustes o elige uno de los instalados: llama3.2:latest.";
        var (disponible, modelo, texto) = AsistenteViewModel.DescribirIa(
            new EstadoModelo("", false, motivo, new[] { "llama3.2:latest" }), "llama3.1:8b");

        Assert.False(disponible);
        Assert.Equal("", modelo);
        Assert.StartsWith(motivo, texto);
        Assert.DoesNotContain("IA local: llama3.1:8b", texto);
    }

    [Fact]
    public void Con_el_modelo_automatico_se_dice_cual_se_usa()
    {
        var (disponible, modelo, texto) = AsistenteViewModel.DescribirIa(
            new EstadoModelo("llama3.2:latest", true, "", new[] { "llama3.2:latest" }), "");
        Assert.True(disponible);
        Assert.Equal("llama3.2:latest", modelo);
        Assert.Equal("IA local: llama3.2:latest (elegido automáticamente)", texto);
    }

    // --- I5: lo de la IA sale desmarcado y se puede deshacer ---

    [Fact]
    public void Las_propuestas_de_la_IA_salen_desmarcadas_y_las_del_nombre_marcadas()
    {
        var t = Cancion("Daft Punk - One More Time.mp3");
        var deIa = new PropuestaFichaFila(new PropuestaFicha(t.FilePath, "FR", TipoVoz.Vocal, "IA"), t);
        var delNombre = new PropuestaFichaFila(new PropuestaFicha(t.FilePath, "", TipoVoz.Instrumental, "Nombre del archivo"), t);

        Assert.False(deIa.Aceptar);
        Assert.True(delNombre.Aceptar);
    }

    // El patrón medido: Anitta → PT aunque «Envolver» se canta en español. Aceptado por error, se deshace.
    [Fact]
    public void Deshacer_una_aceptacion_devuelve_el_idioma_y_la_voz_de_antes()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        var envolver = Rutas.Archivo("Anitta - Envolver.mp3");
        var sandstorm = Rutas.Archivo("Darude - Sandstorm.mp3");
        almacen.Poner(envolver, new FichaDj { Energia = 8, Voz = TipoVoz.Vocal });   // tenía energía y voz, no idioma
        var cambios = new List<(string, CambiosFicha)>
        {
            (envolver, new PropuestaFicha(envolver, "PT", TipoVoz.SinIndicar, "IA").Cambios()),
            (sandstorm, new PropuestaFicha(sandstorm, "", TipoVoz.Instrumental, "IA").Cambios()),
        };

        var anotado = AceptacionFichas.Anotar(almacen, cambios);
        foreach (var (r, c) in cambios) almacen.Poner(r, c.AplicarA(almacen.Obtener(r)));
        Assert.Equal("PT", almacen.Obtener(envolver)!.Idioma);

        Assert.Equal(2, anotado.Deshacer(almacen));

        var f = almacen.Obtener(envolver)!;
        Assert.Equal("", f.Idioma);
        Assert.Equal(8, f.Energia);          // lo que no tocó la aceptación, intacto
        Assert.Equal(TipoVoz.Vocal, f.Voz);
        Assert.Null(almacen.Obtener(sandstorm));   // no tenía ficha: vuelve a no tenerla
    }

    // Si después de aceptar el usuario corrigió a mano, deshacer no pisa su corrección.
    [Fact]
    public void Deshacer_respeta_lo_que_se_cambio_a_mano_despues()
    {
        var almacen = new AlmacenFichas(Path.Combine(_dir, "fichas.json"));
        var ruta = Rutas.Archivo("Anitta - Envolver.mp3");
        var cambios = new List<(string, CambiosFicha)> { (ruta, new PropuestaFicha(ruta, "PT", TipoVoz.Vocal, "IA").Cambios()) };

        var anotado = AceptacionFichas.Anotar(almacen, cambios);
        almacen.Poner(ruta, cambios[0].Item2.AplicarA(null));
        var corregida = almacen.Obtener(ruta)!;
        corregida.Idioma = "ES";   // el usuario lo corrige en Ficha DJ
        almacen.Poner(ruta, corregida);

        anotado.Deshacer(almacen);

        var f = almacen.Obtener(ruta)!;
        Assert.Equal("ES", f.Idioma);               // su corrección se queda
        Assert.Equal(TipoVoz.SinIndicar, f.Voz);    // la voz, que seguía como se puso, se devuelve
    }

    // --- I1: el texto del origen no promete lo que Deezer no comprueba ---

    [Fact]
    public void El_origen_de_la_IA_no_dice_que_el_nombre_este_confirmado()
    {
        var texto = CortadoFila.TextoOrigen(OrigenCompletado.IaConfirmada);
        Assert.DoesNotContain("confirmad", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Deezer", texto);
    }

    // «Completar las marcadas» cuenta las de la IA por cómo empieza el texto.
    [Theory]
    [InlineData(OrigenCompletado.IaConfirmada, true)]
    [InlineData(OrigenCompletado.IaSinConfirmar, true)]
    [InlineData(OrigenCompletado.Etiquetas, false)]
    [InlineData(OrigenCompletado.SinPropuesta, false)]
    public void Las_de_la_IA_empiezan_por_IA(OrigenCompletado origen, bool esIa)
        => Assert.Equal(esIa, CortadoFila.TextoOrigen(origen).StartsWith("IA", StringComparison.Ordinal));

    // --- X10: el menú contextual mientras una herramienta escribe archivos ---

    [Fact]
    public void Mientras_se_escriben_archivos_no_se_puede_escuchar_editar_ni_mandar_a_la_papelera()
    {
        var m = AsistenteView.CalcularMenu(haySeleccion: true, ocupado: true, cuantas: 3);
        Assert.False(m.Escuchar);
        Assert.False(m.Editar);
        Assert.False(m.Papelera);
        Assert.True(m.Carpeta);   // abrir la carpeta no toca el archivo
        Assert.True(m.Marcar);
        Assert.Contains("espera a que termine", m.TextoPapelera);
    }

    [Fact]
    public void Sin_proceso_en_curso_el_menu_es_el_de_siempre()
    {
        var m = AsistenteView.CalcularMenu(haySeleccion: true, ocupado: false, cuantas: 3);
        Assert.True(m.Escuchar && m.Editar && m.Papelera && m.Carpeta && m.Marcar);
        Assert.Equal("Enviar a la papelera (3)", m.TextoPapelera);
        Assert.Equal("Escuchar / Parar", m.TextoEscuchar);

        var sinSeleccion = AsistenteView.CalcularMenu(haySeleccion: false, ocupado: false, cuantas: 0);
        Assert.False(sinSeleccion.Escuchar || sinSeleccion.Papelera || sinSeleccion.Marcar);
    }
}
