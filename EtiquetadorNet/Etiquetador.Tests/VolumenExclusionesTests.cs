using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Volumen: casilla «Ajustar» por fila, «Ajustar solo las seleccionadas» y exclusiones guardadas
/// (E15, equipo «volumen_copias», tanda 4). El caso real: acapellas, intros y ediciones que el DJ
/// quiere más bajas a propósito, y que el modo automático (antes «Nivel medio de la biblioteca») subía junto al resto.
/// </summary>
public class VolumenExclusionesTests
{
    private static readonly string Raiz = Path.Combine(Path.GetTempPath(), "beattag-volumen-no-se-crea");
    private static readonly string Acapellas = Path.Combine(Raiz, "Acapellas");
    private static readonly string Reggaeton = Path.Combine(Raiz, "Reggaeton");

    /// <summary>Una fila medida con objetivo de club (−11 LUFS) y un pico con margen de sobra.</summary>
    private static LoudnessRow Fila(string carpeta, string nombre, double lufs, double pico = -8)
        => new() { FileName = nombre, Folder = Raiz, FilePath = Path.Combine(carpeta, nombre), Lufs = lufs, PeakDb = pico, Target = -11 };

    private static HashSet<string> Ninguna() => new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ qué está excluido

    [Fact]
    public void Una_carpeta_excluida_cubre_sus_subcarpetas_pero_no_las_que_empiezan_igual()
    {
        var carpetas = new[] { Acapellas };
        Assert.Equal(ExclusionVolumen.Carpeta, AjusteVolumenSeleccion.De(Path.Combine(Acapellas, "a.mp3"), carpetas, Ninguna()));
        Assert.Equal(ExclusionVolumen.Carpeta, AjusteVolumenSeleccion.De(Path.Combine(Acapellas, "2025", "b.mp3"), carpetas, Ninguna()));
        Assert.Equal(ExclusionVolumen.Ninguna, AjusteVolumenSeleccion.De(Path.Combine(Raiz, "Acapellas 2", "c.mp3"), carpetas, Ninguna()));
        Assert.Equal(ExclusionVolumen.Ninguna, AjusteVolumenSeleccion.De(Path.Combine(Reggaeton, "d.mp3"), carpetas, Ninguna()));
    }

    [Fact]
    public void Una_cancion_excluida_se_reconoce_sin_distinguir_mayusculas()
    {
        var ruta = Path.Combine(Reggaeton, "Intro - Gasolina (Hype Intro).mp3");
        var canciones = new HashSet<string>(new[] { ruta.ToUpperInvariant() }, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ExclusionVolumen.Cancion, AjusteVolumenSeleccion.De(ruta, Array.Empty<string>(), canciones));
    }

    [Fact]
    public void Anadir_carpetas_no_repite_ni_deja_entradas_que_no_cambian_nada()
    {
        var sub = Path.Combine(Acapellas, "2025");
        var (l1, n1) = AjusteVolumenSeleccion.AnadirCarpetas(new[] { sub }, new[] { Reggaeton, Reggaeton + Path.DirectorySeparatorChar });
        Assert.Equal(1, n1);
        Assert.Equal(new[] { sub, Reggaeton }, l1);

        // Excluir el padre absorbe a la subcarpeta ya excluida.
        var (l2, n2) = AjusteVolumenSeleccion.AnadirCarpetas(l1, new[] { Acapellas });
        Assert.Equal(1, n2);
        Assert.Equal(new[] { Reggaeton, Acapellas }, l2);

        // Una subcarpeta de otra ya excluida no se añade.
        var (l3, n3) = AjusteVolumenSeleccion.AnadirCarpetas(l2, new[] { Path.Combine(Acapellas, "Otra") });
        Assert.Equal(0, n3);
        Assert.Equal(l2, l3);
    }

