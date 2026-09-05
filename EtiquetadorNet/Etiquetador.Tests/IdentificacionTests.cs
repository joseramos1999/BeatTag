using Etiquetador.App.Services;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// La decisión de la pestaña «Comprobar audio»: dado lo que dice un archivo y lo que resultó ser su
/// audio, ¿hay que pintarlo en rojo o no?
///
/// Es lo único que puede hacer daño aquí. Un falso rojo manda al usuario a corregir un archivo que
/// estaba bien, y con unos pocos deja de fiarse de la columna entera; un falso verde deja pasar
/// justo lo que la pestaña existe para encontrar. Por eso se prueba con los nombres reales de una
/// biblioteca de DJ y no con casos de laboratorio.
/// </summary>
public class IdentificacionTests
{
    private static string Ruta(params string[] partes) => Rutas.De(partes);

    // ---------- Exclusiones: las mismas que en Enriquecer ----------

    [Fact]
    public void Una_carpeta_de_mashups_se_excluye_entera()
    {
        var ruta = Ruta("Musica", "Mashups 2024", "lo que sea.mp3");
        Assert.True(Identificacion.SeExcluye(ruta, out var motivo));
        Assert.Contains("mashup", motivo);
    }

    [Theory]
    [InlineData("Dj Nev - Something Mashup.mp3")]
    [InlineData("Quevedo vs Bad Bunny - Algo.mp3")]
    [InlineData("Artista - Tema (Bootleg).mp3")]
    [InlineData("Artista - Tema (Transition 128).mp3")]
    public void Las_mezclas_no_se_comprueban(string nombre)
        => Assert.True(Identificacion.SeExcluye(Ruta("Musica", nombre), out _));

    // Una edición de catálogo NO es una mezcla: existe como lanzamiento y debe comprobarse.
    [Theory]
    [InlineData("Bad Bunny - Titi Me Pregunto (Radio Edit).mp3")]
    [InlineData("Karol G - Provenza (Extended Mix).mp3")]
    [InlineData("Quevedo - Punto G.mp3")]
    public void Lo_que_si_existe_publicado_se_comprueba(string nombre)
        => Assert.False(Identificacion.SeExcluye(Ruta("Musica", nombre), out _));

    // ---------- El veredicto ----------

    [Fact]
    public void Sin_respuesta_del_servicio_no_se_afirma_nada()
    {
        var v = Identificacion.Comparar("Bad Bunny - Titi Me Pregunto.mp3", null, null, null);
        Assert.Equal(VeredictoId.SinIdentificar, v.Estado);
    }

    [Fact]
    public void El_audio_que_coincide_con_el_nombre_sale_correcto()
    {
        var v = Identificacion.Comparar("Bad Bunny - Titi Me Pregunto.mp3", null, null,
            new Identificado("Bad Bunny", "Tití Me Preguntó"));
        Assert.Equal(VeredictoId.Coincide, v.Estado);
    }

    // El caso más corriente de la biblioteca: el archivo trae adornos de edición y el servicio
    // devuelve el título comercial pelado. Comparar el texto literal marcaría en rojo media
    // biblioteca, y ese es el error que haría inservible la pestaña.
    [Theory]
    [InlineData("Bad Bunny - Titi Me Pregunto (IVAN RF Hype Intro) 106 BPM.mp3")]
    [InlineData("Bad Bunny - Titi Me Pregunto (Extended Mix) 8A.mp3")]
    [InlineData("Bad Bunny - Titi Me Pregunto [Dirty].mp3")]
    public void Los_adornos_de_edicion_no_cuentan_como_discrepancia(string nombre)
    {
        var v = Identificacion.Comparar(nombre, null, null, new Identificado("Bad Bunny", "Tití Me Preguntó"));
        Assert.Equal(VeredictoId.Coincide, v.Estado);
    }

    [Fact]
    public void Un_audio_distinto_se_marca_y_se_dice_cual_es()
    {
        var v = Identificacion.Comparar("Bad Bunny - Titi Me Pregunto.mp3", null, null,
            new Identificado("Karol G", "Provenza"));
        Assert.Equal(VeredictoId.Difiere, v.Estado);
        // El motivo tiene que servir para decidir sin abrir el archivo.
        Assert.Contains("Karol G", v.Motivo);
        Assert.Contains("Provenza", v.Motivo);
    }

