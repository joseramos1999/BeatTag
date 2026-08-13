using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Etiquetador.Core.Providers;

namespace Etiquetador.Core.Pipeline;

public sealed record UndoResult(string File, int Reverted, int Errors, int Missing, int Manual, bool Clean, bool None);

/// <summary>
/// Deshace la ÚLTIMA ejecución (o un manifiesto dado): renombra a la ruta ABSOLUTA original y
/// restaura CADA campo solo si su valor actual sigue siendo el que escribió la app. Lee el formato
/// nuevo (OrigPath/FinalPath/Fields) y también migra el formato antiguo (new/orig/tags/wtitle).
/// </summary>
public sealed class UndoEngine
{
    private enum Outcome { Reverted, Missing, Manual, Error }

    private static readonly JsonSerializerOptions Json = new();
    private readonly AppPaths _paths;
    private readonly Logger? _log;

    public UndoEngine(AppPaths paths, Logger? log = null) { _paths = paths; _log = log; }

    public UndoResult UndoLastRun(string? manifest = null)
    {
        string? mf;
        if (!string.IsNullOrEmpty(manifest)) mf = File.Exists(manifest) ? manifest : null;
        else mf = Directory.Exists(_paths.UndoDir)
            ? new DirectoryInfo(_paths.UndoDir).EnumerateFiles("run_*.jsonl")
                .OrderByDescending(x => x.LastWriteTime).FirstOrDefault()?.FullName
            : null;
        if (mf == null) return new UndoResult("", 0, 0, 0, 0, false, true);

        var nodes = new List<JsonNode>();
        foreach (var ln in File.ReadAllLines(mf, Encoding.UTF8))
        {
            if (ln.Trim().Length == 0) continue;
            try { var n = JsonNode.Parse(ln); if (n != null) nodes.Add(n); } catch { }
        }
        nodes.Reverse();

        int rev = 0, err = 0, missing = 0, manual = 0;
        foreach (var node in nodes)
        {
            Outcome outcome;
            try
            {
                var isNew = node is JsonObject o && o.ContainsKey("OrigPath");
                outcome = isNew ? ProcessNew(node.Deserialize<UndoRecord>(Json)!) : ProcessLegacy(node);
            }
            catch (Exception e)
            {
                outcome = Outcome.Error;
                _log?.Log("  deshacer ERROR: " + e.Message, LogKind.Err);
            }
            switch (outcome)
            {
                case Outcome.Reverted: rev++; break;
                case Outcome.Missing: missing++; break;
                case Outcome.Manual: manual++; break;
                default: err++; break;
            }
        }

        var clean = err == 0 && missing == 0;
        if (clean)
        {
            try
            {
                var dest = mf.EndsWith(".jsonl") ? mf[..^6] + ".deshecho.jsonl" : mf + ".deshecho";
                File.Move(mf, dest);
            }
            catch { }
        }
        return new UndoResult(Path.GetFileName(mf), rev, err, missing, manual, clean, false);
    }

    /// <summary>Nombre del campo de manifiesto que guarda un cambio de volumen (no es un tag).</summary>
    public const string VolumeField = "Volumen";

    // ---------- Formato nuevo ----------
    private Outcome ProcessNew(UndoRecord r)
    {
        var path = File.Exists(r.FinalPath) ? r.FinalPath
            : (File.Exists(r.OrigPath) ? r.OrigPath : null);
        if (path == null) { _log?.Log($"  deshacer: NO encontrado [{Path.GetFileName(r.OrigPath)}]", LogKind.Err); return Outcome.Missing; }

        var renamedBack = RenameBack(r.Renamed, ref path, r.FinalPath, r.OrigPath);

        bool restoredAny = false, skippedAny = false;

        // El volumen no es un tag: se revierte aplicando la ganancia inversa sobre el propio audio.
        // Va antes que los tags porque reescribe el archivo entero.
        if (r.Fields is { Count: > 0 } && r.Fields.TryGetValue(VolumeField, out var vol))
        {
            var pasos = (int)vol.NewNum - (int)vol.OldNum;
            if (pasos != 0)
            {
                var res = Analysis.Mp3Gain.Apply(path, -pasos);
                if (res.Ok) { restoredAny = true; _log?.Log($"  deshacer: volumen devuelto ({-pasos * Analysis.Mp3Gain.DbPerStep:+0.0;-0.0} dB) [{Path.GetFileName(path)}]", LogKind.Ok); }
                else _log?.Log($"  deshacer: NO se pudo devolver el volumen [{Path.GetFileName(path)}]: {res.Error}", LogKind.Err);
            }
        }

        if (r.Fields is { Count: > 0 })
        {
            var tf = TagLib.File.Create(path);
            foreach (var (name, fc) in r.Fields)
            {
                if (name is "Cover" or VolumeField) continue;
                var (restored, skipped) = RestoreField(tf.Tag, name, fc);
                restoredAny |= restored; skippedAny |= skipped;
            }
            if (restoredAny) tf.Save();
            tf.Dispose();
        }

        if (renamedBack || restoredAny) return Outcome.Reverted;
        if (skippedAny) { _log?.Log($"  deshacer: cambios manuales conservados [{Path.GetFileName(r.OrigPath)}]", LogKind.No); return Outcome.Manual; }
        return Outcome.Reverted;
    }