    // Con varias filas seleccionadas se excluye la carpeta de cada una: el aviso nombra y cuenta las que
    // entran de verdad en la lista (antes nombraba la primera pedida, aunque ya estuviera excluida, y
    // House\2024 seguida de House, que la sustituye, contaba «2 carpetas»).
    [Fact]
    public void Excluir_carpetas_nombra_y_cuenta_las_que_entran_de_verdad()
    {
        var (_, nuevas) = AjusteVolumenSeleccion.AnadirCarpetasConNombre(new[] { Acapellas }, new[] { Acapellas, Reggaeton });
        Assert.Equal(new[] { Reggaeton }, nuevas);
        Assert.StartsWith("Excluida del ajuste la carpeta «Reggaeton»:", AjusteVolumenSeleccion.TextoCarpetasExcluidas(nuevas, nuevas.Count, 0));

        var house = Path.Combine(Raiz, "House");
        var del2024 = Path.Combine(house, "2024");
        var (lista, entran) = AjusteVolumenSeleccion.AnadirCarpetasConNombre(Array.Empty<string>(), new[] { del2024, house });
        Assert.Equal(new[] { house }, lista);
        Assert.Equal(new[] { house }, entran);
        Assert.Equal(1, AjusteVolumenSeleccion.AnadirCarpetas(Array.Empty<string>(), new[] { del2024, house }).Anadidas);

        var filas = new[] { Fila(Acapellas, "a.mp3", -14), Fila(Acapellas, "b.mp3", -14), Fila(Reggaeton, "c.mp3", -14) };
        Assert.Equal(new[] { Acapellas, Reggaeton }, AjusteVolumenSeleccion.CarpetasDe(filas));
        Assert.Equal("Excluir esta carpeta del ajuste", AjusteVolumenSeleccion.RotuloExcluirCarpetas(1));
        Assert.Equal("Excluir sus 2 carpetas del ajuste", AjusteVolumenSeleccion.RotuloExcluirCarpetas(AjusteVolumenSeleccion.CarpetasDe(filas).Count));
    }

    [Fact]
    public void Anadir_canciones_no_repite()
    {
        var a = Path.Combine(Raiz, "a.mp3");
        var (lista, n) = AjusteVolumenSeleccion.AnadirCanciones(new[] { a }, new[] { a.ToUpperInvariant(), Path.Combine(Raiz, "b.mp3") });
        Assert.Equal(1, n);
        Assert.Equal(2, lista.Count);
    }

    // ------------------------------------------------------------------ la casilla

    [Fact]
    public void La_casilla_sale_marcada_solo_en_las_que_requieren_ajuste_y_respeta_lo_cambiado_a_mano()
    {
        Assert.True(AjusteVolumenSeleccion.MarcaInicial(puedeMarcarse: true, aMano: null));
        Assert.False(AjusteVolumenSeleccion.MarcaInicial(true, aMano: false));
        Assert.False(AjusteVolumenSeleccion.MarcaInicial(false, aMano: true));   // excluida o sin necesidad: nunca

        var baja = Fila(Reggaeton, "baja.mp3", lufs: -16);          // +5 dB: requiere ajuste
        var bien = Fila(Reggaeton, "bien.mp3", lufs: -11.5);        // +0,5 dB: no
        var flac = Fila(Reggaeton, "flac.flac", lufs: -16);         // se mide, no se ajusta
        var sinMargen = Fila(Reggaeton, "pico.mp3", lufs: -16, pico: -0.3);
        Assert.True(baja.Candidata);
        Assert.False(bien.Candidata);
        Assert.False(flac.Candidata);
        Assert.False(sinMargen.Candidata);

        baja.Exclusion = ExclusionVolumen.Carpeta;
        Assert.False(baja.PuedeMarcarse);
        Assert.Contains("Excluida del ajuste", baja.Aviso);
    }