    [Fact]
    public void Mismo_titulo_pero_de_otro_artista_tambien_se_marca()
    {
        var v = Identificacion.Comparar("Shakira - Antologia.mp3", null, null,
            new Identificado("Aitana", "Antología"));
        Assert.Equal(VeredictoId.Difiere, v.Estado);
        Assert.Contains("Aitana", v.Motivo);
    }

    // Una errata en el nombre la puso quien nombró el archivo, no el audio: no es una discrepancia.
    [Fact]
    public void Una_errata_en_el_nombre_no_es_una_discrepancia()
    {
        var v = Identificacion.Comparar("Bad Bunny - Resentia.mp3", null, null,
            new Identificado("Bad Bunny", "Resentía"));
        Assert.Equal(VeredictoId.Coincide, v.Estado);
    }

    // El servicio devuelve la lista entera de artistas; el archivo casi nunca la trae.
    [Fact]
    public void Que_el_servicio_liste_mas_artistas_no_es_una_discrepancia()
    {
        var v = Identificacion.Comparar("Bad Bunny - Me Porto Bonito.mp3", null, null,
            new Identificado("Bad Bunny, Chencho Corleone", "Me Porto Bonito"));
        Assert.Equal(VeredictoId.Coincide, v.Estado);
    }

    // Hay archivos con el nombre hecho un desastre y las etiquetas bien puestas. Basta con que uno
    // de los dos diga la verdad para dar la canción por correcta.
    [Fact]
    public void Basta_con_que_las_etiquetas_digan_la_verdad()
    {
        var v = Identificacion.Comparar("track01.mp3", "Bad Bunny", "Tití Me Preguntó",
            new Identificado("Bad Bunny", "Tití Me Preguntó"));
        Assert.Equal(VeredictoId.Coincide, v.Estado);
    }

    // Un archivo que no afirma nada no puede estar equivocado. Informar de lo que es sí ayuda;
    // pintarlo en rojo, no.
    [Theory]
    [InlineData("01.mp3")]
    [InlineData("track 07.mp3")]
    [InlineData("pista 3.mp3")]
    public void Un_archivo_que_no_dice_que_cancion_es_no_se_marca_en_rojo(string nombre)
    {
        var v = Identificacion.Comparar(nombre, null, null, new Identificado("Karol G", "Provenza"));
        Assert.NotEqual(VeredictoId.Difiere, v.Estado);
        Assert.Contains("Provenza", v.Motivo);
    }

    // ---------- Falsos avisos vistos en una comprobación real de 14.661 canciones ----------
    //
    // Cada uno de estos salía en rojo el 2026-09-04 y NO era un error del archivo. Están aquí con
    // sus nombres reales a propósito: son la prueba de que el criterio se afinó contra lo que pasa
    // de verdad y no contra lo que yo imaginaba que pasaba.

    // El catálogo y el archivo casi nunca nombran a los mismos artistas ni en el mismo orden.
    // Antes se comparaba solo el primero de cada lado, y eso convertía media colaboración en
    // discrepancia.
    [Theory]
    [InlineData("Bad Bunny, Jowell & Randy, Nengo Flow - Safaera.mp3", "Randy;Nengo Flow;Bad Bunny", "Safaera")]
    [InlineData("Farruko, Rvssian, J Balvin - Ponle.mp3", "J.Balvin, Rvssian, Farruko", "Ponle")]
    [InlineData("J Balvin - Bonita.mp3", "Jowell & Randy, J. Balvin", "Bonita")]
    [InlineData("Alex Gargolas, Randy - Soy una Gargola.mp3", "Randy", "Soy Una Gargola")]
    [InlineData("Daddy Yankee, Eddie Dee - Taladro (Dirty).mp3", "Eddie Dee, Daddy Yankee", "Taladro")]
    public void Los_mismos_artistas_en_otro_orden_no_son_una_discrepancia(string nombre, string artista, string titulo)
        => Assert.Equal(VeredictoId.Coincide,
            Identificacion.Comparar(nombre, null, null, new Identificado(artista, titulo)).Estado);

