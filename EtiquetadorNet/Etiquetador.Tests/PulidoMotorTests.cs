using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// Tanda 2, equipo «pulido_nucleo»: los detalles que dejaron los revisores de la tanda 1 en el
/// motor (recorrido de carpetas, rekordbox, deshacer, carátulas, configuración y Llavero).
/// </summary>
public class PulidoMotorTests
{
    private static string Linea(UndoRecord r) => JsonSerializer.Serialize(r);

    // ------------------------------------------------------------------ M7 · carpetas de sistema

    // Windows pone System (o ReadOnly) a las carpetas personalizadas con desktop.ini. Con
    // AttributesToSkip = Hidden | System, una carpeta de música personalizada desaparecía de la
    // biblioteca y ScanCache y AnalysisCache podaban sus entradas.
    [Theory]
    [InlineData("Reggaeton", FileAttributes.Directory | FileAttributes.System, false)]          // desktop.ini
    [InlineData("Reggaeton", FileAttributes.Directory | FileAttributes.ReadOnly, false)]
    [InlineData("Reggaeton", FileAttributes.Directory | FileAttributes.Hidden, false)]          // la ocultó el usuario
    [InlineData("Ocultas", FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System, true)]
    [InlineData("$RECYCLE.BIN", FileAttributes.Directory, true)]                                // copiada de otro disco, sin atributos
    [InlineData("System Volume Information", FileAttributes.Directory, true)]
    [InlineData(".Trashes", FileAttributes.Directory, true)]                                    // macOS
    [InlineData(".Spotlight-V100", FileAttributes.Directory, true)]
    [InlineData("#recycle", FileAttributes.Directory, true)]                                    // papelera de un Synology
    [InlineData(".AppleDouble", FileAttributes.Directory, true)]                                // «x.mp3» que no es audio
    public void Solo_se_saltan_las_carpetas_de_sistema(string nombre, FileAttributes atributos, bool saltar)
        => Assert.Equal(saltar, LibraryScanner.SaltarCarpeta(nombre, atributos));