    [Fact]
    public void Solo_el_cambio_del_usuario_cuenta_como_hecho_a_mano()
    {
        var fila = Fila(Reggaeton, "a.mp3", lufs: -16);
        var avisos = 0;
        fila.AlMarcarAMano = _ => avisos++;
        fila.Marcar(true);         // lo que hace la tabla al montarse
        Assert.Equal(0, avisos);
        fila.Ajustar = false;      // lo que hace el clic en la casilla
        Assert.Equal(1, avisos);
    }

    // ------------------------------------------------------------------ qué entra en el ajuste

    [Fact]
    public void Ajustar_toma_solo_lo_marcado_y_dice_lo_que_queda_fuera()
    {
        var a = Fila(Reggaeton, "a.mp3", -16);
        var b = Fila(Reggaeton, "b.mp3", -6);                          // −5 dB: también entra
        var desmarcada = Fila(Reggaeton, "intro.mp3", -17);
        var excluida = Fila(Acapellas, "aca.mp3", -18);
        var bien = Fila(Reggaeton, "bien.mp3", -11);
        foreach (var f in new[] { a, b, desmarcada }) f.Marcar(true);
        desmarcada.Ajustar = false;
        excluida.Exclusion = ExclusionVolumen.Carpeta;

        var s = AjusteVolumenSeleccion.Elegir(new[] { a, b, desmarcada, excluida, bien });

        Assert.Equal(new[] { a, b }, s.Ajustar);
        Assert.Equal(1, s.Desmarcadas);
        Assert.Equal(1, s.Excluidas);
        Assert.Equal(0, s.SinNecesidad);   // lo que no requiere ajuste no se cuenta como «fuera»
    }

    [Fact]
    public void Ajustar_las_seleccionadas_ignora_la_casilla_pero_nunca_toca_las_excluidas()
    {
        var desmarcada = Fila(Reggaeton, "a.mp3", -16);
        var excluida = Fila(Reggaeton, "b.mp3", -16);
        var bien = Fila(Reggaeton, "c.mp3", -11.2);
        var otra = Fila(Reggaeton, "d.mp3", -16);
        excluida.Exclusion = ExclusionVolumen.Cancion;
        otra.Marcar(true);

        var s = AjusteVolumenSeleccion.Elegir(new[] { desmarcada, excluida, bien, otra }, new[] { desmarcada, excluida, bien });

        Assert.Equal(new[] { desmarcada }, s.Ajustar);   // la no seleccionada «otra» no entra aunque esté marcada
        Assert.Equal(1, s.Excluidas);
        Assert.Equal(1, s.SinNecesidad);
        Assert.Equal(0, s.Desmarcadas);
    }

    // ------------------------------------------------------------------ textos

    [Fact]
    public void La_confirmacion_cuenta_solo_lo_que_se_va_a_tocar_y_concuerda_en_numero()
    {
        var una = Fila(Reggaeton, "a.mp3", -16);
        var (t1, c1, n1) = AjusteVolumenSeleccion.TextosConfirmacion(new SeleccionAjuste(new[] { una }, 0, 0, 0), porSeleccion: false, parciales: 0);
        Assert.Equal("Se va a ajustar el volumen de 1 grabación", t1);
        Assert.StartsWith("Se aumentará su nivel.", c1);
        Assert.DoesNotContain("fuera", c1);
        Assert.Contains("«Deshacer última»", n1);

        var baja = Fila(Reggaeton, "b.mp3", -6);
        var (t2, c2, _) = AjusteVolumenSeleccion.TextosConfirmacion(new SeleccionAjuste(new[] { una, baja }, 4, 1, 0), false, parciales: 1);
        Assert.Equal("Se va a ajustar el volumen de 2 grabaciones", t2);
        Assert.Contains("Se aumentará el nivel de 1 y se reducirá el de 1.", c2);
        Assert.Contains("marcadas en la columna «Ajustar»", c2);
        Assert.Contains("En 1 el aumento será parcial, porque no admite más sin distorsionar.", c2);
        Assert.EndsWith("Quedan fuera 4 desmarcadas y 1 excluida del ajuste.", c2);

        var (_, c3, _) = AjusteVolumenSeleccion.TextosConfirmacion(new SeleccionAjuste(new[] { baja, Fila(Reggaeton, "c.mp3", -5) }, 0, 0, 1), porSeleccion: true, 0);
        Assert.StartsWith("Se reducirá el nivel de las 2.", c3);
        Assert.Contains("solo las seleccionadas", c3);
        Assert.EndsWith("Queda fuera 1 seleccionada que no lo requiere.", c3);
    }

