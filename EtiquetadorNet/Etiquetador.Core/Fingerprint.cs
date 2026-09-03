using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Etiquetador.Core;

public readonly record struct FingerprintResult(double Duration, string Fingerprint);

/// <summary>
/// Huella acústica con Chromaprint (fpcalc.exe). Reutiliza el fpcalc que ya descargó
/// la app PowerShell en la carpeta lib compartida; si no está, lo descarga una vez.
/// Port de Ensure-Fpcalc / Get-Fingerprint del .ps1.
/// </summary>
public sealed class Fingerprint
{
    // Cada sistema se lleva su propia descarga oficial de Chromaprint 1.5.1, con su SHA-256
    // comprobado a mano contra el archivo que publica el proyecto. En macOS se usa la compilación
    // "universal": vale igual para Apple Silicon y para los Intel antiguos, así que no hay que
    // elegir arquitectura ni mantener dos descargas.
    private const string UrlWindows =
        "https://github.com/acoustid/chromaprint/releases/download/v1.5.1/chromaprint-fpcalc-1.5.1-windows-x86_64.zip";
    private const string ShaWindows = "36b478e16aa69f757f376645db0d436073a42c0097b6bb2677109e7835b59bbc";

    private const string UrlMac =
        "https://github.com/acoustid/chromaprint/releases/download/v1.5.1/chromaprint-fpcalc-1.5.1-macos-universal.tar.gz";
    private const string ShaMac = "d4d8faff4b5f7c558d9be053da47804f9501eaa6c2f87906a9f040f38d61c860";

    private static bool EsWindows => OperatingSystem.IsWindows();
    private static string DownloadUrl => EsWindows ? UrlWindows : UrlMac;
    private static string ExpectedSha256 => EsWindows ? ShaWindows : ShaMac;

    private readonly AppPaths _paths;
    private readonly Logger? _log;

    public Fingerprint(AppPaths paths, Logger? log = null) { _paths = paths; _log = log; }

    /// <summary>Ruta del ejecutable. Fuera de Windows no lleva extensión, como todo en Unix.</summary>
    public string FpcalcPath => Path.Combine(_paths.LibDir, EsWindows ? "fpcalc.exe" : "fpcalc");

