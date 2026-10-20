using System.Text.Json;
using System.Text.Json.Nodes;
using Etiquetador.App.Services;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Revisión de la tanda 3 sobre «Sustituir» en la Bandeja (equipo catalogo): deshacer una
/// sustitución devuelve a cada archivo su ficha y lo decidido (datos-2), la recuperación de una
/// sustitución cortada no borra un archivo que no es la descarga (datos-5), el historial la enseña
/// como traslado, y se pueden llevar a la descarga las etiquetas de DJ de la copia antigua.
/// </summary>
public class SustitucionDeshechaTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Ruta(params string[] partes) => Path.Combine(new[] { _dir }.Concat(partes).ToArray());

    private (bool, string) APapelera(string ruta)
    {
        var papelera = Directory.CreateDirectory(Ruta("Papelera")).FullName;
        File.Move(ruta, Path.Combine(papelera, Path.GetFileName(ruta)));
        return (true, "");
    }

    private void SacarDeLaPapelera(string ruta) => File.Move(Ruta("Papelera", Path.GetFileName(ruta)), ruta);

    private static Func<UndoRecord, string> AnotarEn(string manifiesto) => r =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifiesto)!);
        File.AppendAllText(manifiesto, JsonSerializer.Serialize(r) + "\n");
        return "";
    };

    /// <summary>Lo decidido en Enriquecer y en «Comprobar audio», como lo guarda la aplicación.</summary>
    private sealed class Decisiones
    {
        public readonly IgnoreList Aplicadas, Descartadas, Audio;
        public readonly ApplyMarks Marcas;

        public Decisiones(string dir)
        {
            Aplicadas = new IgnoreList(Path.Combine(dir, "aplicadas.json"));
            Descartadas = new IgnoreList(Path.Combine(dir, "descartadas.json"));
            Audio = new IgnoreList(Path.Combine(dir, "audio.json"));
            Marcas = new ApplyMarks(Path.Combine(dir, "marcas.json"));
        }
    }

    /// <summary>
    /// Sustituye como lo hace la Bandeja (BandejaViewModel.SustituirAsync), sin la interfaz: apunta
    /// lo que había, ejecuta, lleva la ficha, olvida lo decidido sobre la copia antigua y lo anota.
    /// </summary>
    private (SustitucionHecha Registro, string Manifiesto) Sustituir(string nueva, string vieja, AlmacenFichas fichas,
                                                                    Decisiones d, AlmacenSustituciones almacen, AppPaths paths)
    {
        var registro = new SustitucionHecha
        {
            Fecha = DateTime.Now, Bandeja = nueva, Vieja = vieja,
            FichaVieja = fichas.Obtener(vieja), FichaNueva = fichas.Obtener(nueva),
            ViejaBytes = new FileInfo(vieja).Length, NuevaBytes = new FileInfo(nueva).Length,
        };
        var man = paths.NuevoManifiesto();
        var r = SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, AnotarEn(man));
        Assert.True(r.Ok, r.Error);

        SustitucionBandeja.LlevarFicha(fichas, vieja, nueva, r.Destino);
        registro.Destino = r.Destino;
        registro.FichaPuesta = fichas.Obtener(r.Destino);
        SustitucionesBandeja.Apuntar(registro, d.Aplicadas, d.Descartadas, d.Marcas, d.Audio);
        BandejaViewModel.OlvidarLoDecididoSobre(new[] { vieja, r.Destino }, d.Aplicadas, d.Descartadas, d.Marcas, d.Audio);
        if (d.Audio.Contains(nueva)) { d.Audio.Remove(nueva); d.Audio.Add(r.Destino); }   // lo que hace SeguirArchivo
        almacen.Anotar(registro);
        return (registro, man);
    }

    /// <summary>Lo que pasa en la aplicación con cada cambio de la biblioteca: primero esto y luego la recolocación de fichas.</summary>
    private static ConciliacionSustituciones TrasCambiarLaBiblioteca(AlmacenSustituciones almacen, AlmacenFichas fichas, Decisiones d,
                                                                     IReadOnlySet<string> biblioteca, AppPaths paths)
    {
        var c = almacen.Conciliar(fichas, biblioteca.Contains, SustitucionesBandeja.Tamano, DateTime.Now);
        SustitucionesBandeja.Asentar(c, d.Aplicadas, d.Descartadas, d.Marcas, d.Audio, (de, a) =>
        {
            if (d.Audio.Contains(de)) { d.Audio.Remove(de); d.Audio.Add(a); }
        });
        AppEngine.ReubicarHuerfanas(fichas, r => biblioteca.Contains(r) || File.Exists(r),
                                    () => RekordboxRelocator.ReadRenames(paths.UndoDir), new HashSet<string>());
        return c;
    }

    // ---------------------------------------------------------------- datos-2 · de principio a fin

    // El caso de la revisión: la copia antigua tenía ficha (energía 8, «pico»); se sustituye por una
    // descarga mejor, se deshace y se recupera de la papelera. Antes la ficha fusionada acababa en la
    // descarga de la bandeja y la copia recuperada volvía sin ficha ni lo decidido sobre ella.
    [Fact]
    public void Deshacer_y_recuperar_devuelve_a_cada_archivo_su_ficha_y_lo_decidido()
    {
        var paths = new AppPaths(Ruta("datos"));
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "fisher 320.mp3");
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica", "House")).FullName, "Fisher - Losing It.mp3");
        File.WriteAllText(nueva, "nueva, de 320");
        File.WriteAllText(vieja, "vieja");
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        fichas.Poner(vieja, new FichaDj { Energia = 8, Etiquetas = new() { "pico" } });
        fichas.Poner(nueva, new FichaDj { Etiquetas = new() { "boda" } });
        var d = new Decisiones(Ruta("datos"));
        d.Aplicadas.Add(vieja);
        d.Audio.Add(vieja);
        d.Marcas.Set(EnrichViewModel.ClaveMarca(vieja, "huella"), true);
        var almacen = new AlmacenSustituciones(AlmacenSustituciones.RutaEn(Ruta("datos")));

        var (registro, man) = Sustituir(nueva, vieja, fichas, d, almacen, paths);
        Assert.Equal(vieja, registro.Destino);                  // misma extensión: la misma ruta
        Assert.Equal(8, fichas.Obtener(vieja)?.Energia);        // la fusionada, en la descarga
        Assert.False(d.Aplicadas.Contains(vieja));              // lo decidido sobre la antigua ya no vale

        // Mientras siga hecha, nada cambia aunque la biblioteca se relea.
        Assert.False(TrasCambiarLaBiblioteca(almacen, fichas, d, new HashSet<string> { vieja }, paths).HayCambios);

        // Deshacer: la descarga vuelve a la bandeja con SU ficha.
        Assert.Equal(1, new UndoEngine(paths).UndoLastRun(man).Reverted);
        var c = TrasCambiarLaBiblioteca(almacen, fichas, d, new HashSet<string>(), paths);
        Assert.Single(c.Deshechas);
        Assert.Equal(new[] { "boda" }, fichas.Obtener(nueva)?.Etiquetas);
        Assert.Equal(0, fichas.Obtener(nueva)!.Energia);
        Assert.Null(fichas.Obtener(vieja));

        // La copia antigua vuelve de la papelera: recupera su ficha y lo decidido sobre ella.
        SacarDeLaPapelera(vieja);
        c = TrasCambiarLaBiblioteca(almacen, fichas, d, new HashSet<string> { vieja }, paths);
        Assert.Single(c.Recuperadas);
        Assert.Equal(8, fichas.Obtener(vieja)?.Energia);
        Assert.Equal(new[] { "pico" }, fichas.Obtener(vieja)?.Etiquetas);
        Assert.Equal(new[] { "boda" }, fichas.Obtener(nueva)?.Etiquetas);
        Assert.True(d.Aplicadas.Contains(vieja));
        Assert.True(d.Audio.Contains(vieja));
        Assert.Equal(true, d.Marcas.Get(EnrichViewModel.ClaveMarca(vieja, "huella")));
        Assert.Equal(0, almacen.Count);                         // ya no queda nada que vigilar
    }

    [Fact]
    public void Con_otra_extension_tambien()
    {
        var paths = new AppPaths(Ruta("datos"));
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "fisher.aiff");
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica")).FullName, "Fisher - Losing It.mp3");
        File.WriteAllText(nueva, "nueva");
        File.WriteAllText(vieja, "vieja");
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        fichas.Poner(vieja, new FichaDj { Energia = 6 });
        var d = new Decisiones(Ruta("datos"));
        var almacen = new AlmacenSustituciones(AlmacenSustituciones.RutaEn(Ruta("datos")));

        var (registro, man) = Sustituir(nueva, vieja, fichas, d, almacen, paths);
        Assert.Equal(Path.ChangeExtension(vieja, ".aiff"), registro.Destino);

        Assert.Equal(1, new UndoEngine(paths).UndoLastRun(man).Reverted);
        TrasCambiarLaBiblioteca(almacen, fichas, d, new HashSet<string>(), paths);
        Assert.Null(fichas.Obtener(nueva));                     // la descarga no tenía ficha: no se lleva la fusionada
        Assert.Null(fichas.Obtener(registro.Destino));

        SacarDeLaPapelera(vieja);
        TrasCambiarLaBiblioteca(almacen, fichas, d, new HashSet<string> { vieja }, paths);
        Assert.Equal(6, fichas.Obtener(vieja)?.Energia);
    }

    // Lo que el DJ cambió en la ficha DESPUÉS de sustituir se hizo escuchando la descarga: se queda con ella.
    [Fact]
    public void Lo_cambiado_tras_sustituir_se_queda_con_la_descarga()
    {
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        var s = new SustitucionHecha
        {
            Bandeja = Ruta("Bandeja", "a.mp3"), Destino = Ruta("Musica", "A.mp3"),
            FichaNueva = new FichaDj { Energia = 3 }, FichaPuesta = new FichaDj { Energia = 7 },
        };
        fichas.Poner(s.Destino, new FichaDj { Energia = 9 });

        AlmacenSustituciones.DevolverFichaALaDescarga(fichas, s);

        Assert.Equal(9, fichas.Obtener(s.Bandeja)?.Energia);
        Assert.Null(fichas.Obtener(s.Destino));
    }

    // Si la recolocación de huérfanas ya se llevó la fusionada a la bandeja, se corrige igual.
    [Fact]
    public void Si_la_ficha_ya_habia_seguido_a_la_descarga_se_corrige()
    {
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        var s = new SustitucionHecha
        {
            Bandeja = Ruta("Bandeja", "a.mp3"), Destino = Ruta("Musica", "A.mp3"),
            FichaNueva = new FichaDj { Energia = 3 }, FichaPuesta = new FichaDj { Energia = 7 },
        };
        fichas.Poner(s.Bandeja, new FichaDj { Energia = 7 });

        AlmacenSustituciones.DevolverFichaALaDescarga(fichas, s);

        Assert.Equal(3, fichas.Obtener(s.Bandeja)?.Energia);
    }

    // En la ruta de la copia antigua aparece OTRO archivo (otro tamaño): no hereda nada de ella.
    [Fact]
    public void Otro_archivo_en_la_ruta_de_la_copia_antigua_no_recibe_su_ficha()
    {
        var vieja = Ruta("Musica", "A.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(vieja)!);
        File.WriteAllText(vieja, "otro archivo, más largo");
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        var almacen = new AlmacenSustituciones(Ruta("sust.json"));
        almacen.Anotar(new SustitucionHecha
        {
            Vieja = vieja, Destino = vieja, Bandeja = Ruta("Bandeja", "a.mp3"), ViejaBytes = 3,
            FichaVieja = new FichaDj { Energia = 8 }, Deshecha = DateTime.Now,
        });

        var c = almacen.Conciliar(fichas, _ => true, SustitucionesBandeja.Tamano, DateTime.Now);

        Assert.Equal(1, c.Olvidadas);
        Assert.Empty(c.Recuperadas);
        Assert.Null(fichas.Obtener(vieja));
        Assert.Equal(0, almacen.Count);
    }

    [Fact]
    public void Una_copia_antigua_que_no_vuelve_se_deja_de_esperar()
    {
        var almacen = new AlmacenSustituciones(Ruta("sust.json"));
        almacen.Anotar(new SustitucionHecha { Vieja = Ruta("x.mp3"), Destino = Ruta("x.mp3"), Deshecha = DateTime.Now.AddDays(-200) });

        var c = almacen.Conciliar(new AlmacenFichas(Ruta("fichas.json")), _ => false, _ => null, DateTime.Now);

        Assert.Equal(1, c.Olvidadas);
        Assert.Contains(SustitucionesBandeja.TextosRegistro(c), t => t.Contains("se deja de vigilar 1 sustitución"));
    }

    // Mientras la descarga no vuelva a la bandeja (con su tamaño), la sustitución sigue hecha: un
    // renombrado posterior del destino no la da por deshecha.
    [Fact]
    public void Sin_la_descarga_en_la_bandeja_no_se_da_por_deshecha()
    {
        var bandeja = Ruta("Bandeja", "a.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(bandeja)!);
        File.WriteAllText(bandeja, "otra descarga con el mismo nombre");
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        fichas.Poner(Ruta("Musica", "A.mp3"), new FichaDj { Energia = 7 });
        var almacen = new AlmacenSustituciones(Ruta("sust.json"));
        almacen.Anotar(new SustitucionHecha { Bandeja = bandeja, Destino = Ruta("Musica", "A.mp3"), Vieja = Ruta("Musica", "A.mp3"), NuevaBytes = 5 });

        Assert.False(almacen.Conciliar(fichas, _ => false, SustitucionesBandeja.Tamano, DateTime.Now).HayCambios);
        Assert.Equal(7, fichas.Obtener(Ruta("Musica", "A.mp3"))?.Energia);
        Assert.Equal(1, almacen.Count);
    }

    // ---------------------------------------------------------------- catalogo-3 y catalogo-4 · el historial manda, no el tamaño

    // Se sustituye, se aplica la propuesta de Enriquecer (con carátula, otro tamaño) y se deshacen las
    // dos. Antes se reconocía por el tamaño de la descarga en la bandeja, y así no se reconocía: la
    // ficha fusionada se iba con la descarga. Ahora lo dice el propio deshacer (UndoEngine.AlDeshacer,
    // que en la aplicación llega a AppEngine.SeguirArchivo).
    [Fact]
    public void El_deshacer_se_reconoce_por_el_historial_aunque_la_descarga_vuelva_con_otro_tamano()
    {
        var paths = new AppPaths(Ruta("datos"));
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "fisher 320.mp3");
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica")).FullName, "Fisher - Losing It.mp3");
        File.WriteAllText(nueva, "nueva, de 320");
        File.WriteAllText(vieja, "vieja");
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        fichas.Poner(vieja, new FichaDj { Energia = 8 });
        fichas.Poner(nueva, new FichaDj { Energia = 2 });
        var d = new Decisiones(Ruta("datos"));
        var almacen = new AlmacenSustituciones(AlmacenSustituciones.RutaEn(Ruta("datos")));
        var (registro, man) = Sustituir(nueva, vieja, fichas, d, almacen, paths);

        File.AppendAllText(registro.Destino, " + carátula");   // Enriquecer escribe en la descarga: otro tamaño
        var deshacer = new UndoEngine(paths) { AlDeshacer = a => almacen.Seguir(a.Desde, a.Hasta) };
        Assert.Equal(1, deshacer.UndoLastRun(man).Reverted);
        Assert.NotEqual(registro.NuevaBytes, new FileInfo(nueva).Length);

        var c = TrasCambiarLaBiblioteca(almacen, fichas, d, new HashSet<string>(), paths);

        Assert.Single(c.Deshechas);
        Assert.Equal(2, fichas.Obtener(nueva)?.Energia);   // la descarga, con SU ficha
        Assert.Null(fichas.Obtener(vieja));
    }

    // El destino sigue a los renombrados de BeatTag: tras «Enriquecer» renombra la descarga, sigue en
    // la biblioteca y no hay que mirar el disco con cada cambio de ella. Deshacer el renombrado y luego
    // la sustitución se sigue igual.
    [Fact]
    public void El_destino_sigue_a_los_renombrados_y_no_se_mira_el_disco()
    {
        var almacen = new AlmacenSustituciones(Ruta("sust.json"));
        var s = new SustitucionHecha
        {
            Fecha = DateTime.Now, Bandeja = Ruta("Bandeja", "a.mp3"), Destino = Ruta("Musica", "A.mp3"),
            Vieja = Ruta("Musica", "A.mp3"), NuevaBytes = 5,
        };
        almacen.Anotar(s);
        long? SinDisco(string r) => throw new InvalidOperationException("no debería mirar el disco: " + r);

        Assert.True(almacen.Seguir(Ruta("Musica", "A.mp3"), Ruta("Musica", "Fisher - A.mp3")));   // Enriquecer renombra
        Assert.Equal(Ruta("Musica", "Fisher - A.mp3"), s.Destino);
        Assert.False(almacen.Seguir(Ruta("Otra", "x.mp3"), Ruta("Otra", "y.mp3")));
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        Assert.False(almacen.Conciliar(fichas, r => r == Ruta("Musica", "Fisher - A.mp3"), SinDisco, DateTime.Now).HayCambios);

        almacen.Seguir(Ruta("Musica", "Fisher - A.mp3"), Ruta("Musica", "A.mp3"));   // se deshace el renombrado…
        almacen.Seguir(Ruta("Musica", "A.mp3"), Ruta("Bandeja", "a.mp3"));            // …y la sustitución
        Assert.True(s.VueltaALaBandeja);
        var c = almacen.Conciliar(fichas, _ => false, SinDisco, DateTime.Now);
        Assert.Single(c.Deshechas);
        Assert.False(almacen.Seguir(Ruta("Musica", "A.mp3"), Ruta("Bandeja", "a.mp3")));   // deshecha: ya no se sigue
    }

    // Con las etiquetas copiadas no hay tamaño con que reconocerla (NuevaBytes = 0): antes bastaba con
    // que llegara a la bandeja OTRA descarga con su nombre para darla por deshecha sin serlo.
    [Fact]
    public void Sin_tamano_fiable_otra_descarga_con_su_nombre_no_la_da_por_deshecha()
    {
        var bandeja = Ruta("Bandeja", "a.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(bandeja)!);
        File.WriteAllText(bandeja, "otra descarga con el mismo nombre");
        var almacen = new AlmacenSustituciones(Ruta("sust.json"));
        almacen.Anotar(new SustitucionHecha { Fecha = DateTime.Now, Bandeja = bandeja, Destino = Ruta("Musica", "A.mp3"), Vieja = Ruta("Musica", "A.mp3") });

        Assert.False(almacen.Conciliar(new AlmacenFichas(Ruta("fichas.json")), _ => false, SustitucionesBandeja.Tamano, DateTime.Now).HayCambios);
        Assert.Equal(1, almacen.Count);
    }

    // Una hecha cuyo destino ha salido de la biblioteca por otra vía se mira en disco una vez por
    // sesión, no con cada cambio de la biblioteca; y pasado el plazo se olvida.
    [Fact]
    public void Una_hecha_fuera_de_la_biblioteca_se_mira_una_vez_y_acaba_caducando()
    {
        var almacen = new AlmacenSustituciones(Ruta("sust.json"));
        almacen.Anotar(new SustitucionHecha
        {
            Fecha = DateTime.Now, Bandeja = Ruta("Bandeja", "a.mp3"), Destino = Ruta("Musica", "A.mp3"), Vieja = Ruta("Musica", "A.mp3"), NuevaBytes = 5,
        });
        var fichas = new AlmacenFichas(Ruta("fichas.json"));
        var miradas = 0;
        long? Contar(string r) { miradas++; return null; }

        for (var i = 0; i < 5; i++) Assert.False(almacen.Conciliar(fichas, _ => false, Contar, DateTime.Now).HayCambios);
        Assert.Equal(2, miradas);   // una sola vez el destino y la bandeja; antes, los dos con cada cambio (10)

        var c = almacen.Conciliar(fichas, _ => false, Contar, DateTime.Now + AlmacenSustituciones.EsperaCopiaAntigua + TimeSpan.FromDays(1));
        Assert.Equal(1, c.Olvidadas);
        Assert.Equal(0, almacen.Count);
    }

    [Fact]
    public void Lo_apuntado_se_guarda_y_se_lee()
    {
        var archivo = Ruta("datos", "bandeja-sustituidas.json");
        var almacen = new AlmacenSustituciones(archivo);
        var s = new SustitucionHecha
        {
            Fecha = new DateTime(2026, 10, 20, 12, 0, 0), Bandeja = "b", Destino = "d", Vieja = "v", ViejaBytes = 10, NuevaBytes = 20,
            FichaVieja = new FichaDj { Energia = 8, Etiquetas = new() { "pico" } }, Aplicada = true,
        };
        s.Casillas["v\u001Fh"] = false;
        almacen.Anotar(s);
        Assert.Equal("", almacen.Guardar());

        var leido = Assert.Single(new AlmacenSustituciones(archivo).Todas);

        Assert.Equal(("b", "d", "v", 10L, 20L, true), (leido.Bandeja, leido.Destino, leido.Vieja, leido.ViejaBytes, leido.NuevaBytes, leido.Aplicada));
        Assert.Equal(8, leido.FichaVieja?.Energia);
        Assert.False(leido.Casillas["v\u001Fh"]);
    }

    [Fact]
    public void Se_recuerdan_como_mucho_las_ultimas()
    {
        var almacen = new AlmacenSustituciones(Ruta("sust.json"));
        for (var i = 0; i < AlmacenSustituciones.Maximo + 5; i++) almacen.Anotar(new SustitucionHecha { Vieja = $"v{i}" });
        Assert.Equal(AlmacenSustituciones.Maximo, almacen.Count);
        Assert.Equal("v5", almacen.Todas[0].Vieja);
    }

    // Lo decidido de nuevo sobre la copia antigua mientras estaba en la papelera no se pisa.
    [Fact]
    public void Devolver_lo_decidido_no_pisa_lo_nuevo()
    {
        var d = new Decisiones(_dir);
        var s = new SustitucionHecha { Vieja = Ruta("v.mp3"), Aplicada = true };
        s.Casillas[EnrichViewModel.ClaveMarca(s.Vieja, "h")] = true;
        d.Marcas.Set(EnrichViewModel.ClaveMarca(s.Vieja, "h"), false);

        Assert.True(SustitucionesBandeja.Devolver(s, d.Aplicadas, d.Descartadas, d.Marcas, d.Audio));
        Assert.True(d.Aplicadas.Contains(s.Vieja));
        Assert.Equal(false, d.Marcas.Get(EnrichViewModel.ClaveMarca(s.Vieja, "h")));
        Assert.False(SustitucionesBandeja.Devolver(s, d.Aplicadas, d.Descartadas, d.Marcas, d.Audio));   // ya estaba todo
    }

    [Fact]
    public void Si_no_se_puede_guardar_lo_apuntado_se_dice()
        => Assert.Contains("la ficha se quedará con la descarga: disco lleno",
                           BandejaViewModel.TextoSustituida("A.mp3", Ruta("A.mp3"), "", "", "", "disco lleno"));

    [Fact]
    public void La_confirmacion_dice_que_pasa_al_deshacer()
    {
        var nueva = new Track { FilePath = Ruta("Bandeja", "a.mp3"), Bitrate = 320, DurationSeconds = 200 };
        var vieja = new Track { FilePath = Ruta("Musica", "A.mp3"), Bitrate = 128, DurationSeconds = 200 };
        var (_, nota) = SustitucionBandeja.TextosConfirmacion(nueva, vieja);
        Assert.Contains("la descarga vuelve a la bandeja con su propia ficha de DJ", nota);
        Assert.Contains("Serato guarda los cue points y la cuadrícula dentro del propio archivo", nota);
    }

    // ---------------------------------------------------------------- Origen

    // El historial de deshacer enseña la sustitución con su propio nombre, «Sustitución desde la
    // Bandeja», y no como un traslado de carpeta, aunque la descarga salga de la bandeja.
    [Fact]
    public void El_historial_la_ensena_como_sustitucion()
    {
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "a.mp3");
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica")).FullName, "A.mp3");
        File.WriteAllText(nueva, "n");
        File.WriteAllText(vieja, "v");
        var man = Ruta("deshacer", "run.jsonl");

        Assert.True(SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, AnotarEn(man)).Ok);

        var rec = JsonSerializer.Deserialize<UndoRecord>(Assert.Single(File.ReadAllLines(man)))!;
        Assert.Equal(nameof(TipoEjecucion.Sustitucion), rec.Origen);
        Assert.Equal(TipoEjecucion.Sustitucion,
                     HistorialDeshacer.DeducirTipo(File.ReadAllLines(man).Select(l => JsonNode.Parse(l)).ToList()));
    }

    // ---------------------------------------------------------------- datos-5 · la sustitución cortada

    [Theory]
    [InlineData(100, 100, 100, true)]    // la propia descarga en la bandeja, el temporal entero
    [InlineData(100, 100, 40, true)]     // una copia entre discos que no llegó a terminar
    [InlineData(100, 90, 100, false)]    // en la bandeja hay OTRO archivo con su nombre
    [InlineData(100, 100, 120, false)]   // el temporal es mayor que la descarga: no es una copia a medias de ella
    [InlineData(0, 100, 40, true)]       // lo apuntado antes de guardar el tamaño: como antes
    public void El_temporal_solo_sobra_si_en_la_bandeja_esta_la_propia_descarga(long apuntado, long enBandeja, long temporal, bool sobra)
        => Assert.Equal(sobra, SustitucionBandeja.TemporalSobra(apuntado, enBandeja, temporal));

    // El caso de la revisión: tras un renombrado completo en el mismo disco, la descarga entera es el
    // temporal; el usuario la vio «desaparecer» y bajó otra con el mismo nombre. Antes el temporal se
    // borraba sin papelera. Ahora vuelve a la bandeja con otro nombre y la otra no se toca.
    [Fact]
    public void Si_en_la_bandeja_hay_otro_archivo_con_su_nombre_la_descarga_vuelve_como_recuperada()
    {
        var bandeja = Directory.CreateDirectory(Ruta("Bandeja")).FullName;
        var musica = Directory.CreateDirectory(Ruta("Musica")).FullName;
        var nueva = Path.Combine(bandeja, "tema.mp3");
        var vieja = Path.Combine(musica, "Tema.mp3");
        File.WriteAllText(vieja, "vieja");
        var temporal = Path.Combine(musica, ".beattag-sustituir-0123.tmp");
        File.WriteAllText(temporal, "la descarga de verdad");
        File.WriteAllText(nueva, "otra");   // otro tamaño
        var diario = Directory.CreateDirectory(Ruta("datos", "bandeja-sustituciones")).FullName;
        File.WriteAllText(Path.Combine(diario, "0123.json"), JsonSerializer.Serialize(new SustitucionBandeja.DiarioSustitucion
        {
            Temporal = temporal, Bandeja = nueva, Vieja = vieja, Bytes = "la descarga de verdad".Length,
        }));
        Assert.True(SustitucionBandeja.HayQueRecuperar(diario));

        var aviso = Assert.Single(SustitucionBandeja.Recuperar(diario));

        Assert.Contains("«tema (recuperada).mp3»", aviso);
        Assert.Equal("la descarga de verdad", File.ReadAllText(Path.Combine(bandeja, "tema (recuperada).mp3")));
        Assert.Equal("otra", File.ReadAllText(nueva));
        Assert.False(File.Exists(temporal));
        Assert.False(SustitucionBandeja.HayQueRecuperar(diario));   // ya no queda nada apuntado
    }

    [Fact]
    public void Sin_nada_apuntado_no_hay_que_recuperar()
    {
        Assert.False(SustitucionBandeja.HayQueRecuperar(Ruta("no-existe")));
        Assert.False(SustitucionBandeja.HayQueRecuperar(Directory.CreateDirectory(Ruta("vacia")).FullName));
    }

    // ---------------------------------------------------------------- copiar las etiquetas de DJ

    private static void Etiquetar(string ruta, string? comentario, string? tonalidad, uint bpm)
    {
        using var f = TagLib.File.Create(ruta);
        f.Tag.Comment = comentario;
        f.Tag.InitialKey = tonalidad;
        f.Tag.BeatsPerMinute = bpm;
        f.Save();
    }

    [Fact]
    public void Solo_se_ofrece_copiar_lo_que_la_descarga_no_tiene_igual()
    {
        var vieja = new EtiquetasDj("Pico de la noche", "8A", 124);
        Assert.Equal(new EtiquetasDj("Pico de la noche", "", 124), SustitucionBandeja.QueCopiar(vieja, new EtiquetasDj("Promo del pool", "8a", 0)));
        Assert.True(SustitucionBandeja.QueCopiar(vieja, vieja).Vacias);
        Assert.True(SustitucionBandeja.QueCopiar(null, vieja).Vacias);                       // la copia antigua no se pudo leer
        Assert.True(SustitucionBandeja.QueCopiar(vieja, null).Vacias);                       // la descarga no se puede escribir
        Assert.True(SustitucionBandeja.QueCopiar(new EtiquetasDj("", "", 0), new EtiquetasDj("x", "1A", 90)).Vacias);
    }

    [Fact]
    public void Se_describe_lo_que_se_copia()
    {
        Assert.Equal("el comentario, la tonalidad (8A) y el BPM (124)", SustitucionBandeja.DescribirCopia(new EtiquetasDj("x", "8A", 124)));
        Assert.Equal("la tonalidad (8A) y el BPM (124)", SustitucionBandeja.DescribirCopia(new EtiquetasDj("", "8A", 124)));
        Assert.Equal("el comentario", SustitucionBandeja.DescribirCopia(new EtiquetasDj("x", "", 0)));
    }

    [Fact]
    public void La_confirmacion_ofrece_copiarlas_solo_si_hay_algo()
    {
        var nueva = new Track { FilePath = Ruta("Bandeja", "a.mp3"), Bitrate = 320, DurationSeconds = 200 };
        var vieja = new Track { FilePath = Ruta("Musica", "A.mp3"), Bitrate = 128, DurationSeconds = 200 };
        Assert.DoesNotContain(SustitucionBandeja.BotonCopiar, SustitucionBandeja.TextosConfirmacion(nueva, vieja).Cuerpo);
        var (cuerpo, _) = SustitucionBandeja.TextosConfirmacion(nueva, vieja, new EtiquetasDj("x", "8A", 0));
        Assert.Contains($"Con «{SustitucionBandeja.BotonCopiar}», pasan además a la descarga el comentario y la tonalidad (8A)", cuerpo);
        Assert.Contains("la puntuación y el color, no", cuerpo);
    }

    // De principio a fin con archivos de verdad: sustituir copiando, comprobar y deshacer.
    [Fact]
    public void Copiar_las_etiquetas_de_dj_se_deshace_con_la_sustitucion()
    {
        var paths = new AppPaths(Ruta("datos"));
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "fisher 320.mp3");
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica")).FullName, "Fisher - Losing It.mp3");
        Mp3Fixture.WriteMinMp3(nueva);
        Mp3Fixture.WriteMinMp3(vieja);
        Etiquetar(vieja, "Pico de la noche", "8A", 124);
        Etiquetar(nueva, "Descargado de un pool", null, 0);
        var copiar = SustitucionBandeja.QueCopiar(SustitucionBandeja.LeerEtiquetasDj(vieja), SustitucionBandeja.LeerEtiquetasDj(nueva));
        var man = paths.NuevoManifiesto();

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, APapelera, AnotarEn(man));
        var (copiadas, error) = SustitucionBandeja.CopiarEtiquetasDj(r.Destino, copiar, AnotarEn(man));

        Assert.Equal(("", 3), (error, copiadas));
        Assert.Equal(new EtiquetasDj("Pico de la noche", "8A", 124), SustitucionBandeja.LeerEtiquetasDj(vieja));   // la descarga, en su sitio
        Assert.Equal(2, File.ReadAllLines(man).Length);

        // Deshacer quita primero las etiquetas y después devuelve la descarga a la bandeja, tal como llegó.
        var u = new UndoEngine(paths).UndoLastRun(man);
        Assert.Equal(2, u.Reverted);
        Assert.Equal(new EtiquetasDj("Descargado de un pool", "", 0), SustitucionBandeja.LeerEtiquetasDj(nueva));
        Assert.False(File.Exists(vieja));
    }

    // Lo que no se puede anotar no se escribe: la descarga se queda con las suyas.
    [Fact]
    public void Si_no_se_puede_anotar_no_se_copia_nada()
    {
        var ruta = Ruta("a.mp3");
        Mp3Fixture.WriteMinMp3(ruta);
        Etiquetar(ruta, "suyo", null, 0);

        var (copiadas, error) = SustitucionBandeja.CopiarEtiquetasDj(ruta, new EtiquetasDj("de la vieja", "8A", 0), _ => "disco lleno");

        Assert.Equal(0, copiadas);
        Assert.Contains("disco lleno", error);
        Assert.Contains("no se ha tocado la descarga", error);
        Assert.Equal(new EtiquetasDj("suyo", "", 0), SustitucionBandeja.LeerEtiquetasDj(ruta));
    }

    [Fact]
    public void El_estado_dice_que_se_ha_copiado()
    {
        var pedidas = new EtiquetasDj("x", "8A", 0);
        Assert.Equal("", BandejaViewModel.TextoCopiadas(null, 0, ""));
        Assert.Equal(" Se han copiado a la descarga el comentario y la tonalidad (8A) de la copia antigua.",
                     BandejaViewModel.TextoCopiadas(pedidas, 2, ""));
        Assert.Equal(" Se ha copiado a la descarga el comentario de la copia antigua.",
                     BandejaViewModel.TextoCopiadas(new EtiquetasDj("x", "", 0), 1, ""));
        Assert.Equal(" Se ha copiado a la descarga 1 de las 2 etiquetas de DJ de la copia antigua; el formato de la descarga no admite el resto.",
                     BandejaViewModel.TextoCopiadas(pedidas, 1, ""));
        Assert.StartsWith(" ⚠ No se han copiado las etiquetas de DJ", BandejaViewModel.TextoCopiadas(pedidas, 0, "es un AIFF-C"));
    }
}