    [WindowsFact]
    public void Una_carpeta_personalizada_con_atributo_de_sistema_sigue_en_la_biblioteca()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Mp3Fixture.NewTempDir();
        var tocadas = new List<DirectoryInfo>();
        try
        {
            var personalizada = Directory.CreateDirectory(Path.Combine(dir, "Reggaeton"));
            File.WriteAllText(Path.Combine(personalizada.FullName, "desktop.ini"), "[.ShellClassInfo]\r\nIconResource=C:\\icono.ico,0\r\n");
            Mp3Fixture.WriteMinMp3(Path.Combine(personalizada.FullName, "Bad Bunny - Titi Me Pregunto.mp3"));
            personalizada.Attributes |= FileAttributes.System;   // lo que hace Windows al personalizarla
            tocadas.Add(personalizada);

            var oculta = Directory.CreateDirectory(Path.Combine(dir, "Pendientes"));
            Mp3Fixture.WriteMinMp3(Path.Combine(oculta.FullName, "Karol G - Provenza.mp3"));
            oculta.Attributes |= FileAttributes.Hidden;
            tocadas.Add(oculta);

            var deSistema = Directory.CreateDirectory(Path.Combine(dir, "Sistema"));
            Mp3Fixture.WriteMinMp3(Path.Combine(deSistema.FullName, "basura.mp3"));
            deSistema.Attributes |= FileAttributes.Hidden | FileAttributes.System;
            tocadas.Add(deSistema);

            var papelera = Directory.CreateDirectory(Path.Combine(dir, "$RECYCLE.BIN"));
            Mp3Fixture.WriteMinMp3(Path.Combine(papelera.FullName, "$R4X2Y1Z.mp3"));

            var r = LibraryScanner.Recorrer(dir);

            Assert.True(r.Disponible);
            Assert.Equal(new[] { "Bad Bunny - Titi Me Pregunto.mp3", "Karol G - Provenza.mp3" },
                         r.Archivos.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            foreach (var d in tocadas) try { d.Attributes = FileAttributes.Directory; } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ M11 · rutas de rekordbox

    // El formato que escribía BeatTag hasta la 1.9.2 (y probablemente rekordbox) para una ruta de
    // red. Tras la tanda 1 daba «///NAS/Musica/x.mp3» en Windows.
    [Fact]
    public void Una_ruta_de_red_con_localhost_y_triple_barra_sigue_siendo_de_red()
    {
        var r = RekordboxImporter.LocationToPath("file://localhost///NAS/Musica/Daddy%20Yankee%20-%20Gasolina.mp3");
        if (OperatingSystem.IsWindows()) Assert.Equal(@"\\NAS\Musica\Daddy Yankee - Gasolina.mp3", r);
        else Assert.Equal("/NAS/Musica/Daddy Yankee - Gasolina.mp3", r);
    }

    [Fact]
    public void Una_ruta_de_red_con_cuatro_barras_tambien()
    {
        var r = RekordboxImporter.LocationToPath("file:////NAS/Musica/x.mp3");
        if (OperatingSystem.IsWindows()) Assert.Equal(@"\\NAS\Musica\x.mp3", r);
        else Assert.Equal("/NAS/Musica/x.mp3", r);
    }

    // «file://D:/x.mp3»: la unidad donde va el host. No es un servidor llamado «D:».
    [Fact]
    public void La_unidad_en_el_lugar_del_host_no_es_un_servidor()
        => Assert.Equal(@"D:\Musica\x.mp3", RekordboxImporter.LocationToPath("file://D:/Musica/x.mp3"));

    // Las rutas de red del formato nuevo siguen como estaban, y la ida y vuelta casa.
    [WindowsFact]
    public void La_ida_y_vuelta_de_una_ruta_de_red_casa_con_los_dos_formatos()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string ruta = @"\\NAS\Musica\Bad Bunny - Titi Me Pregunto.mp3";
        Assert.Equal(ruta, RekordboxImporter.LocationToPath(RekordboxRelocator.PathToLocation(ruta)));
        Assert.Equal(ruta, RekordboxImporter.LocationToPath("file://localhost///NAS/Musica/Bad%20Bunny%20-%20Titi%20Me%20Pregunto.mp3"));
    }

    // En un Mac, el PathToLocation antiguo escribía «file://localhost//Users/…»: vale lo mismo.
    [Fact]
    public void La_doble_barra_antigua_de_macOS_se_lee_bien_en_macOS()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("/Users/dj/Music/a.mp3", RekordboxImporter.LocationToPath("file://localhost//Users/dj/Music/a.mp3"));
    }

    // ------------------------------------------------------------------ E2 · pendientes para siempre

