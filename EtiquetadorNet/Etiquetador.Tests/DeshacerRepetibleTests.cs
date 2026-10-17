using System.Text.Json;
using Etiquetador.Core;
using Etiquetador.Core.Analysis;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// «Deshacer última» se puede pulsar más de una vez sin que nada se revierta dos veces.
///
/// Nace de dos fallos vistos en revisión con datos reales. Uno: el manifiesto ya deshecho
/// (run_20260920_230254.deshecho.jsonl) seguía casando con «run_*.jsonl» y conservaba su fecha, así
/// que un segundo clic lo volvía a procesar. Dos: con un solo error, el manifiesto se conservaba
/// ENTERO, y al reintentar, el volumen -que se revierte aplicando la ganancia inversa, sin marca-
/// bajaba otra vez lo que ya estaba bien.
/// </summary>
public class DeshacerRepetibleTests
{
    private static string Linea(UndoRecord r) => JsonSerializer.Serialize(r);

    /// <summary>
    /// Un MP3 con margen para subir y bajar (global_gain 6 en todas las tramas) que hace de
    /// «original», y el mismo archivo con el ajuste aplicado como lo hace la pestaña Volumen.
    /// Devuelve los bytes originales y la línea de manifiesto con el formato de LoudnessViewModel.
    /// </summary>
    private static (byte[] Original, string Linea) AjustarVolumen(string mp3, int pasos)
    {
        Mp3Fixture.WriteMinMp3(mp3, frames: 30);
        Assert.True(Mp3Gain.Apply(mp3, +6).Ok);           // nivel de partida con margen
        var original = File.ReadAllBytes(mp3);
        Assert.True(Mp3Gain.Apply(mp3, pasos).Ok);         // el ajuste de la pestaña Volumen
        var cambio = pasos >= 0 ? FieldChange.Num(0, (uint)pasos) : FieldChange.Num((uint)(-pasos), 0);
        var rec = new UndoRecord
        {
            OrigPath = mp3, FinalPath = mp3, Renamed = false,
            Fields = new() { [UndoEngine.VolumeField] = cambio },
        };
        return (original, Linea(rec));
    }

