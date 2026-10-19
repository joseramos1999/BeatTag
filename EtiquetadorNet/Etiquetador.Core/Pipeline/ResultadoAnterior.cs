using System.Globalization;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Pone al día un resultado que guardó en la caché de análisis una versión anterior de las reglas
/// (<see cref="ProcessResult.Reglas"/> menor que <see cref="ProcessResult.ReglasActuales"/>).
///
/// La firma de la caché solo mide las opciones (fuentes, claves, modo limpieza), no la versión de
/// las reglas, y a propósito: tirar la caché al actualizar obliga a reanalizar la biblioteca entera,
/// horas de consultas en la del autor (12.428 canciones). Pero entonces lo guardado se reutiliza tal
/// cual, sin las comprobaciones nuevas, y se aplica en bloque. Aquí se rehace lo que se puede rehacer
/// SIN red, con lo que el resultado ya trae:
///   · el tag Remixer no se escribe (RemixerTag se queda vacío; ver ProcessResult.RemixerTag);
///   · el BPM sigue las reglas del nombre: manda el que declara, un rango no propone ninguno, y el
///     del catálogo no pisa el que el archivo ya tiene (todo BPM guardado antes salía del catálogo);
///   · lo que solo identificó MusicBrainz o AcoustID, o un nombre con huellas de una regla fallida,
///     sale para revisar, igual que con las reglas de ahora.
/// Lo que necesita la consulta original (qué versión trae el catálogo, quién firma) se queda como
/// estaba hasta que se reanalice el archivo.
/// </summary>
public static class ResultadoAnterior
{
    /// <summary>
    /// Pone al día <paramref name="r"/> si lo guardó una versión anterior. Devuelve true si lo ha
    /// cambiado, para que la caché lo vuelva a guardar y no haya que repetirlo en cada lectura.
    /// </summary>
    public static bool Actualizar(ProcessResult? r)
    {
        if (r == null || r.Reglas >= ProcessResult.ReglasActuales) return false;
        if (r.Reglas < 1 && !r.Skip) DesdeLaPrimera(r);
        r.Reglas = ProcessResult.ReglasActuales;
        return true;
    }

    /// <summary>De lo guardado antes de existir <see cref="ProcessResult.Reglas"/> a la versión 1.</summary>
    private static void DesdeLaPrimera(ProcessResult r)
    {
        // Tag Remixer: el Remixer guardado es el texto en bruto, a veces el propio título. No se
        // escribe nada hasta que un análisis nuevo diga quién firma la versión.
        r.RemixerTag = "";

        // BPM. Antes solo se proponía el del catálogo, así que lo guardado es del catálogo.
        var nombre = r.Old ?? "";
        int.TryParse(r.Bpm, NumberStyles.None, CultureInfo.InvariantCulture, out var delCatalogo);
        var bpm = FileProcessor.ElegirBpm(nombre, null, delCatalogo, out _);
        r.Bpm = bpm;
        r.BpmCatalogo = bpm.Length > 0 && FileProcessor.BpmDelNombre(Path.GetFileNameWithoutExtension(nombre), out _) == 0
            ? bpm : "";

        // Motivos para revisar, como los pone FileProcessor.
        if ((r.Revisar ?? "").Length > 0) return;
        var motivos = new List<string>();
        if (r.Source is "MusicBrainz" or "AcoustID") motivos.Add(FileProcessor.MotivoSoloRescate(r.Source));
        var nuevo = Path.GetFileNameWithoutExtension(r.New ?? "");
        var (artista, titulo) = PartesDelNombre(nuevo);
        motivos.AddRange(NombrePropuesto.Anomalias(nuevo, artista, titulo, nombre, bpm));
        if (motivos.Count == 0) return;

        r.Revisar = string.Join(" · ", motivos);
        r.Why = (r.Why ?? "").Length > 0 ? $"revisar: {r.Revisar} · {r.Why}" : $"revisar: {r.Revisar}";
    }

    /// <summary>
    /// Artista y título tal como quedaron en el nombre propuesto, que se compuso como «Artista -
    /// Título (versiones)». El resultado guardado no los trae por separado: su Artist es el del
    /// catálogo sin normalizar y su Title lleva las versiones.
    /// </summary>
    internal static (string Artista, string Titulo) PartesDelNombre(string? nombre)
    {
        var s = nombre ?? "";
        var i = s.IndexOf(" - ", StringComparison.Ordinal);
        var artista = i > 0 ? s[..i] : "";
        var resto = i > 0 ? s[(i + 3)..] : s;
        // Las versiones van en el último paréntesis; uno al principio del título se queda.
        var titulo = TextUtils.Rx(@"\s*\([^()]*\)\s*$").Replace(resto, "");
        return (artista.Trim(), titulo.Trim());
    }
}
