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
        // interfaz-8: no todas las herramientas usan la IA, y «Renombrar con IA» no funciona sin ella.
        Assert.EndsWith("Mientras tanto, " + AsistenteViewModel.SinIa, texto);
        Assert.Contains("las herramientas que la usan", AsistenteViewModel.TextoIaDesactivada);
        Assert.Contains("salvo «Renombrar con IA»", AsistenteViewModel.TextoIaDesactivada);
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
        var m = AsistenteView.CalcularMenu(haySeleccion: true, ocupado: true, escribiendo: true, cuantas: 3);
        Assert.False(m.Escuchar);
        Assert.False(m.Editar);
        Assert.False(m.Papelera);
        Assert.True(m.Carpeta);   // abrir la carpeta no toca el archivo
        Assert.True(m.Marcar);
        Assert.Contains("espera a que termine", m.TextoPapelera);
        Assert.Contains("escribir los archivos", m.TextoEscuchar);
    }

    // Visto en la revisión: «Proponer fichas» con límite 5000 tarda minutos y solo lee. Mientras tanto
    // se tienen que poder escuchar las propuestas que ya están en la tabla. Editar (salta a otra página,
    // y el bloqueo global no deja) y la papelera siguen esperando.
    [Fact]
    public void Mientras_solo_se_lee_se_puede_escuchar_pero_no_editar_ni_mandar_a_la_papelera()
    {
        var m = AsistenteView.CalcularMenu(haySeleccion: true, ocupado: true, escribiendo: false, cuantas: 1);
        Assert.True(m.Escuchar);
        Assert.Equal("Escuchar / Parar", m.TextoEscuchar);
        Assert.False(m.Editar);
        Assert.False(m.Papelera);
        Assert.True(m.Carpeta && m.Marcar);
    }

    [Fact]
    public void Sin_proceso_en_curso_el_menu_es_el_de_siempre()
    {
        var m = AsistenteView.CalcularMenu(haySeleccion: true, ocupado: false, escribiendo: false, cuantas: 3);
        Assert.True(m.Escuchar && m.Editar && m.Papelera && m.Carpeta && m.Marcar);
        Assert.Equal("Enviar a la papelera (3)", m.TextoPapelera);
        Assert.Equal("Escuchar / Parar", m.TextoEscuchar);

        var sinSeleccion = AsistenteView.CalcularMenu(haySeleccion: false, ocupado: false, escribiendo: false, cuantas: 0);
        Assert.False(sinSeleccion.Escuchar || sinSeleccion.Papelera || sinSeleccion.Marcar);
    }

    // --- interfaz-3: la papelera cuenta archivos, no filas ---

    // En «Limpiar publicidad de packs» un archivo tiene una fila por etiqueta (álbum y artista). Con
    // las dos seleccionadas, el diálogo decía «2 archivos» y el segundo envío fallaba en inglés.
    [Fact]
    public void La_papelera_del_asistente_envia_cada_archivo_una_vez()
    {
        var a = Rutas.Archivo("a.mp3");
        var b = Rutas.Archivo("b.mp3");
        var filas = new[]
        {
            new PublicidadFila(new PropuestaPublicidad(a, CampoPublicidad.Album, "Latin Box", "", "pool", true), true),
            new PublicidadFila(new PropuestaPublicidad(a.ToUpperInvariant(), CampoPublicidad.Artista, "@x", "Ozuna", "u", true), true),
            new PublicidadFila(new PropuestaPublicidad(b, CampoPublicidad.Album, "@y", "", "u", true), true),
        };

        Assert.Equal(new[] { a, b }, AsistenteView.RutasDistintas(filas));
    }

    // Enviada la fila del artista, la del álbum del mismo archivo (sin seleccionar) también sale de la
    // tabla: su archivo ya no está y aplicarla fallaría.
    [Fact]
    public void Tras_la_papelera_salen_de_la_tabla_todas_las_filas_de_los_archivos_enviados()
    {
        var a = Rutas.Archivo("a.mp3");
        var album = new PublicidadFila(new PropuestaPublicidad(a, CampoPublicidad.Album, "Latin Box", "", "pool", true), true);
        var artista = new PublicidadFila(new PropuestaPublicidad(a, CampoPublicidad.Artista, "@x", "Ozuna", "u", true), true);
        var otra = new PublicidadFila(new PropuestaPublicidad(Rutas.Archivo("b.mp3"), CampoPublicidad.Album, "@y", "", "u", true), true);

        var quitar = AsistenteView.FilasDeArchivos(new object[] { album, artista, otra }, new[] { a });

        Assert.Equal(new object[] { album, artista }, quitar);
    }

    // --- X5 (revisión): una colección borrada no cambia el ámbito en silencio ---

    // El caso de la revisión: «Bachata para cerrar» se borra en Colecciones y, al volver al Asistente,
    // los desplegables se rellenan. Antes pasaban a «Toda la biblioteca» sin decir nada.
    [Fact]
    public void Al_rellenar_se_sabe_que_la_coleccion_elegida_ya_no_existe()
    {
        var bachata = Coleccion("Bachata para cerrar");
        var pico = Coleccion("Pico");
        var elegida = SelectorAmbito.Opciones(new[] { pico, bachata }, conTodaLaBiblioteca: true)[2];

        var ahora = SelectorAmbito.Elegir(SelectorAmbito.Opciones(new[] { pico }, conTodaLaBiblioteca: true), elegida, out var perdida);

        Assert.Equal("Bachata para cerrar", perdida);
        Assert.True(ahora!.EsTodaLaBiblioteca);
    }

    [Fact]
    public void Si_la_coleccion_sigue_o_no_habia_ninguna_no_se_avisa()
    {
        var pico = Coleccion("Pico");
        var opciones = SelectorAmbito.Opciones(new[] { pico }, conTodaLaBiblioteca: true);

        SelectorAmbito.Elegir(opciones, opciones[1], out var sigue);
        SelectorAmbito.Elegir(opciones, AmbitoOpcion.TodaLaBiblioteca, out var biblioteca);
        SelectorAmbito.Elegir(opciones, null, out var nada);

        Assert.Equal("", sigue);
        Assert.Equal("", biblioteca);
        Assert.Equal("", nada);
    }

    [Fact]
    public void El_aviso_dice_que_coleccion_falta_y_a_que_pasa_cada_herramienta()
    {
        var aviso = SelectorAmbito.AvisoAmbitosPerdidos(new[]
        {
            new SelectorAmbito.AmbitoPerdido("Ordenar para mezclar", "Bachata para cerrar", "Pico"),
            new SelectorAmbito.AmbitoPerdido("Proponer fichas", "Bachata para cerrar", AmbitoOpcion.TextoTodaLaBiblioteca),
            new SelectorAmbito.AmbitoPerdido("Renombrar con IA", "Bachata para cerrar", AmbitoOpcion.TextoTodaLaBiblioteca),
        });

        Assert.Equal("La colección «Bachata para cerrar» ya no existe en Colecciones: «Ordenar para mezclar» pasa a «Pico»; "
                     + "«Proponer fichas» y «Renombrar con IA» pasan a «Toda la biblioteca».", aviso);
    }

    [Fact]
    public void Sin_colecciones_que_elegir_mezclar_se_queda_sin_ninguna()
    {
        var aviso = SelectorAmbito.AvisoAmbitosPerdidos(new[] { new SelectorAmbito.AmbitoPerdido("Ordenar para mezclar", "Pico", null) });
        Assert.Equal("La colección «Pico» ya no existe en Colecciones: «Ordenar para mezclar» se queda sin colección.", aviso);
        Assert.Equal("", SelectorAmbito.AvisoAmbitosPerdidos(Array.Empty<SelectorAmbito.AmbitoPerdido>()));
    }

    // --- X13 (revisión): «Comprobar» quita la pausa por fallos ---

    /// <summary>Un Ollama de mentira que se cae y vuelve: lista un modelo y responde (o no) a /api/generate.</summary>
    private sealed class OllamaQueVuelve : HttpMessageHandler
    {
        public bool Caido { get; set; }
        public int Generaciones;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath == "/api/tags")
                return Task.FromResult(Json("""{"models":[{"name":"llama3.2:latest"}]}"""));
            Interlocked.Increment(ref Generaciones);
            if (Caido) throw new HttpRequestException("Conexión rechazada.");
            return Task.FromResult(Json("""{"response":"{\"idioma\":\"ES\"}","done":true}"""));
        }

        private static HttpResponseMessage Json(string cuerpo)
            => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json") };
    }

    // El caso de la revisión: Ollama se cae a mitad de «Proponer fichas», la herramienta corta a los 3
    // fallos y la cabecera pide «Pulsa «Comprobar» cuando esté resuelto». Se arranca Ollama, se pulsa
    // «Comprobar» y enseguida «Proponer»: sin reiniciar, la pausa de 30 s seguía y la IA devolvía el
    // error sin llegar a preguntar.
    [Fact]
    public async Task Comprobar_con_la_IA_de_vuelta_quita_la_pausa_por_fallos()
    {
        var ollama = new OllamaQueVuelve { Caido = true };
        var ia = new OllamaClient(new ApiClient(new AppPaths(_dir)), http: new HttpClient(ollama));
        for (var i = 0; i < OllamaClient.FallosSeguidosParaPausar; i++)
            await ia.PedirJsonAsync("sistema", $"peticion {i}", "llama3.2");
        var (_, enPausa) = await ia.PedirJsonAsync("sistema", "otra", "llama3.2");
        Assert.Contains("No se pudo contactar", enPausa);
        Assert.Equal(OllamaClient.FallosSeguidosParaPausar, ollama.Generaciones);   // ya no pregunta

        ollama.Caido = false;   // Ollama vuelve
        var estado = await ia.ComprobarModeloAsync("llama3.2");
        var (disponible, _, _) = AsistenteViewModel.TrasComprobarIa(ia, estado, "llama3.2");

        Assert.True(disponible);
        var (json, error) = await ia.PedirJsonAsync("sistema", "despues de comprobar", "llama3.2");
        Assert.Equal("", error);
        Assert.NotNull(json);
        Assert.Equal(OllamaClient.FallosSeguidosParaPausar + 1, ollama.Generaciones);
    }

    // Si la comprobación dice que la IA sigue sin estar, no se toca nada.
    [Fact]
    public async Task Comprobar_sin_IA_no_quita_la_pausa()
    {
        var ollama = new OllamaQueVuelve { Caido = true };
        var ia = new OllamaClient(new ApiClient(new AppPaths(_dir)), http: new HttpClient(ollama));
        for (var i = 0; i < OllamaClient.FallosSeguidosParaPausar; i++)
            await ia.PedirJsonAsync("sistema", $"peticion {i}", "llama3.2");

        var (disponible, _, _) = AsistenteViewModel.TrasComprobarIa(
            ia, new EstadoModelo("", false, "Ollama no responde.", Array.Empty<string>()), "llama3.2");

        Assert.False(disponible);
        await ia.PedirJsonAsync("sistema", "otra", "llama3.2");
        Assert.Equal(OllamaClient.FallosSeguidosParaPausar, ollama.Generaciones);
    }
}
