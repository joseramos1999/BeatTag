using System.Text.Json;
using Etiquetador.App.ViewModels;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Lo que encontró la revisión de X15 e I14 en la Bandeja: descartar todas las copias de una canción
/// que no está en la biblioteca, una descarga sustituida que Enriquecer no volvía a proponer, la
/// confirmación de «Sustituir» y la descarga que se quedaba oculta si la aplicación se cerraba a mitad.
/// </summary>
public class BandejaRevisionTests : IDisposable
{
    private readonly string _dir = Mp3Fixture.NewTempDir();
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Ruta(params string[] partes) => Path.Combine(new[] { _dir }.Concat(partes).ToArray());

    private Track Tema(string nombre, int kbps = 320, string artista = "Fisher", string titulo = "Losing It") => new()
    {
        FilePath = Ruta("Bandeja", nombre), Artist = artista, Title = titulo, Bitrate = kbps, DurationSeconds = 200,
    };

    /// <summary>Una fila de la bandeja como la ve la página: la canción y sus avisos, revisados de verdad.</summary>
    private sealed record Fila(Track Track, IReadOnlyList<AvisoBandeja> Avisos);

    private static List<Fila> Revisar(IEnumerable<Track> bandeja, params Track[] biblioteca)
    {
        var lista = bandeja.ToList();
        var indiceBandeja = new IndiceCanciones(lista);
        var indiceBiblioteca = new IndiceCanciones(biblioteca);
        return lista.Select(t => new Fila(t, RevisionBandeja.Revisar(t, indiceBiblioteca, indiceBandeja, "", _ => false))).ToList();
    }

    private static PlanDescarte<Fila> Planear(IReadOnlyList<Fila> seleccion, IEnumerable<Fila> bandeja)
        => RevisionBandeja.PlanearDescarte(seleccion, bandeja, f => f.Track,
                                           f => f.Avisos.Any(a => a.Tipo == TipoAviso.YaEnBiblioteca));

    private static string[] Nombres(IEnumerable<Fila> filas) => filas.Select(f => f.Track.FileName).ToArray();

    // ---------------------------------------------------------------- X15 · conservar una copia

    // El caso de la revisión: la descarga doble del navegador. Las dos salen «Repetida en la bandeja»,
    // entran en el filtro «Con aviso de duplicado» y, con Ctrl+A, iban las dos a la papelera.
    [Fact]
    public void De_la_descarga_doble_se_conserva_una_copia()
    {
        var filas = Revisar(new[] { Tema("Tema.mp3"), Tema("Tema (1).mp3") });
        Assert.All(filas, f => Assert.True(RevisionBandeja.TieneAvisoDeDuplicado(f.Avisos)));   // las dos en el filtro

        var plan = Planear(filas, filas);

        Assert.Equal(new[] { "Tema (1).mp3" }, Nombres(plan.Descartar));
        Assert.Equal(new[] { "Tema.mp3" }, Nombres(plan.Conservar));   // a igual calidad, el nombre original
        // Y el diálogo lo dice, en lugar de dar a entender que no se pierde nada.
        var cuerpo = RevisionBandeja.CuerpoDescartar(plan.Descartar.Count, 1, 0, plan.Conservar.Count);
        Assert.StartsWith("La descarga ya está en la biblioteca o repetida en la bandeja.", cuerpo);
        Assert.Contains("la de mejor calidad se queda en la bandeja", cuerpo);
    }

    [Fact]
    public void Se_conserva_la_de_mas_calidad()
    {
        var filas = Revisar(new[] { Tema("Fisher - Losing It (320).mp3", 320), Tema("Fisher - Losing It.mp3", 128),
                                    Tema("Fisher - Losing It.aiff", 1411) });
        var plan = Planear(filas, filas);
        Assert.Equal(new[] { "Fisher - Losing It.aiff" }, Nombres(plan.Conservar));
        Assert.Equal(2, plan.Descartar.Count);
    }

    // Si la canción ya está en la biblioteca, ahí queda una copia: se descartan todas las de la bandeja.
    [Fact]
    public void Si_esta_en_la_biblioteca_se_descartan_todas()
    {
        var filas = Revisar(new[] { Tema("Tema.mp3"), Tema("Tema (1).mp3") }, new Track { FilePath = Ruta("Musica", "x.mp3"), Artist = "Fisher", Title = "Losing It" });
        var plan = Planear(filas, filas);
        Assert.Equal(2, plan.Descartar.Count);
        Assert.Empty(plan.Conservar);
    }