    // ---------- Formato antiguo (new/orig/renamed/tags/wtitle) ----------
    private Outcome ProcessLegacy(JsonNode r)
    {
        var cur = J.S(J.P(r, "new"));
        var dir = Path.GetDirectoryName(cur) ?? "";
        var origName = J.S(J.P(r, "orig"));
        var origPath = Path.Combine(dir, origName);

        var path = File.Exists(cur) ? cur
            : (!string.Equals(cur, origPath, StringComparison.Ordinal) && File.Exists(origPath) ? origPath : null);
        if (path == null) { _log?.Log($"  deshacer(antiguo): NO encontrado [{origName}]", LogKind.Err); return Outcome.Missing; }

        var wt = J.S(J.P(r, "wtitle"));
        if (wt.Length > 0)
        {
            var tc = TagLib.File.Create(path);
            var curT = tc.Tag.Title ?? ""; tc.Dispose();
            if (TextUtils.Nk(curT) != TextUtils.Nk(wt)) { _log?.Log($"  deshacer(antiguo): editado a mano, se conserva [{origName}]", LogKind.No); return Outcome.Manual; }
        }

        var renamed = J.P(r, "renamed") is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
        RenameBack(renamed, ref path, cur, origPath);

        var tags = J.P(r, "tags");
        if (tags is JsonObject to)
        {
            var tf = TagLib.File.Create(path);
            if (to.ContainsKey("Title")) tf.Tag.Title = J.S(J.P(tags, "Title"));
            if (to.ContainsKey("Album")) tf.Tag.Album = J.S(J.P(tags, "Album"));
            if (to.ContainsKey("Artist")) tf.Tag.Performers = StrArray(J.P(tags, "Artist"));
            if (to.ContainsKey("Genre")) tf.Tag.Genres = StrArray(J.P(tags, "Genre"));
            if (to.ContainsKey("Year")) { try { tf.Tag.Year = (uint)J.I(J.P(tags, "Year")); } catch { } }
            if (to.ContainsKey("Bpm")) { try { tf.Tag.BeatsPerMinute = (uint)J.I(J.P(tags, "Bpm")); } catch { } }
            tf.Save(); tf.Dispose();
        }
        return Outcome.Reverted;
    }

    // Renombra de vuelta a la ruta original si procede (incluye caso solo-mayús/minús). Devuelve si renombró.
    private static bool RenameBack(bool renamed, ref string path, string finalPath, string origPath)
    {
        if (!renamed || !string.Equals(path, finalPath, StringComparison.Ordinal) ||
            string.Equals(finalPath, origPath, StringComparison.Ordinal)) return false;

        var dir = Path.GetDirectoryName(finalPath) ?? "";
        if (string.Equals(finalPath, origPath, StringComparison.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(origPath);
            var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(origPath) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
            File.Move(finalPath, tmp);
            File.Move(tmp, origPath);
        }
        else File.Move(finalPath, origPath);
        path = origPath;
        return true;
    }

    // Restaura un campo solo si el valor actual coincide con el que escribió la app. (restaurado, omitido).
    private static (bool restored, bool skipped) RestoreField(TagLib.Tag tag, string name, FieldChange fc)
    {
        switch (name)
        {
            case "Title":
                if ((tag.Title ?? "") == (fc.NewStr ?? "")) { tag.Title = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Album":
                if ((tag.Album ?? "") == (fc.NewStr ?? "")) { tag.Album = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Key":   // clave musical (importación de rekordbox)
                if ((tag.InitialKey ?? "") == (fc.NewStr ?? "")) { tag.InitialKey = Empty(fc.OldStr); return (true, false); }
                return (false, true);
            case "Artist":
                if (SeqEq(tag.Performers, fc.NewArr)) { tag.Performers = (fc.OldArr ?? new()).ToArray(); return (true, false); }
                return (false, true);
            case "Genre":
                if (SeqEq(tag.Genres, fc.NewArr)) { tag.Genres = (fc.OldArr ?? new()).ToArray(); return (true, false); }
                return (false, true);
            case "Year":
                if (tag.Year == fc.NewNum) { tag.Year = fc.OldNum; return (true, false); }
                return (false, true);
            case "Bpm":
                if (tag.BeatsPerMinute == fc.NewNum) { tag.BeatsPerMinute = fc.OldNum; return (true, false); }
                return (false, true);
            default:
                return (false, false);
        }
    }

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static bool SeqEq(string[]? a, List<string>? b)
    {
        a ??= Array.Empty<string>();
        b ??= new List<string>();
        if (a.Length != b.Count) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string[] StrArray(JsonNode? n)
    {
        var list = new List<string>();
        if (n is JsonArray arr) foreach (var e in arr) if (e != null) list.Add(J.S(e));
        return list.ToArray();
    }
}
