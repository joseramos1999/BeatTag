using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Pipeline;

public sealed record ApplyOneResult(
    string FinalPath, bool DidRename, Dictionary<string, FieldChange>? Fields,
    bool TagOk, string TagErr, bool RenOk, string RenErr);

/// <summary>
/// Aplica UN resultado a disco: escribe tags, renombra (validado + dedupe + caso solo-mayús/minús),
/// anota el manifiesto reversible (JSONL con rutas absolutas + cambios por campo) y procesadas.log.
/// </summary>
public sealed class ApplyEngine
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly CoverFetcher? _covers;

    public ApplyEngine(CoverFetcher? covers = null) => _covers = covers;

    public async Task<ApplyOneResult> ApplyOneAsync(ProcessResult info, bool over, FieldFlags fields,
        string coverMode, TagLib.Picture? pngPic, string? undoFile, string doneLogPath, CancellationToken ct = default)
    {
        var origPath = info.FilePath;
        var dir = Path.GetDirectoryName(origPath) ?? "";
        var finalPath = origPath;
        Dictionary<string, FieldChange>? log = null;
        bool didRename = false, tagOk = false, renOk = true;
        string tagErr = "", renErr = "";

        // 1) VALIDAR el destino ANTES de escribir nada: si el nombre no es válido, se aborta
        //    toda la operación (no se tocan los tags) para no dejar el archivo a medias.
        string? target = null;
        if (info.New != info.Old)
        {
            if (!RenameSafety.TryResolveTarget(info.New, info.Old, dir, out var t, out var why))
                return new ApplyOneResult(origPath, false, null, false, "", false, "nombre inválido: " + why);
            target = t;
        }

        // 2) Carátula
        TagLib.Picture? cov = null;
        if (coverMode == "png") cov = pngPic;
        else if (coverMode == "spotify" && info.CoverUrl.Length > 0 && _covers != null)
            cov = await _covers.FetchAsync(info.CoverUrl, ct).ConfigureAwait(false);

        // 3) Escribir tags
        try { log = Tagging.ApplyTags(info, over, fields, cov); tagOk = true; }
        catch (Exception e) { tagErr = e.Message; }

        // 4) Renombrar (el destino ya está validado)
        if (target != null)
        {
            try
            {
                if (target.Equals(info.Old, StringComparison.OrdinalIgnoreCase) &&
                    !target.Equals(info.Old, StringComparison.Ordinal))
                {
                    // Solo cambia mayús/minús: renombrado en dos pasos con nombre temporal
                    var ext = Path.GetExtension(target);
                    var tmp = Path.GetFileNameWithoutExtension(target) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext;
                    File.Move(origPath, Path.Combine(dir, tmp));
                    File.Move(Path.Combine(dir, tmp), Path.Combine(dir, target));
                    finalPath = Path.Combine(dir, target);
                    didRename = true;
                }
                else if (!target.Equals(info.Old, StringComparison.Ordinal))
                {
                    File.Move(origPath, Path.Combine(dir, target));
                    finalPath = Path.Combine(dir, target);
                    didRename = true;
                }
            }
            catch (Exception e) { renOk = false; renErr = e.Message; }
        }

        if (undoFile != null && ((log != null && log.Count > 0) || didRename))
        {
            try
            {
                var rec = new UndoRecord { OrigPath = origPath, FinalPath = finalPath, Renamed = didRename, Fields = log };
                File.AppendAllText(undoFile, JsonSerializer.Serialize(rec, Json) + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* el manifiesto es best-effort, pero el fallo no debe romper el aplicado */ }
        }
        try { File.AppendAllText(doneLogPath, finalPath + Environment.NewLine, Encoding.UTF8); } catch { }

        return new ApplyOneResult(finalPath, didRename, log, tagOk, tagErr, renOk, renErr);
    }
}