    // Una copia que no está seleccionada sigue en la bandeja: no hace falta conservar otra.
    [Fact]
    public void Si_alguna_copia_queda_fuera_de_la_seleccion_se_descarta_lo_seleccionado()
    {
        var filas = Revisar(new[] { Tema("Tema.mp3"), Tema("Tema (1).mp3"), Tema("Tema (2).mp3") });
        var plan = Planear(filas.Skip(1).ToList(), filas);
        Assert.Equal(new[] { "Tema (1).mp3", "Tema (2).mp3" }, Nombres(plan.Descartar));
        Assert.Empty(plan.Conservar);
    }

    // Lo que no está repetido se descarta si se pide: el diálogo ya avisa de que no consta que la tengas.
    [Fact]
    public void Una_cancion_sin_repetir_no_se_conserva()
    {
        var filas = Revisar(new[] { Tema("Tema.mp3"), Tema("Otra.mp3", titulo: "Stop It"), Tema("Sin datos.mp3", artista: "") });
        var plan = Planear(filas, filas);
        Assert.Equal(3, plan.Descartar.Count);
        Assert.Empty(plan.Conservar);
    }

    [Fact]
    public void Con_varias_canciones_se_conserva_una_de_cada()
    {
        var filas = Revisar(new[]
        {
            Tema("A.mp3", titulo: "Uno"), Tema("A (1).mp3", titulo: "Uno"),
            Tema("B.mp3", titulo: "Dos"), Tema("B (1).mp3", titulo: "Dos"), Tema("B - copia.mp3", titulo: "Dos"),
        });
        var plan = Planear(filas, filas);
        Assert.Equal(new[] { "A.mp3", "B.mp3" }, Nombres(plan.Conservar).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(3, plan.Descartar.Count);
        Assert.Contains("De 2 canciones repetidas", RevisionBandeja.TextoConservadas(2));
        Assert.Contains("de 2 canciones repetidas", RevisionBandeja.TextoConservadasTrasDescartar(2));
        Assert.Equal("", RevisionBandeja.TextoConservadas(0));
        Assert.DoesNotContain("se queda", RevisionBandeja.CuerpoDescartar(2, 2));
    }

    // ---------------------------------------------------------------- I14 · Enriquecer vuelve a proponerla

    // El caso de la revisión: MP3 de 128 por MP3 de 320, misma ruta. Esa ruta estaba en aplicadas.json
    // y Enriquecer la saltaba incluso con «Reanalizar todo», con las etiquetas de la tienda puestas.
    [Fact]
    public void Lo_decidido_sobre_la_copia_antigua_no_vale_para_la_descarga()
    {
        var destino = Ruta("Musica", "Fisher - Losing It.mp3");
        var otra = Ruta("Musica", "Fisher - Losing It (Extended Mix).mp3");
        var aplicadas = new IgnoreList(Ruta("aplicadas.json"));
        var descartadas = new IgnoreList(Ruta("descartadas.json"));
        var aceptadas = new IgnoreList(Ruta("audio-aceptadas.json"));
        var marcas = new ApplyMarks(Ruta("marcas.json"));
        aplicadas.Add(destino); aplicadas.Add(otra);
        descartadas.Add(destino.ToUpperInvariant());
        aceptadas.Add(destino);
        marcas.Set(EnrichViewModel.ClaveMarca(destino, "0123456789abcdef"), true);
        marcas.Set(destino, false);   // casilla de una versión antigua, solo por ruta
        marcas.Set(EnrichViewModel.ClaveMarca(otra, "0123456789abcdef"), true);

        Assert.True(BandejaViewModel.OlvidarLoDecididoSobre(new[] { destino, destino }, aplicadas, descartadas, marcas, aceptadas));

        Assert.False(aplicadas.Contains(destino));   // Enriquecer ya no la salta como «ya aplicada»
        Assert.False(descartadas.Contains(destino));
        Assert.False(aceptadas.Contains(destino));
        Assert.Null(marcas.Get(EnrichViewModel.ClaveMarca(destino, "0123456789abcdef")));
        Assert.Null(marcas.Get(destino));
        // Lo de otras canciones, aunque su nombre empiece igual, no se toca.
        Assert.True(aplicadas.Contains(otra));
        Assert.True(marcas.Get(EnrichViewModel.ClaveMarca(otra, "0123456789abcdef")));

        // Se guarda y sobrevive a reiniciar la aplicación.
        Assert.Equal("", aplicadas.Save());
        Assert.False(new IgnoreList(Ruta("aplicadas.json")).Contains(destino));
        // Sin nada que olvidar, no hay nada que guardar.
        Assert.False(BandejaViewModel.OlvidarLoDecididoSobre(new[] { destino }, aplicadas, descartadas, marcas, aceptadas));
    }

    [Fact]
    public void El_estado_dice_si_no_se_pudo_guardar_lo_olvidado()
    {
        var destino = Ruta("Musica", "Fisher - Losing It.mp3");
        Assert.DoesNotContain("⚠", BandejaViewModel.TextoSustituida("a.mp3", destino, "rekordbox…", "", ""));
        Assert.Contains("⚠ No se pudo guardar que la descarga está pendiente de revisar en Enriquecer: aplicadas: disco lleno",
                        BandejaViewModel.TextoSustituida("a.mp3", destino, "rekordbox…", "", "aplicadas: disco lleno"));
    }

    // ---------------------------------------------------------------- I14 · la confirmación

    [Fact]
    public void La_confirmacion_dice_lo_que_no_pasa_a_la_descarga_y_lo_de_serato()
    {
        var (cuerpo, nota) = SustitucionBandeja.TextosConfirmacion(Tema("fisher 320.mp3"),
            new Track { FilePath = Ruta("Musica", "Fisher - Losing It.mp3"), Bitrate = 128, DurationSeconds = 200 });

        Assert.Contains("(comentario, tonalidad, puntuación, color…) no pasan", cuerpo);
        Assert.Contains("Serato guarda los cue points y la cuadrícula dentro del propio archivo", nota);
        Assert.Contains("Traktor", nota);
        Assert.Contains("rekordbox", nota);
    }

    // Con la copia antigua en una memoria USB se avisa antes de abrir el diálogo, que ya no promete
    // una papelera que no hay.
    [Fact]
    public void Sin_papelera_se_dice_antes_de_preguntar()
        => Assert.Equal("No se sustituye la copia de la biblioteca: está en una unidad extraíble (F:).",
                        BandejaViewModel.TextoSinPapelera("Está en una unidad extraíble (F:)."));

    // ---------------------------------------------------------------- I14 · si la aplicación se cierra a mitad

    private (string Nueva, string Vieja, string Diario) Preparar()
    {
        var nueva = Path.Combine(Directory.CreateDirectory(Ruta("Bandeja")).FullName, "fisher 320.mp3");
        var vieja = Path.Combine(Directory.CreateDirectory(Ruta("Musica", "House")).FullName, "Fisher - Losing It.mp3");
        File.WriteAllText(nueva, "nueva");
        File.WriteAllText(vieja, "vieja");
        return (nueva, vieja, SustitucionBandeja.CarpetaDiario(Ruta("datos")));
    }

    private (bool, string) APapelera(string ruta)
    {
        var papelera = Directory.CreateDirectory(Ruta("Papelera")).FullName;
        File.Move(ruta, Path.Combine(papelera, Path.GetFileName(ruta)));
        return (true, "");
    }

    private static int Apuntadas(string diario) => Directory.Exists(diario) ? Directory.GetFiles(diario).Length : 0;

    [Fact]
    public void Al_terminar_no_queda_nada_apuntado()
    {
        var (nueva, vieja, diario) = Preparar();
        var visto = 0;

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, p => { visto = Apuntadas(diario); return APapelera(p); }, _ => "", diario);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(1, visto);              // mientras dura, está apuntada
        Assert.Equal(0, Apuntadas(diario));
        Assert.Empty(SustitucionBandeja.Recuperar(diario));
    }

