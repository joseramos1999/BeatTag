using System.Text;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.Tests;

/// <summary>
/// El recorrido de las carpetas de la biblioteca. Lo que importa es no confundir «no hay
/// canciones» con «no se ha podido mirar»: lo segundo, tratado como lo primero, podaba la caché de
/// la carpeta entera. MEDIDO: releer sin caché costó 340.680 ms para 13.464 canciones, frente a
/// 7.774 ms con ella.
/// </summary>
public class LibraryScannerTests
{
    // Biblioteca en un disco USB que no está enchufado al abrir BeatTag.
    [Fact]
    public void Una_carpeta_que_no_existe_no_esta_disponible()
    {
        var desconectada = Path.Combine(Path.GetTempPath(), "etq-usb-" + Guid.NewGuid().ToString("N"), "Musica");

        var r = LibraryScanner.Recorrer(desconectada);

        Assert.False(r.Disponible);
        Assert.Empty(r.Archivos);
        Assert.NotEqual("", r.Motivo);
    }

    // Una carpeta vacía SÍ está disponible: ahí no hay canciones, y eso es un dato.
    [Fact]
    public void Una_carpeta_vacia_esta_disponible()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var r = LibraryScanner.Recorrer(dir);
            Assert.True(r.Disponible);
            Assert.Empty(r.Archivos);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Los «._*» que deja macOS en un disco externo llevan la extensión del audio, pero no son audio:
    // entraban en la biblioteca como canciones sin datos.
    [Fact]
    public void Los_archivos_de_macOS_que_empiezan_por_punto_guion_bajo_no_son_canciones()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            Mp3Fixture.WriteMinMp3(Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.mp3"));
            File.WriteAllBytes(Path.Combine(dir, "._Bad Bunny - Titi Me Pregunto.mp3"), new byte[] { 0, 5, 22, 7 });

            var r = LibraryScanner.Recorrer(dir);

            Assert.True(r.Disponible);
            Assert.Equal("Bad Bunny - Titi Me Pregunto.mp3", Path.GetFileName(Assert.Single(r.Archivos)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Añadir la raíz de una unidad («E:\») traía la papelera y las carpetas de sistema; con una de
    // ellas sin permiso, además, se perdía la unidad entera.
    [WindowsFact]
    public void Se_saltan_la_papelera_y_las_carpetas_de_sistema()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var papelera = Directory.CreateDirectory(Path.Combine(dir, "$RECYCLE.BIN"));
            Mp3Fixture.WriteMinMp3(Path.Combine(papelera.FullName, "$R4X2Y1Z.mp3"));
            papelera.Attributes |= FileAttributes.Hidden | FileAttributes.System;
            var musica = Directory.CreateDirectory(Path.Combine(dir, "Musica", "COMERCIAL"));
            Mp3Fixture.WriteMinMp3(Path.Combine(musica.FullName, "Rosalia - Malamente.mp3"));

            var r = LibraryScanner.Recorrer(dir);

            Assert.True(r.Disponible);
            Assert.Equal("Rosalia - Malamente.mp3", Path.GetFileName(Assert.Single(r.Archivos)));
            papelera.Attributes = FileAttributes.Directory;   // para poder borrarla al terminar
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Lo esencial de M7: una subcarpeta sin permiso («System Volume Information» en la raíz de una
    // unidad) lanzaba a mitad del recorrido y la carpeta ENTERA salía con 0 canciones.
    [WindowsFact]
    public void Una_subcarpeta_sin_permiso_no_tira_la_carpeta_entera()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Mp3Fixture.NewTempDir();
        var privada = Directory.CreateDirectory(Path.Combine(dir, "privada"));
        var regla = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        try
        {
            Mp3Fixture.WriteMinMp3(Path.Combine(dir, "Daddy Yankee - Gasolina.mp3"));
            Mp3Fixture.WriteMinMp3(Path.Combine(privada.FullName, "oculta.mp3"));
            var sub = Directory.CreateDirectory(Path.Combine(dir, "Reggaeton"));
            Mp3Fixture.WriteMinMp3(Path.Combine(sub.FullName, "Bad Bunny - Titi Me Pregunto.mp3"));

            var acl = privada.GetAccessControl();
            acl.AddAccessRule(regla);
            privada.SetAccessControl(acl);

            // Comprobación de la propia prueba: la carpeta de verdad no se deja listar. En los equipos
            // de integración continua de GitHub la cuenta es administradora y la denegación no surte
            // efecto; ahí no se puede reproducir el caso y la prueba no tiene nada que comprobar.
            try { Directory.GetFiles(privada.FullName); return; }
            catch (UnauthorizedAccessException) { }

            var r = LibraryScanner.Recorrer(dir);

            Assert.True(r.Disponible);
            Assert.Equal(new[] { "Bad Bunny - Titi Me Pregunto.mp3", "Daddy Yankee - Gasolina.mp3" },
                         r.Archivos.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            try
            {
                var acl = privada.GetAccessControl();
                acl.RemoveAccessRule(regla);
                privada.SetAccessControl(acl);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // La compatibilidad: EnumerateFiles sigue sin lanzar y da lo mismo que Recorrer.
    [Fact]
    public void EnumerateFiles_da_lo_mismo_que_Recorrer_y_nunca_lanza()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            Mp3Fixture.WriteMinMp3(Path.Combine(dir, "a.mp3"));
            File.WriteAllText(Path.Combine(dir, "portada.jpg"), "x");
            Assert.Equal(LibraryScanner.Recorrer(dir).Archivos, LibraryScanner.EnumerateFiles(dir).ToList());
            Assert.Empty(LibraryScanner.EnumerateFiles(Path.Combine(dir, "no-existe")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- AIFF (M15) ----

    [Theory]
    [InlineData("Bad Bunny - Titi Me Pregunto.aiff")]
    [InlineData("Bad Bunny - Titi Me Pregunto.aif")]
    [InlineData("BAD BUNNY - TITI ME PREGUNTO.AIFF")]
    public void Los_aiff_son_audio_de_la_biblioteca(string nombre)
        => Assert.True(LibraryScanner.EsAudio(nombre));

    /// <summary>
    /// Un AIFF mínimo válido: cabecera FORM/AIFF, bloque COMM (estéreo, 16 bits, 44,1 kHz) y un
    /// segundo de silencio en SSND.
    /// </summary>
    internal static void WriteMinAiff(string path, int segundos = 1)
    {
        const int canales = 2, bits = 16, frecuencia = 44100;
        var marcos = frecuencia * segundos;
        var datos = marcos * canales * (bits / 8);

        using var ms = new MemoryStream();
        void Texto(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void U32(int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        Texto("FORM"); U32(4 + (8 + 18) + (8 + 8 + datos)); Texto("AIFF");
        Texto("COMM"); U32(18); U16(canales); U32(marcos); U16(bits);
        ms.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 });   // 44100 en coma flotante extendida
        Texto("SSND"); U32(8 + datos); U32(0); U32(0);
        ms.Write(new byte[datos]);
        File.WriteAllBytes(path, ms.ToArray());
    }

    // Un DJ que compra en AIFF añadía su carpeta y BeatTag no veía esos archivos en ninguna
    // pestaña. Ahora entran, se leen sus tags y su duración.
    [Fact]
    public void Un_aiff_entra_en_la_biblioteca_con_sus_tags()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var aiff = Path.Combine(dir, "Bad Bunny - Titi Me Pregunto.aiff");
            WriteMinAiff(aiff);
            using (var f = TagLib.File.Create(aiff))
            {
                f.Tag.Title = "Titi Me Pregunto";
                f.Tag.Performers = new[] { "Bad Bunny" };
                f.Save();
            }

            var r = LibraryScanner.Recorrer(dir);
            var t = LibraryScanner.ReadTrack(Assert.Single(r.Archivos));

            Assert.Equal("Titi Me Pregunto", t.Title);
            Assert.Equal("Bad Bunny", t.Artist);
            Assert.Equal(1, t.DurationSeconds);
            Assert.Equal(44100, t.SampleRate);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Y lo que hace Enriquecer con él: escribir tags, renombrar conservando la extensión, y deshacer.
    [Fact]
    public async Task Un_aiff_se_etiqueta_se_renombra_y_se_deshace()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var aiff = Path.Combine(dir, "pista01.aiff");
            WriteMinAiff(aiff);
            var info = new ProcessResult
            {
                FilePath = aiff, Old = "pista01.aiff", New = "Bad Bunny - Titi Me Pregunto.aiff",
                Title = "Titi Me Pregunto", Artist = "Bad Bunny", Album = "Un Verano Sin Ti", Found = true,
            };
            var man = Path.Combine(dir, "run_20261017_120000.jsonl");
            var res = await new ApplyEngine().ApplyOneAsync(info, true, new FieldFlags { Title = true, Artist = true, Album = true },
                                                            "keep", null, man, Path.Combine(dir, "done.log"));

            Assert.True(res.TagOk);
            Assert.True(res.DidRename);
            Assert.Equal(".aiff", Path.GetExtension(res.FinalPath));
            Assert.Equal("Titi Me Pregunto", LibraryScanner.ReadTrack(res.FinalPath).Title);

            var undo = new UndoEngine(new AppPaths(dir)).UndoLastRun(man);
            Assert.Equal(1, undo.Reverted);
            Assert.True(File.Exists(aiff));
            Assert.True(string.IsNullOrEmpty(LibraryScanner.ReadTrack(aiff).Title));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
