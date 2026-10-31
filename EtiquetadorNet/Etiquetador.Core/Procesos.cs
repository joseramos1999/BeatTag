using System.Diagnostics;

namespace Etiquetador.Core;

/// <summary>
/// Parar los programas que lanza la aplicación (fpcalc, afplay, osascript, security, brew…) con todo
/// lo que hayan lanzado ellos.
///
/// POR QUÉ NO Process.Kill(entireProcessTree: true) FUERA DE WINDOWS. En macOS y Linux, .NET para
/// cada proceso del árbol con SIGSTOP antes de buscarle los hijos y matarlo. Si mientras hay uno
/// parado su grupo de procesos queda huérfano -basta con que termine otro proceso del grupo que ya
/// colgaba de launchd-, el núcleo manda SIGHUP + SIGCONT a TODO el grupo (la regla POSIX de los
/// grupos huérfanos con procesos parados). Y ese grupo es también el de la aplicación: cancelar un
/// fpcalc podía llevarse por delante a la propia aplicación.
///
/// MEDIDO en la CI de macOS, octubre de 2026: uno de cada ocho equipos perdía el runner de GitHub,
/// que comparte grupo con las pruebas, a los pocos segundos de empezar, con una prueba cancelando un
/// programa; GitHub tardaba 46 minutos en darlo por perdido. Reproducido sin nada de la aplicación,
/// con un «sleep» parado y huérfano en el grupo del runner: muere el runner; sin pararlo, no.
///
/// Fuera de Windows se buscan antes los descendientes y se matan todos con SIGKILL, sin parar
/// ninguno. En Windows no existe esa regla y se sigue usando la de .NET.
/// </summary>
public static class Procesos
{
    /// <summary>Mata el proceso y lo que haya lanzado. Si ya había terminado, no hace nada.</summary>
    public static void Matar(Process p)
    {
        try
        {
            if (p.HasExited) return;
            if (OperatingSystem.IsWindows())
            {
                p.Kill(entireProcessTree: true);
                return;
            }
            // Los hijos se buscan ANTES: muerto el padre, pasan a colgar de launchd y ya no hay forma
            // de saber de quién eran.
            var descendientes = Descendientes(p.Id);
            p.Kill();   // solo SIGKILL
            foreach (var pid in descendientes)
                try { using var d = Process.GetProcessById(pid); d.Kill(); } catch { /* ya no está */ }
        }
        catch { /* ya había terminado */ }
    }

    /// <summary>Los procesos que cuelgan de <paramref name="pid"/>, a cualquier profundidad, según ps.</summary>
    private static List<int> Descendientes(int pid)
    {
        var hijos = new Dictionary<int, List<int>>();
        try
        {
            var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-axo", "pid=,ppid=" }) psi.ArgumentList.Add(a);
            using var ps = Process.Start(psi);
            if (ps == null) return new List<int>();
            var salida = ps.StandardOutput.ReadToEndAsync();
            if (!ps.WaitForExit(3000)) { try { ps.Kill(); } catch { } return new List<int>(); }
            foreach (var linea in salida.GetAwaiter().GetResult().Split('\n'))
            {
                var partes = linea.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (partes.Length == 2 && int.TryParse(partes[0], out var hijo) && int.TryParse(partes[1], out var padre))
                {
                    if (!hijos.TryGetValue(padre, out var l)) hijos[padre] = l = new List<int>();
                    l.Add(hijo);
                }
            }
        }
        catch { return new List<int>(); }

        var res = new List<int>();
        var pendientes = new Queue<int>(new[] { pid });
        while (pendientes.Count > 0)
            if (hijos.TryGetValue(pendientes.Dequeue(), out var l))
                foreach (var h in l)
                    if (h != pid && !res.Contains(h)) { res.Add(h); pendientes.Enqueue(h); }
        return res;
    }
}