    [Fact]
    public void Si_la_papelera_se_niega_tampoco_queda_nada_apuntado()
    {
        var (nueva, vieja, diario) = Preparar();
        var r = SustitucionBandeja.Ejecutar(nueva, vieja, _ => (false, "sin papelera"), _ => "", diario);
        Assert.False(r.Ok);
        Assert.Equal("nueva", File.ReadAllText(nueva));
        Assert.Equal(0, Apuntadas(diario));
    }

    // Si no se puede apuntar, no se empieza: después no habría forma de encontrar la descarga.
    [Fact]
    public void Si_no_se_puede_apuntar_no_se_toca_nada()
    {
        var (nueva, vieja, _) = Preparar();
        var bloqueo = Ruta("datos-es-un-archivo");
        File.WriteAllText(bloqueo, "");
        var llamadas = 0;

        var r = SustitucionBandeja.Ejecutar(nueva, vieja, p => { llamadas++; return APapelera(p); }, _ => "",
                                            Path.Combine(bloqueo, "bandeja-sustituciones"));

        Assert.False(r.Ok);
        Assert.Contains("No se ha cambiado nada", r.Error);
        Assert.Equal(0, llamadas);
        Assert.Equal("nueva", File.ReadAllText(nueva));
        Assert.Equal("vieja", File.ReadAllText(vieja));
    }

