using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Etiquetador.App.Services;

/// <summary>
/// Acciones sobre el sistema (abrir una carpeta) usando el Launcher de Avalonia.
/// A propósito NO se usa System.Diagnostics.Process: en el ejecutable único, resolver ese
/// ensamblado podía fallar al compilar el método y tumbaba la app antes de entrar en el try/catch.
/// </summary>
public static class Shell
{
    /// <summary>Abre en el explorador la carpeta que contiene el archivo (o la propia carpeta).</summary>
    public static async Task OpenContainingFolderAsync(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
            await OpenFolderAsync(dir);
        }
        catch { /* abrir una carpeta nunca debe tumbar la app */ }
    }

    /// <summary>Abre una carpeta concreta.</summary>
    public static async Task OpenFolderAsync(string dir)
    {
        try
        {
            var top = MainTopLevel();
            if (top?.Launcher is { } launcher)
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
        }
        catch { }
    }

    /// <summary>Abre una dirección web en el navegador del sistema.</summary>
    public static async Task OpenUrlAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
            var top = MainTopLevel();
            if (top?.Launcher is { } launcher) await launcher.LaunchUriAsync(uri);
        }
        catch { }
    }

    private static TopLevel? MainTopLevel()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d
            ? d.MainWindow
            : null;
}