    // Una errata en el nombre del artista es de quien tecleó el catálogo, no dos personas distintas.
    // Los títulos ya toleraban erratas; los artistas no, y no había razón para la diferencia.
    [Theory]
    [InlineData("Dionne Farris - I Know.mp3", "Dione Farris", "I Know")]
    [InlineData("Anuel AA, Spiff TV - No Love.mp3", "Annuel Aa", "Noo Love")]
    public void Una_errata_en_el_artista_no_es_una_discrepancia(string nombre, string artista, string titulo)
        => Assert.Equal(VeredictoId.Coincide,
            Identificacion.Comparar(nombre, null, null, new Identificado(artista, titulo)).Estado);

    // El título por el que se conoce un tema vive muchas veces entre paréntesis. La limpieza los
    // tira -para BUSCAR estorban-, y con ellos se iba lo único que casaba.
    [Theory]
    [InlineData("Rasheeda - My Bubble Gum.mp3", "Rasheeda", "Got That Good (My Bubble Gum)")]
    [InlineData("Pitbull - I Know You Want Me (Calle Ocho).mp3", "Pitbull", "Calle Ocho")]
    public void Un_titulo_alternativo_entre_parentesis_sigue_contando(string nombre, string artista, string titulo)
        => Assert.Equal(VeredictoId.Coincide,
            Identificacion.Comparar(nombre, null, null, new Identificado(artista, titulo)).Estado);

    // Nombre al revés: el título ocupa el hueco del artista. Ninguna regla de separación lo va a
    // colocar bien, pero el dato está en el nombre y basta con mirarlo antes de acusar.
    [Fact]
    public void Un_nombre_al_reves_no_es_una_discrepancia()
        => Assert.Equal(VeredictoId.Coincide,
            Identificacion.Comparar("Hombres Y Mujeres - Feid ft. Gordo (Dellaveg.mp3", null, null,
                new Identificado("Gordo, Feid", "Hombres y mujeres")).Estado);

    // LA REGLA QUE LO SOSTIENE TODO: mirar el nombre entero solo puede ABSOLVER, nunca acusar.
    // Al saltármela, un archivo llamado «1999» pasó de «no dice qué canción es» -que era correcto-
    // a «no coincide», acusándolo por algo que nunca afirmó.
    [Theory]
    [InlineData("Prince - 1999.mp3", "Prince", "1999")]
    [InlineData("Javier Alvarez - 1,2,3,4.mp3", "Javier Alvarez", "1,2,3,4")]
    public void Un_titulo_que_es_solo_un_numero_nunca_se_acusa(string nombre, string artista, string titulo)
        => Assert.NotEqual(VeredictoId.Difiere,
            Identificacion.Comparar(nombre, null, null, new Identificado(artista, titulo)).Estado);

    // Una acapella no es una grabación publicada, es una herramienta de DJ. Los servicios la casan
    // con la recopilación de DJ que la contiene: «China (Acapella)» devolvió «Latin Heat Live Mix 4».
    [Theory]
    [InlineData("Anuel AA, Ozuna - China (Acapella).mp3")]
    [InlineData("Arcangel - Feliz Navidad 3 (Acapella).mp3")]
    [InlineData("Wolfine - Escapate Conmigo (Acapella, Starter).mp3")]
    [InlineData("Alexis & Fido - El Tiburon (Accapella).mp3")]
    [InlineData("Tema - Algo (Percapella).mp3")]
    public void Las_acapellas_no_se_comprueban(string nombre)
        => Assert.True(Identificacion.SeExcluye(Ruta("Musica", nombre), out _));