    /// <summary>
    /// La aplicación se cierra entre el paso 1 y el 3: la papelera «no vuelve» (se lanza una excepción
    /// que no es de E/S y se deja el estado tal cual), como si el proceso hubiera muerto ahí.
    /// </summary>
    private string CortarEnLaPapelera(string nueva, string vieja, string diario, bool viejaEnPapelera)
    {
        string temporal = "";
        try
        {
            SustitucionBandeja.Ejecutar(nueva, vieja, _ =>
            {
                temporal = Directory.GetFiles(Path.GetDirectoryName(vieja)!, "*.tmp").Single();
                if (viejaEnPapelera) APapelera(vieja);
                // Lo que quedaría en disco: el temporal y lo apuntado. Se copia antes de que el
                // propio Ejecutar devuelva la descarga a la bandeja.
                File.Copy(temporal, temporal + ".copia");
                foreach (var f in Directory.GetFiles(diario)) File.Copy(f, f + ".copia");
                return (false, "");
            }, _ => "", diario);
        }
        finally
        {
            // Se deja como lo habría dejado el cierre: la descarga, con el nombre temporal; nada en la bandeja.
            if (File.Exists(nueva)) File.Delete(nueva);
            File.Move(temporal + ".copia", temporal, overwrite: true);
            foreach (var f in Directory.GetFiles(diario, "*.copia")) File.Move(f, f[..^".copia".Length], overwrite: true);
        }
        return temporal;
    }

    [Fact]
    public void Una_descarga_que_se_quedo_oculta_vuelve_a_la_bandeja()
    {
        var (nueva, vieja, diario) = Preparar();
        var temporal = CortarEnLaPapelera(nueva, vieja, diario, viejaEnPapelera: false);
        Assert.True(File.Exists(temporal));
        Assert.False(File.Exists(nueva));

        var avisos = SustitucionBandeja.Recuperar(diario);

        var aviso = Assert.Single(avisos);
        Assert.Contains("«fisher 320.mp3» ha vuelto a la bandeja", aviso);
        Assert.DoesNotContain("papelera", aviso);
        Assert.Equal("nueva", File.ReadAllText(nueva));
        Assert.Equal("vieja", File.ReadAllText(vieja));
        Assert.False(File.Exists(temporal));
        Assert.Equal(0, Apuntadas(diario));
        Assert.Empty(SustitucionBandeja.Recuperar(diario));   // una sola vez
    }

    [Fact]
    public void Si_la_copia_antigua_ya_estaba_en_la_papelera_se_dice()
    {
        var (nueva, vieja, diario) = Preparar();
        CortarEnLaPapelera(nueva, vieja, diario, viejaEnPapelera: true);

        var aviso = Assert.Single(SustitucionBandeja.Recuperar(diario));

        Assert.Contains("ya estaba en la papelera", aviso);
        Assert.Equal("nueva", File.ReadAllText(nueva));
    }

    // Si la carpeta de la bandeja ya no existe, la descarga recupera su nombre donde está, a la vista.
    [Fact]
    public void Sin_la_carpeta_de_la_bandeja_recupera_su_nombre_donde_esta()
    {
        var (nueva, vieja, diario) = Preparar();
        var temporal = CortarEnLaPapelera(nueva, vieja, diario, viejaEnPapelera: false);
        Directory.Delete(Ruta("Bandeja"), true);

        var aviso = Assert.Single(SustitucionBandeja.Recuperar(diario));

        var donde = Path.Combine(Path.GetDirectoryName(temporal)!, "fisher 320.mp3");
        Assert.Contains(donde, aviso);
        Assert.Equal("nueva", File.ReadAllText(donde));
    }

    // Una copia entre discos que no llegó a completarse: el original sigue en la bandeja y el temporal sobra.
    [Fact]
    public void Una_copia_a_medias_se_quita_sin_tocar_el_original()
    {
        var (nueva, vieja, diario) = Preparar();
        var temporal = Path.Combine(Path.GetDirectoryName(vieja)!, ".beattag-sustituir-0123.tmp");
        File.WriteAllText(temporal, "nue");
        Directory.CreateDirectory(diario);
        File.WriteAllText(Path.Combine(diario, "0123.json"), JsonSerializer.Serialize(
            new SustitucionBandeja.DiarioSustitucion { Temporal = temporal, Bandeja = nueva, Vieja = vieja }));

        Assert.Empty(SustitucionBandeja.Recuperar(diario));

        Assert.False(File.Exists(temporal));
        Assert.Equal("nueva", File.ReadAllText(nueva));
        Assert.Equal(0, Apuntadas(diario));
    }

    [Fact]
    public void Sin_nada_apuntado_no_hay_nada_que_recoger()
    {
        Assert.Empty(SustitucionBandeja.Recuperar(Ruta("no-existe")));
        var diario = Directory.CreateDirectory(Ruta("diario")).FullName;
        File.WriteAllText(Path.Combine(diario, "dañado.json"), "{ esto no es");
        var aviso = Assert.Single(SustitucionBandeja.Recuperar(diario));   // se aparta y se dice
        Assert.Contains("dañado.json", aviso);
    }
}
