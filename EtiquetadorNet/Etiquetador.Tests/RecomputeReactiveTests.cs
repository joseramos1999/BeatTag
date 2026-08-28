using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Que la biblioteca cambie por una accion hecha en OTRA pestana no puede poner a trabajar a una
/// pestana cara.
///
/// El sintoma era desconcertante: aplicar cualquier cosa en Enriquecer o en No encontradas te
/// plantaba en Duplicados. La causa, que Duplicados se reagrupaba por huella acustica al reescanear
/// la biblioteca, se marcaba como ocupada, y el bloqueo global -que deshabilita las demas pestanas
/// mientras algo corre- arrastraba al usuario hasta ahi. Ademas de lanzar millones de comparaciones
/// por cada cancion aplicada.
/// </summary>
public class RecomputeReactiveTests
{
    /// <summary>Una pestaña de analisis de mentira, para ver a que se la llama.</summary>
    private sealed class Espia : ScanViewModelBase
    {
        public int Recalculos { get; private set; }
        public int Reactivos { get; private set; }
        private readonly bool _seDesvia;

        public Espia(LibraryStore store, bool seDesvia = false) : base(store) => _seDesvia = seDesvia;

        protected override void Recompute() => Recalculos++;

        protected override void RecomputeReactive()
        {
            Reactivos++;
            if (!_seDesvia) base.RecomputeReactive();   // el comportamiento por defecto
        }
    }

    private static LibraryStore Biblioteca(out string dir)
    {
        dir = Mp3Fixture.NewTempDir();
        var store = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(dir, "scan.json")));
        store.Tracks.Add(new Track { FilePath = @"C:\m\a.mp3", Folder = @"C:\m" });
        return store;
    }

    // Un cambio en la biblioteca llega por la via reactiva, no por la del usuario.
    [Fact]
    public void Un_cambio_en_la_biblioteca_llega_por_la_via_reactiva()
    {
        var store = Biblioteca(out var dir);
        try
        {
            var vm = new Espia(store);
            store.RemoveTrack(@"C:\m\a.mp3");   // como tras aplicar algo en otra pestaña

            Assert.Equal(1, vm.Reactivos);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Por defecto, reaccionar es recalcular: para casi todas las pestañas es barato y no cambia nada.
    [Fact]
    public void Por_defecto_reaccionar_es_recalcular()
    {
        var store = Biblioteca(out var dir);
        try
        {
            var vm = new Espia(store);
            store.RemoveTrack(@"C:\m\a.mp3");

            Assert.Equal(1, vm.Recalculos);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Pero una pestaña cara puede desviarse y NO recalcular. Esta es la salvaguarda: sin ella,
    // Duplicados volvia a agrupar por audio en cada accion del usuario en cualquier otro sitio.
    [Fact]
    public void Una_pestana_cara_puede_no_recalcular()
    {
        var store = Biblioteca(out var dir);
        try
        {
            var vm = new Espia(store, seDesvia: true);
            store.RemoveTrack(@"C:\m\a.mp3");

            Assert.Equal(1, vm.Reactivos);
            Assert.Equal(0, vm.Recalculos);   // no se ha puesto a trabajar
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