    [Fact]
    public void Los_textos_de_estado_distinguen_por_que_no_hay_nada_que_ajustar()
    {
        var vacia = Array.Empty<LoudnessRow>();
        Assert.Equal("No hay ninguna grabación que requiera ajuste.",
                     AjusteVolumenSeleccion.TextoNadaQueAjustar(new SeleccionAjuste(vacia, 0, 0, 0), false, 0));
        Assert.Equal("No hay ningún MP3 que ajustar. 1 grabación está desviada, pero no es MP3: se mide, no se ajusta.",
                     AjusteVolumenSeleccion.TextoNadaQueAjustar(new SeleccionAjuste(vacia, 0, 0, 0), false, 1));
        Assert.Equal("No hay ninguna grabación marcada para ajustar. De las que lo requieren, 3 tienen la casilla «Ajustar» quitada y 1 está excluida del ajuste.",
                     AjusteVolumenSeleccion.TextoNadaQueAjustar(new SeleccionAjuste(vacia, 3, 1, 0), false, 0));
        Assert.Equal("La seleccionada está excluida del ajuste.",
                     AjusteVolumenSeleccion.TextoNadaQueAjustar(new SeleccionAjuste(vacia, 0, 1, 0), true, 0));
        Assert.StartsWith("Ninguna de las seleccionadas requiere ajuste",
                          AjusteVolumenSeleccion.TextoNadaQueAjustar(new SeleccionAjuste(vacia, 0, 0, 2), true, 0));

        Assert.Equal("Exclusiones del ajuste: ninguna", AjusteVolumenSeleccion.TituloExclusiones(0, 0));
        Assert.Equal("Exclusiones del ajuste: 1 carpeta y 3 canciones", AjusteVolumenSeleccion.TituloExclusiones(1, 3));
        Assert.Equal("Exclusiones del ajuste: 1 canción", AjusteVolumenSeleccion.TituloExclusiones(0, 1));
        Assert.Equal("1 grabación marcada para ajustar", AjusteVolumenSeleccion.TextoMarcadas(1));
        Assert.Equal("143 grabaciones marcadas para ajustar", AjusteVolumenSeleccion.TextoMarcadas(143));

        Assert.StartsWith("Excluida del ajuste la carpeta «Acapellas»: 1 grabación que lo requería deja de ajustarse.",
                          AjusteVolumenSeleccion.TextoCarpetasExcluidas(new[] { Acapellas }, 1, 1));
        Assert.Contains("ninguna de sus grabaciones requería ajuste",
                        AjusteVolumenSeleccion.TextoCarpetasExcluidas(new[] { Acapellas }, 1, 0));
        Assert.StartsWith("Excluidas del ajuste 2 carpetas: 7 grabaciones",
                          AjusteVolumenSeleccion.TextoCarpetasExcluidas(new[] { Acapellas, Reggaeton }, 2, 7));
        Assert.Equal("Esa carpeta ya está excluida del ajuste.", AjusteVolumenSeleccion.TextoCarpetasExcluidas(new[] { Acapellas }, 0, 0));
        Assert.StartsWith("Excluidas del ajuste 2 canciones. Se siguen midiendo", AjusteVolumenSeleccion.TextoCancionesExcluidas(2, 2));
    }

