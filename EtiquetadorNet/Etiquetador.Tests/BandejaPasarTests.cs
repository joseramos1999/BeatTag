using System.Reflection;
using System.Text.Json;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// «Pasar a la biblioteca» de la Bandeja, ya por el motor por lotes de Organizar por género, con
/// archivos de verdad en una carpeta temporal. Antes anotaba cada traslado DESPUÉS de moverlo, seguía
/// moviendo aunque no pudiera anotar y no se podía cancelar: con el proceso cortado a mitad, 7 de 18
/// cortes dejaron archivos movidos sin anotar. Aquí se prueba que cada archivo está anotado en el disco
/// antes de moverse, que un corte en cualquier punto se deshace entero, que se cancela entre un archivo
/// y el siguiente y que sin historial no se mueve nada. Sin procesos hijo: el vigilante de la CI corta a
/// 400, así que el corte se simula congelando el disco en ese instante.
/// </summary>
public class BandejaPasarTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;
    private readonly string _bandeja;
    private readonly string _musica;

    public BandejaPasarTests()
    {
        _paths = new AppPaths(Path.Combine(_dir, "datos"));
        _bandeja = Directory.CreateDirectory(Path.Combine(_dir, "Bandeja")).FullName;
        _musica = Directory.CreateDirectory(Path.Combine(_dir, "Musica", "Nuevas")).FullName;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary><paramref name="n"/> descargas en la bandeja (basta con unos bytes: solo se mueven).</summary>
    private List<string> Descargas(int n)
    {
        var lista = new List<string>();
        for (var i = 1; i <= n; i++)
        {
            var ruta = Path.Combine(_bandeja, $"tema{i:0000}.mp3");
            File.WriteAllBytes(ruta, new byte[] { 1, 2, 3, (byte)(i % 256) });
            lista.Add(ruta);
        }
        return lista;
    }

    private string EnLaBiblioteca(string descarga) => Path.Combine(_musica, Path.GetFileName(descarga));

    private ResultadoTraslado Pasar(IReadOnlyList<string> rutas, CancellationToken ct = default,
                                    Action<string, string, SelloArchivo>? seguir = null, Action<string, string>? mover = null)
    {
        using var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(), out var error);
        return AppEngine.PasarABiblioteca(rutas, _musica, m, error, seguir, null, null, ct, mover);
    }

    private UndoRecord[] Anotados()
        => Directory.GetFiles(_paths.UndoDir, "run_*.jsonl").Where(UndoEngine.EsManifiestoPendiente)
                    .SelectMany(File.ReadAllLines).Where(l => l.Trim().Length > 0)
                    .Select(l => JsonSerializer.Deserialize<UndoRecord>(l)!).ToArray();

    /// <summary>Las líneas enteras del manifiesto que hay ahora en el disco, aunque el historial lo tenga abierto.</summary>
    private static List<UndoRecord> AnotadosEnElDisco(byte[] bytes)
    {
        var res = new List<UndoRecord>();
        foreach (var linea in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            if (linea.Trim().Length == 0) continue;
            try { res.Add(JsonSerializer.Deserialize<UndoRecord>(linea)!); }
            catch (JsonException) { /* una línea a medias: deshacer la aparta sin tocar nada */ }
        }
        return res;
    }

    private static byte[] LeerSinCerrojo(string ruta)
    {
        using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    // --- Anotar antes de mover ---

    [Fact]
    public void Cada_descarga_esta_anotada_en_el_disco_antes_de_moverse()
    {
        var descargas = Descargas(250);   // tres lotes
        var ruta = _paths.NuevoManifiesto();
        var sinAnotar = new List<string>();
        ResultadoTraslado res;
        using (var m = ManifiestoDeshacer.Abrir(ruta, out var error))
            res = AppEngine.PasarABiblioteca(descargas, _musica, m, error, null, null, null, CancellationToken.None, mover: (o, d) =>
            {
                if (!AnotadosEnElDisco(LeerSinCerrojo(ruta)).Any(r => r.OrigPath == o && r.FinalPath == d)) sinAnotar.Add(o);
                File.Move(o, d, overwrite: false);
            });

        Assert.Equal(250, res.Movidas);
        Assert.Empty(sinAnotar);
        Assert.Equal(250, Anotados().Length);   // al terminar, el historial es exacto
    }

    // El corte del proceso, simulado: en el instante elegido se copia el manifiesto tal como está en el
    // disco; al terminar, las descargas movidas después de ese instante vuelven a la bandeja y el
    // manifiesto vuelve a ser la copia. Es lo que habría en el disco si el proceso hubiera muerto ahí.
    [Theory]
    [InlineData(1, true)]      // el primer lote anotado y nada movido
    [InlineData(37, false)]    // a mitad del primer lote
    [InlineData(100, false)]   // el primer lote movido, sin reescribir aún
    [InlineData(101, true)]    // el primer lote reescrito y el segundo anotado, sin mover nada de él
    [InlineData(163, false)]   // a mitad del segundo lote
    [InlineData(250, false)]   // la última, sin reescribir su lote
    public void Un_corte_en_cualquier_punto_deja_lo_movido_anotado_y_se_deshace_entero(int corte, bool antesDeMover)
    {
        var descargas = Descargas(250);
        var ruta = _paths.NuevoManifiesto();
        byte[]? enElCorte = null;
        var llamadas = 0;
        using (var m = ManifiestoDeshacer.Abrir(ruta, out var error))
            AppEngine.PasarABiblioteca(descargas, _musica, m, error, null, null, null, CancellationToken.None, mover: (o, d) =>
            {
                var esta = ++llamadas == corte;
                if (esta && antesDeMover) enElCorte = LeerSinCerrojo(ruta);
                File.Move(o, d, overwrite: false);
                if (esta && !antesDeMover) enElCorte = LeerSinCerrojo(ruta);
            });
        Assert.NotNull(enElCorte);

        var movidas = antesDeMover ? corte - 1 : corte;
        foreach (var d in descargas.Skip(movidas)) File.Move(EnLaBiblioteca(d), d);
        File.WriteAllBytes(ruta, enElCorte!);

        var anotados = AnotadosEnElDisco(enElCorte!);
        Assert.All(descargas.Take(movidas), d => Assert.Contains(anotados, r => r.OrigPath == d && r.FinalPath == EnLaBiblioteca(d)));

        var deshecho = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal(0, deshecho.Errors + deshecho.Missing);
        Assert.All(descargas, d => Assert.True(File.Exists(d), $"«{Path.GetFileName(d)}» no ha vuelto a la bandeja"));
        Assert.Empty(Directory.GetFiles(_musica));
    }

    // --- Cancelar ---

    [Fact]
    public void Cancelar_a_mitad_deja_lo_pasado_anotado_y_el_resto_en_la_bandeja()
    {
        var descargas = Descargas(150);
        using var cts = new CancellationTokenSource();
        var movidas = 0;

        var res = Pasar(descargas, cts.Token, seguir: (_, _, _) => { if (++movidas == 120) cts.Cancel(); });

        Assert.True(res.Cancelado);
        Assert.Equal("", res.Detenido);
        Assert.Equal(120, res.Movidas);
        Assert.All(descargas.Skip(120), d => Assert.True(File.Exists(d)));
        Assert.Equal(120, Anotados().Length);
        Assert.Equal("Cancelado. 120 canciones pasadas a la biblioteca; las demás siguen en la bandeja. Se puede deshacer desde Enriquecer.",
                     BandejaViewModel.TextoPasadas(res));

        var deshecho = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal(120, deshecho.Reverted);
        Assert.All(descargas, d => Assert.True(File.Exists(d)));
    }

    [Fact]
    public void Cancelado_antes_de_empezar_no_se_mueve_nada_ni_queda_historial()
    {
        var descargas = Descargas(3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var res = Pasar(descargas, cts.Token);

        Assert.True(res.Cancelado);
        Assert.Equal(0, res.Movidas);
        Assert.All(descargas, d => Assert.True(File.Exists(d)));
        Assert.Empty(Directory.GetFiles(_paths.UndoDir, "run_*.jsonl"));   // vacío: no tapa a la anterior en «Deshacer última»
    }

    // --- Sin historial no se mueve nada ---

    [Fact]
    public void Sin_historial_no_se_mueve_nada_y_se_dice()
    {
        var descargas = Descargas(3);

        var res = AppEngine.PasarABiblioteca(descargas, _musica, null, "Acceso denegado.", null, null, null, CancellationToken.None);

        Assert.Equal(0, res.Movidas);
        Assert.StartsWith("No se ha tocado ningún archivo", res.Detenido);
        Assert.Contains("Acceso denegado", res.Detenido);
        Assert.Equal(res.Detenido, BandejaViewModel.TextoPasadas(res));
        Assert.All(descargas, d => Assert.True(File.Exists(d)));
    }

    /// <summary>Un historial por lotes que deja de poder escribirse en el lote número <c>fallaEn</c>.</summary>
    private sealed class HistorialQueFalla(int fallaEn) : IHistorialPorLotes
    {
        private int _lotes;
        public readonly List<UndoRecord> Anotados = new();
        public long Posicion => Anotados.Count;
        public string Anotar(UndoRecord registro) => AnotarLote(new[] { registro });

        public string AnotarLote(IReadOnlyList<UndoRecord> registros)
        {
            if (++_lotes >= fallaEn) return "No hay espacio suficiente en el disco.";
            Anotados.AddRange(registros);
            return "";
        }

        public string ReescribirDesde(long posicion, IReadOnlyList<UndoRecord> hechos)
        {
            Anotados.RemoveRange((int)posicion, Anotados.Count - (int)posicion);
            Anotados.AddRange(hechos);
            return "";
        }
    }

    [Fact]
    public void Si_un_lote_no_se_puede_anotar_no_se_mueve_nada_de_el()
    {
        var descargas = Descargas(250);
        var historial = new HistorialQueFalla(fallaEn: 2);

        var res = AppEngine.PasarABiblioteca(descargas, _musica, historial, "", null, null, null, CancellationToken.None);

        Assert.Equal(TrasladoPorLotes.TamanoLote, res.Movidas);   // solo el primer lote, que sí se anotó
        Assert.True(res.Cancelado);
        Assert.Contains("tema0101.mp3", res.Detenido);
        Assert.Equal(TrasladoPorLotes.TamanoLote, historial.Anotados.Count);
        Assert.All(descargas.Skip(TrasladoPorLotes.TamanoLote), d => Assert.True(File.Exists(d)));
        Assert.Contains(res.Detenido, BandejaViewModel.TextoPasadas(res));
    }

    [Fact]
    public void Si_la_carpeta_de_datos_no_deja_crear_el_historial_no_se_mueve_nada()
    {
        var descargas = Descargas(2);
        Directory.CreateDirectory(_paths.DataDir);
        File.WriteAllText(_paths.UndoDir, "");   // un archivo donde tendría que ir la carpeta del historial
        var motor = MotorDePrueba.Crear(_paths.DataDir, new Logger());

        var res = motor.PasarABiblioteca(descargas, _musica, null, CancellationToken.None);

        Assert.Equal(0, res.Movidas);
        Assert.StartsWith("No se ha tocado ningún archivo", res.Detenido);
        Assert.All(descargas, d => Assert.True(File.Exists(d)));
        Assert.Empty(Directory.GetFiles(_musica));
    }

    [Fact]
    public void Sin_la_carpeta_de_destino_no_se_mueve_nada_ni_se_crea()
    {
        var descargas = Descargas(2);
        var otra = Path.Combine(_dir, "Musica", "Borrada");

        ResultadoTraslado res;
        using (var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(), out var error))
            res = AppEngine.PasarABiblioteca(descargas, otra, m, error, null, null, null, CancellationToken.None);

        Assert.Equal(0, res.Movidas);
        Assert.Equal(AppEngine.SinCarpetaDeDestino, BandejaViewModel.TextoPasadas(res));
        Assert.False(Directory.Exists(otra));
        Assert.All(descargas, d => Assert.True(File.Exists(d)));
        Assert.Empty(Directory.GetFiles(_paths.UndoDir, "run_*.jsonl"));
    }

    // --- Lo de siempre: nunca sobrescribe y el historial lo llama «Traslado de carpeta» ---

    [Fact]
    public void Dos_descargas_con_el_mismo_nombre_pasa_la_primera_y_la_otra_se_queda()
    {
        var a = Path.Combine(_bandeja, "Pack A", "Tema.mp3");
        var b = Path.Combine(_bandeja, "Pack B", "Tema.mp3");
        foreach (var r in new[] { a, b })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r)!);
            File.WriteAllText(r, r);
        }

        var res = Pasar(new[] { a, b });

        Assert.Equal(1, res.Movidas);
        Assert.Equal(a, File.ReadAllText(Path.Combine(_musica, "Tema.mp3")));   // la primera, sin pisar
        Assert.True(File.Exists(b));
        Assert.Equal("1 canción pasada a la biblioteca · 1 se queda en la bandeja (Tema.mp3: Ya hay un archivo con ese nombre en «Nuevas»; no se sobrescribe). "
                     + "Se puede deshacer desde Enriquecer.", BandejaViewModel.TextoPasadas(res));
        Assert.Single(Anotados());
    }

    [Fact]
    public void El_historial_lo_sigue_llamando_traslado_de_carpeta()
    {
        var descargas = Descargas(2);

        Assert.Equal(2, Pasar(descargas).Movidas);

        Assert.All(Anotados(), r =>
        {
            Assert.Equal(nameof(TipoEjecucion.Traslado), r.Origen);
            Assert.True(r.Renamed);
            Assert.Null(r.CarpetasCreadas);   // la carpeta de destino ya existía: deshacer no la quita
        });
        var ejecucion = Assert.Single(new HistorialDeshacer(_paths.UndoDir).Listar());
        Assert.Equal(TipoEjecucion.Traslado, ejecucion.Tipo);
        Assert.Equal("Traslado de carpeta", ejecucion.TipoTexto);
    }

    // --- Lo que sigue a la descarga, con el motor de la aplicación ---

    [Fact]
    public void La_ficha_y_lo_decidido_en_Enriquecer_siguen_a_la_descarga_y_vuelven_al_deshacer()
    {
        var a = Path.Combine(_bandeja, "Tema.mp3");
        Mp3Fixture.WriteMinMp3(a);
        var b = EnLaBiblioteca(a);
        var biblioteca = new LibraryStore(new AppConfig { Folders = new() { Path.GetDirectoryName(_musica)! } }, () => { },
                                          new ScanCache(Path.Combine(_dir, "scan.json")));
        var motor = MotorDePrueba.Crear(_paths.DataDir, new Logger(), biblioteca);
        void Poner(string campo, object valor) => typeof(AppEngine).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(motor, valor);
        Poner("<Fichas>k__BackingField", new AlmacenFichas(_paths.FichasDjPath));
        var seguirDeshecho = typeof(AppEngine).GetMethod("SeguirDeshecho", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(ArchivoDeshecho) })!;
        Poner("<Undo>k__BackingField", new UndoEngine(_paths) { AlDeshacer = d => seguirDeshecho.Invoke(motor, new object[] { d }) });
        motor.Fichas.Poner(a, new FichaDj { Energia = 6 });
        motor.Applied.Add(a);

        var res = motor.PasarABiblioteca(new[] { a }, _musica, null, CancellationToken.None);

        Assert.Equal(1, res.Movidas);
        Assert.Equal(6, new AlmacenFichas(_paths.FichasDjPath).Obtener(b)!.Energia);   // guardada en el disco
        Assert.True(new IgnoreList(_paths.AppliedPath).Contains(b));
        Assert.False(motor.Applied.Contains(a));

        var deshecho = motor.DeshacerUltima();

        Assert.Equal(1, deshecho.Reverted);
        Assert.True(File.Exists(a));
        Assert.Equal(6, motor.Fichas.Obtener(a)!.Energia);
        Assert.True(motor.Applied.Contains(a));
    }
}
