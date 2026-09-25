using System;
using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;

namespace Etiquetador.App.Services;

/// <summary>
/// Enviar archivos a la papelera del sistema, recuperables. Lo usan Duplicados y el Asistente IA.
/// </summary>
public static class Papelera
{
    /// <summary>Envía el archivo a la papelera. Nunca lo borra del todo: si no puede, lo deja y lo dice.</summary>
    public static (bool Ok, string Error) Enviar(string ruta)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                FileSystem.DeleteFile(ruta, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return (true, "");
            }
            return PapeleraMac(ruta);
        }
        catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>
    /// Papelera en macOS. Microsoft.VisualBasic.FileIO es de Windows: compila fuera pero revienta al
    /// llamarlo.
    ///
    /// Se intenta primero por Finder, que es quien de verdad sabe hacerlo bien: respeta el disco
    /// donde vive el archivo -importante, porque una biblioteca de DJ suele estar en un disco
    /// externo- y deja el "Volver a poner" del menú contextual. La primera vez macOS pedirá permiso
    /// para controlar Finder; si se deniega, se recurre a mover el archivo a ~/.Trash a mano.
    ///
    /// Lo que NO se hace nunca es borrar de verdad. Si los dos caminos fallan, el archivo se queda
    /// donde está y se informa del error: esta función existe para poder deshacer, y un borrado
    /// definitivo disfrazado de papelera sería exactamente lo contrario.
    /// </summary>
    private static (bool Ok, string Error) PapeleraMac(string ruta)
    {
        var porFinder = MoverConFinder(ruta);
        if (porFinder.Ok || !System.IO.File.Exists(ruta)) return (true, "");

        var porCarpeta = MoverATrashDelUsuario(ruta);
        if (porCarpeta.Ok) return (true, "");

        return (false, $"Finder: {porFinder.Error} · ~/.Trash: {porCarpeta.Error}");
    }

    private static (bool Ok, string Error) MoverConFinder(string ruta)
    {
        try
        {
            var script = $"tell application \"Finder\" to delete POSIX file \"{EscaparAppleScript(ruta)}\"";
            var psi = new ProcessStartInfo("osascript")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var p = Process.Start(psi);
            if (p == null) return (false, "no se pudo lanzar osascript");

            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(20_000);
            return p.HasExited && p.ExitCode == 0 ? (true, "") : (false, Corto(err));
        }
        catch (Exception e) { return (false, e.Message); }
    }

    private static (bool Ok, string Error) MoverATrashDelUsuario(string ruta)
    {
        try
        {
            var casa = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (casa.Length == 0) return (false, "no se pudo localizar la carpeta del usuario");

            var papelera = System.IO.Path.Combine(casa, ".Trash");
            System.IO.Directory.CreateDirectory(papelera);
            System.IO.File.Move(ruta, DestinoLibre(papelera, System.IO.Path.GetFileName(ruta)));
            return (true, "");
        }
        catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>
    /// Un nombre que no pise nada en la papelera. Si ya hay un "tema.mp3", el siguiente entra como
    /// "tema 2.mp3": machacar en la papelera el archivo de una limpieza anterior sería perder algo
    /// que el usuario todavía podía recuperar.
    /// </summary>
    internal static string DestinoLibre(string carpeta, string nombre)
    {
        var destino = System.IO.Path.Combine(carpeta, nombre);
        if (!System.IO.File.Exists(destino) && !System.IO.Directory.Exists(destino)) return destino;

        var baseName = System.IO.Path.GetFileNameWithoutExtension(nombre);
        var ext = System.IO.Path.GetExtension(nombre);
        for (var i = 2; i < 10_000; i++)
        {
            destino = System.IO.Path.Combine(carpeta, $"{baseName} {i}{ext}");
            if (!System.IO.File.Exists(destino) && !System.IO.Directory.Exists(destino)) return destino;
        }
        return System.IO.Path.Combine(carpeta, $"{baseName} {Guid.NewGuid():N}{ext}");
    }

    /// <summary>Escapa una ruta para meterla entre comillas en un AppleScript.</summary>
    internal static string EscaparAppleScript(string ruta)
        => ruta.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Corto(string s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 160 ? s : s[..160];
    }
}