    // Un archivo enviado a la papelera después de aplicar deja su registro «ausente» para siempre:
    // el manifiesto seguía siendo «el último» y las ejecuciones anteriores no se alcanzaban.
    [Fact]
    public void Un_pendiente_que_no_vuelve_se_puede_dar_por_perdido_y_se_llega_a_la_ejecucion_anterior()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);

            // Ejecución anterior: un título.
            var anterior = Path.Combine(dir, "pista01.mp3");
            Mp3Fixture.WriteMinMp3(anterior);
            Mp3Fixture.SetTags(anterior, "Titi Me Pregunto", null, null);
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20260920_230254.jsonl"), Linea(new UndoRecord
            {
                OrigPath = anterior, FinalPath = anterior,
                Fields = new() { ["Title"] = FieldChange.Str("pista01", "Titi Me Pregunto") },
            }) + "\n");

            // La última: un renombrado de un archivo que luego se borró, y un título que sí está.
            var borrado = Path.Combine(dir, "Karol G - Provenza.mp3");
            var presente = Path.Combine(dir, "pista02.mp3");
            Mp3Fixture.WriteMinMp3(presente);
            Mp3Fixture.SetTags(presente, "Gasolina", null, null);
            var ultimo = Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl");
            File.WriteAllText(ultimo,
                Linea(new UndoRecord { OrigPath = Path.Combine(dir, "pista03.mp3"), FinalPath = borrado, Renamed = true }) + "\n" +
                Linea(new UndoRecord { OrigPath = presente, FinalPath = presente, Fields = new() { ["Title"] = FieldChange.Str("pista02", "Gasolina") } }) + "\n");

            var undo = new UndoEngine(paths);
            var primero = undo.UndoLastRun();
            Assert.Equal(1, primero.Reverted);
            Assert.Equal(1, primero.Missing);
            Assert.False(primero.SinAvance);

            // El segundo clic no avanza: lo dice, en vez de repetir lo mismo en silencio.
            var segundo = undo.UndoLastRun();
            Assert.Equal("run_20261009_210831.jsonl", segundo.File);
            Assert.True(segundo.SinAvance);
            Assert.Contains("no aparece", segundo.Aviso);
            Assert.Contains("dar por perdidos", segundo.Aviso);

            // La salida explícita.
            var perdidos = undo.DarPorPerdidos();
            Assert.True(perdidos.Ok, perdidos.Error);
            Assert.Equal("run_20261009_210831.jsonl", perdidos.Archivo);
            Assert.Equal(1, perdidos.Registros);
            Assert.False(File.Exists(ultimo));
            Assert.True(File.Exists(Path.Combine(paths.UndoDir, "run_20261009_210831.perdidos.jsonl")));

            // Y ahora sí se llega a la ejecución anterior.
            var tercero = undo.UndoLastRun();
            Assert.Equal("run_20260920_230254.jsonl", tercero.File);
            Assert.Equal("pista01", Mp3Fixture.GetTitle(anterior));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Los renombrados dados por perdidos NO se deshicieron: para rekordbox siguen vigentes (A→B).
    [Fact]
    public void Los_renombrados_dados_por_perdidos_siguen_vigentes_para_rekordbox()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var a = Path.Combine(dir, "pista03.mp3");
            var b = Path.Combine(dir, "Karol G - Provenza.mp3");
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl"),
                Linea(new UndoRecord { OrigPath = a, FinalPath = b, Renamed = true }) + "\n");

            Assert.True(new UndoEngine(paths).DarPorPerdidos().Ok);

            var mapa = RekordboxRelocator.ReadRenames(paths.UndoDir);
            Assert.Equal(b, mapa[a]);
            Assert.False(mapa.ContainsKey(b));   // ninguna vuelta B→A: no se deshizo
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Dar_por_perdidos_sin_nada_pendiente_no_hace_nada()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var r = new UndoEngine(new AppPaths(dir)).DarPorPerdidos();
            Assert.True(r.Ok);
            Assert.Equal("", r.Archivo);
            Assert.Equal(0, r.Registros);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ M3 · archivar falla

    // Todo revertido, pero el manifiesto no se deja MOVER al archivo (aquí, porque el nombre del
    // archivado está ocupado por una carpeta; en la vida real, el antivirus u OneDrive). Antes se
    // quedaba ENTERO como pendiente y un reintento volvía a aplicar la ganancia inversa.
    [Fact]
    public void Si_no_se_puede_archivar_el_manifiesto_un_reintento_no_repite_el_volumen()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var mp3 = Path.Combine(dir, "Burna Boy - Last Last.mp3");
            // Tramas con audio: con las de WriteMinMp3 (gránulos vacíos) Mp3Gain no cambia ni un byte, y
            // la prueba pasaría aunque el reintento repitiera el volumen.
            File.WriteAllBytes(mp3, Mp3GainSintetico.Tramas(30, _ => 150));
            var original = File.ReadAllBytes(mp3);
            Assert.True(Mp3Gain.Apply(mp3, -2).Ok);   // la pestaña Volumen baja 3 dB
            Assert.NotEqual(original, File.ReadAllBytes(mp3));

            var mf = Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl");
            File.WriteAllText(mf, Linea(new UndoRecord
            {
                OrigPath = mp3, FinalPath = mp3,
                Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(2, 0) },
            }) + "\n");
            Directory.CreateDirectory(Path.Combine(paths.UndoDir, "run_20261009_210831.deshecho.jsonl"));

            var undo = new UndoEngine(paths);
            var r = undo.UndoLastRun();
            Assert.Equal(1, r.Reverted);
            Assert.DoesNotContain("NO vuelvas", r.Aviso);
            Assert.Equal(original, File.ReadAllBytes(mp3));
            Assert.DoesNotContain(undo.ManifiestosPendientes(), p => File.ReadAllText(p).Trim().Length > 0);

            // El reintento no encuentra nada que repetir.
            Assert.True(undo.UndoLastRun().None);
            Assert.Equal(original, File.ReadAllBytes(mp3));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un manifiesto vacío (el resto de un archivado que no pudo terminar) no tapa a los anteriores.
    [Fact]
    public void Un_manifiesto_vacio_no_tapa_a_la_ejecucion_anterior()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var mp3 = Path.Combine(dir, "pista01.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            Mp3Fixture.SetTags(mp3, "Gasolina", null, null);
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20261001_100000.jsonl"), Linea(new UndoRecord
            {
                OrigPath = mp3, FinalPath = mp3, Fields = new() { ["Title"] = FieldChange.Str("pista01", "Gasolina") },
            }) + "\n");
            var vacio = Path.Combine(paths.UndoDir, "run_20261010_100000.jsonl");
            File.WriteAllText(vacio, "\n");

            var r = new UndoEngine(paths).UndoLastRun();

            Assert.Equal("run_20261001_100000.jsonl", r.File);
            Assert.Equal("pista01", Mp3Fixture.GetTitle(mp3));
            Assert.False(File.Exists(vacio));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ Carátula

    private static readonly byte[] PortadaPropia = Jpeg(11);
    private static readonly byte[] DelCatalogo = Jpeg(12);
    private static readonly byte[] PuestaAMano = Jpeg(13);

    private static byte[] Jpeg(byte semilla)
    {
        var b = new byte[64];
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
        for (int i = 4; i < b.Length; i++) b[i] = (byte)(semilla * 31 + i);
        return b;
    }

    private static void PonerFrontal(string mp3, byte[] datos)
    {
        using var f = TagLib.File.Create(mp3);
        f.Tag.Pictures = new TagLib.IPicture[]
        {
            new TagLib.Picture(new TagLib.ByteVector(datos)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
        };
        f.Save();
    }

    private static byte[]? Frontal(string mp3)
    {
        using var f = TagLib.File.Create(mp3);
        return f.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)?.Data.Data;
    }

    /// <summary>Lo que hace Enriquecer: sustituye la frontal por la del catálogo y anota el cambio.</summary>
    private static string AplicarCaratula(string dir, AppPaths paths, string mp3)
    {
        var log = Tagging.ApplyTags(new ProcessResult { FilePath = mp3 }, overwrite: true,
            new FieldFlags { Title = false, Artist = false, Album = false, Genre = false, Year = false, Bpm = false },
            new TagLib.Picture(new TagLib.ByteVector(DelCatalogo)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
            paths.PortadasDir);
        Directory.CreateDirectory(paths.UndoDir);
        var mf = Path.Combine(paths.UndoDir, "run_20261017_120000.jsonl");
        File.WriteAllText(mf, Linea(new UndoRecord { OrigPath = mp3, FinalPath = mp3, Fields = log }) + "\n");
        return mf;
    }

    [Fact]
    public void La_marca_de_la_caratula_lleva_la_huella_de_la_que_se_puso()
    {
        Assert.Equal(UndoEngine.CoverSustituida, UndoEngine.MarcaCover(null));
        var sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(DelCatalogo)).ToLowerInvariant();
        Assert.Equal("sustituida:" + sha1, UndoEngine.MarcaCover(DelCatalogo));
        Assert.Equal("sustituida:" + sha1, FieldChange.Portada("", DelCatalogo).NewStr);
    }

    [Fact]
    public void Deshacer_devuelve_la_caratula_si_sigue_siendo_la_de_la_aplicacion()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var mp3 = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            PonerFrontal(mp3, PortadaPropia);
            var mf = AplicarCaratula(dir, paths, mp3);
            Assert.Equal(DelCatalogo, Frontal(mp3));

            var r = new UndoEngine(paths).UndoLastRun(mf);

            Assert.Equal(1, r.Reverted);
            Assert.Equal(PortadaPropia, Frontal(mp3));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Como con los demás campos: un cambio a mano posterior a la ejecución no se pisa al deshacer.
    [Fact]
    public void Deshacer_no_pisa_una_caratula_cambiada_a_mano_despues()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var mp3 = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            PonerFrontal(mp3, PortadaPropia);
            var mf = AplicarCaratula(dir, paths, mp3);
            PonerFrontal(mp3, PuestaAMano);   // otra portada elegida después en Mp3tag

            var r = new UndoEngine(paths).UndoLastRun(mf);

            Assert.Equal(1, r.Manual);
            Assert.Equal(PuestaAMano, Frontal(mp3));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Sin carátula antes: deshacer quita la puesta, pero NO una que el usuario puso después.
    [Fact]
    public void Si_no_tenia_caratula_no_se_quita_la_que_puso_el_usuario_despues()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var mp3 = Path.Combine(dir, "pista01.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            var mf = AplicarCaratula(dir, paths, mp3);
            PonerFrontal(mp3, PuestaAMano);

            new UndoEngine(paths).UndoLastRun(mf);

            Assert.Equal(PuestaAMano, Frontal(mp3));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ Retención de portadas

    [Fact]
    public void Solo_se_borran_las_copias_que_ninguna_ejecucion_pendiente_cita_y_ya_no_son_recientes()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.PortadasDir);
            string Copia(char c, int diasAtras)
            {
                var ruta = Path.Combine(paths.PortadasDir, new string(c, 40) + ".jpg");
                File.WriteAllBytes(ruta, Jpeg((byte)c));
                File.SetLastWriteTime(ruta, DateTime.Now.AddDays(-diasAtras));
                return ruta;
            }
            var citada = Copia('a', 30);       // la cita una ejecución pendiente
            var deshecha = Copia('b', 30);     // solo la cita una ejecución ya deshecha
            var huerfana = Copia('c', 30);
            var reciente = Copia('d', 0);      // recién guardada: su línea de manifiesto puede no estar escrita aún

            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl"),
                Linea(new UndoRecord { OrigPath = "x.mp3", FinalPath = "x.mp3", Fields = new() { [UndoEngine.CoverField] = FieldChange.Portada(citada) } }) + "\n");
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20261001_100000.deshecho.jsonl"),
                Linea(new UndoRecord { OrigPath = "y.mp3", FinalPath = "y.mp3", Fields = new() { [UndoEngine.CoverField] = FieldChange.Portada(deshecha) } }) + "\n");

            var borradas = new UndoEngine(paths).PodarPortadas();

            Assert.Equal(2, borradas);
            Assert.True(File.Exists(citada));
            Assert.True(File.Exists(reciente));
            Assert.False(File.Exists(deshecha));
            Assert.False(File.Exists(huerfana));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si un manifiesto pendiente no se puede leer no se sabe qué cita: no se borra nada.
    [WindowsFact]
    public void Si_un_manifiesto_pendiente_no_se_puede_leer_no_se_borra_ninguna_copia()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.PortadasDir);
            var huerfana = Path.Combine(paths.PortadasDir, new string('c', 40) + ".jpg");
            File.WriteAllBytes(huerfana, Jpeg(1));
            File.SetLastWriteTime(huerfana, DateTime.Now.AddDays(-30));
            var mf = Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl");
            File.WriteAllText(mf, "{}\n");

            using (new FileStream(mf, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Equal(0, new UndoEngine(paths).PodarPortadas());

            Assert.True(File.Exists(huerfana));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Una copia reutilizada (la misma portada en otra canción) se marca como reciente: así la poda
    // no la borra en el hueco entre guardarla y escribir la línea del manifiesto que la cita.
    [Fact]
    public void Reutilizar_una_copia_la_marca_como_reciente()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var carpeta = Path.Combine(dir, "portadas");
            var imagen = new TagLib.Picture(new TagLib.ByteVector(PortadaPropia)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" };
            var ruta = Tagging.GuardarCaratula(imagen, carpeta);
            File.SetLastWriteTime(ruta, DateTime.Now.AddDays(-60));

            Assert.Equal(ruta, Tagging.GuardarCaratula(imagen, carpeta));
            Assert.True(DateTime.Now - File.GetLastWriteTime(ruta) < TimeSpan.FromMinutes(5));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ M5 · renombrados vigentes

    // Ajustes contaba como «renombrados anotados» también los deshechos (las vueltas B→A que
    // ReadRenames necesita para reparar una Location que llegó a apuntar a B).
    [Fact]
    public void Los_renombrados_vigentes_no_cuentan_los_deshechos()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            string R(string n) => Path.Combine(dir, n);
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20260920_230254.deshecho.jsonl"),
                Linea(new UndoRecord { OrigPath = R("a1.mp3"), FinalPath = R("Bad Bunny - Titi Me Pregunto.mp3"), Renamed = true }) + "\n" +
                Linea(new UndoRecord { OrigPath = R("a2.mp3"), FinalPath = R("Karol G - Provenza.mp3"), Renamed = true }) + "\n");
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl"),
                Linea(new UndoRecord { OrigPath = R("c.mp3"), FinalPath = R("Daddy Yankee - Gasolina.mp3"), Renamed = true }) + "\n");

            Assert.Equal(3, RekordboxRelocator.ReadRenames(paths.UndoDir).Count);   // con las vueltas, para reparar
            var vigentes = RekordboxRelocator.RenombradosVigentes(paths.UndoDir);
            Assert.Equal(R("Daddy Yankee - Gasolina.mp3"), Assert.Single(vigentes).Value);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ T9 · configuración rescatada

    private static AppPaths ConfigNueva(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "etq-cfg2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new AppPaths(dir);
    }

    // Lo rescatado se escribe YA, antes de que el proceso pueda morir: el siguiente arranque
    // encuentra la configuración buena, aunque nunca se llegara a guardar (Stop-Process -Force).
    [Fact]
    public void Lo_rescatado_queda_escrito_aunque_el_proceso_muera_antes_de_guardar()
    {
        var paths = ConfigNueva(out var dir);
        try
        {
            var carpetas = Enumerable.Range(1, 43).Select(i => $"\"E:\\\\Musica\\\\Carpeta {i:00}\"");
            var original = $$"""{ "Folders": [{{string.Join(", ", carpetas)}}], "MinChangeIndex": "tres", "Theme": "oscuro" }""";
            File.WriteAllText(paths.ConfigPath, original);

            var cfg = AppConfig.Load(paths, out var aviso);
            Assert.Contains("MinChangeIndex", aviso);
            Assert.Equal(43, cfg.Folders.Count);
            // … y aquí «muere» el proceso: no se llama a Save.

            var otraVez = AppConfig.Load(paths, out var avisoSegundo);
            Assert.Equal("", avisoSegundo);
            Assert.Equal(43, otraVez.Folders.Count);
            Assert.Equal("oscuro", otraVez.Theme);
            var copia = Assert.Single(Directory.GetFiles(paths.DataDir, "config.net.json.ilegible_*"));
            Assert.Equal(original, File.ReadAllText(copia));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Una edición a mano con una coma de más (o un comentario) ya no tira la configuración entera.
    [Fact]
    public void Una_coma_de_mas_o_un_comentario_no_tiran_la_configuracion()
    {
        var paths = ConfigNueva(out var dir);
        try
        {
            File.WriteAllText(paths.ConfigPath, """
            {
              // carpetas de la biblioteca
              "Folders": ["E:\\Musica\\COMERCIAL", "E:\\Musica\\Reggaeton",],
              "Theme": "oscuro",
            }
            """);

            var cfg = AppConfig.Load(paths, out var aviso);

            Assert.Equal("", aviso);
            Assert.Equal(new[] { @"E:\Musica\COMERCIAL", @"E:\Musica\Reggaeton" }, cfg.Folders);
            Assert.Empty(Directory.GetFiles(paths.DataDir, "config.net.json.ilegible_*"));
            Assert.Equal("oscuro", AppConfig.LeerAspecto(paths).Theme);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ------------------------------------------------------------------ T8 · Llavero bloqueado

    // Con el Llavero bloqueado, cada credencial que se descifraba volvía a lanzar `security` (hasta
    // 15 s y un aviso de desbloqueo cada vez). El fallo se recuerda hasta un reintento explícito.
    [Fact]
    public void Un_fallo_del_llavero_se_recuerda_hasta_reintentar()
    {
        var llamadas = 0;
        var bloqueado = true;
        var clave = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        var sesion = new LlaveroMac.ClaveDeSesion(_ =>
        {
            llamadas++;
            return bloqueado
                ? new ResultadoSecurity(36, "", "security: SecKeychainSearchCopyNext: User interaction is not allowed.")
                : new ResultadoSecurity(0, clave + "\n", "");
        });

        Assert.Throws<InvalidOperationException>(() => sesion.Obtener());
        for (var i = 0; i < 4; i++) Assert.Throws<InvalidOperationException>(() => sesion.Obtener());   // los 4 setters *Enc
        Assert.Equal(1, llamadas);

        bloqueado = false;   // el usuario lo desbloquea y pide reintentar
        sesion.Reintentar();
        Assert.Equal(Enumerable.Repeat((byte)7, 32), sesion.Obtener());
        Assert.Equal(Enumerable.Repeat((byte)7, 32), sesion.Obtener());
        Assert.Equal(2, llamadas);
    }

    // El camino real del «sin respuesta en 15 s»: Ejecutar LANZA TimeoutException (no devuelve -1).
    // No se crea otra clave, y tampoco se vuelve a esperar otros 15 s en la siguiente credencial.
    [Fact]
    public void Si_security_no_responde_a_tiempo_no_se_crea_otra_clave_ni_se_vuelve_a_esperar()
    {
        var llamadas = new List<string>();
        var sesion = new LlaveroMac.ClaveDeSesion(args =>
        {
            llamadas.Add(args);
            throw new TimeoutException("El Llavero de macOS no respondió en 15 s (¿está esperando a que se desbloquee?).");
        });

        Assert.Throws<TimeoutException>(() => sesion.Obtener());
        Assert.Throws<TimeoutException>(() => sesion.Obtener());

        var unica = Assert.Single(llamadas);
        Assert.StartsWith("find-generic-password", unica);
    }

    // ------------------------------------------------------------------ T10 · reintentar la lectura

    [WindowsFact]
    public void Las_marcas_de_aplicar_bloqueadas_al_arrancar_se_juntan_al_guardar()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var f = Path.Combine(dir, "marcas-aplicar.json");
            File.WriteAllText(f, """{"E:\\Musica\\a.mp3":false,"E:\\Musica\\b.mp3":true,"E:\\Musica\\c.mp3":true}""");

            ApplyMarks m;
            using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                m = new ApplyMarks(f);
                Assert.NotEqual("", m.AvisoCarga);
                m.Set(@"E:\Musica\b.mp3", false);   // esta sesión cambia una
                m.Set(@"E:\Musica\d.mp3", true);    // y marca otra
                m.Forget(@"E:\Musica\c.mp3");       // y olvida otra (se aplicó)
                Assert.NotEqual("", m.Save());
            }

            Assert.Equal("", m.Save());
            var guardadas = new ApplyMarks(f);
            Assert.False(guardadas.Get(@"E:\Musica\a.mp3"));    // lo de antes, conservado
            Assert.False(guardadas.Get(@"E:\Musica\b.mp3"));    // lo de esta sesión manda
            Assert.True(guardadas.Get(@"E:\Musica\d.mp3"));
            Assert.Null(guardadas.Get(@"E:\Musica\c.mp3"));     // lo olvidado no vuelve
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [WindowsFact]
    public void La_cache_de_analisis_bloqueada_al_arrancar_no_pierde_lo_analizado_en_la_sesion()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3a = Path.Combine(dir, "a.mp3");
            var mp3b = Path.Combine(dir, "b.mp3");
            Mp3Fixture.WriteMinMp3(mp3a);
            Mp3Fixture.WriteMinMp3(mp3b);
            var f = Path.Combine(dir, "analysis-cache.json");
            var previa = new AnalysisCache(f);
            previa.Set(mp3a, "sig", new ProcessResult { FilePath = mp3a, New = "Bad Bunny - Titi Me Pregunto.mp3", Found = true });
            Assert.Equal("", previa.Save());

            AnalysisCache c;
            using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                c = new AnalysisCache(f);
                c.Set(mp3b, "sig", new ProcessResult { FilePath = mp3b, New = "Karol G - Provenza.mp3", Found = true });
                Assert.NotEqual("", c.Save());
            }

            Assert.Equal("", c.Save());
            var leida = new AnalysisCache(f);
            Assert.Equal(2, leida.Count);
            Assert.Equal("Bad Bunny - Titi Me Pregunto.mp3", leida.Get(mp3a, "sig")?.New);
            Assert.Equal("Karol G - Provenza.mp3", leida.Get(mp3b, "sig")?.New);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
