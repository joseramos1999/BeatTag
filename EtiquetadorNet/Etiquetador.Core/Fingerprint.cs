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
    private const string DownloadUrl =
        "https://github.com/acoustid/chromaprint/releases/download/v1.5.1/chromaprint-fpcalc-1.5.1-windows-x86_64.zip";

    // SHA-256 oficial del zip (chromaprint-fpcalc-1.5.1-windows-x86_64.zip). Si no coincide, se rechaza.
    private const string ExpectedSha256 = "36b478e16aa69f757f376645db0d436073a42c0097b6bb2677109e7835b59bbc";

    private readonly AppPaths _paths;
    private readonly Logger? _log;

    public Fingerprint(AppPaths paths, Logger? log = null) { _paths = paths; _log = log; }

    public string FpcalcPath => Path.Combine(_paths.LibDir, "fpcalc.exe");

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
            var zip = Path.Combine(_paths.LibDir, "fpcalc.zip");
            var bytes = await http.GetByteArrayAsync(DownloadUrl, ct).ConfigureAwait(false);
            // Verifica el SHA-256 contra el checksum oficial conocido (no ejecutar una descarga manipulada).
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(sha, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _log?.Log($"fpcalc: SHA-256 NO coincide con el oficial (obtenido {sha}); se descarta la descarga.", LogKind.Err);
                return null;
            }
            await File.WriteAllBytesAsync(zip, bytes, ct).ConfigureAwait(false);
            var ex = Path.Combine(_paths.LibDir, "fpc");
            if (Directory.Exists(ex)) Directory.Delete(ex, true);
            ZipFile.ExtractToDirectory(zip, ex);
            var src = Directory.EnumerateFiles(ex, "fpcalc.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (src != null && IsWindowsExecutable(src)) File.Copy(src, exe, true);
            else _log?.Log("fpcalc: el archivo extraído no parece un ejecutable Windows válido; se descarta.", LogKind.Err);
            try { File.Delete(zip); Directory.Delete(ex, true); } catch { }
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

    // Comprobación de integridad mínima: cabecera PE ('MZ') y tamaño razonable (no ejecutar basura).
    private static bool IsWindowsExecutable(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length < 10_000) return false;
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 0x4D && fs.ReadByte() == 0x5A;   // 'M' 'Z'
        }
        catch { return false; }
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