    // Y lo que NO puede pasar: que por rebajar el criterio dejen de verse los errores de verdad.
    // Estos cuatro salían en rojo con razón y tienen que seguir saliendo.
    [Theory]
    [InlineData("Bruno Mars - Marry You.mp3", "Bruno Mars", "Grenade")]
    [InlineData("Rauw Alejandro - Fantasias.mp3", "Barthold Kuijken", "The Twelve Fantasias for Traverse Flute")]
    [InlineData("Pitbull, Marc Anthony - Rain Over Me.mp3", "Bonnie Tyler", "Have You Ever Seen the Rain?")]
    [InlineData("Aventura - Ella Y Yo.mp3", "Gino Paoli", "Sassi")]
    public void Los_errores_de_verdad_siguen_saliendo(string nombre, string artista, string titulo)
        => Assert.Equal(VeredictoId.Difiere,
            Identificacion.Comparar(nombre, null, null, new Identificado(artista, titulo)).Estado);

    // ---------- Lectura de la respuesta del servicio ----------

    [Fact]
    public void Una_respuesta_con_cancion_se_lee_entera()
    {
        var r = AuddProvider.Interpretar(
            """{"status":"success","result":{"artist":"Bad Bunny","title":"Tití Me Preguntó","album":"Un Verano Sin Ti"}}""");

        Assert.Equal("", r.Error);
        Assert.False(r.Detener);
        Assert.NotNull(r.Match);
        Assert.Equal("Bad Bunny", r.Match!.Artist);
        Assert.Equal("Tití Me Preguntó", r.Match.Title);
        Assert.Equal("Un Verano Sin Ti", r.Match.Album);
    }

    // Que no la reconozca es una respuesta normal, no un fallo: se guarda para no volver a pagarla.
    [Fact]
    public void No_reconocerla_no_es_un_error()
    {
        var r = AuddProvider.Interpretar("""{"status":"success","result":null}""");
        Assert.Null(r.Match);
        Assert.Equal("", r.Error);
        Assert.False(r.Detener);
    }

    // Cuota agotada o clave mala afectan a TODAS: hay que parar la pasada, no seguir gastando
    // intentos condenados a fallar. Es la diferencia entre «no reconoce tu música» y «se acabó».
    [Theory]
    [InlineData(900)]
    [InlineData(901)]
    [InlineData(903)]
    public void Un_problema_de_la_cuenta_detiene_la_pasada(int codigo)
    {
        var r = AuddProvider.Interpretar(
            "{\"status\":\"error\",\"error\":{\"error_code\":" + codigo
            + ",\"error_message\":\"no limits left\"}}");

        Assert.True(r.Detener);
        Assert.Contains("no limits left", r.Error);
    }

    // Un error de una sola canción no puede tumbar la pasada entera.
    [Fact]
    public void Un_error_de_una_cancion_no_detiene_la_pasada()
    {
        var r = AuddProvider.Interpretar(
            """{"status":"error","error":{"error_code":300,"error_message":"fingerprinting failed"}}""");

        Assert.False(r.Detener);
        Assert.NotEqual("", r.Error);
    }

    [Fact]
    public void Una_respuesta_ilegible_se_trata_como_error_y_no_como_silencio()
    {
        var r = AuddProvider.Interpretar("<html>502 Bad Gateway</html>");
        Assert.Null(r.Match);
        Assert.NotEqual("", r.Error);
    }

    // ---------- El informe que se genera para revisar la pasada ----------

    // El separador de estos informes es ';' porque es lo que espera Excel en español, y en una
    // biblioteca de DJ hay nombres con ';' y con comillas. Sin escapar, esas filas se parten en
    // columnas de más y el informe deja de cuadrar justo en los archivos más raros, que son los
    // que uno quiere mirar.
    [Theory]
    [InlineData("Artista - Tema.mp3", "\"Artista - Tema.mp3\"")]
    [InlineData("A; B - Tema.mp3", "\"A; B - Tema.mp3\"")]
    [InlineData("Tema \"raro\".mp3", "\"Tema \"\"raro\"\".mp3\"")]
    [InlineData(null, "\"\"")]
    public void Un_campo_del_informe_se_escapa_siempre(string? entrada, string esperado)
        => Assert.Equal(esperado, Etiquetador.Core.TextUtils.CsvField(entrada));

    [Fact]
    public void Un_salto_de_linea_no_parte_una_fila_del_informe()
    {
        var salida = Etiquetador.Core.TextUtils.CsvField("Tema\r\ncon salto");
        Assert.DoesNotContain("\n", salida);
        Assert.DoesNotContain("\r", salida);
    }