    /// <summary>Devuelve la ruta de fpcalc.exe, descargándolo si hace falta. null si no se pudo.</summary>
    public async Task<string?> EnsureFpcalcAsync(HttpClient http, CancellationToken ct = default)
    {
        var exe = FpcalcPath;
        if (File.Exists(exe)) return exe;
        try
        {
            if (!DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            { _log?.Log("fpcalc: la descarga debe ser HTTPS.", LogKind.Err); return null; }

            _log?.Log("Descargando Chromaprint (fpcalc, una sola vez)...");
            Directory.CreateDirectory(_paths.LibDir);
            var comprimido = Path.Combine(_paths.LibDir, EsWindows ? "fpcalc.zip" : "fpcalc.tar.gz");
            var bytes = await http.GetByteArrayAsync(DownloadUrl, ct).ConfigureAwait(false);
            // Verifica el SHA-256 contra el checksum oficial conocido (no ejecutar una descarga manipulada).
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(sha, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Log($"fpcalc: SHA-256 NO coincide con el oficial (obtenido {sha}); se descarta la descarga.", LogKind.Err);
                return null;
            }
            await File.WriteAllBytesAsync(comprimido, bytes, ct).ConfigureAwait(false);
            var ex = Path.Combine(_paths.LibDir, "fpc");
            if (Directory.Exists(ex)) Directory.Delete(ex, true);
            Descomprimir(comprimido, ex);

            var nombre = EsWindows ? "fpcalc.exe" : "fpcalc";
            var src = Directory.EnumerateFiles(ex, nombre, SearchOption.AllDirectories).FirstOrDefault();
            if (src != null && EsEjecutableValido(src))
            {
                File.Copy(src, exe, true);
                // En Unix un archivo copiado no nace ejecutable: sin esto, fpcalc no arranca.
                if (!EsWindows) MarcarEjecutable(exe);
            }
            else _log?.Log("fpcalc: el archivo extraído no parece un ejecutable válido para este sistema; se descarta.", LogKind.Err);
            try { File.Delete(comprimido); Directory.Delete(ex, true); } catch { }
            if (File.Exists(exe)) { _log?.Log("  fpcalc listo."); return exe; }
        }
        catch (Exception e) { _log?.Log("No se pudo obtener fpcalc: " + e.Message, LogKind.No); }
        return null;
    }

    private static string Corto(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 120 ? s : s[..120];
    }

    /// <summary>Windows trae .zip; macOS, .tar.gz. Se descomprime según lo que toque.</summary>
    private static void Descomprimir(string archivo, string destino)
    {
        if (EsWindows) { ZipFile.ExtractToDirectory(archivo, destino); return; }

        Directory.CreateDirectory(destino);
        using var gz = new GZipStream(File.OpenRead(archivo), CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gz, destino, overwriteFiles: true);
    }

    /// <summary>
    /// Comprobación de integridad mínima: la cabecera propia de cada sistema y un tamaño razonable,
    /// para no acabar ejecutando basura aunque el SHA-256 hubiera cuadrado por lo que fuera.
    ///   Windows  'MZ'         → ejecutable PE.
    ///   macOS    CA FE BA BE  → binario universal (varias arquitecturas en el mismo archivo),
    ///            CF FA ED FE  → Mach-O de 64 bits suelto.
    /// </summary>
    private static bool EsEjecutableValido(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length < 10_000) return false;

            using var fs = File.OpenRead(path);
            var b = new byte[4];
            if (fs.Read(b, 0, 4) < 4) return false;

            if (EsWindows) return b[0] == 0x4D && b[1] == 0x5A;   // 'M' 'Z'

            var universal = b[0] == 0xCA && b[1] == 0xFE && b[2] == 0xBA && b[3] == 0xBE;
            var machO64 = b[0] == 0xCF && b[1] == 0xFA && b[2] == 0xED && b[3] == 0xFE;
            return universal || machO64;
        }
        catch { return false; }
    }

    /// <summary>
    /// Da permiso de ejecución al propietario, conservando el resto (Unix).
    ///
    /// El analizador avisa de que estas dos llamadas no existen en Windows, y tiene razón: por eso
    /// solo se llama a este método cuando NO estamos en Windows. Se silencia aquí, en las dos
    /// líneas concretas, en vez de en todo el archivo.
    /// </summary>
    private static void MarcarEjecutable(string path)
    {
#pragma warning disable CA1416   // solo se llega aquí fuera de Windows
        try
        {
            var modo = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, modo | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { /* si no se puede, el fallo saldrá al intentar ejecutarlo y queda en el registro */ }
#pragma warning restore CA1416
    }

    /// <summary>Calcula la huella de un archivo. null si fpcalc no está o falla.</summary>
    public async Task<FingerprintResult?> GetAsync(string path, HttpClient http, CancellationToken ct = default)
    {
        var fpcalc = await EnsureFpcalcAsync(http, ct).ConfigureAwait(false);
        if (fpcalc == null) { _log?.Detail("      huella: fpcalc no disponible"); return null; }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fpcalc,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-json");
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi);
            if (proc == null) { _log?.Detail("      huella: no se pudo arrancar fpcalc"); return null; }
            // Leer stdout Y stderr en paralelo: si no se vacía stderr, fpcalc puede bloquearse al llenar el búfer.
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var outText = await outTask.ConfigureAwait(false);
            var errText = await errTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(outText))
            {
                // Causa habitual: audio ilegible o dañado. fpcalc lo explica en stderr.
                _log?.Detail($"      huella: fpcalc no devolvió nada (código {proc.ExitCode})"
                           + (string.IsNullOrWhiteSpace(errText) ? "" : $" · {Corto(errText)}"));
                return null;
            }
            var j = JsonNode.Parse(outText);
            var fp = j?["fingerprint"]?.GetValue<string>();
            if (string.IsNullOrEmpty(fp)) { _log?.Detail("      huella: fpcalc no incluyó huella en la respuesta"); return null; }
            var dur = j?["duration"]?.GetValue<double>() ?? 0;
            return new FingerprintResult(dur, fp);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { _log?.Detail($"      huella: fallo al calcularla · {e.Message}"); return null; }
    }
}
