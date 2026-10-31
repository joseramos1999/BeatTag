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
    /// Un programa que lanza a OTRO, y ese otro deja una marca en disco a los ~2 s. Si la marca no
    /// aparece, se paró también lo que había lanzado.
    /// </summary>
    private static ProcessStartInfo ConNieto(string dir, string marca)
    {
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(dir, "nieto.cmd");
            File.WriteAllText(bat, $"@ping -n 3 127.0.0.1 >nul\r\n@echo tarde> \"{marca}\"\r\n");
            return new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "cmd.exe", "/c", bat }, UseShellExecute = false, CreateNoWindow = true };
        }
        return new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", $"(sleep 2; echo tarde > '{marca}') & wait" }, UseShellExecute = false };
    }

    [Fact]
    public async Task Matar_para_tambien_lo_que_lanzo_el_proceso()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var marca = Path.Combine(dir, "tarde.txt");
            using var p = Process.Start(ConNieto(dir, marca))!;
            await Task.Delay(500);   // que le dé tiempo a lanzar al nieto

            Procesos.Matar(p);

            Assert.True(p.WaitForExit(5000), "el proceso sigue vivo");
            await Task.Delay(3000);
            Assert.False(File.Exists(marca), "lo que lanzó el proceso siguió vivo");
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