    // ---------- Cuándo vale la respuesta guardada y cuándo hay que volver a preguntar ----------
    //
    // Es la decisión con más consecuencias de la pestaña, y falla hacia los dos lados: laxa, se
    // pagan consultas ya hechas; estricta, un «no» del motor gratuito entierra para siempre las
    // canciones que el de pago sí sabría, que son las únicas por las que valía la pena pagar.

    // Saber qué es una canción cierra el asunto, se use el motor que se use.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Una_cancion_ya_identificada_no_se_vuelve_a_preguntar(bool usarPago)
        => Assert.True(IdentificationScanner.RespuestaCerrada(
            sinResultado: false, pagoIntentado: false, usarPago: usarPago));

    // Con el de pago apagado, el «no» del gratuito es la última palabra disponible.
    [Fact]
    public void Sin_motor_de_pago_la_negativa_del_gratuito_cierra()
        => Assert.True(IdentificationScanner.RespuestaCerrada(
            sinResultado: true, pagoIntentado: false, usarPago: false));

    // EL CASO QUE IMPORTA: se activa el de pago después de una pasada gratuita. Esas canciones
    // tienen que volver a la cola, o el motor de pago no llegaría nunca a las suyas.
    [Fact]
    public void Activar_el_pago_reabre_lo_que_el_gratuito_no_supo()
        => Assert.False(IdentificationScanner.RespuestaCerrada(
            sinResultado: true, pagoIntentado: false, usarPago: true));

    // Y si ya se preguntó a los dos, no se vuelve a pagar por el mismo «no».
    [Fact]
    public void Una_negativa_de_los_dos_motores_no_se_paga_dos_veces()
        => Assert.True(IdentificationScanner.RespuestaCerrada(
            sinResultado: true, pagoIntentado: true, usarPago: true));

    // ---------- Pedir otro fragmento del tema ----------
    //
    // Un aviso falso suele ser que el trozo enviado cayó en un break o en un trozo hablado. Volver
    // a preguntar solo sirve si se manda un trozo DISTINTO; repetir el mismo daría la misma
    // respuesta y habría gastado una consulta para nada.

    [Fact]
    public void Pedir_otro_fragmento_no_repite_el_anterior()
    {
        foreach (var f in IdentificationScanner.Fracciones)
            Assert.NotEqual(f, IdentificationScanner.SiguienteFraccion(f));
    }

    // Una respuesta guardada antes de que se anotara el punto no trae ninguno: se hizo con el de
    // partida, así que lo siguiente tiene que ser otro, no el de partida otra vez.
    [Fact]
    public void Sin_punto_guardado_se_pasa_al_siguiente_y_no_al_de_partida()
    {
        var siguiente = IdentificationScanner.SiguienteFraccion(0);
        Assert.NotEqual(IdentificationScanner.Fracciones[0], siguiente);
        Assert.Contains(siguiente, IdentificationScanner.Fracciones);
    }

    // Insistiendo se recorren todos los puntos antes de repetir ninguno: cada reintento prueba
    // sitio nuevo hasta agotar la lista.
    [Fact]
    public void Insistiendo_se_recorren_todos_los_puntos_antes_de_repetir()
    {
        var vistos = new List<double>();
        var actual = IdentificationScanner.Fracciones[0];
        for (var i = 0; i < IdentificationScanner.Fracciones.Length; i++)
        {
            vistos.Add(actual);
            actual = IdentificationScanner.SiguienteFraccion(actual);
        }

        Assert.Equal(IdentificationScanner.Fracciones.Length, vistos.Distinct().Count());
        Assert.Equal(IdentificationScanner.Fracciones[0], actual);   // y vuelve al principio
    }

    // Ninguno cae en el intro ni en la cola: son justo los sitios donde el audio no se parece a la
    // grabación publicada, que es el problema que esta pestaña vino a resolver.
    [Fact]
    public void Ningun_punto_cae_en_el_intro_ni_en_la_cola()
        => Assert.All(IdentificationScanner.Fracciones, f => Assert.InRange(f, 0.10, 0.75));
}
