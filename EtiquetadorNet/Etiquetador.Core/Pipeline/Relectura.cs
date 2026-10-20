using System;
using System.Collections.Generic;
using System.Linq;

namespace Etiquetador.Core.Pipeline;

/// <summary>
/// Cómo volverá a leerse una lista de géneros que se va a escribir ahora en un archivo.
///
/// POR QUÉ. MEDIDO con TagLib# 2.3: en ID3v2.3 (la versión que TagLib usa por defecto y la de casi
/// todos los MP3 de record pool) el género se guarda en un solo texto, y al leerlo se parte por «/»:
/// «Dance/Electrónica» escrito como un género vuelve como «Dance» y «Electrónica». También convierte
/// las referencias numéricas antiguas («(17)Rock» vuelve como «Rock»). Deshacer compara lo que hay
/// en el archivo con lo anotado, así que si se anotaba lo que se PIDIÓ escribir no coincidía nunca: el
/// cambio se daba por editado a mano y no se devolvía. Y la comprobación «ya lo tiene» de Unificar
/// géneros tampoco coincidía nunca, así que cada pasada volvía a escribir el mismo archivo.
///
/// La predicción se hace con la misma biblioteca: se escribe la lista en una etiqueta ID3v2 en
/// memoria, de la versión que tiene el archivo, y se vuelve a leer. Solo en los formatos que leen el
/// género primero de ID3v2 (MP3, AIFF y WAV); en los demás (FLAC, M4A, OGG) se lee tal cual se escribe.
/// Aun así, quien escribe debe comprobarlo después (<see cref="DebeReleerse"/>): es una predicción.
/// </summary>
public static class Relectura
{
    /// <summary>Los géneros que dará <c>Tag.Genres</c> después de escribir <paramref name="valores"/> en <paramref name="archivo"/>.</summary>
    public static string[] Generos(TagLib.File archivo, IReadOnlyList<string> valores)
    {
        var lista = valores.ToArray();
        if (!LeeElGeneroDeId3v2(archivo)) return lista;
        try
        {
            var version = archivo.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3 ? id3.Version : TagLib.Id3v2.Tag.DefaultVersion;
            if (TagLib.Id3v2.Tag.ForceDefaultVersion) version = TagLib.Id3v2.Tag.DefaultVersion;
            var prueba = new TagLib.Id3v2.Tag { Version = version, Genres = lista };
            return new TagLib.Id3v2.Tag(prueba.Render()).Genres ?? Array.Empty<string>();
        }
        catch
        {
            return lista;   // sin predicción: quien escribe lo comprueba al releer
        }
    }

    /// <summary>
    /// ¿Hay que volver a leer el archivo después de escribir, para anotar lo que de verdad quedó?
    /// Cuando la predicción dice que cambiará, para confirmarlo, y siempre que hay una barra, que es el
    /// caso medido.
    /// </summary>
    public static bool DebeReleerse(IReadOnlyList<string> escritos, IReadOnlyList<string> previstos)
        => !escritos.SequenceEqual(previstos, StringComparer.Ordinal) || escritos.Any(v => v.Contains('/'));

    /// <summary>MP3, AIFF y WAV: su etiqueta combinada lee el género primero de ID3v2.</summary>
    private static bool LeeElGeneroDeId3v2(TagLib.File archivo)
        => archivo is TagLib.Mpeg.AudioFile or TagLib.Aiff.File or TagLib.Riff.File;
}
