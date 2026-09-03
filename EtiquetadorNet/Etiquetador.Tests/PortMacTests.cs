using Etiquetador.App.ViewModels;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Las cuatro piezas que quedaban del port a macOS: secretos, papelera, instalador de Ollama y
/// fpcalc.
///
/// Lo que se puede fijar aqui son las decisiones y las piezas puras. Lo que NO -que el Llavero
/// guarde de verdad, que Finder mueva el archivo, que brew instale- necesita un Mac con sesion
/// iniciada, y queda anotado en docs/macos.md como limite conocido.
/// </summary>
public class PortMacTests
{
    // --- Secretos ---

    // La ida y vuelta tiene que funcionar en el sistema donde se ejecute, sea cual sea el mecanismo.
    // En Windows es DPAPI; en macOS, AES con la clave del Llavero.
    [Fact]
    public void Un_secreto_cifrado_se_recupera_igual()
    {
        if (!Secretos.Disponible) return;   // sin cifrado disponible no hay nada que comprobar

        const string secreto = "clave-de-prueba-123";
        var cifrado = Secretos.Protect(secreto);

        Assert.NotEqual(secreto, cifrado);
        var (valor, estado) = Secretos.TryUnprotect(cifrado);
        Assert.Equal(UnprotectStatus.Ok, estado);
        Assert.Equal(secreto, valor);
    }

    [Fact]
    public void Lo_vacio_sigue_vacio()
    {
        Assert.Equal("", Secretos.Protect(""));
        Assert.Equal(UnprotectStatus.Empty, Secretos.TryUnprotect("").Status);
    }

    // Una configuracion heredada en claro se devuelve tal cual, no se confunde con un cifrado roto:
    // esa distincion es la que evita re-cifrar vacios y perder las credenciales del usuario.
    [Theory]
    [InlineData("clave-en-claro-xyz")]     // ni siquiera es hexadecimal
    [InlineData("abcdef0123456789")]       // hexadecimal, pero sin ninguna de nuestras cabeceras
    public void El_texto_en_claro_heredado_se_reconoce(string valor)
    {
        var (v, estado) = Secretos.TryUnprotect(valor);
        Assert.Equal(UnprotectStatus.Cleartext, estado);
        Assert.Equal(valor, v);
    }

    // Un secreto cifrado en el OTRO sistema tiene que dar error criptografico, nunca pasar por texto
    // en claro: devolver el churro hexadecimal como si fuera la credencial seria lo peor de todo.
    [Fact]
    public void Lo_cifrado_en_el_otro_sistema_se_marca_como_ilegible()
    {
        // "BTG1" + relleno = cabecera del formato de macOS.
        const string blobMac = "425447310102030405060708090A0B0C0D0E0F101112131415161718191A1B1C";
        // Cabecera de un blob DPAPI de Windows.
        const string blobWin = "01000000d08c9ddf00112233445566778899aabbccddeeff";

        var ajeno = OperatingSystem.IsWindows() ? blobMac : blobWin;
        Assert.Equal(UnprotectStatus.CryptoError, Secretos.TryUnprotect(ajeno).Status);
    }

    // --- Papelera ---

    // En la papelera no se puede machacar lo que ya hay: seria perder algo que el usuario todavia
    // podia recuperar de una limpieza anterior.
    [Fact]
    public void La_papelera_no_pisa_un_archivo_ya_existente()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "tema.mp3"), "el de antes");

            var destino = DuplicatesViewModel.DestinoLibre(dir, "tema.mp3");

            Assert.NotEqual(Path.Combine(dir, "tema.mp3"), destino);
            Assert.False(File.Exists(destino));
            Assert.EndsWith(".mp3", destino);            // conserva la extension
            Assert.Contains("tema", Path.GetFileName(destino));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Si_no_hay_nada_se_usa_el_nombre_tal_cual()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            Assert.Equal(Path.Combine(dir, "tema.mp3"), DuplicatesViewModel.DestinoLibre(dir, "tema.mp3"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // Las comillas y las barras invertidas de una ruta romperian el AppleScript -o algo peor- si se
    // metieran sin escapar dentro de la cadena.
    [Theory]
    [InlineData("/Musica/tema.mp3", "/Musica/tema.mp3")]
    [InlineData("/Musica/co\"millas.mp3", "/Musica/co\\\"millas.mp3")]
    [InlineData("/Musica/ba\\rra.mp3", "/Musica/ba\\\\rra.mp3")]
    public void La_ruta_se_escapa_para_applescript(string entrada, string esperado)
        => Assert.Equal(esperado, DuplicatesViewModel.EscaparAppleScript(entrada));

    // --- Instalador de Ollama ---

    // El nombre del gestor sale en los mensajes que ve el usuario: tiene que ser el de SU sistema.
    [Fact]
    public void El_gestor_de_paquetes_es_el_del_sistema()
        => Assert.Equal(OperatingSystem.IsWindows() ? "winget" : "Homebrew",
                        Etiquetador.App.Services.OllamaInstaller.GestorDePaquetes);

    // --- fpcalc ---

    // Fuera de Windows el ejecutable no lleva extension. Si esto se rompe, la aplicacion buscaria
    // "fpcalc.exe" en un Mac y daria la huella acustica por no disponible sin mas explicacion.
    [Fact]
    public void Fpcalc_se_llama_como_toca_en_cada_sistema()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var fp = new Fingerprint(new AppPaths(dir));
            var nombre = Path.GetFileName(fp.FpcalcPath);
            Assert.Equal(OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc", nombre);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