    // El umbral se cita desde la constante: si cambia, los textos cambian con él.
    [Fact]
    public void Los_textos_citan_el_umbral_de_1_dB()
    {
        var vacia = Array.Empty<LoudnessRow>();
        var nada = AjusteVolumenSeleccion.TextoNadaQueAjustar(new SeleccionAjuste(vacia, 0, 0, 2), true, 0);
        Assert.Contains("1 dB", nada);
        Assert.DoesNotContain("2 dB", nada);

        var (_, cuerpo, _) = AjusteVolumenSeleccion.TextosConfirmacion(new SeleccionAjuste(new[] { Fila(Reggaeton, "a.mp3", -16) }, 0, 0, 0), false, 0);
        Assert.Contains("más de 1 dB del nivel de referencia", cuerpo);
    }

    [Fact]
    public void Los_textos_nuevos_concuerdan_en_numero()
    {
        Assert.Equal("El nivel de la grabación analizada es correcto.", AjusteVolumenSeleccion.TextoResumen(1, 1, 0, 0, 0));
        Assert.Equal("El nivel es uniforme en las 12 grabaciones analizadas.", AjusteVolumenSeleccion.TextoResumen(12, 12, 0, 0, 0));
        Assert.Equal("9 con el nivel correcto · 1 por debajo · 0 por encima. 1 requiere ajuste.", AjusteVolumenSeleccion.TextoResumen(10, 9, 1, 0, 1));
        Assert.Equal("8 con el nivel correcto · 1 por debajo · 1 por encima. 2 requieren ajuste.", AjusteVolumenSeleccion.TextoResumen(10, 8, 1, 1, 2));
        Assert.Equal("", AjusteVolumenSeleccion.TextoResumen(0, 0, 0, 0, 0));

        Assert.Equal("Comprobando el pico real de 1 grabación que se aumentaría…", AjusteVolumenSeleccion.TextoComprobandoPicoReal(1));
        Assert.Equal("Comprobando el pico real de 7 grabaciones que se aumentarían…", AjusteVolumenSeleccion.TextoComprobandoPicoReal(7));

        Assert.Equal("", AjusteVolumenSeleccion.TextoPicoRealPendiente(0));
        Assert.Equal("1 grabación que se aumentaría aún no tiene el pico real medido: pulsa Analizar para medirlo antes de ajustar.",
                     AjusteVolumenSeleccion.TextoPicoRealPendiente(1));
        Assert.StartsWith("5 grabaciones que se aumentarían aún no tienen el pico real medido", AjusteVolumenSeleccion.TextoPicoRealPendiente(5));

        Assert.Equal("", AjusteVolumenSeleccion.TextoSinMargen(0));
        Assert.Equal(" · 1 sin cambios: su pico real no deja margen para aumentarla", AjusteVolumenSeleccion.TextoSinMargen(1));
        Assert.EndsWith("margen para aumentarlas", AjusteVolumenSeleccion.TextoSinMargen(3));

        Assert.StartsWith("1 grabación mono se ha vuelto a medir", AjusteVolumenSeleccion.TextoMonoMedidas(1));
        Assert.StartsWith("4 grabaciones mono se han vuelto a medir", AjusteVolumenSeleccion.TextoMonoMedidas(4));
    }

    // ------------------------------------------------------------------ se guardan

    [Fact]
    public void Las_exclusiones_se_guardan_en_la_configuracion()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var cfg = new AppConfig
            {
                VolumenCarpetasExcluidas = { Acapellas },
                VolumenCancionesExcluidas = { Path.Combine(Reggaeton, "intro.mp3") },
            };
            Assert.True(cfg.Save(paths, out var error), error);

            var leida = AppConfig.Load(paths);
            Assert.Equal(new[] { Acapellas }, leida.VolumenCarpetasExcluidas);
            Assert.Equal(new[] { Path.Combine(Reggaeton, "intro.mp3") }, leida.VolumenCancionesExcluidas);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
