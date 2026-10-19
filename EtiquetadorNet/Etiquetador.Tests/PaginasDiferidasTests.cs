using Avalonia.Collections;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Las páginas que no están a la vista no se rehacen en cada cambio de la biblioteca (Ficha DJ,
/// Colecciones, Estadísticas, Bandeja), y lo escrito en el panel de ficha no se pierde al cambiar de
/// selección o de página.
/// </summary>
public class PaginasDiferidasTests
{
    // --- X7: recalcular solo a la vista ---

    // El caso real: cada tecla de energía en Ficha DJ avisa de que las fichas cambiaron, y Colecciones,
    // oculta, recalculaba la colección abierta cada vez. Ahora una sola vez, al volver.
    [Fact]
    public void Oculta_no_recalcula_y_al_mostrarse_recalcula_una_sola_vez()
    {
        var veces = 0;
        var r = new RecalculoDiferido(() => veces++);
        r.CambiarVisible(true);
        Assert.Equal(1, veces);          // la primera vez que se muestra, siempre
        r.CambiarVisible(false);

        for (var i = 0; i < 7; i++) r.Pedir();   // 7, 8, 7… pulsadas en otra página
        Assert.Equal(1, veces);
        Assert.True(r.Pendiente);

        r.CambiarVisible(true);
        Assert.Equal(2, veces);
        Assert.False(r.Pendiente);

        r.CambiarVisible(true);          // ya visible: nada nuevo
        Assert.Equal(2, veces);
    }

    [Fact]
    public void A_la_vista_recalcula_al_momento()
    {
        var veces = 0;
        var r = new RecalculoDiferido(() => veces++);
        r.CambiarVisible(true);
        r.Pedir();
        r.Pedir();
        Assert.Equal(3, veces);
    }

    [Fact]
    public void Sin_cambios_mientras_estaba_oculta_no_recalcula_al_volver()
    {
        var veces = 0;
        var r = new RecalculoDiferido(() => veces++);
        r.CambiarVisible(true);
        r.CambiarVisible(false);
        r.CambiarVisible(true);
        Assert.Equal(1, veces);
    }

    // El caso de la revisión: con Ficha DJ oculta se aceptan fichas en el Asistente. Al volver no se
    // rehace la tabla entera (15.000 filas, vista nueva, selección perdida): solo se releen las fichas.
    [Fact]
    public void Un_cambio_de_fichas_oculta_solo_relee_al_volver()
    {
        var recalculos = 0;
        var refrescos = 0;
        var r = new RecalculoDiferido(() => recalculos++, () => refrescos++);
        r.CambiarVisible(true);
        r.CambiarVisible(false);

        r.PedirRefresco();
        r.PedirRefresco();   // dos aceptaciones seguidas en el Asistente
        Assert.True(r.RefrescoPendiente);
        Assert.False(r.Pendiente);

        r.CambiarVisible(true);
        Assert.Equal(1, recalculos);   // solo el de la primera vez
        Assert.Equal(1, refrescos);
        Assert.False(r.RefrescoPendiente);

        r.CambiarVisible(false);
        r.CambiarVisible(true);
        Assert.Equal(1, refrescos);    // nada pendiente: nada que hacer
    }

    [Fact]
    public void A_la_vista_un_cambio_de_fichas_relee_al_momento()
    {
        var recalculos = 0;
        var refrescos = 0;
        var r = new RecalculoDiferido(() => recalculos++, () => refrescos++);
        r.CambiarVisible(true);
        r.PedirRefresco();
        Assert.Equal(1, recalculos);
        Assert.Equal(1, refrescos);
    }

    // Si además cambió la biblioteca, el recálculo entero ya relee las fichas: el retoque sobra.
    [Fact]
    public void Si_tambien_cambio_la_biblioteca_basta_con_el_recalculo()
    {
        var recalculos = 0;
        var refrescos = 0;
        var r = new RecalculoDiferido(() => recalculos++, () => refrescos++);
        r.CambiarVisible(true);
        r.CambiarVisible(false);

        r.PedirRefresco();
        r.Pedir();
        r.PedirRefresco();
        r.CambiarVisible(true);

        Assert.Equal(2, recalculos);
        Assert.Equal(0, refrescos);
        Assert.False(r.RefrescoPendiente);
    }

    // Antes de mostrarse la primera vez no hay filas que releer: lo hará el primer recálculo.
    [Fact]
    public void Antes_de_mostrarse_la_primera_vez_el_refresco_no_se_apunta()
    {
        var refrescos = 0;
        var r = new RecalculoDiferido(() => { }, () => refrescos++);
        r.PedirRefresco();
        Assert.False(r.RefrescoPendiente);
        r.CambiarVisible(true);
        Assert.Equal(0, refrescos);
    }

    // Sin retoque ligero (Colecciones, Estadísticas), pedirlo rehace la página como siempre.
    [Fact]
    public void Sin_retoque_ligero_el_refresco_es_un_recalculo()
    {
        var veces = 0;
        var r = new RecalculoDiferido(() => veces++);
        r.CambiarVisible(true);
        r.CambiarVisible(false);
        r.PedirRefresco();
        Assert.True(r.Pendiente);
        r.CambiarVisible(true);
        Assert.Equal(2, veces);
    }

    // Sustituir la vista de la tabla no puede perder el orden ni el filtro que eligió el usuario.
    [Fact]
    public void La_vista_nueva_conserva_el_orden_y_el_filtro()
    {
        var antes = new DataGridCollectionView(new List<string> { "b", "a" });
        antes.SortDescriptions.Add(DataGridSortDescription.FromPath("Length"));
        Func<object, bool> filtro = o => (string)o != "z";
        antes.Filter = filtro;

        var despues = VistaTabla.Nueva(new List<string> { "c", "z" }, antes);

        Assert.Single(despues.SortDescriptions);
        Assert.Same(filtro, despues.Filter);
    }

    // --- I10: el índice de la biblioteca de la bandeja, fuera del hilo de la interfaz y reutilizado ---

    [Fact]
    public async Task El_indice_se_reutiliza_hasta_que_se_invalida()
    {
        var indice = new IndiceReutilizable<object>();
        var a = await indice.ObtenerAsync(() => new object());
        var b = await indice.ObtenerAsync(() => new object());   // otro cambio de destino, otra revisión
        Assert.Same(a, b);
        Assert.Equal(1, indice.Construcciones);

        indice.Invalidar();   // la biblioteca ha cambiado
        var c = await indice.ObtenerAsync(() => new object());
        Assert.NotSame(a, c);
        Assert.Equal(2, indice.Construcciones);
    }

    // Lo que importa es que pedir el índice no bloquee a quien lo pide (el hilo de la interfaz):
    // ObtenerAsync tiene que volver ANTES de construir. Antes se comparaba el id del hilo que pide con
    // el del que construye, y fallaba de vez en cuando (5 de 6 ejecuciones en la revisión): xUnit
    // corre las pruebas en hilos del grupo, y al hacer el await ese mismo hilo podía recoger el Task.Run.
    [Fact]
    public async Task Pedir_el_indice_no_espera_a_que_se_construya()
    {
        using var puerta = new ManualResetEventSlim();
        var indice = new IndiceReutilizable<object>();
        Assert.False(indice.Listo);

        // Si se construyera en el hilo que lo pide, ObtenerAsync se quedaría aquí hasta que la espera
        // caducase, y la tarea volvería ya terminada.
        var tarea = indice.ObtenerAsync(() => { puerta.Wait(TimeSpan.FromSeconds(10)); return new object(); });
        Assert.False(tarea.IsCompleted);
        Assert.False(indice.Listo);   // la Bandeja dice «Revisando…» mientras tanto

        puerta.Set();
        Assert.NotNull(await tarea);
        Assert.True(indice.Listo);

        indice.Invalidar();
        Assert.False(indice.Listo);
    }

    // --- I10 (revisión): una revisión que nadie espera no falla en silencio ---

    [Fact]
    public async Task Un_fallo_en_un_trabajo_sin_esperar_llega_a_quien_lo_cuenta()
    {
        Exception? contada = null;
        await SinEsperar.Lanzar(() => Task.FromException(new InvalidOperationException("índice roto")), e => contada = e);
        Assert.IsType<InvalidOperationException>(contada);
        Assert.Equal("índice roto", contada!.Message);
    }

    [Fact]
    public async Task Cancelar_un_trabajo_sin_esperar_no_cuenta_como_fallo()
    {
        Exception? contada = null;
        await SinEsperar.Lanzar(() => Task.FromCanceled(new CancellationToken(true)), e => contada = e);
        await SinEsperar.Lanzar(() => Task.CompletedTask, e => contada = e);
        Assert.Null(contada);
    }

    [Theory]
    [InlineData(1, 0, "Bandeja revisada contra la biblioteca: la canción no tiene avisos.")]
    [InlineData(1, 1, "Bandeja revisada contra la biblioteca: la canción tiene avisos.")]
    [InlineData(12, 0, "Bandeja revisada contra la biblioteca: ninguna de las 12 canciones tiene avisos.")]
    [InlineData(12, 3, "Bandeja revisada contra la biblioteca: 3 de 12 canciones con avisos.")]
    public void Tras_revisar_se_dice_como_ha_quedado(int canciones, int conAvisos, string esperado)
        => Assert.Equal(esperado, BandejaViewModel.TextoRevisada(canciones, conAvisos));

    // Dos revisiones seguidas mientras se construye esperan a la misma construcción.
    [Fact]
    public async Task Dos_peticiones_a_la_vez_comparten_la_construccion()
    {
        var puerta = new TaskCompletionSource();
        var indice = new IndiceReutilizable<object>();
        var una = indice.ObtenerAsync(() => { puerta.Task.Wait(); return new object(); });
        var otra = indice.ObtenerAsync(() => new object());
        puerta.SetResult();
        Assert.Same(await una, await otra);
        Assert.Equal(1, indice.Construcciones);
    }

    [Fact]
    public async Task Si_la_construccion_falla_la_siguiente_lo_reintenta()
    {
        var indice = new IndiceReutilizable<object>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => indice.ObtenerAsync(() => throw new InvalidOperationException()));
        Assert.NotNull(await indice.ObtenerAsync(() => new object()));
        Assert.Equal(2, indice.Construcciones);
    }

    // --- X12: lo escrito en el panel de ficha no se pierde ---

    // El caso real: «boda, público mayor, cierre» en Etiquetas de 30 temas, y clic en otra fila. La
    // página pide al editor que guarde lo pendiente antes de cargar la nueva selección.
    [Fact]
    public void Lo_escrito_sin_guardar_se_guarda_antes_de_cambiar()
    {
        var editor = new FichaEditorViewModel();
        var guardados = 0;
        editor.GuardarPedido += () => guardados++;
        editor.Cargar(Enumerable.Range(0, 30).Select(_ => (FichaDj?)new FichaDj { Energia = 7 }).ToList());

        editor.Etiquetas = "boda, público mayor, cierre";
        Assert.True(editor.HayCambios);

        Assert.True(editor.GuardarSiHayCambios());
        Assert.Equal(1, guardados);
        Assert.Equal(new[] { "boda", "público mayor", "cierre" }, editor.Cambios().EtiquetasAnadir);
    }

    [Fact]
    public void Sin_nada_escrito_no_se_guarda_nada()
    {
        var editor = new FichaEditorViewModel();
        var guardados = 0;
        editor.GuardarPedido += () => guardados++;

        Assert.False(editor.GuardarSiHayCambios());   // sin selección

        editor.Cargar(new List<FichaDj?> { new FichaDj { Energia = 7 } });
        Assert.False(editor.GuardarSiHayCambios());   // con selección, sin tocar nada

        editor.Etiquetas = "boda";
        editor.Etiquetas = "";                         // escrito y borrado: no hay cambio real
        Assert.False(editor.GuardarSiHayCambios());
        Assert.Equal(0, guardados);
    }

    // --- X12 (revisión): lo escrito se guarda en la selección ANTERIOR, no en la nueva ---

    private static FichaRow Fila(string nombre, FichaDj? ficha = null)
        => new(new Etiquetador.Core.Track { FilePath = Rutas.Archivo(nombre), Folder = Rutas.Carpeta }, ficha);

    // El caso real: «boda, público mayor, cierre» en Etiquetas de 30 temas y clic en otra fila. La
    // página guarda en «las seleccionadas» cuando el panel se lo pide; si la selección cambiase antes
    // de guardar, las etiquetas de boda acabarían en la fila del clic.
    [Fact]
    public void Al_cambiar_de_seleccion_lo_escrito_va_a_las_filas_de_antes()
    {
        var editor = new FichaEditorViewModel();
        var sel = new SeleccionConFicha<FichaRow>(editor);
        var boda = Enumerable.Range(1, 30).Select(i => Fila($"Boda {i:00}.mp3", new FichaDj { Energia = 6 })).ToList();
        var otra = Fila("Bad Bunny - Tití Me Preguntó.mp3", new FichaDj { Energia = 9 });

        IReadOnlyList<FichaRow>? guardadoEn = null;
        List<string>? etiquetasGuardadas = null;
        editor.GuardarPedido += () => { guardadoEn = sel.Filas.ToList(); etiquetasGuardadas = editor.Cambios().EtiquetasAnadir?.ToList(); };

        sel.Cambiar(boda);
        editor.Etiquetas = "boda, público mayor, cierre";
        var guardada = sel.Cambiar(new[] { otra });

        Assert.True(guardada);
        Assert.Equal(boda, guardadoEn);
        Assert.Equal(new[] { "boda", "público mayor", "cierre" }, etiquetasGuardadas);
        // Y el panel ya enseña la ficha de la fila nueva, sin nada pendiente.
        Assert.Same(otra, Assert.Single(sel.Filas));
        Assert.Equal(1, editor.Cuantas);
        Assert.False(editor.HayCambios);
        Assert.Equal("", editor.Etiquetas);
    }

    [Fact]
    public void Sin_nada_escrito_cambiar_de_seleccion_no_guarda()
    {
        var editor = new FichaEditorViewModel();
        var sel = new SeleccionConFicha<FichaRow>(editor);
        var guardados = 0;
        editor.GuardarPedido += () => guardados++;

        Assert.False(sel.Cambiar(new[] { Fila("Uno.mp3") }));
        Assert.False(sel.Cambiar(new[] { Fila("Dos.mp3"), Fila("Tres.mp3") }));
        Assert.False(sel.Cambiar(Array.Empty<FichaRow>()));
        Assert.Equal(0, guardados);
        Assert.Equal(0, editor.Cuantas);
    }

    // Al cerrar la aplicación (o salir de la página, o rehacer la tabla) se guarda lo pendiente sin
    // cambiar la selección.
    [Fact]
    public void Guardar_lo_escrito_sin_cambiar_de_seleccion()
    {
        var editor = new FichaEditorViewModel();
        var sel = new SeleccionConFicha<FichaRow>(editor);
        var filas = new[] { Fila("Uno.mp3"), Fila("Dos.mp3") };
        IReadOnlyList<FichaRow>? guardadoEn = null;
        editor.GuardarPedido += () => guardadoEn = sel.Filas;

        Assert.False(sel.GuardarLoEscrito());   // sin selección
        sel.Cambiar(filas);
        Assert.False(sel.GuardarLoEscrito());   // nada escrito
        editor.Etiquetas = "cierre";
        Assert.True(sel.GuardarLoEscrito());
        Assert.Equal(filas, guardadoEn);
        Assert.Equal(filas, sel.Filas);
    }

    [Theory]
    [InlineData(1, "Ficha guardada al cerrar la aplicación, para no perder lo escrito.")]
    [InlineData(30, "Ficha guardada en 30 canciones al cambiar de selección, para no perder lo escrito.")]
    public void El_estado_dice_donde_se_guardo(int cuantas, string esperado)
        => Assert.Equal(esperado, FichasViewModel.TextoGuardadoParaNoPerder(
            cuantas, cuantas == 1 ? "al cerrar la aplicación" : "al cambiar de selección"));

    // --- X5: nombre de colección ni vacío ni repetido ---

    [Theory]
    [InlineData("Bachata para cerrar", "Bachata para cerrar")]
    [InlineData("  Pico  ", "Pico 2")]
    [InlineData("pico", "pico 2")]
    [InlineData("", "Nueva colección")]
    [InlineData("   ", "Nueva colección")]
    public void El_nombre_de_una_coleccion_no_queda_vacio_ni_repetido(string propuesto, string esperado)
        => Assert.Equal(esperado, ColeccionesViewModel.NombreUnico(propuesto, new[] { "Pico", "Boda" }));
}
