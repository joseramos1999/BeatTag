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
    //
    // Configurar «Música» y «Música/House» a la vez metía cada canción de House DOS veces en la
    // biblioteca. No era cosmético: falseaba el recuento y las estadísticas, inventaba duplicados
    // donde había un solo archivo, y cualquier operación por lote tocaba el mismo archivo dos veces.

    // Se corta al elegir la carpeta, que es donde se puede explicar por qué.
    [Fact]
    public void No_se_puede_anadir_una_carpeta_que_ya_esta_dentro_de_otra()
    {
        var store = Montar(_raiz);

        var alta = store.AddFolder(_a);

        Assert.False(alta.Anadida);
        Assert.NotEqual("", alta.Aviso);          // y se dice por qué: si no, parece que no hizo nada
        Assert.Single(store.Folders);
    }

    // Al revés: la nueva engloba a las que ya estaban, y esas dejan de hacer falta por separado.
    [Fact]
    public void Anadir_la_carpeta_de_fuera_absorbe_las_de_dentro()
    {
        var store = Montar(_a, _b);

        var alta = store.AddFolder(_raiz);

        Assert.True(alta.Anadida);
        Assert.NotEqual("", alta.Aviso);
        Assert.Single(store.Folders);
        Assert.Equal(_raiz, store.Folders[0].Path);
    }

    // Salvo que una de las de dentro esté DESMARCADA: absorberla la volvería a incluir sin decir
    // nada, y desmarcar es justo la forma que tiene el usuario de dejar música fuera del análisis.
    [Fact]
    public void No_absorbe_una_carpeta_desmarcada_a_su_espalda()
    {
        var store = Montar(_a, _b);
        store.Folders.First(f => f.Path == _a).Enabled = false;

        var alta = store.AddFolder(_raiz);

        Assert.False(alta.Anadida);
        Assert.Equal(2, store.Folders.Count);
        Assert.False(store.Folders.First(f => f.Path == _a).Enabled);   // sigue fuera
    }

    // Y si una configuración guardada por una versión anterior ya trae el solape, el escaneo se
    // defiende solo: cada archivo entra una vez, y por la carpeta de FUERA, que es la que lo
    // seguirá cubriendo si se quita la de dentro.
    [Fact]
    public async Task Un_solape_ya_guardado_no_duplica_canciones_al_escanear()
    {
        var (store, _) = await Escaneada(_raiz, _a);

        Assert.Equal(3, store.Tracks.Count);                            // tres archivos, tres filas
        Assert.Equal(3, store.Tracks.Select(t => t.FilePath).Distinct().Count());
        Assert.All(store.Tracks, t => Assert.Equal(_raiz, t.Folder));   // la de fuera es la dueña
    }

    // Por eso quitar la de dentro no se lleva nada: la de fuera sigue configurada y las cubre.
    [Fact]
    public async Task Quitar_la_carpeta_de_dentro_no_deja_huerfana_la_musica()
    {
        var (store, _) = await Escaneada(_raiz, _a);

        store.RemoveFolder(_a);

        Assert.Equal(3, store.Tracks.Count);
        Assert.All(store.Tracks, t => Assert.Equal(_raiz, t.Folder));
    }

    // Comparar rutas «a pelo» haría que «Musica2» pareciera estar dentro de «Musica».
    // Las barras se escriben «/» y se traducen a la del sistema: esto tiene que valer igual en
    // Windows y en macOS, y con la barra escrita a mano no valdría en los dos.
    [Theory]
    [InlineData("Musica/House", "Musica", true)]
    [InlineData("Musica/House/Deep", "Musica", true)]
    [InlineData("Musica2", "Musica", false)]        // el caso que rompe la comparación ingenua
    [InlineData("Musica", "Musica", false)]         // la misma no está «dentro» de sí misma
    [InlineData("Musica", "Musica/House", false)]   // la de fuera no está dentro de la de dentro
    [InlineData("Otra", "Musica", false)]
    public void Una_ruta_esta_dentro_de_otra_solo_si_cuelga_de_ella(string ruta, string raiz, bool dentro)
        => Assert.Equal(dentro, LibraryStore.EstaDentroDe(DelSistema(ruta), DelSistema(raiz)));

    private static string DelSistema(string conBarras)
        => Path.Combine(_baseRutas, conBarras.Replace('/', Path.DirectorySeparatorChar));

    private static readonly string _baseRutas = Path.Combine(Path.GetTempPath(), "beattag-rutas");

    // --- Ninguna carpeta marcada ---

    // Desmarcarlas todas deja la biblioteca igual de vacía que no tener ninguna. Darla por
    // escaneada abría la aplicación entera sobre una lista sin canciones, en vez de pedir que se
    // marcara alguna.
    [Fact]
    public async Task Desmarcarlas_todas_deja_la_biblioteca_sin_preparar()
    {
        var (store, _) = await Escaneada(_a, _b);

        foreach (var f in store.Folders.ToList()) f.Enabled = false;

        Assert.Empty(store.Tracks);
        Assert.Empty(store.EnabledPaths());
    }

    // Y escanear sin ninguna marcada no puede declarar la biblioteca lista.
    [Fact]
    public async Task Escanear_sin_ninguna_carpeta_marcada_no_la_da_por_lista()
    {
        var store = Montar(_a, _b);
        foreach (var f in store.Folders) f.Enabled = false;

        await store.ScanAsync();

        Assert.False(store.IsScanned);
        Assert.Empty(store.Tracks);
    }
}
