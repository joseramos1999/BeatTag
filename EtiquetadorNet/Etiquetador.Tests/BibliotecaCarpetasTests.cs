using Etiquetador.App.Services;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// La lista de canciones tiene que ir a la par con la lista de carpetas.
///
/// No es cosmética. TODAS las pestañas trabajan sobre esta misma lista en memoria, no sobre las
/// carpetas configuradas: mientras una canción siga aquí, se sigue analizando, midiendo y
/// comprobando. Quitar una carpeta y que sus archivos siguieran ahí significaba tocar archivos de
/// una carpeta que el usuario había retirado a propósito.
/// </summary>
public class BibliotecaCarpetasTests : IDisposable
{
    private readonly string _raiz;
    private readonly string _a;
    private readonly string _b;

    public BibliotecaCarpetasTests()
    {
        _raiz = Mp3Fixture.NewTempDir();
        _a = Path.Combine(_raiz, "A");
        _b = Path.Combine(_raiz, "B");
        Directory.CreateDirectory(_a);
        Directory.CreateDirectory(_b);
        Mp3Fixture.WriteMinMp3(Path.Combine(_a, "a1.mp3"));
        Mp3Fixture.WriteMinMp3(Path.Combine(_a, "a2.mp3"));
        Mp3Fixture.WriteMinMp3(Path.Combine(_b, "b1.mp3"));
    }

    public void Dispose() { try { Directory.Delete(_raiz, true); } catch { } }

    private LibraryStore Montar(params string[] carpetas)
    {
        var config = new AppConfig { Folders = carpetas.ToList() };
        return new LibraryStore(config, () => { }, new ScanCache(Path.Combine(_raiz, "scan.json")));
    }

    private async Task<(LibraryStore Store, List<int> Avisos)> Escaneada(params string[] carpetas)
    {
        var store = Montar(carpetas);
        await store.ScanAsync();

        // Cuántas veces avisa: las pestañas se recalculan con cada aviso, y avisar de más también
        // cuesta. Se cuenta a partir de aquí, ya escaneado.
        var avisos = new List<int>();
        store.Changed += () => avisos.Add(store.Tracks.Count);
        return (store, avisos);
    }

    // --- Quitar una carpeta ---

    [Fact]
    public async Task Quitar_una_carpeta_saca_sus_canciones_de_la_lista()
    {
        var (store, avisos) = await Escaneada(_a, _b);
        Assert.Equal(3, store.Tracks.Count);

        store.RemoveFolder(_a);

        Assert.Single(store.Tracks);
        Assert.All(store.Tracks, t => Assert.Equal(_b, t.Folder));
        Assert.Single(avisos);   // un solo aviso, no uno por canción
    }

    // Quitar una carpeta no puede llevarse por delante las canciones de las demás.
    [Fact]
    public async Task Quitar_una_carpeta_no_toca_las_otras()
    {
        var (store, _) = await Escaneada(_a, _b);

        store.RemoveFolder(_b);

        Assert.Equal(2, store.Tracks.Count);
        Assert.All(store.Tracks, t => Assert.Equal(_a, t.Folder));
        Assert.True(store.IsScanned);   // lo que queda sigue estando al día: no hace falta reescanear
    }

    // --- Vaciar ---

    [Fact]
    public async Task Vaciar_las_carpetas_deja_la_lista_vacia()
    {
        var (store, avisos) = await Escaneada(_a, _b);

        store.ClearFolders();

        Assert.Empty(store.Folders);
        Assert.Empty(store.Tracks);
        Assert.False(store.IsScanned);   // ya no hay nada escaneado
        Assert.Single(avisos);
    }

    // Vaciar cuando no hay nada que vaciar no debe hacer trabajar a media aplicación.
    [Fact]
    public async Task Vaciar_sin_carpetas_no_avisa_a_nadie()
    {
        var (store, avisos) = await Escaneada();

        store.ClearFolders();

        Assert.Empty(avisos);
    }

    // --- La casilla de marcar/desmarcar ---

    // La casilla promete excluir la carpeta del análisis. Antes solo guardaba la preferencia, así
    // que hasta el siguiente escaneo la carpeta seguía analizándose igual.
    [Fact]
    public async Task Desmarcar_una_carpeta_saca_sus_canciones_ya()
    {
        var (store, _) = await Escaneada(_a, _b);

        store.Folders.First(f => f.Path == _a).Enabled = false;

        Assert.Single(store.Tracks);
        Assert.All(store.Tracks, t => Assert.Equal(_b, t.Folder));
    }

    // Volver a marcarla no puede devolver las canciones por arte de magia: no están cargadas. Se
    // declara la biblioteca sin escanear, que es el estado que ya hace que la aplicación pida
    // pulsar Escanear, en vez de ponerse a leer disco sin que nadie lo haya pedido.
    [Fact]
    public async Task Volver_a_marcarla_pide_escanear_otra_vez()
    {
        var (store, _) = await Escaneada(_a, _b);
        var carpeta = store.Folders.First(f => f.Path == _a);
        carpeta.Enabled = false;

        carpeta.Enabled = true;

        Assert.False(store.IsScanned);
    }

    // --- Añadir ---

    // Añadir una carpeta deja la biblioteca corta igual que volver a marcar una: sus canciones aún
    // no están, y el resto de pestañas no puede seguir creyendo que la lista está completa.
    [Fact]
    public async Task Anadir_una_carpeta_pide_escanear()
    {
        var (store, _) = await Escaneada(_a);
        Assert.True(store.IsScanned);

        store.AddFolder(_b);

        Assert.False(store.IsScanned);
        Assert.Equal(2, store.Folders.Count);
    }

    // --- Carpetas anidadas ---

    // Con una carpeta dentro de otra, cada canción entra una vez por cada raíz configurada. Quitar
    // la de dentro debe dejar la copia que entró por la de fuera: esa carpeta sigue configurada.
    [Fact]
    public async Task Quitar_la_carpeta_de_dentro_conserva_lo_que_entro_por_la_de_fuera()
    {
        var (store, _) = await Escaneada(_raiz, _a);
        var porLaDeFuera = store.Tracks.Count(t => t.Folder == _raiz);
        Assert.True(porLaDeFuera > 0, "la raíz tiene que haber recogido los archivos de dentro");

        store.RemoveFolder(_a);

        Assert.Equal(porLaDeFuera, store.Tracks.Count);
        Assert.All(store.Tracks, t => Assert.Equal(_raiz, t.Folder));
    }
}
