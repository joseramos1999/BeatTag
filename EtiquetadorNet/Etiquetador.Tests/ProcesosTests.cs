using System.Diagnostics;
using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// Parar los programas que lanza la aplicación, con lo que lanzaron ellos, sin usar fuera de Windows
/// Process.Kill(entireProcessTree: true): para cada proceso con SIGSTOP y, con un grupo de procesos
/// huérfano, el núcleo manda SIGHUP a todo el grupo. En la CI de macOS se llevaba el runner de GitHub.
/// </summary>
public class ProcesosTests
{
    /// <summary>
    /// Un programa que lanza a OTRO, y es ese otro el que deja la marca, y solo cuando la prueba lo
    /// suelta (ver ProgramaQueEspera). Si al soltarlo no la deja, se paró también lo que había lanzado.
    /// </summary>
    private static ProcessStartInfo ConNieto(ProgramaQueEspera nieto)
        => OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "cmd.exe", "/c", nieto.Ruta }, UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "\"$0\" & wait", nieto.Ruta }, UseShellExecute = false };

    // Se mata cuando el nieto YA está en marcha (lo apunta él), no a los 500 ms. Antes el nieto
    // dejaba la marca solo a los ~2 s de arrancar y se esperaban 500 ms antes de matar: falló en la
    // CI de Windows tras unir el PR #6, con la prueba durando 5 s cuando sus esperas suman 3,5. Un
    // retraso de ese tamaño en el Task.Delay, que corre en el pool, basta para que el nieto escriba
    // antes de que lo maten: medido en octubre de 2026, los temporizadores llegaban hasta 0,9 s tarde
    // en 4 núcleos y lanzar un programa costaba hasta 0,7 s. Y matar antes de que el nieto exista
    // pasaba sin probar nada.
    [Fact]
    public async Task Matar_para_tambien_lo_que_lanzo_el_proceso()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var nieto = new ProgramaQueEspera(dir, "nieto");
            using var p = Process.Start(ConNieto(nieto))!;
            await nieto.EsperarQueArranqueAsync();

            Procesos.Matar(p);

            Assert.True(p.WaitForExit(TimeSpan.FromMinutes(1)), "el proceso sigue vivo");
            Assert.False(await nieto.SiguioVivoAsync(), "lo que lanzó el proceso siguió vivo");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Matar_un_proceso_que_ya_termino_no_hace_nada()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "exit 0" }, UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "exit 0" }, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();

        Procesos.Matar(p);   // no lanza

        Assert.True(p.HasExited);
    }

    // Que nadie vuelva a usar la de .NET: en macOS es un cuelgue de la CI cada pocas ejecuciones, y
    // no falla ninguna prueba que lo avise.
    [Fact]
    public void Solo_Procesos_mata_arboles_de_procesos()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Etiquetador.Core", "Procesos.cs")))
            dir = dir.Parent;
        Assert.True(dir != null, "no se encontró el árbol de fuentes desde " + AppContext.BaseDirectory);

        var separador = Path.DirectorySeparatorChar;
        var usos = new[] { "Etiquetador.Core", "Etiquetador.App" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(dir!.FullName, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}"))
            .Where(f => Path.GetFileName(f) != "Procesos.cs" && File.ReadAllText(f).Contains("entireProcessTree"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(usos);
    }
}