    // Caso real del 09-10 a las 21:08: 29 MP3 ajustados a −3 / −4,5 dB. Deshacer los devuelve a su
    // nivel; un segundo clic NO puede volver a subirlos: tiene que ir a la ejecución anterior.
    [Fact]
    public void Un_segundo_clic_va_a_la_ejecucion_anterior_y_no_repite_el_volumen()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);

            // Ejecución anterior (Enriquecer, 20-09): un título.
            var tema = Path.Combine(dir, "tema.mp3");
            Mp3Fixture.WriteMinMp3(tema);
            Mp3Fixture.SetTags(tema, "Titi Me Pregunto", null, null);
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20260920_230254.jsonl"), Linea(new UndoRecord
            {
                OrigPath = tema, FinalPath = tema,
                Fields = new() { ["Title"] = FieldChange.Str("pista01", "Titi Me Pregunto") },
            }) + "\n");

            // La de volumen (09-10 21:08): 29 MP3, alternando −3 dB (2 pasos) y −4,5 dB (3 pasos).
            var originales = new Dictionary<string, byte[]>();
            var lineas = new List<string>();
            for (var i = 1; i <= 29; i++)
            {
                var mp3 = Path.Combine(dir, $"Tema {i:00}.mp3");
                var (orig, linea) = AjustarVolumen(mp3, i % 2 == 0 ? -2 : -3);
                originales[mp3] = orig;
                lineas.Add(linea);
            }
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl"), string.Join("\n", lineas) + "\n");

            var undo = new UndoEngine(paths);

            var primero = undo.UndoLastRun();
            Assert.Equal("run_20261009_210831.jsonl", primero.File);
            Assert.Equal(29, primero.Reverted);
            Assert.True(primero.Clean);
            Assert.True(File.Exists(Path.Combine(paths.UndoDir, "run_20261009_210831.deshecho.jsonl")));
            foreach (var (mp3, orig) in originales) Assert.Equal(orig, File.ReadAllBytes(mp3));

            // El archivado es ahora el archivo con la fecha de modificación más reciente: justo lo
            // que antes hacía que se eligiera otra vez.
            var segundo = undo.UndoLastRun();
            Assert.Equal("run_20260920_230254.jsonl", segundo.File);
            Assert.Equal("pista01", Mp3Fixture.GetTitle(tema));
            foreach (var (mp3, orig) in originales) Assert.Equal(orig, File.ReadAllBytes(mp3));   // ni un paso más

            Assert.True(undo.UndoLastRun().None);   // ya no queda nada que deshacer
            foreach (var (mp3, orig) in originales) Assert.Equal(orig, File.ReadAllBytes(mp3));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("run_20261009_210831.jsonl", true)]
    [InlineData("run_20261009_210831_2.jsonl", true)]            // dos ejecuciones en el mismo segundo
    [InlineData("run_20260920_230254.deshecho.jsonl", false)]    // el archivado real que se volvía a elegir
    [InlineData("run_20260920_230254.deshecho.deshecho.jsonl", false)]
    [InlineData("run_20261009_210831.deshecho-2.jsonl", false)]
    [InlineData("run_20261009_210831.jsonl.tmp", false)]
    [InlineData("run_rb.jsonl", false)]
    public void Solo_se_eligen_manifiestos_de_ejecucion_puros(string nombre, bool pendiente)
        => Assert.Equal(pendiente, UndoEngine.EsManifiestoPendiente(nombre));

    // Se elige por la fecha del NOMBRE: un archivado con la fecha de modificación más reciente no
    // cuenta, y la ejecución más nueva gana aunque su archivo se haya tocado antes.
    [Fact]
    public void El_ultimo_es_el_de_fecha_mas_reciente_en_el_nombre()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var viejo = Path.Combine(paths.UndoDir, "run_20260901_120000.jsonl");
            var nuevo = Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl");
            var archivado = Path.Combine(paths.UndoDir, "run_20261010_090000.deshecho.jsonl");
            foreach (var f in new[] { viejo, nuevo, archivado }) File.WriteAllText(f, "");
            File.SetLastWriteTime(nuevo, new DateTime(2026, 10, 9, 21, 8, 31));
            File.SetLastWriteTime(viejo, new DateTime(2026, 10, 12, 10, 0, 0));       // tocado después
            File.SetLastWriteTime(archivado, new DateTime(2026, 10, 15, 10, 0, 0));   // el más reciente en disco

            Assert.Equal(new[] { nuevo, viejo }, new UndoEngine(paths).ManifiestosPendientes());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Dos ejecuciones en el mismo segundo ya no se mezclan en un manifiesto, y la segunda es «la última».
    [Fact]
    public void Dos_ejecuciones_en_el_mismo_segundo_no_comparten_manifiesto()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var t = new DateTime(2026, 10, 9, 21, 8, 31);
            var a = paths.NuevoManifiesto(t);
            File.WriteAllText(a, "");
            var b = paths.NuevoManifiesto(t);
            File.WriteAllText(b, "");

            Assert.Equal("run_20261009_210831.jsonl", Path.GetFileName(a));
            Assert.Equal("run_20261009_210831_2.jsonl", Path.GetFileName(b));
            Assert.Equal(new[] { b, a }, new UndoEngine(paths).ManifiestosPendientes());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Deshacer un ajuste con un archivo que no está (se movió por fuera): los demás se revierten y
    // el manifiesto se queda SOLO con el que falta. Al reaparecer y reintentar, se revierte ese y
    // los que ya estaban bien no bajan otros 3 dB.
    [Fact]
    public void Reintentar_un_deshacer_a_medias_solo_toca_lo_pendiente()
    {
        var dir = Mp3Fixture.NewTempDir();
        var fuera = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var originales = new Dictionary<string, byte[]>();
            var lineas = new List<string>();
            for (var i = 1; i <= 5; i++)
            {
                var mp3 = Path.Combine(dir, $"Tema {i:00}.mp3");
                var (orig, linea) = AjustarVolumen(mp3, +2);   // +3 dB
                originales[mp3] = orig;
                lineas.Add(linea);
            }
            var manifiesto = Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl");
            File.WriteAllText(manifiesto, string.Join("\n", lineas) + "\n");

            var movido = Path.Combine(dir, "Tema 03.mp3");
            var aparte = Path.Combine(fuera, "Tema 03.mp3");
            File.Move(movido, aparte);

            var undo = new UndoEngine(paths);
            var primero = undo.UndoLastRun();
            Assert.Equal(4, primero.Reverted);
            Assert.Equal(1, primero.Missing);
            Assert.Equal(1, primero.Pendientes);
            Assert.False(primero.Clean);

            // El manifiesto se queda solo con el que falta; lo revertido, archivado aparte.
            var quedan = File.ReadAllLines(manifiesto).Where(l => l.Trim().Length > 0).ToList();
            Assert.Single(quedan);
            Assert.Contains("Tema 03.mp3", quedan[0]);
            var archivado = Path.Combine(paths.UndoDir, "run_20261009_210831.deshecho.jsonl");
            Assert.Equal(4, File.ReadAllLines(archivado).Count(l => l.Trim().Length > 0));

            File.Move(aparte, movido);   // vuelve el archivo
            var segundo = undo.UndoLastRun();
            Assert.Equal("run_20261009_210831.jsonl", segundo.File);
            Assert.Equal(1, segundo.Reverted);
            Assert.True(segundo.Clean);
            Assert.False(File.Exists(manifiesto));
            Assert.True(File.Exists(Path.Combine(paths.UndoDir, "run_20261009_210831.deshecho-2.jsonl")));

            foreach (var (mp3, orig) in originales) Assert.Equal(orig, File.ReadAllBytes(mp3));   // los cinco, exactos
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
            try { Directory.Delete(fuera, true); } catch { }
        }
    }

    // El caso del hallazgo tal cual: uno de los MP3 está abierto en rekordbox y no se puede
    // reescribir. Sale como error, se cierra rekordbox y se reintenta.
    [WindowsFact]
    public void Un_mp3_abierto_en_otro_programa_se_reintenta_sin_repetir_los_demas()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var originales = new Dictionary<string, byte[]>();
            var lineas = new List<string>();
            for (var i = 1; i <= 4; i++)
            {
                var mp3 = Path.Combine(dir, $"Tema {i:00}.mp3");
                var (orig, linea) = AjustarVolumen(mp3, +2);
                originales[mp3] = orig;
                lineas.Add(linea);
            }
            File.WriteAllText(Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl"), string.Join("\n", lineas) + "\n");

            var undo = new UndoEngine(paths);
            UndoResult primero;
            // Abierto como lo tendría un reproductor: se deja leer, pero no sustituir.
            using (new FileStream(Path.Combine(dir, "Tema 02.mp3"), FileMode.Open, FileAccess.Read, FileShare.Read))
                primero = undo.UndoLastRun();
            Assert.Equal(3, primero.Reverted);
            Assert.Equal(1, primero.Errors);

            var segundo = undo.UndoLastRun();
            Assert.Equal(1, segundo.Reverted);
            Assert.True(segundo.Clean);
            foreach (var (mp3, orig) in originales) Assert.Equal(orig, File.ReadAllBytes(mp3));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Un MP3 cuya etiqueta TagLib no sabe leer: el volumen se devuelve, y antes, al abrirlo después
    // para los tags (que un registro de volumen ni siquiera tiene), fallaba y el registro quedaba
    // como error: el reintento lo bajaba otra vez.
    [Fact]
    public void Un_mp3_con_la_etiqueta_danada_no_deja_el_volumen_pendiente()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var mp3 = Path.Combine(dir, "Tema 01.mp3");
            var (original, linea) = AjustarVolumen(mp3, +2);
            // Delante, una cabecera ID3v2 que dice medir más que el archivo entero (truncado): TagLib
            // busca el audio después de esa etiqueta y no lo encuentra; el ajuste de volumen la
            // ignora y llega a las tramas.
            var cabecera = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0x7F, 0x7F, 0x7F, 0x7F };
            File.WriteAllBytes(mp3, cabecera.Concat(File.ReadAllBytes(mp3)).ToArray());
            var originalConCabecera = cabecera.Concat(original).ToArray();
            Assert.ThrowsAny<Exception>(() => { using var f = TagLib.File.Create(mp3); });

            var manifiesto = Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl");
            File.WriteAllText(manifiesto, linea + "\n");

            var res = new UndoEngine(paths).UndoLastRun();

            Assert.Equal(1, res.Reverted);
            Assert.True(res.Clean);
            Assert.Equal(originalConCabecera, File.ReadAllBytes(mp3));
            Assert.True(new UndoEngine(paths).UndoLastRun().None);   // nada pendiente que repetir
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Mientras no se pueda, el manifiesto no se toca: un reintento con el disco desconectado no
    // puede perder ningún registro.
    [Fact]
    public void Si_no_se_pudo_nada_el_manifiesto_queda_igual()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            Directory.CreateDirectory(paths.UndoDir);
            var manifiesto = Path.Combine(paths.UndoDir, "run_20261009_210831.jsonl");
            var contenido = Linea(new UndoRecord
            {
                OrigPath = Path.Combine(dir, "E", "Musica", "no-esta.mp3"),
                FinalPath = Path.Combine(dir, "E", "Musica", "no-esta.mp3"),
                Fields = new() { [UndoEngine.VolumeField] = FieldChange.Num(0, 2) },
            }) + "\n";
            File.WriteAllText(manifiesto, contenido);

            var res = new UndoEngine(paths).UndoLastRun();

            Assert.Equal(1, res.Missing);
            Assert.Equal(contenido, File.ReadAllText(manifiesto));
            Assert.Empty(Directory.EnumerateFiles(paths.UndoDir, "*deshecho*"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- Carátula (contrato «Cover»): la frontal anterior se guarda en deshacer/portadas ----

    private static readonly byte[] PortadaVieja = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9 };
    private static readonly byte[] PortadaNueva = { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9, 9, 0xFF, 0xD9 };
    private static readonly byte[] Contraportada = { 0x89, 0x50, 0x4E, 0x47, 7, 7, 7 };

    private static TagLib.Picture Imagen(byte[] datos, TagLib.PictureType tipo, string mime)
        => new(new TagLib.ByteVector(datos)) { Type = tipo, MimeType = mime };

    private static void PonerImagenes(string mp3, params TagLib.IPicture[] imagenes)
    {
        using var f = TagLib.File.Create(mp3);
        f.Tag.Pictures = imagenes;
        f.Save();
    }

    private static TagLib.IPicture[] Imagenes(string mp3)
    {
        using var f = TagLib.File.Create(mp3);
        return f.Tag.Pictures;
    }

    [Fact]
    public void Deshacer_devuelve_la_caratula_frontal_anterior_y_no_toca_las_demas()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var mp3 = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            PonerImagenes(mp3, Imagen(PortadaVieja, TagLib.PictureType.FrontCover, "image/jpeg"),
                               Imagen(Contraportada, TagLib.PictureType.BackCover, "image/png"));

            // Lo que hace quien sustituye la carátula: guarda la anterior y anota la ruta.
            Directory.CreateDirectory(paths.PortadasDir);
            var copia = Path.Combine(paths.PortadasDir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(PortadaVieja)).ToLowerInvariant() + ".jpg");
            File.WriteAllBytes(copia, PortadaVieja);
            PonerImagenes(mp3, Imagen(PortadaNueva, TagLib.PictureType.FrontCover, "image/jpeg"),
                               Imagen(Contraportada, TagLib.PictureType.BackCover, "image/png"));

            var manifiesto = Path.Combine(dir, "run_20261017_120000.jsonl");
            File.WriteAllText(manifiesto, Linea(new UndoRecord
            {
                OrigPath = mp3, FinalPath = mp3,
                Fields = new() { [UndoEngine.CoverField] = FieldChange.Portada(copia) },
            }) + "\n");

            var res = new UndoEngine(paths).UndoLastRun(manifiesto);

            Assert.Equal(1, res.Reverted);
            var imagenes = Imagenes(mp3);
            var frontal = Assert.Single(imagenes, p => p.Type == TagLib.PictureType.FrontCover);
            Assert.Equal(PortadaVieja, frontal.Data.Data);
            var trasera = Assert.Single(imagenes, p => p.Type == TagLib.PictureType.BackCover);
            Assert.Equal(Contraportada, trasera.Data.Data);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Si_no_tenia_caratula_deshacer_quita_la_frontal_puesta()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var mp3 = Path.Combine(dir, "pista01.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            PonerImagenes(mp3, Imagen(PortadaNueva, TagLib.PictureType.FrontCover, "image/jpeg"),
                               Imagen(Contraportada, TagLib.PictureType.BackCover, "image/png"));

            var manifiesto = Path.Combine(dir, "run_20261017_120000.jsonl");
            File.WriteAllText(manifiesto, Linea(new UndoRecord
            {
                OrigPath = mp3, FinalPath = mp3,
                Fields = new() { [UndoEngine.CoverField] = FieldChange.Str("", UndoEngine.CoverSustituida) },
            }) + "\n");

            new UndoEngine(paths).UndoLastRun(manifiesto);

            var imagenes = Imagenes(mp3);
            Assert.DoesNotContain(imagenes, p => p.Type == TagLib.PictureType.FrontCover);
            Assert.Single(imagenes, p => p.Type == TagLib.PictureType.BackCover);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Si la carpeta de datos cambió de sitio, la ruta absoluta anotada ya no existe, pero la copia
    // sigue en la carpeta de portadas actual con el mismo nombre.
    [Fact]
    public void La_copia_se_busca_tambien_en_la_carpeta_de_portadas_actual()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var paths = new AppPaths(dir);
            var mp3 = Path.Combine(dir, "pista01.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            PonerImagenes(mp3, Imagen(PortadaNueva, TagLib.PictureType.FrontCover, "image/jpeg"));
            Directory.CreateDirectory(paths.PortadasDir);
            File.WriteAllBytes(Path.Combine(paths.PortadasDir, "abc123.jpg"), PortadaVieja);
            var rutaAntigua = Path.Combine(dir, "otro-sitio", "deshacer", "portadas", "abc123.jpg");

            var manifiesto = Path.Combine(dir, "run_20261017_120000.jsonl");
            File.WriteAllText(manifiesto, Linea(new UndoRecord
            {
                OrigPath = mp3, FinalPath = mp3,
                Fields = new() { [UndoEngine.CoverField] = FieldChange.Str(rutaAntigua, UndoEngine.CoverSustituida) },
            }) + "\n");

            new UndoEngine(paths).UndoLastRun(manifiesto);

            Assert.Equal(PortadaVieja, Assert.Single(Imagenes(mp3)).Data.Data);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Los registros antiguos ("changed") no guardaban la carátula: no hay nada que devolver y no se
    // puede quitar la que hay como si el archivo no hubiera tenido ninguna.
    [Fact]
    public void Un_registro_antiguo_de_caratula_no_la_toca()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var mp3 = Path.Combine(dir, "pista01.mp3");
            Mp3Fixture.WriteMinMp3(mp3);
            PonerImagenes(mp3, Imagen(PortadaNueva, TagLib.PictureType.FrontCover, "image/jpeg"));
            var manifiesto = Path.Combine(dir, "run_20260920_230254.jsonl");
            File.WriteAllText(manifiesto, Linea(new UndoRecord
            {
                OrigPath = mp3, FinalPath = mp3,
                Fields = new() { [UndoEngine.CoverField] = FieldChange.Str(null, "changed") },
            }) + "\n");

            new UndoEngine(new AppPaths(dir)).UndoLastRun(manifiesto);

            Assert.Equal(PortadaNueva, Assert.Single(Imagenes(mp3)).Data.Data);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
