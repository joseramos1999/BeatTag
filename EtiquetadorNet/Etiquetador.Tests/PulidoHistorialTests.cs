using System.Text.Json;
using System.Text.Json.Nodes;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Pulido del historial de deshacer tras la tanda 3, con archivos MP3 de verdad y el deshacer de
/// verdad:
///   · datos-1: deshacer un renombrado desde el historial pone al día las ejecuciones posteriores,
///     que siguen al archivo hasta su nombre anterior (antes, «Deshacer última» les devolvía su
///     cambio a OTRO archivo que llegara después a ese nombre);
///   · datos-3: deshacer avisa de cada archivo que toca, para que las cachés de audio lo sigan;
///   · interfaz-9: un archivo con su registro corrector es una sola fila y cuenta una vez;
///   · Origen: cada escritor dice qué operación es;
///   · géneros con barra: en ID3v2.3 «Dance/Electrónica» se relee como dos géneros.
/// </summary>
public class PulidoHistorialTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    private readonly AppPaths _paths;
    private readonly string _musica;

    public PulidoHistorialTests()
    {
        _paths = new AppPaths(_dir);   // siempre una carpeta temporal: nunca la de datos del usuario
        _paths.EnsureDirectories();
        _musica = Path.Combine(_dir, "musica");
        Directory.CreateDirectory(_musica);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------------------------------------------------------------- utilidades

    private string Manifiesto(string nombre) => Path.Combine(_paths.UndoDir, nombre);

    private void Escribir(string nombre, params UndoRecord[] registros)
        => File.WriteAllText(Manifiesto(nombre), string.Join("\n", registros.Select(r => JsonSerializer.Serialize(r))) + "\n");

    private static UndoRecord Registro(string linea) => JsonSerializer.Deserialize<UndoRecord>(linea)!;

    private string Cancion(string carpeta, string nombre, string titulo)
    {
        var p = Path.Combine(Directory.CreateDirectory(carpeta).FullName, nombre);
        Mp3Fixture.WriteMinMp3(p);
        Mp3Fixture.SetTags(p, titulo, "Fisher", null);
        return p;
    }

    private string Cancion(string nombre, string titulo) => Cancion(_musica, nombre, titulo);

    private static string[] Generos(string ruta)
    {
        using var f = TagLib.File.Create(ruta);
        return f.Tag.Genres ?? Array.Empty<string>();
    }

    private static void PonerGenero(string ruta, params string[] generos)
    {
        using var f = TagLib.File.Create(ruta);
        f.Tag.Genres = generos;
        f.Save();
    }

    /// <summary>Cambia el título como lo haría la aplicación y devuelve el registro.</summary>
    private static UndoRecord CambiarTitulo(string ruta, string nuevo, bool aplicada = true)
    {
        var antes = Mp3Fixture.GetTitle(ruta);
        Mp3Fixture.SetTags(ruta, nuevo, null, null);
        return new UndoRecord
        {
            OrigPath = ruta, FinalPath = ruta, Aplicada = aplicada,
            Fields = new() { ["Title"] = FieldChange.Str(antes, nuevo) },
        };
    }

    /// <summary>Renombra (y cambia el título) como lo haría la aplicación.</summary>
    private static UndoRecord Renombrar(string de, string a, string titulo)
    {
        var campos = new Dictionary<string, FieldChange> { ["Title"] = FieldChange.Str(Mp3Fixture.GetTitle(de), titulo) };
        Mp3Fixture.SetTags(de, titulo, null, null);
        File.Move(de, a);
        return new UndoRecord { OrigPath = de, FinalPath = a, Renamed = true, Fields = campos };
    }

    private HistorialDeshacer Historial => new(_paths.UndoDir);

    private EjecucionDeshacer Ejecucion(string id) => Historial.Listar().Single(e => e.Id == id);

    private static async Task Esperar(Func<bool> condicion)
    {
        for (var i = 0; i < 100 && !condicion(); i++) await Task.Delay(50);
        Assert.True(condicion());
    }

    // ---------------------------------------------------------------- datos-1

    /// <summary>Una «papelera» de prueba: mueve el archivo a una carpeta aparte.</summary>
    private (bool, string) APapelera(string ruta)
    {
        var papelera = Directory.CreateDirectory(Path.Combine(_dir, "Papelera")).FullName;
        File.Move(ruta, Path.Combine(papelera, Path.GetFileName(ruta)));
        return (true, "");
    }

    // El recorrido del hallazgo: Sustituir desde la Bandeja (la descarga ocupa la ruta de la copia
    // antigua), Enriquecer aplica a la descarga sin renombrarla, se deshace SOLO la sustitución, el
    // usuario recupera la copia antigua de la papelera (como aconseja la Bandeja) y pulsa «Deshacer
    // última». Antes, la copia antigua recibía las etiquetas que tenía la descarga y salía de
    // aplicadas.json; ahora el cambio de Enriquecer vuelve a la descarga, que es la suya.
    [Fact]
    public void Deshacer_solo_la_sustitucion_deja_que_lo_posterior_siga_a_la_descarga()
    {
        var vieja = Cancion(Path.Combine(_musica, "House"), "Fisher - Losing It.mp3", "Losing It");
        var nueva = Cancion(Path.Combine(_dir, "Bandeja"), "fisher 320.mp3", "losing it (clean extended)");
        var sustitucion = Manifiesto("run_20261019_120000.jsonl");
        var r = SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, rec =>
        {
            File.AppendAllText(sustitucion, JsonSerializer.Serialize(rec) + "\n");
            return "";
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(vieja, r.Destino);
        Escribir("run_20261019_120500.jsonl", CambiarTitulo(vieja, "Losing It (Clean Extended)"));   // Enriquecer

        var lote = Ejecucion("run_20261019_120000");
        var analisis = Historial.Analizar(lote, null);
        Assert.Empty(analisis.Bloqueados);
        Assert.True(Assert.Single(analisis.Solapes).PierdeDeshacer);

        var aplicadas = new IgnoreList(Path.Combine(_dir, "aplicadas.json"));
        aplicadas.Add(vieja);   // Enriquecer dio por aplicada la descarga donde estaba entonces

        var undo = new UndoEngine(_paths);
        var res = undo.DeshacerCambios(lote.Manifiesto!, analisis.Huellas);
        Assert.Equal(1, res.Reverted);
        Assert.True(File.Exists(nueva));      // la descarga vuelve a la bandeja…
        Assert.False(File.Exists(vieja));
        // …y el registro de Enriquecer la sigue hasta allí, con su marca de aplicada.
        var posterior = Registro(Assert.Single(File.ReadAllLines(Manifiesto("run_20261019_120500.jsonl"))));
        Assert.Equal((nueva, nueva), (posterior.OrigPath, posterior.FinalPath));
        Assert.Equal(new[] { (vieja, nueva) }, res.Vueltas);
        Assert.Equal(1, EnrichViewModel.SeguirAplicadas(aplicadas, res.Vueltas));
        Assert.False(aplicadas.Contains(vieja));

        File.Move(Path.Combine(_dir, "Papelera", "Fisher - Losing It.mp3"), vieja);   // recuperada de la papelera

        var ultima = undo.UndoLastRun();
        Assert.Equal((1, 0), (ultima.Reverted, ultima.Missing));
        Assert.Equal("losing it (clean extended)", Mp3Fixture.GetTitle(nueva));   // a la descarga, lo suyo
        Assert.Equal("Losing It", Mp3Fixture.GetTitle(vieja));                    // la copia antigua, intacta
        Assert.Equal(new[] { nueva }, ultima.AplicadasRevertidas.Distinct());     // sale de aplicadas la descarga, no la otra
        Assert.Equal(1, EnrichViewModel.OlvidarRevertidas(aplicadas, new ApplyMarks(Path.Combine(_dir, "marcas.json")), ultima.AplicadasRevertidas));
        Assert.Equal(0, aplicadas.Count);
    }

    // Si el historial de la posterior se bloquea justo al escribirlo (antivirus, OneDrive), nunca
    // se deja apuntando a un nombre que ya no es el de su archivo: el archivo vuelve a donde lo
    // dejó la ejecución y su registro se queda pendiente para reintentarlo.
    [Fact]
    public void Si_la_posterior_no_se_deja_poner_al_dia_el_archivo_vuelve_y_queda_pendiente()
    {
        var a = Cancion("pista01.mp3", "pista01");
        var b = Path.Combine(_musica, "Fisher - Losing It.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(a, b, "Losing It"));
        Escribir("run_20261002_100000.jsonl", CambiarTitulo(b, "LOSING IT", aplicada: false));
        var posteriorAntes = File.ReadAllText(Manifiesto("run_20261002_100000.jsonl"));
        var undo = new UndoEngine(_paths) { EscribirPosterior = (_, _) => "El archivo está en uso." };

        var res = undo.DeshacerCambios(Manifiesto("run_20261001_100000.jsonl"), null);

        Assert.Equal((0, 1), (res.Reverted, res.Errors));
        Assert.True(File.Exists(b));
        Assert.False(File.Exists(a));
        Assert.Contains("1 archivo se ha dejado con el nombre que le dio la ejecución", res.Aviso);
        Assert.Contains("vuelve a intentarlo", res.Aviso);
        Assert.Single(File.ReadAllLines(Manifiesto("run_20261001_100000.jsonl")));   // sigue pendiente
        Assert.Equal(posteriorAntes, File.ReadAllText(Manifiesto("run_20261002_100000.jsonl")));

        // La posterior sigue encontrando su archivo donde lo dejó.
        var ultima = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal(1, ultima.Reverted);
        Assert.Equal("Losing It", Mp3Fixture.GetTitle(b));
    }

    // Lo mismo, pero el historial de la posterior ya está en uso ANTES de empezar: no se toca el
    // archivo. En macOS y Linux abrir un archivo no impide que otro lo abra en exclusiva.
    [WindowsFact]
    public void Con_el_historial_de_la_posterior_en_uso_no_se_toca_el_archivo()
    {
        var a = Cancion("pista01.mp3", "pista01");
        var b = Path.Combine(_musica, "Fisher - Losing It.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(a, b, "Losing It"));
        Escribir("run_20261002_100000.jsonl", CambiarTitulo(b, "LOSING IT", aplicada: false));

        UndoResult res;
        using (new FileStream(Manifiesto("run_20261002_100000.jsonl"), FileMode.Open, FileAccess.Read, FileShare.Read))
            res = new UndoEngine(_paths).DeshacerCambios(Manifiesto("run_20261001_100000.jsonl"), null);

        Assert.Equal((0, 1), (res.Reverted, res.Errors));
        Assert.Contains("1 archivo no se ha deshecho todavía", res.Aviso);
        Assert.True(File.Exists(b));
        Assert.Equal("LOSING IT", Mp3Fixture.GetTitle(b));   // ni el nombre ni las etiquetas

        // Ya libre, se deshace y la posterior lo sigue.
        var otra = new UndoEngine(_paths).DeshacerCambios(Manifiesto("run_20261001_100000.jsonl"), null);
        Assert.Equal(1, otra.Reverted);
        Assert.True(File.Exists(a));
        Assert.Equal(a, Registro(File.ReadAllLines(Manifiesto("run_20261002_100000.jsonl")).Single()).FinalPath);
    }

    // Dentro de la misma ejecución: lo que no se ha elegido y tocó el archivo después de renombrarlo
    // también lo sigue.
    [Fact]
    public void En_la_misma_ejecucion_lo_no_elegido_sigue_al_archivo()
    {
        var a = Cancion("pista01.mp3", "pista01");
        var b = Path.Combine(_musica, "Fisher - Losing It.mp3");
        var renombrado = Renombrar(a, b, "Losing It");
        Escribir("run_20261001_100000.jsonl", renombrado, CambiarTitulo(b, "LOSING IT", aplicada: false));
        var e = Ejecucion("run_20261001_100000");
        var huellaRenombrado = Historial.Cambios(e).First(c => c.Renombra).Huella;

        var res = new UndoEngine(_paths).DeshacerCambios(e.Manifiesto!, new[] { huellaRenombrado });

        Assert.Equal(1, res.Reverted);
        Assert.True(File.Exists(a));
        var queda = Registro(Assert.Single(File.ReadAllLines(e.Manifiesto!)));
        Assert.Equal((a, a), (queda.OrigPath, queda.FinalPath));
        Assert.Equal(1, new UndoEngine(_paths).UndoLastRun().Reverted);
        Assert.Equal("Losing It", Mp3Fixture.GetTitle(a));
    }

    [Fact]
    public void Seguir_un_archivo_reescribe_el_formato_antiguo_y_se_para_en_un_renombrado()
    {
        var b = Rutas.Archivo("b.mp3");
        var a = Rutas.Archivo("a.mp3");

        // Formato antiguo: «new» es la ruta y «orig», solo el nombre.
        var seguir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [b] = a };
        var antiguo = new JsonObject { ["new"] = b, ["orig"] = "b.mp3", ["renamed"] = false, ["tags"] = new JsonObject { ["Title"] = "x" } }.ToJsonString();
        var nuevo = JsonNode.Parse(UndoEngine.Reubicar(antiguo, seguir, out var clave)!)!;
        Assert.Equal(b, clave);
        Assert.Equal((a, "a.mp3"), (nuevo["new"]!.GetValue<string>(), nuevo["orig"]!.GetValue<string>()));

        // Un ajuste de volumen también lo sigue: así no se le aplica la ganancia inversa a otro archivo.
        var volumen = JsonSerializer.Serialize(new UndoRecord { OrigPath = b, FinalPath = b, Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(0, 2) } });
        Assert.Equal(a, Registro(UndoEngine.Reubicar(volumen, seguir, out _)!).FinalPath);

        // Un registro que lo renombra: desde ahí ese nombre es de otro archivo y se deja de seguir.
        var renombra = JsonSerializer.Serialize(new UndoRecord { OrigPath = b, FinalPath = Rutas.Archivo("c.mp3"), Renamed = true });
        Assert.Null(UndoEngine.Reubicar(renombra, seguir, out _));
        Assert.Empty(seguir);
        Assert.Null(UndoEngine.Reubicar(volumen, seguir, out _));
    }

    private static string Titulo(string ruta)
        => JsonSerializer.Serialize(new UndoRecord { OrigPath = ruta, FinalPath = ruta, Fields = new() { ["Title"] = FieldChange.Str("x", "y") } });

    private static string RutaDe(string linea) => Registro(linea).FinalPath;

    // Revisión de datos-1: cada vuelta recorría por su cuenta todas las líneas pendientes que la
    // seguían (MEDIDO: 6.000 registros, 13,7 s frente a 2,7 s). Con 5.000 vueltas y 5.000 pendientes
    // intercaladas, la versión anterior leía unos 12 millones de líneas (minutos); esta, 5.000.
    [Fact]
    public void Seguir_en_la_misma_ejecucion_lee_cada_linea_una_vez()
    {
        const int n = 5000;
        var textos = new List<string>();
        var vueltas = new List<UndoEngine.Vuelta>();
        for (var i = 0; i < n; i++)
        {
            var nombre = Rutas.De("m", $"tema {i}.mp3");
            vueltas.Add(new UndoEngine.Vuelta(textos.Count, nombre, Rutas.De("m", $"pista{i:00000}.mp3")));
            textos.Add(JsonSerializer.Serialize(new UndoRecord { OrigPath = Rutas.De("m", $"pista{i:00000}.mp3"), FinalPath = nombre, Renamed = true }));
            textos.Add(Titulo(nombre));   // no elegida: tocó el archivo después de renombrarlo
        }
        var pendiente = textos.Select((_, j) => j % 2 == 1).ToArray();

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        UndoEngine.SeguirEnEsteManifiesto(textos, pendiente, vueltas);
        reloj.Stop();

        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(5), $"tardó {reloj.Elapsed.TotalSeconds:0.0} s");
        for (var i = 0; i < n; i++)
            Assert.Equal(Rutas.De("m", $"pista{i:00000}.mp3"), RutaDe(textos[2 * i + 1]));
    }

    // Si dos vueltas dejaron su archivo en la misma ruta, lo pendiente que va detrás de la segunda es
    // de la segunda: antes lo reclamaba la primera, porque se recorría entera antes que la otra.
    [Fact]
    public void Seguir_en_la_misma_ejecucion_atiende_a_la_vuelta_mas_reciente()
    {
        var a = Rutas.Archivo("a.mp3");
        var h1 = Rutas.Archivo("h1.mp3");
        var h2 = Rutas.Archivo("h2.mp3");
        var c = Rutas.Archivo("c.mp3");
        var textos = new List<string>
        {
            JsonSerializer.Serialize(new UndoRecord { OrigPath = h1, FinalPath = a, Renamed = true }),   // 0: vuelta 1
            Titulo(a),                                                                                    // 1: pendiente → h1
            JsonSerializer.Serialize(new UndoRecord { OrigPath = h2, FinalPath = a, Renamed = true }),   // 2: vuelta 2
            Titulo(a),                                                                                    // 3: pendiente → h2
            JsonSerializer.Serialize(new UndoRecord { OrigPath = a, FinalPath = c, Renamed = true }),    // 4: pendiente, renombra
            Titulo(a),                                                                                    // 5: ya es otro archivo
        };
        var pendiente = new[] { false, true, false, true, true, true };

        UndoEngine.SeguirEnEsteManifiesto(textos, pendiente,
            new[] { new UndoEngine.Vuelta(2, a, h2), new UndoEngine.Vuelta(0, a, h1) });

        Assert.Equal(new[] { h1, h2, c, a }, new[] { 1, 3, 4, 5 }.Select(j => RutaDe(textos[j])));
    }

    // Revisión de datos-1: si una posterior no se puede ni leer, se aplazan todos los renombrados
    // elegidos, también los de archivos que no tocó. El aviso no puede decir que los cambió.
    [Fact]
    public void El_aviso_de_lo_aplazado_dice_si_la_posterior_no_se_pudo_leer()
    {
        Assert.Equal("1 archivo no se ha deshecho todavía: el historial de una ejecución posterior que también lo cambió está en uso (run_2.jsonl: en uso). "
                     + "Vuelve a intentarlo dentro de unos segundos.",
                     UndoEngine.TextoAplazados(1, "run_2.jsonl: en uso", ilegible: false));
        Assert.Equal("3 archivos no se han deshecho todavía: no se ha podido leer el historial de una ejecución posterior (run_2.jsonl: en uso), "
                     + "y sin él no se sabe si los cambió también. Vuelve a intentarlo dentro de unos segundos.",
                     UndoEngine.TextoAplazados(3, "run_2.jsonl: en uso", ilegible: true));
        Assert.Contains("no se sabe si lo cambió también", UndoEngine.TextoAplazados(1, "x", ilegible: true));
    }

    // Lo mismo con archivos de verdad: la posterior, bloqueada hasta para leerla, solo tocó OTRO
    // archivo. En macOS y Linux abrir un archivo no impide que otro lo lea.
    [WindowsFact]
    public void Con_una_posterior_ilegible_el_aviso_no_dice_que_cambio_el_archivo()
    {
        var a = Cancion("pista01.mp3", "pista01");
        var b = Path.Combine(_musica, "Fisher - Losing It.mp3");
        var otro = Cancion("otro.mp3", "Otro");
        Escribir("run_20261001_100000.jsonl", Renombrar(a, b, "Losing It"));
        Escribir("run_20261002_100000.jsonl", CambiarTitulo(otro, "OTRO", aplicada: false));

        UndoResult res;
        using (new FileStream(Manifiesto("run_20261002_100000.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
            res = new UndoEngine(_paths).DeshacerCambios(Manifiesto("run_20261001_100000.jsonl"), null);

        Assert.Equal((0, 1), (res.Reverted, res.Errors));
        Assert.True(File.Exists(b));   // no se toca: no se sabe qué hace la posterior
        Assert.Contains("1 archivo no se ha deshecho todavía: no se ha podido leer el historial de una ejecución posterior", res.Aviso);
        Assert.DoesNotContain("que también lo cambió", res.Aviso);
    }

    // ---------------------------------------------------------------- datos-3

    [Fact]
    public void Al_deshacer_se_avisa_de_cada_archivo_con_su_sello_de_antes()
    {
        var a = Cancion("pista01.mp3", "pista01");
        var b = Path.Combine(_musica, "Fisher - Losing It.mp3");
        var c = Cancion("editado.mp3", "editado");
        Escribir("run_20261001_100000.jsonl", Renombrar(a, b, "Losing It"), CambiarTitulo(c, "Editado"));
        Mp3Fixture.SetTags(c, "Lo cambié a mano", null, null);   // se conserva: no se toca el archivo
        var sello = SelloArchivo.De(b);
        var avisos = new List<ArchivoDeshecho>();

        var res = new UndoEngine(_paths) { AlDeshacer = avisos.Add }.UndoLastRun();

        Assert.Equal((1, 1), (res.Reverted, res.Manual));
        var aviso = Assert.Single(avisos);
        Assert.Equal((b, a, true, false), (aviso.Desde, aviso.Hasta, aviso.Etiquetas, aviso.Volumen));
        Assert.Equal((sello.M, sello.S, true), (aviso.FechaAntes, aviso.TamanoAntes, aviso.ExistiaAntes));
    }

    // Con el aviso, la huella sigue al archivo como al escribir: antes caducaba por la fecha y
    // «Comprobar audio» volvía a mandar al servicio de pago lo que ya había identificado.
    [Fact]
    public void Tras_deshacer_la_huella_sigue_valiendo()
    {
        var a = Cancion("pista01.mp3", "pista01");
        var b = Path.Combine(_musica, "Fisher - Losing It.mp3");
        Escribir("run_20261001_100000.jsonl", Renombrar(a, b, "Losing It"));
        var s = SelloArchivo.De(b);
        var cache = Path.Combine(_dir, "huellas.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new Dictionary<string, object> { [b] = new { M = s.M, S = s.S, D = 180.0, F = new[] { 1, 2, 3 } } }));
        var huellas = new FingerprintScanner(cache, Path.Combine(_dir, "fpcalc-inexistente"));
        Assert.NotNull(huellas.Get(b));

        // Lo mismo que hace AppEngine.SeguirArchivo con las huellas.
        void Seguir(string de, string para, SelloArchivo antes)
        {
            if (!string.Equals(de, para, StringComparison.Ordinal)) huellas.Mover(de, para);
            if (antes.Existe) huellas.Retocar(para, antes.M, antes.S);
        }
        new UndoEngine(_paths) { AlDeshacer = x => AppEngine.SeguirDeshecho(x, Seguir, (r, antes, _, _) => huellas.Retocar(r, antes.M, antes.S)) }.UndoLastRun();

        Assert.Equal("pista01", Mp3Fixture.GetTitle(a));
        Assert.Equal(new[] { 1, 2, 3 }, huellas.Get(a));
        Assert.Null(huellas.Get(b));
    }

    [Fact]
    public void Que_hace_cada_aviso_con_las_caches()
    {
        var de = Rutas.Archivo("b.mp3");
        var a = Rutas.Archivo("a.mp3");
        var seguidos = new List<(string De, string A, SelloArchivo S)>();
        var volumenes = new List<(string Ruta, SelloArchivo S, double Db, double Anotados)>();
        void Aviso(ArchivoDeshecho x) => AppEngine.SeguirDeshecho(x, (o, d, s) => seguidos.Add((o, d, s)), (r, s, db, an) => volumenes.Add((r, s, db, an)));
        var sello = new SelloArchivo(10, 20, true);

        Aviso(new ArchivoDeshecho(de, a, Etiquetas: true, Volumen: false, 10, 20, true));   // nombre y etiquetas
        Assert.Equal((de, a, sello), Assert.Single(seguidos));
        seguidos.Clear();

        Aviso(new ArchivoDeshecho(de, a, Etiquetas: false, Volumen: false, 10, 20, true));  // solo el nombre: la fecha no cambia
        Assert.Equal((de, a, default(SelloArchivo)), Assert.Single(seguidos));
        seguidos.Clear();

        // El volumen: la huella y la identificación siguen, y la sonoridad se desplaza los pasos devueltos.
        // De la ganancia aplicada solo se descuentan los anotados (un ajuste de 1.10 no lo estaba).
        Aviso(new ArchivoDeshecho(a, a, Etiquetas: false, Volumen: true, 10, 20, true, PasosVolumen: 3, PasosAnotados: 1));
        Assert.Empty(seguidos);
        Assert.Equal((a, sello, 4.5, 1.5), Assert.Single(volumenes));
    }

    // ---------------------------------------------------------------- interfaz-9

    // Lo que hace el Asistente al escribir «Hip-Hop/Rap» en ID3v2.3: el registro de lo pedido y el
    // corrector con lo que quedó. Antes eran dos filas, la columna «Archivos» decía 2 y marcar solo
    // la primera no devolvía nada.
    [Fact]
    public async Task Un_archivo_con_su_registro_corrector_es_una_fila_y_se_deshace_entero()
    {
        var p = Cancion("tema.mp3", "Tema");
        var otra = Cancion("otra.mp3", "Otra");
        PonerGenero(p, "Hip-Hop/Rap");
        var quedo = Generos(p);
        Assert.Equal(new[] { "Hip-Hop", "Rap" }, quedo);   // MEDIDO: TagLib# parte por la barra en ID3v2.3
        PonerGenero(otra, "House");
        Escribir("run_20261009_160301.jsonl",
            new UndoRecord { OrigPath = p, FinalPath = p, Origen = nameof(TipoEjecucion.GeneroPorReglas), Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "Rap" }, new[] { "Hip-Hop/Rap" }) } },
            new UndoRecord { OrigPath = p, FinalPath = p, Origen = nameof(TipoEjecucion.GeneroPorReglas), Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "Rap" }, quedo) } },
            new UndoRecord { OrigPath = otra, FinalPath = otra, Origen = nameof(TipoEjecucion.GeneroPorReglas), Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "Rap" }, new[] { "House" }) } });

        var e = Ejecucion("run_20261009_160301");
        Assert.Equal((3, 2), (e.Cambios, e.Archivos));
        Assert.Equal("Género por carpeta y artista", e.TipoTexto);
        Assert.Equal(2, new FilaEjecucion(e).Archivos);

        var undo = new UndoEngine(_paths);
        UndoResult? hecho = null;
        var vm = new HistorialDeshacerViewModel(Historial, (ej, h) =>
        {
            var r = hecho = undo.DeshacerCambios(ej.Manifiesto!, h);
            return Task.FromResult<string?>($"{r.Reverted}");
        });
        await vm.CargarAsync();
        await Esperar(() => vm.Cambios.Count == 2);
        var fila = vm.Cambios.Single(c => c.Archivo == "tema.mp3");
        Assert.Equal(2, fila.Registros.Count);
        Assert.Equal("género", fila.Cambios);

        fila.Marcada = true;
        Assert.Equal(1, vm.Marcadas);
        var plan = await vm.PrepararAsync(soloMarcadas: true);
        Assert.Equal(2, plan!.Huellas!.Count);   // los dos registros
        Assert.Equal("Deshacer (1)", plan.Aceptar);
        Assert.StartsWith("1 archivo volverá", plan.Cuerpo);
        await vm.EjecutarAsync(plan);

        Assert.Equal(new[] { "Rap" }, Generos(p));
        Assert.Equal(new[] { "House" }, Generos(otra));   // no estaba marcada
        // El original, cuando le llega el turno, ya encuentra el género devuelto por su corrector: no
        // es un cambio manual conservado (antes, «1 revertido · 1 conservado» con el archivo entero).
        Assert.Equal((2, 0), (hecho!.Reverted, hecho.Manual));
    }

    // Un campo que ya tiene el valor de antes está devuelto, no «cambiado a mano»; uno con otro valor
    // sí se conserva, como siempre.
    [Fact]
    public void Un_campo_que_ya_esta_como_antes_cuenta_como_devuelto()
    {
        var p = Cancion("tema.mp3", "Tema");
        var q = Cancion("otro.mp3", "Otro");
        Escribir("run_20261001_100000.jsonl", CambiarTitulo(p, "Tema (Extended)"), CambiarTitulo(q, "Otro (Extended)"));
        Mp3Fixture.SetTags(p, "Tema", null, null);          // el usuario lo devolvió a mano a como estaba
        Mp3Fixture.SetTags(q, "Otro (Mi edición)", null, null);   // y este lo cambió por otra cosa

        var res = new UndoEngine(_paths).UndoLastRun();

        Assert.Equal((1, 1), (res.Reverted, res.Manual));
        Assert.Equal(new[] { p }, res.AplicadasRevertidas.Distinct());   // vuelve a proponerse: está como antes de aplicar
        Assert.Equal("Tema", Mp3Fixture.GetTitle(p));
        Assert.Equal("Otro (Mi edición)", Mp3Fixture.GetTitle(q));
    }

    [Fact]
    public void La_confirmacion_cuenta_archivos_y_no_registros()
    {
        var ejecucion = new EjecucionDeshacer("run_20261009_160301", new DateTime(2026, 10, 9, 16, 3, 0), TipoEjecucion.GeneroPorReglas,
                                              3, 0, 0, 0, Rutas.Archivo("run_20261009_160301.jsonl"), null);
        var a1 = new CambioDeshacer("h1", Rutas.Archivo("a.mp3"), Rutas.Archivo("a.mp3"), false, new[] { "Genre" }, EstadoCambio.Pendiente);
        var a2 = a1 with { Huella = "h2" };
        var b = new CambioDeshacer("h3", Rutas.Archivo("b.mp3"), Rutas.Archivo("b.mp3"), false, new[] { "Genre" }, EstadoCambio.Pendiente);
        var a = new AnalisisDeshacer(ejecucion, new[] { a1, a2, b }, Array.Empty<BloqueoDeshacer>(), Array.Empty<SolapeDeshacer>(),
                                     new[] { a1, a2 });

        var p = HistorialDeshacerViewModel.Planificar(a, soloMarcadas: true);

        Assert.StartsWith("2 archivos volverán a como estaban antes de esa ejecución.", p.Cuerpo);
        Assert.Contains("1 archivo no aparece en el disco", p.Cuerpo);
        Assert.Equal("Deshacer (2)", p.Aceptar);
        Assert.Equal(3, p.Huellas!.Count);
        Assert.Equal("Deshacer 2 canciones de «Género por carpeta y artista» del 09/10/2026 16:03", p.Titulo);
    }

    [Fact]
    public void Las_filas_juntan_los_registros_de_un_archivo_en_el_mismo_estado()
    {
        CambioDeshacer C(string huella, string archivo, EstadoCambio estado, bool renombra = false, params string[] campos)
            => new(huella, Rutas.Archivo("antes-" + archivo), Rutas.Archivo(archivo), renombra, campos, estado);

        var filas = HistorialDeshacerViewModel.Agrupar(new[]
        {
            C("1", "a.mp3", EstadoCambio.Pendiente, true, "Title"),
            C("2", "b.mp3", EstadoCambio.Pendiente, false, "Genre"),
            C("3", "A.MP3", EstadoCambio.Pendiente, false, "Genre"),   // el mismo archivo: Windows y macOS no distinguen mayúsculas
            C("4", "a.mp3", EstadoCambio.Deshecho, false, "Genre"),    // otro estado: otra fila
            new CambioDeshacer("5", "", "", false, Array.Empty<string>(), EstadoCambio.Pendiente, Legible: false),
            new CambioDeshacer("6", "", "", false, Array.Empty<string>(), EstadoCambio.Pendiente, Legible: false),
        });

        Assert.Equal(5, filas.Count);
        Assert.Equal(new[] { "1", "3" }, filas[0].Huellas);
        Assert.Equal("título, género, nombre", filas[0].Cambios);
        Assert.Equal("antes-a.mp3", filas[0].NombreAnterior);
        Assert.Equal("b.mp3", filas[1].Archivo);
        Assert.False(filas[3].Seleccionable);                            // lo ilegible va aparte y no se marca
        Assert.Equal(new[] { "6" }, filas[4].Huellas);
    }

    [Theory]
    [InlineData(nameof(TipoEjecucion.LimpiezaPacks), "Limpieza de publicidad de packs")]
    [InlineData(nameof(TipoEjecucion.GeneroPorReglas), "Género por carpeta y artista")]
    [InlineData(nameof(TipoEjecucion.Sustitucion), "Sustitución desde la Bandeja")]
    public void Las_herramientas_nuevas_tienen_nombre_propio(string origen, string texto)
    {
        var r = new UndoRecord { OrigPath = Rutas.Archivo("a.mp3"), FinalPath = Rutas.Archivo("a.mp3"), Origen = origen, Fields = new() { ["Genre"] = FieldChange.Arr(new[] { "x" }, new[] { "y" }) } };
        var tipo = HistorialDeshacer.DeducirTipo(new[] { JsonNode.Parse(JsonSerializer.Serialize(r)) });
        Assert.Equal(origen, tipo.ToString());
        Assert.Equal(texto, HistorialDeshacer.Texto(tipo));
    }

    [Fact]
    public void Los_textos_del_historial_concuerdan_en_singular()
    {
        Assert.Equal("1 ejecución anotada · 1 con cambios que se pueden deshacer.", HistorialDeshacerViewModel.ResumenLista(1, 1));
        Assert.Equal("3 ejecuciones anotadas · 0 con cambios que se pueden deshacer.", HistorialDeshacerViewModel.ResumenLista(3, 0));
        Assert.Equal("Todavía no hay ningún cambio anotado.", HistorialDeshacerViewModel.ResumenLista(0, 0));
    }

    // El aviso de un deshacer atascado decía «1 no aparecen y 1 dan error».
    [Fact]
    public void El_aviso_de_un_deshacer_atascado_concuerda_en_singular()
    {
        var falta = Rutas.De("no-existe", "a.mp3");
        var noAudio = Path.Combine(_musica, "notas.txt");
        File.WriteAllText(noAudio, "esto no es audio");   // sus etiquetas no se pueden leer: da error
        Escribir("run_20261001_100000.jsonl",
            new UndoRecord { OrigPath = falta, FinalPath = falta, Fields = new() { ["Title"] = FieldChange.Str("x", "y") } },
            new UndoRecord { OrigPath = noAudio, FinalPath = noAudio, Fields = new() { ["Title"] = FieldChange.Str("x", "y") } });

        var res = new UndoEngine(_paths).UndoLastRun();

        Assert.True(res.SinAvance);
        Assert.Contains("(1 no aparece y 1 da error)", res.Aviso);
    }

    // ---------------------------------------------------------------- Origen

    private static FormularioEditor Abierto(string ruta, TagValues v)
        => new(Path.GetFileName(ruta), v.Title, v.Artist, v.Album, v.Genre,
               v.Year == 0 ? "" : v.Year.ToString(), v.Bpm == 0 ? "" : v.Bpm.ToString(), v.Comment);

    // Lo que justifica el campo: el Editor y Unificar géneros escriben solo «Genre», y por los campos
    // la edición del Editor salía como «Géneros».
    [Fact]
    public void Una_edicion_del_Editor_que_solo_cambia_el_genero_sale_como_edicion()
    {
        var p = Cancion("tema.mp3", "Tema");
        PonerGenero(p, "Funk");
        var cargado = TagEditor.Read(p);

        var g = EdicionManual.Guardar(p, cargado, Abierto(p, cargado) with { Genre = "Disco" }, 0, 0,
                                      _paths.NuevoManifiesto(new DateTime(2026, 10, 1, 10, 0, 0)));
        Assert.Equal(new[] { "Genre" }, g.Cambios.Keys);

        var q = Cancion("otro.mp3", "Otro");
        PonerGenero(q, "Funk");
        using (var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(new DateTime(2026, 10, 1, 11, 0, 0)), out var error))
            Assert.Equal(1, AppEngine.EscribirGeneros(new[] { (q, "Disco") }, m, error, null, null, null, CancellationToken.None).Escritas);

        Assert.Equal(TipoEjecucion.EdicionEtiquetas, Ejecucion("run_20261001_100000").Tipo);
        Assert.Equal("Edición de etiquetas", Ejecucion("run_20261001_100000").TipoTexto);
        Assert.Equal(TipoEjecucion.Generos, Ejecucion("run_20261001_110000").Tipo);
    }

    /// <summary>Un historial en memoria que se queda con lo anotado.</summary>
    private sealed class HistorialEnMemoria : IHistorialDeshacer
    {
        public readonly List<UndoRecord> Anotados = new();
        public string Anotar(UndoRecord registro) { Anotados.Add(registro); return ""; }
    }

    [Fact]
    public void Renombrar_y_las_fichas_al_comentario_dicen_que_son()
    {
        var a = Cancion("fisher losing it.mp3", "x");
        var renombrados = new HistorialEnMemoria();
        AppEngine.Renombrar(new[] { (a, "Fisher - Losing It") }, renombrados, "", null, null, null, CancellationToken.None);
        Assert.Equal(nameof(TipoEjecucion.Renombrado), Assert.Single(renombrados.Anotados).Origen);

        var b = Cancion("b.mp3", "b");
        var comentarios = new HistorialEnMemoria();
        AppEngine.VolcarComentarios(new[] { b }, _ => new FichaDj { Energia = 8 }, comentarios, "", null, null);
        Assert.Equal(nameof(TipoEjecucion.Comentario), Assert.Single(comentarios.Anotados).Origen);
    }

    [Fact]
    public async Task Aplicar_anota_su_origen_segun_quien_llama()
    {
        var p = Cancion("tema.mp3", "tema");
        ProcessResult Titulo(string t) => new() { FilePath = p, Old = "tema.mp3", New = "tema.mp3", Title = t, Found = true };
        var soloTitulo = new FieldFlags { Title = true, Artist = false, Album = false, Genre = false, Year = false, Bpm = false };
        var done = Path.Combine(_dir, "procesadas.log");
        var mf = Path.Combine(_paths.UndoDir, "run_20261001_100000.jsonl");

        await new ApplyEngine().ApplyOneAsync(Titulo("Tema"), true, soloTitulo, "keep", null, mf, done, aplicada: true);   // Enriquecer, No encontradas
        await new ApplyEngine().ApplyOneAsync(Titulo("TEMA"), true, soloTitulo, "keep", null, mf, done);                   // Incompletas
        await new ApplyEngine().ApplyOneAsync(Titulo("Tema!"), true, soloTitulo, "keep", null, mf, done, origen: nameof(TipoEjecucion.Sustitucion));

        Assert.Equal(new[] { "AplicarPropuestas", "EdicionEtiquetas", "Sustitucion" },
                     File.ReadAllLines(mf).Select(l => Registro(l).Origen));
    }

    // ---------------------------------------------------------------- papelera (interfaz-1, rendimiento-2 y -3)

    private LibraryStore Biblioteca(int canciones)
    {
        var store = new LibraryStore(new AppConfig(), () => { }, new ScanCache(Path.Combine(_dir, "scan.json")));
        for (var i = 0; i < canciones; i++)
            store.Tracks.Add(new Track { FilePath = Rutas.De("m", $"carpeta {i % 7}", $"tema {i}.mp3"), Folder = Rutas.Carpeta });
        return store;
    }

    // Enviar a la papelera miles de canciones desde la Biblioteca las quitaba una a una, y cada baja
    // rehacía los grupos de las dos tablas agrupadas por carpeta (MEDIDO: 1-2 s con 5.000-18.000).
    [Fact]
    public void Quitar_muchas_canciones_avisa_una_sola_vez_a_las_tablas()
    {
        var store = Biblioteca(1000);
        var avisos = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        store.Tracks.CollectionChanged += (_, e) => avisos.Add(e.Action);
        var cambios = 0;
        store.Changed += () => cambios++;
        var quitar = store.Tracks.Where((_, i) => i % 2 == 0).Select(t => t.FilePath.ToUpperInvariant()).ToList();

        Assert.Equal(500, store.RemoveTracks(quitar));

        Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Reset }, avisos);
        Assert.Equal(1, cambios);
        Assert.Equal(500, store.Tracks.Count);
        Assert.All(store.Tracks.Select((t, i) => (t, i)), x => Assert.EndsWith($"tema {x.i * 2 + 1}.mp3", x.t.FilePath));   // el orden se conserva

        // Unas pocas se siguen quitando en su sitio (la tabla no pierde la posición).
        avisos.Clear();
        Assert.Equal(2, store.RemoveTracks(store.Tracks.Take(2).Select(t => t.FilePath)));
        Assert.Equal(2, avisos.Count(a => a == System.Collections.Specialized.NotifyCollectionChangedAction.Remove));
        Assert.Equal(0, store.RemoveTracks(new[] { Rutas.Archivo("no-esta.mp3") }));
        Assert.Equal(2, cambios);   // sin bajas, sin aviso
    }

    // La papelera de la Biblioteca que se para al cerrar la ventana y el envío de Duplicados que cuenta
    // como proceso se prueban ahora recorriendo las páginas de verdad (RevisionUnionTests): estas dos
    // pruebas solo llamaban a PapeleraYQuitarAsync con su propio token (igual que DialogosCancionTests)
    // y miraban una lista de nombres de propiedades.

    // ---------------------------------------------------------------- géneros con barra

    [Fact]
    public void Un_genero_con_barra_se_anota_como_queda_y_deshacer_lo_devuelve()
    {
        var p = Cancion("tema.mp3", "Tema");
        PonerGenero(p, "House");

        AppEngine.ResultadoGeneros res;
        var mf = _paths.NuevoManifiesto(new DateTime(2026, 10, 1, 10, 0, 0));
        using (var m = ManifiestoDeshacer.Abrir(mf, out var error))
            res = AppEngine.EscribirGeneros(new[] { (p, "Dance/Electrónica") }, m, error, null, null, null, CancellationToken.None);

        Assert.Equal(1, res.Escritas);
        Assert.Equal(new[] { "Dance", "Electrónica" }, Generos(p));
        // Un solo registro (la predicción acierta) y con lo que se relee, que es con lo que compara deshacer.
        var rec = Registro(Assert.Single(File.ReadAllLines(mf)));
        Assert.Equal(new[] { "Dance", "Electrónica" }, rec.Fields!["Genre"].NewArr);
        Assert.Equal(new[] { "House" }, rec.Fields["Genre"].OldArr);

        // Volver a pasar ya no reescribe el archivo: antes la comprobación «ya estaba» no coincidía nunca.
        AppEngine.ResultadoGeneros otra;
        using (var m = ManifiestoDeshacer.Abrir(_paths.NuevoManifiesto(new DateTime(2026, 10, 1, 11, 0, 0)), out var error))
            otra = AppEngine.EscribirGeneros(new[] { (p, "Dance/Electrónica") }, m, error, null, null, null, CancellationToken.None);
        Assert.Equal((0, 1), (otra.Escritas, otra.SinCambio));

        var u = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal((1, 0), (u.Reverted, u.Manual));   // antes: 0 revertidos, 1 «cambiado a mano»
        Assert.Equal(new[] { "House" }, Generos(p));
    }

    // Lo mismo al aplicar una propuesta: Deezer da géneros como «Rap/Hip Hop».
    [Fact]
    public async Task Aplicar_un_genero_del_catalogo_con_barra_se_puede_deshacer()
    {
        var p = Cancion("drake.mp3", "drake");
        PonerGenero(p, "Hip-Hop");
        var info = new ProcessResult { FilePath = p, Old = "drake.mp3", New = "drake.mp3", Genre = "Rap/Hip Hop", Found = true };
        var soloGenero = new FieldFlags { Title = false, Artist = false, Album = false, Genre = true, Year = false, Bpm = false };
        var mf = Path.Combine(_paths.UndoDir, "run_20261001_100000.jsonl");

        await new ApplyEngine().ApplyOneAsync(info, true, soloGenero, "keep", null, mf, Path.Combine(_dir, "procesadas.log"), aplicada: true);

        Assert.Equal(new[] { "Rap", "Hip Hop" }, Generos(p));
        Assert.Equal(new[] { "Rap", "Hip Hop" }, Registro(Assert.Single(File.ReadAllLines(mf))).Fields!["Genre"].NewArr);
        var u = new UndoEngine(_paths).UndoLastRun();
        Assert.Equal((1, 0), (u.Reverted, u.Manual));
        Assert.Equal(new[] { "Hip-Hop" }, Generos(p));
    }

    // La predicción se hace con la misma biblioteca y la versión de la etiqueta del archivo: tiene
    // que coincidir con lo que se relee de verdad, sea cual sea la versión.
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void La_prediccion_coincide_con_lo_que_se_relee(byte version)
    {
        var p = Cancion("tema.mp3", "Tema");
        using (var f = TagLib.File.Create(p))
        {
            ((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true)).Version = version;
            f.Tag.Genres = new[] { "House" };
            f.Save();
        }

        string[] previstos;
        using (var f = TagLib.File.Create(p))
        {
            previstos = Relectura.Generos(f, new[] { "Dance/Electrónica", "Latin" });
            f.Tag.Genres = new[] { "Dance/Electrónica", "Latin" };
            f.Save();
        }

        Assert.Equal(Generos(p), previstos);
        if (version == 3) Assert.Equal(new[] { "Dance", "Electrónica", "Latin" }, previstos);
        Assert.True(Relectura.DebeReleerse(new[] { "Dance/Electrónica" }, new[] { "Dance/Electrónica" }));   // con barra, se comprueba siempre
        Assert.False(Relectura.DebeReleerse(new[] { "House" }, new[] { "House" }));
    }
}
