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

    // Marcar deja el recálculo para cuando se vuelva a mostrar, sin hacerlo ahora.
    [Fact]
    public void Marcar_deja_el_recalculo_para_la_proxima_vez()
    {
        var veces = 0;
        var r = new RecalculoDiferido(() => veces++);
        r.CambiarVisible(true);
        r.Marcar();
        Assert.Equal(1, veces);
        r.CambiarVisible(false);
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

    [Fact]
    public async Task El_indice_se_construye_fuera_del_hilo_que_lo_pide()
    {
        var hiloQuePide = Environment.CurrentManagedThreadId;
        var hiloQueConstruye = -1;
        var indice = new IndiceReutilizable<object>();
        await indice.ObtenerAsync(() => { hiloQueConstruye = Environment.CurrentManagedThreadId; return new object(); });
        Assert.NotEqual(hiloQuePide, hiloQueConstruye);
    }

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
